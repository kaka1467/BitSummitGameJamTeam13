using UnityEngine;

/// <summary>
/// Detects when the player is caught by the parent.
/// The player is caught if the parent is at the door AND the player is not sleeping.
/// Once caught, the state persists until manually reset or the scene is reloaded.
/// </summary>
public class ParentDetection : MonoBehaviour
{
    // References to the other controllers
    public DoorController doorController;
    public SleepingController sleepingController;

    // Detection settings
    public float checkInterval = 0.1f; // How often to check for capture (in seconds)

    // Detection state
    public bool isCaught = false; // Whether the player has been caught

    // Internal timer
    private float detectionTimer = 0f;

    void Update()
    {
        // If already caught, do nothing
        if (isCaught)
            return;

        // Accumulate time
        detectionTimer += Time.deltaTime;

        // Perform detection check at interval
        if (detectionTimer >= checkInterval)
        {
            detectionTimer = 0f;
            CheckDetection();
        }
    }

    void CheckDetection()
    {
        // Make sure both controllers are assigned
        if (doorController == null || sleepingController == null)
            return;

        // Player is caught if parent is at the door AND player is not sleeping
        if (doorController.IsParentHere && !sleepingController.IsSleeping)
        {
            isCaught = true;
            Debug.Log("Caught by parent!");
            // TODO: Replace with game over UI later
        }
    }
}

/*
Setup:
1) Attach this script to any GameObject in your scene (e.g., an empty "GameManager" object).
2) In the Inspector, drag the DoorController GameObject into the "Door Controller" field.
3) Drag the SleepingController GameObject into the "Sleeping Controller" field.
4) Optionally adjust the "Check Interval" (default 0.1 seconds is reasonable).
5) Monitor the "Is Caught" field in the Inspector to see when the player is caught.
*/