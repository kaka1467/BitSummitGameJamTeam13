using System;
using System.Collections.Generic;

/// <summary>
/// ParentRankSyncCoordinator：
/// 親機のランキング同期の「状態保持」だけを持つ通常C#クラス（MonoBehaviourではない）。
///
/// 今回の範囲（保持元）：
///  ・同期ストア（保存コピー。子機が正）＝既存 RankSyncStore をそのまま使う
///  ・ピア検証状態（今接続している子機の同期元）＝既存 RankSyncPeerGate をそのまま使う
///  ・この接続で検証済みかどうかの状態
///  ・表示状態の直前値（通知が必要かの判断材料）
///
/// 境界（このクラスが触らないもの）：
///  ・判定・適用・表示・遷移のロジックは既存クラス（RankSyncStore／RankSyncPeerGate／
///    RankSyncDisplay／RankSyncNavigation）と旧側（ParentUdpSender）が担う（同期ロジックは作り直さない）。
///  ・UnityEngine／PlayerPrefs／SceneManager／ソケット／Coroutineを参照しない。
///  ・現在プレイID・捕獲確定プレイIDは ParentPlaySession が唯一の保持元（ここに複製しない。必要な時点で引数で渡す）。
///  ・ACK・チャレンジ・保存・復元の実送信／実保存は旧側が行う（本クラスはデータを返すだけ）。
///  ・接続時検証（Discovery／HELLO）は入口（既存 PeerGate への委譲）だけをここが受け、
///    結果別ログ・実送信・再送・通知・実遷移は旧側（ParentUdpSender）が担う。
///  ・保存／復元は「保存に渡すデータの取得」と「読み出された保存データの既存復元処理」だけをここが受け、
///    PlayerPrefs のキー・Get／Set／Save・時刻の取得・ログ・通知の実発火は旧側が担う。
///
/// 互換：
///  ・保持中のコピー（RankSyncStore）と接続確認状態（RankSyncPeerGate）は本クラスが保持し、
///    外部へは読み取り専用の問い合わせ（Store*／*Peer*／HasPendingChallenge 等）だけで参照する
///    （可変オブジェクトを別名で外へ返さない）。
///  ・接続ごとの検証済みフラグと直前表示状態の setter は private とし、更新は入口
///    （ResetSyncVerifiedForNewChallenge／NoteSnapshotVerified／CommitSyncState）だけが行う。
///  ・ParentUdpSender 側の委譲プロパティ（_rankSync／_peerGate／_syncVerifiedThisConnection／
///    _lastSyncDisplayState）は参照が無くなった時点で削除した（公開APIは名前・型・戻り値を維持）。
/// </summary>
public sealed class ParentRankSyncCoordinator
{
    /// <summary>ランキングの保存コピー（子機が正）＝既存 RankSyncStore をそのまま使用する。
    /// 外部へは読み取り専用の問い合わせ（Store*）だけで参照する（本クラスが保持元）。</summary>
    private RankSyncStore Store { get; } = new RankSyncStore();

    /// <summary>今接続している子機の同期元（接続確認で確定）＝既存 RankSyncPeerGate をそのまま使用する。
    /// 外部へは読み取り専用の問い合わせ（*Peer*／HasPendingChallenge 等）だけで参照する。</summary>
    private RankSyncPeerGate PeerGate { get; } = new RankSyncPeerGate();

    /// <summary>この接続でスナップショット適用／同一コピー確認まで進んだか（表示の Current 判定に使う）。
    /// 更新は本クラスの入口（ResetSyncVerifiedForNewChallenge／NoteSnapshotVerified）だけが行う。</summary>
    public bool SyncVerifiedThisConnection { get; private set; }

    /// <summary>表示状態が変わったときだけ通知を出すための直前値。
    /// 更新は本クラスの入口（CommitSyncState）だけが行う。</summary>
    public RankSyncDisplayState LastSyncDisplayState { get; private set; } = RankSyncDisplayState.NoData;

    // ── 表示判定・同期結果の問い合わせ・通知要否（今回の範囲）──────────────────
    /// <summary>表示用コピーの状態（既存 RankSyncDisplay.Evaluate をそのまま使う＝判定式を複製しない）。</summary>
    public RankSyncDisplayState GetDisplayState()
        => RankSyncDisplay.Evaluate(
            Store.HasData,
            PeerGate.IsConfirmed,
            PeerGate.ConnectedSourceId,
            Store.SourceId,
            SyncVerifiedThisConnection);

    /// <summary>表示用コピーの状態ラベル（「未同期」「前回同期データ」「同期完了」）。</summary>
    public string GetDisplayLabel() => RankSyncDisplay.ToLabel(GetDisplayState());

    /// <summary>
    /// 表示用コピーが「今の接続相手の最新」と言えるか。
    /// false のときは前回同期データ／未同期であり、最新の子機データとして見せてはいけない。
    /// </summary>
    public bool IsSyncCurrent => GetDisplayState() == RankSyncDisplayState.Current;

    /// <summary>
    /// 同期済み結果の取得（GameOver／TimeUpを区別する）。
    /// データが無ければ false と既定値（lastScore=0／top5=null）を返す（既存の戻り値・out値を維持）。
    /// </summary>
    public bool TryGetSyncedResult(bool gameOver, out int lastScore, out int[] top5)
    {
        if (!Store.HasData)
        {
            lastScore = 0;
            top5 = null;
            return false;
        }

        lastScore = gameOver ? Store.GameOverLastScore : Store.TimeUpLastScore;
        top5 = (int[])(gameOver ? Store.GameOverTop : Store.TimeUpTop).Clone();
        return true;
    }

    /// <summary>通知が必要か（直前表示状態と現在値の比較。既存 RankSyncDisplay.ShouldNotify をそのまま使う）。</summary>
    public bool ShouldNotifySyncState() => RankSyncDisplay.ShouldNotify(LastSyncDisplayState, GetDisplayState());

    /// <summary>
    /// 直前表示状態を現在値へ更新し、更新前の値を返す（通知可のときだけ呼ぶ＝既存の更新順序を維持）。
    /// </summary>
    /// <summary>
    /// パース済みスナップショットの受理・適用判断の結果。
    ///  ・受理拒否は Apply 結果（RankSyncApply）と混ぜない（Mismatch等として扱わない）。
    /// </summary>
    public enum SnapshotDecision
    {
        /// <summary>接続確認済みの相手と一致しないため受理しない（Applyは呼ばない）。</summary>
        NotAccepted = 0,

        /// <summary>受理して Apply を実行した（結果は applyResult）。</summary>
        Applied = 1,
    }

    /// <summary>
    /// 直前表示状態を現在値へ更新し、更新前の値を返す（通知可のときだけ呼ぶ＝既存の更新順序を維持）。
    /// </summary>
    public RankSyncDisplayState CommitSyncState()
    {
        RankSyncDisplayState previous = LastSyncDisplayState;
        LastSyncDisplayState = GetDisplayState();
        return previous;
    }

    // ── 受理・適用判断と受信検証済みの記録（今回の範囲）──────────────────────────
    /// <summary>
    /// 受理・適用判断（既存順序を維持：PeerGate.AcceptsSnapshot → 受理可能な場合だけ Store.Apply）。
    ///  ・受理拒否のときは Apply を呼ばない。
    ///  ・この入口では検証済みフラグを更新しない（呼び出し側が段階に分けて記録する）。
    ///  ・ACK・保存・ログ・通知・遷移は行わない（すべて旧側の責務）。
    /// </summary>
    public SnapshotDecision EvaluateSnapshot(RankingSnapshot snap, out RankSyncApply applyResult)
    {
        applyResult = default;

        if (!PeerGate.AcceptsSnapshot(snap.SourceId)) return SnapshotDecision.NotAccepted;

        applyResult = Store.Apply(snap);
        return SnapshotDecision.Applied;
    }

    /// <summary>
    /// 受信で確認できたことを記録する（検証済みフラグ）。
    ///  ・呼ぶのは Duplicate と新規適用のときだけ（Stale／Mismatch／受理拒否では呼ばない）。
    ///  ・新規適用では「保存とログの後・イベント発火の前」という既存位置で呼ぶ（判定入口と分ける）。
    /// </summary>
    public void NoteSnapshotVerified() => SyncVerifiedThisConnection = true;

    // ── 接続時検証（Discovery／HELLO）の入口（今回の範囲）──────────────────────
    /// <summary>
    /// Discovery の接続時検証（既存 RankSyncPeerGate.OnDiscovery をそのまま使う）。
    ///  ・結果型（NewChallenge／ResentChallenge／AlreadyConfirmed）と challengeId はそのまま返す。
    ///  ・この入口では検証済みフラグを更新しない（本来の位置で ResetSyncVerifiedForNewChallenge を呼ぶ）。
    ///  ・送信・再送・ログ・通知は行わない（すべて旧側の責務）。
    /// </summary>
    public RankSyncPeerGate.DiscoveryResult OnDiscovery(string childSourceId, int childSessionSeq, out string challengeId)
        => PeerGate.OnDiscovery(childSourceId, childSessionSeq, out challengeId);

    /// <summary>
    /// 新しい接続確認を始めるときに検証済みフラグを false へ戻す（旧側の既存位置から呼ぶ）。
    ///  ・呼ぶのは NewChallenge のときだけ（ResentChallenge／AlreadyConfirmed では呼ばない）。
    /// </summary>
    public void ResetSyncVerifiedForNewChallenge() => SyncVerifiedThisConnection = false;

    /// <summary>
    /// チャレンジを実際に送信したことを記録する（既存 RankSyncPeerGate.NoteChallengeSent をそのまま使う）。
    ///  ・呼び出し位置（実送信の後・再送開始の前）は旧側で維持する。
    /// </summary>
    public void NoteChallengeSent() => PeerGate.NoteChallengeSent();

    /// <summary>
    /// RANK_SYNC_HELLO の接続時検証（既存 RankSyncPeerGate.OnHello をそのまま使う）。
    ///  ・結果はそのまま返す（結果別ログ・受理時の通知は旧側に残す）。
    /// </summary>
    public RankSyncPeerGate.HelloResult OnHello(string challengeId, string sourceId, int sessionSeq)
        => PeerGate.OnHello(challengeId, sourceId, sessionSeq);

    // ── 保持中コピー・接続確認状態の読み取り専用問い合わせ（今回の範囲）──────────
    /// <summary>保持中コピーの有無（既存 RankSyncStore.HasData の値をそのまま返す）。</summary>
    public bool HasStoreData => Store.HasData;

    /// <summary>保持中コピーの同期元（既存 RankSyncStore.SourceId の値をそのまま返す）。</summary>
    public string StoreSourceId => Store.SourceId;

    /// <summary>保持中コピーの更新番号（既存 RankSyncStore.UpdateNo の値をそのまま返す）。</summary>
    public int StoreUpdateNo => Store.UpdateNo;

    /// <summary>保持中コピーのGAME_OVER最新スコア（ログ・診断用）。</summary>
    public int StoreGameOverLastScore => Store.GameOverLastScore;

    /// <summary>保持中コピーのGAME_OVER TOP5（ログ・診断用。読み取り専用として公開する）。</summary>
    public IReadOnlyList<int> StoreGameOverTop => Store.GameOverTop;

    /// <summary>保持中コピーのTIME_UP最新スコア（ログ・診断用）。</summary>
    public int StoreTimeUpLastScore => Store.TimeUpLastScore;

    /// <summary>保持中コピーのTIME_UP TOP5（ログ・診断用。読み取り専用として公開する）。</summary>
    public IReadOnlyList<int> StoreTimeUpTop => Store.TimeUpTop;

    /// <summary>保持中コピーの最新結果プレイID（遷移可否の判断材料）。</summary>
    public string StoreLatestResultPlayId => Store.LatestResultPlayId;

    /// <summary>保持中コピーの最新結果種別（遷移可否の判断材料）。</summary>
    public string StoreLatestResultType => Store.LatestResultType;

    /// <summary>今の接続で確定した同期元（未確定なら空。ログ・診断・公開API用）。</summary>
    public string ConfirmedPeerSourceId => PeerGate.ConnectedSourceId;

    /// <summary>今待っているチャレンジ ID（無ければ空。ログ・診断用）。</summary>
    public string PendingPeerChallengeId => PeerGate.PendingChallengeId;

    /// <summary>チャレンジを送った回数（ログ・診断用。上限判定の材料そのもの）。</summary>
    public int ChallengeSendCount => PeerGate.ChallengeSendCount;

    /// <summary>確認待ちのチャレンジがあるか（再送ループが各時点で問い合わせる条件）。</summary>
    public bool HasPendingChallenge => PeerGate.HasPendingChallenge;

    /// <summary>再送上限に達したか（再送ループが各時点で問い合わせる条件）。</summary>
    public bool ChallengeExhausted => PeerGate.ChallengeExhausted;

    // ── 保存／復元データの受け渡し（今回の範囲）────────────────────────────────
    /// <summary>
    /// PlayerPrefs へ保存するための、保持中コピーの既存シリアライズ結果を返す
    /// （既存 RankSyncStore.Serialize をそのまま使う＝保存形式・既定値は変えない）。
    ///  ・PlayerPrefs のキー・Set／Save・時刻の記録・ログは旧側が行う。
    /// </summary>
    public string SerializeForSave() => Store.Serialize();

    /// <summary>
    /// 読み出された保存データを、既存の復元処理で取り込む
    /// （既存 RankSyncStore.Deserialize をそのまま使う＝保存形式・復元失敗時の挙動は変えない）。
    ///  ・成功／失敗は既存の戻り値をそのまま返す（失敗時は何もしない＝既存どおり）。
    ///  ・復元に伴う同期状態は「前回同期データ」のまま扱う：接続検証済みフラグも直前表示状態も変更しない
    ///    （復元だけで Current になる等の新しい条件は追加しない）。
    /// </summary>
    public bool RestoreFromSaveData(string data) => Store.Deserialize(data);

    /// <summary>
    /// 保持中の保存コピーが、今確定している接続相手のものか（遷移可否の判断材料）。
    /// 既存 RankSyncPeerGate.AcceptsSnapshot(Store.SourceId) と同じ判定。
    /// </summary>
    public bool StoreMatchesConfirmedPeer() => PeerGate.AcceptsSnapshot(Store.SourceId);
}
