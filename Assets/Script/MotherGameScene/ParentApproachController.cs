using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// ParentApproachController:
/// Moves the mother model from downstairs to the door along explicitly separated
/// movement and rotation-change waypoints, then either stops or passes by.
///
/// Waypoint layout:
///   startPoint                  — where the mother spawns / resets
///   stairMoveWaypoints[]        — movement points while ascending stairs (Y = stairYaw = -90)
///   stairCornerTurnWaypoint     — position where the mother turns the corner (Y snaps to cornerYaw = 0)
///   hallwayMoveWaypoints[]      — movement points through the hallway after the turn (Y = cornerYaw = 0)
///   doorMoveWaypoint            — position in front of the door (Y snaps to doorYaw = 90 on arrival)
///   passByPoint                 — where the mother walks to when passing by
/// </summary>
public class ParentApproachController : MonoBehaviour
{
    [Header("Start")]
    [Tooltip("Where the mother spawns and resets to.")]
    [SerializeField] private Transform startPoint;
    [Tooltip("The root GameObject of the mother model. Will be re-enabled on each approach start and reset.")]
    [SerializeField] private GameObject motherModelObject;

    [Header("Stair Phase")]
    [Tooltip("Movement waypoints while the mother ascends the stairs. Facing is locked to stairYaw throughout.")]
    [SerializeField] private Transform[] stairMoveWaypoints;
    [Tooltip("The corner waypoint where the mother turns after the stairs. Facing snaps to cornerYaw when she arrives here.")]
    [SerializeField] private Transform stairCornerTurnWaypoint;

    [Header("Hallway Phase")]
    [Tooltip("Movement waypoints through the hallway after the stair corner. Facing is locked to cornerYaw throughout.")]
    [SerializeField] private Transform[] hallwayMoveWaypoints;

    [Header("Door Phase")]
    [Tooltip("The position in front of the door the mother walks to. Facing snaps to doorYaw only after she arrives here.")]
    [SerializeField] private Transform doorMoveWaypoint;
    [Tooltip("Where the mother continues to when passing by (pass-by route only).")]
    [SerializeField] private Transform passByPoint;

    [Header("Movement Speed")]
    [Tooltip("Movement speed for the entire approach sequence.")]
    [SerializeField] private float moveSpeed = 2.0f;


    [Header("Approach Behavior")]
    [Range(0f, 1f)]
    [SerializeField] private float passByProbability = 0.35f;
    [SerializeField] private float pauseAtDoorSeconds = 2.0f;
    [SerializeField] private float pauseBeforePassBySeconds = 0.5f;

    [Header("Explicit Phase Yaw Angles")]
    [Tooltip("Y rotation while ascending the stairs.")]
    [SerializeField] private float stairYaw = -90f;
    [Tooltip("Y rotation after reaching the stair corner turn waypoint.")]
    [SerializeField] private float cornerYaw = 0f;
    [Tooltip("Y rotation applied only after the mother has fully arrived at doorMoveWaypoint.")]
    [SerializeField] private float doorYaw = 90f;

    [Header("Presentation")]
    [SerializeField] private GameObject firstFloorLight;
    [SerializeField] private AudioSource firstFloorLightAudio;
    [SerializeField] private AudioSource approachFootstepAudio;
    [SerializeField] private AudioSource arrivedAtDoorAudio;
    [SerializeField] private AudioSource passByAudio;

    [Header("Debug")]
    [SerializeField] private bool forcePassByForDebug = false;

    [Header("Events")]
    public UnityEvent OnApproachStarted;
    public UnityEvent OnReachedDoor;
    public UnityEvent OnStoppedAtDoor;
    public UnityEvent OnPassedByDoor;

    public bool IsApproaching    { get; private set; } = false;
    public bool ReachedDoor      { get; private set; } = false;
    public bool StoppedAtDoor    { get; private set; } = false;
    public bool PassedByDoor     { get; private set; } = false;

    /// <summary>
    /// True from the moment the stair corner turn completes until the approach ends.
    /// ParentDetectionV2 uses this to decide if suspicion should rise during a peek.
    /// </summary>
    public bool IsInHallwayPhase { get; private set; } = false;

    private Coroutine approachCoroutine;
    private float currentSpeed = 1f;
    private float _fixedPitch; // X — locked for entire run
    private float _fixedRoll;  // Z — locked for entire run
    private bool _forcePassByThisRun = false;
    private bool _forceDoorThisRun   = false;

    // Cached start transform — set once from startPoint in Awake so ResetApproach
    // always has a valid home position even across multiple runs.
    private Vector3 _cachedStartPosition;
    private Quaternion _cachedStartRotation;
    private bool _hasCachedStart = false;

    private void Awake()
    {
        if (startPoint != null)
        {
            _cachedStartPosition = startPoint.position;
            _cachedStartRotation = startPoint.rotation;
            _hasCachedStart = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────────────────────────────────

    public void StartApproach()
    {
        Debug.Log($"[ParentApproachController] StartApproach() called on object='{gameObject.name}'");

        if (IsApproaching)
        {
            Debug.Log("[ParentApproachController] StartApproach: BLOCKED - already approaching");
            return;
        }

        if (startPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] StartApproach: FAILED - startPoint is NULL.", this);
            return;
        }

        if (doorMoveWaypoint == null)
        {
            Debug.LogWarning("[ParentApproachController] StartApproach: FAILED - doorMoveWaypoint is NULL.", this);
            return;
        }

        int stairCount   = (stairMoveWaypoints   != null) ? stairMoveWaypoints.Length   : 0;
        int hallwayCount = (hallwayMoveWaypoints != null) ? hallwayMoveWaypoints.Length : 0;
        bool hasCorner   = stairCornerTurnWaypoint != null;
        Debug.Log($"[ParentApproachController] StartApproach: startPoint='{startPoint.name}' | stairMoveWaypoints={stairCount} | hasCorner={hasCorner} | hallwayMoveWaypoints={hallwayCount} | doorMoveWaypoint='{doorMoveWaypoint.name}' | moveSpeed={moveSpeed}");

        ResetStateFlags();

        // Re-enable the mother model in case it was disabled by a previous run.
        if (motherModelObject != null && !motherModelObject.activeSelf)
        {
            motherModelObject.SetActive(true);
            Debug.Log("[ParentApproachController] StartApproach: re-enabled motherModelObject");
        }

        Vector3 startEuler = transform.rotation.eulerAngles;
        _fixedPitch = startEuler.x;
        _fixedRoll  = startEuler.z;
        Debug.Log($"[ParentApproachController] Locked pitch={_fixedPitch:F2} roll={_fixedRoll:F2} | yaw phases: stair={stairYaw} corner={cornerYaw} door={doorYaw}");

        if (firstFloorLight != null) firstFloorLight.SetActive(true);
        if (firstFloorLightAudio != null) firstFloorLightAudio.Play();

        currentSpeed = moveSpeed;
        Debug.Log($"[ParentApproachController] Movement beginning | moveSpeed={moveSpeed:F2}");

        _forcePassByThisRun = false;
        _forceDoorThisRun   = false;
        approachCoroutine = StartCoroutine(ApproachRoutine());
        IsApproaching = true;
        OnApproachStarted?.Invoke();

        if (approachFootstepAudio != null)
        {
            approachFootstepAudio.loop = true;
            approachFootstepAudio.Play();
        }
    }

    /// <summary>
    /// Starts the approach and forces the pass-by outcome regardless of passByProbability.
    /// Use this for the N-key manual debug trigger — the mother will never stop at the door.
    /// </summary>
    public void StartApproachPassByOnly()
    {
        Debug.Log("[ParentApproachController] StartApproachPassByOnly() called - will force pass-by route");
        _forceDoorThisRun   = false;
        _forcePassByThisRun = true;
        StartApproach();
    }

    /// <summary>
    /// Starts the approach and forces the door-stop outcome regardless of passByProbability.
    /// Use this for the M-key manual debug trigger — the mother will always stop at the door.
    /// </summary>
    public void StartApproachDoorOnly()
    {
        Debug.Log("[ParentApproachController] StartApproachDoorOnly() called - will force door-stop route");
        _forcePassByThisRun = false;
        _forceDoorThisRun   = true;
        StartApproach();
    }

    public void ResetApproach()
    {
        Debug.Log($"[ParentApproachController] ResetApproach() called | IsApproaching={IsApproaching}");

        if (approachCoroutine != null)
        {
            StopCoroutine(approachCoroutine);
            approachCoroutine = null;
        }

        ResetStateFlags();

        if (approachFootstepAudio != null)
        {
            approachFootstepAudio.Stop();
            approachFootstepAudio.loop = false;
        }

        // Re-enable the mother model so the next run can see her.
        if (motherModelObject != null && !motherModelObject.activeSelf)
        {
            motherModelObject.SetActive(true);
            Debug.Log("[ParentApproachController] ResetApproach: re-enabled motherModelObject");
        }

        // Reposition to startPoint, falling back to cached position if startPoint is null.
        if (startPoint != null)
        {
            transform.position = startPoint.position;
            transform.rotation = startPoint.rotation;
            // Refresh cache while we're here.
            _cachedStartPosition = startPoint.position;
            _cachedStartRotation = startPoint.rotation;
            _hasCachedStart = true;
            Debug.Log($"[ParentApproachController] Reset: repositioned to startPoint='{startPoint.name}'");
        }
        else if (_hasCachedStart)
        {
            transform.position = _cachedStartPosition;
            transform.rotation = _cachedStartRotation;
            Debug.Log("[ParentApproachController] Reset: startPoint is NULL - used cached start position");
        }
        else
        {
            Debug.LogWarning("[ParentApproachController] Reset: startPoint is NULL and no cached start - cannot reposition");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Approach coroutine
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator ApproachRoutine()
    {
        Debug.Log("[ParentApproachController] ApproachRoutine: STARTED");

        // ── Phase 1: Stair ascent ─────────────────────────────────────────────
        // Snap to startPoint position and face stairYaw. Hold stairYaw for all
        // stairMoveWaypoints. Do NOT change yaw until stairCornerTurnWaypoint.
        transform.position = startPoint.position;
        SetFacingYaw(stairYaw);
        Debug.Log($"[ParentApproachController] Phase 1: STAIR ASCENT | yaw={stairYaw}");

        if (stairMoveWaypoints != null)
        {
            for (int i = 0; i < stairMoveWaypoints.Length; i++)
            {
                if (stairMoveWaypoints[i] == null) continue;
                Debug.Log($"[ParentApproachController]   Moving to stairMoveWaypoints[{i}]='{stairMoveWaypoints[i].name}' | yaw={stairYaw}");
                yield return MoveToPointWithYaw(stairMoveWaypoints[i], stairYaw);
                Debug.Log($"[ParentApproachController]   Reached stairMoveWaypoints[{i}]='{stairMoveWaypoints[i].name}'");
            }
        }

        // ── Phase 2: Corner turn ──────────────────────────────────────────────
        // Move to stairCornerTurnWaypoint still at stairYaw, then snap to cornerYaw
        // after arriving. IsInHallwayPhase becomes true here.
        if (stairCornerTurnWaypoint != null)
        {
            Debug.Log($"[ParentApproachController] Phase 2: CORNER TURN | moving to '{stairCornerTurnWaypoint.name}' still at yaw={stairYaw}");
            yield return MoveToPointWithYaw(stairCornerTurnWaypoint, stairYaw);
            SetFacingYaw(cornerYaw);
            IsInHallwayPhase = true;
            Debug.Log($"[ParentApproachController]   Arrived at corner — yaw snapped to {cornerYaw} | IsInHallwayPhase=true");
        }
        else
        {
            Debug.Log("[ParentApproachController] Phase 2: CORNER TURN skipped — stairCornerTurnWaypoint is NULL");
            IsInHallwayPhase = true;
        }

        // ── Phase 3: Hallway ──────────────────────────────────────────────────
        // Move through hallway waypoints while holding cornerYaw.
        if (hallwayMoveWaypoints != null)
        {
            for (int i = 0; i < hallwayMoveWaypoints.Length; i++)
            {
                if (hallwayMoveWaypoints[i] == null) continue;
                Debug.Log($"[ParentApproachController]   Moving to hallwayMoveWaypoints[{i}]='{hallwayMoveWaypoints[i].name}' | yaw={cornerYaw}");
                yield return MoveToPointWithYaw(hallwayMoveWaypoints[i], cornerYaw);
                Debug.Log($"[ParentApproachController]   Reached hallwayMoveWaypoints[{i}]='{hallwayMoveWaypoints[i].name}'");
            }
        }

        // ── Phase 4: Approach door ────────────────────────────────────────────
        // Move to doorMoveWaypoint while holding cornerYaw.
        // Only AFTER arriving snap to doorYaw.
        Debug.Log($"[ParentApproachController] Phase 4: APPROACH DOOR | moving to '{doorMoveWaypoint.name}' at yaw={cornerYaw}");
        yield return MoveToPointWithYaw(doorMoveWaypoint, cornerYaw);
        SetFacingYaw(doorYaw);
        Debug.Log($"[ParentApproachController]   Arrived at door — yaw snapped to {doorYaw} | rotation={transform.rotation.eulerAngles}");


        ReachedDoor = true;
        Debug.Log("[ParentApproachController] REACHED DOOR - firing OnReachedDoor");
        OnReachedDoor?.Invoke();

        if (arrivedAtDoorAudio != null)
        {
            arrivedAtDoorAudio.Play();
        }

        // ── Pass-by decision ──────────────────────────────────────────────────
        // _forceDoorThisRun overrides all other flags to guarantee a door stop.
        bool shouldPassBy = !_forceDoorThisRun && (_forcePassByThisRun || forcePassByForDebug || Random.value < passByProbability);
        Debug.Log($"[ParentApproachController] passByDecision: shouldPassBy={shouldPassBy} | _forcePassByThisRun={_forcePassByThisRun} | _forceDoorThisRun={_forceDoorThisRun} | forcePassByForDebug={forcePassByForDebug} | passByProbability={passByProbability} | passByPoint={(passByPoint != null ? passByPoint.name : "NULL")}");

        // ── Stop at door (normal mode) ────────────────────────────────────────
        if (!shouldPassBy || passByPoint == null)
        {
            Debug.Log($"[ParentApproachController] Pausing at door for {pauseAtDoorSeconds}s then firing OnStoppedAtDoor");
            yield return new WaitForSeconds(pauseAtDoorSeconds);

            StoppedAtDoor    = true;
            IsApproaching    = false;
            IsInHallwayPhase = false;

            if (approachFootstepAudio != null)
            {
                approachFootstepAudio.Stop();
                approachFootstepAudio.loop = false;
            }

            Debug.Log("[ParentApproachController] STOPPED AT DOOR - firing OnStoppedAtDoor");
            OnStoppedAtDoor?.Invoke();
            yield break;
        }

        // ── Pass by ───────────────────────────────────────────────────────────
        yield return new WaitForSeconds(pauseBeforePassBySeconds);

        if (passByAudio != null)
        {
            passByAudio.Play();
        }

        Debug.Log($"[ParentApproachController] Phase 5: PASS BY | moving to passByPoint='{passByPoint.name}'");
        yield return MoveToPointWithYaw(passByPoint, doorYaw);

        PassedByDoor     = true;
        IsApproaching    = false;
        IsInHallwayPhase = false;

        if (approachFootstepAudio != null)
        {
            approachFootstepAudio.Stop();
            approachFootstepAudio.loop = false;
        }

        Debug.Log("[ParentApproachController] PASSED BY DOOR - firing OnPassedByDoor");
        OnPassedByDoor?.Invoke();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Movement helpers
    // ──────────────────────────────────────────────────────────────────────────

    // Move toward target holding an explicit yaw the entire way.
    private IEnumerator MoveToPointWithYaw(Transform target, float yaw)
    {
        while (Vector3.Distance(transform.position, target.position) > 0.05f)
        {
            transform.position = Vector3.MoveTowards(transform.position, target.position, currentSpeed * Time.deltaTime);
            transform.rotation = Quaternion.Euler(_fixedPitch, yaw, _fixedRoll);
            yield return null;
        }

        transform.position = target.position;
        transform.rotation = Quaternion.Euler(_fixedPitch, yaw, _fixedRoll);
    }

    // Snap Y to an explicit yaw, preserving locked X and Z.
    private void SetFacingYaw(float yaw)
    {
        transform.rotation = Quaternion.Euler(_fixedPitch, yaw, _fixedRoll);
    }

    private void ResetStateFlags()
    {
        IsApproaching    = false;
        ReachedDoor      = false;
        StoppedAtDoor    = false;
        PassedByDoor     = false;
        IsInHallwayPhase = false;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform previous = startPoint;

        // Start point
        if (startPoint != null)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawSphere(startPoint.position, 0.08f);
        }

        // Stair move waypoints (cyan)
        Gizmos.color = Color.cyan;
        if (stairMoveWaypoints != null)
        {
            for (int i = 0; i < stairMoveWaypoints.Length; i++)
            {
                if (stairMoveWaypoints[i] == null) continue;
                Gizmos.DrawSphere(stairMoveWaypoints[i].position, 0.06f);
                if (previous != null) Gizmos.DrawLine(previous.position, stairMoveWaypoints[i].position);
                previous = stairMoveWaypoints[i];
            }
        }

        // Corner turn waypoint (green)
        if (stairCornerTurnWaypoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(stairCornerTurnWaypoint.position, 0.08f);
            if (previous != null) Gizmos.DrawLine(previous.position, stairCornerTurnWaypoint.position);
            previous = stairCornerTurnWaypoint;
        }

        // Hallway move waypoints (orange)
        if (hallwayMoveWaypoints != null)
        {
            Gizmos.color = new Color(1f, 0.5f, 0f); // orange
            for (int i = 0; i < hallwayMoveWaypoints.Length; i++)
            {
                if (hallwayMoveWaypoints[i] == null) continue;
                Gizmos.DrawSphere(hallwayMoveWaypoints[i].position, 0.06f);
                if (previous != null) Gizmos.DrawLine(previous.position, hallwayMoveWaypoints[i].position);
                previous = hallwayMoveWaypoints[i];
            }
        }

        // Door move waypoint (yellow)
        if (doorMoveWaypoint != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(doorMoveWaypoint.position, 0.09f);
            if (previous != null) Gizmos.DrawLine(previous.position, doorMoveWaypoint.position);
            previous = doorMoveWaypoint;
        }

        // Pass-by point (magenta)
        if (passByPoint != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawSphere(passByPoint.position, 0.08f);
            if (previous != null) Gizmos.DrawLine(previous.position, passByPoint.position);
        }
    }
#endif
}