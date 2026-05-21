using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// SleepingController: Monitors player's sleeping state with PC debug override.
/// PC debug input (Space key) has ABSOLUTE HIGHEST PRIORITY and overrides hardware sensor.
/// Supports hardware pillow sensor (PillowSensor via serial/ESP32) as secondary input.
/// Integrates New Input System for seamless PC testing.
/// Automatically sends SLEEP_LOCK / SLEEP_UNLOCK via ParentUdpSender on state transitions,
/// and sends a periodic SLEEP_UNLOCK heartbeat while awake to recover from false lock states.
/// </summary>
public class SleepingController : MonoBehaviour
{
    [Header("Hardware Sensor")]
    [SerializeField] private PillowSensor pillowSensor;

    [Header("UDP")]
    [Tooltip("Auto-found at Start if not assigned. Sends SLEEP_LOCK / SLEEP_UNLOCK on sleep state change.")]
    [SerializeField] private ParentUdpSender udpSender;

    [Header("Safety Heartbeat")]
    [Tooltip("Interval (seconds) at which SLEEP_UNLOCK is re-sent while the parent is awake.")]
    [SerializeField] private float awakeHeartbeatInterval = 1.0f;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;
    [Tooltip("When true, the pillow sensor is ignored and only Space/Gamepad controls isSleeping. Useful when the sensor is noisy during debug tests.")]
    [SerializeField] private bool ignoreSensorForDebug = false;

    // Player sleeping state
    private bool isSleeping = false;
    private bool wasSleeping = false;

    // Safety heartbeat timer
    private float _awakeHeartbeatTimer = 0f;

    // Tracks previous debug-input state to log Space activation without spamming every frame
    private bool _wasDebugInputActive = false;

    // Diagnostic trackers: store last-logged values so we only print on change
    private bool _diagLastDebugInput = false;
    private bool _diagLastSensorSleeping = false;
    private bool _diagLastIsSleeping = false;
    private bool _diagLastWasSleeping = false;

    /// <summary>
    /// Public read-only property: Player is sleeping (used by ParentDetectionV2 and CaughtReactionController)
    /// </summary>
    public bool IsSleeping => isSleeping;

    void Start()
    {
        if (udpSender == null)
            udpSender = Object.FindFirstObjectByType<ParentUdpSender>();
        isSleeping = false;
        wasSleeping = false;
        _awakeHeartbeatTimer = 0f;
        _wasDebugInputActive = false;
        _diagLastDebugInput = false;
        _diagLastSensorSleeping = false;
        _diagLastIsSleeping = false;
        _diagLastWasSleeping = false;

        // Log udpSender status at startup. GetUdpSender() will retry at runtime if still null.
        if (udpSender != null)
            Debug.Log($"[SC-DIAG] udpSender pre-assigned in Inspector: '{udpSender.gameObject.name}'");
        else
            Debug.LogWarning("[SC-DIAG] udpSender not set in Inspector - will auto-find at runtime via GetUdpSender().");

        // Initialize pillow sensor if assigned
        if (pillowSensor != null)
        {
            pillowSensor.ResetBaseline();
            Debug.Log("SleepingController: Pillow sensor baseline calibrated.");
        }
        else
        {
            Debug.LogWarning("SleepingController: PillowSensor reference not assigned!");
            Debug.Log("SleepingController: Will use PC debug input (Space key or Gamepad Button South) instead.");
        }
    }

    void Update()
    {
        // Determine sleeping state with Space key having ABSOLUTE HIGHEST PRIORITY
        DetermineSleepingState();

        // --- Diagnostic: log isSleeping and wasSleeping only when they change ---
        if (isSleeping != _diagLastIsSleeping)
        {
            Debug.Log($"[SC-DIAG] isSleeping changed: {_diagLastIsSleeping} -> {isSleeping}  |  wasSleeping={wasSleeping}  |  udpSender={(udpSender != null ? udpSender.gameObject.name : "NULL")}");
            _diagLastIsSleeping = isSleeping;
        }
        if (wasSleeping != _diagLastWasSleeping)
        {
            Debug.Log($"[SC-DIAG] wasSleeping changed: {_diagLastWasSleeping} -> {wasSleeping}");
            _diagLastWasSleeping = wasSleeping;
        }

        // --- Edge detection: state transitions ---
        if (isSleeping && !wasSleeping)
        {
            // Awake -> Sleeping transition
            Debug.Log("[SleepingController] State changed: AWAKE -> SLEEPING. Sending SLEEP_LOCK.");
            ParentUdpSender sender = GetUdpSender();
            if (sender != null)
            {
                Debug.Log($"[SC-DIAG] >>> Calling SendStateSLEEP_LOCK() on '{sender.gameObject.name}'");
                sender.SendStateSLEEP_LOCK();
            }
            else
            {
                Debug.LogWarning("[SC-DIAG] *** ParentUdpSender not found - SLEEP_LOCK NOT sent! Is ParentUdpSender in the scene and enabled? ***");
            }
            _awakeHeartbeatTimer = 0f;
        }
        else if (!isSleeping && wasSleeping)
        {
            // Sleeping -> Awake transition
            Debug.Log("[SleepingController] State changed: SLEEPING -> AWAKE. Sending SLEEP_UNLOCK.");
            ParentUdpSender sender = GetUdpSender();
            if (sender != null)
            {
                Debug.Log($"[SC-DIAG] >>> Calling SendStateSLEEP_UNLOCK() on '{sender.gameObject.name}'");
                sender.SendStateSLEEP_UNLOCK();
            }
            else
            {
                Debug.LogWarning("[SC-DIAG] *** ParentUdpSender not found - SLEEP_UNLOCK NOT sent! Is ParentUdpSender in the scene and enabled? ***");
            }
            _awakeHeartbeatTimer = 0f;
        }

        // --- Safety heartbeat: periodically re-send SLEEP_UNLOCK while awake ---
        if (!isSleeping)
        {
            _awakeHeartbeatTimer += Time.deltaTime;
            if (_awakeHeartbeatTimer >= awakeHeartbeatInterval)
            {
                _awakeHeartbeatTimer = 0f;
                ParentUdpSender sender = GetUdpSender();
                if (sender != null)
                {
                    if (showDebugLogs)
                        Debug.Log("[SleepingController] Awake heartbeat: sending SLEEP_UNLOCK.");
                    sender.SendStateSLEEP_UNLOCK();
                }
                else
                {
                    Debug.LogWarning("[SleepingController] Awake heartbeat: ParentUdpSender not found - SLEEP_UNLOCK skipped.");
                }
            }
        }

        wasSleeping = isSleeping;

        if (showDebugLogs)
        {
            Debug.Log($"SleepingController: isSleeping={isSleeping}");
        }
    }

    /// <summary>
    /// Determines the current sleeping state each frame.
    ///
    /// Normal mode  (ignoreSensorForDebug = false):
    ///   isSleeping = debugInput || sensorSleeping
    ///
    /// Debug-only mode (ignoreSensorForDebug = true):
    ///   isSleeping = debugInput
    ///   The pillow sensor is completely ignored, so noisy hardware cannot
    ///   prevent Space-release from clearing the sleeping state.
    /// </summary>
    private void DetermineSleepingState()
    {
        bool debugInput = CheckPCDebugInput();
        bool sensorSleeping = (pillowSensor != null) && pillowSensor.isSleeping;

        // --- Diagnostic: log debugInput and sensorSleeping only when they change ---
        if (debugInput != _diagLastDebugInput)
        {
            Debug.Log($"[SC-DIAG] debugInput changed: {_diagLastDebugInput} -> {debugInput}  (Keyboard.current={(Keyboard.current != null ? "OK" : "NULL")}  spaceKey.isPressed={(Keyboard.current != null ? Keyboard.current.spaceKey.isPressed.ToString() : "N/A")})");
            _diagLastDebugInput = debugInput;
        }
        if (sensorSleeping != _diagLastSensorSleeping)
        {
            Debug.Log($"[SC-DIAG] sensorSleeping changed: {_diagLastSensorSleeping} -> {sensorSleeping}  (ignoreSensorForDebug={ignoreSensorForDebug})");
            _diagLastSensorSleeping = sensorSleeping;
        }

        // Log Space/Gamepad activation edge (once per press, not every frame)
        if (debugInput && !_wasDebugInputActive)
            Debug.Log("[SleepingController] Space/Gamepad debug override ACTIVE - forcing sleeping.");
        else if (!debugInput && _wasDebugInputActive)
            Debug.Log("[SleepingController] Space/Gamepad debug override RELEASED.");
        _wasDebugInputActive = debugInput;

        // Warn once per frame when sensor is being ignored (only if showDebugLogs is on)
        if (ignoreSensorForDebug && sensorSleeping && showDebugLogs)
            Debug.Log("[SleepingController] ignoreSensorForDebug=true: sensor reports sleeping but is being ignored.");

        // Final sleeping state
        if (ignoreSensorForDebug)
            isSleeping = debugInput;              // Space/Gamepad only - sensor has no effect
        else
            isSleeping = debugInput || sensorSleeping; // Normal: either source can trigger sleeping
    }

    /// <summary>
    /// Returns the ParentUdpSender to use for sending, with a 3-level fallback:
    ///   1. Inspector-assigned udpSender field (fastest, preferred)
    ///   2. ParentUdpSender.instance (static singleton set by ParentUdpSender itself)
    ///   3. FindFirstObjectByType (scene search, slowest - used only as last resort)
    /// If found via fallback, the result is cached back into udpSender for next time.
    /// </summary>
    private ParentUdpSender GetUdpSender()
    {
        // Level 1: already cached
        if (udpSender != null)
            return udpSender;

        // Level 2: static singleton
        if (ParentUdpSender.instance != null)
        {
            udpSender = ParentUdpSender.instance;
            Debug.Log($"[SC-DIAG] GetUdpSender: found via ParentUdpSender.instance ('{udpSender.gameObject.name}') - caching.");
            return udpSender;
        }

        // Level 3: scene search
        ParentUdpSender found = Object.FindFirstObjectByType<ParentUdpSender>();
        if (found != null)
        {
            udpSender = found;
            Debug.Log($"[SC-DIAG] GetUdpSender: found via FindFirstObjectByType ('{udpSender.gameObject.name}') - caching.");
            return udpSender;
        }

        // Not found by any method
        Debug.LogWarning($"[SC-DIAG] GetUdpSender: ParentUdpSender NOT found in scene! (called from '{gameObject.name}')");
        return null;
    }

    /// <summary>
    /// Checks PC debug input for sleeping state.
    /// Uses isPressed (held state), NOT wasPressedThisFrame, so it stays true
    /// for the entire duration Space / Button South is held down.
    /// </summary>
    private bool CheckPCDebugInput()
    {
        // Space key held (New Input System)
        if (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
            return true;

        // Gamepad Button South held (A on Xbox / Cross on PlayStation)
        if (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed)
            return true;

        return false;
    }

    /// <summary>
    /// Force sleeping state for testing purposes.
    /// Note: This sets the internal state, but will be overridden by Space key in the next frame.
    /// </summary>
    public void ForceSleep(bool shouldSleep)
    {
        isSleeping = shouldSleep;

        if (showDebugLogs)
            Debug.Log($"SleepingController: Force sleep set to: {shouldSleep} (will be overridden by Space key if pressed)");
    }

    /// <summary>
    /// Get current pillow sensor instance (if available).
    /// Returns null if no sensor is assigned.
    /// </summary>
    public PillowSensor GetPillowSensor()
    {
        return pillowSensor;
    }

    /// <summary>
    /// Reset pillow sensor baseline calibration if available.
    /// Used for re-calibrating the sensor in-game if needed.
    /// </summary>
    public void ResetPillowSensorBaseline()
    {
        if (pillowSensor != null)
        {
            pillowSensor.ResetBaseline();
            Debug.Log("SleepingController: Pillow sensor baseline reset.");
        }
        else
        {
            Debug.LogWarning("SleepingController: No pillow sensor assigned. Cannot reset baseline.");
        }
    }
}
