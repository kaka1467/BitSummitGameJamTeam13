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
    LoudItem
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

    public ParentUdpMessage(
        ParentMessageType type,
        ChildGameResultType resultType = ChildGameResultType.Unknown,
        int score = 0,
        string rawPayload = null,
        string invalidValue = null,
        string parseError = null)
    {
        Type = type;
        ResultType = resultType;
        Score = score;
        RawPayload = rawPayload;
        InvalidValue = invalidValue;
        ParseError = parseError;
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
        }

        const string scorePrefix = "CHILD_SCORE:";
        if (!payload.StartsWith(scorePrefix, StringComparison.Ordinal))
            return new ParentUdpMessage(ParentMessageType.Unknown, rawPayload: payload);

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
