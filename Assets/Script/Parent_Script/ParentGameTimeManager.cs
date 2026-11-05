using System.Collections;
using UnityEngine;

/// <summary>
/// ParentGameTimeManager：親機側のゲーム全体タイムリミットと時計表示を管理する。
///
/// 目的（P09）：
///  ・ゲーム内時刻「夜9:00（21:00）スタート → 深夜2:00（26:00）到達でクリア」の進行を作る。
///  ・ゲーム内時刻は時計モデル（Room/ClockFaceDisplay の ClockFacePresenter）へ表示する。
///    GetClockText() の戻り値を表示側が取得する（ここでは画面へ表示しない）。
///  ・制限時間（プレイ実時間）に到達したら TIME_UP として結果画面（ParentGameClear）へ遷移する。
///
/// 時間の対応：
///  ・プレイ実時間 limitSeconds 秒で、ゲーム内は gameMinutesTotal 分（既定 300分 = 5時間）進む。
///  ・開始時刻 startHour（既定 21 = 21:00）から単調増加し、24時間で折り返して表示する。
///    例）21:00 → 26:00 は 02:00 として表示される（26 % 24 = 2）。
///
/// 責務の境界（ここでは行わない）：
///  ・子機のタイマー（GameManager.time）は変更しない（親機側だけで独立に時間を持つ）。
///  ・親機から子機への TIME_UP 送信は行わない（既存の UDP 仕様を変更しない）。
///  ・捕獲（ゲームオーバー）が確定している場合は遷移しない（捕獲優先。既存の親機ルールに合わせる）。
/// </summary>
public class ParentGameTimeManager : MonoBehaviour
{
    // ── 時間設定 ──────────────────────────────────────────────────────────────
    [Header("時間設定")]
    [Tooltip("プレイ実時間の制限（秒）。この秒数でゲーム内の gameMinutesTotal 分が経過する。")]
    [SerializeField, Min(1f)] private float limitSeconds = 300f;

    [Tooltip("ゲーム内の開始時刻（時）。21 = 夜9時。")]
    [SerializeField, Range(0, 23)] private int startHour = 21;

    [Tooltip("ゲーム内で進む総時間（分）。300分 = 5時間（21:00 → 26:00 = 02:00）。")]
    [SerializeField, Min(1f)] private float gameMinutesTotal = 300f;

    // ── 時計表示 ──────────────────────────────────────────────────────────────
    [Header("時計表示")]
    [Tooltip("時刻の表示形式。{0}=時(2桁), {1}=分(2桁)。ClockFacePresenter が GetClockText() の戻り値を表示する。")]
    [SerializeField] private string clockFormat = "{0:00}:{1:00}";

    // ── 終了設定 ──────────────────────────────────────────────────────────────
    [Header("タイムリミット")]
    [Tooltip("制限時間へ到達したら結果画面（TIME_UP）へ遷移する。")]
    [SerializeField] private bool enableTimeUp = true;

    [Tooltip("TIME_UP で遷移するシーン名。既存の親機クリア画面。")]
    [SerializeField] private string timeUpSceneName = "ParentGameClear";

    [Tooltip("遷移前にシーン名を SceneNameResolver で解決する（既存のタイトル復帰と同じ解決を使う）。")]
    [SerializeField] private bool resolveSceneName = false;

    // ── 開始前ディレイ ────────────────────────────────────────────────────────
    [Header("開始前ディレイ")]
    [Tooltip("本編開始前の待機（秒）。この秒数だけ 21:00 のまま止め、経過後にタイマーを開始する。\n" +
             "0 以下なら Start 直後に即時開始する。")]
    [SerializeField, Min(0f)] private float startDelaySeconds = 0f;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs;

    // ── 状態 ──────────────────────────────────────────────────────────────────
    /// <summary>ゲーム開始からの経過（プレイ実時間・秒）。</summary>
    private float _elapsed;

    /// <summary>TIME_UP を一度だけ実行するためのフラグ。</summary>
    private bool _timeUpFired;

    /// <summary>タイマーが進行中か（本編開始後 true）。</summary>
    private bool _timerRunning;

    /// <summary>結果確定（捕獲／クリア）により完全停止したか（以降は再開しない）。</summary>
    private bool _timerStopped;

    /// <summary>MotherSuspicionSystem の参照キャッシュ（毎フレームの Find を避ける）。</summary>
    private MotherSuspicionSystem _suspicionSystem;

    // ── 公開（読み取り専用） ──────────────────────────────────────────────────
    /// <summary>親機の時間管理をほかの機能から参照するためのインスタンス。</summary>
    public static ParentGameTimeManager Instance { get; private set; }

    /// <summary>ゲーム進行率（0.0〜1.0）。経過実時間 ÷ 制限時間。</summary>
    public float ProgressRate => Mathf.Clamp01(_elapsed / Mathf.Max(1f, limitSeconds));

    /// <summary>制限時間までの残り時間（秒）。</summary>
    public float RemainingSeconds => Mathf.Max(0f, LimitSeconds - _elapsed);

    /// <summary>TIME_UP を実行済みか。</summary>
    public bool IsTimeUp => _timeUpFired;

    /// <summary>タイマーが進行中か（読み取り専用）。開始前・結果確定後は false。</summary>
    public bool IsRunning => _timerRunning;

    /// <summary>結果確定（捕獲／クリア）により完全停止したか（読み取り専用）。</summary>
    public bool IsStopped => _timerStopped;

    /// <summary>
    /// 本プレイが終了したか（新規の親イベントを開始してはいけない状態か）。
    ///  ・時間切れ：TIME_UP を実行済み（_timeUpFired）
    ///  ・開始済み計測の制限到達：進行中（_timerRunning）で _elapsed が制限時間へ到達
    ///  ・明示的なタイマー停止：StopTimer() 済み（_timerStopped）
    ///  ・結果確定：現在プレイの結果処理済み／現在プレイIDと一致する捕獲確定／シーン内捕獲
    ///  ・タイマー未開始・遅延開始（_timerRunning=false かつ _elapsed<Limit）は終了としない。
    ///  ・結果確定は参照時に即時評価する（IsStopped の翌Update反映を待たない）。
    /// </summary>
    public bool IsPlayFinished
    {
        get
        {
            // 時間切れ（TIME_UP 実行済み）
            if (_timeUpFired) return true;

            // 明示的なタイマー停止（StopTimer 済み）
            if (_timerStopped) return true;

            // 開始済み計測が制限時間へ到達（進行中で到達した場合）
            if (_timerRunning && _elapsed >= LimitSeconds) return true;

            // 必須参照の不足は「終了」とは扱わない（1回だけ警告する）。
            WarnIfMissingReference();

            // 結果確定（捕獲／クリア）
            return IsResultConfirmed();
        }
    }

    /// <summary>必須参照（MotherSuspicionSystem）が不足したことを1回だけ警告済みか。</summary>
    private bool _missingRefWarned;

    /// <summary>
    /// 必須参照（MotherSuspicionSystem）が不足しているかを確認し、1回だけ警告する。
    ///  ・Host（UDP）も無い場合は「単体デバッグ相当」として警告のみ（判定は継続）。
    ///  ・Host があるのに参照が無い場合は通常シーンの設定不備として警告する。
    ///  ・参照不足それ自体を「終了」とは扱わない。
    /// </summary>
    private void WarnIfMissingReference()
    {
        if (_missingRefWarned) return;
        if (_suspicionSystem != null) return;

        _missingRefWarned = true;
        bool hasHost = ParentUdpRuntimeHost.Running != null;
        Debug.LogWarning(hasHost
            ? "[ParentGameTimeManager] 必須参照不足：MotherSuspicionSystem が見つかりません（通常シーンの設定不備の可能性）。捕獲状態の判定は Host 経由のみで継続します。"
            : "[ParentGameTimeManager] 必須参照不足：MotherSuspicionSystem も Host もありません（単体デバッグ相当）。捕獲状態は判定できません。", this);
    }

    // ── 自動開始の可否（Manager 不在でも呼べる共通の窓口） ─────────────────────

    /// <summary>判定不能による拒否を1回だけ記録済みか（毎フレームのログを避ける）。</summary>
    private static bool _unavailableRejectLogged;

    /// <summary>
    /// 判定不能による拒否を1回だけログする（毎フレームの出力を避ける）。
    /// </summary>
    public static void LogUnavailableRejectOnce()
    {
        if (_unavailableRejectLogged) return;
        _unavailableRejectLogged = true;
        Debug.LogWarning("[ParentGameTimeManager] 自動開始を拒否：ParentGameTimeManager が無い通常ゲームシーンです" +
                         "（判定不能）。開始可否を判定できないため、自動の親イベントは開始しません。" +
                         "シーンに ParentGameTimeManager を配置してください。");
    }

    /// <summary>
    /// 自動で親イベントを開始してよいかの判定結果。
    ///  ・Allow              ：許可（プレイ継続中）
    ///  ・RejectPlayFinished ：終了による拒否（終了ラッチを立ててよい）
    ///  ・RejectUnavailable  ：判定不能による拒否（終了ラッチは立てない）
    ///  ・AllowSoloDebug     ：単体デバッグ互換の許可（規約）
    /// </summary>
    public enum AutoStartDecision
    {
        Allow = 0,
        RejectPlayFinished = 1,
        RejectUnavailable = 2,
        AllowSoloDebug = 3,
    }

    /// <summary>Manager 不在時にも参照する MotherSuspicionSystem の共有キャッシュ（Start で設定）。</summary>
    private static MotherSuspicionSystem _sharedSuspicionSystem;
    /// <summary>共有参照の解決を1回だけ試したか（毎フレームの Find を避ける）。</summary>
    private static bool _sharedSuspicionResolveAttempted;

    /// <summary>
    /// 自動で親イベントを開始してよいかを判定する（Manager 不在でも呼べる）。
    ///  ・Manager がある場合：終了事実（IsPlayFinished）だけを見る。
    ///  ・Manager が無い場合：
    ///      1) 既存の捕獲・結果情報（Host の Session／MotherSuspicionSystem）を確認する
    ///      2) 通常ゲームシーンなら「判定不能」として拒否する（Host 不在でも拒否）
    ///      3) 非ゲームプレイシーンなら「単体デバッグ互換」として許可する
    ///  ・規約：Host 不在かつ非ゲームプレイシーンを「単体デバッグ」とみなす
    ///    （既存 MotherChoreController.IsGameplayScene と同じフォールバック "ParentGameScene"）。
    ///  ・副作用なし（ログ・ラッチ・コルーチン起動を一切行わない）。
    /// </summary>
    public static AutoStartDecision EvaluateAutoStart()
    {
        var manager = Instance;
        if (manager != null)
            return manager.IsPlayFinished ? AutoStartDecision.RejectPlayFinished : AutoStartDecision.Allow;

        // Manager 不在：既存の捕獲・結果情報は無視しない。
        if (IsFinishedByExistingInfo()) return AutoStartDecision.RejectPlayFinished;

        // 通常ゲームシーンで Manager 不在は設定異常として拒否する（終了ラッチは立てない）。
        if (IsGameplaySceneNow()) return AutoStartDecision.RejectUnavailable;

        // 非ゲームプレイシーン：単体デバッグ互換。
        return AutoStartDecision.AllowSoloDebug;
    }

    /// <summary>Manager 不在時に、既存の捕獲・結果情報から終了と判断できるか。</summary>
    private static bool IsFinishedByExistingInfo()
    {
        var host = ParentUdpRuntimeHost.Running;
        if (host != null && host.Session != null)
        {
            if (IsCaptureConfirmedForCurrentPlay(host.Session)) return true;
            if (host.Session.ResultProcessed) return true;
        }

        var s = ResolveSharedSuspicion();
        return s != null && (s.isCaught || s.HasPermanentGameOver);
    }

    /// <summary>
    /// 共有の MotherSuspicionSystem 参照を解決する（毎フレームの Find を避けるため1回だけ試す）。
    ///  ・Manager がある場合は Start() が設定するため、ここは Manager 不在時のフォールバック。
    /// </summary>
    private static MotherSuspicionSystem ResolveSharedSuspicion()
    {
        if (_sharedSuspicionSystem != null) return _sharedSuspicionSystem;   // 破棄は Unity の null 判定で検知
        if (_sharedSuspicionResolveAttempted) return null;
        _sharedSuspicionResolveAttempted = true;
        _sharedSuspicionSystem = Object.FindFirstObjectByType<MotherSuspicionSystem>();
        return _sharedSuspicionSystem;
    }

    /// <summary>現在のシーンが通常のゲームプレイシーンか（既存のシーン識別を優先して使う）。</summary>
    private static bool IsGameplaySceneNow()
    {
        string active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

        var host = ParentUdpRuntimeHost.Running;
        if (host != null && host.Settings != null && !string.IsNullOrEmpty(host.Settings.GameplaySceneName))
            return ParentSceneBoundaryPolicy.IsGameplayScene(active, host.Settings.GameplaySceneName);

        // 既存規約：Host 設定が無い場合は名前比較へフォールバックする
        // （MotherChoreController.IsGameplayScene と同じ扱い）。
        return active == "ParentGameScene";
    }

    /// <summary>
    /// タイマーを開始する（本編開始時に呼ぶ）。
    ///  ・結果確定で停止済み（_timerStopped）の場合は再開しない（時刻を固定したまま）。
    ///  ・開始前ディレイ待ちの間に呼ばれても、ディレイ経過後に開始される（多重開始は無視）。
    /// </summary>
    public void StartTimer()
    {
        if (_timerStopped)
        {
            Debug.Log("[ParentGameTimeManager] 結果確定済みのため StartTimer を無視します（時刻を固定したまま）。", this);
            return;
        }

        if (_timerRunning) return;   // 既に進行中（多重開始を無視）

        _timerRunning = true;

        Debug.Log($"[ParentGameTimeManager] タイマー開始 — 表示={GetClockText()}", this);
    }

    /// <summary>
    /// タイマーを停止する（結果確定時に呼ぶ）。以降は _elapsed を加算せず時刻を固定する。
    ///  ・完全停止フラグ（_timerStopped）を立てるため、後から StartTimer を呼んでも再開しない。
    ///  ・停止中も時計表示は現在値のまま維持する（Update 側で表示更新を続ける）。
    /// </summary>
    public void StopTimer()
    {
        if (_timerStopped) return;   // 既に停止済み（冪等）

        _timerRunning = false;
        _timerStopped = true;

        Debug.Log($"[ParentGameTimeManager] タイマー停止 — 時刻を固定（表示={GetClockText()}）", this);
    }

    /// <summary>ゲーム開始からの経過（秒）。</summary>
    public float ElapsedSeconds => _elapsed;

    /// <summary>制限時間（秒）。</summary>
    public float LimitSeconds => Mathf.Max(1f, limitSeconds);

    /// <summary>
    /// 現在のゲーム内時刻を "HH:MM" で返す（24時間で折り返す）。
    ///  ・21:00 スタート、進行率 1.0 で 26:00（= 02:00）へ到達する。
    /// </summary>
    public string GetClockText()
    {
        float totalMinutes = startHour * 60f + ProgressRate * gameMinutesTotal;

        // 24時間で折り返す（26:00 → 02:00）。負値にならないよう剰余を正規化する。
        int totalMinutesInt = Mathf.FloorToInt(totalMinutes);
        int hour = ((totalMinutesInt / 60) % 24 + 24) % 24;
        int minute = ((totalMinutesInt % 60) + 60) % 60;

        return string.Format(clockFormat, hour, minute);
    }

    // ── Unityライフサイクル ───────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[ParentGameTimeManager] 重複するインスタンスを破棄します。", this);
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void Start()
    {
        // 再プレイ・シーン再読み込みで前回の状態を持ち越さない。
        _elapsed = 0f;
        _timeUpFired = false;
        _timerRunning = false;
        _timerStopped = false;

        // 開始前は 21:00 のまま（timer 開始まで _elapsed は進まない）。

        // 毎フレームの検索を避けるため、参照はここで1回だけ解決する。
        _suspicionSystem = Object.FindFirstObjectByType<MotherSuspicionSystem>();
        _missingRefWarned = false;

        // Manager 不在時に参照する共有キャッシュも埋めておく（Find を繰り返さない）。
        _sharedSuspicionSystem = _suspicionSystem;
        _sharedSuspicionResolveAttempted = true;

        if (showDebugLogs)
            Debug.Log($"[ParentGameTimeManager] 開始 | 制限={LimitSeconds:F1}s | 開始時刻={startHour:00}:00 | " +
                      $"ゲーム内総時間={gameMinutesTotal:F0}分 | 遷移先='{timeUpSceneName}' | " +
                      $"開始前ディレイ={startDelaySeconds:F2}s", this);

        // 本編開始：startDelaySeconds > 0 なら待ってから、0以下なら即時開始する。
        if (startDelaySeconds > 0f)
            StartCoroutine(StartTimerAfterDelay());
        else
            StartTimer();
    }

    /// <summary>開始前ディレイを待ってからタイマーを開始する（待機中は 21:00 のまま）。</summary>
    private IEnumerator StartTimerAfterDelay()
    {
        Debug.Log($"[ParentGameTimeManager] 本編開始前：{startDelaySeconds:F2}s 待機します（表示={GetClockText()}）", this);

        yield return new WaitForSeconds(startDelaySeconds);

        StartTimer();
    }

    private void Update()
    {
        if (_timeUpFired) return;

        // ── 結果確定（捕獲／クリア）の検知：確定したら即座に停止して時刻を固定する ──
        if (!_timerStopped && IsResultConfirmed())
        {
            StopTimer();
            return;
        }

        // 進行中でなければ加算しない（開始前・結果確定後は時刻が固定される）。
        if (!_timerRunning)
        {
            return;
        }

        // プレイ実時間で進める（既存の親機タイマーと同じ Time.deltaTime）。
        _elapsed += Time.deltaTime;

        if (!enableTimeUp) return;

        if (_elapsed >= LimitSeconds)
        {
            // 1回だけ実行する（毎フレームの多重遷移を防ぐ）。
            _timeUpFired = true;
            _elapsed = LimitSeconds;

            Debug.Log($"[ParentGameTimeManager] TIME UP — 制限時間 {LimitSeconds:F1}s 到達（表示={GetClockText()}）", this);

            if (IsCaptureConfirmed())
            {
                // 捕獲（ゲームオーバー）が確定している場合は TIME_UP で遷移しない（捕獲優先）。
                Debug.Log("[ParentGameTimeManager] 捕獲が確定しているため、TIME_UP では遷移しません（捕獲優先）。", this);
                return;
            }

            TransitionToTimeUp();
        }
    }

    // ── 内部処理 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 結果（捕獲／クリア）が確定しているかを判定する。
    ///  ・捕獲確定：ParentPlaySession の「現在プレイID」と一致する CaptureConfirmedPlayId がある場合のみ。
    ///    非空判定だけでは使わない（前プレイのIDが残っていても終了と誤認しないため）。
    ///  ・終了確定：ParentPlaySession.ResultProcessed が true（捕獲・クリアのどちらでも立つ）。
    ///  ・MotherSuspicionSystem 側の isCaught / HasPermanentGameOver も併せて見る。
    ///  ・Host が無い（ソロ・デバッグ）場合は MotherSuspicionSystem のみで判定する。
    /// </summary>
    private bool IsResultConfirmed()
    {
        var host = ParentUdpRuntimeHost.Running;
        if (host != null && host.Session != null)
        {
            // 捕獲確定：現在プレイIDと一致する場合だけ「このプレイの捕獲確定」と見なす。
            if (IsCaptureConfirmedForCurrentPlay(host.Session)) return true;

            // 終了確定（クリア＝TIME_UP 受信、または捕獲の結果処理済み）
            if (host.Session.ResultProcessed) return true;
        }

        return _suspicionSystem != null &&
               (_suspicionSystem.isCaught || _suspicionSystem.HasPermanentGameOver);
    }

    /// <summary>
    /// 現在プレイの捕獲が確定しているか。
    ///  ・現在 PlaySessionId が非空で、CaptureConfirmedPlayId と一致する場合のみ true。
    ///  ・現在 PlaySessionId が空（プレイ未開始）の場合は false（古いIDで終了と誤認しない）。
    /// </summary>
    private static bool IsCaptureConfirmedForCurrentPlay(ParentPlaySession session)
    {
        if (session == null) return false;
        if (string.IsNullOrEmpty(session.PlaySessionId)) return false;
        return session.PlaySessionId == session.CaptureConfirmedPlayId;
    }

    /// <summary>
    /// 捕獲（ゲームオーバー）が確定しているかを判定する（TIME_UP 遷移の抑止に使う）。
    ///  ・IsResultConfirmed() のうち「捕獲」だけを見る（クリア確定では抑止しない）。
    ///  ・捕獲確定は「現在プレイIDと一致する CaptureConfirmedPlayId」に統一する（非空判定だけでは使わない）。
    /// </summary>
    private bool IsCaptureConfirmed()
    {
        var host = ParentUdpRuntimeHost.Running;
        if (host != null && host.Session != null &&
            IsCaptureConfirmedForCurrentPlay(host.Session))
            return true;

        return _suspicionSystem != null &&
               (_suspicionSystem.isCaught || _suspicionSystem.HasPermanentGameOver);
    }

    /// <summary>TIME_UP の結果画面へ遷移する（既存の遷移ヘルパーを使う）。</summary>
    private void TransitionToTimeUp()
    {
        if (string.IsNullOrEmpty(timeUpSceneName))
        {
            Debug.LogWarning("[ParentGameTimeManager] timeUpSceneName が空のため遷移できません。", this);
            return;
        }

        Debug.Log($"[ParentGameTimeManager] TIME_UP により '{timeUpSceneName}' へ遷移します。", this);

        if (resolveSceneName)
            ParentSceneTransitionRunner.LoadSceneNowResolved(timeUpSceneName);
        else
            ParentSceneTransitionRunner.LoadSceneNow(timeUpSceneName);
    }
}
