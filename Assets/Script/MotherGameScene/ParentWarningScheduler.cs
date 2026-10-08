using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentWarningScheduler:
/// MotherApproachWarningを一定の時間ウィンドウで自動的に発生させる。
/// - 最初は猶予期間として自動接近をすべて阻止する。
/// - 猶予期間後は各ウィンドウで1回だけ、ウィンドウ内のランダムな時刻に自動接近を発生させる。
/// - 疑惑が高いほど、1段階あたりwindowReductionPerGauge秒だけ実効ウィンドウを短縮する。
///   例：baseWindow=20秒、windowReductionPerGauge=1秒、gauge=9なら11秒のウィンドウ。
/// - 大きな音のアイテムはTriggerSoon()で早期チェックを強制できる。
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
    public float timeUntilNextWarning;

    [SerializeField] private bool showDebugLogs = true;

    private Coroutine _schedulerCoroutine;
    private Coroutine _triggerSoonCoroutine;
    private bool _gracePeriodOver;
    // 片付け演出（MotherChoreController）中は true。自動警告の発火を待機させる。
    private bool _blockedByChore;

    public bool IsGracePeriodOver => _gracePeriodOver;

    /// <summary>片付け演出中で自動警告を抑止しているか（Inspector 表示用）。</summary>
    public bool IsBlockedExternally => _blockedByChore;

    /// <summary>
    /// 片付け演出による抑止を設定する。true の間は新しい自動警告を発生させない。
    /// 解除時は、待機中だった次の警告ウィンドウを最初からやり直させる。
    /// </summary>
    public void SetBlockedByChore(bool blocked)
    {
        if (_blockedByChore == blocked) return;
        _blockedByChore = blocked;
        Debug.Log($"[ParentWarningScheduler] 片付け演出による自動警告抑止: {blocked}");

        if (!blocked && autoTrigger && _schedulerCoroutine == null && !isWarningActiveSatisfied())
        {
            // 片付け終了後に自動警告を再開する（停止状態を残さない）。
            StartScheduler();
        }
    }

    /// <summary>現在 warningSystem が有効な警告シーケンス中か。</summary>
    private bool isWarningActiveSatisfied()
    {
        return warningSystem != null && warningSystem.isWarningActive;
    }

    private void Start()
    {
        if (warningSystem == null)
            warningSystem = GetComponent<MotherApproachWarning>();

        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        if (autoTrigger)
            StartScheduler();
    }

    private void Update()
    {
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

        _schedulerCoroutine = StartCoroutine(SchedulerCoroutine());
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

    /// <summary>
    /// 手動デバッグトリガー（1キー）— ドアルートを強制する。
    /// </summary>
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

    /// <summary>
    /// 短い遅延後に警告を発生させる（大きな音のアイテムで使用）。
    /// 緊急チェックを早期に行えるようスケジューラーループをリセットする。
    /// </summary>
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

        // 片付け演出中は発火しない（大きな音による突入も片付けと重複させない）。
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

        float grace = Mathf.Max(0f, graceSeconds);
        if (showDebugLogs)
            Debug.Log($"[ParentWarningScheduler] Grace period started: {grace:F1}s");

        timeUntilNextWarning = grace;
        while (timeUntilNextWarning > 0f)
        {
            // 片付け演出中は時間を進めて待機する（新しい警告は発生させない）。
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
            // 片付け演出中は自動警告を発生させない（通常の親イベントと重複させない）。
            while (_blockedByChore)
                yield return null;

            if (warningSystem != null && warningSystem.isWarningActive)
            {
                yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
            }

            // 片付け演出が開始された場合も発火を待つ。
            while (_blockedByChore)
                yield return null;

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

                // 片付け演出中はウィンドウを進めない（発火を待機する）。
                if (_blockedByChore)
                {
                    yield return null;
                    continue;
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

    private void OnDestroy()
    {
        StopScheduler();
    }
}