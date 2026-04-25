using UnityEngine;
using UnityEngine.InputSystem;

public class DoorController : MonoBehaviour
{
    // Public fields for Inspector assignment
    public Transform door; // The door transform that rotates
    public Transform parentModel; // The parent model transform to show/hide
    public KeyCode doorToggleKey = KeyCode.E; // Key to manually toggle door open/closed
    public KeyCode parentToggleKey = KeyCode.P; // Key to toggle parent visit mode
    public float closedAngle = 0f; // Door angle when closed (degrees)
    public float openAngle = 90f; // Door angle when open (degrees)
    public float openSpeed = 5f; // Speed of door opening/closing

    // Auto parent visit settings
    public bool useAutoParent = true; // When true, parent arrival/departure is driven automatically
    public float parentIntervalMin = 5f; // Minimum seconds between parent visits
    public float parentIntervalMax = 10f; // Maximum seconds between parent visits

    // Internal state variables
    private bool isDoorOpen = false; // Current door state
    private bool isParentHere = false; // Whether parent is at the door
    private float parentTimer = 0f; // Internal timer for auto parent visits
    private float nextParentTime = 0f; // When the next parent visit should happen

    // Public read-only property so other scripts can know if the parent is here
    public bool IsParentHere => isParentHere;

    void Start()
    {
        // Initialize parent model visibility
        if (parentModel != null)
            parentModel.gameObject.SetActive(isParentHere);

        // Schedule the first automatic parent visit
        ScheduleNextParentVisit();
    }

    void ScheduleNextParentVisit()
    {
        nextParentTime = Random.Range(parentIntervalMin, parentIntervalMax);
        parentTimer = 0f;
    }

    void ToggleParentPresence()
    {
        isParentHere = !isParentHere;
        isDoorOpen = isParentHere;
        if (parentModel != null)
            parentModel.gameObject.SetActive(isParentHere);
    }

    void Update()
    {
        // Check for manual door toggle (E key)
        if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
        {
            isDoorOpen = !isDoorOpen; // Toggle door state
        }

        // Parent visit handling
        if (useAutoParent)
        {
            parentTimer += Time.deltaTime;
            if (parentTimer >= nextParentTime)
            {
                ToggleParentPresence();
                ScheduleNextParentVisit();
            }
        }
        else if (Keyboard.current != null && Keyboard.current.pKey.wasPressedThisFrame)
        {
            ToggleParentPresence();
        }

        // Smoothly rotate the door towards the target angle
        if (door != null)
        {
            Quaternion targetRotation = Quaternion.Euler(0f, isDoorOpen ? openAngle : closedAngle, 0f);
            door.localRotation = Quaternion.Lerp(door.localRotation, targetRotation, Time.deltaTime * openSpeed);
        }
    }
}

/*
Setup Instructions:
1. Attach this script to an empty GameObject in your scene (e.g., name it "DoorManager").
2. In the Inspector, drag the door GameObject (the one that rotates) into the "Door" field.
3. Drag the parent model GameObject into the "Parent Model" field.
4. Adjust the angles and speed as needed (closedAngle=0, openAngle=90 for a 90-degree swing).
5. Test: Press E to manually open/close the door. When useAutoParent is enabled, the parent will automatically appear/disappear over time; otherwise, press P to toggle parent presence.
*/
