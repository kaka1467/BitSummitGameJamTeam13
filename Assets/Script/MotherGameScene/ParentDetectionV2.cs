using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentDetectionV2:
/// Consequence and branch controller — reacts to approach events and owns all gauge-write logic.
///
/// Responsibility boundaries:
///   ParentApproachController  — movement and orientation
///   ParentWarningSystem       — foreshadowing sequence, light cues, route selection, rush-in entry point
///   ParentWarningScheduler    — automatic timing windows and N/M debug keys
///   ParentDetectionV2 (this)  — door branching, suspicion ticks, room-check reset, caught trigger,
///                               and loud-item rush-in initiation (via warningSystem.StartLoudItemRushInSequence)
///
/// Gauge is written in four places:
///   OnLoudItemTriggered              — discrete burst when a loud child-device item fires
///   TriggerPrimaryEvent              — 3-tick burst on room entry if player is NOT sleeping
///   ContinuousRoomSuspicionCoroutine — timed +1 per tick while mother is in the room
///   HallwayPeekSuspicionCoroutine    — timed +1 per tick only when mother is in hallway phase
///                                      AND player is actively peeking (CameraSwitcher.IsPeeking)
///
/// Route branching (primary vs dummy) is driven by warningSystem.ActiveRoute.
/// dummyProbability is a fallback only when ActiveRoute is None (e.g. P-key debug).
/// Peek duration for the current run is: peekDurationBase + motherGauge.currentGauge.
///
/// ── Expected behavior quick-reference ────────────────────────────────────────
/// Normal warning run:
///   lightSwitch audio      — plays only if player is peeking at the moment each light fires
///   movementLoopAudio      — plays only while player is peeking AND approach is active
///   door pause             — pauseAtDoorSeconds
///   immediate game-over    — YES if player peeks after ReachedDoor on DoorPeek route
///
/// Loud-item rush-in:
///   rushInAudioSource      — plays immediately (here, before StartLoudItemRushInSequence)
///   lightSwitch audio      — plays unconditionally (second-floor only)
///   movementLoopAudio      — never plays (IsRushIn suppresses it)
///   door pause             — rushInPauseAtDoorSeconds
///   immediate game-over    — YES, same rule as normal DoorPeek
/// </summary>
public class ParentDetectionV2 : MonoBehaviour
{
    // ── System references ─────────────────────────────────────────────────────
    [Header("System References")]
    public ParentWarningSystem       warningSystem;
    public CaughtReactionController  caughtReactionController;
    public MotherGauge               motherGauge;
    public ParentApproachController  approachController;
    public SleepingController        sleepingController;
    public CameraSwitcher            cameraSwitcher;

    // ── Audio ─────────────────────────────────────────────────────────────────
    [Header("Audio Sources")]
    [Tooltip("Played when the dummy (peek) door event fires.")]
    [SerializeField] private AudioSource dummyDoorAudioSource;
    [Tooltip("Played when the primary (full) door opens.")]
    [SerializeField] private AudioSource mainDoorOpenAudioSource;
    [Tooltip("Played when the door closes at the end of any event.")]
    [SerializeField] private AudioSource mainDoorCloseAudioSource;
    [Tooltip("Played immediately when a loud-item rush-in is triggered (before speed/route setup).")]
    [SerializeField] private AudioSource rushInAudioSource;

    // ── Door ──────────────────────────────────────────────────────────────────
    [Header("Door Control")]
    [SerializeField] private DoorController targetDoorController;

    // ── Branching ─────────────────────────────────────────────────────────────
    [Header("Event Branching")]
    [Tooltip("Fallback probability of a dummy (peek) check when no route state is available from ParentWarningSystem (e.g. P key debug).")]
    [SerializeField, Range(0f, 1f)] private float dummyProbability = 0.3f;

    // ── Peek / room-check timing ─────────────────────────────────────────────────
    [Header("Room-Check Timing")]
    [Tooltip("For the DUMMY (peek-only) event: base duration in seconds. Actual = peekDurationBase + currentGauge.")]
    [SerializeField] private float peekDurationBase = 3f;
    [Tooltip("For the PRIMARY (full check) event: mother stays in the room until the player falls asleep on the pillow. " +
             "If the player never sleeps, the mother leaves after this many seconds as a safety timeout.")]
    [SerializeField] private float roomCheckSafetyTimeout = 30f;
    [Tooltip("Seconds after the player falls asleep before the mother closes the door and leaves (at zero suspicion).")]
    [SerializeField] private float leaveAfterSleepDelay = 2f;
    [Tooltip("Seconds after the player falls asleep before the mother leaves at maximum suspicion. Lerped from leaveAfterSleepDelay at zero suspicion to this at max suspicion.")]
    [SerializeField] private float leaveAfterSleepDelayMax = 6f;

    // ── Room entry suspicion ───────────────────────────────────────────────────
    [Header("Room Entry Suspicion")]
    [Tooltip("Seconds between each of the 3 burst ticks when the mother enters the room and player is NOT sleeping.")]
    [SerializeField] private float roomEntryBurstTickInterval = 0.2f;

    // ── Loud item ─────────────────────────────────────────────────────────────
    [Header("Loud Item Feature")]
    [Tooltip("Disable to make the L-key and any in-game loud-item triggers completely inert.")]
    [SerializeField] private bool enableLoudItemFeature = true;
    [Tooltip("Gauge stages added to MotherGauge when a loud item fires. If this pushes gauge to max, game over triggers immediately instead of a rush-in.")]
    [SerializeField] private int loudItemGaugeAmount = 3;

    // ── Continuous room suspicion ────────────────────────────────────────────────
    [Header("In-Room Continuous Suspicion")]
    [Tooltip("Enable continuous suspicion rise while the mother is inside the room during a full door-check event. Active regardless of peeking.")]
    [SerializeField] private bool enableContinuousRoomSuspicion = true;
    [Tooltip("Gauge stages added per tick while the mother is in the room.")]
    [SerializeField] private int continuousRoomSuspicionAmount = 1;
    [Tooltip("Seconds between each +1 suspicion tick while the mother is in the room during a full door-check. Lower = faster passive suspicion rise in room.")]
    [SerializeField] private float continuousRoomSuspicionTickInterval = 2f;
    // Tune this to control how fast suspicion rises PASSIVELY while the mother is in the room.

    // ── Hallway peek suspicion ───────────────────────────────────────────────────────
    [Header("Hallway / Door-Front Peek Suspicion")]
    [Tooltip("Enable suspicion rise while the mother is in the hallway phase AND the player is peeking (IsPeeking=true). Applies during both approach and door-front standing.")]
    [SerializeField] private bool enableHallwayPeekSuspicion = true;
    [Tooltip("Seconds between each +1 suspicion tick while the player is peeking at the mother in the hallway or at the door. Lower = faster suspicion rise when peeking.")]
    [SerializeField] private float hallwayPeekSuspicionTickInterval = 0.5f;
    // Tune this to control how fast suspicion rises when the player ACTIVELY PEEKS at the mother.

    // ── Public state ──────────────────────────────────────────────────────────
    public bool isCaught          = false;
    public bool isMotherLookingNow = false;

    // ── Private state ─────────────────────────────────────────────────────────
    private Coroutine    dummyResetCoroutine        = null;
    private Coroutine    primaryResetCoroutine      = null;
    private Coroutine    hallwayPeekCoroutine       = null;
    private Coroutine    continuousRoomCoroutine    = null;
    private bool         hasPermanentGameOver       = false;
    private float        _activePeekDuration        = 3f;

    // ──────────────────────────────────────────────────────────────────────────
    //  Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        isCaught           = false;
        isMotherLookingNow = false;

        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        if (motherGauge != null)
            motherGauge.enableAutoDecrease = true;

        if (targetDoorController == null)
            targetDoorController = Object.FindFirstObjectByType<DoorController>();

        if (warningSystem == null)
            warningSystem = Object.FindFirstObjectByType<ParentWarningSystem>();

        if (caughtReactionController == null)
            caughtReactionController = Object.FindFirstObjectByType<CaughtReactionController>();

        if (approachController == null)
            approachController = Object.FindFirstObjectByType<ParentApproachController>();

        if (sleepingController == null)
            sleepingController = Object.FindFirstObjectByType<SleepingController>();

        if (cameraSwitcher == null)
            cameraSwitcher = Object.FindFirstObjectByType<CameraSwitcher>();
    }

    private void Update()
    {
        // Immediate game-over rule:
        //   Condition: DoorPeek route active + mother has ReachedDoor/StoppedAtDoor + player is peeking.
        //   Result: OnPlayerCaught() fires in the same frame the peek starts — no grace period.
        if (!hasPermanentGameOver && !isCaught)
        {
            if (cameraSwitcher   != null && cameraSwitcher.IsPeeking   &&
                warningSystem    != null && warningSystem.isWarningActive &&
                warningSystem.ActiveRoute == ParentWarningSystem.RouteState.DoorPeek &&
                approachController != null && (approachController.ReachedDoor || approachController.StoppedAtDoor))
            {
                Debug.Log("[PDV2] Immediate caught: player peeked after mother reached the door");
                OnPlayerCaught();
            }
        }

        // ── Debug keys (editor / playtesting only) ────────────────────────────
        if (Keyboard.current == null) return;

        if (Keyboard.current.pKey.wasPressedThisFrame)  // P — force primary (full room-check) event
        {
            Debug.Log("[PDV2] P key — forcing primary (full) check");
            TriggerFinalEvent(primary: true);
        }

        if (Keyboard.current.oKey.wasPressedThisFrame)  // O — force dummy (peek-only) event
        {
            Debug.Log("[PDV2] O key — forcing dummy (peek) check");
            TriggerFinalEvent(primary: false);
        }

        if (Keyboard.current.lKey.wasPressedThisFrame)  // L — simulate loud child-device item
        {
            Debug.Log("[PDV2] L key — triggering loud item");
            OnLoudItemTriggered();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Public API — called by ParentWarningSystem
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by ParentWarningSystem when the mother has stopped at the door.
    /// Uses warningSystem.ActiveRoute to branch — route was decided before movement started.
    /// Falls back to dummyProbability only when ActiveRoute is None (e.g. P key debug).
    /// </summary>
    public void OnApproachReachedDoor()
    {
        Debug.Log($"[PDV2] OnApproachReachedDoor | isCaught={isCaught} hasPermanentGameOver={hasPermanentGameOver}");
        if (isCaught || hasPermanentGameOver) return;

        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
        _activePeekDuration = peekDurationBase + gauge;
        Debug.Log($"[PDV2] activePeekDuration={_activePeekDuration:F1}s (base={peekDurationBase:F1} + gauge={gauge})");

        bool primary;
        var route = (warningSystem != null) ? warningSystem.ActiveRoute : ParentWarningSystem.RouteState.None;

        if (route == ParentWarningSystem.RouteState.DoorPeek)
        {
            primary = true;
            Debug.Log("[PDV2] Branch: PRIMARY — from ActiveRoute=DoorPeek");
        }
        else if (route == ParentWarningSystem.RouteState.PassBy || route == ParentWarningSystem.RouteState.PassByThenDoorSound)
        {
            primary = false;
            Debug.LogWarning("[PDV2] WARNING: OnApproachReachedDoor was called on a non-door route");
        }
        else
        {
            bool isDummy = Random.value < dummyProbability;
            primary = !isDummy;
            Debug.Log($"[PDV2] Branch: fallback random isDummy={isDummy} (dummyProbability={dummyProbability:F2})");
        }

        TriggerFinalEvent(primary: primary);
    }

    /// <summary>
    /// Called by ParentWarningSystem when the mother passes by without stopping.
    /// Resets the cycle cleanly — no suspicion increase, no door event.
    /// </summary>
    public void OnApproachPassedBy()
    {
        Debug.Log($"[PDV2] OnApproachPassedBy | isCaught={isCaught} hasPermanentGameOver={hasPermanentGameOver}");
        if (isCaught || hasPermanentGameOver) return;

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();
    }

    public void NotifyGameOver()
    {
        hasPermanentGameOver = true;
    }

    /// <summary>
    /// Called when a loud child-device item is triggered (or by L-key debug).
    /// Plays rush-in audio, adds gauge, then hands off to ParentWarningSystem.StartLoudItemRushInSequence().
    /// Fully suppressed if a warning sequence is already active.
    /// </summary>
    public void OnLoudItemTriggered()
    {
        if (!enableLoudItemFeature)
        {
            Debug.Log("[PDV2] Loud Item Feature is DISABLED");
            return;
        }

        if (isCaught || hasPermanentGameOver) return;

        if (warningSystem != null && warningSystem.isWarningActive)
        {
            Debug.Log("[PDV2] Loud item ignored because warning is already active");
            return;
        }

        Debug.Log($"[PDV2] Loud item triggered rush-in request — adding {loudItemGaugeAmount} gauge stages");

        if (rushInAudioSource != null)
            rushInAudioSource.Play();

        if (motherGauge != null)
        {
            motherGauge.AddGauge(loudItemGaugeAmount);

            if (motherGauge.currentGauge >= motherGauge.maxGauge)
            {
                OnPlayerCaught();
                return;
            }
        }

        if (warningSystem != null)
            warningSystem.StartLoudItemRushInSequence();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Final event branching
    // ──────────────────────────────────────────────────────────────────────────

    private void TriggerFinalEvent(bool primary)
    {
        if (primary) TriggerPrimaryEvent();
        else         TriggerDummyEvent();
    }

    private void TriggerPrimaryEvent()
    {
        Debug.Log("[PDV2] TriggerPrimaryEvent — door FULL open, isMotherLookingNow=true");
        isMotherLookingNow = true;

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Full);

        if (mainDoorOpenAudioSource != null)
            mainDoorOpenAudioSource.Play();

        if (caughtReactionController != null)
            caughtReactionController.OnMotherCheck(isFullCheck: true);

        // Room-entry suspicion: increase gauge when player is NOT sleeping.
        bool playerIsSleeping = (sleepingController != null) && sleepingController.IsSleeping;
        int gaugeBefore = (motherGauge != null) ? motherGauge.currentGauge : 0;
        Debug.Log($"[PDV2] Room entry | isMotherLookingNow=true | IsSleeping={playerIsSleeping} | gauge before={gaugeBefore}");

        if (!playerIsSleeping && motherGauge != null)
        {
            Debug.Log($"[PDV2] Room entry suspicion burst starting | count=3 interval={roomEntryBurstTickInterval}s");
            StartCoroutine(RoomEntryBurstSuspicionCoroutine());
        }
        else
        {
            Debug.Log("[PDV2] Room entry suspicion SKIPPED — player is sleeping");
        }

        if (!hasPermanentGameOver)
        {
            if (primaryResetCoroutine != null) StopCoroutine(primaryResetCoroutine);
            primaryResetCoroutine = StartCoroutine(HandlePrimaryResetSequence());
        }

        if (enableContinuousRoomSuspicion && !hasPermanentGameOver)
        {
            if (continuousRoomCoroutine != null) StopCoroutine(continuousRoomCoroutine);
            continuousRoomCoroutine = StartCoroutine(ContinuousRoomSuspicionCoroutine());
            Debug.Log("[PDV2] Continuous room suspicion started");
        }

    }

    private IEnumerator HandlePrimaryResetSequence()
    {
        Debug.Log("[PDV2] HandlePrimaryResetSequence: waiting for player sleep");

        float elapsed = 0f;
        float timeout = Mathf.Max(0f, roomCheckSafetyTimeout);

        while (true)
        {
            if (hasPermanentGameOver || isCaught)
                yield break;

            bool sleeping = (sleepingController != null) && sleepingController.IsSleeping;
            if (sleeping)
            {
                Debug.Log("[PDV2] player fell asleep — mother will leave");
                break;
            }

            if (timeout > 0f)
            {
                elapsed += Time.deltaTime;
                if (elapsed >= timeout)
                {
                    Debug.Log($"[PDV2] safety timeout reached after {timeout:F1}s — mother leaving anyway");
                    break;
                }
            }

            yield return null;
        }

        float suspicionFraction = (motherGauge != null && motherGauge.maxGauge > 0)
            ? Mathf.Clamp01((float)motherGauge.currentGauge / motherGauge.maxGauge)
            : 0f;
        float leaveDelay = Mathf.Max(0f, Mathf.Lerp(leaveAfterSleepDelay, leaveAfterSleepDelayMax, suspicionFraction));
        Debug.Log($"[PDV2] HandlePrimaryResetSequence: leave delay={leaveDelay:F1}s (suspicion={suspicionFraction:F2})");
        if (leaveDelay > 0f)
            yield return new WaitForSeconds(leaveDelay);

        if (hasPermanentGameOver || isCaught)
            yield break;

        if (mainDoorCloseAudioSource != null)
            mainDoorCloseAudioSource.Play();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();

        primaryResetCoroutine = null;
    }

    private void TriggerDummyEvent()
    {
        Debug.Log("[PDV2] TriggerDummyEvent — door PEEK open, isMotherLookingNow=false");
        isMotherLookingNow = false;

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Peek);

        if (dummyDoorAudioSource != null) dummyDoorAudioSource.Play();

        if (caughtReactionController != null)
            caughtReactionController.OnMotherCheck(isFullCheck: false);

        if (dummyResetCoroutine != null) StopCoroutine(dummyResetCoroutine);
        dummyResetCoroutine = StartCoroutine(HandleDummySequence());
    }

    private IEnumerator HandleDummySequence()
    {
        Debug.Log($"[PDV2] HandleDummySequence: activePeekDuration={_activePeekDuration:F1}s");
        yield return new WaitForSeconds(_activePeekDuration);

        if (mainDoorCloseAudioSource != null) mainDoorCloseAudioSource.Play();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();

        dummyResetCoroutine = null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Room entry burst suspicion
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator RoomEntryBurstSuspicionCoroutine()
    {
        for (int i = 0; i < 3; i++)
        {
            if (hasPermanentGameOver || isCaught) yield break;
            if (motherGauge == null) yield break;

            motherGauge.AddGauge(1);
            Debug.Log($"[PDV2] Room entry burst tick {i + 1}/3 | +1 | gauge now {motherGauge.currentGauge}");

            if (motherGauge.currentGauge >= motherGauge.maxGauge)
            {
                Debug.Log("[PDV2] Room entry burst stopped — gauge reached max");
                OnPlayerCaught();
                yield break;
            }

            yield return new WaitForSeconds(roomEntryBurstTickInterval);
        }

        Debug.Log("[PDV2] Room entry burst complete");
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Continuous room suspicion
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator ContinuousRoomSuspicionCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(continuousRoomSuspicionTickInterval);

            bool _contSleeping = (sleepingController != null) && sleepingController.IsSleeping;
            int  _contGauge    = (motherGauge != null) ? motherGauge.currentGauge : 0;
            Debug.Log($"[PDV2] Continuous room suspicion state | motherLooking={isMotherLookingNow} | playerSleeping={_contSleeping} | gaugeBefore={_contGauge}");

            if (hasPermanentGameOver || isCaught)
            {
                Debug.Log("[PDV2] Continuous room suspicion stopped — game over or caught");
                yield break;
            }
            if (!isMotherLookingNow)
            {
                Debug.Log("[PDV2] Continuous room suspicion stopped — isMotherLookingNow is false");
                yield break;
            }
            if (motherGauge == null)
            {
                Debug.Log("[PDV2] Continuous room suspicion stopped — motherGauge is null");
                yield break;
            }

            bool sleeping = (sleepingController != null) && sleepingController.IsSleeping;
            if (sleeping)
            {
                Debug.Log("[PDV2] Continuous room suspicion skipped because player is sleeping");
                continue;
            }

            motherGauge.AddGauge(continuousRoomSuspicionAmount);
            Debug.Log($"[PDV2] Continuous room suspicion tick +{continuousRoomSuspicionAmount} | gauge now {motherGauge.currentGauge}");

            if (motherGauge.currentGauge >= motherGauge.maxGauge)
            {
                Debug.Log("[PDV2] Continuous room suspicion stopped — gauge reached max");
                OnPlayerCaught();
                yield break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Hallway peek suspicion
    // ──────────────────────────────────────────────────────────────────────────

    public void OnApproachStarted()
    {
        if (!enableHallwayPeekSuspicion) return;

        if (hallwayPeekCoroutine != null)
        {
            Debug.Log("[PDV2] OnApproachStarted: HallwayPeekSuspicion already running — skipping");
            return;
        }

        Debug.Log("[PDV2] OnApproachStarted: starting HallwayPeekSuspicion");
        hallwayPeekCoroutine = StartCoroutine(HallwayPeekSuspicionCoroutine());
    }

    private void TryStartHallwayPeekSuspicion()
    {
        if (!enableHallwayPeekSuspicion) return;
        if (hallwayPeekCoroutine != null) return;
        if (approachController == null)  return;
        if (warningSystem == null || !warningSystem.isWarningActive) return;

        Debug.Log("[PDV2] TryStartHallwayPeekSuspicion: starting HallwayPeekSuspicion");
        hallwayPeekCoroutine = StartCoroutine(HallwayPeekSuspicionCoroutine());
    }

    private IEnumerator HallwayPeekSuspicionCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(hallwayPeekSuspicionTickInterval);

            bool _warningActive  = (warningSystem      != null) && warningSystem.isWarningActive;
            bool _hallway        = (approachController != null) && approachController.IsInHallwayPhase;
            bool _playerPeeking  = (cameraSwitcher     != null) && cameraSwitcher.IsPeeking;
            Debug.Log($"[PDV2] HallwayPeekSuspicion state | warningActive={_warningActive} | hallway={_hallway} | playerPeeking={_playerPeeking}");

            if (hasPermanentGameOver || isCaught)
            {
                Debug.Log("[PDV2] HallwayPeekSuspicion stopped — game over or caught");
                yield break;
            }
            if (!_warningActive)
            {
                Debug.Log("[PDV2] HallwayPeekSuspicion stopped — warning sequence ended");
                yield break;
            }
            if (motherGauge == null)
            {
                Debug.Log("[PDV2] HallwayPeekSuspicion stopped — motherGauge is null");
                yield break;
            }

            if (!_hallway)
            {
                Debug.Log("[PDV2] HallwayPeekSuspicion tick SKIPPED — not in hallway phase");
                continue;
            }
            if (!_playerPeeking)
            {
                Debug.Log("[PDV2] HallwayPeekSuspicion tick SKIPPED — player is not peeking (IsPeeking=false)");
                continue;
            }

            motherGauge.AddGauge(1);
            Debug.Log($"[PDV2] HallwayPeekSuspicion tick +1 | gauge now {motherGauge.currentGauge}");

            if (motherGauge.currentGauge >= motherGauge.maxGauge)
            {
                Debug.Log("[PDV2] HallwayPeekSuspicion stopped — gauge reached max");
                OnPlayerCaught();
                yield break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Cycle reset
    // ──────────────────────────────────────────────────────────────────────────

    private void ResetCycle()
    {
        Debug.Log("[PDV2] ResetCycle");

        if (dummyResetCoroutine != null)      { StopCoroutine(dummyResetCoroutine);      dummyResetCoroutine      = null; }
        if (primaryResetCoroutine != null)    { StopCoroutine(primaryResetCoroutine);    primaryResetCoroutine    = null; }
        if (continuousRoomCoroutine != null)  { StopCoroutine(continuousRoomCoroutine);  continuousRoomCoroutine  = null; Debug.Log("[PDV2] Continuous room suspicion stopped — ResetCycle"); }
        if (hallwayPeekCoroutine != null)     { StopCoroutine(hallwayPeekCoroutine);     hallwayPeekCoroutine     = null; Debug.Log("[PDV2] HallwayPeekSuspicion stopped — ResetCycle"); }

        isMotherLookingNow    = false;
        _activePeekDuration   = peekDurationBase;

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Game over
    // ──────────────────────────────────────────────────────────────────────────

    private void OnPlayerCaught()
    {
        Debug.Log("[PDV2] OnPlayerCaught — GAME OVER");
        isCaught           = true;
        isMotherLookingNow = true;
        Debug.LogError("GAME OVER: Caught by Mother!");

        if (caughtReactionController != null)
            caughtReactionController.ForceGameOver();
    }
}