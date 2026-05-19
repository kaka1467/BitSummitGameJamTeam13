using System.Collections;
using UnityEngine;

/// <summary>
/// ParentWarningSystem:
/// Warning sequence coordinator only.
/// Triggers ParentApproachController to move the mother object through the approach path,
/// then forwards the resulting events to ParentDetectionV2 for gameplay branching.
///
/// This script does NOT directly open the door, show the mother model, or
/// fake the arrival. All of that is handled by ParentDetectionV2 and
/// ParentApproachController based on the events fired here.
///
/// Peek duration scales with current gauge: base + floor(gauge / gaugePerExtraSecond) seconds.
/// </summary>
public class ParentWarningSystem : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ParentApproachController approachController;
    [SerializeField] private ParentDetectionV2 parentDetection;
    [SerializeField] private MotherGauge motherGauge;

    [Header("Peek Duration Scaling")]
    [Tooltip("Base peek duration in seconds (gauge 0-9).")]
    [SerializeField] private float peekBaseDuration = 3f;
    [Tooltip("Every this many gauge points adds +1 second to peek duration.")]
    [SerializeField] private float gaugePerExtraSecond = 10f;

    [Header("Manual Door Sequence Lighting")]
    [Tooltip("First floor light turned on first before the mother starts moving (manual door route).")]
    [SerializeField] private GameObject manualFirstFloorLight;
    [Tooltip("Second floor lights turned on after the first floor light delay.")]
    [SerializeField] private GameObject manualSecondFloorLight1;
    [SerializeField] private GameObject manualSecondFloorLight2;
    [SerializeField] private GameObject manualSecondFloorLight3;
    [Tooltip("Audio source for the light switch sound effect.")]
    [SerializeField] private AudioSource manualLightSwitchAudio;
    [Tooltip("Seconds to wait after first-floor light before turning on second-floor lights.")]
    [SerializeField] private float lightStageDelay1 = 1.5f;
    [Tooltip("Seconds to wait after second-floor lights before starting the approach.")]
    [SerializeField] private float lightStageDelay2 = 1.0f;

    [Header("State")]
    [Tooltip("True while the warning / approach sequence is active.")]
    public bool isWarningActive = false;

    /// <summary>
    /// True only while the mother is actively peeking at the door.
    /// Set true when OnStoppedAtDoor fires, cleared when EndWarningSequence runs.
    /// ParentDetectionV2 uses this (combined with IsInHallwayPhase) to drive
    /// suspicion rise during the peek window.
    /// </summary>
    public bool IsPeekingNow { get; private set; } = false;

    private bool eventsSubscribed = false;
    private Coroutine manualDoorCoroutine = null;

    private void Start()
    {
        if (approachController == null)
        {
            approachController = Object.FindFirstObjectByType<ParentApproachController>();
        }

        if (parentDetection == null)
        {
            parentDetection = Object.FindFirstObjectByType<ParentDetectionV2>();
        }

        if (motherGauge == null)
        {
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        }

        Debug.Log($"[ParentWarningSystem] Start | approachController={(approachController != null ? approachController.name : "NULL")} | parentDetection={(parentDetection != null ? parentDetection.name : "NULL")} | motherGauge={(motherGauge != null ? motherGauge.name : "NULL")}");
        SubscribeApproachEvents();
    }

    private void OnEnable()
    {
        SubscribeApproachEvents();
    }

    private void OnDisable()
    {
        UnsubscribeApproachEvents();
    }

    /// <summary>
    /// Starts the mother approach sequence through ParentApproachController.
    /// Do NOT call this if isWarningActive is already true.
    /// </summary>
    public void StartWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartWarningSequence: BLOCKED - sequence already active");
            return;
        }

        if (approachController == null)
        {
            Debug.LogWarning("[ParentWarningSystem] StartWarningSequence: FAILED - approachController is NULL. Assign it in the Inspector.");
            return;
        }

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] WARNING STARTED - handing off to ParentApproachController");
        Debug.Log($"[ParentWarningSystem] Calling approachController.StartApproach() on object='{approachController.name}'");
        approachController.StartApproach();
    }

    /// <summary>
    /// Starts the warning sequence but forces the pass-by outcome.
    /// The mother walks the full path but never stops at the door.
    /// Use this for the manual N-key debug trigger.
    /// </summary>
    public void StartManualPassByWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualPassByWarningSequence: BLOCKED - sequence already active");
            return;
        }

        if (approachController == null)
        {
            Debug.LogWarning("[ParentWarningSystem] StartManualPassByWarningSequence: FAILED - approachController is NULL.");
            return;
        }

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL PASS-BY WARNING STARTED - mother will pass by without stopping at door");
        approachController.StartApproachPassByOnly();
    }

    /// <summary>
    /// Starts the warning sequence and forces the door-stop/peek outcome.
    /// Runs lights in order (first floor, then second floor) before the mother starts moving.
    /// The mother always arrives at the door regardless of passByProbability.
    /// Use this for the manual M-key debug trigger.
    /// </summary>
    public void StartManualDoorWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualDoorWarningSequence: BLOCKED - sequence already active");
            return;
        }

        if (approachController == null)
        {
            Debug.LogWarning("[ParentWarningSystem] StartManualDoorWarningSequence: FAILED - approachController is NULL.");
            return;
        }

        isWarningActive = true;
        IsPeekingNow = false;
        Debug.Log("[ParentWarningSystem] MANUAL DOOR WARNING STARTED - light sequence then approach");

        if (manualDoorCoroutine != null) StopCoroutine(manualDoorCoroutine);
        manualDoorCoroutine = StartCoroutine(ManualDoorSequenceRoutine());
    }

    private IEnumerator ManualDoorSequenceRoutine()
    {
        // Step 1: First-floor light
        if (manualFirstFloorLight != null) manualFirstFloorLight.SetActive(true);
        if (manualLightSwitchAudio != null) manualLightSwitchAudio.Play();
        Debug.Log("[ParentWarningSystem] ManualDoorSequence: first-floor light ON");

        yield return new WaitForSeconds(lightStageDelay1);

        // Step 2: Second-floor lights
        if (manualSecondFloorLight1 != null) manualSecondFloorLight1.SetActive(true);
        if (manualSecondFloorLight2 != null) manualSecondFloorLight2.SetActive(true);
        if (manualSecondFloorLight3 != null) manualSecondFloorLight3.SetActive(true);
        if (manualLightSwitchAudio != null) manualLightSwitchAudio.Play();
        Debug.Log("[ParentWarningSystem] ManualDoorSequence: second-floor lights ON");

        yield return new WaitForSeconds(lightStageDelay2);

        // Step 3: Start the approach (door-only forced route)
        if (!isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] ManualDoorSequence: aborted - warning no longer active");
            manualDoorCoroutine = null;
            yield break;
        }

        Debug.Log("[ParentWarningSystem] ManualDoorSequence: handing off to approachController.StartApproachDoorOnly()");
        approachController.StartApproachDoorOnly();
        manualDoorCoroutine = null;
    }

    /// <summary>
    /// Returns the current gauge-scaled peek duration.
    /// base + floor(currentGauge / gaugePerExtraSecond) seconds.
    /// </summary>
    public float GetScaledPeekDuration()
    {
        float gauge = (motherGauge != null) ? motherGauge.currentGauge : 0f;
        float extra = (gaugePerExtraSecond > 0f) ? Mathf.Floor(gauge / gaugePerExtraSecond) : 0f;
        float duration = peekBaseDuration + extra;
        Debug.Log($"[ParentWarningSystem] GetScaledPeekDuration: gauge={gauge} | extra={extra} | duration={duration}");
        return duration;
    }

    /// <summary>
    /// Public stop entry point. Ends and resets the sequence.
    /// </summary>
    public void StopWarningSequence()
    {
        EndWarningSequence();
    }

    /// <summary>
    /// Ends the warning sequence and resets the approach controller.
    /// Does NOT touch the door or mother model directly —
    /// ParentDetectionV2 owns that responsibility.
    /// </summary>
    public void EndWarningSequence()
    {
        if (!isWarningActive) return;

        isWarningActive = false;
        IsPeekingNow = false;
        Debug.Log("[ParentWarningSystem] WARNING ENDED - resetting approach controller");

        if (manualDoorCoroutine != null)
        {
            StopCoroutine(manualDoorCoroutine);
            manualDoorCoroutine = null;
        }

        if (approachController != null)
        {
            approachController.ResetApproach();
        }
    }

    private void SubscribeApproachEvents()
    {
        if (eventsSubscribed || approachController == null) return;

        approachController.OnApproachStarted.AddListener(HandleApproachStarted);
        approachController.OnReachedDoor.AddListener(HandleReachedDoor);
        approachController.OnStoppedAtDoor.AddListener(HandleStoppedAtDoor);
        approachController.OnPassedByDoor.AddListener(HandlePassedByDoor);

        eventsSubscribed = true;
        Debug.Log("[ParentWarningSystem] Subscribed to ParentApproachController events");
    }

    private void UnsubscribeApproachEvents()
    {
        if (!eventsSubscribed || approachController == null) return;

        approachController.OnApproachStarted.RemoveListener(HandleApproachStarted);
        approachController.OnReachedDoor.RemoveListener(HandleReachedDoor);
        approachController.OnStoppedAtDoor.RemoveListener(HandleStoppedAtDoor);
        approachController.OnPassedByDoor.RemoveListener(HandlePassedByDoor);

        eventsSubscribed = false;
    }

    private void HandleApproachStarted()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Approach started - mother is moving");
    }

    private void HandleReachedDoor()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Reached door - mother arrived at waypoint");
    }

    private void HandleStoppedAtDoor()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Stopped at door - forwarding to ParentDetectionV2.OnApproachReachedDoor()");

        if (!isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] HandleStoppedAtDoor: ignoring - warning not active");
            return;
        }

        IsPeekingNow = true;
        Debug.Log("[ParentWarningSystem] IsPeekingNow = true");

        if (parentDetection != null)
        {
            parentDetection.OnApproachReachedDoor();
        }
        else
        {
            Debug.LogWarning("[ParentWarningSystem] HandleStoppedAtDoor: parentDetection is NULL - cannot forward event");
        }
    }

    private void HandlePassedByDoor()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Passed by door - forwarding to ParentDetectionV2.OnApproachPassedBy()");

        if (!isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] HandlePassedByDoor: ignoring - warning not active");
            return;
        }

        if (parentDetection != null)
        {
            parentDetection.OnApproachPassedBy();
        }
        else
        {
            Debug.LogWarning("[ParentWarningSystem] HandlePassedByDoor: parentDetection is NULL - cannot forward event");
        }

        EndWarningSequence();
    }
}