using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// ParentApproachController:
/// Moves the mother along inspector-assigned waypoints in two explicit routes:
///   Pass-by  : startPoint → stairClimbPoints[] → stairTurnPoint → hallwayPoints[] → doorPoint → passByPoint
///   Door only: startPoint → stairClimbPoints[] → stairTurnPoint → hallwayPoints[] → doorPoint (stop)
///
/// Rotation rules (fixed Y, X/Z locked):
///   Stair climb  : Y = -90  (set instantly at phase start)
///   Stair turn   : Y =   0  (smooth rotation at stairTurnPoint)
///   Door arrival : Y =  90  (smooth rotation at doorPoint)
///
/// Movement loop audio:
///   Managed every frame in UpdateMovementLoopAudio().
///   Plays only when IsApproaching=true, IsRushIn=false, and CameraSwitcher.IsPeeking=true.
///   Stopped immediately when any condition is no longer met, and on ResetStateFlags().
///
/// Rush-in mode (IsRushIn=true):
///   Set by ParentWarningSystem before calling StartApproachDoorOnly() for a loud-item rush.
///   Suppresses movement loop audio and uses rushInPauseAtDoorSeconds instead of pauseAtDoorSeconds.
///   Cleared automatically in ResetStateFlags().
/// </summary>
public class ParentApproachController : MonoBehaviour
{
    // ── Waypoints ─────────────────────────────────────────────────────────────
    [Header("Waypoints")]
    [Tooltip("Where the mother spawns and resets to.")]
    public Transform startPoint;

    [Tooltip("Waypoints for climbing the stairs. Mother faces Y=-90 throughout.")]
    public Transform[] stairClimbPoints;

    [Tooltip("Single point where stair climbing ends and the mother rotates toward the hallway (Y=0).")]
    public Transform stairTurnPoint;

    [Tooltip("Waypoints for hallway movement after the stair turn.")]
    public Transform[] hallwayPoints;

    [Tooltip("Position in front of the door. Mother rotates to Y=90 on arrival.")]
    public Transform doorPoint;

    [Tooltip("Where the mother walks after passing the door (pass-by route only).")]
    public Transform passByPoint;

    // ── Movement ──────────────────────────────────────────────────────────────
    [Header("Movement")]
    [Tooltip("Base movement speed (units/sec).")]
    public float moveSpeed = 2f;

    [Tooltip("How close (units) the mother must be to a waypoint to count as arrived.")]
    public float stopDistance = 0.05f;

    // ── Rotation Speeds ───────────────────────────────────────────────────────
    [Header("Rotation Speeds")]
    [Tooltip("Speed (deg/sec) when rotating at the stair corner turn.")]
    public float stairTurnRotationSpeed = 90f;

    [Tooltip("Speed (deg/sec) when rotating to face the door on arrival.")]
    public float doorTurnRotationSpeed = 120f;

    // ── Visibility ────────────────────────────────────────────────────────────
    [Header("Mother Model Visibility")]
    [Tooltip("Root GameObject of the walking mother model. Shown on approach start via ShowMotherModel(). Hiding is managed externally (e.g. by a scene controller after the cycle ends).")]
    public GameObject motherModelRoot;
    [Tooltip("Optional: child Renderers to enable/disable if motherModelRoot alone is not enough (e.g. LOD children).")]
    public Renderer[] motherModelRenderers;

    // ── Audio ──────────────────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("AudioSource looped during stair climb and hallway walk. Follows peeking state on normal runs; always suppressed on rush-in runs.")]
    public AudioSource movementLoopAudioSource;
    [Tooltip("CameraSwitcher used to determine if the player is peeking. Auto-found at Start if not assigned.")]
    public CameraSwitcher cameraSwitcher;

    // ── Timing ────────────────────────────────────────────────────────────────
    [Header("Timing")]
    [Tooltip("Seconds the mother pauses at the door before the OnStoppedAtDoor event (normal run).")]
    public float pauseAtDoorSeconds = 2f;

    [Tooltip("Seconds the mother pauses at the door before the OnStoppedAtDoor event (loud-item rush-in run).")]
    public float rushInPauseAtDoorSeconds = 0.2f;

    [Tooltip("Seconds the mother pauses at the door before continuing to passByPoint.")]
    public float pauseBeforePassBySeconds = 0.5f;

    // ── Events ────────────────────────────────────────────────────────────────
    [Header("Events")]
    public UnityEvent OnApproachStarted;
    public UnityEvent OnReachedDoor;
    public UnityEvent OnStoppedAtDoor;
    public UnityEvent OnPassedByDoor;

    // ── Public read-only state ────────────────────────────────────────────────
    public bool IsApproaching    { get; private set; }
    public bool ReachedDoor      { get; private set; }
    public bool StoppedAtDoor    { get; private set; }
    public bool PassedByDoor     { get; private set; }
    public bool IsInHallwayPhase { get; private set; }

    // ── Run mode ──────────────────────────────────────────────────────────────
    /// <summary>Set by ParentWarningSystem before starting a loud-item rush-in. Suppresses the normal movement loop audio.</summary>
    public bool IsRushIn         { get; set; }

    // ── Private ───────────────────────────────────────────────────────────────
    private Coroutine _approachCoroutine;
    private float _fixedPitch;
    private float _fixedRoll;

    private void Start()
    {
        if (cameraSwitcher == null)
            cameraSwitcher = Object.FindFirstObjectByType<CameraSwitcher>();
    }

    private void Update()
    {
        UpdateMovementLoopAudio();
    }

    private void UpdateMovementLoopAudio()
    {
        if (movementLoopAudioSource == null) return;
        // Rule: play only on a normal (non-rush-in) run while the approach is active AND player is peeking.
        bool shouldPlay = !IsRushIn && IsApproaching && cameraSwitcher != null && cameraSwitcher.IsPeeking;
        if (shouldPlay && !movementLoopAudioSource.isPlaying)
        {
            movementLoopAudioSource.loop = true;
            movementLoopAudioSource.Play();
            Debug.Log("[ParentApproachController] Movement loop started (peeking)");
        }
        else if (!shouldPlay && movementLoopAudioSource.isPlaying)
        {
            movementLoopAudioSource.Stop();
            Debug.Log("[ParentApproachController] Movement loop stopped (not peeking or rush-in)");
        }
    }

    // Fixed yaw angles — not exposed; adjusted via requirements only
    private const float StairYaw   = -90f;
    private const float HallwayYaw =   0f;
    private const float DoorYaw    =  90f;

    // ──────────────────────────────────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Default entry point — currently delegates to StartApproachDoorOnly().
    /// Kept for backward-compatible scene wiring.
    /// </summary>
    public void StartApproach()
    {
        StartApproachDoorOnly();
    }

    /// <summary>Starts a pass-by run: mother walks through the hallway, past the door, to passByPoint.</summary>
    public void StartApproachPassByOnly()
    {
        if (IsApproaching)
        {
            Debug.Log("[ParentApproachController] Already approaching — ignoring StartApproachPassByOnly");
            return;
        }
        if (!ValidateWaypoints(requirePassBy: true)) return;

        BeginApproach(passByRoute: true);
    }

    /// <summary>Starts a door-stop run: mother walks to doorPoint, rotates to face the room, and stops. Also used for rush-in runs.</summary>
    public void StartApproachDoorOnly()
    {
        if (IsApproaching)
        {
            Debug.Log("[ParentApproachController] Already approaching — ignoring StartApproachDoorOnly");
            return;
        }
        if (!ValidateWaypoints(requirePassBy: false)) return;

        BeginApproach(passByRoute: false);
    }

    /// <summary>Cancels any in-progress approach coroutine, resets all state flags, and teleports the mother back to startPoint.</summary>
    public void ResetApproach()
    {
        Debug.Log($"[ParentApproachController] ResetApproach | IsApproaching={IsApproaching}");

        if (_approachCoroutine != null)
        {
            StopCoroutine(_approachCoroutine);
            _approachCoroutine = null;
        }

        ResetStateFlags();

        if (startPoint != null)
        {
            transform.position = startPoint.position;
            transform.rotation = startPoint.rotation;
        }
        else
        {
            Debug.LogWarning("[ParentApproachController] ResetApproach: startPoint is NULL — cannot reposition.");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Internal start helper
    // ──────────────────────────────────────────────────────────────────────────

    private void BeginApproach(bool passByRoute)
    {
        ResetStateFlags();

        // Capture pitch/roll from startPoint.rotation so a stale mid-run transform
        // does not carry over incorrect values after a cancelled cycle.
        Vector3 startEuler = startPoint.rotation.eulerAngles;
        _fixedPitch = startEuler.x;
        _fixedRoll  = startEuler.z;

        transform.position = startPoint.position;
        transform.rotation = startPoint.rotation;
        SetYaw(StairYaw);

        ShowMotherModel();

        IsApproaching = true;
        OnApproachStarted?.Invoke();

        Debug.Log($"[ParentApproachController] BeginApproach | passByRoute={passByRoute} | pitch={_fixedPitch:F1} roll={_fixedRoll:F1}");
        _approachCoroutine = StartCoroutine(passByRoute ? PassByRoutine() : DoorRoutine());
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Coroutines
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator DoorRoutine()
    {
        Debug.Log("[ParentApproachController] DoorRoutine: START");

        yield return RunStairPhase();
        yield return RunHallwayPhase();

        Debug.Log($"[ParentApproachController] Phase: DOOR | moving to '{doorPoint.name}' then rotate to yaw=90");
        yield return MoveToPoint(doorPoint);
        yield return RotateToYaw(DoorYaw, doorTurnRotationSpeed);

        ReachedDoor = true;
        Debug.Log("[ParentApproachController] REACHED DOOR — firing OnReachedDoor");
        OnReachedDoor?.Invoke();

        float doorPause = IsRushIn ? rushInPauseAtDoorSeconds : pauseAtDoorSeconds;
        Debug.Log($"[ParentApproachController] Door pause: {doorPause:F2}s (IsRushIn={IsRushIn})");
        yield return new WaitForSeconds(doorPause);

        StopMovementAudio();
        StoppedAtDoor = true;
        IsApproaching = false;
        // IsInHallwayPhase is intentionally NOT cleared here.
        // PDV2.HallwayPeekSuspicionCoroutine checks it to decide whether peek-ticks should fire
        // while the mother stands at the door. ResetStateFlags() (via ResetApproach) clears it
        // after the full cycle ends.

        Debug.Log("[ParentApproachController] STOPPED AT DOOR — firing OnStoppedAtDoor");
        OnStoppedAtDoor?.Invoke();
    }

    private IEnumerator PassByRoutine()
    {
        Debug.Log("[ParentApproachController] PassByRoutine: START");

        yield return RunStairPhase();
        yield return RunHallwayPhase();

        Debug.Log($"[ParentApproachController] Phase: DOOR (pass-by) | moving through '{doorPoint.name}' — no stop, no rotation");
        yield return MoveToPoint(doorPoint);

        yield return new WaitForSeconds(pauseBeforePassBySeconds);

        Debug.Log($"[ParentApproachController] Phase: PASS-BY | moving to '{passByPoint.name}'");
        yield return MoveToPoint(passByPoint);

        StopMovementAudio();
        PassedByDoor  = true;
        IsApproaching = false;
        // IsInHallwayPhase cleared by ResetStateFlags() only — consistent with DoorRoutine behaviour.

        Debug.Log("[ParentApproachController] PASSED BY DOOR — firing OnPassedByDoor");
        OnPassedByDoor?.Invoke();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Shared phase helpers
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator RunStairPhase()
    {
        SetYaw(StairYaw);
        Debug.Log($"[ParentApproachController] Phase: STAIR CLIMB | yaw=-90 | IsRushIn={IsRushIn}");

        if (stairClimbPoints != null)
        {
            for (int i = 0; i < stairClimbPoints.Length; i++)
            {
                if (stairClimbPoints[i] == null) continue;
                Debug.Log($"[ParentApproachController]   stairClimbPoints[{i}] '{stairClimbPoints[i].name}'");
                yield return MoveToPoint(stairClimbPoints[i]);
            }
        }

        if (stairTurnPoint != null)
        {
            Debug.Log($"[ParentApproachController] Phase: STAIR TURN | moving to '{stairTurnPoint.name}' then rotate to yaw=0");
            yield return MoveToPoint(stairTurnPoint);
            yield return RotateToYaw(HallwayYaw, stairTurnRotationSpeed);
            Debug.Log("[ParentApproachController]   STAIR TURN complete — movement audio continues into hallway");
        }
    }

    private IEnumerator RunHallwayPhase()
    {
        IsInHallwayPhase = true;
        Debug.Log("[ParentApproachController] Phase: HALLWAY | IsInHallwayPhase=true");

        if (hallwayPoints != null)
        {
            for (int i = 0; i < hallwayPoints.Length; i++)
            {
                if (hallwayPoints[i] == null) continue;
                Debug.Log($"[ParentApproachController]   hallwayPoints[{i}] '{hallwayPoints[i].name}'");
                yield return MoveToPoint(hallwayPoints[i]);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Movement, rotation, and audio helpers
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator MoveToPoint(Transform target)
    {
        while (Vector3.Distance(transform.position, target.position) > stopDistance)
        {
            transform.position = Vector3.MoveTowards(
                transform.position, target.position, moveSpeed * Time.deltaTime);
            yield return null;
        }
        transform.position = target.position;
    }

    private IEnumerator RotateToYaw(float targetYaw, float speed)
    {
        float current = NormalizeAngle(transform.rotation.eulerAngles.y);
        float target  = NormalizeAngle(targetYaw);

        while (Mathf.Abs(NormalizeAngle(transform.rotation.eulerAngles.y) - target) > 0.5f)
        {
            float newYaw = Mathf.MoveTowardsAngle(
                transform.rotation.eulerAngles.y, targetYaw, speed * Time.deltaTime);
            transform.rotation = Quaternion.Euler(_fixedPitch, newYaw, _fixedRoll);
            yield return null;
        }
        SetYaw(targetYaw);
    }

    private void SetYaw(float yaw)
    {
        transform.rotation = Quaternion.Euler(_fixedPitch, yaw, _fixedRoll);
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f)  angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }

    private void StopMovementAudio()
    {
        if (movementLoopAudioSource != null && movementLoopAudioSource.isPlaying)
        {
            movementLoopAudioSource.Stop();
            Debug.Log("[ParentApproachController] Movement audio stopped");
        }
    }

    private void ShowMotherModel()
    {
        if (motherModelRoot != null)
        {
            motherModelRoot.SetActive(true);
            Debug.Log($"[ParentApproachController] Mother model visibility restored | object='{motherModelRoot.name}'");
        }

        if (motherModelRenderers != null)
        {
            foreach (var r in motherModelRenderers)
            {
                if (r == null) continue;
                r.enabled = true;
                Debug.Log($"[ParentApproachController] Mother model visibility restored | renderer='{r.name}'");
            }
        }
    }

    private void ResetStateFlags()
    {
        IsApproaching    = false;
        ReachedDoor      = false;
        StoppedAtDoor    = false;
        PassedByDoor     = false;
        IsInHallwayPhase = false;
        IsRushIn         = false;
        StopMovementAudio();
    }

    private bool ValidateWaypoints(bool requirePassBy)
    {
        if (startPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] startPoint is NULL.", this);
            return false;
        }
        if (doorPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] doorPoint is NULL.", this);
            return false;
        }
        if (requirePassBy && passByPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] passByPoint is NULL — required for pass-by route.", this);
            return false;
        }
        if (stairTurnPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] stairTurnPoint is NULL — stair-to-hallway turn will be skipped.", this);
        }
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Scene gizmos
    // ──────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform prev = startPoint;

        // startPoint — white
        if (startPoint != null)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawSphere(startPoint.position, 0.08f);
        }

        // stairClimbPoints — cyan
        Gizmos.color = Color.cyan;
        if (stairClimbPoints != null)
        {
            foreach (Transform wp in stairClimbPoints)
            {
                if (wp == null) continue;
                Gizmos.DrawSphere(wp.position, 0.06f);
                if (prev != null) Gizmos.DrawLine(prev.position, wp.position);
                prev = wp;
            }
        }

        // stairTurnPoint — blue
        if (stairTurnPoint != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(stairTurnPoint.position, 0.09f);
            if (prev != null) Gizmos.DrawLine(prev.position, stairTurnPoint.position);
            prev = stairTurnPoint;
        }

        // hallwayPoints — green
        Gizmos.color = Color.green;
        if (hallwayPoints != null)
        {
            foreach (Transform wp in hallwayPoints)
            {
                if (wp == null) continue;
                Gizmos.DrawSphere(wp.position, 0.06f);
                if (prev != null) Gizmos.DrawLine(prev.position, wp.position);
                prev = wp;
            }
        }

        // doorPoint — yellow
        if (doorPoint != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(doorPoint.position, 0.09f);
            if (prev != null) Gizmos.DrawLine(prev.position, doorPoint.position);
            prev = doorPoint;
        }

        // passByPoint — magenta
        if (passByPoint != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawSphere(passByPoint.position, 0.08f);
            if (prev != null) Gizmos.DrawLine(prev.position, passByPoint.position);
        }
    }
#endif
}