using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

/// <summary>
/// ParentApproachController：
/// インスペクターで設定したウェイポイントに沿って、2つの明示的なルートで親機を移動させる。
///   ドア確認（通常）：startPoint → hallwayPoint1 → hallwayPoint2 → turnPoint → hallwayPoint3 → doorPoint（停止）
///             →（PDV2が入室を要求した場合のみ）roomEntryPoints[]へ入室 → doorPointへ復帰（退室）
///   フェイント：startPoint → hallwayPoint1 → hallwayPoint2 → turnPoint → hallwayPoint2 → hallwayPoint1 → startPoint（帰還）
///
/// 部屋入室（任意）：
///   PDV2がOnStoppedAtDoorの処理中にRequestRoomEntry()を呼んだときのみ、ドア停止後にroomEntryPoints[]へ進む。
///   入室完了でonEnteredRoom、退室完了（doorPoint復帰）でonExitedRoomを発生する。
///   roomEntryPointsが未設定／空、突入（IsRushIn）サイクル、ドア停止ルート以外では入室せず、
///   従来どおりdoorPointで停止したままコルーチンを終了する。
///
/// 回転規則（X/Z固定、YはウェイポイントのTransform.rotationから取得）：
///   開始：startPoint.rotationで初期化
///   方向転換：turnPoint.rotationのY角へ滑らかに回転
///   ドア到着：doorPoint.rotationのY角へ滑らかに回転
///
/// 移動ループ音：
///   UpdateMovementLoopAudio()で毎フレーム管理する。
///   IsApproaching=true、IsRushIn=falseのときに再生する。
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

    [Tooltip("廊下の1つ目のウェイポイント。未設定ならスキップして次へ進む。")]
    public Transform hallwayPoint1;

    [Tooltip("廊下の2つ目のウェイポイント。未設定ならスキップして次へ進む。")]
    public Transform hallwayPoint2;

    [Tooltip("方向転換地点。到着後、このTransform.rotationのY角へ滑らかに回転する。未設定なら回転をスキップする。")]
    public Transform turnPoint;

    [Tooltip("方向転換後の廊下のウェイポイント（ドア確認ルートのみ使用）。未設定ならスキップする。")]
    public Transform hallwayPoint3;

    [Tooltip("ドア前の位置。到着時にこのTransform.rotationのY角へ回転する。")]
    public Transform doorPoint;

    // ── 部屋内部（入室） ──────────────────────────────────────────────────────
    [Header("部屋内部（入室）")]
    [Tooltip("部屋内部の立ち位置。doorPointから近い順に設定する。未設定または空の場合は入室せず、従来どおりdoorPointで停止する。")]
    public Transform[] roomEntryPoints;

    [Tooltip("部屋から出るとき（doorPointへ戻るとき）に親機が向くY角度（度）。入室時はdoorPointでの向き（Y=90）を維持する。")]
    [SerializeField] private float roomExitYaw = -90f;

    [Tooltip("部屋内に留まれる最大秒数。この時間を超えると、退室要求がなくても自動的に退室する。0以下で無効（無制限）。")]
    [SerializeField] private float roomStayTimeoutSeconds = 20f;

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
    [Tooltip("接近中にループ再生するAudioSource。UpdateMovementLoopAudio()で毎フレーム制御する。突入ルートでは再生しない。")]
    public AudioSource movementLoopAudioSource;

    [Header("足音音量（段階的制御）")]
    [Tooltip("開始地点付近（遠い段階）での足音音量。")]
    [Range(0f, 1f)]
    [SerializeField] private float farVolume = 0.2f;

    [Tooltip("階段・廊下（中間段階）での足音音量。")]
    [Range(0f, 1f)]
    [SerializeField] private float midVolume = 0.5f;

    [Tooltip("扉前（扉に近い段階）での足音音量。")]
    [Range(0f, 1f)]
    [SerializeField] private float nearDoorVolume = 1.0f;

    [Tooltip("現在の音量から目標音量へ変化する速さ（単位／秒）。")]
    [SerializeField] private float volumeChangeSpeed = 1.0f;

    // ── タイミング ────────────────────────────────────────────────────────────
    [Header("タイミング")]
    [Tooltip("通常ルートで、OnStoppedAtDoorイベント前にドアで停止する秒数。")]
    public float pauseAtDoorSeconds = 2f;

    [Tooltip("大きな音による突入ルートで、OnStoppedAtDoorイベント前にドアで停止する秒数。")]
    public float rushInPauseAtDoorSeconds = 0.2f;

    // ── イベント ──────────────────────────────────────────────────────────────
    [Header("イベント")]
    [FormerlySerializedAs("OnApproachStarted")]
    public UnityEvent onApproachStarted;
    [FormerlySerializedAs("OnReachedDoor")]
    public UnityEvent onReachedDoor;
    [FormerlySerializedAs("OnStoppedAtDoor")]
    public UnityEvent onStoppedAtDoor;
    [FormerlySerializedAs("OnPassedByDoor")]
    public UnityEvent onPassedByDoor;
    [Tooltip("親機が部屋内部への入室を完了したときに発生する（入室が受理されたサイクルのみ）。")]
    public UnityEvent onEnteredRoom;
    [Tooltip("親機が部屋内部からの退室を完了し、doorPointへ戻ったときに発生する。")]
    public UnityEvent onExitedRoom;

    // ── 公開読み取り専用状態 ──────────────────────────────────────────────────
    private bool IsApproaching { get; set; }
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
    private float _currentAudioVolume;
    private float _targetAudioVolume;

    // 部屋入室（案B）の状態
    private bool _cycleStartedAsRushIn;   // このサイクルが突入（大きな音）として開始されたか — BeginApproach()で捕捉する
    private bool _doorRoutineActive;      // DoorRoutine()が実行中か（入室要求の受付条件）
    private bool _roomEntryRequested;     // OnStoppedAtDoor中にPDV2から入室要求を受けたか
    private bool _roomPhaseActive;        // 入室フェーズ（部屋内部への移動〜doorPoint復帰）が進行中か
    private bool _leaveRoomRequested;     // 部屋内部からの退室要求を受けたか

    // ──────────────────────────────────────────────────────────────────────────
    //  Unityライフサイクル
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        _currentAudioVolume = farVolume;
        _targetAudioVolume = farVolume;
        if (movementLoopAudioSource != null)
        {
            movementLoopAudioSource.volume = farVolume;
        }
    }

    private void Update()
    {
        UpdateMovementLoopAudio();
    }

    private void UpdateMovementLoopAudio()
    {
        if (movementLoopAudioSource == null) return;
        // 覗き機能削除に伴い、通常ルートの接近中は常に移動ループを再生する。
        bool shouldPlay = !IsRushIn && IsApproaching;
        if (shouldPlay)
        {
            if (!movementLoopAudioSource.isPlaying)
            {
                movementLoopAudioSource.loop = true;
                movementLoopAudioSource.volume = _currentAudioVolume;
                movementLoopAudioSource.Play();
                Debug.Log("[ParentApproachController] 移動ループを開始（接近中）");
            }

            // 現在の音量から目標音量へ滑らかに変化させる
            _currentAudioVolume = Mathf.MoveTowards(_currentAudioVolume, _targetAudioVolume, volumeChangeSpeed * Time.deltaTime);
            movementLoopAudioSource.volume = _currentAudioVolume;
        }
        else if (movementLoopAudioSource.isPlaying)
        {
            movementLoopAudioSource.Stop();
            _targetAudioVolume = farVolume;
            _currentAudioVolume = farVolume;
            movementLoopAudioSource.volume = farVolume;
            Debug.Log("[ParentApproachController] 移動ループを停止（接近終了、または突入中）");
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

    /// <summary>フェイントルートを開始する：turnPointで方向転換し、通ってきた廊下を戻ってstartPointへ帰還する。</summary>
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

    /// <summary>
    /// 親機を部屋内部へ入室させる要求。OnStoppedAtDoorの処理中（＝ドア停止ルート実行中）にPDV2から呼ばれる。
    /// 受理した場合は、ドア停止後にroomEntryPoints[]へ移動し、入室完了でonEnteredRoomを発生する。
    /// 次のいずれかに該当する場合はfalseを返し、呼び出し側は従来どおりの即時処理を行う：
    ///   ドア停止ルートが実行中ではない／突入（IsRushIn）サイクルである／roomEntryPointsが未設定または空である。
    /// </summary>
    public bool RequestRoomEntry()
    {
        if (!_doorRoutineActive)
        {
            Debug.Log("[ParentApproachController] RequestRoomEntry 却下：ドア停止ルートが実行中ではない");
            return false;
        }

        if (_cycleStartedAsRushIn)
        {
            Debug.Log("[ParentApproachController] RequestRoomEntry 却下：突入サイクルのため入室しない");
            return false;
        }

        if (roomEntryPoints == null || roomEntryPoints.Length == 0)
        {
            // 未設定時はログを出さず、従来どおりdoorPointで停止する挙動へフォールバックする。
            return false;
        }

        if (doorPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] RequestRoomEntry 却下：doorPointがNULLです。", this);
            return false;
        }

        _roomEntryRequested = true;
        Debug.Log($"[ParentApproachController] RequestRoomEntry 受理 | roomEntryPoints={roomEntryPoints.Length}");
        return true;
    }

    /// <summary>
    /// 親機を部屋内部から退室させる要求。入室フェーズが進行中の場合のみ受理する。
    /// 親機は入室時と逆順でdoorPointへ戻り、退室完了でonExitedRoomを発生する。
    /// </summary>
    public bool RequestLeaveRoom()
    {
        if (!_roomPhaseActive)
        {
            Debug.Log("[ParentApproachController] RequestLeaveRoom 却下：入室フェーズが進行中ではない");
            return false;
        }

        _leaveRoomRequested = true;
        Debug.Log("[ParentApproachController] RequestLeaveRoom 受理 — 部屋から退室する");
        return true;
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
        // 「このサイクルは突入（大きな音）として開始されたか」を記録する。
        // ParentWarningSystemはIsRushIn=trueを設定してから本メソッドを呼ぶため、
        // ResetStateFlags()でIsRushInが消える前にここで捕捉する（入室可否の判定に使用する）。
        _cycleStartedAsRushIn = IsRushIn;

        ResetStateFlags();

        // 接近開始時は遠い段階の音量から初期化
        _targetAudioVolume = farVolume;
        _currentAudioVolume = farVolume;
        if (movementLoopAudioSource != null)
        {
            movementLoopAudioSource.volume = farVolume;
        }

        // startPoint.rotationからピッチ／ロールを取得し、キャンセルされたサイクル後に
        // 実行途中の古いTransformが誤った値を引き継がないようにする。
        Vector3 startEuler = startPoint.rotation.eulerAngles;
        _fixedPitch = startEuler.x;
        _fixedRoll  = startEuler.z;

        transform.position = startPoint.position;
        transform.rotation = startPoint.rotation;

        ShowMotherModel();

        IsApproaching = true;
        onApproachStarted?.Invoke();

        Debug.Log($"[ParentApproachController] BeginApproach | passByRoute={passByRoute} | pitch={_fixedPitch:F1} roll={_fixedRoll:F1} | targetVolume={farVolume}");
        _approachCoroutine = StartCoroutine(passByRoute ? PassByRoutine() : DoorRoutine());
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  コルーチン
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator DoorRoutine()
    {
        _doorRoutineActive = true;
        Debug.Log("[ParentApproachController] DoorRoutine：開始");

        yield return MoveToTurnPoint();

        yield return MoveToPoint(hallwayPoint3);

        // 扉前フェーズ：目標音量を扉前（最大段階）に設定
        _targetAudioVolume = nearDoorVolume;
        Debug.Log($"[ParentApproachController] Phase: DOOR | moving to '{doorPoint.name}' then rotate to doorPoint's yaw | targetVolume={nearDoorVolume}");
        yield return MoveToPoint(doorPoint);
        yield return RotateToTransformYaw(doorPoint, doorTurnRotationSpeed);

        ReachedDoor = true;
        Debug.Log("[ParentApproachController] ドアに到着 — OnReachedDoorを発生");
        onReachedDoor?.Invoke();

        float doorPause = IsRushIn ? rushInPauseAtDoorSeconds : pauseAtDoorSeconds;
        Debug.Log($"[ParentApproachController] Door pause: {doorPause:F2}s (IsRushIn={IsRushIn})");
        yield return new WaitForSeconds(doorPause);

        StopMovementAudio();
        StoppedAtDoor = true;
        IsApproaching = false;
        // IsInHallwayPhaseは意図的にここでは解除しない。
        // 完全なサイクル終了後、ResetApproach経由のResetStateFlags()で解除する。

        Debug.Log("[ParentApproachController] ドアで停止 — OnStoppedAtDoorを発生");
        onStoppedAtDoor?.Invoke();

        // OnStoppedAtDoorの処理中にPDV2が入室を要求した場合のみ、部屋内部へ移動する。
        // 要求がない場合は従来どおりドア前で停止したままコルーチンを終了する。
        if (_roomEntryRequested)
        {
            _roomEntryRequested = false;
            yield return RoomPhaseCoroutine();
        }

        _doorRoutineActive = false;
    }

    private IEnumerator PassByRoutine()
    {
        Debug.Log("[ParentApproachController] PassByRoutine（フェイント）：開始");

        yield return MoveToTurnPoint();

        // TurnPointで方向転換したあとは、通ってきた廊下を逆順に戻ってstartPointへ帰還する。
        yield return MoveToPoint(hallwayPoint2);
        yield return MoveToPoint(hallwayPoint1);
        yield return MoveToPoint(startPoint);

        StopMovementAudio();
        PassedByDoor  = true;
        IsApproaching = false;
        // IsInHallwayPhaseはResetStateFlags()でのみ解除する — DoorRoutineと同じ動作。

        Debug.Log("[ParentApproachController] フェイント完了 — OnPassedByDoorを発生");
        onPassedByDoor?.Invoke();
    }

    /// <summary>
    /// roomEntryPointsのうち有効（null以外）な要素の数を返す。
    /// </summary>
    private int CountValidRoomEntryPoints()
    {
        if (roomEntryPoints == null) return 0;

        int count = 0;
        for (int i = 0; i < roomEntryPoints.Length; i++)
        {
            if (roomEntryPoints[i] != null) count++;
        }
        return count;
    }

    /// <summary>
    /// ドア停止後、親機を部屋内部へ移動させ、退室要求（または安全タイムアウト）まで部屋に留まらせる。
    /// 移動と回転は既存のMoveToPoint()／RotateToYaw()を再利用する。
    /// 入室完了でonEnteredRoom、doorPointへ戻った時点でonExitedRoomを発生する。
    /// </summary>
    private IEnumerator RoomPhaseCoroutine()
    {
        _roomPhaseActive = true;

        int entryCount = CountValidRoomEntryPoints();
        Debug.Log($"[ParentApproachController] RoomPhase：入室開始 | 有効なroomEntryPoints={entryCount} | yaw={transform.rotation.eulerAngles.y:F1}");

        if (entryCount > 0)
        {
            // 部屋内部へ入る（doorPointから近い順に設定されたウェイポイントを順に進む）。
            // 向きはdoorPoint到着時のまま（doorPoint.rotation）を維持する。
            for (int i = 0; i < roomEntryPoints.Length; i++)
            {
                if (roomEntryPoints[i] == null) continue;
                Debug.Log($"[ParentApproachController]   roomEntry[{i}] '{roomEntryPoints[i].name}'");
                yield return MoveToPoint(roomEntryPoints[i]);
            }
        }
        else
        {
            // 有効なウェイポイントがない場合はその場（doorPoint）を部屋内部とみなす。
            // onEnteredRoomは必ず発生させ、呼び出し側が待ち続けないようにする。
            Debug.LogWarning("[ParentApproachController] RoomPhase：有効なroomEntryPointsがないため、doorPointで入室完了とする。", this);
        }

        Debug.Log("[ParentApproachController] 入室完了 — OnEnteredRoomを発生");
        onEnteredRoom?.Invoke();

        // 退室要求（RequestLeaveRoom）または安全タイムアウトまで部屋に留まる。
        float timeout = Mathf.Max(0f, roomStayTimeoutSeconds);
        float elapsed = 0f;
        while (!_leaveRoomRequested)
        {
            if (timeout > 0f && elapsed >= timeout)
            {
                Debug.Log($"[ParentApproachController] 部屋滞在が安全タイムアウト（{timeout:F1}s）に達した — 自動的に退室する");
                break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }
        _leaveRoomRequested = false;

        // 部屋から出る：退室の向きへ回転してから、入室時と逆順でdoorPointへ戻る。
        Debug.Log($"[ParentApproachController] RoomPhase：退室開始 | yaw={roomExitYaw:F1}");
        yield return RotateToYaw(roomExitYaw, doorTurnRotationSpeed);

        for (int i = roomEntryPoints.Length - 1; i >= 0; i--)
        {
            if (roomEntryPoints[i] == null) continue;
            Debug.Log($"[ParentApproachController]   roomExit[{i}] '{roomEntryPoints[i].name}'");
            yield return MoveToPoint(roomEntryPoints[i]);
        }

        yield return MoveToPoint(doorPoint);

        _roomPhaseActive = false;
        Debug.Log("[ParentApproachController] 退室完了 — OnExitedRoomを発生");
        onExitedRoom?.Invoke();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  共通フェーズヘルパー
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator MoveToTurnPoint()
    {
        IsInHallwayPhase = true;
        // 中間段階（廊下）：turnPointで方向転換するまでは中間音量を維持する
        _targetAudioVolume = midVolume;
        Debug.Log($"[ParentApproachController] フェーズ：廊下 | IsInHallwayPhase=true | targetVolume={midVolume}");
        // 移動ループ音はUpdate()内のUpdateMovementLoopAudio()で管理する — ここではPlay()を呼ばない。

        // 中間ウェイポイントは未設定ならスキップして、可能な限り先へ進む（null安全）。
        yield return MoveToPoint(hallwayPoint1);
        yield return MoveToPoint(hallwayPoint2);

        if (turnPoint != null)
        {
            Debug.Log($"[ParentApproachController]   turnPoint '{turnPoint.name}' — yaw={turnPoint.rotation.eulerAngles.y:F1}へ旋回");
            yield return MoveToPoint(turnPoint);
            yield return RotateToTransformYaw(turnPoint, stairTurnRotationSpeed);
            Debug.Log("[ParentApproachController]   方向転換完了");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  移動／回転ヘルパー
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator MoveToPoint(Transform target)
    {
        if (target == null)
        {
            // 中間ウェイポイントが未設定の場合はスキップして続行する（null安全）。
            yield break;
        }

        while (Vector3.Distance(transform.position, target.position) > stopDistance)
        {
            transform.position = Vector3.MoveTowards(
                transform.position, target.position, moveSpeed * Time.deltaTime);
            yield return null;
        }
        transform.position = target.position;
    }

    private IEnumerator RotateToTransformYaw(Transform target, float speed)
    {
        if (target == null) yield break;

        // ウェイポイントのTransform.rotationから目標Y角を取得する（コードへの角度埋め込みなし）。
        float targetYaw = target.rotation.eulerAngles.y;
        yield return RotateToYaw(targetYaw, speed);
    }

    private IEnumerator RotateToYaw(float targetYaw, float speed)
    {
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
        if (movementLoopAudioSource != null)
        {
            if (movementLoopAudioSource.isPlaying)
            {
                movementLoopAudioSource.Stop();
                Debug.Log("[ParentApproachController] 移動音を停止");
            }
            movementLoopAudioSource.volume = farVolume;
        }
        _targetAudioVolume = farVolume;
        _currentAudioVolume = farVolume;
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

        // 部屋入室（案B）の状態を初期化する。_cycleStartedAsRushInはBeginApproach()で
        // ResetStateFlags()より前に設定されるため、ここではクリアしない。
        _doorRoutineActive  = false;
        _roomEntryRequested = false;
        _roomPhaseActive    = false;
        _leaveRoomRequested = false;

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
        // 中間ウェイポイント（hallwayPoint1〜3 / turnPoint）は未設定でも続行する。
        // フェイントルートも検証内容は同じ（互換性のためrequirePassBy引数は残す）。
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

        // hallwayPoint1 / hallwayPoint2 — 緑
        Gizmos.color = Color.green;
        if (hallwayPoint1 != null)
        {
            Gizmos.DrawSphere(hallwayPoint1.position, 0.06f);
            if (prev != null) Gizmos.DrawLine(prev.position, hallwayPoint1.position);
            prev = hallwayPoint1;
        }
        if (hallwayPoint2 != null)
        {
            Gizmos.DrawSphere(hallwayPoint2.position, 0.06f);
            if (prev != null) Gizmos.DrawLine(prev.position, hallwayPoint2.position);
            prev = hallwayPoint2;
        }

        // turnPoint — 青（方向転換）
        if (turnPoint != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(turnPoint.position, 0.09f);
            if (prev != null) Gizmos.DrawLine(prev.position, turnPoint.position);
            prev = turnPoint;
        }

        // hallwayPoint3 — 緑（ドア確認ルートのみ）
        if (hallwayPoint3 != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(hallwayPoint3.position, 0.06f);
            if (prev != null) Gizmos.DrawLine(prev.position, hallwayPoint3.position);
            prev = hallwayPoint3;
        }

        // doorPoint — 黄
        if (doorPoint != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(doorPoint.position, 0.09f);
            if (prev != null) Gizmos.DrawLine(prev.position, doorPoint.position);
            prev = doorPoint;
        }

        // フェイントの帰還ルート（TurnPoint → H2 → H1 → startPoint）— マゼンタ
        Gizmos.color = Color.magenta;
        if (hallwayPoint2 != null && hallwayPoint1 != null)
            Gizmos.DrawLine(hallwayPoint2.position, hallwayPoint1.position);
        if (hallwayPoint1 != null && startPoint != null)
            Gizmos.DrawLine(hallwayPoint1.position, startPoint.position);

        // roomEntryPoints — 橙（doorPointからの入室ルート）
        if (roomEntryPoints != null && doorPoint != null)
        {
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Transform prevRoom = doorPoint;
            foreach (Transform wp in roomEntryPoints)
            {
                if (wp == null) continue;
                Gizmos.DrawSphere(wp.position, 0.07f);
                Gizmos.DrawLine(prevRoom.position, wp.position);
                prevRoom = wp;
            }
        }
    }
#endif
}