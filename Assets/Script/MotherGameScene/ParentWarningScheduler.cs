using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentWarningScheduler:
/// ParentWarningSystemを一定の時間ウィンドウで自動的に発生させる。
/// - 最初は猶予期間として自動接近をすべて阻止する。
/// - 猶予期間後は各ウィンドウで1回だけ、ウィンドウ内のランダムな時刻に自動接近を発生させる。
/// - 疑惑が高いほど、1段階あたりwindowReductionPerGauge秒だけ実効ウィンドウを短縮する。
///   例：baseWindow=20秒、windowReductionPerGauge=1秒、gauge=9なら11秒のウィンドウ。
/// - 大きな音のアイテムはTriggerSoon()で早期チェックを強制できる。
/// </summary>
public class ParentWarningScheduler : MonoBehaviour
{
    [Header("システム参照")]
    [Tooltip("制御対象のParentWarningSystem")]
    public ParentWarningSystem warningSystem;
    public MotherGauge motherGauge;

    [Header("スケジューラー設定")]
    [Tooltip("警告を自動的に発生させる")]
    public bool autoTrigger = true;

    [Tooltip("シーン開始後、この秒数の間は親機が自動接近しません。")]
    public float graceSeconds = 15f;

    [Tooltip("ゲージによる減少を適用する前の基本ウィンドウ最小時間（秒）。")]
    public float baseWindowMinSeconds = 20f;

    [Tooltip("ゲージによる減少を適用する前の基本ウィンドウ最大時間（秒）。固定する場合はbaseWindowMinSecondsと同じ値にします。")]
    public float baseWindowMaxSeconds = 20f;

    [Header("疑惑によるスケーリング")]
    [Tooltip("ゲージ1段階ごとに基本ウィンドウから減らす秒数。例：baseWindow=20、値=1、gauge=9なら11秒。")]
    public float windowReductionPerGauge = 1f;
    [Tooltip("疑惑レベルに関係なく保証するウィンドウの最小時間（秒）。0になるのを防ぐ。")]
    public float minimumWindowSize = 5f;

    [Header("デバッグ")]
    [Tooltip("現在の有効ウィンドウ内で、次の自動警告までの残り時間。")]
    public float timeUntilNextWarning = 0f;

    [SerializeField] private bool showDebugLogs = true;

    private Coroutine schedulerCoroutine;
    private Coroutine triggerSoonCoroutine;
    private bool _gracePeriodOver = false;

    public bool IsGracePeriodOver => _gracePeriodOver;

    void Start()
    {
        if (warningSystem == null)
            warningSystem = GetComponent<ParentWarningSystem>();

        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        if (autoTrigger)
            StartScheduler();
    }

    void Update()
    {
        if (Keyboard.current == null) return;

        if (Keyboard.current.nKey.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningScheduler] Nキーを押下 - 手動通過トリガー");
            TriggerPassByNow();
        }

        if (Keyboard.current.mKey.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningScheduler] Mキーを押下 - 手動ドアトリガー");
            TriggerDoorNow();
        }
    }

    public void StartScheduler()
    {
        StopSchedulerInternal();

        if (!autoTrigger)
            return;

        schedulerCoroutine = StartCoroutine(SchedulerCoroutine());
    }

    public void StopScheduler()
    {
        StopSchedulerInternal();
    }

    private void StopSchedulerInternal()
    {
        if (schedulerCoroutine != null)
        {
            StopCoroutine(schedulerCoroutine);
            schedulerCoroutine = null;
        }

        if (triggerSoonCoroutine != null)
        {
            StopCoroutine(triggerSoonCoroutine);
            triggerSoonCoroutine = null;
        }

        timeUntilNextWarning = 0f;
    }

    /// <summary>
    /// 手動デバッグトリガー（Nキー）— 通過ルートを強制する。
    /// </summary>
    public void TriggerPassByNow()
    {
        if (warningSystem == null)
        {
            Debug.LogWarning("[ParentWarningScheduler] TriggerPassByNow: warningSystem is NULL - cannot trigger");
            return;
        }

        if (warningSystem.isWarningActive)
        {
            Debug.Log("[ParentWarningScheduler] TriggerPassByNow: BLOCKED - warning sequence is already active");
            return;
        }

        Debug.Log("[ParentWarningScheduler] TriggerPassByNow: MANUAL PASS-BY TRIGGER");
        warningSystem.StartManualPassByWarningSequence();
    }

    /// <summary>
    /// 手動デバッグトリガー（Mキー）— ドアルートを強制する。
    /// </summary>
    public void TriggerDoorNow()
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

    /// <summary>
    /// 短い遅延後に警告を発生させる（大きな音のアイテムで使用）。
    /// 緊急チェックを早期に行えるようスケジューラーループをリセットする。
    /// </summary>
    public void TriggerSoon(float delaySeconds = 1f)
    {
        if (showDebugLogs)
            Debug.Log($"[ParentWarningScheduler] TriggerSoon requested: delay={delaySeconds:F1}s");

        if (schedulerCoroutine != null)
        {
            StopCoroutine(schedulerCoroutine);
            schedulerCoroutine = null;
        }

        if (triggerSoonCoroutine != null)
        {
            StopCoroutine(triggerSoonCoroutine);
        }

        triggerSoonCoroutine = StartCoroutine(TriggerSoonCoroutine(delaySeconds));
    }

    private IEnumerator TriggerSoonCoroutine(float delay)
    {
        float t = Mathf.Max(0f, delay);

        while (t > 0f)
        {
            t -= Time.deltaTime;
            yield return null;
        }

        if (warningSystem != null && !warningSystem.isWarningActive)
        {
            Debug.Log("[ParentWarningScheduler] TriggerSoon firing warning now");
            warningSystem.StartWarningSequence();
            yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
        }

        triggerSoonCoroutine = null;
        StartScheduler();
    }

    private IEnumerator SchedulerCoroutine()
    {
        _gracePeriodOver = false;

        float grace = Mathf.Max(0f, graceSeconds);
        if (showDebugLogs)
            Debug.Log($"[ParentWarningScheduler] Grace period started: {grace:F1}s");

        timeUntilNextWarning = grace;
        while (timeUntilNextWarning > 0f)
        {
            timeUntilNextWarning -= Time.deltaTime;
            yield return null;
        }

        _gracePeriodOver = true;
        timeUntilNextWarning = 0f;

        if (showDebugLogs)
            Debug.Log("[ParentWarningScheduler] Grace period over");

        while (true)
        {
            if (warningSystem != null && warningSystem.isWarningActive)
            {
                yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
            }

            int currentGauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
            float baseWindow = Random.Range(baseWindowMinSeconds, baseWindowMaxSeconds);
            float effectiveWindow = Mathf.Max(minimumWindowSize, baseWindow - currentGauge * windowReductionPerGauge);
            float fireOffset = Random.Range(0f, effectiveWindow);

            if (showDebugLogs)
            {
                Debug.Log(
                    $"[ParentWarningScheduler] New window | base={baseWindow:F2}s | gauge={currentGauge} | reduction={currentGauge * windowReductionPerGauge:F2}s | effective={effectiveWindow:F2}s | fireOffset={fireOffset:F2}s"
                );
            }

            float elapsed = 0f;
            bool firedThisWindow = false;

            while (elapsed < effectiveWindow)
            {
                if (warningSystem != null && warningSystem.isWarningActive)
                {
                    yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
                }

                elapsed += Time.deltaTime;
                timeUntilNextWarning = Mathf.Max(0f, fireOffset - elapsed);

                if (!firedThisWindow && elapsed >= fireOffset)
                {
                    firedThisWindow = true;

                    if (warningSystem != null && !warningSystem.isWarningActive)
                    {
                        Debug.Log("[ParentWarningScheduler] Approach triggered by scheduler");
                        warningSystem.StartWarningSequence();
                        yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
                    }
                }

                yield return null;
            }

            timeUntilNextWarning = 0f;
        }
    }

    void OnDestroy()
    {
        StopScheduler();
    }
}