using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// GameDifficulty：
/// 親機・子機共通の難易度設定（EASY / NORMAL / HARD）とチュートリアル有無、
/// 難易度別パラメータ、難易度別ランキング保存、およびUDP設定同期を管理する（D-R01〜D-R05, D-F01〜D-F05, M-F04, M-F05）。
/// </summary>
public static class GameDifficulty
{
    public enum Level
    {
        Easy = 0,
        Normal = 1,
        Hard = 2,
    }

    public const string SyncPrefix = "SETTINGS_SYNC:";
    public const string TitleCmdPrefix = "TITLE_CMD:";

    private const string PrefKeyDifficulty = "GameDifficulty";
    private const string PrefKeyIncludeTutorial = "GameIncludeTutorial";
    private const string PrefKeyMigratedNormalRanking = "RankingMigratedToPerDifficulty_v1";

    private const string LegacyKeyGameOverScore = "LastGameOverScore";
    private const string LegacyKeyTimeUpScore = "LastTimeUpScore";
    private const string LegacyKeyGameOverRank = "GameOverRank_";
    private const string LegacyKeyTimeUpRank = "TimeUpRank_";
    public const int RankingSize = 5;

    private static bool _loaded;
    private static Level _current = Level.Normal;
    private static bool _includeTutorial = false;
    private static bool _applyingRemoteSync;

    /// <summary>難易度が変更されたときに発火する。</summary>
    public static event Action<Level> Changed;

    /// <summary>チュートリアル有無が変更されたときに発火する。</summary>
    public static event Action<bool> IncludeTutorialChanged;

    /// <summary>難易度・チュートリアル・言語・音量などの設定が変更または同期されたときに発火する。</summary>
    public static event Action OnSettingsChanged;

    /// <summary>現在の難易度の表示名（EASY / NORMAL / HARD）。</summary>
    public static string DisplayName => GetDisplayName(Current);

    public static Level Current
    {
        get
        {
            EnsureLoaded();
            return _current;
        }
    }

    /// <summary>チュートリアルモードが選択されているか（TUTORIALモード時のみ true、EASY/NORMAL/HARD では false）。</summary>
    public static bool IncludeTutorial
    {
        get
        {
            EnsureLoaded();
            return _includeTutorial;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        int savedDiff = PlayerPrefs.GetInt(PrefKeyDifficulty, (int)Level.Normal);
        _current = savedDiff switch
        {
            (int)Level.Easy => Level.Easy,
            (int)Level.Hard => Level.Hard,
            _ => Level.Normal
        };

        _includeTutorial = PlayerPrefs.GetInt(PrefKeyIncludeTutorial, 0) != 0;
        MigrateLegacyRankingIfNeeded();
    }

    public static void SetDifficulty(Level level, bool syncToPeer = true)
    {
        // EASY / NORMAL / HARD を明示選択した場合はチュートリアルモードを解除する
        if (!_applyingRemoteSync && _includeTutorial)
        {
            SetIncludeTutorial(false, syncToPeer: false);
        }
        Set(level, syncToPeer);
    }

    public static void Set(Level level, bool syncToPeer = true)
    {
        EnsureLoaded();
        bool changed = _current != level;
        _current = level;

        PlayerPrefs.SetInt(PrefKeyDifficulty, (int)_current);
        PlayerPrefs.Save();

        // アクティブ難易度のランキングを共通ミラーキーへ反映し、結果画面・親機同期をそろえる（D-Q03）。
        SyncActiveDifficultyRankingToMirrorKeys(notifyPeerRanking: syncToPeer && !_applyingRemoteSync);

        if (changed)
        {
            Changed?.Invoke(_current);
            if (!_applyingRemoteSync)
                OnSettingsChanged?.Invoke();
        }

        if (syncToPeer && !_applyingRemoteSync)
            SendSettingsSyncToPeer();
    }

    public static void SetIncludeTutorial(bool includeTutorial, bool syncToPeer = true)
    {
        EnsureLoaded();
        bool changed = _includeTutorial != includeTutorial;
        _includeTutorial = includeTutorial;

        PlayerPrefs.SetInt(PrefKeyIncludeTutorial, _includeTutorial ? 1 : 0);
        PlayerPrefs.Save();

        if (changed)
        {
            IncludeTutorialChanged?.Invoke(_includeTutorial);
            if (!_applyingRemoteSync)
                OnSettingsChanged?.Invoke();
        }

        if (syncToPeer && !_applyingRemoteSync)
            SendSettingsSyncToPeer();
    }

    public static void CycleNext()
    {
        Level next = Current switch
        {
            Level.Easy => Level.Normal,
            Level.Normal => Level.Hard,
            _ => Level.Easy
        };
        SetDifficulty(next, syncToPeer: true);
    }

    // ── 制限時間・時計スケール（D-Q01：EASY=4分 / NORMAL=6分 / HARD=8分、20:00〜24:00(12:00)共通）──

    /// <summary>現在の難易度の制限時間（秒）。EASY=240s(4分), NORMAL=360s(6分), HARD=480s(8分)。</summary>
    public static float TimeLimitSeconds => GetTimeLimitSeconds(Current);

    public static float GetTimeLimitSeconds(Level level)
    {
        return level switch
        {
            Level.Easy => 240f,
            Level.Hard => 480f,
            _ => 360f,
        };
    }

    /// <summary>時計の開始時刻（時）：20時（夜8時）スタート。</summary>
    public const int ClockStartHour = 20;

    /// <summary>時計の総進行時間（分）：240分（4時間＝20:00 → 24:00/00:00）。全難易度で同じスケール。</summary>
    public const float ClockGameMinutesTotal = 240f;

    // ── 親機側の難易度補正（D-F02 / D-Q02 / D-R05）──────────────────────────────

    /// <summary>HARDでは廊下の照明予兆を点灯しない（D-Q02確定仕様）。</summary>
    public static bool SuppressCorridorForeshadowLights => Current == Level.Hard;

    /// <summary>親イベントの発生間隔倍率（EASY=ゆっくり1.25x, NORMAL=1.0x, HARD=早め0.78x）。</summary>
    public static float ParentWarningIntervalMultiplier => Current switch
    {
        Level.Easy => 1.25f,
        Level.Hard => 0.78f,
        _ => 1.0f,
    };

    /// <summary>怪しさゲージの自然減衰速度倍率（EASY=1.25x, NORMAL=1.0x, HARD=0.5x）。</summary>
    public static float ParentSuspicionDecayMultiplier => Current switch
    {
        Level.Easy => 1.25f,
        Level.Hard => 0.50f,
        _ => 1.0f,
    };

    /// <summary>親の足音音量倍率（EASY=聞こえやすい1.25x, NORMAL=1.0x, HARD=やや小さめ0.75x）。</summary>
    public static float ParentFootstepVolumeMultiplier => Current switch
    {
        Level.Easy => 1.25f,
        Level.Hard => 0.75f,
        _ => 1.0f,
    };

    // ── 子機側の難易度補正（D-F03 / D-F04）────────────────────────────────────

    /// <summary>子機のスクロール・アイテム基本速度倍率（EASY=0.9x, NORMAL=1.0x, HARD=1.25x）。</summary>
    public static float ChildItemSpeedMultiplier => Current switch
    {
        Level.Easy => 0.90f,
        Level.Hard => 1.25f,
        _ => 1.00f,
    };

    /// <summary>悪いアイテムの出現重み倍率（EASY=0.8x, NORMAL=1.0x, HARD=1.5x）。</summary>
    public static float ChildBadItemWeightMultiplier => Current switch
    {
        Level.Easy => 0.80f,
        Level.Hard => 1.50f,
        _ => 1.00f,
    };

    /// <summary>EASYでは悪いアイテムを減点時計のみとし、サイレン（ItemType.BGM）を出さない。</summary>
    public static bool ChildAllowSirenItem => Current != Level.Easy;

    /// <summary>QTEの制限時間（秒）（EASY=6.0s, NORMAL=5.0s, HARD=4.2s）。</summary>
    public static float ChildQteTimeLimitSeconds => Current switch
    {
        Level.Easy => 6.0f,
        Level.Hard => 4.2f,
        _ => 5.0f,
    };

    /// <summary>QTEの入力スロット数（EASY=4個, NORMAL=6個, HARD=7個）。</summary>
    public static int ChildQteSlotCount => Current switch
    {
        Level.Easy => 4,
        Level.Hard => 7,
        _ => 6,
    };

    public static string GetDisplayName(Level level)
    {
        return level switch
        {
            Level.Easy => "EASY",
            Level.Hard => "HARD",
            _ => "NORMAL",
        };
    }

    public static string GetDescription(Level level)
    {
        return level switch
        {
            Level.Easy => "Time Limit: 4 min (20:00-00:00)\nLouder footsteps, no siren items, shorter QTE",
            Level.Hard => "Time Limit: 8 min (20:00-00:00)\nNo hallway warning lights, faster pace & longer QTE",
            _ => "Time Limit: 6 min (20:00-00:00)\nStandard balanced mode",
        };
    }

    // ── 難易度別ランキング管理（D-Q03）──────────────────────────────────────────

    public static string GetTimeUpRankPrefix(Level level) => $"TimeUpRank_{level}_";
    public static string GetGameOverRankPrefix(Level level) => $"GameOverRank_{level}_";
    public static string GetLastTimeUpScoreKey(Level level) => $"LastTimeUpScore_{level}";
    public static string GetLastGameOverScoreKey(Level level) => $"LastGameOverScore_{level}";

    private static void MigrateLegacyRankingIfNeeded()
    {
        if (PlayerPrefs.GetInt(PrefKeyMigratedNormalRanking, 0) != 0) return;

        bool hasNormal = false;
        for (int i = 0; i < RankingSize; i++)
        {
            if (PlayerPrefs.GetInt(GetTimeUpRankPrefix(Level.Normal) + i, 0) > 0)
            {
                hasNormal = true;
                break;
            }
        }

        if (!hasNormal)
        {
            for (int i = 0; i < RankingSize; i++)
            {
                int legacyTu = PlayerPrefs.GetInt(LegacyKeyTimeUpRank + i, 0);
                int legacyGo = PlayerPrefs.GetInt(LegacyKeyGameOverRank + i, 0);
                if (legacyTu > 0) PlayerPrefs.SetInt(GetTimeUpRankPrefix(Level.Normal) + i, legacyTu);
                if (legacyGo > 0) PlayerPrefs.SetInt(GetGameOverRankPrefix(Level.Normal) + i, legacyGo);
            }

            int lastTu = PlayerPrefs.GetInt(LegacyKeyTimeUpScore, 0);
            int lastGo = PlayerPrefs.GetInt(LegacyKeyGameOverScore, 0);
            if (lastTu > 0) PlayerPrefs.SetInt(GetLastTimeUpScoreKey(Level.Normal), lastTu);
            if (lastGo > 0) PlayerPrefs.SetInt(GetLastGameOverScoreKey(Level.Normal), lastGo);
        }

        PlayerPrefs.SetInt(PrefKeyMigratedNormalRanking, 1);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// 現在の難易度で結果を保存し、クリア時（かつチュートリアルモード以外）のみその難易度のTOP5ランキングへ登録する（M-R06, D-Q03）。
    /// チュートリアルモード（IncludeTutorial == true）では EASY / NORMAL / HARD の保存データを汚さず、結果画面はすべて 000000 とする。
    /// </summary>
    public static void RecordResultScore(bool isGameOver, int score)
    {
        EnsureLoaded();
        if (_includeTutorial)
        {
            // チュートリアルモードでは各難易度の最新スコア・ランキングには記録せず、共通ミラーキーのみ0にする
            PlayerPrefs.SetInt(LegacyKeyGameOverScore, 0);
            PlayerPrefs.SetInt(LegacyKeyTimeUpScore, 0);
            for (int i = 0; i < RankingSize; i++)
            {
                PlayerPrefs.SetInt(LegacyKeyGameOverRank + i, 0);
                PlayerPrefs.SetInt(LegacyKeyTimeUpRank + i, 0);
            }
            PlayerPrefs.Save();
            return;
        }

        Level level = _current;

        if (isGameOver)
        {
            PlayerPrefs.SetInt(GetLastGameOverScoreKey(level), score);
            PlayerPrefs.SetInt(LegacyKeyGameOverScore, score);
        }
        else
        {
            PlayerPrefs.SetInt(GetLastTimeUpScoreKey(level), score);
            PlayerPrefs.SetInt(LegacyKeyTimeUpScore, score);
            if (score > 0)
            {
                RankingUtil.InsertScoreToPlayerPrefs(GetTimeUpRankPrefix(level), score, RankingSize);
            }
        }

        SyncActiveDifficultyRankingToMirrorKeys(notifyPeerRanking: false);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// 現在選択されている難易度のランキング・最新得点を共通ミラーキー（TimeUpRank_0..4 等）へコピーする。
    /// チュートリアルモード中はミラーキーをすべて 0 にし、保存済みの各難易度データは保護する。
    /// </summary>
    public static void SyncActiveDifficultyRankingToMirrorKeys(bool notifyPeerRanking = false)
    {
        EnsureLoaded();
        if (_includeTutorial)
        {
            PlayerPrefs.SetInt(LegacyKeyGameOverScore, 0);
            PlayerPrefs.SetInt(LegacyKeyTimeUpScore, 0);
            for (int i = 0; i < RankingSize; i++)
            {
                PlayerPrefs.SetInt(LegacyKeyGameOverRank + i, 0);
                PlayerPrefs.SetInt(LegacyKeyTimeUpRank + i, 0);
            }
            PlayerPrefs.Save();
            return;
        }

        Level level = _current;

        int lastGo = PlayerPrefs.GetInt(GetLastGameOverScoreKey(level), 0);
        int lastTu = PlayerPrefs.GetInt(GetLastTimeUpScoreKey(level), 0);
        PlayerPrefs.SetInt(LegacyKeyGameOverScore, lastGo);
        PlayerPrefs.SetInt(LegacyKeyTimeUpScore, lastTu);

        for (int i = 0; i < RankingSize; i++)
        {
            int goRank = PlayerPrefs.GetInt(GetGameOverRankPrefix(level) + i, 0);
            int tuRank = PlayerPrefs.GetInt(GetTimeUpRankPrefix(level) + i, 0);
            PlayerPrefs.SetInt(LegacyKeyGameOverRank + i, goRank);
            PlayerPrefs.SetInt(LegacyKeyTimeUpRank + i, tuRank);
        }

        PlayerPrefs.Save();

        if (notifyPeerRanking)
        {
            ChildUdpRuntimeHost childHost = ChildUdpRuntimeHost.Running;
            if (childHost != null && childHost.IsRunning)
                childHost.NotifyRankingChanged(RankingSnapshot.TypeNone);
        }
    }

    public static int[] ReadTimeUpRanking(Level level)
    {
        EnsureLoaded();
        return RankingUtil.ReadFromPlayerPrefs(GetTimeUpRankPrefix(level), RankingSize);
    }

    /// <summary>全難易度（EASY / NORMAL / HARD）および共通ミラーキーのランキング・最新スコアのみを0にリセットする（M-Q04）。</summary>
    public static void ResetAllRankings()
    {
        EnsureLoaded();
        foreach (Level level in new[] { Level.Easy, Level.Normal, Level.Hard })
        {
            PlayerPrefs.SetInt(GetLastGameOverScoreKey(level), 0);
            PlayerPrefs.SetInt(GetLastTimeUpScoreKey(level), 0);
            for (int i = 0; i < RankingSize; i++)
            {
                PlayerPrefs.SetInt(GetGameOverRankPrefix(level) + i, 0);
                PlayerPrefs.SetInt(GetTimeUpRankPrefix(level) + i, 0);
            }
        }

        PlayerPrefs.SetInt(LegacyKeyGameOverScore, 0);
        PlayerPrefs.SetInt(LegacyKeyTimeUpScore, 0);
        for (int i = 0; i < RankingSize; i++)
        {
            PlayerPrefs.SetInt(LegacyKeyGameOverRank + i, 0);
            PlayerPrefs.SetInt(LegacyKeyTimeUpRank + i, 0);
        }

        PlayerPrefs.Save();
    }

    // ── UDP設定同期・親機タイトル遠隔操作（M-F04 / M-F05 / D-R04 / D-F05）──────

    public static string BuildSettingsSyncMessage()
    {
        EnsureLoaded();
        int diff = (int)_current;
        int tut = _includeTutorial ? 1 : 0;
        int lang = (int)GameLanguage.Current;

        float bgm = 1f;
        float se = 1f;
        if (AudioManager.Instance != null)
        {
            bgm = AudioManager.Instance.GetBgmVolume();
            se = AudioManager.Instance.GetSeVolume();
        }
        else if (ParentAudioSettings.Instance != null)
        {
            bgm = ParentAudioSettings.BgmVolume;
            se = ParentAudioSettings.SeVolume;
        }

        int bgmMilli = Mathf.Clamp(Mathf.RoundToInt(bgm * 1000f), 0, 1000);
        int seMilli = Mathf.Clamp(Mathf.RoundToInt(se * 1000f), 0, 1000);

        return $"{SyncPrefix}{diff}:{tut}:{lang}:{bgmMilli}:{seMilli}";
    }

    public static void SendSettingsSyncToPeer()
    {
        if (_applyingRemoteSync) return;

        string msg = BuildSettingsSyncMessage();

        ChildUdpRuntimeHost childHost = ChildUdpRuntimeHost.Running;
        if (childHost != null && childHost.IsRunning)
        {
            childHost.SendState(msg);
            return;
        }

        ParentUdpRuntimeHost parentHost = ParentUdpRuntimeHost.Running;
        if (parentHost != null && parentHost.IsRunning && parentHost.IsConnected)
        {
            parentHost.SendState(msg);
        }
    }

    public static void SendTitleCommandToParent(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        ChildUdpRuntimeHost childHost = ChildUdpRuntimeHost.Running;
        if (childHost != null && childHost.IsRunning)
        {
            childHost.SendState($"{TitleCmdPrefix}{command}");
        }
    }

    public static void ApplyRemoteSettingsSync(string payload)
    {
        if (string.IsNullOrEmpty(payload) || !payload.StartsWith(SyncPrefix, StringComparison.Ordinal))
            return;

        string rest = payload.Substring(SyncPrefix.Length);
        string[] parts = rest.Split(':');
        if (parts.Length < 3) return;

        _applyingRemoteSync = true;
        try
        {
            if (int.TryParse(parts[0], out int diffInt))
            {
                Level lv = diffInt switch
                {
                    0 => Level.Easy,
                    2 => Level.Hard,
                    _ => Level.Normal
                };
                Set(lv, syncToPeer: false);
            }

            if (int.TryParse(parts[1], out int tutInt))
            {
                SetIncludeTutorial(tutInt != 0, syncToPeer: false);
            }

            if (int.TryParse(parts[2], out int langInt))
            {
                GameLanguage.Set(langInt == (int)GameLanguage.Language.English
                    ? GameLanguage.Language.English
                    : GameLanguage.Language.Japanese);
            }

            if (parts.Length >= 5 &&
                int.TryParse(parts[3], out int bgmMilli) &&
                int.TryParse(parts[4], out int seMilli))
            {
                float bgm = Mathf.Clamp01(bgmMilli / 1000f);
                float se = Mathf.Clamp01(seMilli / 1000f);

                // 親機が実際に音を出すため、親機側の音量設定へも反映する（M-S01）。
                if (ParentAudioSettings.Instance != null)
                {
                    ParentAudioSettings.Instance.SetBgmVolume(bgm);
                    ParentAudioSettings.Instance.SetSeVolume(se);
                }

                if (AudioManager.Instance != null)
                {
                    AudioManager.Instance.SetBgmVolume(bgm);
                    AudioManager.Instance.SetSeVolume(se);
                }
            }

            Debug.Log($"[GameDifficulty] Applied remote SETTINGS_SYNC: diff={Current}, tutorial={IncludeTutorial}, lang={GameLanguage.Current}");
        }
        finally
        {
            _applyingRemoteSync = false;
        }

        OnSettingsChanged?.Invoke();
    }

    public static void HandleRemoteTitleCommandOnParent(string payload)
    {
        if (string.IsNullOrEmpty(payload) || !payload.StartsWith(TitleCmdPrefix, StringComparison.Ordinal))
            return;

        string cmd = payload.Substring(TitleCmdPrefix.Length).Trim();
        Debug.Log($"[GameDifficulty] Parent received remote TITLE_CMD: '{cmd}'");

        ParentVolumeSettingsPanel parentSettings = UnityEngine.Object.FindFirstObjectByType<ParentVolumeSettingsPanel>();

        switch (cmd)
        {
            case "OPEN_SETTINGS":
                if (parentSettings != null) parentSettings.Open();
                break;
            case "CLOSE_SETTINGS":
                if (parentSettings != null) parentSettings.Close();
                break;
            case "OPEN_DIFFICULTY":
            case "CLOSE_DIFFICULTY":
                // 親機側では難易度選択UIを一切表示しない仕様のため無視する
                break;
            case "CONNECT":
                ParentUdpRuntimeHost.Running?.OnConnectButtonClicked();
                break;
            case "SOLO_START":
                ParentUdpRuntimeHost.Running?.OnSoloStartButtonClicked();
                break;
        }
    }
}
