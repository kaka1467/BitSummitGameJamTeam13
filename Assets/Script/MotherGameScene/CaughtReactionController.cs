using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

/// <summary>
/// CaughtReactionController：
///
/// ゲージ管理 — 厳密な単一書き込みモデル：
///   MotherGaugeに書き込む唯一のスクリプトはParentDetectionV2
///   （毎フレームのSetGaugeDirectによる増減、大きな音によるAddGauge）。
///   このスクリプトはMotherGaugeに一切書き込まない。
///
/// このスクリプトの責務：
///   - ゲームオーバー監視：ゲージを監視し、最大値到達時にシーン／UDP遷移を実行
///   - ParentDetectionV2へNotifyGameOverを転送し、進行を停止
///   - ゲームオーバー時にゲームロジックのコンポーネントを無効化
/// </summary>
public class CaughtReactionController : MonoBehaviour
{
    [Header("システム参照")]
    [SerializeField] private ParentDetectionV2 parentDetection;
    [SerializeField] private SleepingController sleepingController;
    [SerializeField] private DoorController doorController;
    [SerializeField] private ParentUdpSender udpSender;
    [SerializeField] private MotherGauge motherGauge;
    [SerializeField] private CanvasGroup fadeCanvasGroup;

    [Header("シーン設定")]
    [SerializeField] private string gameOverSceneName = "GameOverResult";

    [Header("フェード設定")]
    [SerializeField, Min(0f)] private float fadeSeconds = 0.5f;
    [SerializeField, Min(0f)] private float sceneChangeDelay = 0f;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs = false;

    // ゲームオーバー状態フラグ
    private bool hasTriggeredGameOver = false;
    private Coroutine gameOverRoutine = null;

    void Start()
    {
        if (parentDetection == null)
            parentDetection = Object.FindFirstObjectByType<ParentDetectionV2>();
        if (sleepingController == null)
            sleepingController = Object.FindFirstObjectByType<SleepingController>();
        if (doorController == null)
            doorController = Object.FindFirstObjectByType<DoorController>();
        EnsureUdpSender();
        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        hasTriggeredGameOver = false;

        if (fadeCanvasGroup != null)
        {
            fadeCanvasGroup.alpha = 0f;
            fadeCanvasGroup.interactable = false;
            fadeCanvasGroup.blocksRaycasts = false;
            fadeCanvasGroup.gameObject.SetActive(false);
        }

        if (showDebugLogs)
            Debug.Log("[CaughtReactionController] initialized - game-over watchdog only (gauge owned by PDV2)");
    }

    private void EnsureUdpSender()
    {
        if (udpSender == null)
            udpSender = Object.FindFirstObjectByType<ParentUdpSender>();
        if (udpSender == null)
            udpSender = ParentUdpSender.instance;
    }

    void Update()
    {
        if (hasTriggeredGameOver) return;
        if (motherGauge == null) return;

        // ゲームオーバー監視：PDV2は毎フレームゲージを書き込み、最大値到達時にOnPlayerCaughtを呼ぶ。
        // PDV2が自身のゲームオーバー処理を実行する前に無効化された場合に備えた安全網。
        if (motherGauge.currentGauge >= motherGauge.maxGauge)
        {
            TriggerGameOver();
            return;
        }

        if (showDebugLogs)
        {
            bool isLooking  = (parentDetection != null) && parentDetection.isMotherLookingNow;
            bool isSleeping = (sleepingController != null) && sleepingController.IsSleeping;
            Debug.Log($"[CaughtReactionController-Update] isMotherLookingNow={isLooking} | IsSleeping={isSleeping} | gauge={motherGauge.currentGauge}/{motherGauge.maxGauge} | (gauge written exclusively by PDV2)");
        }
    }

    /// <summary>
    /// 親機がチェックを行ったことを通知する。
    /// このスクリプトはゲージを変更しない（すべての書き込みはPDV2が管理）。
    /// 既存のUnityEvent接続を壊さないためスタブとして残す。
    /// </summary>
    public void OnMotherCheck(bool isFullCheck)
    {
        if (showDebugLogs)
            Debug.Log($"[CaughtReactionController] OnMotherCheck ({(isFullCheck ? "FULL" : "PEEK")})を受信 - ゲージ書き込みはPDV2の責務のため、ここでは何もしません");
    }

    /// <summary>
    /// 大きな音を出すアイテムが発生したことを通知する。
    /// このスクリプトはゲージを変更しない（書き込みはPDV2.OnLoudItemTriggeredが管理）。
    /// 既存の接続を壊さないためスタブとして残す。
    /// </summary>
    public void OnLoudItemTriggered()
    {
        if (showDebugLogs)
            Debug.Log("[CaughtReactionController] OnLoudItemTriggeredを受信 - ゲージ書き込みはPDV2の責務のため、ここでは何もしません");
    }

    /// <summary>
    /// 永続的なゲームオーバーシーケンスを開始する
    /// </summary>
    private void TriggerGameOver()
    {
        if (hasTriggeredGameOver) return;

        hasTriggeredGameOver = true;

        // ParentDetectionに永続的なゲームオーバーを通知し、進行を停止させる
        if (parentDetection != null)
        {
            try { parentDetection.NotifyGameOver(); } catch { }
        }

        if (showDebugLogs)
            Debug.LogWarning("[CaughtReactionController] ゲームオーバー発生 - 疑惑が最大値に到達");

        // ★★★ 【追加】子機へ親に捕まったこと（CAUGHT）を通知する ★★★
        EnsureUdpSender();
        if (udpSender != null)
        {
            udpSender.SendState("CAUGHT");
            StartCoroutine(SendCaughtRetry());
            if (showDebugLogs) Debug.Log("[CaughtReactionController] Sent CAUGHT message to child via UDP.");
        }
        else if (showDebugLogs)
        {
            Debug.LogWarning("[CaughtReactionController] ParentUdpSender not found — CAUGHT not sent.");
        }

        // ゲームロジックのコンポーネントを無効化
        DisableGameLogic();

        // ゲームオーバー処理（フェードとシーンロード）を開始
        if (gameOverRoutine != null) StopCoroutine(gameOverRoutine);
        gameOverRoutine = StartCoroutine(GameOverSequence());
    }

    private IEnumerator SendCaughtRetry()
    {
        yield return new WaitForSecondsRealtime(0.1f);
        EnsureUdpSender();
        if (udpSender != null)
            udpSender.SendState("CAUGHT");
    }

    private void DisableGameLogic()
    {
        if (doorController != null) { doorController.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Door Controller disabled"); }
        if (sleepingController != null) { sleepingController.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Sleeping Controller disabled"); }
        if (parentDetection != null) { parentDetection.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Parent Detection disabled"); }
    }

    private IEnumerator GameOverSequence()
    {
        // 1. 必要に応じて画面をフェードアウト
        if (fadeCanvasGroup != null)
        {
            if (!fadeCanvasGroup.gameObject.activeSelf)
            {
                fadeCanvasGroup.gameObject.SetActive(true);
            }

            fadeCanvasGroup.interactable = false;
            fadeCanvasGroup.blocksRaycasts = true;
            yield return StartCoroutine(FadeOutRoutine());
        }

        // 2. 設定された遅延時間を待機（タイムスケールに依存しないRealtime）
        float delay = Mathf.Max(0f, sceneChangeDelay);
        if (delay > 0f)
        {
            yield return new WaitForSecondsRealtime(delay);
        }

        // 3. 親機側のゲームオーバーシーン（GameOverResult）をロード
        if (showDebugLogs) Debug.Log($"[CaughtReactionController] {gameOverSceneName}シーンをロード中...");

        if (!string.IsNullOrEmpty(gameOverSceneName))
        {
            SceneManager.LoadScene(gameOverSceneName);
        }
        else
        {
            Debug.LogError("[CaughtReactionController] gameOverSceneNameが空です。インスペクターで設定してください。");
        }
        gameOverRoutine = null;
    }

    private IEnumerator FadeOutRoutine()
    {
        if (fadeCanvasGroup == null)
        {
            yield break;
        }

        float duration = Mathf.Max(0f, fadeSeconds);
        if (duration <= 0f)
        {
            fadeCanvasGroup.alpha = 1f;
            yield break;
        }

        float startAlpha = fadeCanvasGroup.alpha;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            fadeCanvasGroup.alpha = Mathf.Lerp(startAlpha, 1f, t);
            yield return null;
        }

        fadeCanvasGroup.alpha = 1f;
    }

    public void ForceGameOver()
    {
        if (!hasTriggeredGameOver) TriggerGameOver();
    }

    /// <summary>
    /// インスペクターまたはテストコードからゲージを0に戻すデバッグ専用ヘルパー。
    /// 通常のゲームプレイではParentDetectionV2.ResetCycle()がゲージをリセットする。
    /// </summary>
    public void DebugResetSuspicionGauge()
    {
        if (motherGauge == null) motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        if (motherGauge != null) motherGauge.SetGaugeDirect(0);
        if (showDebugLogs) Debug.Log("[CaughtReactionController] DebugResetSuspicionGauge: ゲージを0に設定（デバッグ専用）");
    }
}