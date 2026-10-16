using System;
using System.Globalization;

/// <summary>
/// 子機の本編音イベント（親機で同じAudioClipをローカル再生するための1件）。
/// 音声データは送らない（IDと短い状態だけを送る）。
///
///  単発SE      : GAME_AUDIO_SE:&lt;playId&gt;:&lt;seq&gt;:&lt;soundId&gt;:&lt;volumeMilli&gt;
///  BGM等の状態 : GAME_AUDIO_STATE:&lt;playId&gt;:&lt;seq&gt;:&lt;soundId&gt;:&lt;playing&gt;:&lt;loop&gt;:&lt;volumeMilli&gt;
///
///  ・volumeMilli は 0〜1000（0〜1 を1000倍した整数）。
///  ・playId は子機のプレイ識別子（プレイごとに変わる）。seq はプレイ内の連番。
///  ・soundId=0 かつ playing=0 は「BGMなし（停止）」を表す。
/// </summary>
public readonly struct ParentGameAudioEvent
{
    public const string SePrefix = "GAME_AUDIO_SE:";
    public const string StatePrefix = "GAME_AUDIO_STATE:";

    /// <summary>プレイ識別子の最大長（想定外に長い文字列を弾くため）。</summary>
    private const int MaxPlayIdLength = 64;

    /// <summary>true = BGM等の状態（STATE形式）、false = 単発SE（SE形式）。</summary>
    public bool IsState { get; }

    /// <summary>子機のプレイ識別子。</summary>
    public string PlayId { get; }

    /// <summary>プレイ内のイベント連番（重複・順序逆転の判定に使う）。</summary>
    public int Seq { get; }

    /// <summary>音ID（GameAudioId）。</summary>
    public int SoundId { get; }

    /// <summary>再生中か（STATE形式のみ意味を持つ。SE形式は常に true）。</summary>
    public bool Playing { get; }

    /// <summary>ループするか（STATE形式のみ意味を持つ）。</summary>
    public bool Loop { get; }

    /// <summary>子機側の音量（0〜1）。</summary>
    public float Volume { get; }

    private ParentGameAudioEvent(bool isState, string playId, int seq, int soundId, bool playing, bool loop, float volume)
    {
        IsState = isState;
        PlayId = playId;
        Seq = seq;
        SoundId = soundId;
        Playing = playing;
        Loop = loop;
        Volume = volume;
    }

    /// <summary>
    /// GAME_AUDIO_SE / GAME_AUDIO_STATE を解析する。
    /// 失敗時は false を返し、error に理由を入れる（例外は投げない）。
    /// </summary>
    public static bool TryParse(string payload, out ParentGameAudioEvent evt, out string error)
    {
        evt = default;
        error = null;

        if (string.IsNullOrEmpty(payload))
        {
            error = "[ParentUdpReceiver] GAME_AUDIO: payload is empty";
            return false;
        }

        bool isState;
        string body;

        if (payload.StartsWith(StatePrefix, StringComparison.Ordinal))
        {
            isState = true;
            body = payload.Substring(StatePrefix.Length);
        }
        else if (payload.StartsWith(SePrefix, StringComparison.Ordinal))
        {
            isState = false;
            body = payload.Substring(SePrefix.Length);
        }
        else
        {
            error = $"[ParentUdpReceiver] GAME_AUDIO: unknown prefix: '{payload}'";
            return false;
        }

        string[] fields = body.Split(':');
        int expected = isState ? 6 : 4;
        if (fields.Length != expected)
        {
            error = $"[ParentUdpReceiver] GAME_AUDIO: field count {fields.Length} (expected {expected}): '{payload}'";
            return false;
        }

        string playId = fields[0];
        if (string.IsNullOrEmpty(playId) || playId.Length > MaxPlayIdLength)
        {
            error = $"[ParentUdpReceiver] GAME_AUDIO: invalid playId: '{payload}'";
            return false;
        }

        if (!TryParseInt(fields[1], out int seq) || seq < 0)
        {
            error = $"[ParentUdpReceiver] GAME_AUDIO: invalid seq: '{fields[1]}' in '{payload}'";
            return false;
        }

        if (!TryParseInt(fields[2], out int soundId) || soundId < 0)
        {
            error = $"[ParentUdpReceiver] GAME_AUDIO: invalid soundId: '{fields[2]}' in '{payload}'";
            return false;
        }

        bool playing = true;
        bool loop = false;

        if (isState)
        {
            if (!TryParseFlag(fields[3], out playing))
            {
                error = $"[ParentUdpReceiver] GAME_AUDIO: invalid playing: '{fields[3]}' in '{payload}'";
                return false;
            }

            if (!TryParseFlag(fields[4], out loop))
            {
                error = $"[ParentUdpReceiver] GAME_AUDIO: invalid loop: '{fields[4]}' in '{payload}'";
                return false;
            }
        }

        int volumeIndex = fields.Length - 1;
        if (!TryParseInt(fields[volumeIndex], out int volumeMilli) || volumeMilli < 0 || volumeMilli > 1000)
        {
            error = $"[ParentUdpReceiver] GAME_AUDIO: invalid volume: '{fields[volumeIndex]}' in '{payload}'";
            return false;
        }

        evt = new ParentGameAudioEvent(isState, playId, seq, soundId, playing, loop, volumeMilli / 1000f);
        return true;
    }

    private static bool TryParseInt(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseFlag(string text, out bool value)
    {
        if (text == "1") { value = true; return true; }
        if (text == "0") { value = false; return true; }
        value = false;
        return false;
    }
}
