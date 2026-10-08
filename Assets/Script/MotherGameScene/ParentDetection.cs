using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentDetection:
/// ドアイベント／分岐のみを制御する。
///
/// 責務の境界：
///   ParentApproachController  — 移動とルート演出
///   ParentWarningSystem       — シーケンス調整
///   ParentWarningScheduler    — タイミングと自動警告スケジュール（1キーの母親ドア確認起動を含む）
///   ParentDetection（本クラス）— 親機のドア到着／通過に反応し、
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
public class ParentDetection : MonoBehaviour
{
    // ── システム参照 ──────────────────────────────────────────────────────────
    [Header("システム参照")]
    public ParentWarningSystem       warningSystem;
    public CaughtReactionController  caughtReactionController;
    public MotherGauge               motherGauge;
    public ParentApproachController  approachController;
    public SleepingController        sleepingController;

    // ── 親機の速度・足音・Animator ────────────────────────────────────────────
    [Header("親機の速度")]
    public float approachMoveSpeedMin = 1.5f;
    public float approachMoveSpeedMax = 1.5f;
    public float approachSpeedSuspicionBonus;
    public int highSuspicionSpeedGaugeThreshold = 10;
    public float highSuspicionApproachMoveSpeedMin = 1.5f;
    public float highSuspicionApproachMoveSpeedMax = 1.5f;
    public float loudItemRushInMoveSpeed = 140f;
    public bool useFixedDebugApproachSpeed;
    public float fixedDebugApproachSpeed = 30f;
    [Tooltip("ParentDetectionから直接開始された初回接近で使う既存の速め速度。通常の警告開始時は警告種別の速度で上書きする。")]
    public float initialApproachSpeed = 25f;

    private Animator motherAnimator;

    [Header("親機の足音")]
    [SerializeField] private AudioSource hallwayFootstepAudioSource;
    [SerializeField] private AudioSource gardenFootstepAudioSource;
    [Range(0f, 1f)]
    [SerializeField] private float farVolume = 0.2f;
    [Range(0f, 1f)]
    [SerializeField] private float midVolume = 0.5f;
    [Range(0f, 1f)]
    [SerializeField] private float nearDoorVolume = 1f;
    [SerializeField] private float volumeChangeSpeed = 1f;

    // ── オーディオ ─────────────────────────────────────────────────────────────
    [Header("オーディオソース")]
    [Tooltip("ダミー（覗き見）ドアイベント発生時に再生。")]
    [SerializeField] private AudioSource dummyDoorAudioSource;
    [Tooltip("本チェック（全開）でドアが開いたときに再生。")]
    [SerializeField] private AudioSource mainDoorOpenAudioSource;
    [Tooltip("各イベント終了時にドアが閉じるときに再生。")]
    [SerializeField] private AudioSource mainDoorCloseAudioSource;
    [Tooltip("大きな音による突入が発生した直後に再生。")]
    [SerializeField] private AudioSource rushInAudioSource;
    [Tooltip("廊下を通過した後に再生する偽ドア音。")]
    [SerializeField] private AudioSource passByDoorAudioSource;
    [Tooltip("通過完了から偽ドア音を再生するまでの秒数。")]
    [SerializeField, Min(0f)] private float passByDoorSoundDelay = 1f;

    // ── ドア ──────────────────────────────────────────────────────────────────
    [Header("ドア制御")]
    [SerializeField] private DoorController targetDoorController;

    // ── 分岐 ──────────────────────────────────────────────────────────────────
    [Header("イベント分岐")]
    [Tooltip("ParentWarningSystemからルート状態を取得できない場合のダミー（覗き見）チェック確率（例：Pキーのデバッグ）。")]
    [SerializeField, Range(0f, 1f)] private float dummyProbability = 0.3f;

    // ── 猫フェイント（3キー専用） ─────────────────────────────────────────────
    [Header("猫フェイント")]
    [Tooltip("猫フェイントで猫の表示を管理するCatFeintController。未設定の場合は猫フェイントを開始しない。")]
    [SerializeField] private CatFeintController catFeintController;

    private Coroutine _catFeintCoroutine;

    // ── 覗き見／部屋チェックのタイミング ─────────────────────────────────────
    [Header("部屋チェックのタイミング")]
    [Tooltip("ダミー（覗き見のみ）イベントの基本時間（秒）。実際の時間=peekDurationBase + currentGauge。")]
    [SerializeField] private float peekDurationBase = 3f;
    [Tooltip("RushInでドアに到着してから覗き続ける秒数。通常の本チェック時間とは別管理。")]
    [SerializeField] private float rushInPeekDurationSeconds = 6f;
    [Tooltip("本チェック（全開）イベントで、プレイヤーが枕で眠るまで親機が部屋に留まる時間。 " +
             "一度も眠らない場合は、安全タイムアウトとしてこの秒数後に親機が退出する。")]
    [SerializeField] private float roomCheckSafetyTimeout = 30f;

    [Tooltip("帰路（Peek終了後に母親が画面外へ戻る）の完了を待つ上限秒数。\n" +
             "これを超えたら警告を出してサイクルを終了する（無限待機しない）。")]
    [SerializeField] private float returnHomeSafetyTimeout = 15f;
    [Tooltip("プレイヤーが眠ってから親機がドアを閉めて退出するまでの秒数（疑惑0の場合）。")]
    [SerializeField] private float leaveAfterSleepDelay = 2f;
    [Tooltip("最大疑惑時に、プレイヤーが眠ってから親機が退出するまでの秒数。疑惑0のleaveAfterSleepDelayから最大疑惑時のこの値まで補間する。")]
    [SerializeField] private float leaveAfterSleepDelayMax = 6f;
    [Tooltip("部屋からの退室を要求してから退室完了（OnExitedRoom）を待つ最大秒数。超過した場合は従来どおりドアを閉じて終了する。")]
    [SerializeField] private float roomExitSafetyTimeout = 15f;

    // ── 部屋侵入時の疑惑 ──────────────────────────────────────────────────────
    [Header("部屋侵入時の疑惑")]
    [Tooltip("親機が部屋に入り、プレイヤーが睡眠中でないときに発生する3回の増加の間隔（秒）。")]
    [SerializeField] private float roomEntryBurstTickInterval = 0.2f;

    // ── 大きな音のアイテム ────────────────────────────────────────────────────
    [Header("大きな音のアイテム機能")]
    [Tooltip("無効にすると、0キーおよびゲーム内の大きな音のアイテムトリガーが完全に無効になる。")]
    [SerializeField] private bool enableLoudItemFeature = true;
    [Tooltip("trueの場合、大きな音のアイテムが進行中の警告を中断し、突入を強制する。")]
    [SerializeField] private bool forceLoudItemDuringWarning = false;
    [Tooltip("大きな音のアイテム発生時にMotherGaugeへ加算する段階数。最大値に達した場合は突入せず即座にゲームオーバーになる。")]
    [SerializeField] private int loudItemGaugeAmount = 3;

    // ── 部屋内の継続疑惑 ──────────────────────────────────────────────────────
    // ── 庭覗き中の継続疑惑 ────────────────────────────────────────────────────
    [Header("庭覗き中の継続疑惑")]
    [Tooltip("庭覗き中の加算間隔=ドア側の継続疑惑間隔(continuousRoomSuspicionTickInterval)×この倍率。0以下で庭覗き中の疑惑加算を無効化する。")]
    [SerializeField] private float gardenPeekSuspicionIntervalMultiplier = 1.5f;

    private Coroutine _gardenPeekSuspicionCoroutine;

    [Header("部屋内の継続疑惑")]
    [Tooltip("親機の本チェックドアイベント中、疑惑を継続的に増加させる。")]
    [SerializeField] private bool enableContinuousRoomSuspicion = true;
    [Tooltip("部屋チェック継続フェーズで1回ごとに加算するゲージ段階数。")]
    [SerializeField] private int continuousRoomSuspicionAmount = 1;
    [Tooltip("部屋内の継続疑惑を加算する間隔（秒）。")]
    [SerializeField] private float continuousRoomSuspicionTickInterval = 2f;

    // ── 公開状態 ──────────────────────────────────────────────────────────────
    public bool isCaught;
    public bool isMotherLookingNow;

    // ── 非公開状態 ────────────────────────────────────────────────────────────
    private Coroutine    _dummyResetCoroutine;
    private Coroutine    _primaryResetCoroutine;
    private Coroutine    _continuousRoomCoroutine;
    private Coroutine    _rushInPeekCoroutine;
    private Coroutine _passByDoorSoundCoroutine;
    private bool         _hasPermanentGameOver;
    private float        _activePeekDuration        = 3f;

    // ── 部屋入室（案B）：疑惑開始を「部屋入室完了」にずらすための状態 ────────────
    private bool         _roomEntryAccepted;        // このサイクルでRequestRoomEntry()が受理されたか
    private bool         _roomEntryStarted;         // OnEnteredRoom後に疑惑コルーチンを開始済みか
    private bool         _roomExitCompleted;        // OnExitedRoomを受信済みか
    private bool         _approachEventsSubscribed; // ParentApproachControllerの入退室イベントを購読中か
    private int          _roomCycleId;              // OnApproachReachedDoorのたびに増えるサイクル識別子
    private int          _primaryResetCycleId;      // HandlePrimaryResetSequence開始時点の_roomCycleId
    private float _approachSpeed = 1.5f;
    private float _currentFootstepVolume;
    private float _targetFootstepVolume;
    private bool _isGrassFootstepRoute;
    private bool _animatorWarningLogged;
    public float CurrentApproachSpeed => _approachSpeed;

    // ──────────────────────────────────────────────────────────────────────────
    //  Unityライフサイクル
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

        // Animatorの特定は ParentApproachController に一元化する（複数スクリプトが別々に探して
        // 別のAnimatorを掴むことを防ぐ）。取得できない場合は Controller 側が理由つきで警告する。
        if (motherAnimator == null && approachController != null)
            motherAnimator = approachController.MotherAnimator;

        // 廊下ルートの移行判定を、経路を使うどの処理よりも先に確定させる。
        // 母子で共有するルート設定を、Start実行順に依存させないための前倒し。
        if (approachController != null)
            approachController.MigrateLegacyHallwayPoints();

        _approachSpeed = initialApproachSpeed;
        _currentFootstepVolume = farVolume;
        _targetFootstepVolume = farVolume;
        InitializeFootstepAudioSource(hallwayFootstepAudioSource);
        InitializeFootstepAudioSource(gardenFootstepAudioSource);
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
        CancelPassByDoorSound();
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
            Debug.Log($"[ParentDetection] APPROACH SPEED: {speed:F2} units/sec (FIXED DEBUG)");
        }
        else if (!isManual && motherGauge != null &&
                 motherGauge.currentGauge > highSuspicionSpeedGaugeThreshold)
        {
            speed = UnityEngine.Random.Range(
                highSuspicionApproachMoveSpeedMin, highSuspicionApproachMoveSpeedMax);
            Debug.Log($"[ParentDetection] APPROACH SPEED: {speed:F2} units/sec (HIGH SUSPICION RANGE)");
        }
        else
        {
            speed = UnityEngine.Random.Range(approachMoveSpeedMin, approachMoveSpeedMax);
            float suspicionFraction = motherGauge == null || motherGauge.maxGauge <= 0
                ? 0f
                : Mathf.Clamp01((float)motherGauge.currentGauge / motherGauge.maxGauge);
            if (!isManual && approachSpeedSuspicionBonus > 0f)
                speed += approachSpeedSuspicionBonus * suspicionFraction;
            Debug.Log($"[ParentDetection] APPROACH SPEED: {speed:F2} units/sec (NORMAL, suspicion={suspicionFraction:F2})");
        }

        SetApproachSpeed(speed);
    }

    private void UpdateFootstepAudio()
    {
        bool shouldPlay = approachController != null && approachController.IsApproaching &&
                          !approachController.IsRushIn;
        _isGrassFootstepRoute = approachController != null && approachController.IsGardenRoute;
        AudioSource activeSource = _isGrassFootstepRoute
            ? gardenFootstepAudioSource
            : hallwayFootstepAudioSource;

        if (approachController != null)
        {
            _targetFootstepVolume = approachController.ReachedDoor
                ? nearDoorVolume
                : approachController.IsInHallwayPhase
                    ? midVolume
                    : farVolume;
        }

        if (!shouldPlay || activeSource == null)
        {
            StopFootstepAudioSources();
            _currentFootstepVolume = farVolume;
            _targetFootstepVolume = farVolume;
            return;
        }

        StopInactiveFootstepAudioSources(activeSource);
        if (!activeSource.isPlaying)
        {
            activeSource.loop = true;
            activeSource.volume = _currentFootstepVolume;
            activeSource.Play();
        }

        _currentFootstepVolume = Mathf.MoveTowards(
            _currentFootstepVolume, _targetFootstepVolume, volumeChangeSpeed * Time.deltaTime);
        activeSource.volume = _currentFootstepVolume;
    }

    private void HandleWalkingStateChanged(bool isWalking)
    {
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

        // 覗き再生中（ドア覗き／庭覗き）は、アニメーションの担当を覗き側に委ねる。
        // ここで Walk を書き換えると再生直後の覗きが歩行に戻されてしまうため、停止中として扱う。
        motherAnimator.SetBool("Walk", isWalking && !IsPeekAnimationActive());
    }

    /// <summary>
    /// 覗きアニメーションを再生中か（＝歩行アニメーションで上書きしてはいけない状態か）。
    /// ドア覗き（isMotherLookingNow）と庭覗き（approachController.IsGardenPeeking）の両方を対象にする。
    /// </summary>
    private bool IsPeekAnimationActive()
    {
        if (isMotherLookingNow) return true;
        return approachController != null && approachController.IsGardenPeeking;
    }

    /// <summary>
    /// 歩行アニメーションの状態を、現在の覗き状態を考慮して今すぐ反映し直す。
    /// 覗き開始・終了の直後に呼び、Walkの取り違えが残らないようにする。
    /// </summary>
    private void RefreshWalkingAnimationState()
    {
        if (motherAnimator == null) return;
        if (!HasAnimatorParameter("Walk", AnimatorControllerParameterType.Bool)) return;

        bool isWalking = approachController != null && approachController.IsApproaching;
        motherAnimator.SetBool("Walk", isWalking && !IsPeekAnimationActive());
    }

    private void TriggerPeekAnimation(string triggerName)
    {
        if (motherAnimator == null || !HasAnimatorParameter(triggerName, AnimatorControllerParameterType.Trigger))
        {
            LogAnimatorWarning();
            return;
        }

        motherAnimator.ResetTrigger(triggerName);
        motherAnimator.SetTrigger(triggerName);
    }

    private void InitializeFootstepAudioSource(AudioSource source)
    {
        if (source == null) return;
        source.loop = true;
        source.Stop();
        source.volume = farVolume;
    }

    private void StopInactiveFootstepAudioSources(AudioSource activeSource)
    {
        AudioSource inactiveSource = activeSource == hallwayFootstepAudioSource
            ? gardenFootstepAudioSource
            : hallwayFootstepAudioSource;
        if (inactiveSource != null)
            inactiveSource.Stop();
    }

    private void StopFootstepAudioSources()
    {
        if (hallwayFootstepAudioSource != null) hallwayFootstepAudioSource.Stop();
        if (gardenFootstepAudioSource != null) gardenFootstepAudioSource.Stop();
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

    private void LogAnimatorWarning()
    {
        if (_animatorWarningLogged) return;
        _animatorWarningLogged = true;
        Debug.LogWarning(
            "[ParentDetection] 母親AnimatorまたはWalk/Peekパラメータが未設定のため、アニメーション制御をスキップします。",
            this);
    }
    // ── 部屋入室（案B）：ParentApproachControllerイベントの購読 ────────────────

    private void SubscribeApproachEvents()
    {
        if (_approachEventsSubscribed || approachController == null) return;

        approachController.onEnteredRoom.AddListener(HandleEnteredRoom);
        approachController.onExitedRoom.AddListener(HandleExitedRoom);
        approachController.onGardenPeekStarted.AddListener(HandleGardenPeekStarted);

        _approachEventsSubscribed = true;
        Debug.Log("[PD] Subscribed to ParentApproachController events (OnEnteredRoom/OnExitedRoom/OnGardenPeekStarted)");
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
    /// ParentApproachControllerから、親機が部屋内部への移動を完了し入室したときに呼び出される。
    /// ここで初めて部屋侵入時の疑惑（バースト／継続疑惑／睡眠退出待ち）を開始する。
    /// </summary>
    private void HandleEnteredRoom()
    {
        Debug.Log($"[PD] OnEnteredRoom | roomEntryAccepted={_roomEntryAccepted} roomEntryStarted={_roomEntryStarted} isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");

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
    /// ParentApproachControllerから、親機が部屋内部から退室しdoorPointへ戻ったときに呼び出される。
    /// 退室順序の最終段（ドアを閉じる→ResetCycle→EndWarningSequence）をここで実行する。
    /// </summary>
    private void HandleExitedRoom()
    {
        Debug.Log($"[PD] OnExitedRoom | roomEntryStarted={_roomEntryStarted} isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");
        _roomExitCompleted = true;

        // ゲームオーバー確定後はドア状態・疑惑状態を変更しない（従来のサイクル終了と同じ扱い）。
        if (isCaught || _hasPermanentGameOver) return;

        if (mainDoorCloseAudioSource != null)
            mainDoorCloseAudioSource.Play();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();

        Debug.Log("[PD] OnExitedRoom: cycle finished — door closed, warning sequence ended");
    }



    private void Update()
    {
        UpdateFootstepAudio();

        // 覗き機能削除に伴い、覗き見による即時ゲームオーバー判定は廃止する。

        if (Keyboard.current == null) return;

        if (Keyboard.current.digit0Key.wasPressedThisFrame)
        {
            Debug.Log("[PD] 0 key — triggering loud item (rush-in)");
            OnLoudItemTriggered();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  公開API — ParentWarningSystemから呼び出される
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ParentWarningSystemから、親機がドアで停止したときに呼び出される。
    /// warningSystem.ActiveRouteで分岐する — ルートは移動開始前に決定済み。
    /// ActiveRouteがNoneの場合（例：Pキーのデバッグ）のみdummyProbabilityにフォールバックする。
    /// </summary>
    public void OnApproachReachedDoor()
    {
        Debug.Log($"[PD] OnApproachReachedDoor | isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");
        if (isCaught || _hasPermanentGameOver) return;

        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
        _activePeekDuration = peekDurationBase + gauge;
        Debug.Log($"[PD] activePeekDuration={_activePeekDuration:F1}s (base={peekDurationBase:F1} + gauge={gauge})");

        bool primary;
        var route = (warningSystem != null) ? warningSystem.ActiveRoute : ParentWarningSystem.RouteState.None;

        if (route == ParentWarningSystem.RouteState.DoorPeek)
        {
            primary = true;
            Debug.Log("[PD] Branch: PRIMARY — from ActiveRoute=DoorPeek");
        }
        else if (route == ParentWarningSystem.RouteState.HallwayPassBy)
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
        _roomEntryStarted  = false;
        _roomExitCompleted = false;

        // Primaryの場合のみ、親機へ部屋入室を要求する。
        // 受理された場合は、疑惑（バースト／継続疑惑）の開始を「部屋入室完了（OnEnteredRoom）」まで遅らせる。
        // 受理されなかった場合は、従来どおりドア停止時点で疑惑を開始する。
        if (primary && approachController != null)
            _roomEntryAccepted = approachController.RequestRoomEntry();

        TriggerFinalEvent(primary: primary);
    }

    /// <summary>
    /// ParentWarningSystemから、親機が停止せず通過したときに呼び出される。
    /// 疑惑増加やドアイベントを発生させず、サイクルを正常にリセットする。
    /// </summary>
    public void OnApproachPassedBy()
    {
        Debug.Log($"[PD] OnApproachPassedBy | isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");
        if (isCaught || _hasPermanentGameOver) return;

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();
    }

    public void NotifyGameOver()
    {
        CancelPassByDoorSound();
        _hasPermanentGameOver = true;
        if (warningSystem != null)
            warningSystem.NotifyGameOver();
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

        if (mainDoorOpenAudioSource != null)
            mainDoorOpenAudioSource.Play();
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

        if (mainDoorCloseAudioSource != null)
            mainDoorCloseAudioSource.Play();
        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        ResetCycle();
        if (warningSystem != null)
            warningSystem.EndWarningSequence();
        _rushInPeekCoroutine = null;
    }

    /// <summary>
    /// 子機の大きな音のアイテムが発生したとき（または0キーのデバッグ時）に呼び出される。
    /// 突入音を再生し、ゲージを加算してからParentWarningSystem.StartLoudItemRushInSequence()へ引き渡す。
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
    //  最終イベント分岐
    // ──────────────────────────────────────────────────────────────────────────

    private void TriggerFinalEvent(bool primary)
    {
        if (primary) TriggerPrimaryEvent();
        else         TriggerDummyEvent();
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

        if (mainDoorOpenAudioSource != null)
            mainDoorOpenAudioSource.Play();

        if (caughtReactionController != null)
            caughtReactionController.OnMotherCheck(isFullCheck: true);

        // 部屋入室（案B）：入室が受理された場合は、入室完了（OnEnteredRoom）まで疑惑を開始しない。
        if (_roomEntryAccepted)
        {
            Debug.Log("[PD] Room entry accepted — suspicion burst/continuous will start on OnEnteredRoom (mother is walking into the room)");
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
        Debug.Log($"[PD] Room entry | isMotherLookingNow=true | IsSleeping={playerIsSleeping} | gauge before={gaugeBefore}");

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
                Debug.Log("[PD] HandlePrimaryResetSequence: room exit already completed — aborting (finished by OnExitedRoom)");
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
            Debug.Log("[PD] HandlePrimaryResetSequence: room exit already completed — skipping door close (finished by OnExitedRoom)");
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
                            Debug.LogWarning($"[PD] OnExitedRoom not received within {exitTimeout:F1}s — closing the door anyway");
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

        if (mainDoorCloseAudioSource != null)
            mainDoorCloseAudioSource.Play();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        // 【重要】ここで ResetCycle() を呼ばない。
        // ResetCycle() は _primaryResetCoroutine（＝このコルーチン自身）を StopCoroutine するため、
        // これを先に呼ぶと以降の帰路要求・待機が実行されずに打ち切られてしまう。
        // 発見判定の解除だけを先に行い、サイクル全体のリセットは帰路完了後に一度だけ行う。

        // 発見判定の解除（覗き終了）。戻っているだけなのに発見し続けないための最小限の解除。
        isMotherLookingNow = false;

        // 【帰路】ResetApproach で初期位置へ瞬間復帰させる前に、帰路（Turn Back → 帰路List → 画面外）を
        // ParentApproachController へ要求し、受付から完了／失敗まで待つ。
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
                Debug.LogWarning($"[PD] ReturnHome did not finish within {returnTimeout:F1}s — aborting the return trip");
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

        if (dummyDoorAudioSource != null) dummyDoorAudioSource.Play();

        if (caughtReactionController != null)
            caughtReactionController.OnMotherCheck(isFullCheck: false);

        if (_dummyResetCoroutine != null) StopCoroutine(_dummyResetCoroutine);
        _dummyResetCoroutine = StartCoroutine(HandleDummySequence());
    }

    private IEnumerator HandleDummySequence()
    {
        Debug.Log($"[PD] HandleDummySequence: activePeekDuration={_activePeekDuration:F1}s");
        yield return new WaitForSeconds(_activePeekDuration);

        if (mainDoorCloseAudioSource != null) mainDoorCloseAudioSource.Play();

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

        Debug.Log($"[PD] TriggerCatFeintEvent — cat walks StartPoint→DoorPoint, then door PEEK open for {duration:F1}s（母親は登場しない）");
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
        yield return catFeintController.MoveAlongDoorRoute(
            () => warningSystem != null && warningSystem.isWarningActive);

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

        if (dummyDoorAudioSource != null)
            dummyDoorAudioSource.Play();

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

        if (mainDoorCloseAudioSource != null)
            mainDoorCloseAudioSource.Play();

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
            int  contGauge    = (motherGauge != null) ? motherGauge.currentGauge : 0;
            Debug.Log($"[PD] Continuous room suspicion state | motherLooking={isMotherLookingNow} | playerSleeping={contSleeping} | gaugeBefore={contGauge}");

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
            Debug.Log($"[PD] Continuous room suspicion tick +{continuousRoomSuspicionAmount} | gauge now {motherGauge.currentGauge}");

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
    /// 庭覗きの待機開始（ParentApproachController.onGardenPeekStarted）で継続疑惑を開始する。
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
            Debug.LogWarning($"[PD] 庭覗き中の継続疑惑を無効化 | gardenPeekSuspicionIntervalMultiplier={gardenPeekSuspicionIntervalMultiplier:F2} | continuousRoomSuspicionTickInterval={continuousRoomSuspicionTickInterval:F2}（0以下のため異常な高速加算を避ける）", this);
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
        Debug.Log($"[PD] 庭覗き中の継続疑惑 開始 | tickInterval={tickInterval:F2}s | amount={continuousRoomSuspicionAmount}（ドア側{continuousRoomSuspicionTickInterval:F2}s×{gardenPeekSuspicionIntervalMultiplier:F2}）");

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
        CancelPassByDoorSound();
    }

    /// <summary>
    /// 廊下通過完了後の偽ドア音を、従来と同じ1回だけ遅延再生する。
    /// </summary>
    public void PlayPassByDoorSound()
    {
        CancelPassByDoorSound();
        _passByDoorSoundCoroutine = StartCoroutine(PlayPassByDoorSoundCoroutine());
    }

    public void CancelPassByDoorSound()
    {
        if (_passByDoorSoundCoroutine == null) return;
        StopCoroutine(_passByDoorSoundCoroutine);
        _passByDoorSoundCoroutine = null;
    }

    private IEnumerator PlayPassByDoorSoundCoroutine()
    {
        float delay = Mathf.Max(0f, passByDoorSoundDelay);
        Debug.Log($"[ParentDetection] Pass-by door sound: waiting {delay:F1}s");
        yield return new WaitForSeconds(delay);

        if (passByDoorAudioSource != null)
        {
            Debug.Log("[ParentDetection] Pass-by door sound: PLAY");
            passByDoorAudioSource.Play();
        }

        _passByDoorSoundCoroutine = null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  サイクルリセット
    // ──────────────────────────────────────────────────────────────────────────

    private void ResetCycle()
    {
        Debug.Log("[PD] ResetCycle");
        SetApproachSpeed(approachMoveSpeedMin);

        if (_dummyResetCoroutine != null)      { StopCoroutine(_dummyResetCoroutine);      _dummyResetCoroutine      = null; }
        if (_primaryResetCoroutine != null)    { StopCoroutine(_primaryResetCoroutine);    _primaryResetCoroutine    = null; }
        if (_continuousRoomCoroutine != null)  { StopCoroutine(_continuousRoomCoroutine);  _continuousRoomCoroutine  = null; Debug.Log("[PD] Continuous room suspicion stopped — ResetCycle"); }
        if (_gardenPeekSuspicionCoroutine != null) { StopCoroutine(_gardenPeekSuspicionCoroutine); _gardenPeekSuspicionCoroutine = null; Debug.Log("[PD] 庭覗き中の継続疑惑 stopped — ResetCycle"); }
        if (_rushInPeekCoroutine != null) { StopCoroutine(_rushInPeekCoroutine); _rushInPeekCoroutine = null; }

        isMotherLookingNow    = false;
        _activePeekDuration   = peekDurationBase;

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  ゲームオーバー
    // ──────────────────────────────────────────────────────────────────────────

    private void OnPlayerCaught()
    {
        Debug.Log("[PD] OnPlayerCaught — GAME OVER");
        CancelPassByDoorSound();
        isCaught           = true;
        isMotherLookingNow = true;
        Debug.LogError("ゲームオーバー：母親に捕まりました！");

        if (caughtReactionController != null)
            caughtReactionController.ForceGameOver();
    }
}