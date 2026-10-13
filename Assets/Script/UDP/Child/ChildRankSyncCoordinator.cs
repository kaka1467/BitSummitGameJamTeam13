using System;

/// <summary>
/// ChildRankSyncCoordinator：
/// 子機のランキング同期の「状態保持」と「既存ポリシーによる判断」だけを持つ通常C#クラス（MonoBehaviourではない）。
///
/// 今回の範囲（保持元）：
///  ・同期元ID（この子機データの識別・永続）
///  ・更新番号（単調増加・永続）と、その更新番号がACK済みかどうか
///  ・接続確認の世代（起動ごとに+1・永続）
///  ・未確認スナップショット全文（そのまま再送するため保持）
///  ・親機が発行した接続確認の challengeId（受信するまで空）
///
/// 境界（このクラスが触らないもの）：
///  ・ランキング本体（最新スコア・TOP5）は子機の既存キーが唯一の保持元（ここに複製しない。必要な時点で引数で渡す）。
///  ・通信文字列の組み立て・実送信・再送Coroutine・時間待ち・再送回数／間隔は旧側（ChildUdpReceiver）が行う
///    （本クラスは送るべき全文を返すだけ）。
///  ・PlayerPrefsの読み書き・キー・ログ・Unityイベントの実発火・シーン判定／実遷移は旧側が行う。
///  ・接続状態は ChildConnectionLink、プレイIDは ChildPlaySession が保持元（ここに複製しない）。
///  ・UnityEngine／PlayerPrefs／SceneManager／ソケット／Coroutineを参照しない。
///
/// 互換：
///  ・ChildUdpReceiver にあった同期専用状態（_rankSyncSourceId／_rankSyncUpdateNo／_rankSyncSessionSeq／
///    _rankSyncConfirmed／_pendingRankSyncMessage／_rankSyncChallengeId）は本クラスへ移した（旧側に複製しない）。
///  ・通信形式・保存キー・保存形式・バージョンは変えない（既存 RankingSnapshot.Format／RankSyncHello を再利用）。
/// </summary>
public sealed class ChildRankSyncCoordinator
{
    /// <summary>同期元ID（この子機データの識別・永続）。</summary>
    public string SourceId { get; private set; } = "";

    /// <summary>更新番号（単調増加・永続）。</summary>
    public int UpdateNo { get; private set; }

    /// <summary>接続確認の世代（起動ごとに+1・永続）。</summary>
    public int SessionSeq { get; private set; }

    /// <summary>現在の更新番号がACK済みか（永続）。</summary>
    public bool IsConfirmed { get; private set; } = true;

    /// <summary>未確認スナップショット全文（そのまま再送するため保持）。</summary>
    public string PendingMessage { get; private set; } = "";

    /// <summary>親機が今の接続確認用に発行した ID（受信するまで空）。</summary>
    public string ChallengeId { get; private set; } = "";

    // ── 問い合わせ（読み取り）─────────────────────────────────────────────────
    /// <summary>未確認スナップショット全文を保持しているか（再送の前提条件）。</summary>
    public bool HasPendingMessage => !string.IsNullOrEmpty(PendingMessage);

    /// <summary>接続確認の要求（challengeId）を受信済みか（HELLOを返す条件）。</summary>
    public bool HasChallengeId => !string.IsNullOrEmpty(ChallengeId);

    /// <summary>接続時に「未確認のまま保持している全文」をそのまま再送すべきか（既存の再接続条件）。</summary>
    public bool HasUnconfirmedPending => !IsConfirmed && HasPendingMessage;

    // ── 状態更新（旧側の既存位置から呼ぶ）──────────────────────────────────────
    /// <summary>
    /// 新しい更新番号を発行して未確認にする（スナップショットを作る前段。既存順序：採番→本文作成→保存）。
    /// 発行した更新番号を返す。
    /// </summary>
    public int BeginNewUpdate()
    {
        UpdateNo++;
        IsConfirmed = false;
        return UpdateNo;
    }

    /// <summary>
    /// いま保持している sourceId・updateNo で送信用スナップショットを組み立て、未確認として保持する
    /// （既存 RankingSnapshot.Format をそのまま使う＝通信形式・バージョンを変えない）。
    /// ランキング本体の値は呼び出し側（子機の既存キーの読み取り）から引数で渡す。組み立てた全文を返す。
    /// </summary>
    public string SetPendingSnapshotFrom(
        int goLastScore, int[] goTop, int tuLastScore, int[] tuTop, string latestPlayId, string latestResultType)
    {
        IsConfirmed = false;
        PendingMessage = RankingSnapshot.Format(
            SourceId, UpdateNo, goLastScore, goTop, tuLastScore, tuTop, latestPlayId, latestResultType);
        return PendingMessage;
    }

    /// <summary>接続確認（RANK_SYNC_CHALLENGE）で受信した challengeId を保持する（HELLO の応答条件）。</summary>
    public void SetChallengeId(string challengeId) => ChallengeId = challengeId ?? "";

    /// <summary>
    /// ACKで未確認を解除してよいかの判断（既存 ChildRankSyncPolicy をそのまま使う）。
    /// 「未確認である」「自分の同期元である」「今保持している更新番号と一致する」の3条件すべてが必要
    /// （古いACKで新しい未確認更新を消さない）。
    /// </summary>
    public bool ShouldAcceptAck(string ackSourceId, int ackUpdateNo)
        => ChildRankSyncPolicy.ShouldAcceptAck(ackSourceId, ackUpdateNo, SourceId, UpdateNo, IsConfirmed);

    /// <summary>ACK受理を記録する（未確認の解除・保持全文の破棄）。保存・再送停止・ログは旧側。</summary>
    public void NoteAckAccepted()
    {
        IsConfirmed = true;
        PendingMessage = "";
    }

    // ── 起動時の復元（既存の復元判断をそのまま移設）────────────────────────────
    /// <summary>
    /// 同期元IDと世代を復元する。
    ///  ・保存された sourceId が空なら新規発行する（generated=true のとき旧側が既存のログを出す）。
    ///  ・世代（sessionSeq）は保存値+1（起動ごとに進める。古い接続確認が遅延しても親機が巻き戻らないようにする）。
    /// </summary>
    public void RestoreSourceIdentity(string savedSourceId, int savedSessionSeq, out bool sourceIdGenerated)
    {
        sourceIdGenerated = string.IsNullOrEmpty(savedSourceId);
        SourceId = sourceIdGenerated ? Guid.NewGuid().ToString("N") : savedSourceId;
        SessionSeq = savedSessionSeq + 1;
    }

    /// <summary>
    /// 更新番号・確認状態・未確認全文を復元する（既存の復元判断をそのまま）。
    ///  ・未確認の全文が保存されていて、同じ sourceId・同じ updateNo のときは「そのまま」再送対象に戻す
    ///    （同じ内容・同じ更新番号を変えない）。
    ///  ・保存全文が無い／不一致の旧状態からは、新しい更新番号へ移行する。
    ///    戻り値 true＝移行が必要（旧側が現在の子機データから全文を作り直す。古い updateNo では送らない）。
    /// </summary>
    public bool RestorePendingState(int savedUpdateNo, bool savedConfirmed, string savedPendingMessage)
    {
        UpdateNo = savedUpdateNo;
        IsConfirmed = savedConfirmed;
        PendingMessage = "";

        if (IsConfirmed) return false;

        if (ChildRankSyncPolicy.CanResendVerbatim(savedPendingMessage, SourceId, UpdateNo))
        {
            PendingMessage = savedPendingMessage;
            return false;
        }

        UpdateNo++;
        IsConfirmed = false;
        return true;
    }
}
