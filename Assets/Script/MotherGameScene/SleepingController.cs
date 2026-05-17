using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class SleepingController : MonoBehaviour
{
    [SerializeField] private PillowSensor pillowSensor;
    [SerializeField] private ParentUdpSender parentUdpSender;
    [SerializeField] private string motherGameOverSceneName = "MotherGameOver";

    private bool isSleeping;
    private bool hasCalledGameOver = false;

    public bool IsSleeping => isSleeping;

    void Start()
    {
        // Calibrate pillow sensor baseline when the mother is ready
        if (pillowSensor != null)
        {
            pillowSensor.ResetBaseline();
            Debug.Log("SleepingController: Pillow sensor baseline calibrated.");
        }
        else
        {
            Debug.LogWarning("SleepingController: PillowSensor reference not assigned!");
        }
    }

    void Update()
    {
        DetectSleepState();
    }

    /// <summary>
    /// Detection logic: checks if the player is pretending to sleep via the pillow sensor.
    /// If isSleeping is TRUE, player is safely sleeping (proceed normally).
    /// If isSleeping is FALSE, player is CAUGHT (trigger game over).
    /// </summary>
    private void DetectSleepState()
    {
        // Fallback: if no pillow sensor, use keyboard/gamepad input
        if (pillowSensor == null)
        {
            isSleeping = CheckInputDevice();
            return;
        }

        // Check pillow sensor state
        if (pillowSensor.isSleeping)
        {
            // Player is properly pretending to sleep
            isSleeping = true;
        }
        else
        {
            // Player is NOT on the pillow or has moved - CAUGHT!
            isSleeping = false;
            
            if (!hasCalledGameOver)
            {
                OnPlayerCaught();
            }
        }
    }

    /// <summary>
    /// Fallback input detection for testing without hardware sensor.
    /// </summary>
    private bool CheckInputDevice()
    {
        bool sleeping = false;

        // Check Keyboard
        if (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
        {
            sleeping = true;
        }

        // Check Gamepad (Android Handheld)
        if (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed)
        {
            sleeping = true;
        }

        return sleeping;
    }

    /// <summary>
    /// Called when the player is caught not pretending to sleep.
    /// Sends "CAUGHT" message to child device and triggers game over scene.
    /// </summary>
    private void OnPlayerCaught()
    {
        hasCalledGameOver = true;
        Debug.Log("SleepingController: Player CAUGHT! Notifying child device...");

        // Send CAUGHT message to child device
        if (parentUdpSender != null)
        {
            parentUdpSender.SendState("CAUGHT");
        }
        else
        {
            Debug.LogWarning("SleepingController: ParentUdpSender reference not assigned!");
        }

        // Load mother game over scene
        SceneManager.LoadScene(motherGameOverSceneName);
    }

    /// <summary>
    /// Public method to reset the caught state (for restarting levels).
    /// </summary>
    public void ResetCaughtState()
    {
        hasCalledGameOver = false;
        isSleeping = false;
        if (pillowSensor != null)
        {
            pillowSensor.ResetBaseline();
        }
    }
}