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
    public bool isCaught          = false;
    public bool isMotherLookingNow = false;

    // ── 非公開状態 ────────────────────────────────────────────────────────────
    private Coroutine    dummyResetCoroutine        = null;
    private Coroutine    primaryResetCoroutine      = null;
    private Coroutine    continuousRoomCoroutine    = null;
    private bool         hasPermanentGameOver       = false;
    private float        _activePeekDuration        = 3f;

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
        Debug.Log($"[PDV2] OnApproachReachedDoor | isCaught={isCaught} hasPermanentGameOver={hasPermanentGameOver}");
        if (isCaught || hasPermanentGameOver) return;

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

        TriggerFinalEvent(primary: primary);
    }

    /// <summary>
    /// ParentWarningSystemから、親機が停止せず通過したときに呼び出される。
    /// 疑惑増加やドアイベントを発生させず、サイクルを正常にリセットする。
    /// </summary>
    public void OnApproachPassedBy()
    {
        Debug.Log($"[PDV2] OnApproachPassedBy | isCaught={isCaught} hasPermanentGameOver={hasPermanentGameOver}");
        if (isCaught || hasPermanentGameOver) return;

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();
    }

    public void NotifyGameOver()
    {
        hasPermanentGameOver = true;
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

        if (isCaught || hasPermanentGameOver) return;

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

        if (!hasPermanentGameOver)
        {
            if (primaryResetCoroutine != null) StopCoroutine(primaryResetCoroutine);
            primaryResetCoroutine = StartCoroutine(HandlePrimaryResetSequence());
        }

        if (enableContinuousRoomSuspicion && !hasPermanentGameOver)
        {
            if (continuousRoomCoroutine != null) StopCoroutine(continuousRoomCoroutine);
            continuousRoomCoroutine = StartCoroutine(ContinuousRoomSuspicionCoroutine());
            Debug.Log("[PDV2] Continuous room suspicion started");
        }

    }

    private IEnumerator HandlePrimaryResetSequence()
    {
        Debug.Log("[PDV2] HandlePrimaryResetSequence: waiting for player sleep");

        float elapsed = 0f;
        float timeout = Mathf.Max(0f, roomCheckSafetyTimeout);

        while (true)
        {
            if (hasPermanentGameOver || isCaught)
                yield break;

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

        if (hasPermanentGameOver || isCaught)
            yield break;

        if (mainDoorCloseAudioSource != null)
            mainDoorCloseAudioSource.Play();

        if (targetDoorController != null)
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);

        ResetCycle();

        if (warningSystem != null)
            warningSystem.EndWarningSequence();

        primaryResetCoroutine = null;
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

        if (dummyResetCoroutine != null) StopCoroutine(dummyResetCoroutine);
        dummyResetCoroutine = StartCoroutine(HandleDummySequence());
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

        dummyResetCoroutine = null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  部屋侵入時の疑惑バースト
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator RoomEntryBurstSuspicionCoroutine()
    {
        for (int i = 0; i < 3; i++)
        {
            if (hasPermanentGameOver || isCaught) yield break;
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

            bool _contSleeping = (sleepingController != null) && sleepingController.IsSleeping;
            int  _contGauge    = (motherGauge != null) ? motherGauge.currentGauge : 0;
            Debug.Log($"[PDV2] Continuous room suspicion state | motherLooking={isMotherLookingNow} | playerSleeping={_contSleeping} | gaugeBefore={_contGauge}");

            if (hasPermanentGameOver || isCaught)
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

        if (dummyResetCoroutine != null)      { StopCoroutine(dummyResetCoroutine);      dummyResetCoroutine      = null; }
        if (primaryResetCoroutine != null)    { StopCoroutine(primaryResetCoroutine);    primaryResetCoroutine    = null; }
        if (continuousRoomCoroutine != null)  { StopCoroutine(continuousRoomCoroutine);  continuousRoomCoroutine  = null; Debug.Log("[PDV2] Continuous room suspicion stopped — ResetCycle"); }

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