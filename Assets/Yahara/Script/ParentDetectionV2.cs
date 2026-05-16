using UnityEngine;

/// <summary>
/// 親が部屋に入ってきた時にプレイヤーが寝ているかどうかを検出します。
/// 新しい予兆システム（ParentWarningSystem）と連携して動作します。
/// </summary>
public class ParentDetectionV2 : MonoBehaviour
{
    [Header("システム参照")]
    [Tooltip("予兆システム")]
    public ParentWarningSystem warningSystem;
    
    [Tooltip("プレイヤーの睡眠状態を管理するコンポーネント")]
    public SleepingManager sleepingManager;

    [Header("検出設定")]
    [Tooltip("検出のチェック間隔（秒）")]
    public float checkInterval = 0.1f;

    [Header("ネットワーク連携")]
    [Tooltip("UDP送信コンポーネント（子側の場合）")]
    public ChildUdpReceiver udpReceiver;
    
    [Tooltip("UDP送信を使用するかどうか")]
    public bool useNetworkSync = false;

    [Header("デバッグ")]
    [Tooltip("プレイヤーが捕まったかどうか")]
    public bool isCaught = false;

    private float detectionTimer = 0f;

    void Update()
    {
        // すでに捕まっている場合は何もしない
        if (isCaught)
            return;

        // 予兆システムが実行中でない場合は検出しない
        if (warningSystem == null || !warningSystem.isWarningActive)
            return;

        // タイマーを更新
        detectionTimer += Time.deltaTime;

        // 一定間隔で検出チェック
        if (detectionTimer >= checkInterval)
        {
            detectionTimer = 0f;
            CheckDetection();
        }
    }

    void CheckDetection()
    {
        // SleepingManagerが設定されているか確認
        if (sleepingManager == null)
        {
            Debug.LogWarning("SleepingManager is not assigned!");
            return;
        }

        // 親が部屋に入ってきて、プレイヤーが寝ていない場合は捕まる
        bool parentIsPresent = warningSystem != null && warningSystem.isWarningActive;
        bool playerIsSleeping = sleepingManager.IsSleeping;

        if (parentIsPresent && !playerIsSleeping)
        {
            OnPlayerCaught();
        }
    }

    void OnPlayerCaught()
    {
        isCaught = true;
        Debug.Log("Player caught by parent!");

        // SleepingManagerに通知
        if (sleepingManager != null)
        {
            sleepingManager.SetCaughtState();
        }

        // ネットワーク同期が有効な場合、UDP経由で通知
        if (useNetworkSync && udpReceiver != null)
        {
            udpReceiver.SendState("CHILD_CAUGHT");
        }

        // 予兆システムを停止
        if (warningSystem != null)
        {
            warningSystem.StopWarningSequence();
        }
    }

    /// <summary>
    /// 捕まった状態をリセット（デバッグ用）
    /// </summary>
    public void ResetCaughtState()
    {
        isCaught = false;
        Debug.Log("Caught state reset");
    }
}

/*
=== Inspector Setup ===

1) このスクリプトをシーン内のGameObjectにアタッチします（例：GameManager）

2) システム参照:
   - "Warning System": ParentWarningSystemコンポーネントをドラッグ
   - "Sleeping Manager": SleepingManagerコンポーネントをドラッグ

3) 検出設定:
   - "Check Interval": 検出チェックの間隔（デフォルト: 0.1秒）

4) ネットワーク連携（オプション）:
   - "Udp Receiver": ChildUdpReceiverコンポーネント（ネットワーク対戦の場合のみ）
   - "Use Network Sync": ネットワーク同期を使用する場合はON

=== 動作の流れ ===

1. ParentWarningSystemが予兆シーケンスを開始
2. 一階の明かり → 二階の明かり → ドアノック音 → 親が出現
3. 親が部屋にいる間、このスクリプトがプレイヤーの状態をチェック
4. プレイヤーが寝ていない場合 → 捕まる（SleepingManager.SetCaughtState()が呼ばれる）
5. プレイヤーが寝ている場合 → 見逃される

=== 旧ParentDetectionとの違い ===

旧版: DoorControllerを使って親がドアにいるかを判定
新版: ParentWarningSystemの予兆シーケンス実行中かどうかで判定

これにより、予兆システムの流れと検出システムが完全に連携します。
*/