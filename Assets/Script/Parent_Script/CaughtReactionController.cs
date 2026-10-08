using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

/// <summary>
/// CaughtReactionController：
///
/// ゲージ管理 — 厳密な単一書き込みモデル：
///   MotherGaugeに書き込む唯一のスクリプトはMotherSuspicionSystem
///   （毎フレームのSetGaugeDirectによる増減、大きな音によるAddGauge）。
///   このスクリプトはMotherGaugeに一切書き込まない。
///
/// このスクリプトの責務：
///   - ゲームオーバー監視：ゲージを監視し、最大値到達時にシーン／UDP遷移を実行
///   - MotherSuspicionSystemへNotifyGameOverを転送し、進行を停止
///   - ゲームオーバー時にゲームロジックのコンポーネントを無効化
/// </summary>
public class CaughtReactionController : MonoBehaviour
{
    [Header("システム参照")]
    [SerializeField] private MotherSuspicionSystem suspicionSystem;
    [SerializeField] private SleepingController sleepingController;
    [SerializeField] private DoorController doorController;
    [SerializeField] private ParentUdpSender udpSender;
    [SerializeField] private MotherGauge motherGauge;

    [Header("シーン設定")]
    [SerializeField] private string gameOverSceneName = "ParentGameOver";

    [SerializeField, Min(0f)] private float sceneChangeDelay;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs;

    // ゲームオーバー状態フラグ
    private bool _hasTriggeredGameOver;
    private Coroutine _gameOverRoutine;

    private void Start()
    {
        EnsureSuspicionSystem();
        if (sleepingController == null)
            sleepingController = Object.FindFirstObjectByType<SleepingController>();
        if (doorController == null)
            doorController = Object.FindFirstObjectByType<DoorController>();
        EnsureUdpSender();
        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        _hasTriggeredGameOver = false;

        if (showDebugLogs)
            Debug.Log("[CaughtReactionController] initialized - game-over watchdog only (gauge owned by PD)");
    }

    /// <summary>
    /// 怪しさ管理元（MotherSuspicionSystem）を解決する。
    /// 無敵判定・ゲームオーバー抑止で参照する。未設定ならシーンから自動検索する。
    /// </summary>
    private MotherSuspicionSystem EnsureSuspicionSystem()
    {
        if (suspicionSystem == null)
            suspicionSystem = Object.FindFirstObjectByType<MotherSuspicionSystem>();

        return suspicionSystem;
    }

    private void EnsureUdpSender()
    {
        if (udpSender == null)
            udpSender = Object.FindFirstObjectByType<ParentUdpSender>();
        if (udpSender == null)
            udpSender = ParentUdpSender.Instance;
    }

    private void Update()
    {
        if (_hasTriggeredGameOver) return;
        if (motherGauge == null) return;

        // 無敵モード中は最大ゲージ監視による捕獲も行わない。
        if (EnsureSuspicionSystem() != null && suspicionSystem.IsInvincible) return;

        // ゲームオーバー監視：PDは毎フレームゲージを書き込むが、PD側が無敵中に加算を止めた結果として
        // 「ゲージ最大のまま」が残ることがある。無敵を使ったことがあるサイクルでは、
        // ゲージ最大だけを根拠に即座に捕獲しない（＝無敵OFF直後の翌フレーム捕獲を防ぐ）。
        // 無敵を一度も使っていない通常プレイでは従来どおり最大到達で即捕獲する。
        if (suspicionSystem != null && suspicionSystem.WasInvincibleUsedInThisPlay)
        {
            // ゲージが最大未満に戻れば通常の監視を再開する（次の有効な発見判定で捕獲を許可）。
            if (motherGauge.currentGauge < motherGauge.maxGauge)
            {
                suspicionSystem.ClearInvincibleUsage();
            }
            else
            {
                // 最大のまま：PD側の有効な発見判定（OnPlayerCaught）に捕獲を委ねる。
                return;
            }
        }

        if (motherGauge.currentGauge >= motherGauge.maxGauge)
        {
            TriggerGameOver();
            return;
        }

        if (showDebugLogs)
        {
            bool isLooking  = (suspicionSystem != null) && suspicionSystem.isMotherLookingNow;
            bool isSleeping = (sleepingController != null) && sleepingController.IsSleeping;
            Debug.Log($"[CaughtReactionController-Update] isMotherLookingNow={isLooking} | IsSleeping={isSleeping} | gauge={motherGauge.currentGauge}/{motherGauge.maxGauge} | (gauge written exclusively by PD)");
        }
    }

    /// <summary>
    /// 親機がチェックを行ったことを通知する。
    /// このスクリプトはゲージを変更しない（すべての書き込みはPDが管理）。
    /// 既存のUnityEvent接続を壊さないためスタブとして残す。
    /// </summary>
    public void OnMotherCheck(bool isFullCheck)
    {
        if (showDebugLogs)
            Debug.Log($"[CaughtReactionController] OnMotherCheck ({(isFullCheck ? "FULL" : "PEEK")})を受信 - ゲージ書き込みはPDの責務のため、ここでは何もしません");
    }

    /// <summary>
    /// 大きな音を出すアイテムが発生したことを通知する。
    /// このスクリプトはゲージを変更しない（書き込みはPD.OnLoudItemTriggeredが管理）。
    /// 既存の接続を壊さないためスタブとして残す。
    /// </summary>
    public void OnLoudItemTriggered()
    {
        if (showDebugLogs)
            Debug.Log("[CaughtReactionController] OnLoudItemTriggeredを受信 - ゲージ書き込みはPDの責務のため、ここでは何もしません");
    }

    /// <summary>
    /// 永続的なゲームオーバーシーケンスを開始する
    /// </summary>
    private void TriggerGameOver()
    {
        if (_hasTriggeredGameOver) return;

        // 【無敵モード】テスト用無敵（Lキー）がONの間はゲームオーバーを抑止する。
        //   ゲージ最大到達を監視する安全網からの捕獲入口のため、ここでも一括ガードする。
        //   抑止中は _hasTriggeredGameOver を立てない（OFF後に次の有効な判定で通常処理を行う）。
        if (EnsureSuspicionSystem() != null && suspicionSystem.ShouldBlockCapture())
        {
            Debug.Log("[CaughtReactionController] 無敵モード中のためゲームオーバーを抑止します");
            return;
        }

        _hasTriggeredGameOver = true;

        // MotherSuspicionSystemに永続的なゲームオーバーを通知し、進行を停止させる
        if (suspicionSystem != null)
        {
            try
            {
                suspicionSystem.NotifyGameOver();
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        if (showDebugLogs)
            Debug.LogWarning("[CaughtReactionController] ゲームオーバー発生 - 疑惑が最大値に到達");

        // ★★★ 親機捕獲確定をParentUdpSenderに通知（CAUGHTの即時送信・常駐再送・resultProcessed管理） ★★★
        EnsureUdpSender();
        if (udpSender != null)
        {
            udpSender.NotifyGameOverFromParentCatch();
            if (showDebugLogs) Debug.Log("[CaughtReactionController] Notified ParentUdpSender of caught game over.");
        }
        else if (showDebugLogs)
        {
            Debug.LogWarning("[CaughtReactionController] ParentUdpSender not found — NotifyGameOverFromParentCatch skipped.");
        }

        // ゲームロジックのコンポーネントを無効化
        DisableGameLogic();

        // ゲームオーバー処理（シーンロード）を開始
        if (_gameOverRoutine != null) StopCoroutine(_gameOverRoutine);
        _gameOverRoutine = StartCoroutine(GameOverSequence());
    }

    private void DisableGameLogic()
    {
        if (doorController != null) { doorController.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Door Controller disabled"); }
        if (sleepingController != null) { sleepingController.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Sleeping Controller disabled"); }
        if (suspicionSystem != null) { suspicionSystem.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Parent Detection disabled"); }
    }

    private IEnumerator GameOverSequence()
    {
        // 設定された遅延時間を待機（タイムスケールに依存しないRealtime）
        float delay = Mathf.Max(0f, sceneChangeDelay);
        if (delay > 0f)
        {
            yield return new WaitForSecondsRealtime(delay);
        }

        // 親機側のゲームオーバーシーン（ParentGameOver）をロード
        if (showDebugLogs) Debug.Log($"[CaughtReactionController] {gameOverSceneName}シーンをロード中...");

        if (!string.IsNullOrEmpty(gameOverSceneName))
        {
            SceneManager.LoadScene(gameOverSceneName);
        }
        else
        {
            Debug.LogError("[CaughtReactionController] gameOverSceneNameが空です。インスペクターで設定してください。");
        }
        _gameOverRoutine = null;
    }

    public void ForceGameOver()
    {
        // 【無敵モード】無敵中は強制ゲームオーバーも抑止する（捕獲の共通入口で一括ガード）。
        if (EnsureSuspicionSystem() != null && suspicionSystem.ShouldBlockCapture())
        {
            Debug.Log("[CaughtReactionController] 無敵モード中のため ForceGameOver を抑止します");
            return;
        }

        if (!_hasTriggeredGameOver) TriggerGameOver();
    }

    /// <summary>
    /// インスペクターまたはテストコードからゲージを0に戻すデバッグ専用ヘルパー。
    /// 通常のゲームプレイではMotherSuspicionSystem.ResetCycle()がゲージをリセットする。
    /// </summary>
    public void DebugResetSuspicionGauge()
    {
        if (motherGauge == null) motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        if (motherGauge != null) motherGauge.SetGaugeDirect(0);
        if (showDebugLogs) Debug.Log("[CaughtReactionController] DebugResetSuspicionGauge: ゲージを0に設定（デバッグ専用）");
    }
}