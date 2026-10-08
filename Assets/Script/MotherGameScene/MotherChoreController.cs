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

    [Tooltip("親機のAnimator。未設定の場合は approachController.MotherAnimator を使用する。")] [SerializeField]
    private Animator motherAnimator;

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

    // ── アニメーション名 ──────────────────────────────────────────────────────
    //   指定の3構成。Chore は片付け開始時から直接再生して片付け中はループする
    //   （独立した片付け開始アニメーションは再生しない）。
    [Header("アニメーションステート名（Animator の State 名）")] [Tooltip("片付け中（ループ再生）。開始時から直接再生する。")] [SerializeField]
    private string choreLoopStateName = "Chore";

    [Tooltip("こちらを見る（1回再生）。終了後は Chore へ戻る。")] [SerializeField]
    private string choreLookStateName = "Chore_Peek";

    [Tooltip("片付け終わって立つ（1回再生）。終了後に通常の歩きへ切り替える。")] [SerializeField]
    private string choreEndStateName = "Chore_End";

    [Tooltip("ドアを開ける動作（1回再生）。片付けのドア開閉で使用する（Door_Open 専用）。")] [SerializeField]
    private string doorOpenStateName = "Door_Open";

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

    /// <summary>視線アニメーション（3）を再生中か。</summary>
    public bool IsLooking => _lookRoutine != null;

    // ── 内部状態 ──────────────────────────────────────────────────────────────
    private Coroutine _mainRoutine; // 片付け本体（開始待ち＋行き〜帰り）
    private Coroutine _lookRoutine; // 視線アニメ（3）と発見判定の期間管理
    private bool _choreStartRequested; // このプレイで開始要求を出したか
    private int _choreStartCount; // このプレイで開始した回数
    private bool _manualStartRequested; // 6キーによる手動開始の要求を保持しているか（開始待ちは1件だけ）
    private bool _manualStartPending; // 開始待ちコルーチンが手動要求から動いているか
    private bool _subscribed; // MotherApproachController のイベント購読中か
    private bool _pendingExit; // 片付け時間満了済み（3 終了後に 4 へ進むためのフラグ）
    private bool _inChoreLoop; // アニメ2のループ中か（視線抽選の対象期間のみ true）
    private float _loopElapsed; // アニメ2の経過時間
    private bool _detectionActive; // 発見判定（isMotherLookingNow）を有効にしているか
    private float _detectionEndTime; // 発見判定を解除する時刻（Time.time 基準）
    private bool _detectionStartPending; // 発見判定の開始待ちか
    private float _detectionStartTime; // 発見判定を開始する時刻（Time.time 基準）
    private bool _badItemNotifiedDuringLook; // 視線中に通知が来たか（再開始・予約しないことの確認用）
    private bool _choreArrived; // chorePoint 到着・向き合わせ完了（onChoreArrived）を受けたか
    private bool _choreCompleted; // 退場完了（onChoreCompleted）を受けたか
    private bool _choreApproachSuspicionStarted; // 行きの怪しさ加算を開始済みか（1回だけ発行）

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

        return active == "GameScene";
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
    /// 悪いアイテム取得の通知。片付け中ループ（2）の間だけ視線抽選を行う。
    /// 1・3・4・入退室の歩き中、および視線（3）再生中は無視する（再開始・予約をしない）。
    /// </summary>
    public void NotifyBadItemCollected()
    {
        if (!IsChoreActive) return;

        // 視線アニメ再生中の通知：再開始も予約も行わない。
        if (IsLooking)
        {
            _badItemNotifiedDuringLook = true;
            if (showDebugLogs)
                Debug.Log("[MotherChore] 視線アニメ再生中のため通知は無視（再開始・予約なし）");
            return;
        }

        // アニメ2のループ中以外（1・4・入退室の歩き）は抽選しない。
        if (!_inChoreLoop) return;

        // 視線発生率は MotherApproachWarning の設定を参照する（重複保持しない）。
        float probability = ResolveChoreLookProbability();
        if (Random.value > probability)
        {
            if (showDebugLogs)
                Debug.Log($"[MotherChore] 視線抽選：不発（{probability:P0}）");
            return;
        }

        if (showDebugLogs)
            Debug.Log("[MotherChore] 視線抽選：的中 — こっちを見るアニメへ");

        _lookRoutine = StartCoroutine(LookRoutine());
    }

    /// <summary>
    /// 片付けを強制中断する（ゲームオーバー・シーン遷移・リセット用）。
    /// 既存のゲームオーバー処理には何も介入しない（片付けの状態だけを戻す）。
    /// </summary>
    public void AbortChore(string reason)
    {
        bool wasActive = IsChoreActive;

        if (_lookRoutine != null)
        {
            StopCoroutine(_lookRoutine);
            _lookRoutine = null;
        }

        if (_mainRoutine != null)
        {
            StopCoroutine(_mainRoutine);
            _mainRoutine = null;
        }

        _inChoreLoop = false;
        _pendingExit = false;
        _detectionStartPending = false;
        _badItemNotifiedDuringLook = false;
        _choreArrived = false;
        _choreApproachSuspicionStarted = false;
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
        if (_lookRoutine != null)
        {
            StopCoroutine(_lookRoutine);
            _lookRoutine = null;
        }

        if (_mainRoutine != null)
        {
            StopCoroutine(_mainRoutine);
            _mainRoutine = null;
        }

        _choreStartRequested = false;
        _choreStartCount = 0;
        _manualStartRequested = false;
        _manualStartPending = false;
        _inChoreLoop = false;
        _pendingExit = false;
        _loopElapsed = 0f;
        _badItemNotifiedDuringLook = false;
        _detectionStartPending = false;
        _choreArrived = false;
        _choreApproachSuspicionStarted = false;
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

        if (_mainRoutine != null) StopCoroutine(_mainRoutine);
        _mainRoutine = StartCoroutine(ChoreRoutine());
    }

    private IEnumerator ChoreRoutine()
    {
        // ── サイクル開始：通常の親イベントを抑止する ─────────────────────────
        IsChoreActive = true;
        _pendingExit = false;
        _inChoreLoop = false;
        _loopElapsed = 0f;
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
    /// chorePoint での片付け演技：Chore（ループ）→ 必要時 Chore_Peek → Chore_End。
    ///  ・独立した片付け開始アニメーションは再生しない（Chore を直接再生する）。
    ///  ・Chore のループ中だけ視線抽選を受け付ける（_inChoreLoop が抽選の受付ゲート）。
    ///  ・視線中に片付け時間が終了した場合は、Chore_Peek 終了後に Chore_End へ進む。
    /// </summary>
    private IEnumerator ChorePerformPhase()
    {
        // ── 片付け中：Chore（ループ再生）を直接開始する ──────────────────────
        PlayChoreState(choreLoopStateName, loop: true);
        _inChoreLoop = true;
        _loopElapsed = 0f;

        // 片付けの滞在時間は MotherApproachWarning の設定を参照する（重複保持しない）。
        float stayDuration = ResolveChoreStayDuration();

        while (_loopElapsed < stayDuration && IsChoreActive)
        {
            _loopElapsed += Time.deltaTime;

            // 片付け時間満了：Chore_Peek の再生中なら「終了後に Chore_End へ」を意味する
            // フラグだけ立て、Chore_Peek の終了を待ってから Chore_End へ進む。
            if (_loopElapsed >= stayDuration && !IsLooking)
            {
                _pendingExit = true;
                break;
            }

            yield return null;
        }

        _inChoreLoop = false; // ここで抽選の受付を閉じる（歩き・Chore_End では抽選しない）

        if (!IsChoreActive) yield break;

        // ── Chore_Peek（視線）が進行中の場合は、その終了を待ってから Chore_End へ ──
        //    Chore_Peek 終了後は Chore へ戻る実装なので、ここで Chore_End へ切り替える。
        while (IsLooking && IsChoreActive)
            yield return null;

        if (!IsChoreActive) yield break;

        // ── Chore_End（片付け終わって立つ・1回再生）──────────────────────────
        PlayChoreState(choreEndStateName);
        yield return WaitForOneShotState(choreEndStateName);

        // Chore_End 終了後、通常の歩きへ確実に切り替える。
        RestoreWalkingAnimation();
    }

    /// <summary>
    /// 片付け／ドア開けの再生後、既存の歩きアニメーションへ確実に戻す。
    /// 実処理は MotherAnimationPlayer が持つ（Animator を直接操作しない）。
    ///
    /// 【重要】Animator Controller には Chore_End / Door_Open から出る遷移が存在しないため、
    /// Walk を true にしただけでは現在のステートに留まり、歩きへ戻らない。
    /// プレイヤー側で Idle を経由させてから Walk を立てる。
    /// </summary>
    private void RestoreWalkingAnimation()
    {
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        if (player == null)
        {
            Debug.LogWarning("[MotherChore] MotherAnimationPlayer が未設定のため歩きへ戻せません", this);
            return;
        }

        player.RestoreWalking();

        if (showDebugLogs)
            Debug.Log("[MotherChore] 片付け／ドア開け終了 — プレイヤー経由で既存の歩きアニメーションへ復帰");
    }

    /// <summary>
    /// こっちを見る（Chore_Peek）を1回再生する。終了後は Chore へ戻る。
    /// 発見判定は見た目と別に、開始遅延・継続時間で管理する。
    /// 視線中に片付け時間が終了した場合は、Chore_Peek 終了後に Chore_End へ進む
    /// （呼び出し元 ChorePerformPhase が _pendingExit を見て判断する）。
    /// </summary>
    private IEnumerator LookRoutine()
    {
        PlayChoreState(choreLookStateName);

        // 発見判定：開始遅延後に有効化し、継続時間経過で解除する（見た目は再生し続ける）。
        _detectionStartTime = Time.time + Mathf.Max(0f, lookDetectionStartDelay);
        _detectionEndTime = _detectionStartTime + Mathf.Max(0f, lookDetectionDuration);
        _detectionStartPending = true;

        // Chore_Peek の再生終了まで待つ（1回再生ステート）。
        yield return WaitForOneShotState(choreLookStateName);

        _detectionStartPending = false;
        SetDetection(false);

        // 視線中に通知が来ていたら、その旨をログに残す（再開始・予約はしていない）。
        if (_badItemNotifiedDuringLook && showDebugLogs)
            Debug.Log("[MotherChore] 視線中に通知を受信済み — 再開始・予約は行っていません");

        _badItemNotifiedDuringLook = false;
        _lookRoutine = null;

        // 終了後は Chore へ戻る。
        //  片付け時間が満了している場合（_pendingExit）も、いったん Chore へ戻してから
        //  ChorePerformPhase の待機ループが抜けて Chore_End へ進む（見た目の連続性を保つ）。
        PlayChoreState(choreLoopStateName, loop: true);

        if (showDebugLogs)
            Debug.Log($"[MotherChore] Chore_Peek 終了 — Chore へ戻ります（pendingExit={_pendingExit}）");
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
    /// 悪いアイテム取得時の視線発生率（0〜1）を MotherApproachWarning から取得する。
    /// 設定の唯一の保持元は MotherApproachWarning。接続できない場合は警告して 0 を返す
    /// （＝視線抽選を発生させない安全側）。
    /// </summary>
    private float ResolveChoreLookProbability()
    {
        if (warningSystem == null)
        {
            Debug.LogWarning("[MotherChore] MotherApproachWarning が見つからないため、" +
                             "視線発生率を取得できません（MotherApproachWarning を Scene に配置してください）", this);
            return 0f;
        }

        return warningSystem.ChoreLookProbability;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  サイクル終了・抑止の解除
    // ──────────────────────────────────────────────────────────────────────────

    private void FinishChoreCycle()
    {
        if (_lookRoutine != null)
        {
            StopCoroutine(_lookRoutine);
            _lookRoutine = null;
        }

        _mainRoutine = null;
        _inChoreLoop = false;
        _pendingExit = false;
        _detectionStartPending = false;
        _choreArrived = false;
        _choreApproachSuspicionStarted = false;
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
    /// 片付け用のステートへ切り替える。
    /// Animator への書き込みは MotherAnimationPlayer に集約する（Walk 解除もプレイヤーが行う）。
    /// </summary>
    private void PlayChoreState(string stateName, bool loop = false)
    {
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        if (player == null)
        {
            Debug.LogWarning("[MotherChore] MotherAnimationPlayer が未設定のため再生できません", this);
            return;
        }

        // ステート名はインスペクタで差し替え可能なため、名前で振り分けてプレイヤーのAPIを呼ぶ。
        if (stateName == doorOpenStateName) player.PlayDoorOpen();
        else if (stateName == choreLookStateName) player.PlayChorePeek();
        else if (stateName == choreEndStateName) player.PlayChoreEnd();
        else player.PlayChoreLoop();

        if (showDebugLogs)
            Debug.Log($"[MotherChore] アニメ再生要求: {stateName} (loop={loop})");
    }

    /// <summary>
    /// 1回再生ステートの終了を待つ（プレイヤーへ委譲）。
    /// ステート未登録・到達不能・タイムアウトはプレイヤー側が警告して先へ進める。
    /// </summary>
    private IEnumerator WaitForOneShotState(string stateName)
    {
        MotherAnimationPlayer player = ResolveAnimationPlayer();
        if (player == null)
        {
            yield return new WaitForSeconds(1f);
            yield break;
        }

        yield return player.WaitForOneShot(stateName);
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
    /// 片付けのドア開け：Door_Open を1回再生し、ドア本体を指す角度まで開いて完了を待つ。
    ///  ・行き（doorPoint）は openAngle（既存の openAngle に対応）まで開く。
    ///  ・帰り（choreReturnPoint_1）は DoorState.Full（既存の fullopen）まで開く。
    /// 歩行の位置移動は呼び出し側（MotherApproachController）が止めている。
    /// </summary>
    public IEnumerator ChoreDoorOpenRoutine(DoorController.DoorState targetState)
    {
        // 歩きアニメーションの上書きと、歩行による位置移動を止める
        // （Door_Open の再生を守り、開け終わる前に通り抜けないようにする）。
        SetWalkingOverrideSuppressed(true);
        SetMovementSuppressed(true);

        PlayChoreState(doorOpenStateName);

        // モデルの Door_Open 再生とドア本体の回転を両方待つ。
        yield return WaitForOneShotState(doorOpenStateName);

        if (doorController != null)
            yield return doorController.WaitForDoorState(targetState, doorOpenWaitTimeout);
        else
            Debug.LogWarning("[MotherChore] doorController が未設定のため、ドア本体を開けられません", this);

        // 開け終わったので抑止を解除し、歩行（位置移動・Walk）を再開できるようにする。
        // その後 Idle 経由で歩きアニメーションへ戻す
        // （Door_Open から出る遷移が無いため、Play("Idle") + Walk=true が必要）。
        SetWalkingOverrideSuppressed(false);
        SetMovementSuppressed(false);
        RestoreWalkingAnimation();

        // ── 行きの怪しさ加算の開始（Door_Open とドア全開の完了後）──
        //    1回だけ発行する。加算失敗・タイムアウト・中断の経路ではここへ到達しないため
        //    加算は始まらない。
        NotifyChoreApproachSuspicionStarted();

        if (showDebugLogs)
            Debug.Log($"[MotherChore] Door_Open 完了（目標={targetState}）— 通過を許可します");
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
        if (motherAnimator == null && approachController != null)
            motherAnimator = approachController.MotherAnimator;

        if (doorController == null)
            doorController = Object.FindFirstObjectByType<DoorController>();
    }
}