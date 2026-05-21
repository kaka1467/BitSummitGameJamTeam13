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

        // --- Edge detection: state transitions ---
        if (isSleeping && !wasSleeping)
        {
            // Awake -> Sleeping transition
            Debug.Log("[SleepingController] State changed: AWAKE -> SLEEPING. Sending SLEEP_LOCK.");
            if (udpSender != null)
                udpSender.SendStateSLEEP_LOCK();
            else
                Debug.LogWarning("[SleepingController] ParentUdpSender.instance is missing - cannot send SLEEP_LOCK.");
            _awakeHeartbeatTimer = 0f;
        }
        else if (!isSleeping && wasSleeping)
        {
            // Sleeping -> Awake transition
            Debug.Log("[SleepingController] State changed: SLEEPING -> AWAKE. Sending SLEEP_UNLOCK.");
            if (udpSender != null)
                udpSender.SendStateSLEEP_UNLOCK();
            else
                Debug.LogWarning("[SleepingController] ParentUdpSender.instance is missing - cannot send SLEEP_UNLOCK.");
            _awakeHeartbeatTimer = 0f;
        }

        // --- Safety heartbeat: periodically re-send SLEEP_UNLOCK while awake ---
        if (!isSleeping)
        {
            _awakeHeartbeatTimer += Time.deltaTime;
            if (_awakeHeartbeatTimer >= awakeHeartbeatInterval)
            {
                _awakeHeartbeatTimer = 0f;
                if (udpSender != null)
                {
                    if (showDebugLogs)
                        Debug.Log("[SleepingController] Awake heartbeat: sending SLEEP_UNLOCK.");
                    udpSender.SendStateSLEEP_UNLOCK();
                }
                else
                {
                    Debug.LogWarning("[SleepingController] ParentUdpSender.instance is missing - cannot send awake heartbeat SLEEP_UNLOCK.");
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
