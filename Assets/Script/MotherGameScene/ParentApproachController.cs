using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// ParentApproachController：
/// インスペクターで設定したウェイポイントに沿って、2つの明示的なルートで親機を移動させる。
///   通過：startPoint → stairClimbPoints[] → stairTurnPoint → hallwayPoints[] → doorPoint → passByPoint
///   ドアのみ：startPoint → stairClimbPoints[] → stairTurnPoint → hallwayPoints[] → doorPoint（停止）
///
/// 回転規則（Y固定、X/Z固定）：
///   階段上り：Y = -90（フェーズ開始時に即時設定）
///   階段旋回：Y = 0（stairTurnPointで滑らかに回転）
///   ドア到着：Y = 90（doorPointで滑らかに回転）
///
/// 移動ループ音：
///   UpdateMovementLoopAudio()で毎フレーム管理する。
///   IsApproaching=true、IsRushIn=false、CameraSwitcher.IsPeeking=trueのときだけ再生する。
///   条件を満たさなくなったとき、またResetStateFlags()時に即座に停止する。
///
/// 突入モード（IsRushIn=true）：
///   大きな音による突入でStartApproachDoorOnly()を呼ぶ前にParentWarningSystemが設定する。
///   移動ループ音を抑制し、pauseAtDoorSecondsの代わりにrushInPauseAtDoorSecondsを使用する。
///   ResetStateFlags()で自動的に解除される。
/// </summary>
public class ParentApproachController : MonoBehaviour
{
    // ── ウェイポイント ────────────────────────────────────────────────────────
    [Header("ウェイポイント")]
    [Tooltip("親機が出現し、リセット時に戻る場所。")]
    public Transform startPoint;

    [Tooltip("階段を上るウェイポイント。親機は終始Y=-90を向く。")]
    public Transform[] stairClimbPoints;

    [Tooltip("階段上りを終え、親機が廊下方向（Y=0）へ回転する1つの地点。")]
    public Transform stairTurnPoint;

    [Tooltip("階段旋回後に廊下を移動するためのウェイポイント。")]
    public Transform[] hallwayPoints;

    [Tooltip("ドア前の位置。到着時に親機がY=90へ回転する。")]
    public Transform doorPoint;

    [Tooltip("ドア通過後に親機が歩く場所（通過ルートのみ）。")]
    public Transform passByPoint;

    // ── 移動 ──────────────────────────────────────────────────────────────────
    [Header("移動")]
    [Tooltip("基本移動速度（単位／秒）。")]
    public float moveSpeed = 2f;

    [Tooltip("到着と判定するウェイポイントまでの距離（単位）。")]
    public float stopDistance = 0.05f;

    // ── 回転速度 ──────────────────────────────────────────────────────────────
    [Header("回転速度")]
    [Tooltip("階段の角で旋回するときの速度（度／秒）。")]
    public float stairTurnRotationSpeed = 90f;

    [Tooltip("到着時にドアへ向く回転速度（度／秒）。")]
    public float doorTurnRotationSpeed = 120f;

    // ── 表示 ──────────────────────────────────────────────────────────────────
    [Header("親機モデルの表示")]
    [Tooltip("歩く親機モデルのルートGameObject。接近開始時にSetActive(true)にする。ここではSetActive(false)を呼ばず、PDV2がrealMotherObject経由で非表示を管理する。")]
    public GameObject motherModelRoot;
    [Tooltip("任意：motherModelRootだけでは不十分な場合に有効／無効にする子Renderer（例：LODの子）。")]
    public Renderer[] motherModelRenderers;

    // ── オーディオ ─────────────────────────────────────────────────────────────
    [Header("オーディオ")]
    [Tooltip("接近中にループ再生するAudioSource。UpdateMovementLoopAudio()で毎フレーム覗き見状態により制御する。突入ルートでは再生しない。")]
    public AudioSource movementLoopAudioSource;
    [Tooltip("移動ループ音の制御に使うCameraSwitcher。未設定時はStartで自動検索する。")]
    public CameraSwitcher cameraSwitcher;

    // ── タイミング ────────────────────────────────────────────────────────────
    [Header("タイミング")]
    [Tooltip("通常ルートで、OnStoppedAtDoorイベント前にドアで停止する秒数。")]
    public float pauseAtDoorSeconds = 2f;

    [Tooltip("大きな音による突入ルートで、OnStoppedAtDoorイベント前にドアで停止する秒数。")]
    public float rushInPauseAtDoorSeconds = 0.2f;

    [Tooltip("passByPointへ進む前にドアで停止する秒数。")]
    public float pauseBeforePassBySeconds = 0.5f;

    // ── イベント ──────────────────────────────────────────────────────────────
    [Header("イベント")]
    public UnityEvent OnApproachStarted;
    public UnityEvent OnReachedDoor;
    public UnityEvent OnStoppedAtDoor;
    public UnityEvent OnPassedByDoor;

    // ── 公開読み取り専用状態 ──────────────────────────────────────────────────
    public bool IsApproaching    { get; private set; }
    public bool ReachedDoor      { get; private set; }
    public bool StoppedAtDoor    { get; private set; }
    public bool PassedByDoor     { get; private set; }
    public bool IsInHallwayPhase { get; private set; }

    // ── 実行モード ────────────────────────────────────────────────────────────
    /// <summary>大きな音による突入開始前にParentWarningSystemが設定する。移動ループ音を抑制し、rushInPauseAtDoorSecondsを使用する。</summary>
    public bool IsRushIn { get; set; }

    // ── 非公開 ───────────────────────────────────────────────────────────────
    private Coroutine _approachCoroutine;
    private float _fixedPitch;
    private float _fixedRoll;

    // 固定ヨー角 — 外部公開せず、要件に応じて調整する
    private const float StairYaw   = -90f;
    private const float HallwayYaw =   0f;
    private const float DoorYaw    =  90f;

    // ──────────────────────────────────────────────────────────────────────────
    //  Unityのライフサイクル
    // ──────────────────────────────────────────────────────────────────────────

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
        // ルール：接近中かつプレイヤーが覗き見している通常ルート（突入以外）のみ再生する。
        bool shouldPlay = !IsRushIn && IsApproaching && cameraSwitcher != null && cameraSwitcher.IsPeeking;
        if (shouldPlay && !movementLoopAudioSource.isPlaying)
        {
            movementLoopAudioSource.loop = true;
            movementLoopAudioSource.Play();
            Debug.Log("[ParentApproachController] 移動ループを開始（覗き見中）");
        }
        else if (!shouldPlay && movementLoopAudioSource.isPlaying)
        {
            movementLoopAudioSource.Stop();
            Debug.Log("[ParentApproachController] 移動ループを停止（覗き見中でない、または突入中）");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  公開API
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>既定の入口 — StartApproachDoorOnly()へ委譲する。既存シーンの接続との互換性のため残す。</summary>
    public void StartApproach()
    {
        StartApproachDoorOnly();
    }

    /// <summary>通過ルートを開始する：親機が廊下を通り、ドアを過ぎてpassByPointまで歩く。</summary>
    public void StartApproachPassByOnly()
    {
        if (IsApproaching)
        {
            Debug.Log("[ParentApproachController] すでに接近中 — StartApproachPassByOnlyを無視");
            return;
        }
        if (!ValidateWaypoints(requirePassBy: true)) return;

        BeginApproach(passByRoute: true);
    }

    /// <summary>ドア停止ルートを開始する：親機がdoorPointまで歩き、部屋の方向を向いて停止する。突入ルートでも使用する。</summary>
    public void StartApproachDoorOnly()
    {
        if (IsApproaching)
        {
            Debug.Log("[ParentApproachController] すでに接近中 — StartApproachDoorOnlyを無視");
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
    //  内部開始ヘルパー
    // ──────────────────────────────────────────────────────────────────────────

    private void BeginApproach(bool passByRoute)
    {
        ResetStateFlags();

        // startPoint.rotationからピッチ／ロールを取得し、キャンセルされたサイクル後に
        // 実行途中の古いTransformが誤った値を引き継がないようにする。
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
    //  コルーチン
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator DoorRoutine()
    {
        Debug.Log("[ParentApproachController] DoorRoutine：開始");

        yield return RunStairPhase();
        yield return RunHallwayPhase();

        Debug.Log($"[ParentApproachController] Phase: DOOR | moving to '{doorPoint.name}' then rotate to yaw=90");
        yield return MoveToPoint(doorPoint);
        yield return RotateToYaw(DoorYaw, doorTurnRotationSpeed);

        ReachedDoor = true;
        Debug.Log("[ParentApproachController] ドアに到着 — OnReachedDoorを発生");
        OnReachedDoor?.Invoke();

        float doorPause = IsRushIn ? rushInPauseAtDoorSeconds : pauseAtDoorSeconds;
        Debug.Log($"[ParentApproachController] Door pause: {doorPause:F2}s (IsRushIn={IsRushIn})");
        yield return new WaitForSeconds(doorPause);

        StopMovementAudio();
        StoppedAtDoor = true;
        IsApproaching = false;
        // IsInHallwayPhaseは意図的にここでは解除しない。
        // PDV2.HallwayPeekSuspicionCoroutineが、親機がドアに立っている間に覗き見加算を行うか判定するために確認する。
        // 完全なサイクル終了後、ResetApproach経由のResetStateFlags()で解除する。

        Debug.Log("[ParentApproachController] ドアで停止 — OnStoppedAtDoorを発生");
        OnStoppedAtDoor?.Invoke();
    }

    private IEnumerator PassByRoutine()
    {
        Debug.Log("[ParentApproachController] PassByRoutine：開始");

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
        // IsInHallwayPhaseはResetStateFlags()でのみ解除する — DoorRoutineと同じ動作。

        Debug.Log("[ParentApproachController] ドアを通過 — OnPassedByDoorを発生");
        OnPassedByDoor?.Invoke();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  共通フェーズヘルパー
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator RunStairPhase()
    {
        SetYaw(StairYaw);
        Debug.Log($"[ParentApproachController] Phase: STAIR CLIMB | yaw=-90 | IsRushIn={IsRushIn}");
        // 移動ループ音はUpdate()内のUpdateMovementLoopAudio()で管理する — ここではPlay()を呼ばない。

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
            Debug.Log("[ParentApproachController]   階段旋回完了");
        }
    }

    private IEnumerator RunHallwayPhase()
    {
        IsInHallwayPhase = true;
        Debug.Log("[ParentApproachController] フェーズ：廊下 | IsInHallwayPhase=true");

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
    //  移動／回転ヘルパー
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
            Debug.Log("[ParentApproachController] 移動音を停止");
        }
    }

    private void ShowMotherModel()
    {
        if (motherModelRoot != null)
        {
            motherModelRoot.SetActive(true);
            Debug.Log($"[ParentApproachController] 親機モデルの表示を復元 | object='{motherModelRoot.name}'");
        }

        if (motherModelRenderers != null)
        {
            foreach (var r in motherModelRenderers)
            {
                if (r == null) continue;
                r.enabled = true;
                Debug.Log($"[ParentApproachController] 親機モデルの表示を復元 | renderer='{r.name}'");
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
            Debug.LogWarning("[ParentApproachController] startPointがNULLです。", this);
            return false;
        }
        if (doorPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] doorPointがNULLです。", this);
            return false;
        }
        if (requirePassBy && passByPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] passByPointがNULLです — 通過ルートに必要です。", this);
            return false;
        }
        if (stairTurnPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] stairTurnPointがNULLです — 階段から廊下への旋回をスキップします。", this);
        }
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  シーンギズモ
    // ──────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform prev = startPoint;

        // startPoint — 白
        if (startPoint != null)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawSphere(startPoint.position, 0.08f);
        }

        // stairClimbPoints — シアン
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

        // stairTurnPoint — 青
        if (stairTurnPoint != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(stairTurnPoint.position, 0.09f);
            if (prev != null) Gizmos.DrawLine(prev.position, stairTurnPoint.position);
            prev = stairTurnPoint;
        }

        // hallwayPoints — 緑
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

        // doorPoint — 黄
        if (doorPoint != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(doorPoint.position, 0.09f);
            if (prev != null) Gizmos.DrawLine(prev.position, doorPoint.position);
            prev = doorPoint;
        }

        // passByPoint — マゼンタ
        if (passByPoint != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawSphere(passByPoint.position, 0.08f);
            if (prev != null) Gizmos.DrawLine(prev.position, passByPoint.position);
        }
    }
#endif
}