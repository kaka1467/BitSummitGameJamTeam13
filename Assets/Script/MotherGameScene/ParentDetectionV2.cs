using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentDetectionV2: Manages mother's approach progression, staged events, and door control.
/// Synchronizes with motherGauge/timer progression and triggers staged effects (lights, sounds, door).
/// Branches into Primary (Real) or Dummy events at final stage based on dummyProbability.
/// </summary>
public class ParentDetectionV2 : MonoBehaviour
{
    /// <summary>
    /// Door open state enumeration
    /// </summary>
    private enum DoorOpenType
    {
        None,   // Door closed
        Peek,   // Half-open (peek) - 15 degrees
        Full    // Full open - 90 degrees
    }

    [Header("System & UI References")]
    public ParentWarningSystem warningSystem;
    public SleepingController sleepingController;
    public MotherGauge motherGauge;

    [Header("Stage Effects Objects")]
    [SerializeField] private GameObject firstFloorLight;
    [SerializeField] private GameObject secondFloorLight1;
    [SerializeField] private GameObject secondFloorLight2;
    [SerializeField] private GameObject secondFloorLight3;
    [SerializeField] private AudioSource stairsAudioSource;
    [SerializeField] private AudioSource dummyDoorAudioSource;
    [SerializeField] private GameObject realMotherObject;      // Primary (Real) mother/door effect
    [SerializeField] private GameObject dummyMotherObject;     // Dummy mother/door effect

    [Header("Audio Sources (SE)")]
    [SerializeField] private AudioSource lightSwitchAudioSource;     // Light switch sound
    [SerializeField] private AudioSource mainDoorOpenAudioSource;    // Door open sound
    [SerializeField] private AudioSource mainDoorCloseAudioSource;   // Door close sound
    [SerializeField] private AudioSource rushInAudioSource;          // Rush-in footstep sound

    [Header("Door Control")]
    [SerializeField] private DoorController targetDoorController;    // Target door controller to command

    [Header("Event Branching")]
    [SerializeField, Range(0f, 1f)] private float dummyProbability = 0.3f;

    [Header("Gauge Speed Settings")]
    [Tooltip("Gauge increase per second when mother is looking")]
    public float riseSpeed = 45f;
    [Tooltip("Gauge decrease per second when safe")]
    public float dropSpeed = 15f;

    [Header("Door Angle Settings")]
    [SerializeField] private float peekOpenAngle = 15f;   // Peek (half-open) door angle
    [SerializeField] private float fullOpenAngle = 90f;   // Full open door angle

    [Header("Loud Item Feature")]
    [SerializeField] private bool enableLoudItemFeature = true;  // Toggle for loud item feature

    [Header("Debug")]
    public bool isCaught = false;
    public bool isMotherLookingNow = false;  // Flag: mother is staring at player

    // Stage progression flags
    private bool stage1Triggered = false;
    private bool stage2Triggered = false;
    private bool stage3Triggered = false;
    private bool stage4Triggered = false;

    // Current door state
    private DoorOpenType currentDoorState = DoorOpenType.None;

    // Coroutine reference
    private Coroutine dummyResetCoroutine = null;

    // Internal decimal gauge for smooth progression
    private float decimalGauge = 0f;

    void Start()
    {
        isCaught = false;
        isMotherLookingNow = false;

        // Auto-find references if not assigned
        if (motherGauge == null)
        {
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        }

        if (motherGauge != null)
        {
            decimalGauge = motherGauge.currentGauge;
            motherGauge.enableAutoDecrease = false;
        }

        if (sleepingController == null)
        {
            sleepingController = Object.FindFirstObjectByType<SleepingController>();
        }

        if (targetDoorController == null)
        {
            targetDoorController = Object.FindFirstObjectByType<DoorController>();
        }
    }

    void Update()
    {
        // Debug key input (New Input System)
        if (Keyboard.current != null)
        {
            if (Keyboard.current.pKey.wasPressedThisFrame)
            {
                TriggerFinalEvent(primary: true);
            }
            if (Keyboard.current.oKey.wasPressedThisFrame)
            {
                TriggerFinalEvent(primary: false);
            }
            if (Keyboard.current.lKey.wasPressedThisFrame)
            {
                OnLoudItemTriggered();
            }
        }

        // If caught, stop progression
        if (isCaught) return;

        // Check required systems
        if (warningSystem == null || sleepingController == null || motherGauge == null) return;

        // Gauge progression logic
        bool parentIsLooking = warningSystem.isWarningActive;
        bool playerIsSleeping = sleepingController.IsSleeping;

        if (parentIsLooking && !playerIsSleeping)
        {
            // Mother is looking and player is not sleeping: gauge rises quickly
            decimalGauge += riseSpeed * Time.deltaTime;
        }
        else
        {
            // Safe or player is sleeping: gauge decreases naturally
            decimalGauge -= dropSpeed * Time.deltaTime;
        }

        // Clamp gauge between 0 and max
        decimalGauge = Mathf.Clamp(decimalGauge, 0f, motherGauge.maxGauge);

        // Update UI
        motherGauge.currentGauge = Mathf.RoundToInt(decimalGauge);
        motherGauge.AddGauge(0);  // Force UI refresh

        // Check for game over
        if (motherGauge.currentGauge >= motherGauge.maxGauge)
        {
            OnPlayerCaught();
        }

        // Update staged progression
        UpdateStages();
    }

    /// <summary>
    /// Updates stage progression based on gauge progress percentage
    /// </summary>
    private void UpdateStages()
    {
        if (motherGauge == null) return;

        float progress = motherGauge.maxGauge <= 0 ? 0f : (decimalGauge / motherGauge.maxGauge);

        // Stage 1: 25% - First floor light
        if (!stage1Triggered && progress >= 0.25f)
        {
            stage1Triggered = true;
            Debug.Log("?? Stage 1: First Floor Light On");
            if (firstFloorLight != null) firstFloorLight.SetActive(true);
            if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        }

        // Stage 2: 50% - Second floor lights
        if (!stage2Triggered && progress >= 0.5f)
        {
            stage2Triggered = true;
            Debug.Log("?? Stage 2: Second Floor Lights On");
            if (secondFloorLight1 != null) secondFloorLight1.SetActive(true);
            if (secondFloorLight2 != null) secondFloorLight2.SetActive(true);
            if (secondFloorLight3 != null) secondFloorLight3.SetActive(true);
            if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        }

        // Stage 3: 75% - Stairs sound
        if (!stage3Triggered && progress >= 0.75f)
        {
            stage3Triggered = true;
            Debug.Log("?? Stage 3: Stairs Audio Playing");
            if (stairsAudioSource != null) stairsAudioSource.Play();
        }

        // Stage 4: ~95% - Final branching event
        if (!stage4Triggered && progress >= 0.95f)
        {
            stage4Triggered = true;
            Debug.Log("?? Stage 4: Final Event Trigger");

            // Branch based on dummy probability
            bool isDummy = Random.value < dummyProbability;
            TriggerFinalEvent(primary: !isDummy);
        }
    }

    /// <summary>
    /// Triggers the final event: Primary (Real) or Dummy
    /// </summary>
    private void TriggerFinalEvent(bool primary)
    {
        if (primary)
        {
            TriggerPrimaryEvent();
        }
        else
        {
            TriggerDummyEvent();
        }
    }

    /// <summary>
    /// Primary (Real) Event: Mother bursts in, full door open
    /// </summary>
    private void TriggerPrimaryEvent()
    {
        Debug.Log("?? Final Event: PRIMARY (Real) - Mother Bursts In!");
        currentDoorState = DoorOpenType.Full;
        isMotherLookingNow = true;

        // Activate real mother object
        if (realMotherObject != null)
        {
            realMotherObject.SetActive(true);
        }

        // Command door controller to open fully
        if (targetDoorController != null)
        {
            targetDoorController.SetDoorOpen(true);
            Debug.Log("?? Door commanded to FULL OPEN");
        }

        // Play door open sound
        if (mainDoorOpenAudioSource != null)
        {
            mainDoorOpenAudioSource.Play();
        }

        // If gauge is full, trigger immediate game over
        if (motherGauge != null && motherGauge.currentGauge >= motherGauge.maxGauge)
        {
            OnPlayerCaught();
        }
    }

    /// <summary>
    /// Dummy Event: Fake mother, peek open, resets after delay
    /// </summary>
    private void TriggerDummyEvent()
    {
        Debug.Log("?? Final Event: DUMMY - Fake Mother");
        currentDoorState = DoorOpenType.Peek;
        isMotherLookingNow = false;

        // Activate dummy mother object
        if (dummyMotherObject != null)
        {
            dummyMotherObject.SetActive(true);
        }

        // Command door controller to peek open
        if (targetDoorController != null)
        {
            targetDoorController.SetDoorOpen(false);  // false = peek/partially open
            Debug.Log("?? Door commanded to PEEK OPEN");
        }

        // Play dummy door sound
        if (dummyDoorAudioSource != null)
        {
            dummyDoorAudioSource.Play();
        }

        // Start reset sequence
        if (dummyResetCoroutine != null)
        {
            StopCoroutine(dummyResetCoroutine);
        }
        dummyResetCoroutine = StartCoroutine(HandleDummySequence());
    }

    /// <summary>
    /// Coroutine: Dummy event cleanup and reset
    /// </summary>
    private IEnumerator HandleDummySequence()
    {
        // Display dummy for 2.5 seconds
        yield return new WaitForSeconds(2.5f);

        Debug.Log("?? Dummy Event: Cleanup and Reset");

        // Deactivate dummy mother
        if (dummyMotherObject != null)
        {
            dummyMotherObject.SetActive(false);
        }

        // Play door close sound
        if (mainDoorCloseAudioSource != null)
        {
            mainDoorCloseAudioSource.Play();
        }

        // Command door to close
        if (targetDoorController != null)
        {
            targetDoorController.SetDoorOpen(false);
            Debug.Log("?? Door commanded to CLOSE");
        }

        // Turn off all lights
        if (firstFloorLight != null) firstFloorLight.SetActive(false);
        if (secondFloorLight1 != null) secondFloorLight1.SetActive(false);
        if (secondFloorLight2 != null) secondFloorLight2.SetActive(false);
        if (secondFloorLight3 != null) secondFloorLight3.SetActive(false);

        // Reset door state
        currentDoorState = DoorOpenType.None;

        // Reset gauge
        decimalGauge = 0f;
        if (motherGauge != null)
        {
            motherGauge.currentGauge = 0;
            motherGauge.AddGauge(0);
        }

        // Reset warning system
        if (warningSystem != null)
        {
            try
            {
                warningSystem.isWarningActive = false;
            }
            catch { }
        }

        // Reset all stage flags for next cycle
        stage1Triggered = false;
        stage2Triggered = false;
        stage3Triggered = false;
        stage4Triggered = false;

        isMotherLookingNow = false;

        dummyResetCoroutine = null;
    }

    /// <summary>
    /// Called when player is caught (gauge full)
    /// </summary>
    void OnPlayerCaught()
    {
        isCaught = true;
        isMotherLookingNow = true;
        Debug.LogError("?? GAME OVER: Caught by Mother!");

        // Game over is now handled by CaughtReactionController which monitors isMotherLookingNow
        // No need to manually trigger scene load here
    }

    /// <summary>
    /// Called when loud item is obtained
    /// Forces immediate primary event if enabled
    /// </summary>
    public void OnLoudItemTriggered()
    {
        if (!enableLoudItemFeature)
        {
            Debug.Log("?? Loud Item Feature is DISABLED");
            return;
        }

        Debug.Log("?? LOUD ITEM TRIGGERED: Forcing Mother Rush-In!");

        // Set stage4 as triggered to prevent normal progression
        stage4Triggered = true;

        // Play rush-in sound
        if (rushInAudioSource != null)
        {
            rushInAudioSource.Play();
        }

        // Force primary event
        TriggerPrimaryEvent();
    }
}
