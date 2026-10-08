using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// MotherSuspicionSystem:
/// ドアイベント／分岐のみを制御する。
///
/// 責務の境界：
///   MotherApproachController  — 移動とルート演出
///   MotherApproachWarning       — シーケンス調整
///   ParentWarningScheduler    — タイミングと自動警告スケジュール（1キーの母親ドア確認起動を含む）
///   MotherSuspicionSystem（本クラス）— 親機のドア到着／通過に反応し、
///                               分岐、ドア状態、サイクルリセット、大きな音を処理する
///
/// ゲージへの書き込みは次の4つの場合に行う：
///   OnLoudItemTriggered              — loudItemGaugeAmountに応じたAddGauge()の段階加算
///   TriggerPrimaryEvent              — 親機が部屋に入ったときの初回AddGauge()（睡眠中でない場合）
///   ContinuousRoomSuspicionCoroutine — ドアが開き、プレイヤーが睡眠中でない間の時間制AddGauge()
/// 現在のルートの覗き見時間はpeekDurationBase + motherGauge.currentGauge。
/// ルート分岐（本チェックか覗き見か）はwarningSystem.ActiveRouteで決まる。
/// dummyProbabilityはActiveRouteがNoneの場合のフォールバック確率として使用する。
/// </summary>
public class MotherSuspicionSystem : MonoBehaviour
{
    // ── システム参照 ──────────────────────────────────────────────────────────
    [Header("システム参照")] public MotherApproachWarning warningSystem;
    public CaughtReactionController caughtReactionController;
    public MotherGauge motherGauge;
    public MotherApproachController approachController;
    public SleepingController sleepingController;

    // ── 親機の速度・足音・Animator ────────────────────────────────────────────
    [Header("親機の速度")] public float approachMoveSpeedMin = 1.5f;
    public float approachMoveSpeedMax = 1.5f;
    public float approachSpeedSuspicionBonus;
    public int highSuspicionSpeedGaugeThreshold = 10;
    public float highSuspicionApproachMoveSpeedMin = 1.5f;
    public float highSuspicionApproachMoveSpeedMax = 1.5f;
    public float loudItemRushInMoveSpeed = 140f;
    public bool useFixedDebugApproachSpeed;
    public float fixedDebugApproachSpeed = 30f;

    [Tooltip("MotherSuspicionSystemから直接開始された初回接近で使う既存の速め速度。通常の警告開始時は警告種別の速度で上書きする。")]
    public float initialApproachSpeed = 25f;

    private Animator motherAnimator;
    // アニメーション操作の窓口（Animator への書き込みはここに集約する）。
    [SerializeField] private MotherAnimationPlayer animationPlayer;

    // ── ドア ──────────────────────────────────────────────────────────────────
    [Header("ドア制御")] [SerializeField] private DoorController targetDoorController;

    // ── 分岐 ──────────────────────────────────────────────────────────────────
    [Header("イベント分岐")]
    [Tooltip("MotherApproachWarningからルート状態を取得できない場合のダミー（覗き見）チェック確率（例：Pキーのデバッグ）。")]
    [SerializeField, Range(0f, 1f)]
    private float dummyProbability = 0.3f;

    // ── 猫フェイント（3キー専用） ─────────────────────────────────────────────
    [Header("猫フェイント")] [Tooltip("猫フェイントで猫の表示を管理するCatFeintController。未設定の場合は猫フェイントを開始しない。")] [SerializeField]
    private CatFeintController catFeintController;

    private Coroutine _catFeintCoroutine;

    // ── 覗き見／部屋チェックのタイミング ─────────────────────────────────────
    [Header("部屋チェックのタイミング")] [Tooltip("ダミー（覗き見のみ）イベントの基本時間（秒）。実際の時間=peekDurationBase + currentGauge。")] [SerializeField]
    private float peekDurationBase = 3f;

    [Tooltip("RushInでドアに到着してから覗き続ける秒数。通常の本チェック時間とは別管理。")] [SerializeField]
    private float rushInPeekDurationSeconds = 6f;

    [Tooltip("本チェック（全開）イベントで、プレイヤーが枕で眠るまで親機が部屋に留まる時間。 " +
             "一度も眠らない場合は、安全タイムアウトとしてこの秒数後に親機が退出する。")]
    [SerializeField]
    private float roomCheckSafetyTimeout = 30f;

    [Tooltip("帰路（Peek終了後に母親が画面外へ戻る）の完了を待つ上限秒数。\n" +
             "これを超えたら警告を出してサイクルを終了する（無限待機しない）。")]
    [SerializeField]
    private float returnHomeSafetyTimeout = 15f;

    [Tooltip("プレイヤーが眠ってから親機がドアを閉めて退出するまでの秒数（疑惑0の場合）。")] [SerializeField]
    private float leaveAfterSleepDelay = 2f;

    [Tooltip("最大疑惑時に、プレイヤーが眠ってから親機が退出するまでの秒数。疑惑0のleaveAfterSleepDelayから最大疑惑時のこの値まで補間する。")] [SerializeField]
    private float leaveAfterSleepDelayMax = 6f;

    [Tooltip("部屋からの退室を要求してから退室完了（OnExitedRoom）を待つ最大秒数。超過した場合は従来どおりドアを閉じて終了する。")] [SerializeField]
    private float roomExitSafetyTimeout = 15f;

    // ── 部屋侵入時の疑惑 ──────────────────────────────────────────────────────
    [Header("部屋侵入時の疑惑")] [Tooltip("親機が部屋に入り、プレイヤーが睡眠中でないときに発生する3回の増加の間隔（秒）。")] [SerializeField]
    private float roomEntryBurstTickInterval = 0.2f;

    // ── 大きな音のアイテム ────────────────────────────────────────────────────
    [Header("大きな音のアイテム機能")] [Tooltip("無効にすると、0キーおよびゲーム内の大きな音のアイテムトリガーが完全に無効になる。")] [SerializeField]
    private bool enableLoudItemFeature = true;

    [Tooltip("trueの場合、大きな音のアイテムが進行中の警告を中断し、突入を強制する。")] [SerializeField]
    private bool forceLoudItemDuringWarning = false;

    [Tooltip("大きな音のアイテム発生時にMotherGaugeへ加算する段階数。最大値に達した場合は突入せず即座にゲームオーバーになる。")] [SerializeField]
    private int loudItemGaugeAmount = 3;

    // ── 部屋内の継続疑惑 ──────────────────────────────────────────────────────
    // ── 庭覗き中の継続疑惑 ────────────────────────────────────────────────────
    [Header("庭覗き中の継続疑惑")]
    [Tooltip("庭覗き中の加算間隔=ドア側の継続疑惑間隔(continuousRoomSuspicionTickInterval)×この倍率。0以下で庭覗き中の疑惑加算を無効化する。")]
    [SerializeField]
    private float gardenPeekSuspicionIntervalMultiplier = 1.5f;

    private Coroutine _gardenPeekSuspicionCoroutine;

    [Header("部屋内の継続疑惑")] [Tooltip("親機の本チェックドアイベント中、疑惑を継続的に増加させる。")] [SerializeField]
    private bool enableContinuousRoomSuspicion = true;

    [Tooltip("部屋チェック継続フェーズで1回ごとに加算するゲージ段階数。")] [SerializeField]
    private int continuousRoomSuspicionAmount = 1;

    [Tooltip("部屋内の継続疑惑を加算する間隔（秒）。")] [SerializeField]
    private float continuousRoomSuspicionTickInterval = 2f;

    // ── 片付け演出（MotherChoreController との連携） ─────────────────────────
    [Header("片付け演出")] [Tooltip("片付け演出を管理するMotherChoreController。未設定の場合は自動検索する。")] [SerializeField]
    private MotherChoreController motherChoreController;

    [Tooltip("片付け演出に必要な通知（進行率・悪いアイテム）を受け取る親機側の最大許容レイテンシ（秒）。" +
             "0以下で無効。既存の親イベント動作は変更しない。")]
    [SerializeField, Min(0f)]
    private float choreNotificationTimeout = 5f;

    // ── 公開状態 ──────────────────────────────────────────────────────────────
    public bool isCaught;
    public bool isMotherLookingNow;

    /// <summary>片付け演出による視線（MotherChoreController から設定される）。</summary>
    private bool _choreLooking;

    /// <summary>片付け中フラグ（SetChoreOverride）。true の間は通常の親イベント入口を抑止する。</summary>
    private bool _choreOverride;

    /// <summary>片付けの「行き」の歩行中に怪しさを加算するコルーチン（既存の継続疑惑と同じ仕組み）。</summary>
    private Coroutine _choreSuspicionCoroutine;
    /// <summary>片付けのドア開け（Door_Open）中、歩きアニメーションの上書きを抑止しているか。</summary>
    private bool _choreWalkingOverrideSuppressed;
    /// <summary>片付けのドア開け（Door_Open）中、歩行による位置移動を止めているか。</summary>
    private bool _choreMovementSuppressed;

    // ── テスト用無敵モード（Lキー） ───────────────────────────────────────────
    //   怪しさ・発見判定・段階別設定をまとめる本クラス（＝怪しさ管理元）が、
    //   無敵の状態と判定APIも一元管理する。
    [Header("テスト用無敵モード")]
    [Tooltip("Lキーによる無敵モード切り替えを有効にする（テスト用デバッグ機能）。")]
    [SerializeField] private bool enableInvincibleToggle = true;

    [Tooltip("無敵状態を画面隅に表示する。")]
    [SerializeField] private bool showInvincibleIndicator = true;

    [Tooltip("無敵表示の位置（左上からのピクセル）。")]
    [SerializeField] private Vector2 invincibleIndicatorPosition = new Vector2(12f, 12f);

    [Tooltip("無敵表示の文字サイズ。")]
    [SerializeField] private int invincibleIndicatorFontSize = 22;

    /// <summary>無敵モードが有効か（怪しさの加算・母親の捕獲を抑止する）。</summary>
    public bool IsInvincible { get; private set; }

    /// <summary>ON/OFFが切り替わったときに発火する。</summary>
    public event System.Action<bool> InvincibleChanged;

    /// <summary>
    /// このプレイで無敵を一度でもONにしたか。
    /// CaughtReactionController のゲージ最大監視が「無敵OFF直後の翌フレームに、
    /// 最大ゲージだけを根拠として捕獲する」ことを防ぐために参照する。
    /// </summary>
    public bool WasInvincibleUsedInThisPlay { get; private set; }

    private GUIStyle _invincibleStyle;
    private bool _invincibleStyleReady;

    /// <summary>子機から届いた進行率（0〜1）。親機側の経過率と併用する。</summary>
    private float _childProgressRate;

    /// <summary>最後に片付け通知を受け取った時刻（Time.time）。</summary>
    private float _lastChoreNotificationTime = -9999f;

    // ── 非公開状態 ────────────────────────────────────────────────────────────
    private Coroutine _dummyResetCoroutine;
    private Coroutine _primaryResetCoroutine;
    private Coroutine _continuousRoomCoroutine;
    private Coroutine _rushInPeekCoroutine;
    private bool _hasPermanentGameOver;
    private float _activePeekDuration = 3f;

    // ── 部屋入室（案B）：疑惑開始を「部屋入室完了」にずらすための状態 ────────────
    private bool _roomEntryAccepted; // このサイクルでRequestRoomEntry()が受理されたか
    private bool _roomEntryStarted; // OnEnteredRoom後に疑惑コルーチンを開始済みか
    private bool _roomExitCompleted; // OnExitedRoomを受信済みか
    private bool _approachEventsSubscribed; // MotherApproachControllerの入退室イベントを購読中か
    private int _roomCycleId; // OnApproachReachedDoorのたびに増えるサイクル識別子
    private int _primaryResetCycleId; // HandlePrimaryResetSequence開始時点の_roomCycleId
    private float _approachSpeed = 1.5f;
    private bool _animatorWarningLogged;
    public float CurrentApproachSpeed => _approachSpeed;

    // ──────────────────────────────────────────────────────────────────────────
    //  Unityライフサイクル
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        isCaught = false;
        isMotherLookingNow = false;
        _choreLooking = false;
        _choreOverride = false;

        // 再プレイ時は無敵モードを必ずOFFに戻す（状態を持ち越さない）。
        IsInvincible = false;
        WasInvincibleUsedInThisPlay = false;

        if (motherChoreController == null)
            motherChoreController = Object.FindFirstObjectByType<MotherChoreController>();

        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        if (motherGauge != null)
            motherGauge.enableAutoDecrease = true;

        if (targetDoorController == null)
            targetDoorController = Object.FindFirstObjectByType<DoorController>();

        if (warningSystem == null)
            warningSystem = Object.FindFirstObjectByType<MotherApproachWarning>();

        if (caughtReactionController == null)
            caughtReactionController = Object.FindFirstObjectByType<CaughtReactionController>();

        if (approachController == null)
            approachController = Object.FindFirstObjectByType<MotherApproachController>();

        if (sleepingController == null)
            sleepingController = Object.FindFirstObjectByType<SleepingController>();

        // Animatorの特定は MotherApproachController に一元化する（複数スクリプトが別々に探して
        // 別のAnimatorを掴むことを防ぐ）。取得できない場合は Controller 側が理由つきで警告する。
        if (motherAnimator == null && approachController != null)
            motherAnimator = approachController.MotherAnimator;

        // 廊下ルートの移行判定を、経路を使うどの処理よりも先に確定させる。
        // 母子で共有するルート設定を、Start実行順に依存させないための前倒し。
        if (approachController != null)
            approachController.MigrateLegacyHallwayPoints();

        _approachSpeed = initialApproachSpeed;
        if (approachController != null)
        {
            approachController.MovementStateChanged += HandleWalkingStateChanged;
            HandleWalkingStateChanged(false);
        }

        // 部屋入室（案B）：入室完了／退室完了の通知を受け取る。
        // approachControllerはコード上で解決するため、OnEnable()ではなくStart()末尾で購読する。
        SubscribeApproachEvents();
    }

    private void OnDestroy()
    {
        // 偽ドア音の予約は MotherApproachWarning 側が管理する（ここでは触らない）。
        if (approachController != null)
        {
            approachController.MovementStateChanged -= HandleWalkingStateChanged;
        }

        UnsubscribeApproachEvents();
    }

    public void SetApproachSpeed(float speed)
    {
        _approachSpeed = Mathf.Max(0f, speed);
    }

    public void SetLoudItemRushInSpeed(float speed)
    {
        SetApproachSpeed(speed);
    }

    public void ApplyApproachSpeed(bool isManual)
    {
        float speed;
        if (isManual && useFixedDebugApproachSpeed)
        {
            speed = fixedDebugApproachSpeed;
            Debug.Log($"[MotherSuspicionSystem] APPROACH SPEED: {speed:F2} units/sec (FIXED DEBUG)");
        }
        else if (!isManual && motherGauge != null &&
                 motherGauge.currentGauge > highSuspicionSpeedGaugeThreshold)
        {
            speed = UnityEngine.Random.Range(
                highSuspicionApproachMoveSpeedMin, highSuspicionApproachMoveSpeedMax);
            Debug.Log($"[MotherSuspicionSystem] APPROACH SPEED: {speed:F2} units/sec (HIGH SUSPICION RANGE)");
        }
        else
        {
            speed = UnityEngine.Random.Range(approachMoveSpeedMin, approachMoveSpeedMax);
            float suspicionFraction = motherGauge == null || motherGauge.maxGauge <= 0
                ? 0f
                : Mathf.Clamp01((float)motherGauge.currentGauge / motherGauge.maxGauge);
            if (!isManual && approachSpeedSuspicionBonus > 0f)
                speed += approachSpeedSuspicionBonus * suspicionFraction;
            Debug.Log(
                $"[MotherSuspicionSystem] APPROACH SPEED: {speed:F2} units/sec (NORMAL, suspicion={suspicionFraction:F2})");
        }

        SetApproachSpeed(speed);
    }

    private void HandleWalkingStateChanged(bool isWalking)
    {
        // アニメーション操作は MotherAnimationPlayer に集約する（Animator を直接操作しない）。
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        if (player == null)
        {
            LogAnimatorWarning();
            return;
        }

        // 覗き再生中（ドア覗き／庭覗き）は、アニメーションの担当を覗き側に委ねる。
        // ここで Walk を書き換えると再生直後の覗きが歩行に戻されてしまうため、停止中として扱う。
        player.SetWalking(isWalking && !IsPeekAnimationActive());
    }

    /// <summary>
    /// 歩行アニメーションを書き換えてはいけない状態か。
    /// ドア覗き（isMotherLookingNow）・庭覗き（approachController.IsGardenPeeking）・
    /// 片付け中（SetChoreOverride）・片付けのドア開け中（Door_Open の上書き抑止）を対象にする。
    /// </summary>
    private bool IsPeekAnimationActive()
    {
        if (isMotherLookingNow) return true;

        // 片付け中は演技／経路がアニメーションを担当する（Walk を書き換えない）。
        if (_choreOverride) return true;

        // Door_Open 中はモデルの再生を守る（位置移動は経路側が止める）。
        if (_choreWalkingOverrideSuppressed) return true;

        return approachController != null && approachController.IsGardenPeeking;
    }

    /// <summary>
    /// 歩行アニメーションの状態を、現在の覗き状態を考慮して今すぐ反映し直す。
    /// 覗き開始・終了の直後に呼び、Walkの取り違えが残らないようにする。
    /// </summary>
    private void RefreshWalkingAnimationState()
    {
        // アニメーション操作は MotherAnimationPlayer に集約する（Animator を直接操作しない）。
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        if (player == null) return;

        bool isWalking = approachController != null && approachController.IsApproaching;
        player.SetWalking(isWalking && !IsPeekAnimationActive());
    }

    private void TriggerPeekAnimation(string triggerName)
    {
        // 通常の覗き（Peek_Door / Peek_Windows）の Trigger 発火もプレイヤー経由にする。
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        if (player == null)
        {
            LogAnimatorWarning();
            return;
        }

        player.FirePeekTrigger(triggerName);
    }

    private bool HasAnimatorParameter(string parameterName, AnimatorControllerParameterType parameterType)
    {
        if (motherAnimator == null) return false;
        foreach (AnimatorControllerParameter parameter in motherAnimator.parameters)
        {
            if (parameter.name == parameterName && parameter.type == parameterType)
                return true;
        }

        return false;
    }

    private MotherAnimationPlayer ResolveAnimationPlayer()
    {
        // 明示参照が最優先。未設定ならシーンから自動検索する（Animator 操作の窓口を1つに保つ）。
        if (animationPlayer == null)
            animationPlayer = Object.FindFirstObjectByType<MotherAnimationPlayer>();

        return animationPlayer;
    }

    private void LogAnimatorWarning()
    {
        if (_animatorWarningLogged) return;
        _animatorWarningLogged = true;
        Debug.LogWarning(
            "[MotherSuspicionSystem] 母親AnimatorまたはWalk/Peekパラメータが未設定のため、アニメーション制御をスキップします。",
            this);
    }
    // ── 部屋入室（案B）：MotherApproachControllerイベントの購読 ────────────────

    private void SubscribeApproachEvents()
    {
        if (_approachEventsSubscribed || approachController == null) return;

        approachController.onEnteredRoom.AddListener(HandleEnteredRoom);
        approachController.onExitedRoom.AddListener(HandleExitedRoom);
        approachController.onGardenPeekStarted.AddListener(HandleGardenPeekStarted);

        _approachEventsSubscribed = true;
        Debug.Log(
            "[PD] Subscribed to MotherApproachController events (OnEnteredRoom/OnExitedRoom/OnGardenPeekStarted)");
    }

    private void UnsubscribeApproachEvents()
    {
        if (!_approachEventsSubscribed || approachController == null) return;

        approachController.onEnteredRoom.RemoveListener(HandleEnteredRoom);
        approachController.onExitedRoom.RemoveListener(HandleExitedRoom);
        approachController.onGardenPeekStarted.RemoveListener(HandleGardenPeekStarted);

        _approachEventsSubscribed = false;
    }

    /// <summary>
    /// MotherApproachControllerから、親機が部屋内部への移動を完了し入室したときに呼び出される。
    /// ここで初めて部屋侵入時の疑惑（バースト／継続疑惑／睡眠退出待ち）を開始する。
    /// </summary>
    private void HandleEnteredRoom()
    {
        Debug.Log(
            $"[PD] OnEnteredRoom | roomEntryAccepted={_roomEntryAccepted} roomEntryStarted={_roomEntryStarted} isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");

        // 片付け演出のサイクル中は、部屋入室時の疑惑を開始しない（重複防止）。
        if (IsChoreOverrideActive)
        {
            Debug.Log("[PD] OnEnteredRoom: ignored — 片付け演出のサイクル中");
            return;
        }

        if (isCaught || _hasPermanentGameOver) return;

        if (approachController != null && approachController.IsRushIn)
        {
            StartRushInPeek();
            return;
        }

        // 入室が受理されていないサイクル（ダミー／覗き／通過／突入など）では疑惑を開始しない。
        if (!_roomEntryAccepted)
        {
            Debug.Log("[PD] OnEnteredRoom: ignored — room entry was not accepted for this cycle");
            return;
        }

        if (_roomEntryStarted) return;
        _roomEntryStarted = true;

        StartPrimaryRoomSuspicion();
    }

    /// <summary>
    /// MotherApproachControllerから、親機が部屋内部から退室しdoorPointへ戻ったときに呼び出される。
    /// 退室順序の最終段（ドアを閉じる→ResetCycle→EndWarningSequence）をここで実行する。
    /// </summary>
    private void HandleExitedRoom()
    {
        Debug.Log(
            $"[PD] OnExitedRoom | roomEntryStarted={_roomEntryStarted} isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");
        _roomExitCompleted = true;

        // 片付け演出のサイクル中は、通常のサイクル終了処理を行わない（片付け側が管理する）。
        if (IsChoreOverrideActive)
        {
            Debug.Log("[PD] OnExitedRoom: ignored — 片付け演出のサイクル中");
            return;
        }

        // ゲームオーバー確定後はドア状態・疑惑状態を変更しない（従来のサイクル終了と同じ扱い）。
        if (isCaught || _hasPermanentGameOver) return;

        warningSystem?.PlayMainDoorClose();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();

        Debug.Log("[PD] OnExitedRoom: cycle finished — door closed, warning sequence ended");
    }


    private void Update()
    {
        // 足音の更新は MotherApproachWarning.UpdateMotherFootsteps が担当する（移管済み）。

        // 覗き機能削除に伴い、覗き見による即時ゲームオーバー判定は廃止する。

        // 子機からの進行率通知がない環境（片付け通知を受けていない）でも、
        // 親機側の経過率で片付け開始を判断できるようにする。
        if (motherChoreController == null)
            motherChoreController = Object.FindFirstObjectByType<MotherChoreController>();

        if (motherChoreController != null && warningSystem != null &&
            Time.time - _lastChoreNotificationTime > choreNotificationTimeout)
        {
            motherChoreController.NotifyGameProgress(warningSystem.GameplayProgressRate);
        }

        if (Keyboard.current == null) return;

        if (Keyboard.current.digit0Key.wasPressedThisFrame)
        {
            Debug.Log("[PD] 0 key — triggering loud item (rush-in)");
            OnLoudItemTriggered();
        }

        // Lキー：テスト用無敵モードのトグル（既存のデバッグ入力に集約）。
        HandleInvincibleInput();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  公開API — MotherApproachWarningから呼び出される
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MotherApproachWarningから、親機がドアで停止したときに呼び出される。
    /// warningSystem.ActiveRouteで分岐する — ルートは移動開始前に決定済み。
    /// ActiveRouteがNoneの場合（例：Pキーのデバッグ）のみdummyProbabilityにフォールバックする。
    /// </summary>
    public void OnApproachReachedDoor()
    {
        Debug.Log($"[PD] OnApproachReachedDoor | isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");

        // 片付け演出のサイクル中は通常のドア分岐・疑惑・捕獲を行わない（重複防止）。
        if (IsChoreOverrideActive)
        {
            Debug.Log("[PD] OnApproachReachedDoor: ignored — 片付け演出のサイクル中");
            return;
        }

        if (isCaught || _hasPermanentGameOver) return;

        // 片付け演出のサイクル中は通常のドア停止フローを開始しない（重複防止）。
        if (IsChoreOverrideActive)
        {
            Debug.Log("[PD] ApproachReachedDoor entry skipped — 片付け演出のサイクル中");
            return;
        }

        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
        _activePeekDuration = peekDurationBase + gauge;
        Debug.Log($"[PD] activePeekDuration={_activePeekDuration:F1}s (base={peekDurationBase:F1} + gauge={gauge})");

        bool primary;
        var route = (warningSystem != null) ? warningSystem.ActiveRoute : MotherApproachWarning.RouteState.None;

        if (route == MotherApproachWarning.RouteState.DoorPeek)
        {
            primary = true;
            Debug.Log("[PD] Branch: PRIMARY — from ActiveRoute=DoorPeek");
        }
        else if (route == MotherApproachWarning.RouteState.HallwayPassBy)
        {
            primary = false;
            Debug.LogWarning("[PD] WARNING: OnApproachReachedDoor was called on a non-door route");
        }
        else
        {
            bool isDummy = Random.value < dummyProbability;
            primary = !isDummy;
            Debug.Log($"[PD] Branch: fallback random isDummy={isDummy} (dummyProbability={dummyProbability:F2})");
        }

        // 部屋入室（案B）のサイクル状態を初期化する（前サイクルの状態を持ち越さない）。
        // 新しいサイクルが始まったことを、進行中のHandlePrimaryResetSequenceにも伝える。
        _roomCycleId++;
        _roomEntryAccepted = false;
        _roomEntryStarted = false;
        _roomExitCompleted = false;

        // Primaryの場合のみ、親機へ部屋入室を要求する。
        // 受理された場合は、疑惑（バースト／継続疑惑）の開始を「部屋入室完了（OnEnteredRoom）」まで遅らせる。
        // 受理されなかった場合は、従来どおりドア停止時点で疑惑を開始する。
        if (primary && approachController != null)
            _roomEntryAccepted = approachController.RequestRoomEntry();

        TriggerFinalEvent(primary: primary);
    }

    /// <summary>
    /// MotherApproachWarningから、親機が停止せず通過したときに呼び出される。
    /// 疑惑増加やドアイベントを発生させず、サイクルを正常にリセットする。
    /// </summary>
    public void OnApproachPassedBy()
    {
        Debug.Log($"[PD] OnApproachPassedBy | isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");

        // 片付け演出のサイクル中は通常のサイクルリセットを行わない（片付け側が管理する）。
        if (IsChoreOverrideActive)
        {
            Debug.Log("[PD] OnApproachPassedBy: ignored — 片付け演出のサイクル中");
            return;
        }

        if (isCaught || _hasPermanentGameOver) return;

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();
    }

    public void NotifyGameOver()
    {
        warningSystem?.CancelPassByDoorSound();
        _hasPermanentGameOver = true;
        // 片付けの歩行加算も確実に停止する（ゲームオーバー中に加算が残らない）。
        DisableChoreSuspicion();
        if (warningSystem != null)
            warningSystem.NotifyGameOver();

        // 片付け演出を中断する（既存のゲームオーバー処理には介入しない）。
        if (motherChoreController != null)
            motherChoreController.AbortChore("ゲームオーバー");
    }

    private void StartRushInPeek()
    {
        Debug.Log($"[PD] RushIn reached door — starting dedicated peek for {rushInPeekDurationSeconds:F1}s");

        _roomCycleId++;
        _roomEntryAccepted = false;
        _roomEntryStarted = false;
        _roomExitCompleted = false;
        isMotherLookingNow = true;

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Peek);
        if (approachController != null)
            TriggerPeekAnimation("Peek_Door");

        // 覗きの再生を確定させてから、歩行状態を反映し直す（Walkで上書きさせない）。
        RefreshWalkingAnimationState();

        warningSystem?.PlayMainDoorOpen();
        if (caughtReactionController != null)
            caughtReactionController.OnMotherCheck(isFullCheck: false);

        bool sleeping = sleepingController != null && sleepingController.IsSleeping;
        if (!sleeping && motherGauge != null)
            StartCoroutine(RoomEntryBurstSuspicionCoroutine());
        else
            Debug.Log("[PD] RushIn suspicion burst skipped — player is sleeping or gauge is unavailable");

        if (_rushInPeekCoroutine != null)
            StopCoroutine(_rushInPeekCoroutine);
        _rushInPeekCoroutine = StartCoroutine(RushInPeekCoroutine());
    }

    private IEnumerator RushInPeekCoroutine()
    {
        yield return new WaitForSeconds(Mathf.Max(0f, rushInPeekDurationSeconds));

        if (_hasPermanentGameOver || isCaught)
        {
            _rushInPeekCoroutine = null;
            yield break;
        }

        warningSystem?.PlayMainDoorClose();
        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        ResetCycle();
        if (warningSystem != null)
            warningSystem.EndWarningSequence();
        _rushInPeekCoroutine = null;
    }

    /// <summary>
    /// 子機の大きな音のアイテムが発生したとき（または0キーのデバッグ時）に呼び出される。
    /// 突入音を再生し、ゲージを加算してからMotherApproachWarning.StartLoudItemRushInSequence()へ引き渡す。
    /// 警告シーケンスがすでに進行中の場合は完全に抑制する。
    /// </summary>
    public void OnLoudItemTriggered()
    {
        if (!enableLoudItemFeature)
        {
            Debug.Log("[PD] Loud Item Feature is DISABLED");
            return;
        }

        if (isCaught || _hasPermanentGameOver) return;

        if (warningSystem != null && warningSystem.isWarningActive)
        {
            if (!forceLoudItemDuringWarning)
            {
                Debug.Log("[PD] Loud item ignored — warning sequence already active");
                return;
            }

            Debug.Log("[PD] Loud item overriding active warning — forcing rush-in");
            warningSystem.StopWarningSequence();
        }

        Debug.Log($"[PD] Loud item triggered rush-in request — adding {loudItemGaugeAmount} gauge stages");

        warningSystem?.PlayRushIn();

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
    //  最終イベント分岐
    // ──────────────────────────────────────────────────────────────────────────

    private void TriggerFinalEvent(bool primary)
    {
        if (primary) TriggerPrimaryEvent();
        else TriggerDummyEvent();
    }

    private void TriggerPrimaryEvent()
    {
        Debug.Log("[PD] TriggerPrimaryEvent — door FULL open, isMotherLookingNow=true");
        isMotherLookingNow = true;

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Peek);

        if (approachController != null)
            TriggerPeekAnimation("Peek_Door");

        // 覗きの再生を確定させてから、歩行状態を反映し直す（Walkで上書きさせない）。
        RefreshWalkingAnimationState();

        warningSystem?.PlayMainDoorOpen();

        if (caughtReactionController != null)
            caughtReactionController.OnMotherCheck(isFullCheck: true);

        // 部屋入室（案B）：入室が受理された場合は、入室完了（OnEnteredRoom）まで疑惑を開始しない。
        if (_roomEntryAccepted)
        {
            Debug.Log(
                "[PD] Room entry accepted — suspicion burst/continuous will start on OnEnteredRoom (mother is walking into the room)");
            return;
        }

        StartPrimaryRoomSuspicion();
    }

    /// <summary>
    /// 部屋侵入時の疑惑（3回のバースト／継続疑惑／睡眠退出待ち）を開始する。
    /// 従来はドア停止時に呼ばれていたが、部屋入室が受理された場合は入室完了時（OnEnteredRoom）に呼ばれる。
    /// 加算量・間隔・ゲームオーバー条件は従来と同じ。
    /// </summary>
    private void StartPrimaryRoomSuspicion()
    {
        // 部屋侵入時の疑惑：プレイヤーが睡眠中でない場合にゲージを増加させる。
        bool playerIsSleeping = (sleepingController != null) && sleepingController.IsSleeping;
        int gaugeBefore = (motherGauge != null) ? motherGauge.currentGauge : 0;
        Debug.Log(
            $"[PD] Room entry | isMotherLookingNow=true | IsSleeping={playerIsSleeping} | gauge before={gaugeBefore}");

        if (!playerIsSleeping && motherGauge != null)
        {
            Debug.Log($"[PD] Room entry suspicion burst starting | count=3 interval={roomEntryBurstTickInterval}s");
            StartCoroutine(RoomEntryBurstSuspicionCoroutine());
        }
        else
        {
            Debug.Log("[PD] Room entry suspicion SKIPPED — player is sleeping");
        }

        if (!_hasPermanentGameOver)
        {
            if (_primaryResetCoroutine != null) StopCoroutine(_primaryResetCoroutine);
            _primaryResetCoroutine = StartCoroutine(HandlePrimaryResetSequence());
        }

        if (enableContinuousRoomSuspicion && !_hasPermanentGameOver)
        {
            if (_continuousRoomCoroutine != null) StopCoroutine(_continuousRoomCoroutine);
            _continuousRoomCoroutine = StartCoroutine(ContinuousRoomSuspicionCoroutine());
            Debug.Log("[PD] Continuous room suspicion started");
        }
    }

    private IEnumerator HandlePrimaryResetSequence()
    {
        Debug.Log("[PD] HandlePrimaryResetSequence: waiting for player sleep");

        // このシーケンスが担当するサイクルを記録する。
        // 別のサイクル（強制突入など）が始まった場合は、以降のドア操作をそのサイクルへ譲る。
        _primaryResetCycleId = _roomCycleId;

        float elapsed = 0f;
        float timeout = Mathf.Max(0f, roomCheckSafetyTimeout);

        while (true)
        {
            if (_hasPermanentGameOver || isCaught)
            {
                _primaryResetCoroutine = null;
                yield break;
            }

            if (_roomCycleId != _primaryResetCycleId)
            {
                Debug.Log("[PD] HandlePrimaryResetSequence: a newer cycle has started — aborting");
                _primaryResetCoroutine = null;
                yield break;
            }

            // 部屋からの退室が既に完了している場合（親機側の滞在タイムアウト等）は、
            // ドア閉・ResetCycle・EndWarningSequenceはOnExitedRoom側で完了済みなので打ち切る。
            if (_roomExitCompleted)
            {
                Debug.Log(
                    "[PD] HandlePrimaryResetSequence: room exit already completed — aborting (finished by OnExitedRoom)");
                _primaryResetCoroutine = null;
                yield break;
            }

            bool sleeping = (sleepingController != null) && sleepingController.IsSleeping;
            if (sleeping)
            {
                Debug.Log("[PD] player fell asleep — mother will leave");
                break;
            }

            if (timeout > 0f)
            {
                elapsed += Time.deltaTime;
                if (elapsed >= timeout)
                {
                    Debug.Log($"[PD] safety timeout reached after {timeout:F1}s — mother leaving anyway");
                    break;
                }
            }

            yield return null;
        }

        float suspicionFraction = (motherGauge != null && motherGauge.maxGauge > 0)
            ? Mathf.Clamp01((float)motherGauge.currentGauge / motherGauge.maxGauge)
            : 0f;
        float leaveDelay = Mathf.Max(0f, Mathf.Lerp(leaveAfterSleepDelay, leaveAfterSleepDelayMax, suspicionFraction));
        Debug.Log($"[PD] HandlePrimaryResetSequence: leave delay={leaveDelay:F1}s (suspicion={suspicionFraction:F2})");
        if (leaveDelay > 0f)
            yield return new WaitForSeconds(leaveDelay);

        if (_hasPermanentGameOver || isCaught)
        {
            _primaryResetCoroutine = null;
            yield break;
        }

        if (_roomCycleId != _primaryResetCycleId)
        {
            Debug.Log("[PD] HandlePrimaryResetSequence: a newer cycle has started — aborting");
            _primaryResetCoroutine = null;
            yield break;
        }

        if (_roomExitCompleted)
        {
            // 滞在タイムアウト等で先に退室が完了している — 終了処理はOnExitedRoom側で完了済み。
            Debug.Log(
                "[PD] HandlePrimaryResetSequence: room exit already completed — skipping door close (finished by OnExitedRoom)");
            _primaryResetCoroutine = null;
            yield break;
        }

        // 部屋入室（案B）：入室済みの場合は、親機へ退室を要求し、doorPointへ戻るまで待つ。
        // ドアを閉じる／ResetCycle／EndWarningSequenceは退室完了（OnExitedRoom）側で実行する。
        if (_roomEntryStarted && approachController != null)
        {
            if (approachController.RequestLeaveRoom())
            {
                Debug.Log("[PD] RequestLeaveRoom sent — waiting for the mother to leave the room");

                float exitElapsed = 0f;
                float exitTimeout = Mathf.Max(0f, roomExitSafetyTimeout);

                while (!_roomExitCompleted && !_hasPermanentGameOver && !isCaught
                       && _roomCycleId == _primaryResetCycleId)
                {
                    if (exitTimeout > 0f)
                    {
                        exitElapsed += Time.deltaTime;
                        if (exitElapsed >= exitTimeout)
                        {
                            Debug.LogWarning(
                                $"[PD] OnExitedRoom not received within {exitTimeout:F1}s — closing the door anyway");
                            break;
                        }
                    }

                    yield return null;
                }

                if (_hasPermanentGameOver || isCaught)
                {
                    _primaryResetCoroutine = null;
                    yield break;
                }

                if (_roomCycleId != _primaryResetCycleId)
                {
                    // 新しいサイクル（強制突入など）が始まった — ドア操作はそのサイクルに任せる。
                    Debug.Log("[PD] HandlePrimaryResetSequence: a newer cycle has started — aborting");
                    _primaryResetCoroutine = null;
                    yield break;
                }

                if (_roomExitCompleted)
                {
                    // 退室完了 — 終了処理はOnExitedRoom側で完了済み。
                    _primaryResetCoroutine = null;
                    yield break;
                }
            }
            else
            {
                Debug.LogWarning("[PD] RequestLeaveRoom was rejected — falling back to the legacy door close");
            }
        }

        warningSystem?.PlayMainDoorClose();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        // 【重要】ここで ResetCycle() を呼ばない。
        // ResetCycle() は _primaryResetCoroutine（＝このコルーチン自身）を StopCoroutine するため、
        // これを先に呼ぶと以降の帰路要求・待機が実行されずに打ち切られてしまう。
        // 発見判定の解除だけを先に行い、サイクル全体のリセットは帰路完了後に一度だけ行う。

        // 発見判定の解除（覗き終了）。戻っているだけなのに発見し続けないための最小限の解除。
        isMotherLookingNow = false;

        // 【帰路】ResetApproach で初期位置へ瞬間復帰させる前に、帰路（Turn Back → 帰路List → 画面外）を
        // MotherApproachController へ要求し、受付から完了／失敗まで待つ。
        // 受理されなかった場合（突入・入室・ゲームオーバー等）は従来どおり即時復帰する。
        if (approachController != null && approachController.RequestReturnHome())
        {
            Debug.Log("[PD] ReturnHome requested — waiting for the mother to walk back");

            float returnTimeout = Mathf.Max(1f, returnHomeSafetyTimeout);
            float returnElapsed = 0f;

            // 受付（Requested）から実行（Running）を経て完了／失敗になるまで待つ。
            // 開始前の false を完了と誤認しないよう IsReturnHomePending を見る。
            while (approachController.IsReturnHomePending && returnElapsed < returnTimeout)
            {
                if (_hasPermanentGameOver || isCaught)
                {
                    Debug.Log("[PD] ReturnHome aborted — game over");
                    approachController.AbortReturnHome("ゲームオーバー");
                    break;
                }

                returnElapsed += Time.deltaTime;
                yield return null;
            }

            if (returnElapsed >= returnTimeout && approachController.IsReturnHomePending)
            {
                // 上限超過：失敗として確定し、停止・非表示・後始末を実施させる。
                Debug.LogWarning(
                    $"[PD] ReturnHome did not finish within {returnTimeout:F1}s — aborting the return trip");
                approachController.AbortReturnHome("タイムアウト");
            }

            // 結果を1回だけ取り出す（次サイクルへ結果を持ち越さない）。
            if (approachController.TryConsumeReturnHomeResult(out bool returnOk))
                Debug.Log($"[PD] ReturnHome result = {(returnOk ? "Completed" : "Failed")}");
        }

        // 帰路の完了／失敗を確認した後で、サイクル終了処理を一度だけ行う。
        //
        // 【重要】ResetCycle() は _primaryResetCoroutine を StopCoroutine する。
        // ここを実行しているのはその _primaryResetCoroutine 自身（＝このコルーチン）なので、
        // 参照を持ったまま呼ぶと自分を停止してしまい、後続の EndWarningSequence() と
        // 参照解除まで到達しない可能性がある。
        // そこで「呼ぶ前に自分の参照を手放す」ことで、ResetCycle の停止対象から自分だけを外す。
        // 他の終了シーケンス（_dummyReset / _continuousRoom / _gardenPeekSuspicion / _rushInPeek）は
        // 従来どおり ResetCycle() が停止する（外部から呼ぶ場合の機能は維持される）。
        _primaryResetCoroutine = null;

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();

        // 自分の参照は既に手放しているため、ここで触る必要はない。
    }

    private void TriggerDummyEvent()
    {
        Debug.Log("[PD] TriggerDummyEvent — door PEEK open, isMotherLookingNow=false");
        isMotherLookingNow = false;

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Peek);

        if (approachController != null)
            TriggerPeekAnimation("Peek_Door");

        // 覗きの再生を確定させてから、歩行状態を反映し直す（Walkで上書きさせない）。
        RefreshWalkingAnimationState();

        warningSystem?.PlayDummyDoor();

        if (caughtReactionController != null)
            caughtReactionController.OnMotherCheck(isFullCheck: false);

        if (_dummyResetCoroutine != null) StopCoroutine(_dummyResetCoroutine);
        _dummyResetCoroutine = StartCoroutine(HandleDummySequence());
    }

    private IEnumerator HandleDummySequence()
    {
        Debug.Log($"[PD] HandleDummySequence: activePeekDuration={_activePeekDuration:F1}s");
        yield return new WaitForSeconds(_activePeekDuration);

        warningSystem?.PlayMainDoorClose();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();

        _dummyResetCoroutine = null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  猫フェイント（3キー専用・母親は移動しない）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 猫フェイント：猫だけを母親のドア側と同じwaypoint順でStartPoint→DoorPointへ移動させ、
    /// DoorPoint到着後にドアを隙間だけ開け（既存DoorController APIを再利用）、猫を見せて鳴き声を再生する。
    /// 母親は移動せず、母親のドア到着イベント・本チェック・疑惑加算・捕獲判定は発生しない。
    /// 鳴き声は猫専用のAudioSource（CatFeintController）から再生し、既存の親の音源やUDPには触れない。
    /// 覗き時間はドア覗き（ダミー）と同じ式 peekDurationBase+currentGauge。
    /// </summary>
    public bool TriggerCatFeintEvent()
    {
        if (_catFeintCoroutine != null)
        {
            Debug.Log("[PD] TriggerCatFeintEvent: 猫フェイント中のため無視");
            return false;
        }

        if (isCaught || _hasPermanentGameOver) return false;

        if (catFeintController == null)
        {
            Debug.LogWarning("[PD] catFeintControllerが未設定のため猫フェイントを開始しません。SceneでCatFeintControllerを割り当ててください。", this);
            return false;
        }

        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
        float duration = Mathf.Max(0f, peekDurationBase) + gauge;

        Debug.Log(
            $"[PD] TriggerCatFeintEvent — cat walks StartPoint→DoorPoint, then door PEEK open for {duration:F1}s（母親は登場しない）");
        _catFeintCoroutine = StartCoroutine(HandleCatFeintSequence(duration));
        return true;
    }

    /// <summary>
    /// 猫フェイントの本体。猫の移動 → ドア隙間開け＋鳴き声 → 待機 → 猫を隠す → ドアを閉める → 猫を開始状態へ戻す。
    /// 終了・中断（警告終了）どちらでも猫・ドア・警告状態を必ず戻す。
    /// </summary>
    private IEnumerator HandleCatFeintSequence(float duration)
    {
        // 2. 猫オブジェクトだけを母親と同じwaypoint順で移動させる（母親・母親イベントは発火しない）。
        yield return
            catFeintController.MoveAlongDoorRoute(() => warningSystem != null && warningSystem.isWarningActive);

        // 移動中に中断された場合は猫とドアを戻して終了する（警告状態は既に解除済み）。
        if (warningSystem == null || !warningSystem.isWarningActive)
        {
            Debug.Log("[PD] HandleCatFeintSequence: 中断検出 — 猫とドアを戻す");
            catFeintController.HideCat();
            catFeintController.ReturnToStartPosition();
            if (targetDoorController != null)
                targetDoorController.SetDoorState(DoorController.DoorState.Closed);
            _catFeintCoroutine = null;
            yield break;
        }

        // 3. DoorPoint到着後：ドアを全開にし、猫を見せて鳴き声を再生する。
        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Full);

        catFeintController.TriggerJump();
        catFeintController.PlayMeow();

        warningSystem?.PlayDummyDoor();

        // 4. 所定時間後に猫を隠し、ドアを閉める。中断があればループを抜けて後始末する。
        float catVisibleDuration = Mathf.Max(0f, duration - catFeintController.HideBeforeCloseSeconds);
        float elapsed = 0f;
        while (elapsed < catVisibleDuration && warningSystem != null && warningSystem.isWarningActive)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        catFeintController.HideCat();

        float hideBeforeClose = catFeintController.HideBeforeCloseSeconds;
        if (hideBeforeClose > 0f)
            yield return new WaitForSeconds(hideBeforeClose);

        warningSystem?.PlayMainDoorClose();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        // 5. 次回用に猫を開始状態へ戻す（ホーム位置へ戻して非表示）。
        catFeintController.ReturnToStartPosition();

        _catFeintCoroutine = null;

        // 既存の終了経路：ResetCycle（ドア状態・疑惑状態のリセット）→ EndWarningSequence（全灯消灯・状態リセット）。
        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  部屋侵入時の疑惑バースト
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator RoomEntryBurstSuspicionCoroutine()
    {
        for (int i = 0; i < 3; i++)
        {
            if (_hasPermanentGameOver || isCaught) yield break;
            if (motherGauge == null) yield break;

            motherGauge.AddGauge(1);
            Debug.Log($"[PD] Room entry burst tick {i + 1}/3 | +1 | gauge now {motherGauge.currentGauge}");

            if (motherGauge.currentGauge >= motherGauge.maxGauge)
            {
                Debug.Log("[PD] Room entry burst stopped — gauge reached max");
                OnPlayerCaught();
                yield break;
            }

            yield return new WaitForSeconds(roomEntryBurstTickInterval);
        }

        Debug.Log("[PD] Room entry burst complete");
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  部屋内の継続疑惑
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator ContinuousRoomSuspicionCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(continuousRoomSuspicionTickInterval);

            bool contSleeping = (sleepingController != null) && sleepingController.IsSleeping;
            int contGauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
            Debug.Log(
                $"[PD] Continuous room suspicion state | motherLooking={isMotherLookingNow} | playerSleeping={contSleeping} | gaugeBefore={contGauge}");

            if (_hasPermanentGameOver || isCaught)
            {
                Debug.Log("[PD] Continuous room suspicion stopped — game over or caught");
                yield break;
            }

            if (!isMotherLookingNow)
            {
                Debug.Log("[PD] Continuous room suspicion stopped — isMotherLookingNow is false");
                yield break;
            }

            if (motherGauge == null)
            {
                Debug.Log("[PD] Continuous room suspicion stopped — motherGauge is null");
                yield break;
            }

            bool sleeping = (sleepingController != null) && sleepingController.IsSleeping;
            if (sleeping)
            {
                Debug.Log("[PD] Continuous room suspicion skipped because player is sleeping");
                continue;
            }

            motherGauge.AddGauge(continuousRoomSuspicionAmount);
            Debug.Log(
                $"[PD] Continuous room suspicion tick +{continuousRoomSuspicionAmount} | gauge now {motherGauge.currentGauge}");

            if (motherGauge.currentGauge >= motherGauge.maxGauge)
            {
                Debug.Log("[PD] Continuous room suspicion stopped — gauge reached max");
                OnPlayerCaught();
                yield break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  庭覗き中の継続疑惑（GardenPeek専用）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 庭覗きの待機開始（MotherApproachController.onGardenPeekStarted）で継続疑惑を開始する。
    /// 加算量はドア側の継続疑惑と同じ（continuousRoomSuspicionAmount）。
    /// 加算間隔はドア側の間隔×gardenPeekSuspicionIntervalMultiplier（初期値1.5）。
    /// 倍率やドア側間隔が0以下の場合は異常な高速加算を避けるため、警告を出して無効化する。
    /// isMotherLookingNowは変更しない（ドア側の本チェック・他ルートに副作用を出さない）。
    /// </summary>
    private void HandleGardenPeekStarted()
    {
        if (_gardenPeekSuspicionCoroutine != null)
        {
            StopCoroutine(_gardenPeekSuspicionCoroutine);
            _gardenPeekSuspicionCoroutine = null;
        }

        if (_hasPermanentGameOver || isCaught) return;

        if (gardenPeekSuspicionIntervalMultiplier <= 0f || continuousRoomSuspicionTickInterval <= 0f)
        {
            Debug.LogWarning(
                $"[PD] 庭覗き中の継続疑惑を無効化 | gardenPeekSuspicionIntervalMultiplier={gardenPeekSuspicionIntervalMultiplier:F2} | continuousRoomSuspicionTickInterval={continuousRoomSuspicionTickInterval:F2}（0以下のため異常な高速加算を避ける）",
                this);
            return;
        }

        if (motherGauge == null)
        {
            Debug.LogWarning("[PD] 庭覗き中の継続疑惑を開始しません — motherGauge is NULL", this);
            return;
        }

        _gardenPeekSuspicionCoroutine = StartCoroutine(ContinuousGardenPeekSuspicionCoroutine());
    }

    /// <summary>
    /// 庭覗き中の継続疑惑。ドア側ContinuousRoomSuspicionCoroutineと同じ型：
    ///   tickごとに寝たふり中はスキップ、疑惑ゲージにcontinuousRoomSuspicionAmountを加算、
    ///   ゲージ最大で既存のOnPlayerCaught()（捕獲処理）を呼ぶ。
    /// 停止条件（いずれかを満たしたtickで、加算前に抜ける）：
    ///   覗き待機終了（IsGardenPeeking=false）／警告終了・中断（isWarningActive=false）／
    ///   ゲームオーバー・捕獲（isCaught/_hasPermanentGameOver）。
    /// isMotherLookingNowは一切変更しない。
    /// </summary>
    private IEnumerator ContinuousGardenPeekSuspicionCoroutine()
    {
        float tickInterval = continuousRoomSuspicionTickInterval * gardenPeekSuspicionIntervalMultiplier;
        Debug.Log(
            $"[PD] 庭覗き中の継続疑惑 開始 | tickInterval={tickInterval:F2}s | amount={continuousRoomSuspicionAmount}（ドア側{continuousRoomSuspicionTickInterval:F2}s×{gardenPeekSuspicionIntervalMultiplier:F2}）");

        while (true)
        {
            yield return new WaitForSeconds(tickInterval);

            // 停止条件は加算の前に判定する（覗き終了・中断・ゲームオーバー後に加算が残らない）。
            if (_hasPermanentGameOver || isCaught)
            {
                Debug.Log("[PD] 庭覗き中の継続疑惑 停止 — game over or caught");
                yield break;
            }

            if (warningSystem == null || !warningSystem.isWarningActive)
            {
                Debug.Log("[PD] 庭覗き中の継続疑惑 停止 — warning not active");
                yield break;
            }

            if (approachController == null || !approachController.IsGardenPeeking)
            {
                Debug.Log("[PD] 庭覗き中の継続疑惑 停止 — garden peek ended");
                yield break;
            }

            if (motherGauge == null)
            {
                Debug.Log("[PD] 庭覗き中の継続疑惑 停止 — motherGauge is null");
                yield break;
            }

            bool peekSleeping = (sleepingController != null) && sleepingController.IsSleeping;
            Debug.Log($"[PD] 庭覗き中の継続疑惑 state | playerSleeping={peekSleeping} | gaugeBefore={motherGauge.currentGauge}");

            if (peekSleeping)
            {
                Debug.Log("[PD] 庭覗き中の継続疑惑 スキップ — player is sleeping（ドア側と同じ）");
                continue;
            }

            motherGauge.AddGauge(continuousRoomSuspicionAmount);
            Debug.Log($"[PD] 庭覗き中の継続疑惑 tick +{continuousRoomSuspicionAmount} | gauge now {motherGauge.currentGauge}");

            if (motherGauge.currentGauge >= motherGauge.maxGauge)
            {
                Debug.Log("[PD] 庭覗き中の継続疑惑 停止 — gauge reached max（既存の捕獲処理へ）");
                OnPlayerCaught();
                yield break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  廊下からの覗き見疑惑
    // ──────────────────────────────────────────────────────────────────────────

    public void OnApproachStarted()
    {
        warningSystem?.CancelPassByDoorSound();

        // 片付け演出のサイクル中は通常の接近開始処理を行わない（重複防止）。
        if (IsChoreOverrideActive)
            return;
    }

    /// <summary>
    /// 廊下通過完了後の偽ドア音は MotherApproachWarning.PlayPassByDoorSound へ移管した。
    /// 呼び出し元（MotherApproachWarning）は自クラスのメソッドを直接使用する。
    /// </summary>
    private void NoopPassByDoorSoundRemoved()
    {
        // 移管済みのため、ここに再生処理は持たない。
    }


    // ── 片付け演出（MotherChoreController との連携） ─────────────────────────
    //   ・SetChoreOverride(true) 中は通常の親イベント入口を止める（片付けと重複させない）。
    //   ・SetChoreLooking() は視線アニメ中の発見判定を On/Off する（見た目とは別管理）。
    //   ・OnBadItemCollected() / OnGameProgress() は子機からの通知を片付け側へ渡す。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>通常の親イベントが有効か（片付け演出中は false）。片付け側が判定に使う。</summary>
    public bool IsNormalEventsEnabled => !_choreOverride;

    /// <summary>片付け演出による抑止が有効か（通常の親イベントを止める期間）。</summary>
    public bool IsChoreOverrideActive => _choreOverride;

    /// <summary>
    /// 片付けの「行き」の歩行中に怪しさを加算しているか（読み取り専用）。
    /// 視線（3）による加算とは排他で、二重に加算しない。
    /// </summary>
    public bool IsChoreSuspicionActive => _choreSuspicionCoroutine != null;

    /// <summary>片付け演出の抑止を設定する。母機の片付けサイクル開始／終了時に呼ばれる。</summary>
    public void SetChoreOverride(bool active)
    {
        if (_choreOverride == active) return;
        _choreOverride = active;
        Debug.Log($"[PD] SetChoreOverride({active}) — 通常の親イベントを{(active ? "停止" : "再開")}");

        if (!active)
        {
            // 抑止解除時に視線・歩行上加算・片付けの歩き/移動抑止が残らないようにする。
            _choreLooking = false;
            isMotherLookingNow = false;
            _choreWalkingOverrideSuppressed = false;
            _choreMovementSuppressed = false;
            DisableChoreSuspicion();
        }
    }

    /// <summary>
    /// 片付け中の視線による発見判定を設定する。
    /// アニメーションの見た目（再生）とは独立に、開始・終了タイミングをここで管理する。
    /// 【重要】歩行による怪しさ加算（EnableChoreSuspicion 中）と二重に加算しないよう、
    ///         ここで視線を有効化するときは歩行加算を止める。
    /// </summary>
    public void SetChoreLooking(bool looking)
    {
        _choreLooking = looking;
        // 通常イベントの覗き判定と競合しないよう、片付け抑止中だけ反映する。
        if (_choreOverride)
            isMotherLookingNow = looking;

        // 視線（3）では既存の視線判定期間だけ加算する。
        // 歩行加算と二重にならないよう、視線の開始／終了で歩行加算の側を止める。
        if (looking)
            DisableChoreSuspicion();
    }

    /// <summary>
    /// 片付けのドア開け（Door_Open）中、歩きアニメーションの上書きを抑止する。
    /// 歩行の位置移動は MotherApproachController 側が止める。ここでは Walk の書き換えだけを止める
    /// （通常イベントのドア操作へ片付けの再生が遅れて干渉しないようにするため）。
    /// </summary>
    public void SetChoreWalkingOverrideSuppressed(bool suppressed)
    {
        _choreWalkingOverrideSuppressed = suppressed;
        Debug.Log($"[PD] 片付けの歩き上書き抑止: {suppressed}");
    }

    /// <summary>
    /// 片付けのドア開け（Door_Open）中、歩行による位置移動を止めているか。
    /// MotherApproachController の移動ループがこれを見て位置更新を止める
    /// （開け終わる前に母親が通り抜けないようにする）。
    /// </summary>
    public bool IsChoreMovementSuppressed => _choreMovementSuppressed;

    /// <summary>
    /// 片付けのドア開け（Door_Open）中の位置移動抑止を設定する。
    /// MotherApproachController 側から呼ばれる。
    /// </summary>
    public void SetChoreMovementSuppressed(bool suppressed)
    {
        _choreMovementSuppressed = suppressed;
        if (_choreOverride)
            Debug.Log($"[PD] 片付けの歩行位置移動抑止: {suppressed}");
    }
    /// <summary>
    /// 片付けの「行き」の怪しさ加算を開始する。
    /// プレイヤーがゲームを触っている（＝寝たふりでない）状態なら、既存の継続疑惑と同じ
    /// 加算量・加算間隔で怪しさを加算し、最大値で既存の捕獲処理（OnPlayerCaught）へ渡す。
    /// 歩行が終わったら必ず DisableChoreSuspicion() を呼ぶ。
    /// </summary>
    public void EnableChoreSuspicion()
    {
        if (_choreSuspicionCoroutine != null) return;
        _choreSuspicionCoroutine = StartCoroutine(ContinuousChoreSuspicionCoroutine());
        Debug.Log("[PD] 片付けの行き怪しさ加算を開始（既存の継続疑惑と同じ仕組み）");
    }

    /// <summary>
    /// 片付けの「行き」の怪しさ加算を停止する（帰り・演技中・中断・再プレイで呼ぶ）。
    /// 疑惑の減少は行わない（加算を止めるだけ）。
    /// </summary>
    public void DisableChoreSuspicion()
    {
        if (_choreSuspicionCoroutine == null) return;
        StopCoroutine(_choreSuspicionCoroutine);
        _choreSuspicionCoroutine = null;
        Debug.Log("[PD] 片付けの行き怪しさ加算を停止（減少処理は行わない）");
    }

    /// <summary>
    /// 片付けの「行き」の歩行中だけ加算する継続疑惑。
    ///   ・判定は既存と同じ（SleepingController.IsSleeping が false＝ゲームを触っている）
    ///   ・加算量・間隔は既存の継続疑惑と同じフィールド（continuousRoomSuspicionAmount /
    ///     continuousRoomSuspicionTickInterval）を使う
    ///   ・最大到達時は既存の捕獲処理 OnPlayerCaught() へ渡す
    ///   ・視線（3）の再生中（_choreLooking）は加算しない（視線側の期間に任せる＝二重加算しない）
    /// </summary>
    private IEnumerator ContinuousChoreSuspicionCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(continuousRoomSuspicionTickInterval);

            if (_hasPermanentGameOver || isCaught)
            {
                Debug.Log("[PD] 片付けの行き怪しさ加算 停止 — game over or caught");
                yield break;
            }

            if (motherGauge == null)
            {
                Debug.Log("[PD] 片付けの行き怪しさ加算 停止 — motherGauge is null");
                yield break;
            }

            // 視線（3）の期間は視線側の既存加算に任せる（歩行による二重加算をしない）。
            if (_choreLooking)
            {
                Debug.Log("[PD] 片付けの行き怪しさ加算 スキップ — 視線期間中（二重加算を防止）");
                continue;
            }

            bool sleeping = (sleepingController != null) && sleepingController.IsSleeping;
            if (sleeping)
            {
                Debug.Log("[PD] 片付けの行き怪しさ加算 スキップ — プレイヤーが寝たふり中");
                continue;
            }

            motherGauge.AddGauge(continuousRoomSuspicionAmount);
            Debug.Log($"[PD] 片付けの行き怪しさ加算 tick +{continuousRoomSuspicionAmount} | gauge now {motherGauge.currentGauge}");

            if (motherGauge.currentGauge >= motherGauge.maxGauge)
            {
                Debug.Log("[PD] 片付けの行き怪しさ加算 停止 — gauge reached max（既存の捕獲処理へ）");
                OnPlayerCaught();
                yield break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  テスト用無敵モード（Lキー）
    //   ・無敵中は「怪しさの正の加算」と「母親の捕獲・ゲームオーバー」を抑止する。
    //   ・加算は MotherGauge.AddGauge()、捕獲は OnPlayerCaught() / CaughtReactionController の
    //     共通入口でガードする（個別演出だけにチェックを足さない）。
    //   ・自然減少・母親の演出・時間切れクリアは抑止しない。
    //   ・既に成立したゲームオーバーをLキーで取り消すことはしない。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>無敵状態を設定する。ON/OFFのログと表示を更新する。</summary>
    public void SetInvincible(bool value, string reason = null)
    {
        if (IsInvincible == value) return;

        IsInvincible = value;

        // このプレイで無敵を使ったことを記録する（OFF後もゲージ最大監視を緩和するため）。
        if (value) WasInvincibleUsedInThisPlay = true;

        string suffix = string.IsNullOrEmpty(reason) ? "" : $"（{reason}）";
        Debug.Log($"[MotherSuspicion] テスト用無敵モード: {(value ? "ON" : "OFF")}{suffix}" +
                  (value ? " — 怪しさ加算と捕獲を抑止します" : " — 通常の加算・捕獲判定へ戻します"));

        InvincibleChanged?.Invoke(value);
    }

    /// <summary>ON/OFFを反転する（Lキーから呼ばれる）。</summary>
    public void ToggleInvincible()
    {
        SetInvincible(!IsInvincible, reason: "Lキー");
    }

    /// <summary>
    /// 「無敵を使った」記録をクリアする。ゲージが最大未満に戻った時点で呼ばれ、
    /// ゲージ最大監視を通常の即時捕獲へ戻す。
    /// </summary>
    public void ClearInvincibleUsage()
    {
        WasInvincibleUsedInThisPlay = false;
    }

    /// <summary>
    /// 無敵中に加算を抑止すべきか。加算の共通入口（MotherGauge.AddGauge）から参照する。
    /// 正の加算（増加）のみを対象にし、負の加算（自動減少）は抑止しない。
    /// </summary>
    public bool ShouldBlockPositiveGaugeChange(int amount)
    {
        return IsInvincible && amount > 0;
    }

    /// <summary>無敵中に捕獲・ゲームオーバーを抑止すべきか。捕獲の共通入口から参照する。</summary>
    public bool ShouldBlockCapture()
    {
        return IsInvincible;
    }

    /// <summary>
    /// Lキーで無敵をトグルする。既存のデバッグ入力（Update）から呼ぶ。
    /// wasPressedThisFrame を使うため、押しっぱなしで毎フレーム切り替わらない。
    /// </summary>
    private void HandleInvincibleInput()
    {
        if (!enableInvincibleToggle) return;
        if (Keyboard.current == null) return;

        if (Keyboard.current.lKey.wasPressedThisFrame)
        {
            ToggleInvincible();
        }
    }

    /// <summary>無敵状態を画面隅に表示する（Consoleだけでなく目視でも確認できるようにする）。</summary>
    private void OnGUI()
    {
        if (!showInvincibleIndicator) return;

        if (!_invincibleStyleReady)
        {
            _invincibleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = invincibleIndicatorFontSize,
                fontStyle = FontStyle.Bold,
            };
            _invincibleStyleReady = true;
        }

        _invincibleStyle.normal.textColor = IsInvincible ? Color.yellow : Color.gray;

        string text = IsInvincible ? "[L] 無敵モード ON" : "[L] 無敵モード OFF";
        GUI.Label(new Rect(invincibleIndicatorPosition.x, invincibleIndicatorPosition.y, 400f, 40f),
                  text, _invincibleStyle);
    }

    /// <summary>
    /// 6キー（片付け演出の手動テスト開始）の入口。
    /// 既存の 1〜5 キーと同じ方式で、デバッグ入力から呼ばれる。
    /// MotherChoreController へ要求を転送し、開始できたかを返す。
    /// </summary>
    public bool RequestManualChoreStart()
    {
        if (motherChoreController == null)
            motherChoreController = Object.FindFirstObjectByType<MotherChoreController>();

        if (motherChoreController == null)
        {
            Debug.LogWarning("[PD] 6キー：MotherChoreController が見つからないため片付けを開始できません");
            return false;
        }

        return motherChoreController.RequestManualStart();
    }

    /// <summary>
    /// 子機から届いた「悪いアイテム取得」通知。片付け中ループ中の視線抽選に使う。
    /// 視線抽選はMotherChoreController側（抽選条件・確率）で行う。
    /// </summary>
    public void OnBadItemCollected()
    {
        _lastChoreNotificationTime = Time.time;

        if (motherChoreController == null)
            motherChoreController = Object.FindFirstObjectByType<MotherChoreController>();

        if (motherChoreController == null)
        {
            Debug.Log("[PD] OnBadItemCollected: MotherChoreController が見つからないため無視");
            return;
        }

        motherChoreController.NotifyBadItemCollected();
    }

    /// <summary>
    /// 子機から届いたゲーム進行率（0〜1）。片付けの開始条件判定に使う。
    /// 親機側の経過率と併せて、常に大きい方を採用する。
    /// </summary>
    public void OnGameProgress(float progressRate)
    {
        _childProgressRate = Mathf.Clamp01(progressRate);
        _lastChoreNotificationTime = Time.time;

        if (motherChoreController == null)
            motherChoreController = Object.FindFirstObjectByType<MotherChoreController>();

        if (motherChoreController == null) return;

        // 親機側の経過率（MotherApproachWarning の _gameplayElapsedSeconds 相当）と併用する。
        float parentRate = 0f;
        if (warningSystem != null)
            parentRate = warningSystem.GameplayProgressRate;

        motherChoreController.NotifyGameProgress(Mathf.Max(_childProgressRate, parentRate));
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  サイクルリセット
    // ──────────────────────────────────────────────────────────────────────────

    private void ResetCycle()
    {
        Debug.Log("[PD] ResetCycle");
        SetApproachSpeed(approachMoveSpeedMin);

        if (_dummyResetCoroutine != null)
        {
            StopCoroutine(_dummyResetCoroutine);
            _dummyResetCoroutine = null;
        }

        if (_primaryResetCoroutine != null)
        {
            StopCoroutine(_primaryResetCoroutine);
            _primaryResetCoroutine = null;
        }

        if (_continuousRoomCoroutine != null)
        {
            StopCoroutine(_continuousRoomCoroutine);
            _continuousRoomCoroutine = null;
            Debug.Log("[PD] Continuous room suspicion stopped — ResetCycle");
        }

        if (_gardenPeekSuspicionCoroutine != null)
        {
            StopCoroutine(_gardenPeekSuspicionCoroutine);
            _gardenPeekSuspicionCoroutine = null;
            Debug.Log("[PD] 庭覗き中の継続疑惑 stopped — ResetCycle");
        }

        if (_rushInPeekCoroutine != null)
        {
            StopCoroutine(_rushInPeekCoroutine);
            _rushInPeekCoroutine = null;
        }

        isMotherLookingNow = false;
        _activePeekDuration = peekDurationBase;

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  ゲームオーバー
    // ──────────────────────────────────────────────────────────────────────────

    private void OnPlayerCaught()
    {
        // 【無敵モード】テスト用無敵（Lキー）がONの間は捕獲・ゲームオーバーを抑止する。
        //   最大ゲージ到達から呼ばれる唯一の捕獲入口のため、ここで一括ガードする。
        //   抑止中でも移動・アニメーション・覗き・片付け・音・照明は止めない。
        if (ShouldBlockCapture())
        {
            Debug.Log("[PD] OnPlayerCaught: 無敵モード中のため捕獲とゲームオーバーを抑止します");
            return;
        }

        Debug.Log("[PD] OnPlayerCaught — GAME OVER");
        warningSystem?.CancelPassByDoorSound();
        // 片付けの歩行加算も確実に停止する（捕獲後に加算が残らない）。
        DisableChoreSuspicion();
        isCaught = true;
        isMotherLookingNow = true;
        Debug.LogError("ゲームオーバー：母親に捕まりました！");

        // 片付け演出を中断する（既存の捕獲・ゲームオーバー処理はそのまま実行する）。
        if (motherChoreController != null)
            motherChoreController.AbortChore("捕獲");

        if (caughtReactionController != null)
            caughtReactionController.ForceGameOver();
    }
}