using System.Collections;
using UnityEngine;

/// <summary>
/// ParentWarningSystemを自動的にランダムな間隔で発動させます
/// </summary>
public class ParentWarningScheduler : MonoBehaviour
{
    [Header("システム参照")]
    [Tooltip("制御する予兆システム")]
    public ParentWarningSystem warningSystem;

    [Header("スケジュール設定")]
    [Tooltip("予兆を自動的に発動させるかどうか")]
    public bool autoTrigger = true;
    
    [Tooltip("最初の予兆が発動するまでの最小時間（秒）")]
    public float initialDelayMin = 5.0f;
    
    [Tooltip("最初の予兆が発動するまでの最大時間（秒）")]
    public float initialDelayMax = 10.0f;
    
    [Tooltip("予兆と予兆の間の最小時間（秒）")]
    public float intervalMin = 20.0f;
    
    [Tooltip("予兆と予兆の間の最大時間（秒）")]
    public float intervalMax = 40.0f;

    [Header("デバッグ")]
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

    /// <summary>
    /// スケジューラーを開始します
    /// </summary>
    public void StartScheduler()
    {
        if (schedulerCoroutine != null)
        {
            StopCoroutine(schedulerCoroutine);
        }

        schedulerCoroutine = StartCoroutine(SchedulerCoroutine());
    }

    /// <summary>
    /// スケジューラーを停止します
    /// </summary>
    public void StopScheduler()
    {
        if (schedulerCoroutine != null)
        {
            StopCoroutine(schedulerCoroutine);
            schedulerCoroutine = null;
        }
    }

    /// <summary>
    /// 今すぐ予兆を発動させます（手動トリガー）
    /// </summary>
    public void TriggerNow()
    {
        if (warningSystem != null && !warningSystem.isWarningActive)
        {
            warningSystem.StartWarningSequence();
        }
    }

    private IEnumerator SchedulerCoroutine()
    {
        // 最初の待機時間
        float initialDelay = Random.Range(initialDelayMin, initialDelayMax);
        timeUntilNextWarning = initialDelay;
        
        Debug.Log($"First warning will trigger in {initialDelay:F1} seconds");
        
        while (timeUntilNextWarning > 0)
        {
            timeUntilNextWarning -= Time.deltaTime;
            yield return null;
        }

        // メインループ
        while (true)
        {
            // 予兆システムが実行中でない場合のみ発動
            if (warningSystem != null && !warningSystem.isWarningActive)
            {
                Debug.Log("Triggering warning sequence...");
                warningSystem.StartWarningSequence();

                // 予兆シーケンスが完了するまで待つ
                yield return new WaitWhile(() => warningSystem.isWarningActive);
            }

            // 次の予兆までの待機時間を計算
            float nextInterval = Random.Range(intervalMin, intervalMax);
            timeUntilNextWarning = nextInterval;
            
            Debug.Log($"Next warning will trigger in {nextInterval:F1} seconds");

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

/*
=== Inspector Setup ===

1) このスクリプトをParentWarningSystemと同じGameObjectにアタッチします

2) システム参照:
   - "Warning System": ParentWarningSystemコンポーネント（自動検出されますが、手動で設定も可能）

3) スケジュール設定:
   - "Auto Trigger": 自動的に予兆を発動させるかどうか（デフォルト: ON）
   - "Initial Delay Min": ゲーム開始から最初の予兆までの最小時間（デフォルト: 5秒）
   - "Initial Delay Max": ゲーム開始から最初の予兆までの最大時間（デフォルト: 10秒）
   - "Interval Min": 予兆と予兆の間の最小時間（デフォルト: 20秒）
   - "Interval Max": 予兆と予兆の間の最大時間（デフォルト: 40秒）

=== 使用方法 ===

自動モード:
- "Auto Trigger"をONにしておくだけで、自動的にランダムな間隔で予兆が発動します

手動トリガー:
```csharp
ParentWarningScheduler scheduler = GetComponent<ParentWarningScheduler>();
scheduler.TriggerNow(); // 今すぐ予兆を発動
```

スケジューラーの制御:
```csharp
ParentWarningScheduler scheduler = GetComponent<ParentWarningScheduler>();
scheduler.StopScheduler();  // 自動発動を停止
scheduler.StartScheduler(); // 自動発動を再開
```
*/