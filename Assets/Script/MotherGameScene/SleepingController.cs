using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// SleepingController: Monitors player's sleeping state with PC debug override.
/// PC debug input (Space key) has ABSOLUTE HIGHEST PRIORITY and overrides hardware sensor.
/// Supports hardware pillow sensor (PillowSensor via serial/ESP32) as secondary input.
/// Integrates New Input System for seamless PC testing.
/// </summary>
public class SleepingController : MonoBehaviour
{
    [Header("Hardware Sensor")]
    [SerializeField] private PillowSensor pillowSensor;

    [Header("UDP")]
    [Tooltip("Auto-found at Start if not assigned. Sends SLEEP_LOCK / SLEEP_UNLOCK on sleep state change.")]
    [SerializeField] private ParentUdpSender udpSender;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;

    // Player sleeping state
    private bool isSleeping = false;
    private bool _lastSentSleepingState = false;

    /// <summary>
    /// Public read-only property: Player is sleeping (used by ParentDetectionV2 and CaughtReactionController)
    /// </summary>
    public bool IsSleeping => isSleeping;

    void Start()
    {
        if (udpSender == null)
            udpSender = Object.FindFirstObjectByType<ParentUdpSender>();
        isSleeping = false;

        // Initialize pillow sensor if assigned
        if (pillowSensor != null)
        {
            pillowSensor.ResetBaseline();
            Debug.Log("? SleepingController: Pillow sensor baseline calibrated.");
        }
        else
        {
            Debug.LogWarning("?? SleepingController: PillowSensor reference not assigned!");
            Debug.Log("?? Will use PC debug input (Space key or Gamepad Button South) instead.");
        }
    }

    void Update()
    {
        // Determine sleeping state with Space key having ABSOLUTE HIGHEST PRIORITY
        DetermineSleepingState();

        if (isSleeping != _lastSentSleepingState)
        {
            _lastSentSleepingState = isSleeping;
            if (udpSender != null)
            {
                string cmd = isSleeping ? "SLEEP_LOCK" : "SLEEP_UNLOCK";
                Debug.Log($"[SleepingController] Sleeping changed to {isSleeping} - sending {cmd}.");
                udpSender.SendState(cmd);
            }
        }

        if (showDebugLogs)
        {
            Debug.Log($"?? Player Sleeping: {isSleeping}");
        }
    }

    /// <summary>
    /// Determines the current sleeping state with priority hierarchy:
    /// 1. ABSOLUTE HIGHEST: PC debug input (Space key or Gamepad Button South) Å® OVERRIDE everything
    /// 2. SECONDARY: Hardware pillow sensor (if assigned)
    /// 3. DEFAULT: false (not sleeping)
    /// </summary>
    private void DetermineSleepingState()
    {
        // PRIORITY 1: Check PC debug input first (ABSOLUTE OVERRIDE)
        if (CheckPCDebugInput())
        {
            isSleeping = true;
            return;  // Exit immediately - Space key has absolute priority
        }

        // PRIORITY 2: Fall back to hardware sensor if debug input not active
        if (pillowSensor != null)
        {
            isSleeping = pillowSensor.isSleeping;
        }
        else
        {
            // PRIORITY 3: Default to not sleeping if no sensor and no debug input
            isSleeping = false;
        }
    }

    /// <summary>
    /// Checks PC debug input for sleeping state.
    /// Space key or Gamepad Button South (A on Xbox controller) = sleeping.
    /// Returns true if ANY debug input is detected.
    /// </summary>
    private bool CheckPCDebugInput()
    {
        // Check Space key (New Input System) - HIGHEST PRIORITY
        if (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
        {
            if (showDebugLogs)
                Debug.Log("?? Space key pressed: OVERRIDING to sleeping (debug mode)");
            return true;
        }

        // Check Gamepad button (A / Button South) - ALSO HIGH PRIORITY
        if (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed)
        {
            if (showDebugLogs)
                Debug.Log("?? Gamepad South button pressed: OVERRIDING to sleeping (debug mode)");
            return true;
        }

        // No debug input detected
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
            Debug.Log($"?? Force sleep set to: {shouldSleep} (will be overridden by Space key if pressed)");
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
            Debug.Log("?? Pillow sensor baseline reset.");
        }
        else
        {
            Debug.LogWarning("?? No pillow sensor assigned. Cannot reset baseline.");
        }
    }
}
