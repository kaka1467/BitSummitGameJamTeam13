using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem; // 新Input Systemに対応

/// <summary>
/// ParentWarningScheduler: automatically triggers ParentWarningSystem at intervals.
/// Higher suspicion shortens the interval; loud items can force an imminent check.
/// </summary>
public class ParentWarningScheduler : MonoBehaviour
{
    [Header("System References")]
    [Tooltip("The ParentWarningSystem to control")]
    public ParentWarningSystem warningSystem;

    [Header("Scheduler Settings")]
    [Tooltip("Automatically trigger warnings")]
    public bool autoTrigger = true;

    public float initialDelayMin = 5.0f;
    public float initialDelayMax = 10.0f;
    public float intervalMin = 20.0f;
    public float intervalMax = 40.0f;

    [Header("Scaling by Suspicion")]
    [Tooltip("Minimum scale applied to intervals at max suspicion (0..1). Lower makes checks more frequent.)")]
    [Range(0.1f, 1f)]
    public float minIntervalScale = 0.4f;

    [Header("Debug")]
    [Tooltip("Time remaining until next warning (for debugging)")]
    public float timeUntilNextWarning = 0f;

    private Coroutine schedulerCoroutine;

    void Start()
    {
        if (warningSystem == null)
        {
            warningSystem = GetComponent<ParentWarningSystem>();
        }

        if (autoTrigger)
        {
            StartScheduler();
        }
    }

    void Update()
    {
        if (Keyboard.current != null)
        {
            if (Keyboard.current.nKey.wasPressedThisFrame)
            {
                Debug.Log("[ParentWarningScheduler] N key pressed - manual PASS-BY trigger");
                TriggerPassByNow();
            }

            if (Keyboard.current.mKey.wasPressedThisFrame)
            {
                Debug.Log("[ParentWarningScheduler] M key pressed - manual DOOR-OPEN trigger");
                TriggerDoorNow();
            }
        }
    }

    public void StartScheduler()
    {
        if (schedulerCoroutine != null)
        {
            StopCoroutine(schedulerCoroutine);
        }
        schedulerCoroutine = StartCoroutine(SchedulerCoroutine());
    }

    public void StopScheduler()
    {
        if (schedulerCoroutine != null)
        {
            StopCoroutine(schedulerCoroutine);
            schedulerCoroutine = null;
        }
    }

    /// <summary>
    /// Manual debug trigger (N key) — forces pass-by route.
    /// Mother walks the full path but never stops at the door.
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

        Debug.Log("[ParentWarningScheduler] TriggerPassByNow: MANUAL PASS-BY TRIGGER - starting pass-by warning sequence");
        warningSystem.StartManualPassByWarningSequence();
    }

    /// <summary>
    /// Manual debug trigger (M key) — forces door-open/peek route.
    /// Mother walks the full path and always stops at the door.
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

        Debug.Log("[ParentWarningScheduler] TriggerDoorNow: MANUAL DOOR TRIGGER - starting door-open warning sequence");
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

        Debug.Log("[ParentWarningScheduler] TriggerNow: MANUAL TRIGGER - starting warning sequence");
        warningSystem.StartWarningSequence();
    }

    /// <summary>
    /// Trigger a warning after a short delay (used for loud items)
    /// </summary>
    public void TriggerSoon(float delaySeconds = 1f)
    {
        if (schedulerCoroutine != null)
        {
            StopCoroutine(schedulerCoroutine);
            schedulerCoroutine = null;
        }
        StartCoroutine(TriggerSoonCoroutine(delaySeconds));
    }

    private IEnumerator TriggerSoonCoroutine(float delay)
    {
        float t = delay;
        while (t > 0f)
        {
            t -= Time.deltaTime;
            yield return null;
        }

        if (warningSystem != null && !warningSystem.isWarningActive)
        {
            warningSystem.StartWarningSequence();
        }

        // Resume normal scheduling
        StartScheduler();
    }

    private IEnumerator SchedulerCoroutine()
    {
        float initialDelay = Random.Range(initialDelayMin, initialDelayMax);
        timeUntilNextWarning = initialDelay;

        while (timeUntilNextWarning > 0)
        {
            timeUntilNextWarning -= Time.deltaTime;
            yield return null;
        }

        while (true)
        {
            if (warningSystem != null && !warningSystem.isWarningActive)
            {
                Debug.Log("[ParentWarningScheduler] Warning triggered by scheduler");
                warningSystem.StartWarningSequence();

                // Wait while the warning sequence is active
                yield return new WaitWhile(() => warningSystem.isWarningActive);
            }

            // Determine next interval scaled by current suspicion level (if available)
            float nextInterval = Random.Range(intervalMin, intervalMax);

            // Try to get suspicion fraction (0..1)
            float suspicionFraction = 0f;
            var gauge = Object.FindFirstObjectByType<MotherGauge>();
            if (gauge != null && gauge.maxGauge > 0)
            {
                suspicionFraction = (float)gauge.currentGauge / gauge.maxGauge;
            }

            float scale = Mathf.Lerp(1f, minIntervalScale, suspicionFraction);
            nextInterval *= scale;

            timeUntilNextWarning = nextInterval;

            while (timeUntilNextWarning > 0)
            {
                timeUntilNextWarning -= Time.deltaTime;
                yield return null;
            }
        }
    }

    void OnDestroy()
    {
        StopScheduler();
    }
}