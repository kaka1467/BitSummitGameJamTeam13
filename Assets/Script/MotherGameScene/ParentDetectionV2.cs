using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentDetectionV2:
/// ドアイベント／分岐のみを制御する。
///
/// 責務の境界：
///   ParentApproachController  — 移動とルート演出
///   ParentWarningSystem       — シーケンス調整
///   ParentWarningScheduler    — タイミングとN/Mデバッグキー
///   ParentDetectionV2（本クラス）— 親機のドア到着／通過に反応し、
///                               分岐、ドア状態、サイクルリセット、大きな音を処理する
///
/// ゲージへの書き込みは次の4つの場合に行う：
///   OnLoudItemTriggered              — loudItemGaugeAmountに応じたAddGauge()の段階加算
///   TriggerPrimaryEvent              — 親機が部屋に入ったときの初回AddGauge()（睡眠中でない場合）
///   ContinuousRoomSuspicionCoroutine — ドアが開き、プレイヤーが睡眠中でない間の時間制AddGauge()
/// 現在のルートの覗き見時間はpeekDurationBase + motherGauge.currentGauge。
/// ルート分岐（本チェックか覗き見か）はwarningSystem.ActiveRouteで決まる。
/// dummyProbabilityはActiveRouteがNoneの場合（例：Pキーのデバッグ）のみ予備として使用する。
/// </summary>
public class ParentDetectionV2 : MonoBehaviour
{
    // ── システム参照 ──────────────────────────────────────────────────────────
    [Header("システム参照")]
    public ParentWarningSystem       warningSystem;
    public CaughtReactionController  caughtReactionController;
    public MotherGauge               motherGauge;
    public ParentApproachController  approachController;
    public SleepingController        sleepingController;

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

    // ── ドア ──────────────────────────────────────────────────────────────────
    [Header("ドア制御")]
    [SerializeField] private DoorController targetDoorController;

    // ── 分岐 ──────────────────────────────────────────────────────────────────
    [Header("イベント分岐")]
    [Tooltip("ParentWarningSystemからルート状態を取得できない場合のダミー（覗き見）チェック確率（例：Pキーのデバッグ）。")]
    [SerializeField, Range(0f, 1f)] private float dummyProbability = 0.3f;

    // ── 覗き見／部屋チェックのタイミング ─────────────────────────────────────
    [Header("部屋チェックのタイミング")]
    [Tooltip("ダミー（覗き見のみ）イベントの基本時間（秒）。実際の時間=peekDurationBase + currentGauge。")]
    [SerializeField] private float peekDurationBase = 3f;
    [Tooltip("本チェック（全開）イベントで、プレイヤーが枕で眠るまで親機が部屋に留まる時間。 " +
             "一度も眠らない場合は、安全タイムアウトとしてこの秒数後に親機が退出する。")]
    [SerializeField] private float roomCheckSafetyTimeout = 30f;
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
    [Tooltip("無効にすると、Lキーおよびゲーム内の大きな音のアイテムトリガーが完全に無効になる。")]
    [SerializeField] private bool enableLoudItemFeature = true;
    [Tooltip("trueの場合、大きな音のアイテムが進行中の警告を中断し、突入を強制する。")]
    [SerializeField] private bool forceLoudItemDuringWarning = false;
    [Tooltip("大きな音のアイテム発生時にMotherGaugeへ加算する段階数。最大値に達した場合は突入せず即座にゲームオーバーになる。")]
    [SerializeField] private int loudItemGaugeAmount = 3;

    // ── 部屋内の継続疑惑 ──────────────────────────────────────────────────────
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
    private bool         _hasPermanentGameOver;
    private float        _activePeekDuration        = 3f;

    // ── 部屋入室（案B）：疑惑開始を「部屋入室完了」にずらすための状態 ────────────
    private bool         _roomEntryAccepted;        // このサイクルでRequestRoomEntry()が受理されたか
    private bool         _roomEntryStarted;         // OnEnteredRoom後に疑惑コルーチンを開始済みか
    private bool         _roomExitCompleted;        // OnExitedRoomを受信済みか
    private bool         _approachEventsSubscribed; // ParentApproachControllerの入退室イベントを購読中か
    private int          _roomCycleId;              // OnApproachReachedDoorのたびに増えるサイクル識別子
    private int          _primaryResetCycleId;      // HandlePrimaryResetSequence開始時点の_roomCycleId

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

        // 部屋入室（案B）：入室完了／退室完了の通知を受け取る。
        // approachControllerはコード上で解決するため、OnEnable()ではなくStart()末尾で購読する。
        SubscribeApproachEvents();
    }

    private void OnDestroy()
    {
        UnsubscribeApproachEvents();
    }
    // ── 部屋入室（案B）：ParentApproachControllerイベントの購読 ────────────────

    private void SubscribeApproachEvents()
    {
        if (_approachEventsSubscribed || approachController == null) return;

        approachController.onEnteredRoom.AddListener(HandleEnteredRoom);
        approachController.onExitedRoom.AddListener(HandleExitedRoom);

        _approachEventsSubscribed = true;
        Debug.Log("[PDV2] Subscribed to ParentApproachController room events (OnEnteredRoom/OnExitedRoom)");
    }

    private void UnsubscribeApproachEvents()
    {
        if (!_approachEventsSubscribed || approachController == null) return;

        approachController.onEnteredRoom.RemoveListener(HandleEnteredRoom);
        approachController.onExitedRoom.RemoveListener(HandleExitedRoom);

        _approachEventsSubscribed = false;
    }

    /// <summary>
    /// ParentApproachControllerから、親機が部屋内部への移動を完了し入室したときに呼び出される。
    /// ここで初めて部屋侵入時の疑惑（バースト／継続疑惑／睡眠退出待ち）を開始する。
    /// </summary>
    private void HandleEnteredRoom()
    {
        Debug.Log($"[PDV2] OnEnteredRoom | roomEntryAccepted={_roomEntryAccepted} roomEntryStarted={_roomEntryStarted} isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");

        if (isCaught || _hasPermanentGameOver) return;

        // 入室が受理されていないサイクル（ダミー／覗き／通過／突入など）では疑惑を開始しない。
        if (!_roomEntryAccepted)
        {
            Debug.Log("[PDV2] OnEnteredRoom: ignored — room entry was not accepted for this cycle");
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
        Debug.Log($"[PDV2] OnExitedRoom | roomEntryStarted={_roomEntryStarted} isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");
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

        Debug.Log("[PDV2] OnExitedRoom: cycle finished — door closed, warning sequence ended");
    }



    private void Update()
    {
        // 覗き機能削除に伴い、覗き見による即時ゲームオーバー判定は廃止する。

        if (Keyboard.current == null) return;

        if (Keyboard.current.pKey.wasPressedThisFrame)
        {
            Debug.Log("[PDV2] P key — forcing primary (full) check");
            TriggerFinalEvent(primary: true);
        }

        if (Keyboard.current.oKey.wasPressedThisFrame)
        {
            Debug.Log("[PDV2] O key — forcing dummy (peek) check");
            TriggerFinalEvent(primary: false);
        }

        if (Keyboard.current.lKey.wasPressedThisFrame)
        {
            Debug.Log("[PDV2] L key — triggering loud item");
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
        Debug.Log($"[PDV2] OnApproachReachedDoor | isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");
        if (isCaught || _hasPermanentGameOver) return;

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
        Debug.Log($"[PDV2] OnApproachPassedBy | isCaught={isCaught} hasPermanentGameOver={_hasPermanentGameOver}");
        if (isCaught || _hasPermanentGameOver) return;

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();
    }

    public void NotifyGameOver()
    {
        _hasPermanentGameOver = true;
    }

    /// <summary>
    /// 子機の大きな音のアイテムが発生したとき（またはLキーのデバッグ時）に呼び出される。
    /// 突入音を再生し、ゲージを加算してからParentWarningSystem.StartLoudItemRushInSequence()へ引き渡す。
    /// 警告シーケンスがすでに進行中の場合は完全に抑制する。
    /// </summary>
    public void OnLoudItemTriggered()
    {
        if (!enableLoudItemFeature)
        {
            Debug.Log("[PDV2] Loud Item Feature is DISABLED");
            return;
        }

        if (isCaught || _hasPermanentGameOver) return;

        if (warningSystem != null && warningSystem.isWarningActive)
        {
            if (!forceLoudItemDuringWarning)
            {
                Debug.Log("[PDV2] Loud item ignored — warning sequence already active");
                return;
            }

            Debug.Log("[PDV2] Loud item overriding active warning — forcing rush-in");
            warningSystem.StopWarningSequence();
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
    //  最終イベント分岐
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

        // 部屋入室（案B）：入室が受理された場合は、入室完了（OnEnteredRoom）まで疑惑を開始しない。
        if (_roomEntryAccepted)
        {
            Debug.Log("[PDV2] Room entry accepted — suspicion burst/continuous will start on OnEnteredRoom (mother is walking into the room)");
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

        if (!_hasPermanentGameOver)
        {
            if (_primaryResetCoroutine != null) StopCoroutine(_primaryResetCoroutine);
            _primaryResetCoroutine = StartCoroutine(HandlePrimaryResetSequence());
        }

        if (enableContinuousRoomSuspicion && !_hasPermanentGameOver)
        {
            if (_continuousRoomCoroutine != null) StopCoroutine(_continuousRoomCoroutine);
            _continuousRoomCoroutine = StartCoroutine(ContinuousRoomSuspicionCoroutine());
            Debug.Log("[PDV2] Continuous room suspicion started");
        }
    }

    private IEnumerator HandlePrimaryResetSequence()
    {
        Debug.Log("[PDV2] HandlePrimaryResetSequence: waiting for player sleep");

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
                Debug.Log("[PDV2] HandlePrimaryResetSequence: a newer cycle has started — aborting");
                _primaryResetCoroutine = null;
                yield break;
            }

            // 部屋からの退室が既に完了している場合（親機側の滞在タイムアウト等）は、
            // ドア閉・ResetCycle・EndWarningSequenceはOnExitedRoom側で完了済みなので打ち切る。
            if (_roomExitCompleted)
            {
                Debug.Log("[PDV2] HandlePrimaryResetSequence: room exit already completed — aborting (finished by OnExitedRoom)");
                _primaryResetCoroutine = null;
                yield break;
            }

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

        if (_hasPermanentGameOver || isCaught)
        {
            _primaryResetCoroutine = null;
            yield break;
        }

        if (_roomCycleId != _primaryResetCycleId)
        {
            Debug.Log("[PDV2] HandlePrimaryResetSequence: a newer cycle has started — aborting");
            _primaryResetCoroutine = null;
            yield break;
        }

        if (_roomExitCompleted)
        {
            // 滞在タイムアウト等で先に退室が完了している — 終了処理はOnExitedRoom側で完了済み。
            Debug.Log("[PDV2] HandlePrimaryResetSequence: room exit already completed — skipping door close (finished by OnExitedRoom)");
            _primaryResetCoroutine = null;
            yield break;
        }

        // 部屋入室（案B）：入室済みの場合は、親機へ退室を要求し、doorPointへ戻るまで待つ。
        // ドアを閉じる／ResetCycle／EndWarningSequenceは退室完了（OnExitedRoom）側で実行する。
        if (_roomEntryStarted && approachController != null)
        {
            if (approachController.RequestLeaveRoom())
            {
                Debug.Log("[PDV2] RequestLeaveRoom sent — waiting for the mother to leave the room");

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
                            Debug.LogWarning($"[PDV2] OnExitedRoom not received within {exitTimeout:F1}s — closing the door anyway");
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
                    Debug.Log("[PDV2] HandlePrimaryResetSequence: a newer cycle has started — aborting");
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
                Debug.LogWarning("[PDV2] RequestLeaveRoom was rejected — falling back to the legacy door close");
            }
        }

        if (mainDoorCloseAudioSource != null)
            mainDoorCloseAudioSource.Play();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();

        _primaryResetCoroutine = null;
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

        if (_dummyResetCoroutine != null) StopCoroutine(_dummyResetCoroutine);
        _dummyResetCoroutine = StartCoroutine(HandleDummySequence());
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

        _dummyResetCoroutine = null;
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
    //  部屋内の継続疑惑
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator ContinuousRoomSuspicionCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(continuousRoomSuspicionTickInterval);

            bool contSleeping = (sleepingController != null) && sleepingController.IsSleeping;
            int  contGauge    = (motherGauge != null) ? motherGauge.currentGauge : 0;
            Debug.Log($"[PDV2] Continuous room suspicion state | motherLooking={isMotherLookingNow} | playerSleeping={contSleeping} | gaugeBefore={contGauge}");

            if (_hasPermanentGameOver || isCaught)
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
    //  廊下からの覗き見疑惑
    // ──────────────────────────────────────────────────────────────────────────

    public void OnApproachStarted()
    {
        // 覗き機能削除に伴い、廊下覗き見による疑惑加算は実行しない。
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  サイクルリセット
    // ──────────────────────────────────────────────────────────────────────────

    private void ResetCycle()
    {
        Debug.Log("[PDV2] ResetCycle");

        if (_dummyResetCoroutine != null)      { StopCoroutine(_dummyResetCoroutine);      _dummyResetCoroutine      = null; }
        if (_primaryResetCoroutine != null)    { StopCoroutine(_primaryResetCoroutine);    _primaryResetCoroutine    = null; }
        if (_continuousRoomCoroutine != null)  { StopCoroutine(_continuousRoomCoroutine);  _continuousRoomCoroutine  = null; Debug.Log("[PDV2] Continuous room suspicion stopped — ResetCycle"); }

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
        Debug.Log("[PDV2] OnPlayerCaught — GAME OVER");
        isCaught           = true;
        isMotherLookingNow = true;
        Debug.LogError("ゲームオーバー：母親に捕まりました！");

        if (caughtReactionController != null)
            caughtReactionController.ForceGameOver();
    }
}