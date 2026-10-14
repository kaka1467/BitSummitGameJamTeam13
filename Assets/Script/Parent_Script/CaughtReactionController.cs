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
    [SerializeField] private MotherGauge motherGauge;

    [Header("ゲームオーバー演出参照")]
    [Tooltip("捕獲時の演出（廊下／庭／片付け入室中／片付けPeek）の順番を管理する。未設定なら自動検索する。")]
    [SerializeField] private MotherGameOverSequenceController gameOverSequenceController;
    [Tooltip("捕獲した位置・状況の保存と、演出の移動に使う。未設定なら自動検索する。")]
    [SerializeField] private MotherApproachController approachController;
    [Tooltip("片付けの進行段階（入室中かPeek中か）を取得する。未設定なら自動検索する。")]
    [SerializeField] private MotherChoreController choreController;

    [Tooltip("捕獲確定のMAX到達音・UI非表示・捕獲後の心拍を受け持つ。未設定なら自動検索する。")]
    [SerializeField] private GameOverPresentationController presentationController;

    [Header("シーン設定")]
    [SerializeField] private string gameOverSceneName = "ParentGameOver";

    [Tooltip("演出の『あと』の追加待機（秒）。Realtime。演出完了を待たずにシーンを切り替える用途には使わない。")]
    [SerializeField, Min(0f)] private float sceneChangeDelay;

    [Header("演出タイミング")]
    [Tooltip("演出を始める『前』の待機（秒）。Realtime。0で即開始。")]
    [SerializeField, Min(0f)] private float performanceStartDelay;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs;

    // ゲームオーバー状態フラグ
    private bool _hasTriggeredGameOver;
    private Coroutine _gameOverRoutine;

    // 捕獲時に保存する引き継ぎ情報（状態が消える前に保存する）
    private MotherGameOverCaptureContext _context;

    // 実行時解決する親機Host（Inspector割当なし・非シリアライズ）
    private ParentUdpRuntimeHost _host;

    private void Start()
    {
        EnsureSuspicionSystem();
        if (sleepingController == null)
            sleepingController = Object.FindFirstObjectByType<SleepingController>();
        if (doorController == null)
            doorController = Object.FindFirstObjectByType<DoorController>();
        EnsureHost();
        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        if (gameOverSequenceController == null)
            gameOverSequenceController = Object.FindFirstObjectByType<MotherGameOverSequenceController>();
        if (approachController == null)
            approachController = Object.FindFirstObjectByType<MotherApproachController>();
        if (choreController == null)
            choreController = Object.FindFirstObjectByType<MotherChoreController>();
        if (presentationController == null)
            presentationController = Object.FindFirstObjectByType<GameOverPresentationController>();

        _hasTriggeredGameOver = false;
        _context = default;

        // 再プレイで音・UI・再生済みフラグが残らないように初期化する。
        if (presentationController != null)
            presentationController.ResetForReplay();

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

    /// <summary>
    /// 稼働中の親機Hostを優先し、無ければ初期化済みHostを解決する（未初期化Hostは使わない）。
    ///  ・取得できた結果は保持する（毎フレームの検索は行わない）。
    ///  ・Connected条件は付けない（Disconnectedでも稼働Hostは参照できる）。
    /// </summary>
    private void EnsureHost()
    {
        if (_host != null) return;

        ParentUdpRuntimeHost running = ParentUdpRuntimeHost.Running;
        if (running != null) { _host = running; return; }

        ParentUdpRuntimeHost found = Object.FindFirstObjectByType<ParentUdpRuntimeHost>();
        if (found != null && found.IsConfigured) _host = found;
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

        // 捕獲確定（多重実行防止済み。ここから演出→結果画面へ進む）。
        Debug.Log($"[CaughtReactionController] 捕獲確定: ゲームオーバー処理を開始します" +
                  $"（母ゲージ最大={motherGauge != null && motherGauge.currentGauge >= motherGauge.maxGauge}）", this);

        // ★ 捕獲確定の音とUI（MAX到達音／怪しさメーター非表示＋Danger保持／通常心拍の停止）★
        //  ・無敵による抑止はこの上で済んでいるため、ここまで来た時だけ鳴る。
        //  ・ゲージ最大監視とOnPlayerCaughtの両方がこのメソッドへ合流するため、同じ捕獲では1回だけになる
        //    （_hasTriggeredGameOver と GameOverPresentationController 側の両方で防止）。
        if (presentationController != null)
            presentationController.NotifyCaptured();
        else
            Debug.LogWarning("[CaughtReactionController] 【設定不足】GameOverPresentationController が見つからないため、" +
                             "MAX到達音とUI切り替えを行いません（演出は継続します）", this);

        // ★ 演出へ引き継ぐ状況を保存する（状態を消す処理より前）★
        //  ・ゲージ最大監視経由：ここでは通常の進行状態がまだ生きているため、現在の状態を直接保存する。
        //  ・OnPlayerCaught経由：すでに AbortChore が呼ばれているため、
        //    MotherChoreController が中断時に記録した「記憶値」を使って判定する。
        CaptureGameOverContext();

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

        // ★ 捕獲直後に、通常移動・帰路・片付け移動・Peek横スライド・通常旋回を停止する ★
        //   performanceStartDelay の待機に入る前に止め、保存位置と演出開始位置がずれないようにする。
        //   ・位置リセット・モデル非表示は行わない（演出は捕獲した位置から開始する）。
        //   ・Animator とドア回転は止めない（演出用の移動・再生は継続できる）。
        if (gameOverSequenceController != null)
            gameOverSequenceController.PrepareForCapture();
        // 演出Controllerが未設定でも通常移動は止める（位置を維持したまま。二重呼び出しは冪等）。
        if (approachController != null)
            approachController.EnterGameOverExclusiveControl();

        if (showDebugLogs)
            Debug.LogWarning("[CaughtReactionController] ゲームオーバー発生 - 疑惑が最大値に到達");

        // ★★★ 親機捕獲確定を親機Hostに通知（CAUGHTの即時送信・常駐再送・resultProcessed管理） ★★★
        EnsureHost();
        if (_host != null)
        {
            _host.NotifyGameOverFromParentCatchPrepared();
            if (showDebugLogs) Debug.Log("[CaughtReactionController] Notified ParentUdpRuntimeHost of caught game over.");
        }
        else if (showDebugLogs)
        {
            Debug.LogWarning("[CaughtReactionController] ParentUdpRuntimeHost not found — NotifyGameOverFromParentCatch skipped.");
        }

        // ゲームロジックのコンポーネントを無効化
        DisableGameLogic();

        // ゲームオーバー処理（シーンロード）を開始
        if (_gameOverRoutine != null) StopCoroutine(_gameOverRoutine);
        _gameOverRoutine = StartCoroutine(GameOverSequence());
    }

    private void DisableGameLogic()
    {
        // DoorController は「演出でドア本体を動かす」必要があるため enabled は維持する。
        // 代わりに手動入力（Pキー）だけを止め、演出がドアを閉められる事故を防ぐ。
        if (doorController != null)
        {
            doorController.ManualInputEnabled = false;
            if (showDebugLogs) Debug.Log("[CaughtReactionController] Door Controller: 手動入力のみ停止（回転は演出のため維持）");
        }
        if (sleepingController != null) { sleepingController.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Sleeping Controller disabled"); }
        if (suspicionSystem != null) { suspicionSystem.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Parent Detection disabled"); }
    }

    /// <summary>
    /// ゲームオーバー演出（4パターン）を実行してから、親機側のゲームオーバーシーンへ遷移する。
    ///  ・演出前の待機＝performanceStartDelay、演出後の追加待機＝sceneChangeDelay。
    ///  ・演出Controllerが未設定／ルート未設定／失敗／タイムアウトでも、必ず結果画面へ進める。
    ///  ・シーン遷移の実行元はこのメソッドに一本化する（多重実行は _hasTriggeredGameOver で防止済み）。
    /// </summary>
    private IEnumerator GameOverSequence()
    {
        // ── 演出前の待機（任意・Realtime）────────────────────────────────────
        if (performanceStartDelay > 0f)
            yield return new WaitForSecondsRealtime(performanceStartDelay);

        // 結果画面へ進む理由を、ログで必ず分かるように記録する。
        string transitionReason;

        // ── 演出（廊下／庭／片付け入室中／片付けPeek）────────────────────────
        if (gameOverSequenceController == null)
            gameOverSequenceController = Object.FindFirstObjectByType<MotherGameOverSequenceController>();

        if (gameOverSequenceController == null)
        {
            transitionReason = "設定不足（MotherGameOverSequenceController が見つからない）";
            Debug.LogError("[CaughtReactionController] 【設定不足】MotherGameOverSequenceController が見つからないため、" +
                           "演出を行わずシーン遷移のみ行います（GameOverController の配置と CaughtReactionController の参照を確認してください）");
        }
        else if (!gameOverSequenceController.HasAnyRouteConfigured)
        {
            transitionReason = "設定不足（4ルートの steps が未登録）";
            Debug.LogError("[CaughtReactionController] 【設定不足】ゲームオーバー演出のルートにステップ（Waypoint）が登録されていないため、" +
                           "演出を行わずシーン遷移のみ行います。 " + gameOverSequenceController.DescribeRouteConfiguration() +
                           "（Waypointオブジェクトの配置だけでは登録になりません。各 Route.steps へ登録してください）");
        }
        else
        {
            bool finished = false;
            // 演出自体が全体タイムアウトを持ち、必ず onResult を返す（停止中でも無限待機しない）。
            yield return gameOverSequenceController.PlaySequence(_context, ok => finished = ok);

            if (finished)
            {
                transitionReason = "正常完了（演出を最後まで再生）";
                Debug.Log("[CaughtReactionController] ゲームオーバー演出が完了しました");
            }
            else
            {
                transitionReason = "演出失敗／タイムアウト（詳細は MotherGameOverSequence のログ参照）";
                Debug.LogWarning("[CaughtReactionController] ゲームオーバー演出が完了しませんでした（失敗／タイムアウト）— 結果画面へ進みます");
            }
        }

        // ── 演出後の追加待機（任意・Realtime）────────────────────────────────
        float delay = Mathf.Max(0f, sceneChangeDelay);
        if (delay > 0f)
            yield return new WaitForSecondsRealtime(delay);

        // ── 最終的なゲームオーバーシーン遷移 ────────────────────────────────
        // 演出完了（または安全な打ち切り）を子機へ通知してから結果画面へ進む。
        //  ・Jumpscare開始時ではなく、カメラ完了待ちまで終えて遷移が確定したここで送る。
        //  ・子機はこの通知まで結果画面へ進まず、待機中は入力と得点が停止する。
        EnsureHost();
        if (_host != null)
            _host.NotifyGameOverSequenceCompletedPrepared(transitionReason);
        else
            Debug.LogWarning("[CaughtReactionController] 親機Hostが無いため、演出完了通知を送れません" +
                             "（子機は子機側の上限で待機を解除します）", this);

        LoadGameOverScene(transitionReason);
        _gameOverRoutine = null;
    }

    /// <summary>
    /// 親機のゲームオーバーシーンをロードする（このメソッドが最終遷移の唯一の実行元）。
    ///  reason には「正常完了／設定不足／再生失敗／タイムアウト」等を入れてログに明記する。
    /// </summary>
    private void LoadGameOverScene(string reason)
    {
        Debug.Log($"[CaughtReactionController] 結果画面へ遷移します: scene='{gameOverSceneName}' / 理由={reason} / " +
                  "呼び出し元=CaughtReactionController.GameOverSequence", this);

        if (!string.IsNullOrEmpty(gameOverSceneName))
        {
            SceneManager.LoadScene(gameOverSceneName);
        }
        else
        {
            Debug.LogError("[CaughtReactionController] gameOverSceneNameが空です。インスペクターで設定してください。");
        }
    }

    /// <summary>
    /// 捕獲の種類・位置・片付けの進行段階を、演出へ引き継ぐために保存する。
    ///  ・片付けの進行段階は AbortChore が記録した「中断前の記憶値」を使う（状態は消えているため）。
    ///  ・位置は現在位置を使う（演出は捕獲した位置から始め、startPoint へは戻さない）。
    /// </summary>
    private void CaptureGameOverContext()
    {
        if (approachController == null) approachController = Object.FindFirstObjectByType<MotherApproachController>();
        if (choreController == null) choreController = Object.FindFirstObjectByType<MotherChoreController>();

        Transform mother = approachController != null ? approachController.transform : null;

        _context = new MotherGameOverCaptureContext
        {
            startPosition = mother != null ? mother.position : Vector3.zero,
            startRotation = mother != null ? mother.rotation : Quaternion.identity,
            gardenPeek = approachController != null && (approachController.IsGardenPeeking || approachController.IsGardenRoute),
            choreDoorPassed = approachController != null && approachController.IsChoreDoorPassed,
        };

        if (choreController != null)
        {
            _context.choreCapture = choreController.WasActiveOnLastAbort || choreController.IsChoreActive;

            // 中断の記憶値があればそれを優先（片付けをしていた証拠）。無ければ現在段階。
            MotherChoreController.ChorePhase phase = choreController.WasActiveOnLastAbort
                ? choreController.LastAbortedPhase
                : choreController.CurrentPhase;

            _context.chorePhase = phase;
            _context.choreLooking = phase == MotherChoreController.ChorePhase.Looking;
        }

        Debug.Log($"[CaughtReactionController] 捕獲状況を保存: gardenPeek={_context.gardenPeek} " +
                  $"choreCapture={_context.choreCapture} choreLooking={_context.choreLooking} " +
                  $"chorePhase={_context.chorePhase} choreDoorPassed={_context.choreDoorPassed} " +
                  $"position={_context.startPosition.ToString("F2")}");
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