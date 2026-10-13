using System;

/// <summary>
/// ChildNotificationHub：
/// 子機の「通知と保留」だけを持つ通常C#クラス（MonoBehaviourではない）。今回の範囲は SLEEP（睡眠入力ロック）のみ。
///
/// 保持する状態（実行時の唯一の保持元）：
///  ・現在の睡眠入力ロック状態（IsSleepLocked）
///
/// 境界（このクラスが触らないもの）：
///  ・UnityEngine／SceneManager／PlayerPrefs／GameObject／ソケット／Coroutineを参照しない。
///  ・捕獲演出待機のロック（captureWaitLocked）は扱わない。
///    ChildSleepLockApplier が「sleepLocked OR captureWaitLocked」で適用する既存方式を維持し、同じフラグへ統合しない。
///  ・購読成立時の初期同期は consumer（ChildSleepLockApplier）が IsSleepLocked を読んで force 適用する
///    既存方式を維持する（イベント購読だけで自動通知を増やさない）。
///
/// 状態の保持元：
///  ・ChildUdpReceiver の isSleepInputLocked は廃止し、IsSleepInputLocked はこのクラスの状態を返す。
///    イベント（SleepLockChanged）は add/remove をこのクラスへ直接委譲する（転送ハンドラを挟まない）。
/// </summary>
public sealed class ChildNotificationHub
{
    private bool _isSleepLocked;

    /// <summary>睡眠入力ロック状態の変化通知（同値では発火しない）。</summary>
    public event Action<bool> SleepLockChanged;

    /// <summary>現在の睡眠入力ロック状態（購読者が居なくても保持される）。</summary>
    public bool IsSleepLocked => _isSleepLocked;

    /// <summary>実際の購読者が居るか（add/remove を委譲されているため実購読者と一致する）。</summary>
    public bool HasSleepLockSubscriber => SleepLockChanged != null;

    /// <summary>
    /// 睡眠入力ロック状態を更新する。
    ///  ・同値なら何もしない（状態も通知も変えない＝同値ハートビートで通知しない）。
    ///  ・更新してから1回だけ通知する。
    /// </summary>
    public void SetSleepLocked(bool next)
    {
        if (_isSleepLocked == next) return;

        _isSleepLocked = next;
        SleepLockChanged?.Invoke(next);
    }

    // ── CAUGHT（親機の捕獲）の未通知保留と通知 ────────────────────────────────
    /// <summary>
    /// 親機の捕獲（CAUGHT）を通知するイベント（購読準備前の受信は最大1件保留する）。
    ///  ・add/remove を直接委譲されているため、HasCaughtSubscriber は実際の購読者（GameManager）の有無と一致する。
    /// </summary>
    public event Action CaughtReceived;

    /// <summary>実際の購読者が居るか。</summary>
    public bool HasCaughtSubscriber => CaughtReceived != null;

    /// <summary>未通知の CAUGHT が保留されているか（購読前に受理した1件）。</summary>
    public bool HasPendingCaught { get; private set; }

    /// <summary>保留した受信時プレイID（現在プレイIDとは別の記録＝保留の記録）。</summary>
    public string PendingCaughtPlayId { get; private set; } = "";

    /// <summary>購読前受信を1件だけ保留する（同値・再送では増やさない＝単一のフラグとIDを上書きするだけ）。</summary>
    public void EnqueuePendingCaught(string receivedPlayId)
    {
        HasPendingCaught = true;
        PendingCaughtPlayId = receivedPlayId ?? "";
    }

    /// <summary>保留を破棄する（保留フラグとIDをクリア）。</summary>
    public void ClearPendingCaught()
    {
        HasPendingCaught = false;
        PendingCaughtPlayId = "";
    }

    /// <summary>現在の購読者へ CAUGHT を通知する（保留状態は変更しない）。</summary>
    public void NotifyCaughtNow()
    {
        CaughtReceived?.Invoke();
    }

    /// <summary>CAUGHT 保留の消費結果（呼び出し側が既存の診断ログを出し分けるために使う）。</summary>
    public enum CaughtPendingOutcome
    {
        /// <summary>保留が無かった。</summary>
        None = 0,
        /// <summary>非ゲームシーン（タイトル／結果）へ移ったため保留を破棄した。</summary>
        Cleared = 1,
        /// <summary>消費条件が揃わないため保留を維持した。</summary>
        Kept = 2,
        /// <summary>保留をクリアしてから1回通知した。</summary>
        Notified = 3,
    }

    /// <summary>
    /// CAUGHT 保留の消費（シーン条件は呼び出し側が評価して渡す）。
    ///  ・非ゲームシーン（タイトル／結果）→ 保留を破棄
    ///  ・ゲームシーン以外（ロード中など）→ 保留を維持
    ///  ・ゲームシーン：実購読者あり かつ 保留IDが現在プレイIDと一致 → クリアしてから1回通知
    ///  ※caughtHandled（受理済み）は本クラスでは扱わない（ChildPlaySession が唯一の保持元）。
    /// </summary>
    public CaughtPendingOutcome ConsumePendingCaught(bool isNonGameplayScene, bool isGameplayScene, string currentPlayId)
    {
        if (!HasPendingCaught) return CaughtPendingOutcome.None;

        if (isNonGameplayScene)
        {
            ClearPendingCaught();
            return CaughtPendingOutcome.Cleared;
        }

        if (!isGameplayScene) return CaughtPendingOutcome.Kept;
        if (CaughtReceived == null) return CaughtPendingOutcome.Kept;
        if (PendingCaughtPlayId != (currentPlayId ?? "")) return CaughtPendingOutcome.Kept;

        ClearPendingCaught();   // クリアしてから通知する
        CaughtReceived?.Invoke();
        return CaughtPendingOutcome.Notified;
    }

    // ── 捕獲演出待機の状態保持（今回の範囲：状態の保持のみ）─────────────────────
    /// <summary>
    /// 捕獲演出待機の段階（UnityEngine非依存の独自enum）。
    ///  ・None      ：このプレイでは捕獲を受理しておらず、完了通知も受けていない
    ///  ・Waiting   ：捕獲を受理し、親機の演出完了通知を待っている
    ///  ・Completed ：演出完了（または子機側の上限）で待機を解除済み
    ///  ※旧側の公開型 ChildUdpReceiver.CaptureWaitState とは、旧側のプロパティで
    ///    明示的に対応付ける（数値キャストはしない。ハブは旧MonoBehaviourの型を参照しない）。
    /// </summary>
    public enum CaptureWaitPhase
    {
        None = 0,
        Waiting = 1,
        Completed = 2,
    }

    /// <summary>現在の捕獲演出待機の段階（保持元）。</summary>
    public CaptureWaitPhase WaitPhase { get; set; } = CaptureWaitPhase.None;

    /// <summary>待機状態が属するプレイID（保留の記録。現在プレイIDの正は ChildPlaySession）。</summary>
    public string CaptureWaitPlayId { get; set; } = "";

    /// <summary>捕獲演出の待機中か（親機の演出完了待ち）。</summary>
    public bool WaitingGameOverSequence { get; set; }

    // ── 捕獲演出待機のイベントと状態遷移（今回の範囲）────────────────────────────
    /// <summary>捕獲を受理し、演出待機へ入ったときに発火する（add/remove を旧側から直接委譲される）。</summary>
    public event Action CaptureWaitStarted;

    /// <summary>演出完了（または子機側の上限）で待機を解除したときに、理由付きで発火する。</summary>
    public event Action<string> CaptureSequenceReleased;

    /// <summary>
    /// 待機開始：状態（Waiting・待機ID・待機bool）を更新してから開始通知する。
    /// 二重開始は既存どおり抑止する（既に待機中なら false）。
    /// </summary>
    public bool BeginCaptureWait(string currentPlayId)
    {
        if (WaitingGameOverSequence) return false;

        WaitingGameOverSequence = true;
        WaitPhase = CaptureWaitPhase.Waiting;
        CaptureWaitPlayId = currentPlayId ?? "";

        CaptureWaitStarted?.Invoke();

        // 先着確認：待機前に完了通知が届いていた場合は、待機へ入った時点で即解除する（既存の条件をそのまま移設）。
        //  pendingが非空、かつ currentPlayIdが空 または pending == currentPlayId
        if (!string.IsNullOrEmpty(SequenceDonePendingPlayId) &&
            (string.IsNullOrEmpty(currentPlayId) || SequenceDonePendingPlayId == currentPlayId))
        {
            ReleaseWait(currentPlayId, $"演出完了通知が捕獲通知より先に届いていた（playId='{SequenceDonePendingPlayId}'）");
        }

        return true;
    }

    /// <summary>
    /// 待機解除：状態（Completed・待機bool）を更新してから解除通知する。
    /// 二重解除は既存どおり抑止する（待機中でなければ false。未捕獲を完了扱いにしない）。
    /// </summary>
    public bool ReleaseWait(string currentPlayId, string reason)
    {
        if (!WaitingGameOverSequence) return false;

        WaitingGameOverSequence = false;
        WaitPhase = CaptureWaitPhase.Completed;
        if (string.IsNullOrEmpty(CaptureWaitPlayId)) CaptureWaitPlayId = currentPlayId ?? "";

        CaptureSequenceReleased?.Invoke(reason);
        return true;
    }

    /// <summary>
    /// 子機側の上限による解除。既に解除済みなら Completed にそろえるだけで通知はしない（既存どおり）。
    /// </summary>
    public bool ReleaseWaitByLocalTimeout(string currentPlayId, string reason)
    {
        if (!WaitingGameOverSequence)
        {
            WaitPhase = CaptureWaitPhase.Completed;
            if (string.IsNullOrEmpty(CaptureWaitPlayId)) CaptureWaitPlayId = currentPlayId ?? "";
            return false;
        }

        return ReleaseWait(currentPlayId, $"子機側の上限（{reason}）");
    }

    /// <summary>
    /// 待機状態のリセット（None・待機ID・待機bool）。解除イベントは発火しない（誤って結果遷移させない）。
    /// </summary>
    public void ResetWait()
    {
        WaitingGameOverSequence = false;
        WaitPhase = CaptureWaitPhase.None;
        CaptureWaitPlayId = "";
    }

    /// <summary>
    /// 現在プレイの演出完了が確定済みか。
    ///  ・Completed のときだけ true（「待機中ではない」だけでは完了扱いにしない）。
    ///  ・待機IDが分かる場合は現在プレイIDと一致するときだけ true（前回プレイを混ぜない）。
    /// </summary>
    public bool IsWaitCompletedForPlay(string currentPlayId)
    {
        if (WaitPhase != CaptureWaitPhase.Completed) return false;

        if (!string.IsNullOrEmpty(CaptureWaitPlayId) &&
            !string.IsNullOrEmpty(currentPlayId) &&
            CaptureWaitPlayId != currentPlayId)
        {
            return false;
        }

        return true;
    }

    // ── 演出完了通知の先着・重複判定に使うID（保持はハブ内だけ。外部からの書き込みは不要）────────────────
    /// <summary>完了通知を処理済みのプレイID（重複・再送を無視する判定に使う記録）。</summary>
    public string SequenceDoneHandledPlayId { get; private set; } = "";

    /// <summary>捕獲通知より先に届いた完了通知のプレイID（順序入れ替わり対応の記録）。</summary>
    public string SequenceDonePendingPlayId { get; private set; } = "";

    /// <summary>
    /// 演出完了通知の重複判定と初回受理の記録。
    ///  ・非空IDかつ処理済みIDと一致する場合だけ重複として false を返す（空IDの挙動は従来どおり）。
    ///  ・初回受理なら処理済みIDを記録して true を返す。
    ///  ※ACKの送信は呼び出し側（解析経路）が行う。ハブはソケット・ログ・シーン処理を行わない。
    /// </summary>
    public bool TryAcceptSequenceDone(string effectivePlayId)
    {
        if (!string.IsNullOrEmpty(effectivePlayId) && SequenceDoneHandledPlayId == effectivePlayId)
            return false;

        SequenceDoneHandledPlayId = effectivePlayId;
        return true;
    }

    /// <summary>
    /// 初回受理後の処理：待機中なら解除（戻り値 true）、未待機なら先着保留（戻り値 false）。
    ///  ・解除理由の文字列は呼び出し側が組み立てて渡す（ハブはログを出さない）。
    /// </summary>
    public bool HandleSequenceDoneAccepted(string effectivePlayId, string releaseReason)
    {
        if (WaitingGameOverSequence)
        {
            ReleaseWait(effectivePlayId, releaseReason);
            return true;
        }

        SequenceDonePendingPlayId = effectivePlayId;
        return false;
    }

    /// <summary>
    /// 先着・重複記録のリセット（新プレイ／タイトル復帰）。解除イベントは発火しない。
    ///  ・待機状態のリセット（ResetWait）とは別メソッドのまま、既存の呼び出し順序を維持する。
    /// </summary>
    public void ResetSequenceDoneRecords()
    {
        SequenceDoneHandledPlayId = "";
        SequenceDonePendingPlayId = "";
    }
}
