using System.Collections;
using UnityEngine;

/// <summary>
/// ParentWarningSystem:
/// Owns all pre-approach presentation: foreshadowing lights, delays, speed scaling, and route selection.
/// Also owns the loud-item rush-in entry point (bypasses lights/delays, forces DoorPeek at high speed).
///
/// Public entry points:
///   StartWarningSequence()             — normal automatic flow (called by scheduler)
///   StartManualPassByWarningSequence() — N-key debug: full foreshadow, forced PassBy
///   StartManualDoorWarningSequence()   — M-key debug: full foreshadow, forced DoorPeek
///   TriggerInstantPassBy()             — instant debug PassBy, no lights or delays
///   TriggerInstantDoor()               — instant debug DoorPeek, no lights or delays
///   StartLoudItemRushInSequence()      — loud-item rush-in: second-floor lights only, speed=loudItemRushInMoveSpeed
///   StopWarningSequence()              — force-stop and reset (e.g. game over, scene unload)
///   EndWarningSequence()               — clean end called by ParentDetectionV2 after a cycle completes
///
/// Responsibility boundaries:
///   ParentWarningSystem     — lights, delays, speed, route probability, rush-in setup
///   ParentApproachController— path movement and orientation
///   ParentDetectionV2       — door branching, suspicion, room-check consequences, caught
/// </summary>
public class ParentWarningSystem : MonoBehaviour
{
    // ── Core references ───────────────────────────────────────────────────────
    [Header("References")]
    [SerializeField] public ParentApproachController approachController;
    [SerializeField] public ParentDetectionV2        parentDetection;
    [SerializeField] public MotherGauge              motherGauge;

    // ── Foreshadowing lights ──────────────────────────────────────────────────
    [Header("Foreshadowing Lights")]
    [SerializeField] private GameObject firstFloorLight;
    [SerializeField] private GameObject secondFloorLight1;
    [SerializeField] private GameObject secondFloorLight2;
    [SerializeField] private GameObject secondFloorLight3;
    [SerializeField] private AudioSource lightSwitchAudioSource;

    // ── Foreshadowing delay scaling ───────────────────────────────────────────
    [Header("Foreshadowing Delays (Low Suspicion)")]
    [Tooltip("Min seconds between first-floor light and second-floor lights at low suspicion.")]
    public float secondFloorDelayMin = 1f;
    [Tooltip("Max seconds between first-floor light and second-floor lights at low suspicion.")]
    public float secondFloorDelayMax = 10f;
    [Tooltip("Min seconds between second-floor lights and approach start at low suspicion.")]
    public float approachDelayMin = 1f;
    [Tooltip("Max seconds between second-floor lights and approach start at low suspicion.")]
    public float approachDelayMax = 3f;

    [Header("Foreshadowing Delays (High Suspicion)")]
    [Tooltip("If current gauge is above this threshold, use the high-suspicion delay ranges below.")]
    public int highSuspicionDelayGaugeThreshold = 5;
    [Tooltip("Min seconds between first-floor light and second-floor lights when gauge is above threshold.")]
    public float highSuspicionSecondFloorDelayMin = 1f;
    [Tooltip("Max seconds between first-floor light and second-floor lights when gauge is above threshold.")]
    public float highSuspicionSecondFloorDelayMax = 3f;
    [Tooltip("Min seconds between second-floor lights and approach start when gauge is above threshold.")]
    public float highSuspicionApproachDelayMin = 0f;
    [Tooltip("Max seconds between second-floor lights and approach start when gauge is above threshold.")]
    public float highSuspicionApproachDelayMax = 1f;

    // ── Movement speed ────────────────────────────────────────────────────────
    [Header("Approach Speed")]
    [Tooltip("Minimum moveSpeed assigned for automatic runs.")]
    public float approachMoveSpeedMin = 5f;
    [Tooltip("Maximum moveSpeed assigned for automatic runs.")]
    public float approachMoveSpeedMax = 15f;
    [Tooltip("Extra moveSpeed added linearly at max suspicion on automatic runs.")]
    public float approachSpeedSuspicionBonus = 0f;
    [Tooltip("If current gauge is above this threshold, use the high suspicion speed range below.")]
    public int highSuspicionSpeedGaugeThreshold = 5;
    [Tooltip("Minimum moveSpeed assigned when gauge is above the threshold.")]
    public float highSuspicionApproachMoveSpeedMin = 25f;
    [Tooltip("Maximum moveSpeed assigned when gauge is above the threshold.")]
    public float highSuspicionApproachMoveSpeedMax = 30f;

    // ── Rush-in speed ─────────────────────────────────────────────────────────
    [Header("Loud-Item Rush-In")]
    [Tooltip("moveSpeed assigned to the approach controller for a loud-item rush-in. Should be noticeably faster than normal high-suspicion speeds.")]
    public float loudItemRushInMoveSpeed = 40f;

    // ── Debug speed override ──────────────────────────────────────────────────
    [Header("Debug Speed Override (N / M manual routes)")]
    [Tooltip("When true, manual N/M routes use fixedDebugApproachSpeed instead of the random range.")]
    public bool useFixedDebugApproachSpeed = false;
    [Tooltip("Fixed moveSpeed used for manual N/M routes when useFixedDebugApproachSpeed is true.")]
    public float fixedDebugApproachSpeed = 4f;

    // ── Route probability ─────────────────────────────────────────────────────
    [Header("Route Probability")]
    [Tooltip("DoorPeek probability at zero suspicion (gauge=0).")]
    [Range(0f, 1f)]
    public float doorChanceAtMinSuspicion = 0.2f;
    [Tooltip("DoorPeek probability at maximum suspicion (gauge=maxGauge). Should be close to 1 for high danger feel.")]
    [Range(0f, 1f)]
    public float doorChanceAtMaxSuspicion = 0.95f;
    [Tooltip("Among non-door outcomes, this fraction selects PassByThenDoorSound instead of PassBy.")]
    [Range(0f, 1f)]
    public float basePassByThenDoorSoundChance = 0.33f;

    // ── Third route audio ─────────────────────────────────────────────────────
    [Header("Pass-By-Then-Door-Sound Route")]
    [Tooltip("AudioSource played after the pass-by completes on the PassByThenDoorSound route.")]
    [SerializeField] private AudioSource passByThenDoorSoundAudioSource;
    [Tooltip("Seconds after pass-by finishes before the remote door sound plays.")]
    [SerializeField] private float passByThenDoorSoundDelay = 1f;

    // ── State ─────────────────────────────────────────────────────────────────
    [Header("State")]
    [Tooltip("True while the warning / approach sequence is active.")]
    public bool isWarningActive = false;

    // ── Active route state ────────────────────────────────────────────────────
    /// <summary>Route chosen for the current run. Set before movement starts; cleared when the sequence ends.</summary>
    public enum RouteState { None, PassBy, DoorPeek, PassByThenDoorSound }
    public RouteState ActiveRoute { get; private set; } = RouteState.None;

    // ── Private ───────────────────────────────────────────────────────────────
    private bool      _eventsSubscribed    = false;
    private Coroutine _foreshadowCoroutine = null;
    private Coroutine _passByThenDoorSoundCoroutine = null;

    private void Start()
    {
        if (approachController == null)
            approachController = Object.FindFirstObjectByType<ParentApproachController>();

        if (parentDetection == null)
            parentDetection = Object.FindFirstObjectByType<ParentDetectionV2>();

        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        Debug.Log(
            $"[ParentWarningSystem] Start | approachController={(approachController != null ? approachController.name : "NULL")} | parentDetection={(parentDetection != null ? parentDetection.name : "NULL")} | motherGauge={(motherGauge != null ? motherGauge.name : "NULL")}"
        );

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

    public void StartWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] WARNING START — beginning foreshadowing sequence");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.None));
    }

    public void StartManualPassByWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualPassByWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL ROUTE: N — PASS-BY");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.PassBy, true));
    }

    public void StartManualDoorWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualDoorWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL ROUTE: M — DOOR/PEEK");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.Door, true));
    }

    public void TriggerInstantPassBy()
    {
        if (isWarningActive) return;
        if (!ValidateController()) return;

        isWarningActive = true;
        ActiveRoute = RouteState.PassBy;
        Debug.Log("[ParentWarningSystem] INSTANT DEBUG: PASS-BY");

        ApplyApproachSpeed(true, 0f);
        approachController.StartApproachPassByOnly();
    }

    public void TriggerInstantDoor()
    {
        if (isWarningActive) return;
        if (!ValidateController()) return;

        isWarningActive = true;
        ActiveRoute = RouteState.DoorPeek;
        Debug.Log("[ParentWarningSystem] INSTANT DEBUG: DOOR/PEEK");

        ApplyApproachSpeed(true, 0f);
        approachController.StartApproachDoorOnly();
    }

    /// <summary>
    /// Loud-item rush-in: skips first-floor light and foreshadow delays.
    /// Turns on second-floor lights only, sets speed to loudItemRushInMoveSpeed, forces DoorPeek route.
    /// Called by ParentDetectionV2.OnLoudItemTriggered() after audio and gauge are already applied.
    /// No-op if a warning sequence is already active.
    /// </summary>
    public void StartLoudItemRushInSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartLoudItemRushInSequence: BLOCKED — sequence already active");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        ActiveRoute     = RouteState.DoorPeek;

        if (approachController != null)
            approachController.IsRushIn = true;

        // Second-floor lights only — first-floor light is skipped for rush-in.
        if (secondFloorLight1 != null) secondFloorLight1.SetActive(true);
        if (secondFloorLight2 != null) secondFloorLight2.SetActive(true);
        if (secondFloorLight3 != null) secondFloorLight3.SetActive(true);
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();

        if (approachController != null)
            approachController.moveSpeed = loudItemRushInMoveSpeed;

        Debug.Log($"[ParentWarningSystem] LOUD-ITEM RUSH-IN | speed={loudItemRushInMoveSpeed} | route=DoorPeek");
        approachController.StartApproachDoorOnly();
    }

    /// <summary>Force-stops any active foreshadow or pass-by-sound coroutines, then calls EndWarningSequence().</summary>
    public void StopWarningSequence()
    {
        if (_foreshadowCoroutine != null)
        {
            StopCoroutine(_foreshadowCoroutine);
            _foreshadowCoroutine = null;
        }

        if (_passByThenDoorSoundCoroutine != null)
        {
            StopCoroutine(_passByThenDoorSoundCoroutine);
            _passByThenDoorSoundCoroutine = null;
        }

        Debug.Log("[ParentWarningSystem] WARNING STOPPED");
        TurnOffAllLights();
        EndWarningSequence();
    }

    public void EndWarningSequence()
    {
        if (!isWarningActive) return;

        isWarningActive = false;
        ActiveRoute = RouteState.None;
        Debug.Log("[ParentWarningSystem] WARNING ENDED — resetting approach controller");

        TurnOffAllLights();

        if (approachController != null)
            approachController.ResetApproach();
    }

    private enum RouteOverride { None, PassBy, Door }

    private IEnumerator ForeshadowAndApproachCoroutine(RouteOverride routeOverride, bool isManual = false)
    {
        float suspicionFraction = GetSuspicionFraction();
        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
        bool highSuspicionDelays = !isManual && gauge > highSuspicionDelayGaugeThreshold;

        if (firstFloorLight != null) firstFloorLight.SetActive(true);
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] FIRST FLOOR LIGHT ON");

        float secondFloorDelay;
        if (highSuspicionDelays)
        {
            secondFloorDelay = Random.Range(highSuspicionSecondFloorDelayMin, highSuspicionSecondFloorDelayMax);
            Debug.Log($"[ParentWarningSystem] Second-floor delay: {secondFloorDelay:F1}s (HIGH SUSPICION range {highSuspicionSecondFloorDelayMin}-{highSuspicionSecondFloorDelayMax}s | gauge={gauge})");
        }
        else
        {
            secondFloorDelay = Random.Range(secondFloorDelayMin, secondFloorDelayMax);
            Debug.Log($"[ParentWarningSystem] Second-floor delay: {secondFloorDelay:F1}s (LOW SUSPICION range {secondFloorDelayMin}-{secondFloorDelayMax}s | gauge={gauge})");
        }
        yield return new WaitForSeconds(secondFloorDelay);

        if (secondFloorLight1 != null) secondFloorLight1.SetActive(true);
        if (secondFloorLight2 != null) secondFloorLight2.SetActive(true);
        if (secondFloorLight3 != null) secondFloorLight3.SetActive(true);
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] SECOND FLOOR LIGHTS ON");

        float approachDelay;
        if (highSuspicionDelays)
        {
            approachDelay = Random.Range(highSuspicionApproachDelayMin, highSuspicionApproachDelayMax);
            Debug.Log($"[ParentWarningSystem] Approach-start delay: {approachDelay:F1}s (HIGH SUSPICION range {highSuspicionApproachDelayMin}-{highSuspicionApproachDelayMax}s | gauge={gauge})");
        }
        else
        {
            approachDelay = Random.Range(approachDelayMin, approachDelayMax);
            Debug.Log($"[ParentWarningSystem] Approach-start delay: {approachDelay:F1}s (LOW SUSPICION range {approachDelayMin}-{approachDelayMax}s | gauge={gauge})");
        }
        yield return new WaitForSeconds(approachDelay);

        ApplyApproachSpeed(isManual, suspicionFraction);

        RouteState chosenRoute;
        if (routeOverride == RouteOverride.PassBy)
        {
            chosenRoute = RouteState.PassBy;
        }
        else if (routeOverride == RouteOverride.Door)
        {
            chosenRoute = RouteState.DoorPeek;
        }
        else
        {
            chosenRoute = ChooseRoute();
        }

        ActiveRoute = chosenRoute;
        Debug.Log($"[ParentWarningSystem] ROUTE CHOSEN: {ActiveRoute}");

        switch (ActiveRoute)
        {
            case RouteState.DoorPeek:
                approachController.StartApproachDoorOnly();
                break;

            case RouteState.PassBy:
            case RouteState.PassByThenDoorSound:
                approachController.StartApproachPassByOnly();
                break;
        }

        _foreshadowCoroutine = null;
    }

    private RouteState ChooseRoute()
    {
        float suspicionFraction = GetSuspicionFraction();
        float doorChance = Mathf.Lerp(doorChanceAtMinSuspicion, doorChanceAtMaxSuspicion, suspicionFraction);
        float roll = Random.value;

        if (roll < doorChance)
        {
            Debug.Log($"[ParentWarningSystem] ChooseRoute | suspicion={suspicionFraction:F2} | doorChance={doorChance:F2} | result=DoorPeek");
            return RouteState.DoorPeek;
        }

        float nonDoorRoll = Random.value;
        RouteState result = nonDoorRoll < basePassByThenDoorSoundChance
            ? RouteState.PassByThenDoorSound
            : RouteState.PassBy;

        Debug.Log(
            $"[ParentWarningSystem] ChooseRoute | suspicion={suspicionFraction:F2} | doorChance={doorChance:F2} | nonDoorRoll={nonDoorRoll:F2} | result={result}"
        );

        return result;
    }

    private void ApplyApproachSpeed(bool isManual, float suspicionFraction = 0f)
    {
        if (approachController == null) return;

        float speed;

        if (isManual && useFixedDebugApproachSpeed)
        {
            speed = fixedDebugApproachSpeed;
            Debug.Log($"[ParentWarningSystem] APPROACH SPEED: {speed:F2} units/sec (FIXED DEBUG)");
        }
        else
        {
            int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;

            if (!isManual && gauge > highSuspicionSpeedGaugeThreshold)
            {
                speed = Random.Range(highSuspicionApproachMoveSpeedMin, highSuspicionApproachMoveSpeedMax);
                Debug.Log($"[ParentWarningSystem] APPROACH SPEED: {speed:F2} units/sec (HIGH SUSPICION RANGE)");
            }
            else
            {
                speed = Random.Range(approachMoveSpeedMin, approachMoveSpeedMax);

                if (!isManual && approachSpeedSuspicionBonus > 0f)
                    speed += approachSpeedSuspicionBonus * suspicionFraction;

                Debug.Log($"[ParentWarningSystem] APPROACH SPEED: {speed:F2} units/sec (RANDOMISED, suspicion={suspicionFraction:F2})");
            }
        }

        approachController.moveSpeed = speed;
    }

    private float GetSuspicionFraction()
    {
        if (motherGauge == null || motherGauge.maxGauge <= 0)
            return 0f;

        return Mathf.Clamp01((float)motherGauge.currentGauge / motherGauge.maxGauge);
    }

    private void TurnOffAllLights()
    {
        if (firstFloorLight != null) firstFloorLight.SetActive(false);
        if (secondFloorLight1 != null) secondFloorLight1.SetActive(false);
        if (secondFloorLight2 != null) secondFloorLight2.SetActive(false);
        if (secondFloorLight3 != null) secondFloorLight3.SetActive(false);
    }

    private void SubscribeApproachEvents()
    {
        if (_eventsSubscribed || approachController == null) return;

        approachController.OnApproachStarted.AddListener(HandleApproachStarted);
        approachController.OnReachedDoor.AddListener(HandleReachedDoor);
        approachController.OnStoppedAtDoor.AddListener(HandleStoppedAtDoor);
        approachController.OnPassedByDoor.AddListener(HandlePassedByDoor);

        _eventsSubscribed = true;
        Debug.Log("[ParentWarningSystem] Subscribed to ParentApproachController events");
    }

    private void UnsubscribeApproachEvents()
    {
        if (!_eventsSubscribed || approachController == null) return;

        approachController.OnApproachStarted.RemoveListener(HandleApproachStarted);
        approachController.OnReachedDoor.RemoveListener(HandleReachedDoor);
        approachController.OnStoppedAtDoor.RemoveListener(HandleStoppedAtDoor);
        approachController.OnPassedByDoor.RemoveListener(HandlePassedByDoor);

        _eventsSubscribed = false;
    }

    private void HandleApproachStarted()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Approach started");

        if (parentDetection != null)
            parentDetection.OnApproachStarted();
    }

    private void HandleReachedDoor()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Reached door");
    }

    private void HandleStoppedAtDoor()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Stopped at door — forwarding to ParentDetectionV2.OnApproachReachedDoor()");

        if (!isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] HandleStoppedAtDoor: ignoring — warning not active");
            return;
        }

        if (parentDetection != null)
            parentDetection.OnApproachReachedDoor();
        else
            Debug.LogWarning("[ParentWarningSystem] HandleStoppedAtDoor: parentDetection is NULL");
    }

    private void HandlePassedByDoor()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Passed by door");

        if (!isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] HandlePassedByDoor: ignoring — warning not active");
            return;
        }

        if (ActiveRoute == RouteState.PassByThenDoorSound)
        {
            if (_passByThenDoorSoundCoroutine != null)
                StopCoroutine(_passByThenDoorSoundCoroutine);

            _passByThenDoorSoundCoroutine = StartCoroutine(PlayPassByThenDoorSoundCoroutine());
        }

        if (parentDetection != null)
            parentDetection.OnApproachPassedBy();
        else
            Debug.LogWarning("[ParentWarningSystem] HandlePassedByDoor: parentDetection is NULL");
    }

    private IEnumerator PlayPassByThenDoorSoundCoroutine()
    {
        float delay = Mathf.Max(0f, passByThenDoorSoundDelay);
        Debug.Log($"[ParentWarningSystem] PassByThenDoorSound: waiting {delay:F1}s");

        yield return new WaitForSeconds(delay);

        if (passByThenDoorSoundAudioSource != null)
        {
            Debug.Log("[ParentWarningSystem] PassByThenDoorSound: PLAY");
            passByThenDoorSoundAudioSource.Play();
        }
        else
        {
            Debug.LogWarning("[ParentWarningSystem] PassByThenDoorSound: AudioSource is NULL");
        }

        _passByThenDoorSoundCoroutine = null;
    }

    private bool ValidateController()
    {
        if (approachController != null) return true;
        Debug.LogWarning("[ParentWarningSystem] approachController is NULL — assign it in the Inspector.", this);
        return false;
    }
}