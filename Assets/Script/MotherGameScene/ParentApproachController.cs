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
    [Tooltip("The root GameObject of the walking mother model. Will be SetActive(true) on approach start and SetActive(false) is NOT called here — PDV2 manages hide via realMotherObject.")]
    public GameObject motherModelRoot;
    [Tooltip("Optional: child Renderers to enable/disable if motherModelRoot alone is not enough (e.g. LOD children).")]
    public Renderer[] motherModelRenderers;

    // ── Audio ──────────────────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("AudioSource looped during stair climb and hallway walk. Stopped automatically when the mother stops at the door, completes a pass-by, resets, or is cancelled.")]
    public AudioSource movementLoopAudioSource;

    // ── Timing ────────────────────────────────────────────────────────────────
    [Header("Timing")]
    [Tooltip("Seconds the mother pauses at the door before the OnStoppedAtDoor event.")]
    public float pauseAtDoorSeconds = 2f;

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

    // ── Private ───────────────────────────────────────────────────────────────
    private Coroutine _approachCoroutine;
    private float _fixedPitch;
    private float _fixedRoll;

    // Fixed yaw angles — not exposed; adjusted via requirements only
    private const float StairYaw   = -90f;
    private const float HallwayYaw =   0f;
    private const float DoorYaw    =  90f;

    // ──────────────────────────────────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Starts the approach and randomly picks pass-by or door (default entry point).</summary>
    public void StartApproach()
    {
        StartApproachDoorOnly();
    }

    /// <summary>Pass-by route: mother walks all the way past the door without stopping.</summary>
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

    /// <summary>Door route: mother walks to the door and stops there.</summary>
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

        yield return new WaitForSeconds(pauseAtDoorSeconds);

        StopMovementAudio();
        StoppedAtDoor = true;
        IsApproaching = false;
        // IsInHallwayPhase intentionally NOT cleared here.
        // It must remain true while the mother is at the door so that
        // ParentDetectionV2.TryStartHallwayPeekSuspicion() can fire correctly.
        // ResetStateFlags() (called from ResetApproach) clears it after the cycle ends.

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
        // IsInHallwayPhase cleared by ResetStateFlags() only — consistent with DoorRoutine.

        Debug.Log("[ParentApproachController] PASSED BY DOOR — firing OnPassedByDoor");
        OnPassedByDoor?.Invoke();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Shared phase helpers
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator RunStairPhase()
    {
        SetYaw(StairYaw);
        Debug.Log("[ParentApproachController] Phase: STAIR CLIMB | yaw=-90");

        if (movementLoopAudioSource != null)
        {
            movementLoopAudioSource.loop = true;
            movementLoopAudioSource.Play();
        }

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
    //  Movement / rotation helpers
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