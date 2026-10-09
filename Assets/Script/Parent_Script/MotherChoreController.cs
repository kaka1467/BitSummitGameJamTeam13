using System.Collections;
using UnityEngine;

/// <summary>
/// MotherChoreController：
/// ゲーム後半に母親が部屋へ入室して「片付け」を行い、終了後に退出する演出を管理する。
///
/// 既存との関係（責務の境界）：
///   MotherApproachWarning      — 通常の警告（予告灯・遅延・ルート選択）。片付け中は開始しない。
///   ParentWarningScheduler   — 自動警告のスケジュール。片付け中は発火を待機する。
///   MotherApproachController — 経路移動と向き。片付けは専用ルートを StartChoreRoute() で開始する。
///                              行きは既存の廊下ルート（hallwaypeak と同じ移動順序・移動方法）を再利用し、
///                              doorPoint では Door_Peek ではなく Door_Open で開ける。
///                              帰りは choreReturnPoint_1（Door_Open で全開）→ choreReturnPoint_2（ドア閉め）
///                              → HallwayPoint_3（ドア操作なし）→ hallwayPassByPoint。
///                              既存の onChoreArrived / onChoreCompleted を受け取る。
///                              roomEntryPoints（通常の入室ルート）は使わない。
///   MotherSuspicionSystem          — 通常のドア分岐・疑惑・捕獲。片付けサイクル中は入口で無視する
///                              （SetChoreOverride(true) により抑止する）。
///                              行きの怪しさ加算は EnableChoreSuspicion() / DisableChoreSuspicion() で
///                              既存の継続疑惑と同じ仕組みを使う。
///   本クラス                 — 片付けの開始条件・進行・アニメーション・視線抽選・発見判定の期間。
///
/// 進行（仕様）：
///   専用ルートで歩いて chorePoint へ → 1（片付け始める・1回） → 2（片付け中・ループ）
///   → 4（片付け終わって立つ・1回） → 専用ルートで歩いて choreReturnPoint_1 / _2 へ戻る
///   2 の間に悪いアイテム取得の通知を受けたら視線抽選を行い、当選した場合は 3（こっちを見る・1回）へ移り、
///   終了後に 2 へ戻る。
///
/// 怪しさ加算（行きのみ）：
///   行きの移動開始 〜 chorePoint 到着・向き合わせ完了まで、プレイヤーがゲームを触っていれば加算する。
///   01・02・04 および帰りの移動では加算しない。03 では既存の視線判定期間だけ加算する。
///   帰りでは減少処理も追加しない。
///
/// 制約：
///   ・片付け中は通常の親イベントと重複させない（MotherApproachWarning / Scheduler を抑止する）。
///   ・1、3、4、入退室の歩き中は視線抽選をしない（抽選は 2 のループ中のみ）。
///   ・3 の再生中に通知が来ても再開始や予約をしない。
///   ・3 の途中で片付け時間が終わったら、3 の終了後に 4 へ進む。
///   ・ゲームオーバーになったら片付けを中断し、既存のゲームオーバー処理を妨げない。
///   ・中断・シーン変更・再プレイ時に片付け状態・経路状態・怪しさ加算状態が残らないようにする。
///   ・アニメーションの見た目の切り替えと、発見判定の有効期間を分ける。
///
/// 【手動設定が必要】Animator Controller に Chore / Chore_Peek / Chore_End / Door_Open の
/// ステートを用意し、MotherApproachController の片付け専用ルート（chorePoint など）を Inspector で割り当てる。
/// </summary>
public class MotherChoreController : MonoBehaviour
{
    // ── 参照 ──────────────────────────────────────────────────────────────────
    [Header("システム参照")] [Tooltip("通常の警告シーケンス。片付けの開始可否と重複防止に使用する。")] [SerializeField]
    private MotherApproachWarning warningSystem;

    [Tooltip("親機の経路移動。入室・退室は既存 API をそのまま使用する。")] [SerializeField]
    private MotherApproachController approachController;

    [Tooltip("通常のドア分岐・疑惑・捕獲。片付け中はここの入口処理を抑止する。")] [SerializeField]
    private MotherSuspicionSystem parentDetection;

    [Tooltip("自動警告のスケジューラ。片付け中は発火を待機させる。")] [SerializeField]
    private ParentWarningScheduler warningScheduler;

    [Tooltip("アニメーション操作の窓口。Animator への書き込みはここに集約する（未設定なら自動検索）。")] [SerializeField]
    private MotherAnimationPlayer animationPlayer;

    // ── 開始条件 ──────────────────────────────────────────────────────────────
    [Header("開始条件")] [Tooltip("片付けイベントを許可するゲーム進行率（0〜1）。この値以上になると片付けを開始できる。初期値0.6。")] [SerializeField, Range(0f, 1f)]
    private float choreAllowedProgressRate = 0.6f;

    [Tooltip("1プレイ中に片付けを開始する回数。1以上。")] [SerializeField, Min(1)]
    private int choreCountPerPlay = 1;

    [Tooltip("進行率到達後、片付けを開始するまでの追加待機秒数（乱数下限）。")] [SerializeField, Min(0f)]
    private float startDelayMin = 3f;

    [Tooltip("進行率到達後、片付けを開始するまでの追加待機秒数（乱数上限）。")] [SerializeField, Min(0f)]
    private float startDelayMax = 8f;

    [Tooltip("片付け開始時、通常の警告が進行中なら終了まで待つか。OFFなら片付けを延期する。")] [SerializeField]
    private bool waitForActiveWarning = true;

    // ── 片付け時間 ────────────────────────────────────────────────────────────
    //   片付けの滞在時間は MotherApproachWarning の「片付け演出 > 片付けの滞在時間」を参照する
    //   （ここでは保持しない。設定の唯一の保持元は MotherApproachWarning）。

    // ── 視線 ──────────────────────────────────────────────────────────────────
    //   悪いアイテム取得時の視線発生率は MotherApproachWarning の「片付け演出 > 悪いアイテム取得時の視線発生率」を参照する
    //   （ここでは保持しない）。
    [Tooltip("視線アニメーション開始直後、発見判定を有効にするまでの遅延秒数。見た目とは別に管理する。")] [SerializeField, Min(0f)]
    private float lookDetectionStartDelay = 0.3f;

    [Tooltip("発見判定を有効にしたまま維持する秒数。経過後に解除してアニメーション再生は継続する。")] [SerializeField, Min(0f)]
    private float lookDetectionDuration = 1.5f;

    [Tooltip("片付けで視線を分散させるときの最小の片付け区間（秒）。区間がこれ未満になる場合は、滞在時間に収まらない旨を警告する。")] [SerializeField, Min(0f)]
    private float minChoreSegmentSeconds = 0.5f;

    // ── アニメーション ────────────────────────────────────────────────────────
    //   ステート名・パラメーター名・再生方法・再生完了判定は MotherAnimationPlayer が保持する。
    //   ここでは用途別API（RequestChoreStart / RequestChorePeek / RequestChoreEnd /
    //   RequestDoorOpen と各 WaitFor…）だけを呼び、ステート名は渡さない。

    // ── ドア開閉（片付け） ────────────────────────────────────────────────────
    [Header("片付けのドア開閉")] [Tooltip("片付けで使用する DoorController。行きの doorPoint と帰りの choreReturnPoint_1 でドアを開け、choreReturnPoint_2 で閉める。")] [SerializeField]
    private DoorController doorController;

    [Tooltip("Door_Open 再生後、ドアが目標角度へ到達するまで待つ最大秒数。超過しても完了待ちを続けない。")] [SerializeField, Min(0.5f)]
    private float doorOpenWaitTimeout = 5f;

    [Tooltip("帰りの HallwayPoint_3 到着でドアを閉める。完了を待つ最大秒数。")] [SerializeField, Min(0.5f)]
    private float doorCloseWaitTimeout = 5f;

    // ── 退室待ち ──────────────────────────────────────────────────────────────
    [Header("帰り待ち")]
    [Tooltip("hallwayPassBy での退場・onChoreCompleted を待つ最大秒数。超過してもサイクルは必ず閉じる。")]
    [SerializeField, Min(1f)]
    private float returnSafetyTimeout = 20f;

    // ── 動作フラグ ────────────────────────────────────────────────────────────
    [Header("動作")] [Tooltip("片付けイベント自体を有効にする。OFF にすると通常の親イベントのみになる。")] [SerializeField]
    private bool choreEnabled = true;

    [Tooltip("進行率・時間・視線抽選のデバッグログを出す。")] [SerializeField]
    private bool showDebugLogs = true;

    // ── 公開状態 ──────────────────────────────────────────────────────────────
    /// <summary>片付けサイクル進行中か（通常の親イベントを抑止する期間）。</summary>
    public bool IsChoreActive { get; private set; }

    /// <summary>視線アニメーション（Chore_Peek）を再生中か。</summary>
    public bool IsLooking => _gazeInProgress;

    /// <summary>
    /// 片付けの再生が失敗したか（呼び出し元が経路を中断するために参照する）。
    /// Door_Open / Chore_End の再生完了を確認できなかった場合に true になる。
    /// </summary>
    public bool IsChoreRouteFailed => _choreRouteFailed;

    // ── 内部状態 ──────────────────────────────────────────────────────────────
    private Coroutine _mainRoutine; // 片付け本体（開始待ち＋行き〜帰り）
    private bool _choreStartRequested; // このプレイで開始要求を出したか
    private int _choreStartCount; // このプレイで開始した回数
    private bool _manualStartRequested; // 6キーによる手動開始の要求を保持しているか（開始待ちは1件だけ）
    private bool _manualStartPending; // 開始待ちコルーチンが手動要求から動いているか
    private bool _subscribed; // MotherApproachController のイベント購読中か
    private bool _gazeInProgress; // 視線（Chore_Peek）を再生中か（IsLooking）
    private int _targetLookCount; // このサイクルの目標視線回数（開始時に抽選して固定）
    private int _completedLookCount; // 正常終了を確認できた視線の回数
    private bool _detectionActive; // 発見判定（isMotherLookingNow）を有効にしているか
    private float _detectionEndTime; // 発見判定を解除する時刻（Time.time 基準）
    private bool _detectionStartPending; // 発見判定の開始待ちか
    private float _detectionStartTime; // 発見判定を開始する時刻（Time.time 基準）
    private bool _choreArrived; // chorePoint 到着・向き合わせ完了（onChoreArrived）を受けたか
    private bool _choreCompleted; // 退場完了（onChoreCompleted）を受けたか
    private bool _choreApproachSuspicionStarted; // 行きの怪しさ加算を開始済みか（1回だけ発行）
    private bool _choreEndRequested;             // Chore_End への遷移要求を発行済みか（二重要求防止）
    private bool _choreRouteFailed;              // 片付けの再生失敗（呼び出し元へ伝えて経路を中断させる）
    private bool _choreStartIsManual;            // このサイクルが6キーの手動開始か（照明予兆の高疑惑判定に使う）

    // ──────────────────────────────────────────────────────────────────────────
    //  Unity ライフサイクル
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        ResolveReferences();
        SubscribeApproachEvents();
        ResetChoreState();
    }

    private void OnEnable()
    {
        // シーン遷移やコンポーネント再有効化で古い購読が残らないようにする。
        SubscribeApproachEvents();
    }

    private void OnDisable()
    {
        UnsubscribeApproachEvents();
        // ゲームオーバー・シーンアンロードで片付け状態が残らないようにする。
        AbortChore("OnDisable");
    }

    private void OnDestroy()
    {
        UnsubscribeApproachEvents();
    }

    private void Update()
    {
        // 発見判定の開始・終了は、アニメーションの見た目とは独立に時間で管理する。
        if (_detectionStartPending && Time.time >= _detectionStartTime)
        {
            _detectionStartPending = false;
            SetDetection(true);
        }

        if (_detectionActive && Time.time >= _detectionEndTime)
        {
            SetDetection(false);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  公開 API
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ゲーム進行率の通知。しきい値（choreAllowedProgressRate）以上になると片付けを開始する。
    /// MotherSuspicionSystem 経由で、子機からの進行通知（または親機側の経過時間）で呼ばれる。
    /// </summary>
    public void NotifyGameProgress(float progressRate)
    {
        if (!choreEnabled || IsChoreActive) return;
        if (_choreStartRequested || _choreStartCount >= choreCountPerPlay) return;
        if (progressRate < choreAllowedProgressRate) return;

        _choreStartRequested = true;
        float delay = Random.Range(Mathf.Min(startDelayMin, startDelayMax),
            Mathf.Max(startDelayMin, startDelayMax));

        if (showDebugLogs)
            Debug.Log($"[MotherChore] 進行率 {progressRate:F2} ≥ {choreAllowedProgressRate:F2} — {delay:F1}s 後に片付けを開始します");

        if (_mainRoutine != null) StopCoroutine(_mainRoutine);
        _mainRoutine = StartCoroutine(StartChoreAfterDelay(delay));
    }

    /// <summary>
    /// 現在のシーンがゲームプレイシーン（親機の GameScene）か。
    /// ParentUdpSender の gameplaySceneName と同じ判定を使う（未設定なら名前比較にフォールバック）。
    /// </summary>
    private bool IsGameplayScene()
    {
        string active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

        ParentUdpSender sender = ParentUdpSender.Instance;
        if (sender != null && !string.IsNullOrEmpty(sender.gameplaySceneName))
            return active == sender.gameplaySceneName;

        return active == "ParentGameScene";
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  6キー：片付け演出の手動テスト開始
    //   ・ゲーム進行率とランダムな開始待ち時間を無視して開始を要求する。
    //   ・通常の片付け制御（ChoreRoutine）を通すため、通常イベント停止・怪しさ加算・
    //     アニメーション制御はすべて通常どおり実行される。
    //   ・自動発生の1プレイ回数制限を超えて試せる（回数を消費しない）。
    //   ・開始待ちの要求は1件だけ保持し、連打しても重複させない。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 6キーによる片付け演出の手動テスト開始要求。
    /// 開始できる場合は true、開始不可（理由は Console 出力）の場合は false を返す。
    ///  ・ゲーム進行率（choreAllowedProgressRate）と開始待ち（startDelayMin/Max）を無視する。
    ///  ・自動発生の回数制限（choreCountPerPlay）を消費しない。
    ///  ・通常の親イベントが進行中なら、その終了を待ってから開始する。
    ///  ・重複防止は「開始待ち（_manualStartRequested／_manualStartPending）」と
    ///    「実行中（IsChoreActive）」だけで行い、実行済みフラグは持たない
    ///    （同一プレイ中に何度でも試せるようにするため）。
    ///  ・ゲームオーバー後は受け付けない。
    /// </summary>
    public bool RequestManualStart()
    {
        // ── 開始不可の理由を Console に出す ──────────────────────────────────
        if (!choreEnabled)
        {
            Debug.LogWarning("[MotherChore] 6キー：片付け演出が無効（choreEnabled=false）のため開始しません");
            return false;
        }

        // ゲームプレイシーン以外（結果画面・タイトル・ロード中）では開始しない。
        //   MotherChoreController は GameScene 専用のため通常は到達しないが、
        //   明示的にガードして結果画面・タイトルでの誤動作を防ぐ。
        if (!IsGameplayScene())
        {
            Debug.LogWarning("[MotherChore] 6キー：ゲームプレイシーンではないため開始しません" +
                             $"（現在='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'）");
            return false;
        }

        if (IsChoreActive)
        {
            Debug.Log("[MotherChore] 6キー：片付け実行中のため開始しません（重複防止）");
            return false;
        }

        // 開始待ち中の連打を無視する（要求は1件だけ保持する）。
        if (_manualStartRequested)
        {
            Debug.Log("[MotherChore] 6キー：既に開始待ちを受け付けているため無視します（重複防止）");
            return false;
        }

        // 開始待ち（自動発生）が既に動いている場合は、二重に開始しない。
        if (_choreStartRequested && !_manualStartPending)
        {
            Debug.Log("[MotherChore] 6キー：自動発生の開始待ちが進行中のため、手動要求は受け付けません（重複防止）");
            return false;
        }

        if (parentDetection != null && parentDetection.isCaught)
        {
            Debug.LogWarning("[MotherChore] 6キー：ゲームオーバー中のため開始しません");
            return false;
        }

        // 参照が無いと通常の片付け制御（経路・抑止）が成立しないため開始しない。
        if (approachController == null || warningSystem == null)
        {
            Debug.LogWarning("[MotherChore] 6キー：approachController または warningSystem が未設定のため開始できません");
            return false;
        }

        // ── 要求を1件だけ保持して開始待ちを開始する（進行率・待ち時間は無視）────────
        _manualStartRequested = true;
        _manualStartPending = true;

        Debug.Log("[MotherChore] 6キー：片付け演出の手動開始を要求しました" +
                  (warningSystem.isWarningActive ? "（通常イベントの終了を待ちます）" : ""));

        if (_mainRoutine != null) StopCoroutine(_mainRoutine);
        _mainRoutine = StartCoroutine(StartChoreAfterDelay(0f, isManual: true));
        return true;
    }

    /// <summary>
    /// 悪いアイテム取得の通知。
    ///   【重要】片付けの視線は「回数保証の自然視線」（ChorePerformPhase の1つの制御）だけで行う。
    ///   悪いアイテム由来の視線は使用しない（自然視線に混ざらないようここでは何もしない。再開始・予約もしない）。
    /// </summary>
    public void NotifyBadItemCollected()
    {
        if (!IsChoreActive) return;

        if (showDebugLogs)
            Debug.Log("[MotherChore] 悪いアイテム通知を受信しましたが、悪いアイテム由来の視線は使用しません（自然視線のみ）");
    }

    /// <summary>
    /// 片付けを強制中断する（ゲームオーバー・シーン遷移・リセット用）。
    /// 既存のゲームオーバー処理には何も介入しない（片付けの状態だけを戻す）。
    /// </summary>
    public void AbortChore(string reason)
    {
        bool wasActive = IsChoreActive;

        if (_mainRoutine != null)
        {
            StopCoroutine(_mainRoutine);
            _mainRoutine = null;
        }

        _gazeInProgress = false;
        _detectionStartPending = false;
        _choreArrived = false;
        _choreApproachSuspicionStarted = false;
        _choreEndRequested = false;
        // 未消費の終了要求・Trigger を Animator に残さない（再プレイ・中断で持ち越さない）。
        ResolveAnimationPlayer()?.ResetChoreParameters();
        _choreRouteFailed = false;
        _choreCompleted = false;
        // 6キーの開始待ち要求もクリアする（中断・ゲームオーバー・シーン変更で残さない）。
        _manualStartRequested = false;
        _manualStartPending = false;
        IsChoreActive = false;
        SetDetection(false);

        // 経路と怪しさ加算を確実に解除する（ゲームオーバー・中断・OnDisable・再プレイ）。
        if (parentDetection != null)
        {
            parentDetection.DisableChoreSuspicion();
            // ドア操作の待機・歩き/移動抑止も残さない（通常イベントのドア操作へ遅れて干渉しないようにする）。
            parentDetection.SetChoreWalkingOverrideSuppressed(false);
            parentDetection.SetChoreMovementSuppressed(false);
        }

        if (approachController != null)
            approachController.AbortChoreRoute(reason);

        // 通常の親イベントを再び許可する（イベント停止状態を残さない）。
        ReleaseChoreLocks();

        if (!wasActive) return;

        if (showDebugLogs)
            Debug.Log($"[MotherChore] 片付けを中断します（{reason}）");
    }

    /// <summary>再プレイ・シーン開始時に内部状態を初期化する。</summary>
    public void ResetChoreState()
    {
        if (_mainRoutine != null)
        {
            StopCoroutine(_mainRoutine);
            _mainRoutine = null;
        }

        _choreStartRequested = false;
        _choreStartCount = 0;
        _manualStartRequested = false;
        _manualStartPending = false;
        _gazeInProgress = false;
        _targetLookCount = 0;
        _completedLookCount = 0;
        _detectionStartPending = false;
        _choreArrived = false;
        _choreApproachSuspicionStarted = false;
        _choreEndRequested = false;
        // 未消費の終了要求・Trigger を Animator に残さない（再プレイ・中断で持ち越さない）。
        ResolveAnimationPlayer()?.ResetChoreParameters();
        _choreRouteFailed = false;
        _choreCompleted = false;
        IsChoreActive = false;
        SetDetection(false);

        // 経路状態と怪しさ加算状態も確実に解除する（再プレイで持ち越さない）。
        if (parentDetection != null)
        {
            parentDetection.DisableChoreSuspicion();
            parentDetection.SetChoreWalkingOverrideSuppressed(false);
            parentDetection.SetChoreMovementSuppressed(false);
        }

        if (approachController != null)
            approachController.ResetChoreRoute();

        ReleaseChoreLocks();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  片付け本体
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 開始待ち。delay 経過後、通常イベントの終了を待ってから片付けを開始する。
    /// isManual=true（6キー）の場合は、進行率と待ち時間を無視し、回数制限を消費しない。
    /// </summary>
    private IEnumerator StartChoreAfterDelay(float delay, bool isManual = false)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, delay));

        // 待機中に通常の親イベントが始まったら、終了を待つ（重複させない）。
        if (warningSystem != null && warningSystem.isWarningActive)
        {
            if (!waitForActiveWarning && !isManual)
            {
                if (showDebugLogs)
                    Debug.Log("[MotherChore] 通常イベントが進行中のため片付けを開始しません（次回再挑戦）");
                _choreStartRequested = false;
                yield break;
            }

            // 手動要求（6キー）は「通常イベントの終了を待って開始する」仕様のため、
            // waitForActiveWarning が false でも待つ。
            Debug.Log("[MotherChore] 通常イベントの終了を待ってから片付けを開始します" +
                      (isManual ? "（6キーの手動要求）" : ""));
            while (warningSystem != null && warningSystem.isWarningActive)
                yield return null;
        }

        // ゲームオーバー確定後は片付けを開始しない。
        if (parentDetection != null && parentDetection.isCaught)
        {
            if (isManual)
            {
                Debug.LogWarning("[MotherChore] 6キー：待機中にゲームオーバーになったため開始しません");
                _manualStartRequested = false;
                _manualStartPending = false;
            }

            _choreStartRequested = false;
            yield break;
        }

        if (IsChoreActive)
        {
            if (isManual)
            {
                Debug.Log("[MotherChore] 6キー：待機中に別の片付けが開始されたため、要求を破棄します（重複防止）");
                _manualStartRequested = false;
                _manualStartPending = false;
            }

            yield break;
        }

        // 回数制限は自動発生のみに適用する（手動テストは制限を超えて試せる・回数を消費しない）。
        if (!isManual)
        {
            if (_choreStartCount >= choreCountPerPlay) yield break;
            _choreStartCount++;
        }

        if (isManual)
        {
            // 要求を消費する（開始待ちを閉じる）。実行済みフラグは持たない
            // （片付け完了・ロック解除後に再び6キーを受け付けるため）。
            _manualStartRequested = false;
            _manualStartPending = false;
            Debug.Log("[MotherChore] 6キー：手動開始を実行します（自動発生の回数は消費しません）");
        }

        _choreStartRequested = false;
        _choreStartIsManual = isManual;   // 照明予兆の高疑惑判定に使う（6キーは true）

        if (_mainRoutine != null) StopCoroutine(_mainRoutine);
        _mainRoutine = StartCoroutine(ChoreRoutine());
    }

    private IEnumerator ChoreRoutine()
    {
        // ── サイクル開始：通常の親イベントを抑止する ─────────────────────────
        IsChoreActive = true;
        _gazeInProgress = false;
        _targetLookCount = 0;
        _completedLookCount = 0;
        SetDetection(false);
        ApplyChoreLocks();

        if (showDebugLogs)
            Debug.Log("[MotherChore] 片付けサイクル開始 — 通常の親イベントを停止します");

        if (approachController == null || warningSystem == null)
        {
            Debug.LogWarning("[MotherChore] approachController または warningSystem が未設定のため片付けを開始できません", this);
            FinishChoreCycle();
            yield break;
        }

        // 通常イベントが進行中なら、終わるまで待つ（重複させない）。
        while (warningSystem.isWarningActive)
            yield return null;

        // ── 片付け開始の照明の予兆（通常の hallway イベントと同じ照明・順番・待ち時間）──
        //    MotherApproachWarning の既存予兆処理を再利用する（片付け専用の秒数・照明設定は増やさない）。
        //    6キーの手動開始（isManual）でも照明の予兆は省略しない。
        //    通常イベントの移動は二重起動しない（ここでは照明と待機のみ）。
        if (warningSystem != null)
        {
            if (showDebugLogs)
                Debug.Log("[MotherChore] 片付け開始の照明予兆を開始します（通常の hallway と同じ照明処理を再利用）");
            yield return warningSystem.RunChoreForeshadowLights(_choreStartIsManual);
        }

        if (!IsChoreActive) yield break;

        // ── 片付け専用ルート開始（行き → chorePoint到着・向き合わせ → onChoreArrived）──
        //    roomEntryPoints（通常の入室ルート）は使わない。専用経路だけを歩く。
        if (!approachController.StartChoreRoute())
        {
            Debug.LogWarning("[MotherChore] 片付け専用ルートを開始できませんでした（設定不足）— 片付けを中止します");
            FinishChoreCycle();
            yield break;
        }

        // ── 行きの怪しさ加算は「DoorPoint の Door_Open 完了後」から開始する ──
        //    StartChoreRoute 直後（開始待ち・廊下移動・Door_Open 中）では加算しない。
        //    実際の開始は MotherApproachController が ChoreDoorOpenRoutine から
        //    NotifyChoreApproachSuspicionStarted() を呼ぶタイミングで行う（1回だけ）。

        // ── chorePoint 到着・向き合わせ完了待ち（onChoreArrived を購読して検知する）──
        _choreArrived = false;
        while (IsChoreActive && !_choreArrived)
        {
            // 中断（ゲームオーバー等）で経路が閉じられた場合はここで抜ける。
            if (approachController == null || !approachController.IsChoreRouteActive)
            {
                if (!_choreArrived) break;
            }

            yield return null;
        }

        // 到着（向き合わせ完了）で行きの怪しさ加算を終了する。
        // 01・02・04 と帰りの移動では加算しない（ここから先は演技／帰路のみ）。
        if (parentDetection != null)
            parentDetection.DisableChoreSuspicion();

        if (!IsChoreActive) yield break;

        if (!_choreArrived)
        {
            Debug.LogWarning("[MotherChore] chorePoint へ到着できなかったため片付けを中止します");
            FinishChoreCycle();
            yield break;
        }

        // ── 演技：1 → 2（ループ・視線抽選あり）→ 4 ──────────────────────────
        yield return ChorePerformPhase();
        if (!IsChoreActive) yield break;

        // ── 帰り：専用ルートの帰りを開始させる（演技待ちを解除する）──────────
        //    帰りでは怪しさの加算も減少も行わない。
        if (showDebugLogs)
            Debug.Log("[MotherChore] 演技終了 — 帰りの経路へ進みます（怪しさ加算なし）");

        approachController.NotifyChorePerformanceFinished(); // 演技待ちを解除 → 帰り開始

        // ── 退場完了（onChoreCompleted）待ち ──────────────────────────────
        _choreCompleted = false;
        float returnElapsed = 0f;
        while (IsChoreActive && !_choreCompleted && returnElapsed < returnSafetyTimeout)
        {
            returnElapsed += Time.deltaTime;
            yield return null;
        }

        if (IsChoreActive && !_choreCompleted)
        {
            Debug.LogWarning($"[MotherChore] 戻り先への到着を {returnSafetyTimeout:F1}s 待てませんでした — 片付けサイクルを強制終了します");
        }

        FinishChoreCycle();
    }

    /// <summary>
    /// chorePoint での片付け演技：Chore（ループ）と Chore_Peek（視線）を「1つの制御」で進行する。
    ///  ・目標視線回数は MotherSuspicionSystem の設定（怪しさ段階別 Min/Max）から開始時に抽選して固定する。
    ///  ・自然な間隔（滞在時間内に分散）と回数保証を同じ制御でまとめて管理する（競合する別コルーチンを作らない）。
    ///  ・各視線は Chore_Peek の正常終了を確認してから1回として数える（未開始・失敗・中断は数えない）。
    ///  ・各視線の後は Chore へ戻り、短い片付け区間を挟む。
    ///  ・時間満了時に目標未達なら終了要求Boolをまだ立てず、残りの視線を完了してから Chore_End へ進む。
    ///  ・ゲームオーバー・中断は回数保証より優先する。失敗時は無限リトライしない。
    /// </summary>
    private IEnumerator ChorePerformPhase()
    {
        // ── 片付け中：Chore（ループ）へ遷移要求する ──────────────────────────
        //    終了要求は Bool（Chore_ExitRequested）で保持する。開始時は false に戻す。
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        player?.SetChoreExitRequested(false);
        player?.RequestChoreStart();

        float stayDuration = Mathf.Max(0f, ResolveChoreStayDuration());

        // 開始時点の怪しさ段階で設定を固定し、目標回数を Min〜Max の整数から抽選する。
        // （設定元は MotherSuspicionSystem だけ。ここでは結果だけを使う）
        int minLooks, maxLooks;
        ResolveChoreLookCountRange(out minLooks, out maxLooks);
        _targetLookCount = Random.Range(minLooks, maxLooks + 1);
        _completedLookCount = 0;

        // 滞在時間内に視線を分散させるための「短い片付け区間」の長さ。
        float segment = stayDuration / Mathf.Max(1, _targetLookCount + 1);

        if (showDebugLogs)
            Debug.Log($"[MotherChore] 片付け演技開始 | 滞在={stayDuration:F1}s | 目標視線={_targetLookCount}回（{minLooks}〜{maxLooks}）| 区間={segment:F2}s");

        if (segment < minChoreSegmentSeconds)
            Debug.LogWarning($"[MotherChore] 目標視線 {_targetLookCount}回 は滞在 {stayDuration:F1}s に収まらない可能性があります" +
                             $"（区間 {segment:F2}s < 最小 {minChoreSegmentSeconds:F2}s）。時間満了後に残りを完了します。", this);

        float phaseStart = Time.time;

        // ── フェーズ1：滞在時間内に分散して視線を再生する ──────────────────
        while (IsChoreActive && _completedLookCount < _targetLookCount)
        {
            // 短い片付け区間（Chore へ戻って待つ）。滞在時間の満了で打ち切る。
            float segmentEnd = Time.time + segment;
            while (IsChoreActive && Time.time < segmentEnd && (Time.time - phaseStart) < stayDuration)
                yield return null;

            if (!IsChoreActive) break;                              // 中断は回数保証より優先
            if ((Time.time - phaseStart) >= stayDuration) break;    // 時間満了 → フェーズ2へ

            bool lookOk = false;
            yield return ChoreLookOnce(ok => lookOk = ok);
            if (!IsChoreActive) break;
            if (!lookOk)
            {
                Debug.LogWarning("[MotherChore] Chore_Peek の再生完了を確認できなかったため、残りの視線を打ち切ります（失敗）", this);
                break;   // 失敗時に無限リトライしない
            }
            _completedLookCount++;
        }

        // ── フェーズ2：時間満了で目標未達なら、終了要求をまだ立てずに残りを完了する ──
        while (IsChoreActive && _completedLookCount < _targetLookCount)
        {
            // 各視線の後は Chore へ戻る。遷移が落ち着くよう最小の片付け区間を挟む。
            float segmentEnd = Time.time + minChoreSegmentSeconds;
            while (IsChoreActive && Time.time < segmentEnd)
                yield return null;
            if (!IsChoreActive) break;

            bool lookOk = false;
            yield return ChoreLookOnce(ok => lookOk = ok);
            if (!IsChoreActive) break;
            if (!lookOk)
            {
                Debug.LogWarning("[MotherChore] 残りの視線を再生できなかったため打ち切ります（失敗）", this);
                break;
            }
            _completedLookCount++;
        }

        if (showDebugLogs)
            Debug.Log($"[MotherChore] 片付け演技終了 | 視線 {_completedLookCount}/{_targetLookCount}回 | 滞在={stayDuration:F1}s");

        if (!IsChoreActive) yield break;

        // ── Chore_End（片付け終わって立つ・1回）──────────────────────────────
        //    視線がすべて終わってから終了要求Boolを立てる（時間満了で未達でも同じ）。
        if (!_choreEndRequested)
        {
            player?.RequestChoreEnd();
            _choreEndRequested = true;
        }

        bool choreEndOk = false;
        yield return WaitForChoreOneShot(ChoreOneShot.ChoreEnd, ok => choreEndOk = ok);

        if (!choreEndOk)
        {
            Debug.LogWarning("[MotherChore] Chore_End の再生完了を確認できなかったため、歩き復帰を保留します（失敗）", this);
            _choreRouteFailed = true;
            yield break;
        }

        // Chore_End 終了後、Animator の Chore_End → Idle 遷移を待って歩き要求を出す。
        RestoreWalkingAnimation();
    }

    /// <summary>
    /// 片付け／ドア開けの再生後、既存の歩きアニメーションへ確実に戻す。
    /// 実処理は MotherAnimationPlayer が持つ（Animator を直接操作しない）。
    ///
    /// 【重要】Animator Controller には Chore_End / Door_Open から出る遷移が存在しないため、
    /// Chore_End / Door_Open は Animator の Transition で Idle へ自動遷移するため、
    /// ここでは Walk を立てるだけでよい（Play("Idle") による強制復帰は不要）。
    /// </summary>
    private void RestoreWalkingAnimation()
    {
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        if (player == null)
        {
            Debug.LogWarning("[MotherChore] MotherAnimationPlayer が未設定のため歩きへ戻せません", this);
            return;
        }

        // Chore_End / Door_Open → Idle の遷移（Exit Time）が働いた後、Walk=true で
        // Idle → Walk の既存遷移に乗る。直接 Play しない。
        player.SetWalking(true);

        if (showDebugLogs)
            Debug.Log("[MotherChore] 片付け／ドア開け終了 — 歩き要求（Walk=true）を出しました");
    }

    /// <summary>
    /// 片付けの視線（Chore_Peek）を1回だけ再生し、正常終了を確認してから結果を返す。
    ///  ・同時再生しない（呼び出し元 ChorePerformPhase が1つずつ順番に待つ）。
    ///  ・再生中の再要求をしない（直列に呼ばれる前提）。
    ///  ・未消費Triggerは MotherAnimationPlayer.RequestChorePeek が ResetTrigger してから発火する。
    ///  ・発見判定は見た目とは別に、開始遅延・継続時間で管理する（従来どおり）。
    ///  ・未開始・失敗・中断は lookOk=false（呼び出し側は回数に数えない）。
    /// </summary>
    private IEnumerator ChoreLookOnce(System.Action<bool> onResult)
    {
        _gazeInProgress = true;

        ResolveAnimationPlayer()?.RequestChorePeek();

        // 発見判定：開始遅延後に有効化し、継続時間経過で解除する（見た目は再生し続ける）。
        _detectionStartTime = Time.time + Mathf.Max(0f, lookDetectionStartDelay);
        _detectionEndTime = _detectionStartTime + Mathf.Max(0f, lookDetectionDuration);
        _detectionStartPending = true;

        // Chore_Peek の再生終了（正常終了）を確認する。
        bool lookOk = false;
        yield return WaitForChoreOneShot(ChoreOneShot.ChoreLook, ok => lookOk = ok);

        _detectionStartPending = false;
        SetDetection(false);
        _gazeInProgress = false;

        onResult?.Invoke(lookOk);
    }

    /// <summary>
    /// 片付けの滞在時間（秒）を MotherApproachWarning から取得する。
    /// 設定の唯一の保持元は MotherApproachWarning。接続できない場合は固定値へ戻さず、
    /// 警告して既定の0（＝到着後すぐ Chore_End）ではなく安全側の短い値ではなく、
    /// 呼び出し側が停止しないよう明確に 0 を返す（警告で気付けるようにする）。
    /// </summary>
    private float ResolveChoreStayDuration()
    {
        if (warningSystem == null)
        {
            Debug.LogWarning("[MotherChore] MotherApproachWarning が見つからないため、" +
                             "片付けの滞在時間を取得できません（MotherApproachWarning を Scene に配置してください）", this);
            return 0f;
        }

        return warningSystem.ChoreStayDuration;
    }

    /// <summary>
    /// 片付け開始時の怪しさ段階に応じた視線回数（Min/Max）を取得する。
    /// 設定元は MotherSuspicionSystem だけ（ここでは保持しない）。
    /// 接続できない場合は安全側（1〜1）へ倒して警告する。
    /// </summary>
    private void ResolveChoreLookCountRange(out int min, out int max)
    {
        if (parentDetection == null)
        {
            Debug.LogWarning("[MotherChore] MotherSuspicionSystem が見つからないため、片付け視線回数を1〜1にします", this);
            min = 1;
            max = 1;
            return;
        }

        parentDetection.ResolveChoreLookCountRange(out min, out max);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  サイクル終了・抑止の解除
    // ──────────────────────────────────────────────────────────────────────────

    private void FinishChoreCycle()
    {
        _mainRoutine = null;
        _gazeInProgress = false;
        _detectionStartPending = false;
        _choreArrived = false;
        _choreApproachSuspicionStarted = false;
        _choreEndRequested = false;
        // 未消費の終了要求・Trigger を Animator に残さない（再プレイ・中断で持ち越さない）。
        ResolveAnimationPlayer()?.ResetChoreParameters();
        _choreRouteFailed = false;
        _choreCompleted = false;
        IsChoreActive = false;
        SetDetection(false);

        // 念のため経路と怪しさ加算も閉じる（停止状態を残さない）。
        if (parentDetection != null)
        {
            parentDetection.DisableChoreSuspicion();
            parentDetection.SetChoreWalkingOverrideSuppressed(false);
            parentDetection.SetChoreMovementSuppressed(false);
        }

        if (showDebugLogs)
            Debug.Log("[MotherChore] 片付けサイクル終了 — 通常の親イベントを再開します");

        ReleaseChoreLocks();

        // 手動開始の要求・待ち状態をクリアして、6キーを再び受け付ける。
        //   ※ 退場・完了処理（IsChoreActive=false / ReleaseChoreLocks）より後に解除する。
        //     先に解除すると、退場処理中の連打で二重に開始できてしまう。
        _manualStartRequested = false;
        _manualStartPending = false;

        Debug.Log("[MotherChore] 6キー：再び手動開始を受け付けます（片付け完了・ロック解除済み）");
    }

    /// <summary>片付け用に張った抑止を解除し、スケジューラを再開する。</summary>
    private void ReleaseChoreLocks()
    {
        if (warningScheduler != null)
            warningScheduler.SetBlockedByChore(false);

        if (parentDetection != null)
            parentDetection.SetChoreOverride(false);
    }

    /// <summary>片付け開始時に通常の親イベントを抑止する。</summary>
    private void ApplyChoreLocks()
    {
        if (warningScheduler != null)
            warningScheduler.SetBlockedByChore(true);

        if (parentDetection != null)
            parentDetection.SetChoreOverride(true);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  MotherApproachController イベント
    // ──────────────────────────────────────────────────────────────────────────

    private void SubscribeApproachEvents()
    {
        if (_subscribed || approachController == null) return;
        approachController.onChoreArrived.AddListener(HandleChoreArrived);
        approachController.onChoreCompleted.AddListener(HandleChoreCompleted);
        _subscribed = true;
    }

    private void UnsubscribeApproachEvents()
    {
        if (!_subscribed || approachController == null) return;
        approachController.onChoreArrived.RemoveListener(HandleChoreArrived);
        approachController.onChoreCompleted.RemoveListener(HandleChoreCompleted);
        _subscribed = false;
    }

    /// <summary>chorePoint 到着・向き合わせ完了（演技開始の合図）。</summary>
    private void HandleChoreArrived()
    {
        if (!IsChoreActive) return;
        _choreArrived = true;
        if (showDebugLogs)
            Debug.Log("[MotherChore] chorePoint 到着・向き合わせ完了（onChoreArrived）");
    }

    /// <summary>退場完了（onChoreCompleted）。片付けイベント完了。</summary>
    private void HandleChoreCompleted()
    {
        if (!IsChoreActive) return;
        _choreCompleted = true;
        if (showDebugLogs)
            Debug.Log("[MotherChore] 戻り先へ到着（onChoreCompleted）");
        FinishChoreCycle();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  アニメーション
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 片付けの1回再生の用途（ステート名は MotherAnimationPlayer が保持する）。
    /// ここでは用途だけを指定し、ステート名は渡さない。
    /// </summary>
    private enum ChoreOneShot
    {
        ChoreLook,  // こちらを見る（Chore_Peek）
        ChoreEnd,   // 片付け終わって立つ（Chore_End）
        DoorOpen,   // ドアを開ける（Door_Open）
    }

    /// <summary>
    /// 用途別に1回再生の完了を待つ（ステート名は受け取らない）。
    ///  ・成功 … ステートへ到達して最後まで再生された
    ///  ・失敗 … 未開始・別ステートへ退出・タイムアウト
    ///  ・MotherAnimationPlayer 未設定時も「失敗(false)」として扱う
    ///    （呼び出し側が正常終了と混同しないようにする）。
    /// 判定の中身は MotherAnimationPlayer の WaitForChorePeek / WaitForChoreEnd / WaitForDoorOpen が持つ。
    /// </summary>
    private IEnumerator WaitForChoreOneShot(ChoreOneShot oneShot, System.Action<bool> onResult)
    {
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        if (player == null)
        {
            Debug.LogWarning("[MotherChore] MotherAnimationPlayer が未設定のため再生完了を判定できません（失敗扱い）", this);
            yield return new WaitForSeconds(1f);
            onResult?.Invoke(false);
            yield break;
        }

        switch (oneShot)
        {
            case ChoreOneShot.ChoreLook: yield return player.WaitForChorePeek(onResult); break;
            case ChoreOneShot.ChoreEnd:  yield return player.WaitForChoreEnd(onResult);  break;
            case ChoreOneShot.DoorOpen:  yield return player.WaitForDoorOpen(onResult);  break;
        }
    }

    /// <summary>
    /// アニメーション操作の窓口（MotherAnimationPlayer）を解決する。
    /// 明示参照が最優先。未設定ならシーンから自動検索する
    /// （MotherApproachController の AnimationPlayer アクセサは廃止したため依存しない）。
    /// </summary>
    private MotherAnimationPlayer ResolveAnimationPlayer()
    {
        if (animationPlayer != null) return animationPlayer;

        animationPlayer = Object.FindFirstObjectByType<MotherAnimationPlayer>();
        return animationPlayer;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  片付けのドア開閉（Door_Open 再生とドア本体回転の連携）
    //   ・MotherApproachController の片付けルートから呼ばれる。
    //   ・Door_Open 中は歩行による位置移動と歩きアニメーションの上書きを止め、
    //     モデルの再生とドア回転が両方終わるまで待ってから通過させる。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 片付けのドア開け：Door_Open へ遷移要求し、ドア本体を指す角度まで開いて完了を待つ。
    ///  ・行き（doorPoint）は openAngle（既存の openAngle に対応）まで開く。
    ///  ・帰り（choreReturnPoint_1）は DoorState.Full（既存の fullopen）まで開く。
    /// 歩行の位置移動は呼び出し側（MotherApproachController）が止めている。
    ///
    /// isApproach=true（行き）のときだけ、完了後に「行きの怪しさ加算」を開始する。
    /// 再生が未開始・失敗・タイムアウトの場合は加算せず、移動許可もしない（success=false で返す）。
    /// </summary>
    public IEnumerator ChoreDoorOpenRoutine(DoorController.DoorState targetState, bool isApproach)
    {
        // 歩きアニメーションの上書きと、歩行による位置移動を止める
        // （Door_Open の再生を守り、開け終わる前に通り抜けないようにする）。
        SetWalkingOverrideSuppressed(true);
        SetMovementSuppressed(true);

        // 未消費の終了要求をクリアしてから Door_Open を要求する
        // （前回サイクルの Chore_ExitRequested が残っていると Chore へ行けないため）。
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        player?.ResetChoreParameters();

        player?.RequestDoorOpen();

        // ── モデルの Door_Open 再生完了を待つ（未開始・失敗を正常終了と混同しない）──
        bool doorOpenAnimationOk = false;
        yield return WaitForChoreOneShot(ChoreOneShot.DoorOpen, ok => doorOpenAnimationOk = ok);

        if (!doorOpenAnimationOk)
        {
            Debug.LogWarning("[MotherChore] Door_Open の再生完了を確認できなかったため、" +
                             "移動許可と怪しさ加算へ進みません（失敗）", this);
            SetWalkingOverrideSuppressed(false);
            SetMovementSuppressed(false);

            // 失敗を呼び出し元へ伝える（MotherApproachController が経路を中断し、
            // FinishChoreRouteAborted → 通常イベント停止・移動抑止を解除する）。
            _choreRouteFailed = true;
            yield break;
        }

        // ── ドア本体の全開完了を待つ ──
        if (doorController != null)
            yield return doorController.WaitForDoorState(targetState, doorOpenWaitTimeout);
        else
            Debug.LogWarning("[MotherChore] doorController が未設定のため、ドア本体を開けられません", this);

        // 開け終わったので抑止を解除し、歩行（位置移動・Walk）を再開できるようにする。
        // Door_Open → Idle の遷移（Exit Time）が働いた後、Walk=true で Idle → Walk に乗る。
        SetWalkingOverrideSuppressed(false);
        SetMovementSuppressed(false);
        RestoreWalkingAnimation();

        // ── 行き限定：怪しさ加算の開始（Door_Open とドア全開の完了後）──
        //    帰り（isApproach=false）では呼ばない。
        if (isApproach)
            NotifyChoreApproachSuspicionStarted();

        if (showDebugLogs)
            Debug.Log($"[MotherChore] Door_Open 完了（目標={targetState}, isApproach={isApproach}）— 通過を許可します");
    }

    /// <summary>
    /// 片付けの行き怪しさ加算の開始を1回だけ通知する（Door_Open とドア全開の完了後に呼ばれる）。
    ///  ・開始待ち／廊下移動／Door_Open 中は呼ばれない（＝加算しない）。
    ///  ・中断・再プレイで状態が残らないよう FinishChoreCycle / AbortChore / ResetChoreState で解除する。
    /// </summary>
    public void NotifyChoreApproachSuspicionStarted()
    {
        if (!IsChoreActive) return;
        if (_choreApproachSuspicionStarted) return;   // 二重起動を防ぐ

        _choreApproachSuspicionStarted = true;

        if (parentDetection != null)
        {
            parentDetection.EnableChoreSuspicion();
            Debug.Log("[MotherChore] 行きの怪しさ加算を開始しました（Door_Open 完了後）");
        }
    }

    /// <summary>
    /// ドアを閉める（片付けの帰り、HallwayPoint_3 到着時）。
    /// モデルのアニメーションは再生せず、ドア本体だけを閉じる。
    /// </summary>
    public IEnumerator ChoreDoorCloseRoutine()
    {
        if (doorController == null)
        {
            Debug.LogWarning("[MotherChore] doorController が未設定のため、ドアを閉められません", this);
            yield break;
        }

        if (showDebugLogs)
            Debug.Log("[MotherChore] ドアを閉めます（HallwayPoint_3 到着）");

        yield return doorController.WaitForDoorState(DoorController.DoorState.Closed, doorCloseWaitTimeout);
    }

    /// <summary>
    /// 歩きアニメーションの上書き抑止を切り替える。
    /// Door_Open 中は MotherSuspicionSystem からの Walk 書き換えを止め、
    /// 通常イベントのドア操作へ片付けの再生が干渉しないようにする。
    /// </summary>
    private void SetWalkingOverrideSuppressed(bool suppressed)
    {
        if (parentDetection != null)
            parentDetection.SetChoreWalkingOverrideSuppressed(suppressed);
    }

    /// <summary>
    /// 片付けのドア開け中、歩行による位置移動を止めるかどうかを設定する。
    /// 経路側（MotherApproachController）の移動ループがこれを見て位置更新を止める。
    /// </summary>
    private void SetMovementSuppressed(bool suppressed)
    {
        if (parentDetection != null)
            parentDetection.SetChoreMovementSuppressed(suppressed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  発見判定（アニメーションの見た目とは独立に管理する）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>発見判定を有効／無効にする（アニメーションの見た目とは独立に管理する）。</summary>
    private void SetDetection(bool turnOn)
    {
        if (parentDetection == null) return;
        if (_detectionActive == turnOn) return;

        _detectionActive = turnOn;
        parentDetection.SetChoreLooking(turnOn);

        if (showDebugLogs)
            Debug.Log($"[MotherChore] 発見判定 {(turnOn ? "開始" : "終了")}（アニメ再生とは別管理）");
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  参照解決
    // ──────────────────────────────────────────────────────────────────────────

    private void ResolveReferences()
    {
        if (warningSystem == null)
            warningSystem = Object.FindFirstObjectByType<MotherApproachWarning>();
        if (approachController == null)
            approachController = Object.FindFirstObjectByType<MotherApproachController>();
        if (parentDetection == null)
            parentDetection = Object.FindFirstObjectByType<MotherSuspicionSystem>();
        if (warningScheduler == null)
            warningScheduler = Object.FindFirstObjectByType<ParentWarningScheduler>();

        if (doorController == null)
            doorController = Object.FindFirstObjectByType<DoorController>();
    }
}