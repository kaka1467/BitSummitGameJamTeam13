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
///   フェイントA（HallwayPassBy）：startPoint → hallwayPoint1 → hallwayPoint2 → turnPoint → hallwayPoint3
///             → doorPoint（停止せず通過・ドア操作なし）→ hallwayPassByPoint（画面外で停止）
///             → onPassedByDoor（既存のPDV2.OnApproachPassedBy経由でサイクル終了・怪しさなし）
///   庭側素通り（GardenPassBy）：TurnPointでドア側経路から分岐し、gardenRoutePoints[]の中間ウェイポイントを
///             順番に通ってGardenPeekPoint（停止せず通過）→ GardenPassByPointへ進む。
///             hallwayPoint3／doorPointはドア側の経由点のため、庭ルートでは通らない。
///   庭側覗き（GardenPeek）：GardenPassByと同じ経路でGardenPeekPointまで進み、そこで停止して覗き方向へ回転する。
///             覗き時間（gardenPeekDurationBase+覗き開始時のゲージ値）経過後、GardenPassByPointまで進み、
///             onPassedByDoorを一度だけ発生して既存の終了処理（PDV2.OnApproachPassedBy →
///             ResetCycle/EndWarningSequence → ResetApproach）でStartPointへ復帰する。
///             ドア開閉・疑惑加算・捕獲判定には到達しない。
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
///   庭ルート（GardenPassBy/GardenPeek）：TurnPoint以降は移動中、進行方向へ滑らかに向きを変える
///             （MoveToPointFacingMovement）。中間ウェイポイントのTransform.rotationは読まない。
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

    [Tooltip("フェイントA（HallwayPassBy）用の画面外到達点。doorPointを停止せず通過した後に進む。未設定の場合はHallwayPassByを開始しない。")]
    public Transform hallwayPassByPoint;

    [Tooltip("庭側素通り用の到達点。doorPointを停止せず通過した後、庭側の実配置に沿って進む。")]
    public Transform gardenPassByPoint;

    [Tooltip("庭側ルートの最初の到達点。GardenPassByでは停止せず通過する。")]
    public Transform gardenPeekPoint;

    [Tooltip("庭側ルートの中間ウェイポイント（TurnPointからGardenPeekPointへ向かう順番に設定）。未設定の場合はGardenPassByを開始しない。")]
    public Transform[] gardenRoutePoints;

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

    [Tooltip("母親の足元が床に埋まる／浮く場合に調整。ワールドYに加算")]
    [SerializeField] private float motherHeightOffset = 0f;

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
    [Tooltip("母親モデルのAnimator。未設定の場合はMotherRouteRoot配下から自動取得する。")]
    [SerializeField] private Animator motherAnimator;

    // ── 演出 ──────────────────────────────────────────────────────────────────
    [Header("演出")]
    [Tooltip("母親の覗き見・捕獲突入時に点灯する目のオブジェクト。")]
    [SerializeField] private GameObject glowingEyesObject;

    [Header("目の発光マテリアル/カラー")]
    [Tooltip("左目の発光用Renderer。")]
    [SerializeField] private Renderer eyeRendererL;
    [Tooltip("右目の発光用Renderer。")]
    [SerializeField] private Renderer eyeRendererR;
    [ColorUsage(true, true)]
    [SerializeField] private Color normalGlowColor = new Color(1f, 0.8f, 0.2f, 1f);
    [ColorUsage(true, true)]
    [SerializeField] private Color dangerGlowColor = new Color(1f, 0f, 0f, 1f);
    [Tooltip("怪しさゲージ参照。未設定の場合はシーンから自動取得する。")]
    [SerializeField] private MotherGauge motherGauge;

    // ── オーディオ ─────────────────────────────────────────────────────────────
    [Header("移動音 (AudioSource)")]
    [Tooltip("廊下の移動中に再生するAudioSource。")]
    [SerializeField] private AudioSource hallwayFootstepAudioSource;
    [Tooltip("庭（草むら）の移動中に再生するAudioSource。")]
    [SerializeField] private AudioSource gardenFootstepAudioSource;
    [Tooltip("廊下通過フェイント完了時に再生する偽ドア音。")]
    [SerializeField] private AudioSource passBySoundAudioSource;

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

    [Tooltip("庭側覗き（GardenPeek）でGardenPeekPointに留まる基本秒数。実際の覗き時間=この値+GardenPeekPoint到着時のゲージ値。")]
    [SerializeField] private float gardenPeekDurationBase = 3f;

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
    [Tooltip("親機がGardenPeekPointで覗き待機を開始したときに発生する。庭覗き中の継続疑惑（ParentDetectionV2）の開始トリガー。")]
    public UnityEvent onGardenPeekStarted;

    // ── 公開読み取り専用状態 ──────────────────────────────────────────────────
    private bool IsApproaching { get; set; }
    public bool ReachedDoor      { get; private set; }
    public bool StoppedAtDoor    { get; private set; }
    public bool PassedByDoor     { get; private set; }
    public bool IsInHallwayPhase { get; private set; }
    /// <summary>GardenPeekPointで覗き待機中か。庭覗き専用の状態で、isMotherLookingNow（ドア側の本チェック）には影響しない。</summary>
    public bool IsGardenPeeking => _isGardenPeeking;

    /// <summary>通過完了時の偽ドア音を再生する。</summary>
    public void PlayPassBySound()
    {
        if (passBySoundAudioSource != null)
            passBySoundAudioSource.Play();
    }

    // ── 実行モード ────────────────────────────────────────────────────────────
    /// <summary>大きな音による突入開始前にParentWarningSystemが設定する。移動ループ音を抑制し、rushInPauseAtDoorSecondsを使用する。</summary>
    public bool IsRushIn { get; set; }

    // ── 非公開 ───────────────────────────────────────────────────────────────
    private Coroutine _approachCoroutine;
    private float _fixedPitch;
    private float _fixedRoll;
    private float _currentAudioVolume;
    private float _targetAudioVolume;
    private float _gardenPeekDuration;   // GardenPeekの覗き時間（秒）。GardenPeekPoint到着時に一度だけ決定する。
    private bool _isGardenPeeking;       // GardenPeekPointで覗き待機中か。isMotherLookingNowには影響しない。

    // 部屋入室（案B）の状態
    private bool _cycleStartedAsRushIn;   // このサイクルが突入（大きな音）として開始されたか — BeginApproach()で捕捉する
    private bool _doorRoutineActive;      // DoorRoutine()が実行中か（入室要求の受付条件）
    private bool _roomEntryRequested;     // OnStoppedAtDoor中にPDV2から入室要求を受けたか
    private bool _roomPhaseActive;        // 入室フェーズ（部屋内部への移動〜doorPoint復帰）が進行中か
    private bool _leaveRoomRequested;     // 部屋内部からの退室要求を受けたか
    private bool _animatorWarningLogged;
    private string _diagnosticRouteName = "<none>";
    private int _diagnosticLastStateHash;
    private bool _isGrassFootstepRoute;

    // ──────────────────────────────────────────────────────────────────────────
    //  Unityライフサイクル
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        SetGlowingEyes(false);
        _currentAudioVolume = farVolume;
        _targetAudioVolume = farVolume;
        InitializeFootstepAudioSource(hallwayFootstepAudioSource);
        InitializeFootstepAudioSource(gardenFootstepAudioSource);

        // 覗き時間の計算に使う疑惑ゲージをキャッシュする（見つからない場合は覗き開始時に再試行する）。
        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();
    }

    private void Update()
    {
        UpdateMovementLoopAudio();
        UpdateEyeColor();
        LogDiagnosticPeekStateEntry();
    }

    private void UpdateMovementLoopAudio()
    {
        // 覗き機能削除に伴い、通常ルートの接近中は常に移動ループを再生する。
        bool shouldPlay = !IsRushIn && IsApproaching;
        AudioSource activeSource = GetActiveFootstepAudioSource();
        if (shouldPlay && activeSource != null)
        {
            StopInactiveFootstepAudioSources(activeSource);
            if (!activeSource.isPlaying)
            {
                activeSource.loop = true;
                activeSource.volume = _currentAudioVolume;
                activeSource.Play();
                Debug.Log("[ParentApproachController] 移動ループを開始（接近中）");
            }

            // 現在の音量から目標音量へ滑らかに変化させる
            _currentAudioVolume = Mathf.MoveTowards(_currentAudioVolume, _targetAudioVolume, volumeChangeSpeed * Time.deltaTime);
            activeSource.volume = _currentAudioVolume;
        }
        else
        {
            StopFootstepAudioSources();
            _targetAudioVolume = farVolume;
            _currentAudioVolume = farVolume;
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
    /// フェイントA（HallwayPassBy）を開始する：TurnPointからドア方向へ進み、
    /// doorPointでは停止せずに通り過ぎてhallwayPassByPoint（画面外）へ到達する。
    /// onReachedDoor／onStoppedAtDoorは発生させず、DoorControllerは操作しない。
    /// 終了時は既存のonPassedByDoorを発生させる（PDV2.OnApproachPassedBy → ResetCycle → EndWarningSequence）。
    /// hallwayPassByPointが未設定の場合は警告を1回出して開始しない。
    /// </summary>
    public void StartApproachHallwayPassBy()
    {
        if (IsApproaching)
        {
            Debug.Log("[ParentApproachController] すでに接近中 — StartApproachHallwayPassByを無視");
            return;
        }

        if (hallwayPassByPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] hallwayPassByPointが未設定のためHallwayPassByを開始しません。SceneでTransformを割り当ててください。", this);
            return;
        }

        if (!ValidateWaypoints(requirePassBy: false)) return;

        BeginApproach(passByRoute: true, hallwayPassBy: true);
    }

    /// <summary>
    /// 庭側素通りを開始する。ドア停止イベントを発生させず、庭側到達点でonPassedByDoorを発生させる。
    /// </summary>
    public void StartApproachGardenPassBy()
    {
        if (IsApproaching)
        {
            Debug.Log("[ParentApproachController] すでに接近中 — StartApproachGardenPassByを無視");
            return;
        }

        if (gardenPassByPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] gardenPassByPointが未設定のためGardenPassByを開始しません。SceneでTransformを割り当ててください。", this);
            return;
        }

        if (!HasValidGardenRoutePoints())
        {
            Debug.LogWarning("[ParentApproachController] gardenRoutePointsが未設定のためGardenPassByを開始しません。SceneでTurnPoint→GardenPeekPoint間の中間ウェイポイントを順番に割り当ててください。", this);
            return;
        }

        if (!ValidateWaypoints(requirePassBy: false)) return;

        BeginApproach(passByRoute: true, gardenPassBy: true);
    }

    /// <summary>
    /// gardenRoutePointsに有効（null以外）な中間ウェイポイントが1つ以上あるかを返す。
    /// </summary>
    private bool HasValidGardenRoutePoints()
    {
        if (gardenRoutePoints == null) return false;
        for (int i = 0; i < gardenRoutePoints.Length; i++)
        {
            if (gardenRoutePoints[i] != null) return true;
        }
        return false;
    }

    /// <summary>
    /// 庭側覗きを開始する。GardenPassByと同じ経路でGardenPeekPointまで進み、停止して覗き方向へ回転する。
    /// 覗き時間（gardenPeekDurationBase+GardenPeekPoint到着時のゲージ値。到着時に一度だけ取得）経過後、
    /// GardenPassByPointまで進み、onPassedByDoorを一度だけ発生させて既存の終了処理でStartPointへ復帰する。
    /// </summary>
    public void StartApproachGardenPeek()
    {
        if (IsApproaching)
        {
            Debug.Log("[ParentApproachController] すでに接近中 — StartApproachGardenPeekを無視");
            return;
        }

        if (gardenPeekPoint == null)
        {
            Debug.LogWarning("[ParentApproachController] gardenPeekPointが未設定のためGardenPeekを開始しません。SceneでTransformを割り当ててください。", this);
            return;
        }

        if (!HasValidGardenRoutePoints())
        {
            Debug.LogWarning("[ParentApproachController] gardenRoutePointsが未設定のためGardenPeekを開始しません。SceneでTurnPoint→GardenPeekPoint間の中間ウェイポイントを順番に割り当ててください。", this);
            return;
        }

        if (!ValidateWaypoints(requirePassBy: false)) return;

        BeginApproach(passByRoute: true, gardenPeek: true);
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

        if (CountValidRoomEntryPoints() == 0)
        {
            // 未設定／全要素Noneの場合はログを出さず、従来どおりdoorPointで停止する挙動へフォールバックする。
            // 有効なTransformが1個以上あるときだけ入室を受理する。
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
            transform.position = OffsetGoalPosition(startPoint.position);
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

    private void BeginApproach(bool passByRoute, bool hallwayPassBy = false, bool gardenPassBy = false, bool gardenPeek = false)
    {
        EnsureMotherAnimator();
        _isGrassFootstepRoute = false;
        StopFootstepAudioSources();
        _diagnosticRouteName = gardenPeek
            ? "GardenPeek"
            : gardenPassBy
                ? "GardenPassBy"
                : hallwayPassBy
                    ? "HallwayPassBy"
                    : passByRoute
                        ? "PassBy"
                        : "Door";
        // 「このサイクルは突入（大きな音）として開始されたか」を記録する。
        // ParentWarningSystemはIsRushIn=trueを設定してから本メソッドを呼ぶため、
        // ResetStateFlags()でIsRushInが消える前にここで捕捉する（入室可否の判定に使用する）。
        _cycleStartedAsRushIn = IsRushIn;

        ResetStateFlags();

        // 接近開始時は遠い段階の音量から初期化
        _targetAudioVolume = farVolume;
        _currentAudioVolume = farVolume;
        SetFootstepAudioSourceVolume(farVolume);

        // startPoint.rotationからピッチ／ロールを取得し、キャンセルされたサイクル後に
        // 実行途中の古いTransformが誤った値を引き継がないようにする。
        Vector3 startEuler = startPoint.rotation.eulerAngles;
        _fixedPitch = startEuler.x;
        _fixedRoll  = startEuler.z;

        transform.position = OffsetGoalPosition(startPoint.position);
        transform.rotation = startPoint.rotation;

        ShowMotherModel();
        SetGlowingEyes(_cycleStartedAsRushIn);
        SetWalkingAnimation(true);

        IsApproaching = true;
        onApproachStarted?.Invoke();

        Debug.Log($"[ParentApproachController] BeginApproach | passByRoute={passByRoute} | hallwayPassBy={hallwayPassBy} | gardenPassBy={gardenPassBy} | gardenPeek={gardenPeek} | pitch={_fixedPitch:F1} roll={_fixedRoll:F1} | targetVolume={farVolume}");

        if (gardenPeek)
            _approachCoroutine = StartCoroutine(GardenPeekRoutine());
        else if (gardenPassBy)
            _approachCoroutine = StartCoroutine(GardenPassByRoutine());
        else if (hallwayPassBy)
            _approachCoroutine = StartCoroutine(HallwayPassByRoutine());
        else
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
        SetWalkingAnimation(false);
        LogDiagnosticWaypoint("DoorPoint arrival and rotation complete", doorPoint);
        SetGlowingEyes(true);

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

        SetGlowingEyes(false);
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
    /// フェイントA（HallwayPassBy）の移動ルーチン：
    ///   startPoint → hallwayPoint1 → hallwayPoint2 → turnPoint（旋回）
    ///   → hallwayPoint3 → doorPoint（停止せず通過・ドア操作なし）
    ///   → hallwayPassByPoint（画面外で停止）
    /// onReachedDoor／onStoppedAtDoorは一切発生させないため、ParentDetectionV2の
    /// ドア分岐（primary=固定でドア全開になる経路）には到達しない。
    /// 到達後に既存のonPassedByDoorを発生させ、
    /// PDV2.OnApproachPassedBy → ResetCycle → EndWarningSequence → ResetApproach（StartPointへ即時復帰）
    /// の既存フローで怪しさ・捕獲なしのままサイクルを終える。
    /// </summary>
    private IEnumerator HallwayPassByRoutine()
    {
        Debug.Log("[ParentApproachController] HallwayPassByRoutine（フェイントA：ドア前を停止せず通り過ぎる）：開始");

        // 廊下フェーズ：既存のMoveToTurnPointを再利用（hallwayPoint1 → hallwayPoint2 → turnPoint旋回）。
        yield return MoveToTurnPoint();

        // ドア方向へ進む。hallwayPoint3は未設定なら既存仕様どおりスキップされる。
        // ドアに近づくため、既存DoorRoutineと同じくドア前音量へ引き上げる（音量機構自体は変更しない）。
        _targetAudioVolume = nearDoorVolume;

        yield return MoveToPoint(hallwayPoint3);

        // doorPointは停止せず通過する。ドアは操作せず、onReachedDoor／onStoppedAtDoorも発生させない。
        if (doorPoint != null)
        {
            Debug.Log("[ParentApproachController]   doorPointを通過（停止なし・ドア操作なし）");
            yield return MoveToPoint(doorPoint);
        }

        // 画面外の到達点まで進み、到達したら停止する。
        yield return MoveToPoint(hallwayPassByPoint);
        PlayPassBySound();

        StopMovementAudio();
        PassedByDoor  = true;
        IsApproaching = false;
        // IsInHallwayPhaseは既存DoorRoutine／PassByRoutineと同じくResetStateFlags()でのみ解除する。

        Debug.Log("[ParentApproachController] フェイントA完了 — 画面外で停止しOnPassedByDoorを発生");
        onPassedByDoor?.Invoke();
    }

    private IEnumerator GardenPassByRoutine()
    {
        Debug.Log("[ParentApproachController] GardenPassByRoutine（庭側素通り）：開始");

        yield return MoveToTurnPoint();
        _targetAudioVolume = nearDoorVolume;

        // TurnPointでドア側経路から分岐し、庭側の中間ウェイポイントを設定順に進む。
        // 移動中は進行方向を向く（中間ウェイポイントのTransform.rotationは読まない）。
        // hallwayPoint3／doorPointはドア側の経由点のため、庭ルートでは通らない。
        for (int i = 0; i < gardenRoutePoints.Length; i++)
        {
            Transform gardenRoute = gardenRoutePoints[i];
            if (gardenRoute == null) continue;
            Debug.Log($"[ParentApproachController]   gardenRoute[{i}] '{gardenRoute.name}'");
            yield return MoveToPointFacingMovement(gardenRoute);
        }

        yield return MoveToPointFacingMovement(gardenPeekPoint);
        yield return MoveToPointFacingMovement(gardenPassByPoint);

        StopMovementAudio();
        PassedByDoor = true;
        IsApproaching = false;

        Debug.Log("[ParentApproachController] 庭側素通り完了 — OnPassedByDoorを発生");
        onPassedByDoor?.Invoke();
    }

    /// <summary>
    /// 庭側覗きの移動ルーチン：
    ///   GardenPassByと同じ経路（TurnPoint → gardenRoutePoints[] → GardenPeekPoint）で進み、
    ///   移動中は進行方向を向く（中間ウェイポイントのTransform.rotationは読まない）。
    ///   GardenPeekPointで停止して覗き方向（GardenPeekPoint.rotationのY角）へ回転する。
    ///   覗き時間は覗き開始時に一度だけ決定された_gardenPeekDurationを使用する（覗き中のゲージ変化では延長しない）。
    ///   時間経過後はGardenPassByPointまで進み、そこで既存のonPassedByDoorを一度だけ発生させる。
    ///   終了処理（警告終了・全灯消灯・StartPoint復帰）はPWS.HandlePassedByDoor →
    ///   PDV2.OnApproachPassedBy → ResetCycle + EndWarningSequence の既存経路で行う
    ///   （ドア開閉・疑惑加算・捕獲判定には到達しない）。
    /// </summary>
    private IEnumerator GardenPeekRoutine()
    {
        Debug.Log("[ParentApproachController] GardenPeekRoutine（庭側覗き）：開始");

        // 5キーと同じ経路：MoveToTurnPoint → gardenRoutePointsを設定順に進む。
        // 移動中は進行方向を向く。hallwayPoint3／doorPointはドア側の経由点のため、庭ルートでは通らない。
        yield return MoveToTurnPoint();
        _targetAudioVolume = nearDoorVolume;

        for (int i = 0; i < gardenRoutePoints.Length; i++)
        {
            Transform gardenRoute = gardenRoutePoints[i];
            if (gardenRoute == null) continue;
            Debug.Log($"[ParentApproachController]   gardenRoute[{i}] '{gardenRoute.name}'");
            yield return MoveToPointFacingMovement(gardenRoute);
        }

        // GardenPeekPointで停止し、設定された覗き方向（GardenPeekPoint.rotationのY角）へ回転する。
        yield return MoveToPointFacingMovement(gardenPeekPoint);
        yield return RotateToTransformYaw(gardenPeekPoint, doorTurnRotationSpeed);
        SetWalkingAnimation(false);
        LogDiagnosticWaypoint("GardenPeekPoint arrival and rotation complete", gardenPeekPoint);
        SetGlowingEyes(true);
        TriggerWindowPeekAnimation();

        // 覗き時間は「GardenPeekPoint到着時のゲージ値」を一度だけ取得して決定する（覗き中のゲージ変化では延長しない）。
        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        int gaugeAtPeekStart = (motherGauge != null) ? motherGauge.currentGauge : 0;
        _gardenPeekDuration = Mathf.Max(0f, gardenPeekDurationBase) + gaugeAtPeekStart;
        Debug.Log($"[ParentApproachController]   GardenPeekPointで覗き | {_gardenPeekDuration:F1}s (base={gardenPeekDurationBase:F1} + gauge={gaugeAtPeekStart})");

        // 覗き待機開始：庭覗き中の継続疑惑（PDV2）に開始を通知する。
        _isGardenPeeking = true;
        onGardenPeekStarted?.Invoke();

        yield return new WaitForSeconds(_gardenPeekDuration);

        // 覗き待機終了：継続疑惑が次tick以内に確実に停止するよう、フラグを先に解除する。
        _isGardenPeeking = false;
        SetGlowingEyes(false);

        // 覗き終了：GardenPassByPointまで進み、到着後に既存の終了処理へ引き渡す。
        // 途中で警告終了・灯り消灯は行わない（通知はGardenPassByPoint到着後の1回だけ）。
        Debug.Log("[ParentApproachController]   覗き終了 — GardenPassByPointへ進む");
        yield return MoveToPointFacingMovement(gardenPassByPoint);

        StopMovementAudio();
        PassedByDoor = true;
        IsApproaching = false;

        // 到着後に一度だけ通知する（二重通知なし）。
        // PWS.HandlePassedByDoor → PDV2.OnApproachPassedBy → ResetCycle + EndWarningSequence
        // （全灯消灯・isWarningActive解除・ResetApproachでStartPointへ復帰）が既存経路で実行される。
        Debug.Log("[ParentApproachController] 庭側覗き完了 — GardenPassByPoint到着、OnPassedByDoorを発生");
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
            LogDiagnosticWaypoint("TurnPoint arrival and rotation complete", turnPoint);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  移動／回転ヘルパー
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 目標位置へmotherHeightOffset（ワールドY）を1回だけ加算した到達点を返す。
    /// 常にwaypoint（またはstartPoint）の生座標から再計算するため、フレーム間・waypoint間で
    /// オフセットは累積しない。X/Zは元のwaypoint座標をそのまま使う。
    /// </summary>
    private Vector3 OffsetGoalPosition(Vector3 targetPosition)
    {
        return new Vector3(targetPosition.x, targetPosition.y + motherHeightOffset, targetPosition.z);
    }

    private IEnumerator MoveToPoint(Transform target)
    {
        if (target == null)
        {
            // 中間ウェイポイントが未設定の場合はスキップして続行する（null安全）。
            yield break;
        }

        // 到達点は「waypoint位置＋motherHeightOffset」を1回だけ計算する。
        // 現在位置へオフセットを加算しないため、フレーム間・waypoint間で高さが累積しない。
        Vector3 goal = OffsetGoalPosition(target.position);

        if (Vector3.Distance(transform.position, goal) > stopDistance)
            SetWalkingAnimation(true);

        while (Vector3.Distance(transform.position, goal) > stopDistance)
        {
            transform.position = Vector3.MoveTowards(
                transform.position, goal, moveSpeed * Time.deltaTime);
            yield return null;
        }
        transform.position = goal;
        UpdateGardenFootstepClip(target);
    }

    /// <summary>
    /// 庭ルート専用の移動ヘルパー。移動中、進行方向（現在位置から目標へのXZ方向）へ滑らかに向きを変えながら進む。
    /// 向きは移動方向から求めるため、中間ウェイポイントのTransform.rotationは読まない。
    /// X/Z回転は_fixedPitch/_fixedRollで固定し、Y角のみを変える（既存RotateToYawと同じ規則）。
    /// ドア側ルート（DoorRoutine／HallwayPassByRoutine／PassByRoutine）は既存のMoveToPointを使い続けるため影響しない。
    /// </summary>
    private IEnumerator MoveToPointFacingMovement(Transform target)
    {
        if (target == null) yield break;

        // MoveToPointと同じく、到達点はwaypointの生座標＋motherHeightOffsetを1回だけ適用する。
        Vector3 goal = OffsetGoalPosition(target.position);

        if (Vector3.Distance(transform.position, goal) > stopDistance)
            SetWalkingAnimation(true);

        while (Vector3.Distance(transform.position, goal) > stopDistance)
        {
            // 向きはXZのみで決める（delta.y=0）ため、高さ補正は旋回に影響しない。
            Vector3 delta = goal - transform.position;
            delta.y = 0f;
            if (delta.sqrMagnitude > 0.0001f)
            {
                float targetYaw = NormalizeAngle(Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg);
                float newYaw = Mathf.MoveTowardsAngle(
                    transform.rotation.eulerAngles.y, targetYaw, stairTurnRotationSpeed * Time.deltaTime);
                transform.rotation = Quaternion.Euler(_fixedPitch, newYaw, _fixedRoll);
            }

            transform.position = Vector3.MoveTowards(
                transform.position, goal, moveSpeed * Time.deltaTime);
            yield return null;
        }
        transform.position = goal;
        UpdateGardenFootstepClip(target);
    }

    private void UpdateGardenFootstepClip(Transform target)
    {
        if (!_isGrassFootstepRoute &&
            (target.name == "GardenPoint_1" ||
             (gardenRoutePoints != null && gardenRoutePoints.Length > 0 && target == gardenRoutePoints[0])))
        {
            _isGrassFootstepRoute = true;
            StopFootstepAudioSources();
        }
    }

    private AudioSource GetActiveFootstepAudioSource()
    {
        return _isGrassFootstepRoute
            ? gardenFootstepAudioSource
            : hallwayFootstepAudioSource;
    }

    private void InitializeFootstepAudioSource(AudioSource source)
    {
        if (source == null)
            return;

        source.loop = true;
        source.Stop();
        source.volume = farVolume;
    }

    private void StopInactiveFootstepAudioSources(AudioSource activeSource)
    {
        AudioSource inactiveSource = activeSource == hallwayFootstepAudioSource
            ? gardenFootstepAudioSource
            : hallwayFootstepAudioSource;
        if (inactiveSource != null && inactiveSource.isPlaying)
            inactiveSource.Stop();
    }

    private void StopFootstepAudioSources()
    {
        if (hallwayFootstepAudioSource != null)
            hallwayFootstepAudioSource.Stop();
        if (gardenFootstepAudioSource != null)
            gardenFootstepAudioSource.Stop();
    }

    private void SetFootstepAudioSourceVolume(float volume)
    {
        if (hallwayFootstepAudioSource != null)
            hallwayFootstepAudioSource.volume = volume;
        if (gardenFootstepAudioSource != null)
            gardenFootstepAudioSource.volume = volume;
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

    public void TriggerDoorPeekAnimation()
    {
        EnsureMotherAnimator();
        if (motherAnimator == null)
        {
            LogAnimatorWarning();
            return;
        }

        if (!HasAnimatorParameter("Peek_Door", AnimatorControllerParameterType.Trigger))
        {
            LogAnimatorWarning();
            return;
        }

        LogDiagnosticPeekTrigger("Peek_Door", doorPoint);
        motherAnimator.SetTrigger("Peek_Door");
    }

    private void TriggerWindowPeekAnimation()
    {
        EnsureMotherAnimator();
        if (motherAnimator == null)
        {
            LogAnimatorWarning();
            return;
        }

        if (!HasAnimatorParameter("Peek_Windows", AnimatorControllerParameterType.Trigger))
        {
            LogAnimatorWarning();
            return;
        }

        LogDiagnosticPeekTrigger("Peek_Windows", gardenPeekPoint);
        motherAnimator.SetTrigger("Peek_Windows");
    }

    private void SetWalkingAnimation(bool isWalking)
    {
        EnsureMotherAnimator();
        if (motherAnimator == null)
        {
            LogAnimatorWarning();
            return;
        }

        if (!HasAnimatorParameter("Walk", AnimatorControllerParameterType.Bool))
        {
            LogAnimatorWarning();
            return;
        }

        motherAnimator.SetBool("Walk", isWalking);
    }

    private void EnsureMotherAnimator()
    {
        if (motherAnimator == null)
            motherAnimator = GetComponentInChildren<Animator>(true);
    }

    private bool HasAnimatorParameter(string parameterName, AnimatorControllerParameterType parameterType)
    {
        foreach (AnimatorControllerParameter parameter in motherAnimator.parameters)
        {
            if (parameter.name == parameterName && parameter.type == parameterType)
                return true;
        }

        return false;
    }

    private void LogAnimatorWarning()
    {
        if (_animatorWarningLogged)
            return;

        _animatorWarningLogged = true;
        Debug.LogWarning(
            "[ParentApproachController] 母親AnimatorまたはWalk/Peekパラメータが未設定のため、アニメーション制御をスキップします。",
            this);
    }

    private void LogDiagnosticWaypoint(string eventName, Transform target)
    {
        Debug.Log(
            $"[MotherAnimationDiagnostic] event={eventName} frame={Time.frameCount} time={Time.time:F3} " +
            $"route={_diagnosticRouteName} rootPosition={transform.position} " +
            $"target={target.name} targetPosition={target.position} distance={Vector3.Distance(transform.position, target.position):F4} " +
            $"walk={GetDiagnosticWalkValue()} animator={GetDiagnosticAnimatorName()} controller={GetDiagnosticControllerName()} " +
            $"state={GetDiagnosticStateName()} inTransition={GetDiagnosticTransitionState()}",
            this);
    }

    private void LogDiagnosticPeekTrigger(string triggerName, Transform target)
    {
        Debug.Log(
            $"[MotherAnimationDiagnostic] event=before {triggerName} frame={Time.frameCount} time={Time.time:F3} " +
            $"route={_diagnosticRouteName} rootPosition={transform.position} " +
            $"target={GetDiagnosticTargetName(target)} targetPosition={GetDiagnosticTargetPosition(target)} distance={GetDiagnosticTargetDistance(target):F4} " +
            $"walk={GetDiagnosticWalkValue()} animator={GetDiagnosticAnimatorName()} controller={GetDiagnosticControllerName()} " +
            $"state={GetDiagnosticStateName()} inTransition={GetDiagnosticTransitionState()}",
            this);
    }

    private void LogDiagnosticPeekStateEntry()
    {
        if (motherAnimator == null || !motherAnimator.isActiveAndEnabled)
            return;

        AnimatorStateInfo state = motherAnimator.GetCurrentAnimatorStateInfo(0);
        if (state.fullPathHash == _diagnosticLastStateHash)
            return;

        _diagnosticLastStateHash = state.fullPathHash;
        string stateName = state.IsName("Base Layer.Door Peek") ? "Door Peek" :
            state.IsName("Base Layer.window peek") ? "window peek" : null;
        if (stateName == null)
            return;

        Transform target = stateName == "Door Peek" ? doorPoint : gardenPeekPoint;
        Debug.Log(
            $"[MotherAnimationDiagnostic] event=entered {stateName} state frame={Time.frameCount} time={Time.time:F3} " +
            $"route={_diagnosticRouteName} rootPosition={transform.position} " +
            $"target={GetDiagnosticTargetName(target)} targetPosition={GetDiagnosticTargetPosition(target)} distance={GetDiagnosticTargetDistance(target):F4} " +
            $"walk={GetDiagnosticWalkValue()} animator={GetDiagnosticAnimatorName()} controller={GetDiagnosticControllerName()} " +
            $"state={stateName} inTransition={GetDiagnosticTransitionState()}",
            this);
    }

    private string GetDiagnosticWalkValue()
    {
        return motherAnimator != null && HasAnimatorParameter("Walk", AnimatorControllerParameterType.Bool)
            ? motherAnimator.GetBool("Walk").ToString()
            : "<unavailable>";
    }

    private string GetDiagnosticAnimatorName()
    {
        return motherAnimator != null ? motherAnimator.gameObject.name : "<unavailable>";
    }

    private string GetDiagnosticControllerName()
    {
        return motherAnimator != null && motherAnimator.runtimeAnimatorController != null
            ? motherAnimator.runtimeAnimatorController.name
            : "<unavailable>";
    }

    private string GetDiagnosticStateName()
    {
        return motherAnimator != null && motherAnimator.isActiveAndEnabled
            ? motherAnimator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle")
                ? "Idle"
                : motherAnimator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Walk")
                    ? "Walk"
                    : motherAnimator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Door Peek")
                        ? "Door Peek"
                        : motherAnimator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.window peek")
                            ? "window peek"
                            : "<unknown>"
            : "<unavailable>";
    }

    private string GetDiagnosticTransitionState()
    {
        return motherAnimator != null && motherAnimator.isActiveAndEnabled
            ? motherAnimator.IsInTransition(0).ToString()
            : "<unavailable>";
    }

    private string GetDiagnosticTargetName(Transform target)
    {
        return target != null ? target.name : "<unavailable>";
    }

    private string GetDiagnosticTargetPosition(Transform target)
    {
        return target != null ? target.position.ToString() : "<unavailable>";
    }

    private float GetDiagnosticTargetDistance(Transform target)
    {
        return target != null ? Vector3.Distance(transform.position, target.position) : -1f;
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
        StopFootstepAudioSources();
        SetFootstepAudioSourceVolume(farVolume);
        _isGrassFootstepRoute = false;
        _targetAudioVolume = farVolume;
        _currentAudioVolume = farVolume;
    }

    private void SetGlowingEyes(bool isEnabled)
    {
        if (glowingEyesObject != null)
            glowingEyesObject.SetActive(isEnabled);

        if (isEnabled)
            UpdateEyeColor();
    }

    private void UpdateEyeColor()
    {
        if (glowingEyesObject == null || !glowingEyesObject.activeSelf)
            return;

        int gauge = motherGauge != null ? motherGauge.currentGauge : 0;
        Color glowColor = gauge >= 7 ? dangerGlowColor : normalGlowColor;
        Color hdrGlowColor = glowColor * Mathf.Pow(2f, 3f);
        SetEyeEmissionColor(eyeRendererL, hdrGlowColor);
        SetEyeEmissionColor(eyeRendererR, hdrGlowColor);
    }

    private static void SetEyeEmissionColor(Renderer eyeRenderer, Color glowColor)
    {
        if (eyeRenderer == null)
            return;

        Material eyeMaterial = eyeRenderer.material;
        if (!eyeMaterial.HasProperty("_EmissionColor"))
            return;

        eyeMaterial.EnableKeyword("_EMISSION");
        eyeMaterial.SetColor("_EmissionColor", glowColor);
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
        _isGardenPeeking = false;
        SetGlowingEyes(false);

        // 部屋入室（案B）の状態を初期化する。_cycleStartedAsRushInはBeginApproach()で
        // ResetStateFlags()より前に設定されるため、ここではクリアしない。
        _doorRoutineActive  = false;
        _roomEntryRequested = false;
        _roomPhaseActive    = false;
        _leaveRoomRequested = false;

        SetWalkingAnimation(false);
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

        // フェイントA（HallwayPassBy）— シアン（TurnPoint → H3 → doorPoint通過 → hallwayPassByPoint）
        Gizmos.color = Color.cyan;
        if (hallwayPassByPoint != null)
        {
            Gizmos.DrawSphere(hallwayPassByPoint.position, 0.09f);
            Transform passPrev = (turnPoint != null) ? turnPoint : ((hallwayPoint2 != null) ? hallwayPoint2 : startPoint);
            if (hallwayPoint3 != null)
            {
                Gizmos.DrawLine(passPrev.position, hallwayPoint3.position);
                passPrev = hallwayPoint3;
            }
            if (doorPoint != null)
            {
                Gizmos.DrawLine(passPrev.position, doorPoint.position);
                passPrev = doorPoint;
            }
            Gizmos.DrawLine(passPrev.position, hallwayPassByPoint.position);
        }

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