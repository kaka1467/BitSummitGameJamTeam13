using System;
using System.Collections.Generic;

public enum ParentMessageType
{
    Invalid,
    Unknown,
    Ping,
    DiscoveryRequest,
    StartGame,
    TimeUp,
    ChildDead,
    ChildScore,
    LoadingComplete,
    LoudItem,
    TeamReturnToTitle,
    // 片付け演出用：子機のゲーム進行率（0〜1を1000倍した整数）通知
    GameProgress,
    // 子機を唯一の基準としたランキング／スコアのスナップショット同期
    RankingSync,
    // 接続単位の同期元確認（Discovery 直後に子機が送る）
    RankSyncHello
}

public enum ChildGameResultType
{
    Unknown,
    GameOver,
    TimeUp
}

public readonly struct ParentUdpMessage
{
    public ParentMessageType Type { get; }
    public ChildGameResultType ResultType { get; }
    public int Score { get; }
    public string RawPayload { get; }
    public string InvalidValue { get; }
    public string ParseError { get; }

    /// <summary>
    /// プレイ識別子。START_GAME / RETURN_TO_TITLE に付与される。
    /// 「どのプレイ（試行）のメッセージか」を両端末で共有するために使う。
    /// 旧フォーマット（識別子なし）では空文字。
    /// </summary>
    public string PlaySessionId { get; }

    /// <summary>
    /// RETURN_TO_TITLE の復帰モード。true = FAST（ボタン起点。受信側も短いフェードで合わせる）。
    /// 旧形式（FASTなし）は false。
    /// </summary>
    public bool ReturnToTitleFast { get; }

    /// <summary>RANK_SYNC のときだけ有効なスナップショット（それ以外は default）。</summary>
    public RankingSnapshot Snapshot { get; }

    /// <summary>RANK_SYNC_HELLO のときだけ有効な接続確認情報（それ以外は default）。</summary>
    public RankSyncHello Hello { get; }

    public ParentUdpMessage(
        ParentMessageType type,
        ChildGameResultType resultType = ChildGameResultType.Unknown,
        int score = 0,
        string rawPayload = null,
        string invalidValue = null,
        string parseError = null,
        string playSessionId = "",
        bool returnToTitleFast = false,
        RankingSnapshot snapshot = default,
        RankSyncHello hello = default)
    {
        Type = type;
        ResultType = resultType;
        Score = score;
        RawPayload = rawPayload;
        InvalidValue = invalidValue;
        ParseError = parseError;
        PlaySessionId = playSessionId ?? "";
        ReturnToTitleFast = returnToTitleFast;
        Snapshot = snapshot;
        Hello = hello;
    }
}

public static class ParentUdpMessageParser
{
    private const string MagicNumber = "TEAM13_";

    public static ParentUdpMessage Parse(string rawMessage)
    {
        if (string.IsNullOrEmpty(rawMessage) || !rawMessage.StartsWith(MagicNumber, StringComparison.Ordinal))
            return new ParentUdpMessage(ParentMessageType.Invalid, rawPayload: rawMessage);

        string payload = rawMessage.Substring(MagicNumber.Length);

        // DISCOVERY_REQUEST:<sourceId>:<sessionSeq> — 子機の接続元識別つき
        // （親機が「同じ子機のDiscovery再送」と「新しい接続確認」を区別するために使う）
        const string discoveryPrefix = "DISCOVERY_REQUEST:";
        if (payload.StartsWith(discoveryPrefix, StringComparison.Ordinal))
        {
            string[] d = payload.Split(':');
            if (d.Length == 3 && RankingSnapshot.IsValidId(d[1], allowEmpty: false)
                && int.TryParse(d[2], out int discoverySeq) && discoverySeq >= 0)
            {
                return new ParentUdpMessage(
                    ParentMessageType.DiscoveryRequest,
                    score: discoverySeq,
                    rawPayload: payload,
                    playSessionId: d[1]);
            }
            return new ParentUdpMessage(ParentMessageType.DiscoveryRequest, rawPayload: payload);
        }

        switch (payload)
        {
            case "PING":
                return new ParentUdpMessage(ParentMessageType.Ping, rawPayload: payload);
            case "DISCOVERY_REQUEST":
                return new ParentUdpMessage(ParentMessageType.DiscoveryRequest, rawPayload: payload);
            case "START_GAME":
                return new ParentUdpMessage(ParentMessageType.StartGame, rawPayload: payload);
            case "TIME_UP":
                return new ParentUdpMessage(ParentMessageType.TimeUp, rawPayload: payload);
            case "CHILD_DEAD":
                return new ParentUdpMessage(ParentMessageType.ChildDead, rawPayload: payload);
            case "LOADING_COMPLETE":
                return new ParentUdpMessage(ParentMessageType.LoadingComplete, rawPayload: payload);
            case "LOUD_ITEM":
                return new ParentUdpMessage(ParentMessageType.LoudItem, rawPayload: payload);
        }

        // RANK_SYNC_HELLO:<version>:<sourceId>:<sessionSeq> — 接続単位の同期元確認
        const string rankSyncHelloPrefix = "RANK_SYNC_HELLO:";
        if (payload.StartsWith(rankSyncHelloPrefix, StringComparison.Ordinal))
        {
            if (RankSyncHello.TryParse(payload, out RankSyncHello hello, out string helloError))
            {
                return new ParentUdpMessage(ParentMessageType.RankSyncHello, rawPayload: payload, hello: hello);
            }

            return new ParentUdpMessage(
                ParentMessageType.Invalid,
                rawPayload: payload,
                parseError: $"[ParentUdpSender] Invalid RANK_SYNC_HELLO: {helloError}");
        }

        // RANK_SYNC:<version>:<sourceId>:<updateNo>:<GOlast>:<GO0..GO4>:<TUlast>:<TU0..TU4>:<latestPlayId>:<latestType>
        // 子機を唯一の基準とする表示用スナップショット（親機は独自にランキングを更新しない）
        const string rankSyncPrefix = "RANK_SYNC:";
        if (payload.StartsWith(rankSyncPrefix, StringComparison.Ordinal))
        {
            if (RankingSnapshot.TryParse(payload, out RankingSnapshot snap, out string snapError))
            {
                return new ParentUdpMessage(ParentMessageType.RankingSync, rawPayload: payload, snapshot: snap);
            }

            return new ParentUdpMessage(
                ParentMessageType.Invalid,
                rawPayload: payload,
                parseError: $"[ParentUdpSender] Invalid RANK_SYNC: {snapError}");
        }

        // START_GAME:<playSessionId> — プレイ識別子付きの開始通知（旧: 引数なしの "START_GAME"）
        const string startGamePrefix = "START_GAME:";
        if (payload.StartsWith(startGamePrefix, StringComparison.Ordinal))
        {
            string sid = payload.Substring(startGamePrefix.Length);
            return new ParentUdpMessage(ParentMessageType.StartGame, rawPayload: payload, playSessionId: sid);
        }

        // RETURN_TO_TITLE:<playSessionId>:<seq>[:FAST] — プレイ識別子＋そのプレイ内の連番＋復帰モード。
        // 旧フォーマット "RETURN_TO_TITLE:<seq>"（識別子なし）も受け付ける。
        // 3つ目の値は "FAST" のみ許可し、それ以外の追加値は推測で処理せず不正として拒否する。
        const string returnToTitlePrefix = "RETURN_TO_TITLE:";
        if (payload.StartsWith(returnToTitlePrefix, StringComparison.Ordinal))
        {
            string rest = payload.Substring(returnToTitlePrefix.Length);
            string[] parts = rest.Split(':');

            string sid = "";
            string seqText;
            bool fast = false;

            if (parts.Length == 1)
            {
                // 旧形式: <seq>（識別子なし）
                seqText = parts[0];
            }
            else if (parts.Length == 2)
            {
                // <playSessionId>:<seq>
                sid = parts[0];
                seqText = parts[1];
            }
            else if (parts.Length == 3 && parts[2] == "FAST")
            {
                // <playSessionId>:<seq>:FAST（ボタン起点）
                sid = parts[0];
                seqText = parts[1];
                fast = true;
            }
            else
            {
                return new ParentUdpMessage(
                    ParentMessageType.Invalid,
                    rawPayload: payload,
                    parseError: $"[ParentUdpSender] Invalid RETURN_TO_TITLE format (only 'FAST' is allowed as 3rd field): '{payload}'");
            }

            if (!int.TryParse(seqText, out int seq))
            {
                return new ParentUdpMessage(
                    ParentMessageType.Invalid,
                    rawPayload: payload,
                    invalidValue: seqText,
                    parseError: $"[ParentUdpSender] Could not parse seq in RETURN_TO_TITLE: '{seqText}'");
            }

            return new ParentUdpMessage(
                ParentMessageType.TeamReturnToTitle,
                rawPayload: payload,
                score: seq,
                playSessionId: sid,
                returnToTitleFast: fast);
        }

        const string scorePrefix = "CHILD_SCORE:";
        if (!payload.StartsWith(scorePrefix, StringComparison.Ordinal))
        {
            // 片付け演出：ゲーム進行率通知 "GAME_PROGRESS:<0〜1を1000倍した整数>"
            const string progressPrefix = "GAME_PROGRESS:";
            if (payload.StartsWith(progressPrefix, StringComparison.Ordinal))
            {
                string progressText = payload.Substring(progressPrefix.Length);
                if (int.TryParse(progressText, out int progressMilli))
                {
                    return new ParentUdpMessage(
                        ParentMessageType.GameProgress,
                        score: progressMilli,
                        rawPayload: payload);
                }

                return new ParentUdpMessage(
                    ParentMessageType.Invalid,
                    rawPayload: payload,
                    parseError: $"[ParentUdpSender] Could not parse progress in GAME_PROGRESS: '{progressText}'");
            }

            return new ParentUdpMessage(ParentMessageType.Unknown, rawPayload: payload);
        }

        string scorePayload = payload.Substring(scorePrefix.Length);
        ChildGameResultType resultType;
        string resultPrefix;

        if (scorePayload.StartsWith("GAME_OVER:", StringComparison.Ordinal))
        {
            resultType = ChildGameResultType.GameOver;
            resultPrefix = "GAME_OVER:";
        }
        else if (scorePayload.StartsWith("TIME_UP:", StringComparison.Ordinal))
        {
            resultType = ChildGameResultType.TimeUp;
            resultPrefix = "TIME_UP:";
        }
        else
        {
            return new ParentUdpMessage(
                ParentMessageType.Invalid,
                rawPayload: payload,
                parseError: $"[ParentUdpSender] Unrecognised CHILD_SCORE format: '{scorePayload}'");
        }

        // CHILD_SCORE:<GAME_OVER|TIME_UP>:<score>[:<playSessionId>]
        // プレイIDは任意（旧形式: なし）。3つ目以降の追加値は拒否する。
        string remainder = scorePayload.Substring(resultPrefix.Length);
        string[] scoreParts = remainder.Split(':');
        if (scoreParts.Length > 2)
        {
            return new ParentUdpMessage(
                ParentMessageType.Invalid,
                resultType,
                rawPayload: payload,
                parseError: $"[ParentUdpSender] Invalid CHILD_SCORE format (too many fields): '{scorePayload}'");
        }

        string scoreText = scoreParts[0];
        string resultPlayId = scoreParts.Length == 2 ? scoreParts[1] : "";

        if (!int.TryParse(scoreText, out int score))
        {
            return new ParentUdpMessage(
                ParentMessageType.Invalid,
                resultType,
                rawPayload: payload,
                invalidValue: scoreText,
                parseError: $"[ParentUdpSender] Could not parse score in CHILD_SCORE: '{scoreText}'");
        }

        return new ParentUdpMessage(
            ParentMessageType.ChildScore,
            resultType,
            score,
            payload,
            playSessionId: resultPlayId);
    }
}

/// <summary>
/// 接続確認の応答（子機→親機）。
/// 形式: RANK_SYNC_HELLO:&lt;version&gt;:&lt;challengeId&gt;:&lt;sourceId&gt;:&lt;sessionSeq&gt;
/// 親機が発行した challengeId をそのまま返す。sessionSeq は診断用で、同期元の新旧判定には使わない。
/// </summary>
public readonly struct RankSyncHello
{
    public string ChallengeId { get; }
    public string SourceId { get; }
    public int SessionSeq { get; }

    private RankSyncHello(string challengeId, string sourceId, int sessionSeq)
    {
        ChallengeId = challengeId;
        SourceId = sourceId;
        SessionSeq = sessionSeq;
    }

    public static RankSyncHello Create(string challengeId, string sourceId, int sessionSeq)
        => new RankSyncHello(challengeId, sourceId, sessionSeq);

    public static string Format(string challengeId, string sourceId, int sessionSeq)
        => "RANK_SYNC_HELLO:" + RankingSnapshot.VersionName + ":" + challengeId + ":" + sourceId + ":" + sessionSeq;

    public static bool TryParse(string payload, out RankSyncHello hello, out string error)
    {
        hello = default;
        error = null;

        if (string.IsNullOrEmpty(payload)) { error = "empty payload"; return false; }

        string[] p = payload.Split(':');
        if (p.Length != 5) { error = "expected 5 fields, got " + p.Length; return false; }
        if (p[0] != "RANK_SYNC_HELLO") { error = "unexpected header '" + p[0] + "'"; return false; }
        if (p[1] != RankingSnapshot.VersionName) { error = "unsupported version '" + p[1] + "'"; return false; }
        if (!RankingSnapshot.IsValidId(p[2], allowEmpty: false)) { error = "invalid challengeId '" + p[2] + "'"; return false; }
        if (!RankingSnapshot.IsValidId(p[3], allowEmpty: false)) { error = "invalid sourceId '" + p[3] + "'"; return false; }
        if (!int.TryParse(p[4], out int seq) || seq < 0) { error = "invalid sessionSeq '" + p[4] + "'"; return false; }

        hello = new RankSyncHello(p[2], p[3], seq);
        return true;
    }
}

/// <summary>
/// 接続確認の要求（親機→子機）。形式: RANK_SYNC_CHALLENGE:&lt;version&gt;:&lt;challengeId&gt;
/// 新しい接続確認を始めるときだけ新しい ID を発行し、同じ確認要求の再送は同じ ID を使う。
/// </summary>
public readonly struct RankSyncChallenge
{
    public string ChallengeId { get; }

    private RankSyncChallenge(string challengeId)
    {
        ChallengeId = challengeId;
    }

    public static RankSyncChallenge Create(string challengeId) => new RankSyncChallenge(challengeId);

    public static string Format(string challengeId)
        => "RANK_SYNC_CHALLENGE:" + RankingSnapshot.VersionName + ":" + challengeId;

    public static bool TryParse(string payload, out RankSyncChallenge challenge, out string error)
    {
        challenge = default;
        error = null;

        if (string.IsNullOrEmpty(payload)) { error = "empty payload"; return false; }

        string[] p = payload.Split(':');
        if (p.Length != 3) { error = "expected 3 fields, got " + p.Length; return false; }
        if (p[0] != "RANK_SYNC_CHALLENGE") { error = "unexpected header '" + p[0] + "'"; return false; }
        if (p[1] != RankingSnapshot.VersionName) { error = "unsupported version '" + p[1] + "'"; return false; }
        if (!RankingSnapshot.IsValidId(p[2], allowEmpty: false)) { error = "invalid challengeId '" + p[2] + "'"; return false; }

        challenge = new RankSyncChallenge(p[2]);
        return true;
    }
}

/// <summary>
/// 子機→親機のランキング／スコア同期スナップショット（Unity非依存の純粋データ）。
/// 形式: RANK_SYNC:&lt;version&gt;:&lt;sourceId&gt;:&lt;updateNo&gt;:&lt;GOlast&gt;:&lt;GO0..GO4&gt;:&lt;TUlast&gt;:&lt;TU0..TU4&gt;:&lt;latestPlayId&gt;:&lt;latestType&gt;
/// ・GAME_OVER と TIME_UP は完全に別物として持つ（混ぜない）
/// ・TOP5 は降順（子機の並びをそのまま保持し、同点ルールは子機側に従う）
/// ・受信側は「スナップショット全体」を検証し、どこか1つでも不正なら全体を拒否する
/// </summary>
public readonly struct RankingSnapshot
{
    public const string VersionName = "v1";
    public const int TopSize = 5;
    public const int MaxScore = 999999;
    public const string TypeNone = "NONE";
    public const string TypeGameOver = "GAME_OVER";
    public const string TypeTimeUp = "TIME_UP";

    public string FormatVersion { get; }
    public string SourceId { get; }
    public int UpdateNo { get; }
    public int GameOverLastScore { get; }
    public int[] GameOverTop { get; }
    public int TimeUpLastScore { get; }
    public int[] TimeUpTop { get; }
    public string LatestResultPlayId { get; }
    public string LatestResultType { get; }

    private RankingSnapshot(string version, string sourceId, int updateNo,
                            int goLast, int[] goTop, int tuLast, int[] tuTop,
                            string latestPlayId, string latestType)
    {
        FormatVersion = version;
        SourceId = sourceId;
        UpdateNo = updateNo;
        GameOverLastScore = goLast;
        GameOverTop = goTop;
        TimeUpLastScore = tuLast;
        TimeUpTop = tuTop;
        LatestResultPlayId = latestPlayId ?? "";
        LatestResultType = string.IsNullOrEmpty(latestType) ? TypeNone : latestType;
    }

    /// <summary>送信側（子機）用の生成。</summary>
    public static RankingSnapshot Create(string sourceId, int updateNo,
                                         int goLast, int[] goTop, int tuLast, int[] tuTop,
                                         string latestPlayId, string latestType)
    {
        return new RankingSnapshot(VersionName, sourceId, updateNo, goLast, Copy5(goTop), tuLast, Copy5(tuTop),
                                   latestPlayId, string.IsNullOrEmpty(latestType) ? TypeNone : latestType);
    }

    public static string Format(string sourceId, int updateNo,
                                int goLast, int[] goTop, int tuLast, int[] tuTop,
                                string latestPlayId, string latestType)
    {
        return "RANK_SYNC:" + VersionName + ":" + sourceId + ":" + updateNo
             + ":" + goLast + ":" + Csv(goTop)
             + ":" + tuLast + ":" + Csv(tuTop)
             + ":" + (latestPlayId ?? "")
             + ":" + (string.IsNullOrEmpty(latestType) ? TypeNone : latestType);
    }

    private static string Csv(int[] a)
    {
        if (a == null) return "";
        string s = "";
        for (int i = 0; i < a.Length; i++)
        {
            if (i > 0) s += ",";
            s += a[i];
        }
        return s;
    }

    private static int[] Copy5(int[] a)
    {
        int[] r = new int[TopSize];
        if (a != null)
        {
            for (int i = 0; i < TopSize && i < a.Length; i++) r[i] = a[i];
        }
        return r;
    }

    /// <summary>IDは英数字・ハイフン・アンダースコアのみ（区切り文字 ':' と ',' を含まない）。</summary>
    public static bool IsValidId(string id, bool allowEmpty)
    {
        if (string.IsNullOrEmpty(id)) return allowEmpty;
        if (id.Length > 64) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '_';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>スナップショット全体を検証して取り込む。1つでも不正なら false（部分保存しない）。</summary>
    public static bool TryParse(string payload, out RankingSnapshot snapshot, out string error)
    {
        snapshot = default;
        error = null;

        if (string.IsNullOrEmpty(payload)) { error = "empty payload"; return false; }

        string[] parts = payload.Split(':');
        if (parts.Length != 10) { error = "expected 10 fields, got " + parts.Length; return false; }
        if (parts[0] != "RANK_SYNC") { error = "unexpected header '" + parts[0] + "'"; return false; }
        if (parts[1] != VersionName) { error = "unsupported version '" + parts[1] + "'"; return false; }
        if (!IsValidId(parts[2], allowEmpty: false)) { error = "invalid sourceId '" + parts[2] + "'"; return false; }

        if (!int.TryParse(parts[3], out int updateNo) || updateNo < 0)
        {
            error = "invalid updateNo '" + parts[3] + "'"; return false;
        }

        if (!TryParseScore(parts[4], out int goLast)) { error = "invalid GO last score '" + parts[4] + "'"; return false; }
        int[] goTop;
        if (!TryParseTop(parts[5], out goTop, out string goErr)) { error = "GO top5: " + goErr; return false; }

        if (!TryParseScore(parts[6], out int tuLast)) { error = "invalid TU last score '" + parts[6] + "'"; return false; }
        int[] tuTop;
        if (!TryParseTop(parts[7], out tuTop, out string tuErr)) { error = "TU top5: " + tuErr; return false; }

        string latestPlayId = parts[8];
        string latestType = parts[9];
        if (latestType != TypeNone && latestType != TypeGameOver && latestType != TypeTimeUp)
        {
            error = "invalid latestType '" + latestType + "'"; return false;
        }
        if (latestType == TypeNone)
        {
            // 最新結果なし。playId は空のみ許可（誤って古いプレイを紐付けない）
            if (!string.IsNullOrEmpty(latestPlayId)) { error = "latestPlayId must be empty when latestType=NONE"; return false; }
        }
        else if (!IsValidId(latestPlayId, allowEmpty: false))
        {
            error = "invalid latestPlayId '" + latestPlayId + "'"; return false;
        }

        snapshot = new RankingSnapshot(parts[1], parts[2], updateNo, goLast, goTop, tuLast, tuTop, latestPlayId, latestType);
        return true;
    }

    private static bool TryParseScore(string text, out int score)
    {
        score = 0;
        if (!int.TryParse(text, out int v)) return false;
        if (v < 0 || v > MaxScore) return false;
        score = v;
        return true;
    }

    /// <summary>TOP5: 件数ちょうど5・各点は0..MaxScore・降順（同点可）。</summary>
    private static bool TryParseTop(string csv, out int[] top, out string error)
    {
        top = null;
        error = null;

        string[] items = csv.Split(',');
        if (items.Length != TopSize) { error = "expected " + TopSize + " entries, got " + items.Length; return false; }

        int[] r = new int[TopSize];
        for (int i = 0; i < TopSize; i++)
        {
            if (!TryParseScore(items[i], out int v)) { error = "invalid score at index " + i + ": '" + items[i] + "'"; return false; }
            r[i] = v;
        }
        for (int i = 1; i < TopSize; i++)
        {
            if (r[i] > r[i - 1]) { error = "not sorted descending"; return false; }
        }

        top = r;
        return true;
    }
}

/// <summary>スナップショット適用結果。</summary>
public enum RankSyncApply
{
    Applied,        // 新しい更新を適用した
    Duplicate,      // 同じ更新・同じ内容（適用せず、ACKのみ返す）
    Mismatch,       // 同じ更新番号なのに内容が違う（適用せず、ACKも返さない＝保存成功と誤認させない）
    Stale,          // 古い更新（適用せず、ACKのみ）
    SourceChanged   // 同期元が切り替わった（接続確認済みの新ソース）
}

/// <summary>
/// 親機が保持する「子機データの表示用コピー」（Unity非依存の純粋クラス）。
/// 親機はこのコピーを表示するだけで、独自にランキングへ挿入・合体・修復・点数削除はしない。
/// </summary>
public sealed class RankSyncStore
{
    public bool HasData { get; private set; }
    public string SourceId { get; private set; } = "";
    public int UpdateNo { get; private set; } = -1;
    public int GameOverLastScore { get; private set; }
    public readonly int[] GameOverTop = new int[RankingSnapshot.TopSize];
    public int TimeUpLastScore { get; private set; }
    public readonly int[] TimeUpTop = new int[RankingSnapshot.TopSize];
    public string LatestResultPlayId { get; private set; } = "";
    public string LatestResultType { get; private set; } = RankingSnapshot.TypeNone;

    /// <summary>
    /// 同じ同期元の古い更新は適用しない。同じ更新番号でも内容が違えば Mismatch（適用しない・ACKも返さない）。
    /// 別の同期元は、接続確認（RankSyncPeerGate）を通した呼び出し側で SourceChanged として扱う。
    /// </summary>
    public RankSyncApply Apply(RankingSnapshot s)
    {
        if (HasData && s.SourceId == SourceId)
        {
            if (s.UpdateNo == UpdateNo)
            {
                return HasSameContentAs(s) ? RankSyncApply.Duplicate : RankSyncApply.Mismatch;
            }
            if (s.UpdateNo < UpdateNo) return RankSyncApply.Stale;
        }

        bool sourceChanged = HasData && s.SourceId != SourceId;

        SourceId = s.SourceId;
        UpdateNo = s.UpdateNo;
        GameOverLastScore = s.GameOverLastScore;
        Copy(s.GameOverTop, GameOverTop);
        TimeUpLastScore = s.TimeUpLastScore;
        Copy(s.TimeUpTop, TimeUpTop);
        LatestResultPlayId = s.LatestResultPlayId ?? "";
        LatestResultType = string.IsNullOrEmpty(s.LatestResultType) ? RankingSnapshot.TypeNone : s.LatestResultType;
        HasData = true;

        return sourceChanged ? RankSyncApply.SourceChanged : RankSyncApply.Applied;
    }

    /// <summary>保持中コピーと、渡されたスナップショットの内容が完全一致するか（更新番号は含めない）。</summary>
    public bool HasSameContentAs(RankingSnapshot s)
    {
        if (GameOverLastScore != s.GameOverLastScore) return false;
        if (TimeUpLastScore != s.TimeUpLastScore) return false;
        if ((LatestResultPlayId ?? "") != (s.LatestResultPlayId ?? "")) return false;
        if (LatestResultType != (string.IsNullOrEmpty(s.LatestResultType) ? RankingSnapshot.TypeNone : s.LatestResultType)) return false;
        if (!SameArray(GameOverTop, s.GameOverTop)) return false;
        if (!SameArray(TimeUpTop, s.TimeUpTop)) return false;
        return true;
    }

    private static bool SameArray(int[] a, int[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static void Copy(int[] src, int[] dst)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = (src != null && i < src.Length) ? src[i] : 0;
        }
    }

    /// <summary>PlayerPrefs 保存用の文字列（スナップショット形式と同じ）。</summary>
    public string Serialize()
    {
        return RankingSnapshot.Format(SourceId, UpdateNo, GameOverLastScore, GameOverTop,
                                      TimeUpLastScore, TimeUpTop, LatestResultPlayId, LatestResultType);
    }

    public bool Deserialize(string data)
    {
        if (string.IsNullOrEmpty(data)) return false;
        if (!RankingSnapshot.TryParse(data, out RankingSnapshot snap, out _)) return false;
        Apply(snap);
        return true;
    }
}

/// <summary>
/// 親機の「今接続している子機の同期元」を確定するゲート（Unity非依存）。
/// ・親機が接続確認用のランダムな challengeId を発行し、子機はそれをそのまま返す（RANK_SYNC_HELLO）
/// ・今待っている challengeId と一致する応答だけを受理する（古い接続の応答は sourceId・起動回数に関係なく拒否）
/// ・同じ確認要求の再送は同じ challengeId、本当に新しい接続確認のときだけ新 ID
/// ・別 sourceId 間で sessionSeq（起動回数）の大小比較はしない（診断用に保持するのみ）
/// ・確定済みの同じ相手の Discovery 再送では状態を壊さない
/// ・IPでは判定しない（同じ端末の初期化では同じIPを使う可能性があるため）
/// </summary>
public sealed class RankSyncPeerGate
{
    /// <summary>接続確認要求（チャレンジ）の最大送信回数。これを超えたら同期待ちとして扱う。</summary>
    public const int MaxChallengeSends = 6;

    /// <summary>今の接続で確定した同期元（未確定なら空）。</summary>
    public string ConnectedSourceId { get; private set; } = "";

    /// <summary>診断用: 直近で確定した起動世代。同期元の新旧判定には使わない。</summary>
    public int LastSessionSeq { get; private set; } = -1;

    /// <summary>診断用: 直近で確定した接続確認 ID。</summary>
    public string LastAcceptedChallengeId { get; private set; } = "";

    public string PendingChallengeId { get; private set; } = "";
    public string PendingSourceId { get; private set; } = "";
    public int PendingSessionSeq { get; private set; } = -1;
    public int ChallengeSendCount { get; private set; }

    public bool IsConfirmed => !string.IsNullOrEmpty(ConnectedSourceId);
    public bool HasPendingChallenge => !string.IsNullOrEmpty(PendingChallengeId);
    /// <summary>再送上限に達した（無期限に待たない）。</summary>
    public bool ChallengeExhausted => HasPendingChallenge && ChallengeSendCount >= MaxChallengeSends;

    public enum HelloResult { Accepted, Duplicate, NoPending, StaleResponse, Invalid }
    public enum DiscoveryResult { NewChallenge, ResentChallenge, AlreadyConfirmed }

    /// <summary>
    /// DISCOVERY_REQUEST 受信。送るべき challengeId を返す（空なら送らない）。
    /// 新しい接続確認を始めるときだけ新 ID を発行し、同じ相手の再送は同じ ID を使う。
    /// 既に確定済みの同じ相手（同じ sourceId・同じ起動世代）の再送では何もしない（確定状態を壊さない）。
    /// </summary>
    public DiscoveryResult OnDiscovery(string childSourceId, int childSessionSeq, out string challengeId)
    {
        challengeId = "";
        string src = childSourceId ?? "";

        if (!string.IsNullOrEmpty(src) && IsConfirmed && src == ConnectedSourceId && childSessionSeq == LastSessionSeq)
            return DiscoveryResult.AlreadyConfirmed;

        if (!string.IsNullOrEmpty(src) && HasPendingChallenge
            && src == PendingSourceId && childSessionSeq == PendingSessionSeq)
        {
            // 同じ確認要求の再送は同じ ID（子機が要求し直したときは同じ ID で応える）。
            // 自動再送は ChallengeSendCount で上限を決めるので、ここで無期限に押し続けはしない。
            challengeId = PendingChallengeId;
            return DiscoveryResult.ResentChallenge;
        }

        // 本当に新しい接続確認を始めるときだけ新IDを発行する（旧同期元のコピーは Current にしない）
        ConnectedSourceId = "";
        PendingSourceId = src;
        PendingSessionSeq = childSessionSeq;
        PendingChallengeId = Guid.NewGuid().ToString("N");
        ChallengeSendCount = 0;
        challengeId = PendingChallengeId;
        return DiscoveryResult.NewChallenge;
    }

    /// <summary>チャレンジを1回送ったことを記録（再送上限の管理）。</summary>
    public void NoteChallengeSent()
    {
        if (HasPendingChallenge) ChallengeSendCount++;
    }

    /// <summary>
    /// 接続確認の応答。今待っている challengeId と一致する応答だけを受理する。
    /// 古い接続の応答は sourceId・起動回数に関係なく拒否する（巻き戻さない）。
    /// </summary>
    public HelloResult OnHello(string challengeId, string sourceId, int sessionSeq)
    {
        if (!RankingSnapshot.IsValidId(challengeId, allowEmpty: false) ||
            !RankingSnapshot.IsValidId(sourceId, allowEmpty: false) || sessionSeq < 0)
            return HelloResult.Invalid;

        // 同じ接続確認への再応答（冪等）
        if (!string.IsNullOrEmpty(LastAcceptedChallengeId) && challengeId == LastAcceptedChallengeId
            && IsConfirmed && sourceId == ConnectedSourceId)
            return HelloResult.Duplicate;

        if (!HasPendingChallenge)
        {
            // 確認待ちが無いときの応答は、確定済みなら古い接続の遅延応答として拒否する。
            return IsConfirmed ? HelloResult.StaleResponse : HelloResult.NoPending;
        }
        if (challengeId != PendingChallengeId) return HelloResult.StaleResponse;

        ConnectedSourceId = sourceId;
        LastSessionSeq = sessionSeq;
        LastAcceptedChallengeId = challengeId;
        PendingChallengeId = "";
        PendingSourceId = "";
        PendingSessionSeq = -1;
        ChallengeSendCount = 0;
        return HelloResult.Accepted;
    }

    /// <summary>確定済みの接続相手と一致するスナップショットだけを適用対象にする。</summary>
    public bool AcceptsSnapshot(string sourceId)
        => IsConfirmed && !string.IsNullOrEmpty(sourceId) && sourceId == ConnectedSourceId;
}

/// <summary>親機の表示用コピーの状態（UIの理由表示に使用）。</summary>
public enum RankSyncDisplayState
{
    NoData,        // 初回同期前（一度も受信していない）
    PreviousData,  // 前回同期データ（未接続・または切替確認中）
    Current        // 同期完了（現在の接続相手の最新）
}

public static class RankSyncDisplay
{
    /// <summary>
    /// 表示状態の判定。
    /// Current（同期完了）は「現在の接続相手が確定済み」かつ「コピーの同期元が現在の接続相手と一致」かつ
    /// 「今回の接続でスナップショットを適用、または同一の保存コピーを確認済み」のときだけ。
    /// 別 sourceId の接続確認が成功しただけでは、前の sourceId のコピーを Current にしない。
    /// </summary>
    public static RankSyncDisplayState Evaluate(
        bool hasCopy, bool peerConfirmed, string peerSourceId, string copySourceId, bool verifiedThisConnection)
    {
        if (!hasCopy) return RankSyncDisplayState.NoData;
        if (!peerConfirmed || string.IsNullOrEmpty(peerSourceId)) return RankSyncDisplayState.PreviousData;
        if ((copySourceId ?? "") != peerSourceId) return RankSyncDisplayState.PreviousData;
        if (!verifiedThisConnection) return RankSyncDisplayState.PreviousData;
        return RankSyncDisplayState.Current;
    }

    /// <summary>
    /// スコア／ランキングを表示してよいか。
    /// Current なら状態ラベル（syncStateText）未割当でも表示する。
    /// PreviousData は値を伏せる方針を維持（ラベルがある場合のみ説明として表示可能）。
    /// </summary>
    public static bool ShouldShowValues(RankSyncDisplayState state, bool hasStateLabel)
        => state == RankSyncDisplayState.Current
           || (state == RankSyncDisplayState.PreviousData && hasStateLabel);

    /// <summary>表示状態が変わったときだけ UI へ更新通知を出す。</summary>
    public static bool ShouldNotify(RankSyncDisplayState previous, RankSyncDisplayState current)
        => previous != current;

    public static string ToLabel(RankSyncDisplayState state)
    {
        switch (state)
        {
            case RankSyncDisplayState.NoData: return "未同期";
            case RankSyncDisplayState.PreviousData: return "前回同期データ";
            default: return "同期完了";
        }
    }
}

/// <summary>子機側の同期状態の判定（純粋）。</summary>
public static class ChildRankSyncPolicy
{
    /// <summary>
    /// 未確認全文を「そのまま」再送できるか。
    /// sourceId・updateNo・形式が現在の保持状態と一致する全文があるときだけ true。
    /// </summary>
    public static bool CanResendVerbatim(string pendingText, string ourSourceId, int ourUpdateNo)
    {
        if (string.IsNullOrEmpty(pendingText)) return false;
        if (!RankingSnapshot.TryParse(pendingText, out RankingSnapshot snap, out _)) return false;
        return snap.SourceId == ourSourceId && snap.UpdateNo == ourUpdateNo;
    }

    /// <summary>
    /// ACKで未確認を解除してよいか。
    /// 「未確認である」「自分の同期元である」「今保持している更新番号と一致する」の3条件すべてが必要
    /// → 古いACKで新しい未確認更新を消さない。
    /// </summary>
    public static bool ShouldAcceptAck(string ackSourceId, int ackUpdateNo, string ourSourceId, int ourUpdateNo, bool confirmed)
    {
        if (confirmed) return false;
        if (ackSourceId != ourSourceId) return false;
        return ackUpdateNo == ourUpdateNo;
    }
}

/// <summary>親機の結果画面への遷移可否（純粋）。ランキングコピー更新と分離して判定する。</summary>
public static class RankSyncNavigation
{
    /// <summary>
    /// 遷移してよいのは「適用された同期（Applied / SourceChanged）」かつ
    /// 「接続確認済みの同期元」「非タイトル」「現在のプレイの最新結果」「捕獲確定の TIME_UP ではない」ときだけ。
    /// Stale / Duplicate / Mismatch では遷移しない。
    /// </summary>
    public static bool ShouldNavigate(RankSyncApply apply, bool onTitle, bool sourceAccepted,
                                      string latestPlayId, string latestType,
                                      string currentPlayId, string captureConfirmedPlayId)
    {
        if (apply != RankSyncApply.Applied && apply != RankSyncApply.SourceChanged) return false;
        if (onTitle) return false;
        if (!sourceAccepted) return false;
        if (string.IsNullOrEmpty(latestPlayId)) return false;
        if (string.IsNullOrEmpty(latestType) || latestType == RankingSnapshot.TypeNone) return false;
        if (string.IsNullOrEmpty(currentPlayId) || latestPlayId != currentPlayId) return false;
        if (latestType == RankingSnapshot.TypeTimeUp && latestPlayId == captureConfirmedPlayId) return false;
        return true;
    }
}