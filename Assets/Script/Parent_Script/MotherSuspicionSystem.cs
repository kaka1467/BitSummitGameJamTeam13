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

    [Tooltip("【片付け演出中】RushIn要求が発生したときの移動速度倍率。基準は「適用前の片付け通常速度」（固定値を直書きしない）。" +
             "片付け専用ルートは変更せず、速度だけをこの倍率にする。")]
    [SerializeField, Min(0f)]
    private float choreRushInSpeedMultiplier = 1.5f;

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

    // ── 片付け中の視線回数（怪しさ段階別の Min/Max） ──────────────────────────
    //   設定元は本クラスだけ。MotherChoreController は「片付けイベント開始時」に1回だけ取得し、
    //   その怪しさ段階の Min〜Max から目標回数を抽選して固定する（演技中に段階が変わっても変更しない）。
    //   【回数保証】MotherApproachWarning の滞在時間は上限ではなく目安。この回数を滞在時間内に
    //   終えられない場合は、時間を超えて完了してから Chore_End へ進む。
    [Header("片付け中の視線回数（怪しさ段階別）")]
    [Tooltip("怪しさ段階『低』と判定するゲージ上限（この値以下を低とする）。")]
    [SerializeField, Min(0)]
    private int choreLookLowGaugeMax = 3;

    [Tooltip("怪しさ段階『中』と判定するゲージ上限（『低』の上限超〜この値以下を中、超を高とする）。")]
    [SerializeField, Min(0)]
    private int choreLookMidGaugeMax = 6;

    [Tooltip("怪しさ『低』の片付け視線回数（最小）。1以上。")]
    [SerializeField, Min(1)]
    private int choreLookLowMin = 1;

    [Tooltip("怪しさ『低』の片付け視線回数（最大）。最小以上。")]
    [SerializeField, Min(1)]
    private int choreLookLowMax = 1;

    [Tooltip("怪しさ『中』の片付け視線回数（最小）。1以上。")]
    [SerializeField, Min(1)]
    private int choreLookMidMin = 1;

    [Tooltip("怪しさ『中』の片付け視線回数（最大）。最小以上。")]
    [SerializeField, Min(1)]
    private int choreLookMidMax = 2;

    [Tooltip("怪しさ『高』の片付け視線回数（最小）。1以上。")]
    [SerializeField, Min(1)]
    private int choreLookHighMin = 2;

    [Tooltip("怪しさ『高』の片付け視線回数（最大）。最小以上。")]
    [SerializeField, Min(1)]
    private int choreLookHighMax = 3;

    // ── 公開状態 ──────────────────────────────────────────────────────────────
    public bool isCaught;
    public bool isMotherLookingNow;

    /// <summary>片付け演出による視線（MotherChoreController から設定される）。</summary>
    private bool _choreLooking;

    /// <summary>片付け中フラグ（SetChoreOverride）。true の間は通常の親イベント入口を抑止する。</summary>
    private bool _choreOverride;

    /// <summary>
    /// 無敵モードで捕獲要求が抑止されたか（＝通常は捕獲で終わる覗きを、通常の終了処理へ進めるための印）。
    ///  ・isCaught / _hasPermanentGameOver とは別物。捕獲は実行していないので「捕獲済み」にはしない。
    ///  ・サイクル開始（OnApproachReachedDoor）と ResetCycle で必ず解除し、次イベントへ持ち越さない。
    /// </summary>
    private bool _captureSuppressedDuringPeek;

    /// <summary>片付けの「行き」の歩行中に怪しさを加算するコルーチン（既存の継続疑惑と同じ仕組み）。</summary>
    private Coroutine _choreSuspicionCoroutine;
    /// <summary>
    /// 片付けの視線（Chore_Peek）期間に怪しさを加算するコルーチン。
    /// 歩行加算とは排他（二重に加算しない）。
    /// </summary>
    private Coroutine _choreLookSuspicionCoroutine;
    /// <summary>
    /// 上のコルーチンが実走中か。
    /// 加算の途中で捕獲・中断が起きた場合でも、古いコルーチン／ハンドルを残さないために持つ。
    /// </summary>
    private bool _choreLookSuspicionRunning;
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

    // ── RushIn／片付けRushIn の速度上書き（基準を保存して正確に戻す） ────────────
    //  ・「戻すためだけに乱数を再抽選しない」ため、適用前の速度を保存して復元する。
    //  ・重複要求で倍率を累積させない（適用中は再適用しない）。
    //  ・速度の書き込み元はこのクラス（_approachSpeed）に集約したままにする。
    private bool _rushInSpeedActive;
    private float _rushInSpeedBase;
    private bool _choreRushInSpeedActive;
    private float _choreRushInSpeedBase;

    // ── LOUD_ITEM（子機の大きな音）の購読 ────────────────────────────────────
    //  ・受信後の反応（OnLoudItemTriggered）は本クラスが担当する。
    //    UDP側（ParentUdpSender）は「受信した」ことだけを通知する。
    //  ・購読は Host が保持する ParentNotificationHub の既存イベントを基準にする（旧窓口とは二重購読しない）。
    //  ・同じ通知で二重に反応しないよう、購読する実体は1つに限定する（_loudItemSubscriber）。
    private static MotherSuspicionSystem _loudItemSubscriber; // 二重発火防止：現在購読している実体
    private bool _loudItemSubscribed;
    private ParentNotificationHub _loudItemHub;                // 購読したHub（解除も同じ個体へ行う）

    // ── GAME_PROGRESS（子機のゲーム進行率）の購読 ────────────────────────────
    //  ・受信後の反応（OnGameProgress）は本クラスが担当する。
    //    UDP側（ParentUdpSender）は「受信した進行率」だけを通知する。
    //  ・購読は Host が保持する ParentNotificationHub の既存イベントを基準にする（旧窓口とは二重購読しない）。
    //  ・同じ通知を複数の実体が処理しないよう、購読する実体は1つに限定する（_gameProgressSubscriber）。
    //  ・LOUD_ITEM とは別の静的な担当スロットで管理する（互いのガードに影響しない）。
    private static MotherSuspicionSystem _gameProgressSubscriber; // 二重処理防止：現在購読している実体
    private bool _gameProgressSubscribed;
    private ParentNotificationHub _gameProgressHub;             // 購読したHub（解除も同じ個体へ行う）

    // ──────────────────────────────────────────────────────────────────────────
    //  Unityライフサイクル
    // ──────────────────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        // LOUD_ITEM（子機の大きな音）の受信購読を張る。
        // ParentUdpSender.Instance がまだ無い場合は購読せず、Start/Update で再試行する。
        TrySubscribeLoudItem();

        // GAME_PROGRESS（子機のゲーム進行率）の受信購読を張る（同じ再試行方式）。
        TrySubscribeGameProgress();
    }

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
        // 再プレイで RushIn の速度上書き状態を持ち越さない。
        ClearRushInSpeedState();
        if (approachController != null)
        {
            approachController.MovementStateChanged += HandleWalkingStateChanged;
            HandleWalkingStateChanged(false);
        }

        // 部屋入室（案B）：入室完了／退室完了の通知を受け取る。
        // approachControllerはコード上で解決するため、OnEnable()ではなくStart()末尾で購読する。
        SubscribeApproachEvents();

        // LOUD_ITEM の購読（OnEnable の時点で Instance が未生成だった場合の保険。取得は再試行できる）。
        TrySubscribeLoudItem();

        // GAME_PROGRESS の購読も同様に保険を張る（再試行は Update）。
        TrySubscribeGameProgress();
    }

    /// <summary>
    /// コンポーネントが無効化されたときの後始末。
    ///  ・Unity は「コンポーネントの enabled=false」ではコルーチンを止めないため、
    ///    捕獲演出などで MotherSuspicionSystem が無効化されても視線加算が残らないよう明示的に停止する。
    ///    （GameObject の非アクティブ化・破棄ではコルーチン自体が止まるが、ハンドルはここで解除する）
    ///  ・_choreOverride は触らない（片付け側の解除処理 ReleaseChoreLocks→SetChoreOverride(false) が
    ///    歩行加算の停止などを行うため、ここで先に false にすると抑止解除の後始末が走らなくなる）。
    /// </summary>
    private void OnDisable()
    {
        // LOUD_ITEM の購読を外す（捕獲演出などで enabled=false になった時点から通知を受けない）。
        UnsubscribeLoudItem();

        // GAME_PROGRESS の購読も同じ理由で外す。
        UnsubscribeGameProgress();

        StopChoreLookSuspicion();
        _choreLooking = false;
    }

    private void OnDestroy()
    {
        // 偽ドア音の予約は MotherApproachWarning 側が管理する（ここでは触らない）。
        if (approachController != null)
        {
            approachController.MovementStateChanged -= HandleWalkingStateChanged;
        }

        UnsubscribeApproachEvents();

        // 片付け視線の加算コルーチンは、このオブジェクトの破棄で自動停止する。
        // 破棄後に古いハンドル／走行フラグが残らないようにここで解除する
        // （破棄後の StopCoroutine は呼ばない）。
        _choreLookSuspicionCoroutine = null;
        _choreLookSuspicionRunning = false;

        // LOUD_ITEM の購読も外す（破棄後に通知を受けない）。
        UnsubscribeLoudItem();

        // GAME_PROGRESS の購読も外す（破棄後に通知を受けない）。
        UnsubscribeGameProgress();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  LOUD_ITEM（子機の大きな音）の受信購読
    //  ・受信後の反応は本クラスが担当し、UDP側（ParentUdpSender）は受信を通知するだけ。
    //  ・購読は Host が保持する ParentNotificationHub の既存イベントを基準にする（旧窓口とは二重購読しない）。
    //  ・同じ通知で二重に反応しないよう、購読する実体は1つに限定する。
    //  ・enabled=false（捕獲演出など）で購読を外し、再有効化で購読を再開する。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Host が保持する ParentNotificationHub の LoudItemReceived を購読する。
    /// Host が未稼働の場合は購読せず false を返す（OnEnable/Start/Update で再試行する）。
    /// </summary>
    private bool TrySubscribeLoudItem()
    {
        if (_loudItemSubscribed) return true;
        if (!isActiveAndEnabled) return false;

        // 二重発火防止：先に購読している有効な実体が居る場合は購読しない。
        // （破棄済みの実体は Unity の null 判定で外れるため、その場合は引き継ぐ）
        if (_loudItemSubscriber != null && _loudItemSubscriber != this && _loudItemSubscriber._loudItemSubscribed)
        {
            Debug.LogWarning("[PD] LOUD_ITEM の購読は既に別の MotherSuspicionSystem が担当しています（二重発火防止のため購読しません）。", this);
            return false;
        }

        ParentUdpRuntimeHost host = ResolveParentHost();
        if (host == null || host.Notifications == null) return false;

        ParentNotificationHub hub = host.Notifications;
        _loudItemHub = hub;
        _loudItemHub.LoudItemReceived += HandleLoudItemReceived;
        _loudItemSubscribed = true;
        _loudItemSubscriber = this;

        Debug.Log($"[PD] LOUD_ITEM の購読を開始しました（Host='{host.gameObject.name}', id={host.GetInstanceID()}）。", this);
        return true;
    }

    /// <summary>
    /// LOUD_ITEM の購読を解除する（OnDisable/OnDestroy から呼ぶ。二重に呼んでも安全）。
    /// </summary>
    private void UnsubscribeLoudItem()
    {
        if (_loudItemSubscribed)
        {
            if (_loudItemHub != null)
            {
                _loudItemHub.LoudItemReceived -= HandleLoudItemReceived;
            }

            _loudItemSubscribed = false;
            Debug.Log("[PD] LOUD_ITEM の購読を解除しました。", this);
        }

        // 担当から外れる（別の実体が購読できるようにする）。
        if (_loudItemSubscriber == this)
        {
            _loudItemSubscriber = null;
        }

        _loudItemHub = null;
    }

    /// <summary>
    /// 子機の大きな音（LOUD_ITEM）を受信したときに、メインスレッドで1回だけ呼ばれる。
    /// 既存の条件判定（enableLoudItemFeature／捕獲済み／警告中の扱い／無敵時の捕獲抑止と突入継続）は
    /// OnLoudItemTriggered 側のまま変更しない。
    /// </summary>
    /// <summary>稼働中のHostを優先し、無ければ初期化済みHostを探す（未初期化Hostは購読先にしない）。</summary>
    private static ParentUdpRuntimeHost ResolveParentHost()
    {
        ParentUdpRuntimeHost host = ParentUdpRuntimeHost.Running;
        if (host != null) return host;

        ParentUdpRuntimeHost found = FindFirstObjectByType<ParentUdpRuntimeHost>();
        return (found != null && found.IsConfigured) ? found : null;
    }

    private void HandleLoudItemReceived()
    {
        if (!isActiveAndEnabled) return;   // 無効化中は反応しない（購読解除済みだが念のため）
        OnLoudItemTriggered();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  GAME_PROGRESS（子機のゲーム進行率）の受信購読
    //  ・受信後の反応は本クラス（OnGameProgress）が担当し、UDP側は受信を通知するだけ。
    //  ・購読は Host が保持する ParentNotificationHub の既存イベントを基準にする（旧窓口とは二重購読しない）。
    //  ・同じ通知を複数の実体が処理しないよう、購読する実体は1つに限定する。
    //  ・enabled=false（捕獲演出など）で購読を外し、再有効化で購読を再開する。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Host が保持する ParentNotificationHub の GameProgressReceived を購読する。
    /// Host が未稼働の場合は購読せず false を返す（OnEnable/Start/Update で再試行する）。
    /// </summary>
    private bool TrySubscribeGameProgress()
    {
        if (_gameProgressSubscribed) return true;
        if (!isActiveAndEnabled) return false;

        // 二重処理防止：先に購読している有効な実体が居る場合は購読しない。
        // （破棄済みの実体は Unity の null 判定で外れるため、その場合は引き継ぐ）
        if (_gameProgressSubscriber != null && _gameProgressSubscriber != this && _gameProgressSubscriber._gameProgressSubscribed)
        {
            Debug.LogWarning("[PD] GAME_PROGRESS の購読は既に別の MotherSuspicionSystem が担当しています（二重処理防止のため購読しません）。", this);
            return false;
        }

        ParentUdpRuntimeHost host = ResolveParentHost();
        if (host == null || host.Notifications == null) return false;

        ParentNotificationHub hub = host.Notifications;
        _gameProgressHub = hub;
        _gameProgressHub.GameProgressReceived += HandleGameProgressReceived;
        _gameProgressSubscribed = true;
        _gameProgressSubscriber = this;

        Debug.Log($"[PD] GAME_PROGRESS の購読を開始しました（Host='{host.gameObject.name}', id={host.GetInstanceID()}）。", this);
        return true;
    }

    /// <summary>
    /// GAME_PROGRESS の購読を解除する（OnDisable/OnDestroy から呼ぶ。二重に呼んでも安全）。
    /// </summary>
    private void UnsubscribeGameProgress()
    {
        if (_gameProgressSubscribed)
        {
            if (_gameProgressHub != null)
            {
                _gameProgressHub.GameProgressReceived -= HandleGameProgressReceived;
            }

            _gameProgressSubscribed = false;
            Debug.Log("[PD] GAME_PROGRESS の購読を解除しました。", this);
        }

        // 担当から外れる（別の実体が購読できるようにする）。
        if (_gameProgressSubscriber == this)
        {
            _gameProgressSubscriber = null;
        }

        _gameProgressHub = null;
    }

    /// <summary>
    /// 子機のゲーム進行率を受信したときに、メインスレッドで1回だけ呼ばれる。
    /// 既存の OnGameProgress をそのまま1回呼ぶ（本体は変更しない）。
    /// </summary>
    private void HandleGameProgressReceived(float progressRate)
    {
        if (!isActiveAndEnabled) return;   // 無効化中は反応しない（購読解除済みだが念のため）
        OnGameProgress(progressRate);
    }

    public void SetApproachSpeed(float speed)
    {
        _approachSpeed = Mathf.Max(0f, speed);
    }

    /// <summary>
    /// 【RushIn】突入用の移動速度を適用する。
    ///  ・適用前の速度（＝このイベントの通常速度）を保存し、帰路開始時に
    ///    RestoreRushInSpeed() で正確に戻す（戻すための乱数再抽選はしない）。
    ///  ・重複要求では保存済みの基準を上書きしない（累積しない）。
    ///  ・速度の書き込み元はこのクラスのまま（呼び出し側は速度値を書かない）。
    /// </summary>
    public void SetLoudItemRushInSpeed(float speed)
    {
        if (!_rushInSpeedActive)
        {
            _rushInSpeedActive = true;
            _rushInSpeedBase = _approachSpeed;
        }

        SetApproachSpeed(speed);
        Debug.Log($"[MotherSuspicionSystem] RushIn速度を適用 | 基準={_rushInSpeedBase:F2} → 実効={_approachSpeed:F2}（重複要求でも基準は保持）");
    }

    /// <summary>
    /// 【RushIn解除】突入用の速度上書きを解除し、適用前に保存した通常速度へ戻す。
    ///  ・帰路移動の「開始前」に呼ぶ（Peekの速い動作は終わっている）。
    ///  ・冪等（解除済みなら何もしない）。
    /// </summary>
    public void RestoreRushInSpeed(string reason)
    {
        if (!_rushInSpeedActive) return;

        _rushInSpeedActive = false;
        SetApproachSpeed(_rushInSpeedBase);
        Debug.Log($"[MotherSuspicionSystem] RushIn速度を解除（{reason}）→ 復元速度={_approachSpeed:F2}");
    }

    /// <summary>
    /// 【片付けRushIn】片付け演出中の RushIn 要求で、片付けの通常移動速度 × 倍率を適用する。
    ///  ・基準は「適用前の片付け通常速度」を保存して使う（乱数は再抽選しない／固定値を直書きしない）。
    ///  ・重複要求では累積しない（適用中は何もしない）。
    ///  ・片付け専用ルートは変更しない（速度だけを変える）。
    /// </summary>
    public void ApplyChoreRushInSpeed(float multiplier)
    {
        if (_choreRushInSpeedActive)
        {
            Debug.Log($"[MotherSuspicionSystem] 片付けRushInの倍率は適用済みのため累積しません（実効速度={_approachSpeed:F2}）");
            return;
        }

        _choreRushInSpeedActive = true;
        _choreRushInSpeedBase = _approachSpeed;

        float rate = Mathf.Max(0f, multiplier);
        SetApproachSpeed(_choreRushInSpeedBase * rate);
        Debug.Log($"[MotherSuspicionSystem] 片付けRushIn | 片付け通常速度={_choreRushInSpeedBase:F2} × 倍率={rate:F2} → 実効速度={_approachSpeed:F2}（ルートは片付け専用のまま）");
    }

    /// <summary>
    /// 【片付けRushIn解除】倍率を解除し、適用前の片付け通常速度へ戻す。
    /// 片付けの帰路開始・片付け終了／中断・再プレイで呼ぶ（冪等）。
    /// </summary>
    public void ReleaseChoreRushInSpeed(string reason)
    {
        if (!_choreRushInSpeedActive) return;

        _choreRushInSpeedActive = false;
        SetApproachSpeed(_choreRushInSpeedBase);
        Debug.Log($"[MotherSuspicionSystem] 片付けRushInの倍率を解除（{reason}）→ 復元速度={_approachSpeed:F2}");
    }

    /// <summary>
    /// 速度上書きの「状態」だけを解除する（速度値は書き換えない）。
    /// 次サイクル開始（ResetCycle）・再プレイでは、速度の基準を決めるのは呼び出し側なので
    /// ここで復元値を持ち込まない（持ち越しだけを防ぐ）。
    /// </summary>
    private void ClearRushInSpeedState()
    {
        _rushInSpeedActive = false;
        _choreRushInSpeedActive = false;
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

        // Door_Open 中はモデルの再生を守る（位置移動は経路側が止める）。
        // 片付けの歩行区間（行き・帰り）は Walk を再生する必要があるため、片付け中フラグ
        // （_choreOverride）では歩行を止めない（演技・ドア開けは別の経路で Walk を落とす）。
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
        // LOUD_ITEM の購読がまだ張れていない場合は再試行する
        // （ParentUdpSender.Instance の取得が遅れた場合。以降の早期 return より前に置く）。
        if (!_loudItemSubscribed)
        {
            TrySubscribeLoudItem();
        }

        // GAME_PROGRESS の購読も同じ理由で再試行する（購読成立後の最初の Update で保留分が通知される）。
        if (!_gameProgressSubscribed)
        {
            TrySubscribeGameProgress();
        }

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
        Debug.Log($"[PD] OnApproachReachedDoor | isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver} | invincible={IsInvincible}");

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
        // 無敵による「捕獲抑止」の印もサイクル開始で解除する（次イベントへ持ち越さない）。
        _captureSuppressedDuringPeek = false;

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
    /// 子機の大きな音のアイテムが発生したとき（LOUD_ITEM 受信時）に、メインスレッドで1回だけ呼ばれる。
    ///
    ///  A) イベントが始まっていない：RushInを1回開始する。
    ///  B) イベントが進行中：現在のイベントを即時中断し（帰宅移動・Peek終了を待たない）、
    ///     表示中の母親を即座に消してから、新しいRushInを1回開始する。
    ///
    /// 「進行中」の判定は実際の警告／RushInシーケンス状態（warningSystem.isWarningActive）を使う。
    /// RushInの開始は既存のRushIn用シーケンス（StartLoudItemRushInSequence）を再利用し、
    /// 速度はRushIn本来の基準速度（loudItemRushInMoveSpeed）を使う。
    /// （通常の予告灯付き接近イベント＝ForeshadowAndApproachCoroutine は開始しない）
    ///
    /// ゲージ加算・捕獲判定は移動3倍速化より前のLOUD_ITEM処理と同じ順序・回数（1回だけ加算）で行う。
    /// 順序：進行中なら即時中断 → 突入音 → ゲージ加算（1回） → 捕獲判定 → 新RushInを1回開始。
    /// </summary>
    public void OnLoudItemTriggered()
    {
        if (!enableLoudItemFeature)
        {
            Debug.Log("[PD] Loud Item Feature is DISABLED");
            return;
        }

        if (isCaught || _hasPermanentGameOver) return;

        // イベントが進行中か（実際の警告／RushInシーケンス状態で判定）。
        bool eventInProgress = warningSystem != null && warningSystem.isWarningActive;

        if (eventInProgress)
        {
            // B：現在のイベントを即時中断し、表示中の母親を消し、開始位置・姿勢を整える。
            InterruptActiveSequenceForLoudItem();

            // 中断中に捕獲確定・ゲームオーバーになっていないことを確認（なっていれば新RushInを開始しない）。
            if (isCaught || _hasPermanentGameOver) return;
        }

        Debug.Log($"[PD] Loud item → rush-in request — adding {loudItemGaugeAmount} gauge stage(s) (once)");

        // 【音】6キーで開始した片付け演出中は、RushIn専用の効果音を鳴らさない
        //   （他の音＝足音・ドア音・MAX到達音・心拍・Jumpscareは止めない。AudioSourceのStopや全体ミュートはしない）。
        //   ※ 自動発生の片付けは今回の変更対象ではないため従来どおり鳴らす。
        bool choreActive = motherChoreController != null && motherChoreController.IsChoreActive;
        bool manualChoreActive = motherChoreController != null && motherChoreController.IsManualChoreActive;
        bool choreReturnTrip = approachController != null && approachController.IsReturnTripActive;

        if (manualChoreActive)
        {
            Debug.Log("[PD] 6キーで開始した片付け演出中のため、RushIn専用の効果音は再生しません" +
                      "（ゲージ加算・捕獲判定・速度倍率は通常どおり）");
        }
        else
        {
            warningSystem?.PlayRushIn();
        }

        if (motherGauge != null)
        {
            // ゲージ加算は1回だけ（中断処理側では加算しない）。
            motherGauge.AddGauge(loudItemGaugeAmount);

            if (motherGauge.currentGauge >= motherGauge.maxGauge)
            {
                // 捕獲が成立した場合だけ突入演出を打ち切る（以降はゲームオーバー処理へ委ねる）。
                // 無敵で抑止された場合は捕獲済みにせず、通常どおり突入（ラッシュイン）演出を続行する。
                if (OnPlayerCaught()) return;
                Debug.Log("[PD] 無敵で捕獲を抑止したため、突入（ラッシュイン）演出を続行します");
            }
        }

        // 【片付け演出中】片付け専用ルートは差し替えない（通常のドア突入ルートへ切り替えない）。
        //   移動速度だけを「片付けの通常速度 × 倍率」にする（基準は適用前の速度を保存して使う）。
        if (choreActive)
        {
            if (choreReturnTrip)
            {
                // 片付けの帰路中は倍率を適用しない（帰路は通常の片付け速度のまま）。
                Debug.Log("[PD] 片付けの帰路中のRushIn要求 — 速度倍率は適用しません（通常の片付け速度のまま／ルートは片付け専用のまま）");
            }
            else
            {
                Debug.Log($"[PD] 片付け演出中のRushIn要求 — ルート=片付け専用（差し替えなし）/ 片付け中={choreActive} / " +
                          $"手動(6キー)={manualChoreActive} / 帰路中={choreReturnTrip} / " +
                          $"基準速度={_approachSpeed:F2} / 倍率={choreRushInSpeedMultiplier:F2}");
                ApplyChoreRushInSpeed(choreRushInSpeedMultiplier);
            }

            return;   // 通常RushIn（loudItemRushInMoveSpeed・DoorPeekルート）は開始しない
        }

        // 新しいRushInを1回だけ開始する（isWarningActiveは上記の中断でfalseになっており、
        // StartLoudItemRushInSequence 側でも二重起動をガードしている）。
        if (warningSystem != null)
            warningSystem.StartLoudItemRushInSequence();
    }

    /// <summary>
    /// LOUD_ITEM用：進行中の旧イベントを即時中断する。
    ///  ・帰宅移動やPeek終了は待たない（既存の停止・終了処理を再利用する）。
    ///  ・旧イベントのコルーチンが後から「母親を再表示」「新RushInを終了」「帰宅」「警告フラグ書き換え」を
    ///    しないよう、関連コルーチンを停止し、開始位置・姿勢へ整える。
    ///  ・MotherSuspicionSystem / UDP購読を持つルートは無効化しない（見た目＝motherModelRoot のみ非表示）。
    ///  ・無関係なコルーチンは一括停止しない（対象のコルーチンだけを個別に停止する）。
    ///  ・中断と開始の間に通常イベントが割り込まないよう、本処理とStartLoudItemRushInSequenceは同一フレームで連続実行する。
    /// </summary>
    private void InterruptActiveSequenceForLoudItem()
    {
        Debug.Log($"[PD] LOUD_ITEM: 進行中の旧イベントを即時中断します" +
                  $"（legacy forceLoudItemDuringWarning={forceLoudItemDuringWarning}: 新仕様では進行中は常に中断するため未使用）");

        // 1) 母を見た目だけ即座に消す（以降の位置リセット・再表示を見せないため最初に行う）。
        approachController?.HideMotherModelImmediate();

        // 2) 警告シーケンス（予告コルーチン・灯り・警告フラグ）を既存の停止処理で止める。
        //    StopWarningSequence → EndWarningSequence（isWarningActive=false / ActiveRoute=None /
        //    approachController.ResetApproach()＝接近コルーチン停止＋開始位置・姿勢へリセット）。
        warningSystem?.StopWarningSequence();

        // 3) 猫フェイント（3キー）が進行中なら停止し、猫とドアを後始末する。
        //    HandleCatFeintSequence は終了時に ResetCycle/EndWarningSequence を呼ぶため、
        //    放置すると後から新RushInを終了させてしまう。
        StopCatFeintForLoudItemInterrupt();

        // 4) このサイクルの疑惑加算・覗き・リセット用コルーチンを止め、ドアを閉める（既存の ResetCycle を再利用）。
        //    片付け視線の加算（ContinuousChoreLookSuspicionCoroutine）も ResetCycle 内の StopChoreLookSuspicion で止まる。
        ResetCycle();

        // 5) 念のため開始位置・姿勢を再確認する（冪等。新RushInは BeginApproach でここから開始する）。
        approachController?.ResetApproach();

        Debug.Log("[PD] LOUD_ITEM: 旧イベントの中断が完了（表示OFF→開始位置/姿勢を準備→新RushIn開始へ）");
    }

    /// <summary>
    /// LOUD_ITEMの即時中断用：猫フェイントのコルーチンを止め、猫とドアを後始末する。
    /// （HandleCatFeintSequence 自身の中断時と同じ処理を行う）
    /// </summary>
    private void StopCatFeintForLoudItemInterrupt()
    {
        if (_catFeintCoroutine != null)
        {
            StopCoroutine(_catFeintCoroutine);
            _catFeintCoroutine = null;
            Debug.Log("[PD] LOUD_ITEM中断: 猫フェイントのコルーチンを停止しました");
        }

        if (catFeintController != null)
        {
            catFeintController.HideCat();
            catFeintController.ReturnToStartPosition();
        }
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
        // 無敵ONでも「設定された覗き時間」は再生する（覗きを途中で打ち切らないための経過時間）。
        float peekElapsed = 0f;

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

            // 【無敵モード】通常は「捕獲（ゲームオーバー）」で終わる状況では、捕獲が終了の役割を担う。
            //   無敵中は捕獲されないため、次のいずれかでは待ち続けずに通常の終了処理
            //   （退出待ち→ドア閉め→帰路→終了通知）へ進める。捕獲済みにはしない（isCaught は立てない）。
            //     ・捕獲要求が実際に抑止された（ゲージ上限に到達したが無敵で抑止された）
            //     ・無敵中でプレイヤーが起きている（加算が抑止されてゲージが上がらず、
            //       通常なら捕獲に至る状況のまま捕獲要求そのものが発生しない）
            bool sleeping = (sleepingController != null) && sleepingController.IsSleeping;

            if (_captureSuppressedDuringPeek)
            {
                Debug.Log("[PD] 無敵で捕獲を抑止したため、通常の覗き終了処理（退出・ドア閉め・帰路・終了通知）へ進みます");
                break;
            }

            if (IsInvincible && !sleeping)
            {
                // 【無敵モード】捕獲は発生しないため「捕獲待ち」で待ち続けない。
                //   ただし覗き（Peek_Door）は設定された時間（_activePeekDuration）を再生してから
                //   終了する。無敵ONでも「Peekの再生・終了・帰路」を止めない。
                if (peekElapsed >= Mathf.Max(0f, _activePeekDuration))
                {
                    Debug.Log($"[PD] 無敵モード中でプレイヤーが起きているため（通常なら捕獲に至る状況）、" +
                              $"設定された覗き時間 {_activePeekDuration:F1}s の経過後に通常の覗き終了処理" +
                              "（退出・ドア閉め・帰路・終了通知）へ進みます");
                    break;
                }
            }

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

            peekElapsed += Time.deltaTime;
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
    /// DoorPoint到着後にドアを全開にし（既存DoorController APIを再利用）、猫を見せて鳴き声を再生する。
    /// 母親は移動せず、母親のドア到着イベント・本チェック・疑惑加算・捕獲判定は発生しない。
    /// 鳴き声は猫専用のAudioSource（CatFeintController）から再生し、既存の親の音源やUDPには触れない。
    /// ドアを開けておく時間は CatFeintController の catDoorHoldOpenSeconds（＋現在ゲージ）で調整する。
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

        // ドアを開けておく時間の管理元は CatFeintController のみ（catDoorHoldOpenSeconds）。
        //   怪しさゲージ分の加算は従来どおり行う（＝旧 peekDurationBase + gauge と同じ扱いを維持）。
        float holdOpenSeconds = catFeintController.DoorHoldOpenSeconds + gauge;

        Debug.Log(
            $"[PD] TriggerCatFeintEvent — cat walks StartPoint→DoorPoint, then door FULL open for {holdOpenSeconds:F1}s" +
            $"（CatFeintController.catDoorHoldOpenSeconds={catFeintController.DoorHoldOpenSeconds:F1}s + gauge={gauge}／母親は登場しない）" +
            "（開放時間はFull到達から計測・ジャンプ動作が長い場合は完了まで延長）");
        _catFeintCoroutine = StartCoroutine(HandleCatFeintSequence(holdOpenSeconds));
        return true;
    }

    /// <summary>
    /// 猫フェイントの本体。猫の移動 → ドアを全開（目標開度へ到達まで待つ）＋鳴き声 → 設定時間待機
    /// → ジャンプ動作の完了を待つ → 猫を隠す → ドアを閉める → 猫を開始状態へ戻す。
    /// ドアを開けておく時間は「Full到達時点」から holdOpenSeconds（CatFeintController 設定＋ゲージ）だけ数える。
    ///  ・Full へ到達しなかった場合は開放時間を数えず、猫を隠す→ドアを閉める→状態解除の安全な失敗終了（警告ログで区別）。
    ///  ・ジャンプ動作が設定時間より長い場合は、見せ終えるまでドアを開けたままにする（実測をログ出力）。
    ///  ・中断（警告終了）時はジャンプ完了を待たず、猫・ドア・警告状態を必ず戻す。
    /// </summary>
    private IEnumerator HandleCatFeintSequence(float holdOpenSeconds)
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
        //    【開放時間の計測開始点】猫フェイント用の目標開度（Full）へ「実際に到達した時点」から数える。
        //     ・ドアの回転速度・開度は変更しない（既存 DoorController の通常速度のまま）。
        //     ・到達の成否は WaitForDoorReached のコールバックで区別する（タイムアウトを到達成功と混同しない）。
        bool doorReached = true;
        if (targetDoorController != null)
        {
            targetDoorController.SetDoorState(DoorController.DoorState.Full);   // 通常速度のまま開き始める
            yield return targetDoorController.WaitForDoorReached(
                catFeintController.DoorReachWaitTimeoutSeconds, ok => doorReached = ok);

            // 到達待ちの間に中断された場合は猫とドアを戻して終了する（警告状態は既に解除済み）。
            if (warningSystem == null || !warningSystem.isWarningActive)
            {
                Debug.Log("[PD] HandleCatFeintSequence: ドア到達待ち中に中断検出 — 猫とドアを戻す");
                catFeintController.HideCat();
                catFeintController.ReturnToStartPosition();
                targetDoorController.SetDoorState(DoorController.DoorState.Closed);
                _catFeintCoroutine = null;
                yield break;
            }

            if (!doorReached)
            {
                // 【失敗終了】Full 未到達を「Full到達後の開放時間」として数えない。
                //   猫を隠し、ドアを閉め、イベント状態を解除して次の猫フェイントを実行できる状態に戻す。
                Debug.LogWarning("[PD] 猫フェイント: ドアが Full へ到達しませんでした" +
                                 $"（{catFeintController.DoorReachWaitTimeoutSeconds:F1}s タイムアウト）" +
                                 " — 開放時間は数えず、安全に失敗終了します（猫を隠す→ドアを閉める→状態解除）");
                catFeintController.HideCat();
                catFeintController.ReturnToStartPosition();
                targetDoorController.SetDoorState(DoorController.DoorState.Closed);
                _catFeintCoroutine = null;
                ResetCycle();
                if (warningSystem != null)
                    warningSystem.EndWarningSequence();
                yield break;
            }
        }

        // 開放時間の計測開始（Full 到達後）。以降のジャンプ完了待ちもこの時間に含めて実測する。
        float holdStartTime = Time.time;

        catFeintController.TriggerJump();
        catFeintController.PlayMeow();

        warningSystem?.PlayDummyDoor();

        // 4. 設定した開放時間（Full到達後・怪しさゲージ分込み）が過ぎるまで待つ。
        //    catHideBeforeCloseSeconds は「この開放時間のうちに猫を隠すタイミング」であり、
        //    合計は設定時間のまま（同じ待ち時間を二重に加算しない）。
        //    中断があればループを抜けて後始末する。
        float hideBeforeClose = catFeintController.HideBeforeCloseSeconds;
        float catVisibleDuration = Mathf.Max(0f, Mathf.Max(0f, holdOpenSeconds) - hideBeforeClose);
        float elapsed = 0f;
        while (elapsed < catVisibleDuration && warningSystem != null && warningSystem.isWarningActive)
        {
            // TriggerJump の時点から、この既存の待機ループ内でジャンプの開始／終了を観測する
            //（開放時間内に終わったジャンプを「未完了」と誤判定しないため）。
            catFeintController.ObserveJumpProgress();
            elapsed += Time.deltaTime;
            yield return null;
        }

        // 5. 正常終了では、猫のジャンプ動作を見せ終えてから隠す（固定秒数で完了と断定せず Animator 状態で判定）。
        //    中断時は動作完了を待たずに安全終了する（後始末はこの下の共通処理で実施）。
        if (warningSystem != null && warningSystem.isWarningActive)
        {
            CatFeintController.JumpWaitResult jumpResult = CatFeintController.JumpWaitResult.NotConfigured;
            yield return catFeintController.WaitForJumpFinished(
                catFeintController.JumpWaitTimeoutSeconds, result => jumpResult = result);

            float actualHold = Time.time - holdStartTime;
            if (jumpResult == CatFeintController.JumpWaitResult.TimedOut)
            {
                // 開始できない／終了遷移を抜けない＝タイムアウト失敗。アニメ完了と断定せず猫を隠して安全終了。
                Debug.LogWarning("[PD] 猫フェイント: ジャンプ動作の開始／終了を確認できませんでした" +
                                 $"（{catFeintController.JumpWaitTimeoutSeconds:F1}s タイムアウト）" +
                                 " — アニメ完了と断定せず、猫を隠して安全終了します（ドアは閉じます）");
            }
            else if (jumpResult == CatFeintController.JumpWaitResult.NotConfigured)
            {
                // ジャンプ設定なし（Jumpパラメーター無し等）＝待機対象なし。「ジャンプ完了」とは区別してログする。
                Debug.Log("[PD] 猫フェイント: ジャンプ設定なし・待機対象なし（ジャンプ完了とは区別）" +
                          $" — 開放時間 実測 {actualHold:F1}s（設定 {holdOpenSeconds:F1}s）");
            }
            else if (actualHold > Mathf.Max(0f, holdOpenSeconds) + 0.05f)
            {
                Debug.Log($"[PD] 猫フェイント: ジャンプ動作の完了を待つため開放時間が延長されました" +
                          $"（設定 {holdOpenSeconds:F1}s → 実測 {actualHold:F1}s）");
            }
            else
            {
                // 通常終了（ジャンプ完了待ち込みの実測をログに残す：設定時間と実測を区別できるようにする）。
                Debug.Log($"[PD] 猫フェイント: 開放時間 実測 {actualHold:F1}s（設定 {holdOpenSeconds:F1}s／ジャンプ完了待ち込み）");
            }
        }
        else
        {
            Debug.Log("[PD] 猫フェイント: 中断のためジャンプ動作の完了を待たずに安全終了します（猫を隠す→ドアを閉める）");
        }

        catFeintController.HideCat();

        if (hideBeforeClose > 0f)
            yield return new WaitForSeconds(hideBeforeClose);

        warningSystem?.PlayMainDoorClose();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        // 6. 次回用に猫を開始状態へ戻す（ホーム位置へ戻して非表示）。
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
    //   ・OnGameProgress() は子機からの進行率通知を片付け側へ渡す。
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
            StopChoreLookSuspicion();   // 片付け解除：視線期間の加算もハンドルごと解除する
            // 片付けRushInの速度倍率も解除する（中断・正常終了・次サイクル・再プレイへ持ち越さない）。
            ReleaseChoreRushInSpeed("片付け終了/中断");
        }
    }

    /// <summary>
    /// 片付け中の視線による発見判定を設定する。
    /// アニメーションの見た目（再生）とは独立に、開始・終了タイミングをここで管理する。
    /// 【重要】歩行による怪しさ加算（EnableChoreSuspicion 中）と二重に加算しないよう、
    ///         ここで視線を有効化するときは歩行加算を止める。
    /// 【追加】視線の開始（false→true）で、通常の覗きと同じ条件（寝たふり中は加算しない）の
    ///         視線期間加算を開始する。true→true の再通知では再開始も開始時加算もしない。
    /// </summary>
    public void SetChoreLooking(bool looking)
    {
        bool wasLooking = _choreLooking;
        _choreLooking = looking;
        // 通常イベントの覗き判定と競合しないよう、片付け抑止中だけ反映する。
        if (_choreOverride)
            isMotherLookingNow = looking;

        if (looking)
        {
            // 視線（3）では既存の視線判定期間だけ加算する。
            // 歩行加算と二重にならないよう、視線の開始／終了で歩行加算の側を止める。
            DisableChoreSuspicion();

            // false→true のときだけ開始する（true→true の再通知では再開始・開始時加算をしない）。
            if (!wasLooking)
                StartChoreLookSuspicion();
        }
        else
        {
            // 視線終了：加算コルーチンとハンドルを確実に解除する（減少処理は行わない）。
            StopChoreLookSuspicion();
        }
    }

    /// <summary>
    /// 片付けの視線（Chore_Peek）期間の怪しさ加算を開始する。
    ///  ・条件は通常の覗き（部屋内の継続疑惑）と同じ
    ///    （enableContinuousRoomSuspicion を尊重／寝たふり中は加算しない）
    ///  ・加算量・間隔も同じフィールド（continuousRoomSuspicionAmount /
    ///    continuousRoomSuspicionTickInterval）を使う（片付け専用の値は増やさない）
    ///  ・既に走っているときは再開始しない（true→true の再通知で二重加算しない）
    /// </summary>
    private void StartChoreLookSuspicion()
    {
        if (!enableContinuousRoomSuspicion)
        {
            Debug.Log("[PD] 片付け視線の怪しさ加算なし — enableContinuousRoomSuspicion=false（通常の覗きと同じ設定に従う）");
            return;
        }

        if (_choreLookSuspicionCoroutine != null) return;

        // 【重要】StartCoroutine は「コルーチンの最初の yield まで」を同期実行してから戻る。
        //   コルーチン先頭の開始時加算で上限到達すると OnPlayerCaught → AbortChore
        //   （＝SetDetection(false) → SetChoreLooking(false) → StopChoreLookSuspicion()）が
        //   この代入より先に走り、コルーチンは開始時に終了している。
        //   その場合に「終了済みハンドル」を保持すると、次回の StartChoreLookSuspicion が
        //   二重起動ガードで早期 return し、以降の視線加算が始まらなくなる。
        //   走行中フラグ（コルーチンが生きている間だけ true）で確認してから代入する。
        Coroutine handle = StartCoroutine(ContinuousChoreLookSuspicionCoroutine());
        _choreLookSuspicionCoroutine = _choreLookSuspicionRunning ? handle : null;

        Debug.Log($"[PD] 片付け視線の怪しさ加算を開始（amount={continuousRoomSuspicionAmount} / interval={continuousRoomSuspicionTickInterval:F2}s）");
    }

    /// <summary>
    /// 片付けの視線による怪しさ加算を停止し、ハンドルを必ず解除する。
    /// 視線終了・片付け解除・正常終了・中断・無効化で呼ぶ（疑惑の減少は行わない）。
    /// 加算の途中で捕獲・中断が起きて自分自身から停止される場合もあるため、
    /// ハンドルを先に null にしてから停止する（古いコルーチン・古いハンドルを残さない）。
    /// </summary>
    private void StopChoreLookSuspicion()
    {
        Coroutine running = _choreLookSuspicionCoroutine;
        _choreLookSuspicionCoroutine = null;
        _choreLookSuspicionRunning = false;

        if (running == null) return;

        StopCoroutine(running);
        Debug.Log("[PD] 片付け視線の怪しさ加算を停止（減少処理は行わない）");
    }

    /// <summary>
    /// 片付けの視線（Chore_Peek）期間の加算本体。
    ///  ・検知開始時に、その時点で寝たふり中でなければ1回加算する
    ///  ・以降は continuousRoomSuspicionTickInterval ごとに、その時点の寝たふり状態を見て加算する
    ///  ・上限到達は既存の OnPlayerCaught() へつなぐ（既存の捕獲・ゲームオーバー処理を変えない）
    ///  ・視線終了（_choreLooking=false）／片付け解除（_choreOverride=false）でも自分を終了させる
    /// </summary>
    private IEnumerator ContinuousChoreLookSuspicionCoroutine()
    {
        _choreLookSuspicionRunning = true;

        // 検知開始時の1回（寝たふり中なら加算しない）
        if (!ApplyChoreLookSuspicionTick("検知開始"))
        {
            _choreLookSuspicionRunning = false;
            _choreLookSuspicionCoroutine = null;
            yield break;
        }

        while (_choreLookSuspicionRunning && _choreLooking && _choreOverride)
        {
            yield return new WaitForSeconds(continuousRoomSuspicionTickInterval);

            if (!_choreLookSuspicionRunning || !_choreLooking || !_choreOverride) break;
            if (!ApplyChoreLookSuspicionTick("定期")) break;
        }

        _choreLookSuspicionRunning = false;
        _choreLookSuspicionCoroutine = null;
    }

    /// <summary>
    /// 片付け視線の加算を1回試みる。
    ///  ・続行してよければ true、停止すべきなら false を返す
    ///  ・条件は通常の覗き（継続疑惑）と同じ: 寝たふり中は加算しない
    ///  ・ゲームオーバー・捕獲・ゲージ未設定・上限到達では停止する
    /// </summary>
    private bool ApplyChoreLookSuspicionTick(string reason)
    {
        if (_hasPermanentGameOver || isCaught)
        {
            Debug.Log($"[PD] 片付け視線の怪しさ加算 停止 — game over or caught（{reason}）");
            return false;
        }

        if (motherGauge == null)
        {
            Debug.Log($"[PD] 片付け視線の怪しさ加算 停止 — motherGauge is null（{reason}）");
            return false;
        }

        // その時点の寝たふり状態で判定する（通常の覗きの継続疑惑と同じ条件）。
        bool sleeping = (sleepingController != null) && sleepingController.IsSleeping;
        if (sleeping)
        {
            Debug.Log($"[PD] 片付け視線の怪しさ加算 スキップ — プレイヤーが寝たふり中（{reason}）");
            return true;
        }

        motherGauge.AddGauge(continuousRoomSuspicionAmount);
        Debug.Log($"[PD] 片付け視線の怪しさ加算 {reason} +{continuousRoomSuspicionAmount} | gauge now {motherGauge.currentGauge}");

        if (motherGauge.currentGauge >= motherGauge.maxGauge)
        {
            Debug.Log($"[PD] 片付け視線の怪しさ加算 停止 — gauge reached max（{reason} / 既存の捕獲処理へ）");
            OnPlayerCaught();
            return false;
        }

        return true;
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

    /// <summary>永久ゲームオーバーが確定しているか（診断用・読み取り専用）。</summary>
    public bool HasPermanentGameOver => _hasPermanentGameOver;

    /// <summary>警告シーケンスが進行中か（診断用・読み取り専用）。</summary>
    public bool IsWarningSequenceActive => warningSystem != null && warningSystem.isWarningActive;

    /// <summary>片付けのドア開けによる歩行上書き抑止中か（診断用・読み取り専用）。</summary>
    public bool IsChoreWalkingOverrideSuppressed => _choreWalkingOverrideSuppressed;

    /// <summary>無敵で捕獲が抑止された印が立っているか（診断用・読み取り専用）。</summary>
    public bool CaptureSuppressedDuringPeek => _captureSuppressedDuringPeek;

    /// <summary>このサイクルで部屋入室の疑惑を開始済みか（診断用・読み取り専用）。</summary>
    public bool IsRoomEntryStarted => _roomEntryStarted;

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

        // 切り替え時点の実行フェーズを1回出力する（毎フレームは出さない）。
        LogInvinciblePhase(value ? "無敵ON" : "無敵OFF");

        InvincibleChanged?.Invoke(value);
    }

    /// <summary>
    /// 無敵の切り替え時点の実行フェーズを1回出力する。
    /// 母親イベントが進行中の場合は、切り分け用の状態一覧も1回だけ出す。
    /// </summary>
    private void LogInvinciblePhase(string trigger)
    {
        string route = (warningSystem != null) ? warningSystem.ActiveRoute.ToString() : "none";
        int gauge = (motherGauge != null) ? motherGauge.currentGauge : -1;
        int max = (motherGauge != null) ? motherGauge.maxGauge : -1;

        string approach = approachController != null
            ? $"接近中={approachController.IsApproaching} 庭覗き中={approachController.IsGardenPeeking} " +
              $"庭ルート={approachController.IsGardenRoute} 帰路={approachController.ReturnHomePhase} " +
              $"経路失敗={approachController.RouteExecutionFailed} ドア停止={approachController.StoppedAtDoor} " +
              $"終了通知済み={approachController.IsRouteEndNotified}"
            : "approachController=none";

        Debug.Log($"[MotherSuspicion] {trigger} 時点のフェーズ | 警告中={IsWarningSequenceActive} ルート={route} | " +
                  $"ゲージ={gauge}/{max} 捕獲={isCaught} 永久GO={_hasPermanentGameOver} ドア覗き={isMotherLookingNow} " +
                  $"部屋入室開始={_roomEntryStarted} 捕獲抑止印={_captureSuppressedDuringPeek} | {approach}");

        if (approachController != null &&
            (approachController.IsApproaching || approachController.IsReturningHome ||
             approachController.IsGardenPeeking || approachController.StoppedAtDoor))
        {
            approachController.LogRouteStateDump($"{trigger} 時点");
        }
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

        Debug.Log("[PD] 6キー：MotherChoreController.RequestManualStart() へ開始要求を転送します");
        return motherChoreController.RequestManualStart();
    }

    /// <summary>
    /// 片付け開始時点の怪しさ段階に応じた視線回数（Min/Max）を返す。
    ///  ・設定元は本クラスだけ（MotherChoreController は開始時に1回だけ呼んで回数を確定する）。
    ///  ・怪しさ段階：ゲージが『低』上限以下→低、『中』上限以下→中、それ超→高。
    ///  ・検証：Min は1以上、Max は Min 以上（不正値は警告して安全側へ補正する）。
    /// </summary>
    public void ResolveChoreLookCountRange(out int min, out int max)
    {
        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;

        int rawMin, rawMax;
        if (gauge <= choreLookLowGaugeMax) { rawMin = choreLookLowMin; rawMax = choreLookLowMax; }
        else if (gauge <= choreLookMidGaugeMax) { rawMin = choreLookMidMin; rawMax = choreLookMidMax; }
        else { rawMin = choreLookHighMin; rawMax = choreLookHighMax; }

        if (rawMin < 1)
        {
            Debug.LogWarning($"[PD] 片付け視線回数の Min が1未満（{rawMin}）のため1へ補正します", this);
            rawMin = 1;
        }

        if (rawMax < rawMin)
        {
            Debug.LogWarning($"[PD] 片付け視線回数の Max({rawMax}) が Min({rawMin}) 未満のため Min へ補正します", this);
            rawMax = rawMin;
        }

        min = rawMin;
        max = rawMax;
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
        // 次サイクルへ RushIn の速度上書き状態を持ち越さない（速度の基準は上のSetApproachSpeedが決める）。
        ClearRushInSpeedState();

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

        // サイクルリセット時も、片付け視線の加算コルーチン・ハンドルを残さない（無効化・再プレイ対策）。
        StopChoreLookSuspicion();

        // 無敵による「捕獲抑止」の印もここで解除する（サイクル終了で必ず消える）。
        _captureSuppressedDuringPeek = false;

        isMotherLookingNow = false;
        _activePeekDuration = peekDurationBase;

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  ゲームオーバー
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 捕獲・ゲームオーバーの共通入口（ゲージ上限到達・大きな音などから呼ばれる）。
    /// 戻り値：true = 捕獲を実行した ／ false = 無敵モードで抑止した。
    ///  ・呼び出し元は戻り値で「捕獲で終わった」と「捕獲は抑止された＝通常の終了処理へ進むべき」を区別する。
    ///  ・抑止時に isCaught / _hasPermanentGameOver は立てない（捕獲済みにはしない）。
    /// </summary>
    private bool OnPlayerCaught()
    {
        // 【無敵モード】テスト用無敵（Lキー）がONの間は捕獲・ゲームオーバーを抑止する。
        //   最大ゲージ到達から呼ばれる唯一の捕獲入口のため、ここで一括ガードする。
        //   抑止中でも移動・アニメーション・覗き・片付け・音・照明は止めない。
        if (ShouldBlockCapture())
        {
            // 覗き中（＝通常は捕獲で終わる覗き）に抑止した場合は、通常の覗き終了処理
            // （ドア閉め・帰路・終了通知）へ進めるための印を立てる。
            // 捕獲は実行していないので isCaught / ゲームオーバーは立てない。
            if (_primaryResetCoroutine != null || _roomEntryStarted)
            {
                _captureSuppressedDuringPeek = true;
                Debug.Log("[PD] OnPlayerCaught: 無敵モード中のため捕獲を抑止 — 通常の覗き終了処理（ドア閉め・帰路・終了通知）へ進めます。" +
                          "isCaught/ゲームオーバーは立てません");
            }
            else
            {
                Debug.Log("[PD] OnPlayerCaught: 無敵モード中のため捕獲とゲームオーバーを抑止します");
            }
            return false;
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

        return true;
    }
}