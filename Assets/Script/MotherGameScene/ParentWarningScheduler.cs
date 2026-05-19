using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem; // 新Input Systemに対応

/// <summary>
/// ParentWarningSystemを自動的にランダムな間隔で発動させます。
/// キーボードの『Oキー』でいつでもお母さんを強制召喚できるデバッグ機能付き。
/// </summary>
public class ParentWarningScheduler : MonoBehaviour
{
    [Header("システム参照")]
    [Tooltip("制御する予兆システム")]
    public ParentWarningSystem warningSystem;

    [Header("スケジュール設定")]
    [Tooltip("予兆を自動的に発動させるかどうか")]
    public bool autoTrigger = true;

    public float initialDelayMin = 5.0f;
    public float initialDelayMax = 10.0f;
    public float intervalMin = 20.0f;
    public float intervalMax = 40.0f;

    [Header("デバッグ確認")]
    [Tooltip("次の予兆までの残り時間")]
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
        // ★【無敵のデバッグ機能】キーボードの『O（オー）』キーを押すと、
        // タイマーを無視して今すぐその場でお母さんを強制的に出現させられます！
        if (Keyboard.current != null && Keyboard.current.oKey.wasPressedThisFrame)
        {
            TriggerNow();
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

    public void TriggerNow()
    {
        if (warningSystem != null && !warningSystem.isWarningActive)
        {
            Debug.Log("【デバッグ】お母さん襲撃シーケンスを手動で即座に開始します！");
            warningSystem.StartWarningSequence();
        }
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
                Debug.Log("【タイマー】お母さんが出現しました！");
                warningSystem.StartWarningSequence();

                // 予兆シーケンスが完了して廊下に帰るまで待つ
                yield return new WaitWhile(() => warningSystem.isWarningActive);
            }

            float nextInterval = Random.Range(intervalMin, intervalMax);
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