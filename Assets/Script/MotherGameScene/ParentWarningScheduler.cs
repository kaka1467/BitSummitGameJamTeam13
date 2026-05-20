using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentWarningScheduler:
/// Automatically triggers ParentWarningSystem in repeating time windows.
/// - First, a grace period blocks all automatic approaches.
/// - After grace, each window triggers exactly one automatic approach
///   at a random time within that window.
/// - Higher suspicion shortens the effective window by windowReductionPerGauge seconds per stage.
///   e.g. baseWindow=20s, windowReductionPerGauge=1s, gauge=9 → 11s window.
/// - Loud items can still force an early check via TriggerSoon().
/// </summary>
public class ParentWarningScheduler : MonoBehaviour
{
    [Header("System References")]
    [Tooltip("The ParentWarningSystem to control")]
    public ParentWarningSystem warningSystem;
    public MotherGauge motherGauge;

    [Header("Scheduler Settings")]
    [Tooltip("Automatically trigger warnings")]
    public bool autoTrigger = true;

    [Tooltip("No automatic parent approach happens during this many seconds after scene start.")]
    public float graceSeconds = 15f;

    [Tooltip("Minimum base window length in seconds before per-gauge reduction is applied.")]
    public float baseWindowMinSeconds = 20f;

    [Tooltip("Maximum base window length in seconds before per-gauge reduction is applied. Set equal to baseWindowMinSeconds for a fixed base window.")]
    public float baseWindowMaxSeconds = 20f;

    [Header("Scaling by Suspicion")]
    [Tooltip("Seconds subtracted from the base window per gauge stage. e.g. baseWindow=20, value=1, gauge=9 → 11s window.")]
    public float windowReductionPerGauge = 1f;
    [Tooltip("Minimum window size in seconds regardless of suspicion level. Prevents windows from collapsing to zero.")]
    public float minimumWindowSize = 5f;

    [Header("Debug")]
    [Tooltip("Time remaining until next automatic warning inside the current active window.")]
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
            Debug.Log("[ParentWarningScheduler] N key pressed - manual PASS-BY trigger");
            TriggerPassByNow();
        }

        if (Keyboard.current.mKey.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningScheduler] M key pressed - manual DOOR trigger");
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
    /// Manual debug trigger (N key) — forces pass-by route.
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
    /// Manual debug trigger (M key) — forces door route.
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
    /// Trigger a warning after a short delay (used for loud items).
    /// Resets the scheduler loop so urgent checks can happen early.
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