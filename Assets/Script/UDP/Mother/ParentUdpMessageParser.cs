using System;

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
    // 片付け演出用：子機が悪いアイテム（時間減少効果を持つ既存アイテム）を取得した通知
    BadItem,
    // 片付け演出用：子機のゲーム進行率（0〜1を1000倍した整数）通知
    GameProgress
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

    public ParentUdpMessage(
        ParentMessageType type,
        ChildGameResultType resultType = ChildGameResultType.Unknown,
        int score = 0,
        string rawPayload = null,
        string invalidValue = null,
        string parseError = null,
        string playSessionId = "")
    {
        Type = type;
        ResultType = resultType;
        Score = score;
        RawPayload = rawPayload;
        InvalidValue = invalidValue;
        ParseError = parseError;
        PlaySessionId = playSessionId ?? "";
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
            // 片付け演出：悪いアイテム取得通知（既存の LOUD_ITEM と同じ形式の単純メッセージ）
            case "BAD_ITEM":
                return new ParentUdpMessage(ParentMessageType.BadItem, rawPayload: payload);
        }

        // START_GAME:<playSessionId> — プレイ識別子付きの開始通知（旧: 引数なしの "START_GAME"）
        const string startGamePrefix = "START_GAME:";
        if (payload.StartsWith(startGamePrefix, StringComparison.Ordinal))
        {
            string sid = payload.Substring(startGamePrefix.Length);
            return new ParentUdpMessage(ParentMessageType.StartGame, rawPayload: payload, playSessionId: sid);
        }

        // RETURN_TO_TITLE:<playSessionId>:<seq> — プレイ識別子とそのプレイ内の連番。
        // 旧フォーマット "RETURN_TO_TITLE:<seq>" / "RETURN_TO_TITLE" も受け付ける（識別子なし扱い）。
        const string returnToTitlePrefix = "RETURN_TO_TITLE:";
        if (payload.StartsWith(returnToTitlePrefix, StringComparison.Ordinal))
        {
            string rest = payload.Substring(returnToTitlePrefix.Length);
            string sid = "";
            string seqText = rest;

            int sep = rest.IndexOf(':');
            if (sep >= 0)
            {
                sid = rest.Substring(0, sep);
                seqText = rest.Substring(sep + 1);
            }

            int seq = 0;
            if (!int.TryParse(seqText, out seq))
                seq = 0;

            return new ParentUdpMessage(ParentMessageType.TeamReturnToTitle, rawPayload: payload, score: seq, playSessionId: sid);
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

        string scoreText = scorePayload.Substring(resultPrefix.Length);
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
            payload);
    }
}
