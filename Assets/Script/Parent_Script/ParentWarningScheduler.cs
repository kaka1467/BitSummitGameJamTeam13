using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentWarningScheduler:
/// MotherApproachWarningを自動的に発生させる（タイミング管理の唯一の管理元）。
/// - 最初は猶予期間（graceSeconds）として自動接近をすべて阻止する。
/// - イベント完全終了 →（最低休憩）→（怪しさ段階別のランダム追加待機）→ 次の予感、の順で1回だけ発生させる。
/// - 追加待機の上限は怪しさメーターの既存3段階（MotherGauge.CurrentColorState）で決める。
///     低（青:0〜3）→ 0〜lowSuspicionExtraWaitMaxSeconds
///     中（紫:4〜6）→ 0〜midSuspicionExtraWaitMaxSeconds
///     高（赤:7〜）  → 0〜highSuspicionExtraWaitMaxSeconds
///   段階は抽選時点で確定し、待機途中のゲージ変化では再抽選・短縮しない。
/// - 大きな音のアイテムはTriggerSoon()で早期チェックを強制できる。
/// </summary>
public class ParentWarningScheduler : MonoBehaviour
{
    [Header("システム参照")]
    [Tooltip("制御対象のMotherApproachWarning")]
    public MotherApproachWarning warningSystem;
    public MotherGauge motherGauge;

    [Header("スケジューラー設定")]
    [Tooltip("警告を自動的に発生させる")]
    public bool autoTrigger = true;

    [Tooltip("シーン開始後、この秒数の間は親機が自動接近しません。")]
    public float graceSeconds = 15f;

    [Header("イベント後の最低休憩")]
    [Tooltip("通常の親イベント（覗き・通過など）が終了した後、次の予告を開始するまでに必ず確保する最低休憩時間（秒）。")]
    [SerializeField] public float postEventRestSeconds = 3f;

    [Tooltip("強い親イベント（突入・ドア覗き・片付けなど）が終了した後、次の予告を開始するまでに必ず確保する最低休憩時間（秒）。")]
    [SerializeField] public float postStrongEventRestSeconds = 5f;

    [Header("イベント後のランダム追加待機（怪しさ段階別の上限）")]
    [Tooltip("怪しさが低（青：ゲージ0〜3）のときの追加待機の上限（秒）。0〜この値から1回だけ抽選する。")]
    [SerializeField, Min(0f)] private float lowSuspicionExtraWaitMaxSeconds = 7f;

    [Tooltip("怪しさが中（紫：ゲージ4〜6）のときの追加待機の上限（秒）。0〜この値から1回だけ抽選する。")]
    [SerializeField, Min(0f)] private float midSuspicionExtraWaitMaxSeconds = 5f;

    [Tooltip("怪しさが高（赤：ゲージ7〜）のときの追加待機の上限（秒）。0〜この値から1回だけ抽選する。")]
    [SerializeField, Min(0f)] private float highSuspicionExtraWaitMaxSeconds = 3f;

    [Header("デバッグ")]
    [Tooltip("次のランダム追加待機の残り時間（秒）。未抽選のときは0。")]
    public float timeUntilNextWarning;

    [SerializeField] private bool showDebugLogs = true;

    private Coroutine _schedulerCoroutine;
    private Coroutine _triggerSoonCoroutine;
    private bool _gracePeriodOver;
    // 片付け演出（MotherChoreController）中は true。自動警告の発火を待機させる。
    private bool _blockedByChore;
    // 「前の母親イベントの終了待ち」を1回だけ記録する（この待機が長い＝前のサイクルが終わっていない）。
    private bool _waitingForActiveWarningLogged;

    // ── イベント終了後の最低休憩 ──────────────────────────────────────────────
    // 前フレームの isWarningActive（終了エッジ検出用）。
    private bool _wasWarningActive;
    // 直近のイベントが強イベントだったか（イベント中にサンプリングして終了時に使う）。
    private bool _lastEventWasStrong;
    // 残りの最低休憩時間（秒）。0 より大きい間は次の予告を開始しない。
    private float _restRemaining;

    // 強イベント判定に使う参照（Start で解決する。未配置なら null のまま）。
    private MotherSuspicionSystem _parentDetection;
    private MotherApproachController _approachController;

    // ── 本プレイ終了（プレイ単位のラッチ） ────────────────────────────────────
    // 終了を検知したら true。以降、このプレイでは自動発火を再開しない（Start() で初期化）。
    private bool _playFinished;
    // 終了ログを1回だけ出すための記録。
    private bool _playFinishedLogged;

    public bool IsGracePeriodOver => _gracePeriodOver;

    /// <summary>片付け演出中で自動警告を抑止しているか（Inspector 表示用）。</summary>
    public bool IsBlockedExternally => _blockedByChore;

    /// <summary>
    /// 片付け演出による抑止を設定する。true の間は新しい自動警告を発生させない。
    /// 解除時は、待機中だった次の警告ウィンドウを最初からやり直させる。
    /// </summary>
    public void SetBlockedByChore(bool blocked)
    {
        if (_blockedByChore == blocked) return;
        _blockedByChore = blocked;
        Debug.Log($"[ParentWarningScheduler] 片付け演出による自動警告抑止: {blocked}");

        if (!blocked)
        {
            // 片付けは強イベントなので、終了時に強イベント休憩を確保する。
            // （片付け中は isWarningActive が立たないため、TrackEventEnd では検知できない）
            StartPostEventRest(true);
        }

        if (!blocked && autoTrigger && _schedulerCoroutine == null && !isWarningActiveSatisfied())
        {
            // 片付け終了後に自動警告を再開する（停止状態を残さない）。
            //  ・本プレイ終了後は再開しない（StartScheduler 内で拒否される）。
            StartScheduler();
        }
    }

    // ── イベント終了後の最低休憩 ──────────────────────────────────────────────

    /// <summary>
    /// イベント終了（isWarningActive の立ち下がり）を検知し、休憩タイマーを進める。
    ///  ・イベント中は「強イベントか」をサンプリングしておく（終了後は状態が消えるため）。
    ///  ・片付け中（_blockedByChore）は休憩を進めない（猶予・ウィンドウと同じ扱い）。
    /// </summary>
    private void TrackEventEnd()
    {
        DetectEventEnd();

        // 休憩の消化（片付け中は進めない）。
        if (_restRemaining > 0f && !_blockedByChore)
            _restRemaining = Mathf.Max(0f, _restRemaining - Time.deltaTime);
    }

    /// <summary>
    /// isWarningActive の立ち下がり（イベント終了）を検知して休憩を開始する。
    ///  ・イベント中は「強イベントか」をサンプリングしておく（終了後は状態が消えるため）。
    ///  ・Update から呼ぶほか、待機ゲート（WaitForPostEventRest）からも呼ぶ。
    ///    終了通知はコルーチンから来るため、Update の検知が1フレーム遅れても
    ///    ゲート側で必ず休憩を開始できるようにする（二重開始はしない）。
    /// </summary>
    private void DetectEventEnd()
    {
        bool active = isWarningActiveSatisfied();

        // イベント中は強イベント判定を記録し続ける（種別が後から判明するケースも拾う）。
        if (active)
            _lastEventWasStrong = IsStrongEventActive();

        // 終了エッジ：休憩を開始する。
        if (_wasWarningActive && !active)
        {
            StartPostEventRest(_lastEventWasStrong);
            _lastEventWasStrong = false;
        }

        _wasWarningActive = active;
    }

    /// <summary>
    /// 現在進行中の親イベントが「強いイベント」かを判定する。
    ///  ・突入（MotherApproachController.IsRushIn）
    ///  ・ドア覗き（本チェック。MotherApproachWarning.ActiveRoute == DoorPeek）
    /// 参照が無い場合は false（通常イベント扱い）。
    /// </summary>
    private bool IsStrongEventActive()
    {
        if (_approachController != null && _approachController.IsRushIn) return true;
        if (warningSystem != null &&
            warningSystem.ActiveRoute == MotherApproachWarning.RouteState.DoorPeek) return true;

        return false;
    }

    /// <summary>
    /// イベント終了後の最低休憩を開始する。既に長い休憩が残っている場合は延長しない。
    /// </summary>
    private void StartPostEventRest(bool strong)
    {
        float rest = Mathf.Max(0f, strong ? postStrongEventRestSeconds : postEventRestSeconds);
        if (rest <= 0f || rest <= _restRemaining) return;

        _restRemaining = rest;
        Debug.Log($"[ParentWarningScheduler] イベント後の最低休憩を開始: {rest:F2}s（強イベント={strong}）");
    }

    /// <summary>
    /// 最低休憩が消化されるまで待機する。残り時間は timeUntilNextWarning にも反映する。
    ///  ・待機の先頭で終了検知をやり直す（Update の検知が1フレーム遅れても休憩を落とさない）。
    ///  ・終了エッジは一度きりなので、ここで開始しても Update 側と二重にはならない。
    ///  ・休憩が0なら1フレームも待たずに戻る（呼び出し側は「休憩 → 追加待機」の順序を保つため、
    ///    ウィンドウ抽選の直前と発火直前の両方で本メソッドを通す）。
    /// </summary>
    private IEnumerator WaitForPostEventRest()
    {
        DetectEventEnd();

        bool waited = false;
        while (_restRemaining > 0f)
        {
            // P13休憩中に終了した場合は、休憩を消化せず即座に待機を終える。
            if (LatchPlayFinishedFromCoroutine()) yield break;

            waited = true;
            timeUntilNextWarning = _restRemaining;
            yield return null;
        }

        // 実際に休憩を消化したときだけ1回出す（毎フレームは出さない／終了時は出さない）。
        if (waited && _restRemaining <= 0f && showDebugLogs)
            Debug.Log("[ParentWarningScheduler] 最低休憩の消化が完了 — 追加待機の抽選へ進みます");
    }

    /// <summary>現在 warningSystem が有効な警告シーケンス中か。</summary>
    private bool isWarningActiveSatisfied()
    {
        return warningSystem != null && warningSystem.isWarningActive;
    }

    // ── 本プレイ終了の判定と停止 ──────────────────────────────────────────────

    /// <summary>
    /// 本プレイが終了しているか（新規の自動発火をしてはいけない状態か）。
    ///  ・共通判定は ParentGameTimeManager.IsPlayFinished（時間切れ／明示停止／結果確定／捕獲）。
    ///  ・警告側（MotherApproachWarning）にも同じ判定を問い合わせる（参照の二重化を避けるため
    ///    Warning 側が Manager へ委譲する1本の経路を使う）。
    ///  ・ラッチ済み（_playFinished）なら常に true（同じプレイでは再開しない）。
    ///  ・必須参照不足は「終了」と扱わない（判定は Manager 側の責務）。
    /// </summary>
    /// <summary>
    /// 自動で親イベントを開始してよいかの判定（共通窓口へ委譲）。
    ///  ・ParentGameTimeManager があれば IsPlayFinished、無ければシーン文脈で判定する。
    ///  ・終了による拒否（RejectPlayFinished）だけが終了ラッチの対象。
    ///  ・判定不能（RejectUnavailable）は終了ラッチを立てず、開始だけを拒否する。
    /// </summary>
    private bool IsPlayFinished()
    {
        if (_playFinished) return true;
        return ParentGameTimeManager.EvaluateAutoStart() == ParentGameTimeManager.AutoStartDecision.RejectPlayFinished;
    }

    /// <summary>判定不能による拒否か（終了ラッチは立てない）。</summary>
    private bool IsStartUnavailable()
    {
        if (_playFinished) return false;
        return ParentGameTimeManager.EvaluateAutoStart() == ParentGameTimeManager.AutoStartDecision.RejectUnavailable;
    }

    /// <summary>
    /// 終了を検知した場合にラッチして停止する。既にラッチ済みなら何もしない。
    ///  ・戻り値 true ＝ 終了（呼び出し側は yield break などで待機を抜けること）。
    ///  ・予約・待機を停止し、timeUntilNextWarning を 0 にする。
    ///  ・autoTrigger は書き換えない。
    ///  ・ログは1回だけ。
    ///  ・注意：このメソッドを自身の実行コルーチンから呼ぶと StopCoroutine で自分自身が止まる。
    ///    コルーチン内からは StopSchedulerHandleForPlayFinished()（ハンドルのみ解放）を使う。
    /// </summary>
    private bool TryLatchPlayFinished()
    {
        if (_playFinished) return true;

        // 判定不能（通常ゲームシーンで Manager 不在）は終了ラッチを立てず、開始だけを拒否する。
        if (IsStartUnavailable())
        {
            ParentGameTimeManager.LogUnavailableRejectOnce();
            StopSchedulerInternal();
            return true;   // 開始しない（ただし終了ラッチは立てない）
        }

        if (!IsPlayFinished()) return false;

        _playFinished = true;

        // 待機コルーチン・予約コルーチンを停止し、残り表示を0にする。
        StopSchedulerInternal();

        // Warning 側の未消費予約も破棄する（複数回呼ばれても安全）。
        if (warningSystem != null) warningSystem.NotifyPlayFinished();

        if (!_playFinishedLogged)
        {
            _playFinishedLogged = true;
            Debug.Log("[ParentWarningScheduler] 本プレイ終了を検知 — 自動発火を停止します" +
                      "（以降このプレイでは再開しません。予約・待機を破棄、autoTrigger は変更しません）");
        }

        return true;
    }

    /// <summary>
    /// 自身の実行コルーチン内から終了する場合に使う：ラッチ・ログ・予約破棄だけを行い、
    /// スケジューラーのハンドルは解放しない（呼び出し側が yield break 直前に解放する）。
    ///  ・子 IEnumerator（WaitForPostEventRest 等）が yield break しても親は続行するため、
    ///    親側で必ずこの戻り値を確認して yield break すること。
    /// </summary>
    private bool LatchPlayFinishedFromCoroutine()
    {
        if (_playFinished) return true;

        // 判定不能（通常ゲームシーンで Manager 不在）は終了ラッチを立てず、待機だけを抜ける。
        if (IsStartUnavailable())
        {
            ParentGameTimeManager.LogUnavailableRejectOnce();
            return true;
        }

        if (!IsPlayFinished()) return false;

        _playFinished = true;

        if (warningSystem != null) warningSystem.NotifyPlayFinished();

        if (!_playFinishedLogged)
        {
            _playFinishedLogged = true;
            Debug.Log("[ParentWarningScheduler] 本プレイ終了を検知 — 自動発火を停止します" +
                      "（以降このプレイでは再開しません。予約・待機を破棄、autoTrigger は変更しません）");
        }

        return true;
    }

    private void Start()
    {
        if (warningSystem == null)
            warningSystem = GetComponent<MotherApproachWarning>();

        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        // 強イベント判定用の参照を解決する（毎フレームの Find を避けるため1回だけ）。
        //  ・warningSystem 側の参照を使う（Inspector 設定を尊重する）。
        //  ・未設定なら既存の FindFirstObjectByType と同じ解決方法で補う。
        if (_parentDetection == null && warningSystem != null)
            _parentDetection = warningSystem.parentDetection;
        if (_parentDetection == null)
            _parentDetection = Object.FindFirstObjectByType<MotherSuspicionSystem>();

        if (_approachController == null && warningSystem != null)
            _approachController = warningSystem.approachController;
        if (_approachController == null)
            _approachController = Object.FindFirstObjectByType<MotherApproachController>();

        // 再プレイ・シーン再読み込みで前回の休憩状態を持ち越さない。
        _wasWarningActive = false;
        _lastEventWasStrong = false;
        _restRemaining = 0f;
        // プレイ単位の終了ラッチも初期化する（新しいプレイでは自動発火を再開する）。
        _playFinished = false;
        _playFinishedLogged = false;

        if (autoTrigger)
            StartScheduler();
    }

    private void Update()
    {
        // イベント終了（isWarningActive の立ち下がり）を検知して最低休憩を開始し、
        // 休憩タイマーを進める。キー入力より先に処理する（入力の有無に影響されない）。
        TrackEventEnd();

        // 本プレイ終了を検知したら、待機・予約を停止する（外部停止。コルーチン内ではないため安全）。
        TryLatchPlayFinished();

        if (Keyboard.current == null) return;

        if (Keyboard.current.digit1Key.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningScheduler] 1キーを押下 - 母親ドア確認トリガー");
            TriggerDoorNow();
        }
    }

    private void StartScheduler()
    {
        StopSchedulerInternal();

        if (!autoTrigger)
            return;

        // 本プレイ終了後は再起動しない（同じプレイでは再開しない）。
        if (TryLatchPlayFinished()) return;

        _schedulerCoroutine = StartCoroutine(SchedulerCoroutine());

        // U6：初回 StartCoroutine が同フレームで即終了（yield break）した場合、
        //     コルーチン内で _schedulerCoroutine = null にしていても、ここで停止済みハンドルが
        //     再代入されてしまう。ラッチ・判定不能・終了のいずれかなら null に戻して整合させる。
        //  ・終了以外の初回即終了経路は現状存在しない（猶予0かつ終了時のみが該当）。
        //  ・StopAllCoroutines は使わない。
        if (_playFinished || IsStartUnavailable() || IsPlayFinished())
            _schedulerCoroutine = null;
    }

    private void StopScheduler()
    {
        StopSchedulerInternal();
    }

    private void StopSchedulerInternal()
    {
        if (_schedulerCoroutine != null)
        {
            StopCoroutine(_schedulerCoroutine);
            _schedulerCoroutine = null;
        }

        if (_triggerSoonCoroutine != null)
        {
            StopCoroutine(_triggerSoonCoroutine);
            _triggerSoonCoroutine = null;
        }

        timeUntilNextWarning = 0f;
    }

    /// <summary>
    /// 手動デバッグトリガー（1キー）— ドアルートを強制する。
    /// </summary>
    private void TriggerDoorNow()
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

        Debug.Log("[ParentWarningScheduler] TriggerDoorNow: MANUAL DOOR TRIGGER");
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

        Debug.Log("[ParentWarningScheduler] TriggerNow: MANUAL TRIGGER");
        warningSystem.StartWarningSequence();
    }

    /// <summary>
    /// 短い遅延後に警告を発生させる（大きな音のアイテムで使用）。
    /// 緊急チェックを早期に行えるようスケジューラーループをリセットする。
    /// </summary>
    public void TriggerSoon(float delaySeconds = 1f)
    {
        if (showDebugLogs)
            Debug.Log($"[ParentWarningScheduler] TriggerSoon requested: delay={delaySeconds:F1}s");

        if (_schedulerCoroutine != null)
        {
            StopCoroutine(_schedulerCoroutine);
            _schedulerCoroutine = null;
        }

        if (_triggerSoonCoroutine != null)
        {
            StopCoroutine(_triggerSoonCoroutine);
        }

        _triggerSoonCoroutine = StartCoroutine(TriggerSoonCoroutine(delaySeconds));
    }

    private IEnumerator TriggerSoonCoroutine(float delay)
    {
        float t = Mathf.Max(0f, delay);

        while (t > 0f)
        {
            t -= Time.deltaTime;
            yield return null;
        }

        // 片付け演出中は発火しない（大きな音による突入も片付けと重複させない）。
        while (_blockedByChore)
            yield return null;

        if (warningSystem != null && !warningSystem.isWarningActive)
        {
            Debug.Log("[ParentWarningScheduler] TriggerSoon firing warning now");
            warningSystem.StartWarningSequence();
            yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
        }

        _triggerSoonCoroutine = null;
        StartScheduler();
    }

    private IEnumerator SchedulerCoroutine()
    {
        _gracePeriodOver = false;

        float grace = Mathf.Max(0f, graceSeconds);
        if (showDebugLogs)
            Debug.Log($"[ParentWarningScheduler] Grace period started: {grace:F1}s");

        timeUntilNextWarning = grace;
        while (timeUntilNextWarning > 0f)
        {
            // 猶予中に終了した場合は、猶予を消化せず即座に停止する（開始へ進まない）。
            if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

            // 片付け演出中は時間を進めて待機する（新しい警告は発生させない）。
            if (!_blockedByChore)
                timeUntilNextWarning -= Time.deltaTime;
            yield return null;
        }

        _gracePeriodOver = true;
        timeUntilNextWarning = 0f;

        if (showDebugLogs)
            Debug.Log("[ParentWarningScheduler] Grace period over");

        while (true)
        {
            // ループ先頭：終了していれば停止する。
            if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

            // 片付け演出中は自動警告を発生させない（通常の親イベントと重複させない）。
            while (_blockedByChore)
            {
                // 片付け待ち中に終了した場合は待機を抜ける（開始へ進まない）。
                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }
                yield return null;
            }

            if (warningSystem != null && warningSystem.isWarningActive)
            {
                // 前の母親イベントが終わるまで待つ。ここが長い場合、前のサイクルが
                // 終了通知（onPassedByDoor／EndWarningSequence）を受け取れていない。
                // 停止の切り分けに必要なので、この警告は showDebugLogs に関係なく1回だけ出す。
                if (!_waitingForActiveWarningLogged)
                {
                    _waitingForActiveWarningLogged = true;
                    Debug.LogWarning("[ParentWarningScheduler] 前の母親イベントの終了待ちに入りました" +
                                     "（isWarningActive=true）。この待機が長い場合は前のサイクルが終了通知を受け取れていません。");
                }

                yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
                _waitingForActiveWarningLogged = false;

                // イベント待ち明けに終了していた場合は停止する。
                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

                // イベント終了直後の最低休憩を消化してから次のウィンドウを抽選する。
                //  ・子 IEnumerator が yield break しても親は続行するため、ここで再確認する。
                yield return WaitForPostEventRest();
                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }
            }

            // 片付け演出が開始された場合も発火を待つ。
            while (_blockedByChore)
            {
                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }
                yield return null;
            }

            // ── 最低休憩の共通ゲート（通常完了・片付け完了の両方がここを通る）───────────
            //  ・通常イベント：isWarningActive の立ち下がり（＝帰路・非表示・ドア閉・後始末後）で
            //    StartPostEventRest が武装した休憩を、上の分岐で既に消化している（ここでは即通過）。
            //  ・片付け：SetBlockedByChore(false) が武装した休憩を、ここで必ず消化してから
            //    次の追加待機（fireOffset）を抽選する。
            //    → 休憩と追加待機が並行して「max(休憩, 追加待機)」になる不一致を解消する。
            //  ・休憩は _restRemaining の1本だけで管理し、StartPostEventRest が残時間より短い値を
            //    無視するため、同じ終了を原因に休憩が延長・繰り返しされることはない。
            yield return WaitForPostEventRest();
            if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

            // 休憩中に片付けが始まった場合は、追加待機を抽選せず先頭（ブロック解除待ち）へ戻る。
            if (_blockedByChore) continue;

            // ── 追加待機（fireOffset）の抽選：怪しさ段階の上限から1回だけ抽選する ──
            //  ・段階の判定元は MotherGauge.CurrentColorState（既存の青=低／紫=中／赤=高）。
            //  ・旧方式の「基準ウィンドウ5〜10s − ゲージ補正 → 下限5s」は廃止した。
            //    下限クランプが無いため、高段階の0〜3秒が0〜5秒へ戻されることはない。
            //  ・ここで確定した stage / waitMaxSeconds / fireOffset は待機中に再抽選しない。
            float waitMaxSeconds = ResolveExtraWaitMaxSeconds(out MotherGauge.SuspicionColorState waitStage);
            float fireOffset = Random.Range(0f, Mathf.Max(0f, waitMaxSeconds));

            if (showDebugLogs)
            {
                int gaugeAtDraw = (motherGauge != null) ? motherGauge.currentGauge : -1;
                Debug.Log($"[ParentWarningScheduler] 追加待機を抽選 | stage={DescribeStage(waitStage)}" +
                          $" | gauge={gaugeAtDraw} | max={waitMaxSeconds:F2}s | fireOffset={fireOffset:F2}s");
            }

            float elapsed = 0f;
            bool firedThisWindow = false;
            // 追加待機を捨てたか（割り込みで古い elapsed / fireOffset を持ち越さない）。
            bool windowDiscarded = false;
            string discardReason = null;

            // 上限が0秒（高段階で上限0）でも必ず1回は発火判定を通すため、!firedThisWindow も継続条件にする。
            while (elapsed < waitMaxSeconds || !firedThisWindow)
            {
                // ウィンドウ待機中に終了した場合は elapsed を進めず停止する（発火直前へ落とさない）。
                if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

                // ── 割り込み検出：追加待機は破棄する ──
                //  ・片付け開始／通常イベント・手動イベント開始で、抽選済みの fireOffset と
                //    消化済みの elapsed は無効になる。割り込みイベントの完全終了後に
                //    「新たな休憩 → 新たな追加待機（再抽選）」へ進む。
                if (_blockedByChore)
                {
                    windowDiscarded = true;
                    discardReason = "片付け開始";
                    break;
                }

                if (warningSystem != null && warningSystem.isWarningActive)
                {
                    windowDiscarded = true;
                    discardReason = "通常／手動イベント開始";
                    break;
                }

                // 休憩が残っている間は追加待機を消化しない（休憩 → 追加待機の順序を崩さない）。
                if (_restRemaining > 0f)
                {
                    timeUntilNextWarning = _restRemaining;
                    yield return null;
                    continue;
                }

                elapsed += Time.deltaTime;
                timeUntilNextWarning = Mathf.Max(0f, fireOffset - elapsed);

                if (!firedThisWindow && elapsed >= fireOffset)
                {
                    firedThisWindow = true;

                    // ── 発火直前の最終確認（同一フレームに解除・終了通知が重なる場合を含む）──
                    //   1) プレイが終了していない
                    if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

                    //   2) 片付けブロック中ではない／他イベントが占有中ではない
                    if (_blockedByChore || (warningSystem != null && warningSystem.isWarningActive))
                    {
                        windowDiscarded = true;
                        discardReason = "発火直前に他イベントが占有";
                        break;
                    }

                    //   3) 必要な休憩が完了している（0なら即座に戻る）
                    yield return WaitForPostEventRest();
                    if (LatchPlayFinishedFromCoroutine()) { _schedulerCoroutine = null; yield break; }

                    if (_blockedByChore || _restRemaining > 0f ||
                        (warningSystem != null && warningSystem.isWarningActive))
                    {
                        windowDiscarded = true;
                        discardReason = "休憩の消化中に他イベントが占有";
                        break;
                    }

                    //   4) 問題なければ予感を開始し、そのイベントの完全終了まで待つ。
                    if (warningSystem != null)
                    {
                        warningSystem.StartWarningSequence();

                        // 予感が実際に開始された場合だけ、その完全終了まで待ってウィンドウを閉じる。
                        //  ・終了後は先頭へ戻り「休憩 → 新しい追加待機（再抽選）」をやり直す。
                        //    古いウィンドウの残り時間を持ち越さない（次の待機を読みやすくする）。
                        if (warningSystem.isWarningActive)
                        {
                            // 抽選時の怪しさ段階・上限・fireOffset・実測待機を1行で確認できるようにする
                            // （予感開始ごとに1回だけ。毎フレームは出さない）。
                            Debug.Log($"[ParentWarningScheduler] Approach triggered by scheduler" +
                                      $" | stage={DescribeStage(waitStage)} | max={waitMaxSeconds:F2}s" +
                                      $" | fireOffset={fireOffset:F2}s | 実測待機={elapsed:F2}s");

                            yield return new WaitWhile(() => warningSystem != null && warningSystem.isWarningActive);
                            break;
                        }

                        // 開始拒否（候補0件など）の場合は従来どおり同一ウィンドウ内で経過を進める
                        // （firedThisWindow が立っているため同じウィンドウで再発火しない）。
                    }
                }

                yield return null;
            }

            // 追加待機を破棄した場合は、古い残り時間を表示せずに先頭（完全終了待ち→休憩→再抽選）へ戻る。
            if (windowDiscarded)
            {
                timeUntilNextWarning = 0f;
                if (showDebugLogs && discardReason != null)
                {
                    Debug.Log($"[ParentWarningScheduler] 追加待機を破棄: {discardReason}" +
                              "（古い elapsed / fireOffset は持ち越さず、割り込みイベントの完全終了後に再抽選します）");
                }

                continue;
            }

            timeUntilNextWarning = 0f;
        }
    }

    /// <summary>
    /// ランダム追加待機の上限（秒）を、怪しさメーターの既存3段階から決める。
    ///  ・段階の判定元は MotherGauge.CurrentColorState ひとつ（青=低／紫=中／赤=高）。
    ///    境界（0〜3 / 4〜6 / 7〜）は MotherGauge 側の既存定義をそのまま使い、ここでは再定義しない。
    ///  ・MotherGauge が見つからない場合は「低（青）」として扱う（旧実装の gauge=0 と同じ扱い）。
    ///  ・副作用なし（読み取りのみ）。
    /// </summary>
    private float ResolveExtraWaitMaxSeconds(out MotherGauge.SuspicionColorState stage)
    {
        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        stage = (motherGauge != null)
            ? motherGauge.CurrentColorState
            : MotherGauge.SuspicionColorState.Blue;

        switch (stage)
        {
            case MotherGauge.SuspicionColorState.Purple:
                return Mathf.Max(0f, midSuspicionExtraWaitMaxSeconds);
            case MotherGauge.SuspicionColorState.Red:
                return Mathf.Max(0f, highSuspicionExtraWaitMaxSeconds);
            default:
                return Mathf.Max(0f, lowSuspicionExtraWaitMaxSeconds);
        }
    }

    /// <summary>ログ用の怪しさ段階名（青=低／紫=中／赤=高）。</summary>
    private static string DescribeStage(MotherGauge.SuspicionColorState stage)
    {
        switch (stage)
        {
            case MotherGauge.SuspicionColorState.Purple: return "中(Purple)";
            case MotherGauge.SuspicionColorState.Red: return "高(Red)";
            default: return "低(Blue)";
        }
    }

    private void OnDestroy()
    {
        StopScheduler();
    }
}