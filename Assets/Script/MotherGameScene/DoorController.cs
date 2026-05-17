using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// DoorController: Manages door rotation with smooth Lerp animation.
/// Supports both manual toggle (E key) via New Input System and external commands from ParentDetectionV2.
/// Synchronizes with parent model visibility based on door state.
/// </summary>
public class DoorController : MonoBehaviour
{
    /// <summary>
    /// Door state enumeration
    /// </summary>
    public enum DoorState
    {
        Closed,  // Door fully closed (0 degrees)
        Peek,    // Door slightly open for peeking (-15 to -30 degrees)
        Full     // Door fully open (-180 degrees)
    }

    [Header("Door Setup")]
    [SerializeField] private Transform door;           // The door transform that rotates
    [SerializeField] private Transform parentModel;    // The parent model to show/hide

    [Header("Rotation Settings")]
    [SerializeField] private float closedAngle = 0f;   // Closed position (0 degrees)
    [SerializeField] private float peekAngle = -15f;   // Peek position (-15 degrees for testing)
    [SerializeField] private float openAngle = -180f;  // Fully open position (-180 degrees)
    [SerializeField] private float openSpeed = 5f;     // Rotation speed multiplier

    [Header("Debug")]
    public bool showDebugLogs = false;

    // Current door state
    private DoorState currentDoorState = DoorState.Closed;
    private DoorState targetDoorState = DoorState.Closed;

    // Track parent visibility
    private bool isParentHere = false;

    /// <summary>
    /// Read-only property: Check if parent is currently at the door
    /// </summary>
    public bool IsParentHere => isParentHere;

    /// <summary>
    /// Read-only property: Get current door state
    /// </summary>
    public DoorState CurrentDoorState => currentDoorState;

    void Start()
    {
        // Ensure door is at closed angle initially
        if (door != null)
        {
            door.localRotation = Quaternion.Euler(0f, closedAngle, 0f);
        }

        // Ensure parent model is hidden initially
        if (parentModel != null)
        {
            parentModel.gameObject.SetActive(false);
        }

        isParentHere = false;
        currentDoorState = DoorState.Closed;
        targetDoorState = DoorState.Closed;
    }

    void Update()
    {
        // Manual toggle via E key (New Input System)
        if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
        {
            if (showDebugLogs)
                Debug.Log("?? E Key pressed: Toggling door");

            // Toggle between Closed and Full
            if (targetDoorState == DoorState.Closed)
            {
                SetDoorState(DoorState.Full);
            }
            else
            {
                SetDoorState(DoorState.Closed);
            }
        }

        // Smoothly rotate door towards target angle
        UpdateDoorRotation();
    }

    /// <summary>
    /// Updates door rotation towards target angle using Lerp
    /// </summary>
    private void UpdateDoorRotation()
    {
        if (door == null) return;

        float targetAngleY = GetTargetAngle(targetDoorState);
        Quaternion targetRotation = Quaternion.Euler(0f, targetAngleY, 0f);

        // Lerp towards target rotation
        door.localRotation = Quaternion.Lerp(door.localRotation, targetRotation, Time.deltaTime * openSpeed);

        // Update current state if rotation is close enough to target
        if (Quaternion.Angle(door.localRotation, targetRotation) < 1f)
        {
            currentDoorState = targetDoorState;
        }
    }

    /// <summary>
    /// Gets the target Y rotation angle for the given door state
    /// </summary>
    private float GetTargetAngle(DoorState state)
    {
        return state switch
        {
            DoorState.Closed => closedAngle,
            DoorState.Peek => peekAngle,
            DoorState.Full => openAngle,
            _ => closedAngle
        };
    }

    /// <summary>
    /// Sets door to a specific state (called by ParentDetectionV2 and manual input)
    /// </summary>
    public void SetDoorState(DoorState newState)
    {
        if (targetDoorState == newState) return;

        targetDoorState = newState;

        if (showDebugLogs)
            Debug.Log($"?? Door state changed to: {newState}");

        // Update parent model visibility based on door state
        UpdateParentVisibility();
    }

    /// <summary>
    /// Backward compatibility method: Maps boolean to DoorState
    /// true = Full open, false = Closed
    /// </summary>
    public void SetDoorOpen(bool isOpen)
    {
        DoorState newState = isOpen ? DoorState.Full : DoorState.Closed;
        SetDoorState(newState);

        if (showDebugLogs)
            Debug.Log($"?? SetDoorOpen({isOpen}) -> {newState}");
    }

    /// <summary>
    /// Updates parent model visibility based on door state
    /// </summary>
    private void UpdateParentVisibility()
    {
        // Parent is visible when door is Peek or Full
        bool shouldShowParent = (targetDoorState == DoorState.Peek || targetDoorState == DoorState.Full);

        if (shouldShowParent != isParentHere)
        {
            isParentHere = shouldShowParent;

            if (parentModel != null)
            {
                parentModel.gameObject.SetActive(isParentHere);

                if (showDebugLogs)
                    Debug.Log($"?? Parent Model: {(isParentHere ? "VISIBLE" : "HIDDEN")}");
            }
        }
    }

    /// <summary>
    /// Alternative method to set parent visibility directly (if needed)
    /// </summary>
    public void SetParentVisible(bool isVisible)
    {
        if (isParentHere == isVisible) return;

        isParentHere = isVisible;

        if (parentModel != null)
        {
            parentModel.gameObject.SetActive(isVisible);

            if (showDebugLogs)
                Debug.Log($"?? Parent visibility set to: {isVisible}");
        }
    }

    /// <summary>
    /// Get the current rotation angle of the door (in degrees on Y-axis)
    /// </summary>
    public float GetCurrentDoorAngle()
    {
        if (door == null) return 0f;
        return door.localEulerAngles.y;
    }
}
