using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentWarningScheduler:
/// MotherApproachWarningを自動的に発生させる（タイミング管理の唯一の管理元）。
/// </summary>
public class ParentWarningScheduler : MonoBehaviour
{
    [Header("システム参照")]
    [Tooltip("制御対象のMotherApproachWarning")]
    public MotherApproachWarning warningSystem;
    public MotherGauge motherGauge;

    [Header("スケジューラー設定")]
    [Tooltip("警告を自動的に発生させる")]
    public bool autoTrigger = true;

    [Tooltip("シーン開始後、この秒数の間は親機が自動接近しません。")]
    public float graceSeconds = 15f;

    [Header("イベント後の最低休憩")]
    [Tooltip("通常の親イベント（覗き・通過など）が終了した後、次の予告を開始するまでに必ず確保する最低休憩時間（秒）。")]
    [SerializeField] public float postEventRestSeconds = 3f;

    [Tooltip("強い親イベント（突入・ドア覗き・庭覗き・片付けなど）が終了した後、次の予告を開始するまでに必ず確保する最低休憩時間（秒）。")]
    [SerializeField] public float postStrongEventRestSeconds = 8f;

    [Header("イベント後のランダム追加待機（怪しさ段階別の上限）")]
    [Tooltip("怪しさが低（青：ゲージ0〜3）のときの追加待機の上限（秒）。0〜この値から1回だけ抽選する。")]
    [SerializeField, Min(0f)] private float lowSuspicionExtraWaitMaxSeconds = 7f;

    [Tooltip("怪しさが中（紫：ゲージ4〜6）のときの追加待機の上限（秒）。0〜この値から1回だけ抽選する。")]
    [SerializeField, Min(0f)] private float midSuspicionExtraWaitMaxSeconds = 5f;

    [Tooltip("怪しさが高（赤：ゲージ7〜）のときの追加待機の上限（秒）。0〜この値から1回だけ抽選する。")]
    [SerializeField, Min(0f)] private float highSuspicionExtraWaitMaxSeconds = 3f;

    [Header("デバッグ")]
    [Tooltip("次のランダム追加待機の残り時間（秒）。未抽選のときは0。")]
    public float timeUntilNextWarning;

    [SerializeField] private bool showDebugLogs = true;

    private Coroutine _schedulerCoroutine;
    private Coroutine _triggerSoonCoroutine;
    private bool _gracePeriodOver;
    private bool _blockedByChore;
    private bool _waitingForActiveWarningLogged;

    private bool _wasWarningActive;
    private bool _lastEventWasStrong;
    private float _restRemaining;

    private MotherSuspicionSystem _parentDetection;
    private MotherApproachController _approachController;

    private bool _playFinished;
    private bool _playFinishedLogged;

    public bool IsGracePeriodOver => _gracePeriodOver;

    public bool IsBlockedExternally => _blockedByChore;

    public void SetBlockedByChore(bool blocked)
    {
        if (_blockedByChore == blocked) return;
        _blockedByChore = blocked;
        Debug.Log($"[ParentWarningScheduler] 片付け演出による自動警告抑止: {blocked}");

        if (!blocked)
        {
            StartPostEventRest(true);
        }

        if (!blocked && autoTrigger && _schedulerCoroutine == null && !isWarningActiveSatisfied())
        {
            StartScheduler();
        }
    }

    private void TrackEventEnd()
    {
        DetectEventEnd();

        if (_restRemaining > 0f && !_blockedByChore)
            _restRemaining = Mathf.Max(0f, _restRemaining - Time.deltaTime);
    }

    private void DetectEventEnd()
    {
        bool active = isWarningActiveSatisfied();

        if (active)
            _lastEventWasStrong = IsStrongEventActive();

        if (_wasWarningActive && !active)
        {
            StartPostEventRest(_lastEventWasStrong);
            _lastEventWasStrong = false;
        }

        _wasWarningActive = active;
    }

    private bool IsStrongEventActive()
    {
        if (_approachController != null && (_approachController.IsRushIn || _approachController.CycleStartedAsRushIn)) return true;
        if (warningSystem != null &&
            (warningSystem.ActiveRoute == MotherApproachWarning.RouteState.DoorPeek ||
             warningSystem.ActiveRoute == MotherApproachWarning.RouteState.GardenPeek)) return true;

        return false;
    }

    private void StartPostEventRest(bool strong)
    {
        float baseRest = Mathf.Max(0f, strong ? postStrongEventRestSeconds : postEventRestSeconds);
        float rest = baseRest * GameDifficulty.ParentWarningIntervalMultiplier;
        if (rest <= 0f || rest <= _restRemaining) return;

        _restRemaining = rest;
        Debug.Log($"[ParentWarningScheduler] イベント後の最低休憩を開始: {rest:F2}s（強イベント={strong}, diff={GameDifficulty.DisplayName}）");
    }

    private IEnumerator WaitForPostEventRest()
    {
        DetectEventEnd();

        bool waited = false;
        while (_restRemaining > 0f)
        {
            if (LatchPlayFinishedFromCoroutine()) yield break;

            waited = true;
            timeUntilNextWarning = _restRemaining;
            yield return null;
        }

        if (waited && _restRemaining <= 0f && showDebugLogs)
            Debug.Log("[ParentWarningScheduler] 最低休憩の消化が完了 — 追加待機の抽選へ進みます");
    }

    private bool isWarningActiveSatisfied()
    {
        return warningSystem != null && warningSystem.isWarningActive;
    }

    private bool IsPlayFinished()
    {
        if (_playFinished) return true;
        return ParentGameTimeManager.EvaluateAutoStart() == ParentGameTimeManager.AutoStartDecision.RejectPlayFinished;
    }

    private bool IsStartUnavailable()
    {
        if (_playFinished) return false;
        return ParentGameTimeManager.EvaluateAutoStart() == ParentGameTimeManager.AutoStartDecision.RejectUnavailable;
    }

    private bool TryLatchPlayFinished()
    {
        if (_playFinished) return true;

        if (IsStartUnavailable())
        {
            ParentGameTimeManager.LogUnavailableRejectOnce();
            StopSchedulerInternal();
            return true;
        }

        if (!IsPlayFinished()) return false;

        _playFinished = true;
        StopSchedulerInternal();

        if (warningSystem != null) warningSystem.NotifyPlayFinished();

        if (!_playFinishedLogged)
        {
            _playFinishedLogged = true;
            Debug.Log("[ParentWarningScheduler] 本プレイ終了を検知 — 自動発火を停止します" +
                      "（以降このプレイでは再開しません。予約・待機を破棄、autoTrigger は変更しません）");
        }

        return true;
    }

    private bool LatchPlayFinishedFromCoroutine()
    {
        if (_playFinished) return true;

        if (IsStartUnavailable())
        {
            ParentGameTimeManager.LogUnavailableRejectOnce();
            return true;
        }

        if (!IsPlayFinished()) return false;

        _playFinished = true;

        if (warningSystem != null) warningSystem.NotifyPlayFinished();

        if (!_playFinishedLogged)
        {
            _playFinishedLogged = true;
            Debug.Log("[ParentWarningScheduler] 本プレイ終了を検知 — 自動発火を停止します" +
                      "（以降このプレイでは再開しません。予約・待機を破棄、autoTrigger は変更しません）");
        }

        return true;
    }

    private void Start()
    {
        if (warningSystem == null)
            warningSystem = GetComponent<MotherApproachWarning>();

        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        if (_parentDetection == null && warningSystem != null)
            _parentDetection = warningSystem.parentDetection;
        if (_parentDetection == null)
            _parentDetection = Object.FindFirstObjectByType<MotherSuspicionSystem>();

        if (_approachController == null && warningSystem != null)
            _approachController = warningSystem.approachController;
        if (_approachController == null)
            _approachController = Object.FindFirstObjectByType<MotherApproachController>();

        _wasWarningActive = false;
        _lastEventWasStrong = false;
        _restRemaining = 0f;
        _playFinished = false;
        _playFinishedLogged = false;

        if (autoTrigger && !GameDifficulty.IncludeTutorial)
            StartScheduler();
    }

    private void Update()
    {
        TrackEventEnd();
        TryLatchPlayFinished();

        if (Keyboard.current == null) return;

        if (Keyboard.current.digit1Key.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningScheduler] 1キーを押下 - 母親ドア確認トリガー");
            TriggerDoorNow();
        }
    }

    private void StartScheduler()
    {
        StopSchedulerInternal();

        if (!autoTrigger)
            return;

        if (TryLatchPlayFinished()) return;

        _schedulerCoroutine = StartCoroutine(SchedulerCoroutine());

        if (_playFinished || IsStartUnavailable() || IsPlayFinished())
            _schedulerCoroutine = null;
    }

    private void StopScheduler()
    {
        StopSchedulerInternal();
    }

    private void StopSchedulerInternal()
    {
        if (_schedulerCoroutine != null)
        {
            StopCoroutine(_schedulerCoroutine);
            _schedulerCoroutine = null;
        }

        if (_triggerSoonCoroutine != null)
        {
            StopCoroutine(_triggerSoonCoroutine);
            _triggerSoonCoroutine = null;
        }

        timeUntilNextWarning = 0f;
    }

    private void TriggerDoorNow()
    {
        if (warningSystem == null)
        {
            Debug.LogWarning("[ParentWarningScheduler] TriggerDoorNow: warningSystem is NULL - cannot trigger");
            return;
        }

        if (warningSystem.isWarningActive)
        {
            Debug.Log("[ParentWarningScheduler] TriggerDoorNow: BLOCKED - warning sequence is already active");
            return;
        }

        Debug.Log("[ParentWarningScheduler] TriggerDoorNow: MANUAL DOOR TRIGGER");
        warningSystem.StartManualDoorWarningSequence();
    }

    public void TriggerNow()
    {
        if (warningSystem == null)
        {
            Debug.LogWarning("[ParentWarningScheduler] TriggerNow: warningSystem is NULL - cannot trigger");
            return;
        }

        if (warningSystem.isWarningActive)
        {
            Debug.Log("[ParentWarningScheduler] TriggerNow: BLOCKED - warning sequence is already active");
            return;
        }

        Debug.Log("[ParentWarningScheduler] TriggerNow: MANUAL TRIGGER");
        warningSystem.StartWarningSequence();
    }

    public void TriggerSoon(float delaySeconds = 1f)
    {
        if (showDebugLogs)
            Debug.Log($"[ParentWarningScheduler] TriggerSoon requested: delay={delaySeconds:F1}s");

        if (_schedulerCoroutine != null)
        {
            StopCoroutine(_schedulerCoroutine);
            _schedulerCoroutine = null;
        }

        if (_triggerSoonCoroutine != null)
        {
            StopCoroutine(_triggerSoonCoroutine);
        }

        _triggerSoonCoroutine = StartCoroutine(TriggerSoonCoroutine(delaySeconds));
    }

    private IEnumerator TriggerSoonCoroutine(float delay)
    {
        float t = Mathf.Max(0f, delay);

        while (t > 0f)
        {
            t -= Time.deltaTime;
            yield return null;
        }

        while (_blockedByChore)
            yield return null;

        if (warningSystem != null && !warningSystem.isWarningActive)
        {
            Debug.Log("[ParentWarningScheduler] TriggerSoon firing warning now");
            warningSystem.StartWarningSequence();
            yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
        }

        _triggerSoonCoroutine = null;
        StartScheduler();
    }

    private IEnumerator SchedulerCoroutine()
    {
        _gracePeriodOver = false;

        float grace = Mathf.Max(0f, graceSeconds * GameDifficulty.ParentWarningIntervalMultiplier);
        if (showDebugLogs)
            Debug.Log($"[ParentWarningScheduler] Grace period started: {grace:F1}s (diff={GameDifficulty.DisplayName})");

        timeUntilNextWarning = grace;
        while (timeUntilNextWarning > 0f)
        {
            if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

            if (!_blockedByChore)
                timeUntilNextWarning -= Time.deltaTime;
            yield return null;
        }

        _gracePeriodOver = true;
        timeUntilNextWarning = 0f;

        if (showDebugLogs)
            Debug.Log("[ParentWarningScheduler] Grace period over");

        while (true)
        {
            if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

            while (_blockedByChore)
            {
                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }
                yield return null;
            }

            if (warningSystem != null && warningSystem.isWarningActive)
            {
                if (!_waitingForActiveWarningLogged)
                {
                    _waitingForActiveWarningLogged = true;
                    Debug.LogWarning("[ParentWarningScheduler] 前の母親イベントの終了待ちに入りました" +
                                     "（isWarningActive=true）。この待機が長い場合は前のサイクルが終了通知を受け取れていません。");
                }

                yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
                _waitingForActiveWarningLogged = false;

                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

                yield return WaitForPostEventRest();
                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }
            }

            while (_blockedByChore)
            {
                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }
                yield return null;
            }

            yield return WaitForPostEventRest();
            if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

            if (_blockedByChore) continue;

            float waitMaxSeconds = ResolveExtraWaitMaxSeconds(out MotherGauge.SuspicionColorState waitStage);
            float fireOffset = Random.Range(0f, Mathf.Max(0f, waitMaxSeconds));

            if (showDebugLogs)
            {
                int gaugeAtDraw = (motherGauge != null) ? motherGauge.currentGauge : -1;
                Debug.Log($"[ParentWarningScheduler] 追加待機を抽選 | stage={DescribeStage(waitStage)}" +
                          $" | gauge={gaugeAtDraw} | max={waitMaxSeconds:F2}s | fireOffset={fireOffset:F2}s");
            }

            float elapsed = 0f;
            bool firedThisWindow = false;
            bool windowDiscarded = false;
            string discardReason = null;

            while (elapsed < waitMaxSeconds || !firedThisWindow)
            {
                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

                if (_blockedByChore)
                {
                    windowDiscarded = true;
                    discardReason = "片付け開始";
                    break;
                }

                if (warningSystem != null && warningSystem.isWarningActive)
                {
                    windowDiscarded = true;
                    discardReason = "通常／手動イベント開始";
                    break;
                }

                if (_restRemaining > 0f)
                {
                    timeUntilNextWarning = _restRemaining;
                    yield return null;
                    continue;
                }

                elapsed += Time.deltaTime;
                timeUntilNextWarning = Mathf.Max(0f, fireOffset - elapsed);

                if (!firedThisWindow && elapsed >= fireOffset)
                {
                    firedThisWindow = true;

                    if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

                    if (_blockedByChore || (warningSystem != null && warningSystem.isWarningActive))
                    {
                        windowDiscarded = true;
                        discardReason = "発火直前に他イベントが占有";
                        break;
                    }

                    yield return WaitForPostEventRest();
                    if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

                    if (_blockedByChore || _restRemaining > 0f ||
                        (warningSystem != null && warningSystem.isWarningActive))
                    {
                        windowDiscarded = true;
                        discardReason = "休憩の消化中に他イベントが占有";
                        break;
                    }

                    if (warningSystem != null)
                    {
                        warningSystem.StartWarningSequence();

                        if (warningSystem.isWarningActive)
                        {
                            Debug.Log($"[ParentWarningScheduler] Approach triggered by scheduler" +
                                      $" | stage={DescribeStage(waitStage)} | max={waitMaxSeconds:F2}s" +
                                      $" | fireOffset={fireOffset:F2}s | 実測待機={elapsed:F2}s");

                            yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
                            break;
                        }
                    }
                }

                yield return null;
            }

            if (windowDiscarded)
            {
                timeUntilNextWarning = 0f;
                if (showDebugLogs && discardReason != null)
                {
                    Debug.Log($"[ParentWarningScheduler] 追加待機を破棄: {discardReason}" +
                              "（古い elapsed / fireOffset は持ち越さず、割り込みイベントの完全終了後に再抽選します）");
                }

                continue;
            }

            timeUntilNextWarning = 0f;
        }
    }

    private float ResolveExtraWaitMaxSeconds(out MotherGauge.SuspicionColorState stage)
    {
        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        stage = (motherGauge != null)
            ? motherGauge.CurrentColorState
            : MotherGauge.SuspicionColorState.Blue;

        float baseWait;
        switch (stage)
        {
            case MotherGauge.SuspicionColorState.Purple:
                baseWait = Mathf.Max(0f, midSuspicionExtraWaitMaxSeconds);
                break;
            case MotherGauge.SuspicionColorState.Red:
                baseWait = Mathf.Max(0f, highSuspicionExtraWaitMaxSeconds);
                break;
            default:
                baseWait = Mathf.Max(0f, lowSuspicionExtraWaitMaxSeconds);
                break;
        }

        return baseWait * GameDifficulty.ParentWarningIntervalMultiplier;
    }

    private static string DescribeStage(MotherGauge.SuspicionColorState stage)
    {
        switch (stage)
        {
            case MotherGauge.SuspicionColorState.Purple: return "中(Purple)";
            case MotherGauge.SuspicionColorState.Red: return "高(Red)";
            default: return "低(Blue)";
        }
    }

    private void OnDestroy()
    {
        StopScheduler();
    }
}
