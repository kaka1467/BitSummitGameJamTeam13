using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
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
            Level.Easy => GameLanguage.Pick(
                "制限時間 4分（20:00〜00:00）\n足音が聞こえやすく、サイレンが出ない入門モード",
                "Time Limit: 4 min (20:00-00:00)\nLouder footsteps, no siren items, shorter QTE"),
            Level.Hard => GameLanguage.Pick(
                "制限時間 8分（20:00〜00:00）\n廊下の照明予兆なし・親の接近とスクロールが速い高難度モード",
                "Time Limit: 8 min (20:00-00:00)\nNo hallway warning lights, faster pace & longer QTE"),
            _ => GameLanguage.Pick(
                "制限時間 6分（20:00〜00:00）\n標準のバランスで遊べる通常モード",
                "Time Limit: 6 min (20:00-00:00)\nStandard balanced mode"),
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
    /// 保存後、親機とのRANK_SYNC互換のため現在難易度のランキングを共通ミラーキーへコピーする。
    /// </summary>
    public static void RecordResultScore(bool isGameOver, int score)
    {
        EnsureLoaded();
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
            if (!_includeTutorial && score > 0)
            {
                RankingUtil.InsertScoreToPlayerPrefs(GetTimeUpRankPrefix(level), score, RankingSize);
            }
        }

        SyncActiveDifficultyRankingToMirrorKeys(notifyPeerRanking: false);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// 現在選択されている難易度のランキング・最新得点を共通ミラーキー（TimeUpRank_0..4 等）へコピーする。
    /// これにより既存の RANK_SYNC パケットで親機の ResultScoreUI にも現在難易度のランキングがそのまま同期される。
    /// </summary>
    public static void SyncActiveDifficultyRankingToMirrorKeys(bool notifyPeerRanking = false)
    {
        EnsureLoaded();
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
        DifficultyTitleUI diffUi = UnityEngine.Object.FindFirstObjectByType<DifficultyTitleUI>();

        switch (cmd)
        {
            case "OPEN_SETTINGS":
                if (parentSettings != null) parentSettings.Open();
                break;
            case "CLOSE_SETTINGS":
                if (parentSettings != null) parentSettings.Close();
                break;
            case "OPEN_DIFFICULTY":
                if (diffUi != null) diffUi.OpenPanel(fromRemote: true);
                break;
            case "CLOSE_DIFFICULTY":
                if (diffUi != null) diffUi.ClosePanel(fromRemote: true);
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

/// <summary>
/// DifficultyTitleUI：
/// タイトル画面（ChildTitle / ParentTitle）にモード選択UI（EASY / NORMAL / HARD / TUTORIAL）を表示する。
/// ・EASY / NORMAL / HARD を選んだ場合はチュートリアルなし（IncludeTutorial = false）で本編を開始する。
/// ・TUTORIAL を選んだ場合はチュートリアルモード（IncludeTutorial = true）として子機・親機チュートリアルを実行する。
/// </summary>
public class DifficultyTitleUI : MonoBehaviour
{
    private static readonly Dictionary<string, Sprite> s_spriteCache = new Dictionary<string, Sprite>();

    public static DifficultyTitleUI Instance { get; private set; }

    /// <summary>難易度選択モーダルが開いているか（MenuNavigationController 等の背景入力抑止用）。</summary>
    public static bool IsAnyModalOpen => Instance != null && Instance._isOpen;

    public bool IsOpen => _isOpen;

    [Header("UI References (未設定時は自動生成または子階層から自動取得)")]
    [SerializeField] private GameObject _badgeRoot;
    [SerializeField] private TextMeshProUGUI _badgeLabel;
    [SerializeField] private GameObject _modalRoot;
    [SerializeField] private GameObject _detailPopupRoot;
    [SerializeField] private Image _detailCardImage;
    [SerializeField] private TextMeshProUGUI _detailDescText;
    [SerializeField] private TextMeshProUGUI _detailOkLabel;
    [SerializeField] private TextMeshProUGUI _detailBackLabel;
    [SerializeField] private Image _detailOkSelectedOutline;
    [SerializeField] private Image _detailBackSelectedOutline;
    [SerializeField] private TextMeshProUGUI _closeModalBtnLabel;
    [SerializeField] private Image[] _cardSelectionOutlines = new Image[3];
    [SerializeField] private Image _tutorialSelectionOutline;
    [SerializeField] private TextMeshProUGUI _tutorialStateText;

    [Header("Layout Settings - Badge (右上のモード表示ボタン)")]
    [SerializeField] private int _badgeSortingOrder = 60;
    [SerializeField] private Vector2 _badgeAnchoredPosition = new Vector2(-36f, -28f);
    [SerializeField] private Vector2 _badgeSize = new Vector2(420f, 76f);
    [SerializeField] private float _badgeFontSize = 22f;

    [Header("Layout Settings - Modal & Difficulty Cards")]
    [SerializeField] private int _modalSortingOrder = 100;
    [SerializeField] private Vector2 _titleAnchoredPosition = new Vector2(0f, 380f);
    [SerializeField] private Vector2 _titleSize = new Vector2(900f, 70f);
    [SerializeField] private float _titleFontSize = 42f;
    [SerializeField] private float[] _cardAnchoredX = new float[] { -420f, 0f, 420f };
    [SerializeField] private float _cardAnchoredY = 60f;
    [SerializeField] private Vector2 _cardSize = new Vector2(320f, 410f);

    [Header("Layout Settings - Tutorial Button & Back Button")]
    [SerializeField] private Vector2 _tutorialBtnAnchoredPosition = new Vector2(0f, -260f);
    [SerializeField] private Vector2 _tutorialBtnSize = new Vector2(620f, 150f);
    [SerializeField] private Vector2 _tutorialStateAnchoredPosition = new Vector2(0f, -365f);
    [SerializeField] private Vector2 _tutorialStateSize = new Vector2(900f, 50f);
    [SerializeField] private float _tutorialStateFontSize = 24f;
    [SerializeField] private Vector2 _closeBtnAnchoredPosition = new Vector2(0f, -445f);
    [SerializeField] private Vector2 _closeBtnSize = new Vector2(320f, 64f);
    [SerializeField] private float _closeBtnFontSize = 26f;

    [Header("Layout Settings - Detail Popup (確認ポップアップ)")]
    [SerializeField] private Vector2 _detailCardAnchoredPosition = Vector2.zero;
    [SerializeField] private Vector2 _detailCardSize = new Vector2(1280f, 720f);
    [SerializeField] private Vector2 _detailDescAnchoredPosition = new Vector2(150f, 40f);
    [SerializeField] private Vector2 _detailDescSize = new Vector2(680f, 220f);
    [SerializeField] private float _detailDescFontSize = 28f;
    [SerializeField] private Vector2 _detailOkAnchoredPosition = new Vector2(-16f, -152f);
    [SerializeField] private Vector2 _detailBackAnchoredPosition = new Vector2(316f, -152f);
    [SerializeField] private Vector2 _detailActionBtnSize = new Vector2(250f, 84f);
    [SerializeField] private float _detailActionFontSize = 32f;

    private bool _isOpen;
    private bool _isDetailOpen;
    private bool _isTutorialDetailPopup;
    private GameDifficulty.Level _pendingLevel = GameDifficulty.Level.Normal;
    private int _cursorIndex = 1; // 0=Easy, 1=Normal, 2=Hard, 3=TutorialMode
    private int _detailCursorIndex = 0; // 0=OK, 1=BACK
    private int _openedFrame = -1;
    private int _detailOpenedFrame = -1;
    private float _lastNavTime;
    private Action _pendingGameStartCallback;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapOnLoad()
    {
        SceneManager.sceneLoaded -= OnSceneLoadedStatic;
        SceneManager.sceneLoaded += OnSceneLoadedStatic;
        TryAttachToActiveScene(SceneManager.GetActiveScene().name);
    }

    private static void OnSceneLoadedStatic(Scene scene, LoadSceneMode mode)
    {
        TryAttachToActiveScene(scene.name);
    }

    public static DifficultyTitleUI EnsureAttached()
    {
        DifficultyTitleUI existing = FindFirstObjectByType<DifficultyTitleUI>();
        if (existing != null) return existing;

        TryAttachToActiveScene(SceneManager.GetActiveScene().name);
        return FindFirstObjectByType<DifficultyTitleUI>();
    }

    private static void TryAttachToActiveScene(string sceneName)
    {
        if (sceneName != "ChildTitle" && sceneName != "ParentTitle") return;
        if (FindFirstObjectByType<DifficultyTitleUI>() != null) return;

        Canvas targetCanvas = FindMainUiCanvas();
        if (targetCanvas == null) return;

        var hostGo = new GameObject("DifficultyTitleUI", typeof(RectTransform));
        hostGo.transform.SetParent(targetCanvas.transform, false);
        RectTransform hostRt = hostGo.GetComponent<RectTransform>();
        hostRt.anchorMin = Vector2.zero;
        hostRt.anchorMax = Vector2.one;
        hostRt.offsetMin = Vector2.zero;
        hostRt.offsetMax = Vector2.zero;
        hostGo.AddComponent<DifficultyTitleUI>();
    }

    private static Canvas FindMainUiCanvas()
    {
        Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
        Canvas best = null;
        int bestScore = int.MinValue;

        foreach (Canvas c in canvases)
        {
            if (c == null || !c.isActiveAndEnabled) continue;
            int score = 0;
            if (c.isRootCanvas) score += 10;
            if (c.GetComponent<GraphicRaycaster>() != null) score += 20;
            if (c.GetComponent<CanvasScaler>() != null) score += 10;
            if (c.GetComponentsInChildren<Button>(true).Length > 0) score += 30;
            if (string.Equals(c.gameObject.name, "Canvas", StringComparison.OrdinalIgnoreCase)) score += 15;
            if (string.Equals(c.gameObject.name, "Title", StringComparison.OrdinalIgnoreCase)) score -= 25;

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best != null ? best : FindFirstObjectByType<Canvas>();
    }

    public static Sprite LoadResourceSprite(string resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath)) return null;
        if (s_spriteCache.TryGetValue(resourcePath, out Sprite cached) && cached != null)
            return cached;

        Sprite sp = Resources.Load<Sprite>(resourcePath);
        if (sp == null)
        {
            Texture2D tex = Resources.Load<Texture2D>(resourcePath);
            if (tex != null)
            {
                sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
        }

        if (sp != null) s_spriteCache[resourcePath] = sp;
        return sp;
    }

    private void Awake()
    {
        Instance = this;
        BuildUI();
        GameDifficulty.Changed += OnDifficultyChanged;
        GameDifficulty.IncludeTutorialChanged += OnTutorialChanged;
        GameLanguage.Changed += OnLanguageChanged;
        RefreshVisuals();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        GameDifficulty.Changed -= OnDifficultyChanged;
        GameDifficulty.IncludeTutorialChanged -= OnTutorialChanged;
        GameLanguage.Changed -= OnLanguageChanged;
    }

    private void OnDifficultyChanged(GameDifficulty.Level _) => RefreshVisuals();
    private void OnTutorialChanged(bool _) => RefreshVisuals();
    private void OnLanguageChanged(GameLanguage.Language _) => RefreshVisuals();

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        Gamepad gp = Gamepad.current;

        if (!_isOpen)
        {
            bool openShortcut = (kb != null && kb.tabKey.wasPressedThisFrame) ||
                                (gp != null && gp.buttonNorth.wasPressedThisFrame);
            if (openShortcut)
            {
                OpenPanel();
                return;
            }
            return;
        }

        if (Time.frameCount <= _openedFrame)
            return;

        if (_isDetailOpen)
        {
            if (Time.frameCount <= _detailOpenedFrame)
                return;

            if (Time.unscaledTime - _lastNavTime > 0.16f)
            {
                int moveX = 0;
                if (kb != null)
                {
                    if (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame) moveX = -1;
                    else if (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame) moveX = 1;
                }
                if (gp != null)
                {
                    if (gp.dpad.left.wasPressedThisFrame || gp.leftStick.x.ReadValue() < -0.55f) moveX = -1;
                    else if (gp.dpad.right.wasPressedThisFrame || gp.leftStick.x.ReadValue() > 0.55f) moveX = 1;
                }

                if (moveX != 0)
                {
                    _detailCursorIndex = (_detailCursorIndex + 1) % 2;
                    _lastNavTime = Time.unscaledTime;
                    RefreshDetailSelectionVisuals();
                }
            }

            bool confirm = (kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)) ||
                           (gp != null && gp.buttonSouth.wasPressedThisFrame);
            bool cancel = (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame)) ||
                          (gp != null && gp.buttonEast.wasPressedThisFrame);

            if (confirm)
            {
                if (_detailCursorIndex == 0)
                    ConfirmDetailSelection();
                else
                    CloseDetailPopup();
            }
            else if (cancel)
            {
                CloseDetailPopup();
            }
            return;
        }

        if (Time.unscaledTime - _lastNavTime > 0.18f)
        {
            int moveX = 0;
            int moveY = 0;

            if (kb != null)
            {
                if (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame) moveX = -1;
                else if (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame) moveX = 1;
                if (kb.upArrowKey.wasPressedThisFrame || kb.wKey.wasPressedThisFrame) moveY = -1;
                else if (kb.downArrowKey.wasPressedThisFrame || kb.sKey.wasPressedThisFrame) moveY = 1;
            }

            if (gp != null)
            {
                if (gp.dpad.left.wasPressedThisFrame || gp.leftStick.x.ReadValue() < -0.55f) moveX = -1;
                else if (gp.dpad.right.wasPressedThisFrame || gp.leftStick.x.ReadValue() > 0.55f) moveX = 1;
                if (gp.dpad.up.wasPressedThisFrame || gp.leftStick.y.ReadValue() > 0.55f) moveY = -1;
                else if (gp.dpad.down.wasPressedThisFrame || gp.leftStick.y.ReadValue() < -0.55f) moveY = 1;
            }

            if (moveX != 0)
            {
                if (_cursorIndex < 3)
                    _cursorIndex = (_cursorIndex + moveX + 3) % 3;
                _lastNavTime = Time.unscaledTime;
                RefreshVisuals();
            }
            else if (moveY != 0)
            {
                _cursorIndex = _cursorIndex < 3 ? 3 : (int)GameDifficulty.Current;
                _lastNavTime = Time.unscaledTime;
                RefreshVisuals();
            }
        }

        bool submit = (kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)) ||
                      (gp != null && gp.buttonSouth.wasPressedThisFrame);
        bool close = (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame)) ||
                     (gp != null && (gp.buttonEast.wasPressedThisFrame || gp.buttonNorth.wasPressedThisFrame));

        if (submit)
        {
            if (_cursorIndex == 3)
                OpenTutorialDetailPopup();
            else
                OpenDetailPopup((GameDifficulty.Level)_cursorIndex);
        }
        else if (close)
        {
            ClosePanel();
        }
    }

    public void OpenForGameStart(Action onConfirmed)
    {
        _pendingGameStartCallback = onConfirmed;
        OpenPanel(fromRemote: false, keepPendingCallback: true);
    }

    public void OpenPanel(bool fromRemote = false, bool keepPendingCallback = false)
    {
        if (_modalRoot == null) return;
        if (!keepPendingCallback)
            _pendingGameStartCallback = null;

        _isOpen = true;
        _isDetailOpen = false;
        _isTutorialDetailPopup = false;
        _openedFrame = Time.frameCount;
        _cursorIndex = GameDifficulty.IncludeTutorial ? 3 : (int)GameDifficulty.Current;
        if (_detailPopupRoot != null) _detailPopupRoot.SetActive(false);
        _modalRoot.SetActive(true);
        _modalRoot.transform.SetAsLastSibling();
        RefreshVisuals();

        if (!fromRemote)
            GameDifficulty.SendTitleCommandToParent("OPEN_DIFFICULTY");
    }

    public void ClosePanel(bool fromRemote = false)
    {
        _isOpen = false;
        _isDetailOpen = false;
        _isTutorialDetailPopup = false;
        _pendingGameStartCallback = null;
        if (_detailPopupRoot != null) _detailPopupRoot.SetActive(false);
        if (_modalRoot != null) _modalRoot.SetActive(false);
        RefreshVisuals();

        if (!fromRemote)
            GameDifficulty.SendTitleCommandToParent("CLOSE_DIFFICULTY");
    }

    private void OpenDetailPopup(GameDifficulty.Level level)
    {
        _cursorIndex = (int)level;
        _pendingLevel = level;

        if (_detailPopupRoot == null) return;
        _isDetailOpen = true;
        _isTutorialDetailPopup = false;
        _detailCursorIndex = 0;
        _detailOpenedFrame = Time.frameCount;

        string spritePath = level switch
        {
            GameDifficulty.Level.Easy => "Difficulty/ui_difficulty_easy",
            GameDifficulty.Level.Hard => "Difficulty/ui_difficulty_hard",
            _ => "Difficulty/ui_difficulty_normal"
        };

        if (_detailCardImage != null)
            _detailCardImage.sprite = LoadResourceSprite(spritePath);

        if (_detailDescText != null)
        {
            _detailDescText.text = GameDifficulty.GetDescription(level);
            _detailDescText.color = Color.white;
        }

        if (_detailOkLabel != null)
        {
            _detailOkLabel.text = "OK";
            _detailOkLabel.color = new Color(0.05f, 0.02f, 0.15f, 1f);
        }

        if (_detailBackLabel != null)
        {
            _detailBackLabel.text = "BACK";
            _detailBackLabel.color = new Color(0.05f, 0.02f, 0.15f, 1f);
        }

        _detailPopupRoot.SetActive(true);
        RefreshDetailSelectionVisuals();
        RefreshVisuals();
    }

    private void OpenTutorialDetailPopup()
    {
        _cursorIndex = 3;
        if (_detailPopupRoot == null)
        {
            GameDifficulty.SetIncludeTutorial(true);
            return;
        }

        _isDetailOpen = true;
        _isTutorialDetailPopup = true;
        _detailCursorIndex = 0;
        _detailOpenedFrame = Time.frameCount;

        if (_detailCardImage != null)
            _detailCardImage.sprite = LoadResourceSprite("Difficulty/ui_tutorial_desc");

        if (_detailDescText != null)
        {
            _detailDescText.text = GameLanguage.Pick(
                "チュートリアル専用モードです。\n子機と親機の操作を順番に練習します（クリア時スコアは0）。",
                "Tutorial Mode.\nPractice both Child and Parent controls step by step.");
            _detailDescText.color = new Color(0.05f, 0.02f, 0.18f, 1f);
        }

        if (_detailOkLabel != null)
        {
            _detailOkLabel.text = "OK";
            _detailOkLabel.color = Color.white;
        }

        if (_detailBackLabel != null)
        {
            _detailBackLabel.text = "BACK";
            _detailBackLabel.color = Color.white;
        }

        _detailPopupRoot.SetActive(true);
        RefreshDetailSelectionVisuals();
        RefreshVisuals();
    }

    private void CloseDetailPopup()
    {
        _isDetailOpen = false;
        _isTutorialDetailPopup = false;
        if (_detailPopupRoot != null)
            _detailPopupRoot.SetActive(false);
        RefreshVisuals();
    }

    private void OnDetailBackButtonClicked()
    {
        _detailCursorIndex = 1;
        CloseDetailPopup();
    }

    private void ConfirmDetailSelection()
    {
        if (_isTutorialDetailPopup)
        {
            // TUTORIAL モードのみチュートリアルを実行する
            GameDifficulty.SetIncludeTutorial(true);
        }
        else
        {
            // EASY / NORMAL / HARD ではチュートリアルを流さず、そのまま本編を開始する
            GameDifficulty.Set(_pendingLevel);
            GameDifficulty.SetIncludeTutorial(false);
        }

        Action startCallback = _pendingGameStartCallback;
        _pendingGameStartCallback = null;

        _isDetailOpen = false;
        _isTutorialDetailPopup = false;
        if (_detailPopupRoot != null)
            _detailPopupRoot.SetActive(false);

        ClosePanel();
        startCallback?.Invoke();
    }

    private void RefreshDetailSelectionVisuals()
    {
        if (_detailOkSelectedOutline != null)
            _detailOkSelectedOutline.gameObject.SetActive(_detailCursorIndex == 0);
        if (_detailBackSelectedOutline != null)
            _detailBackSelectedOutline.gameObject.SetActive(_detailCursorIndex == 1);
    }

    [ContextMenu("難易度UIをシーン上に生成（Unity内で配置・サイズ調整用）")]
    public void BuildUIInSceneForEditing()
    {
        BuildUI(bindRuntimeListeners: Application.isPlaying);
        RefreshVisuals();
    }

    private void BuildUI(bool bindRuntimeListeners = true)
    {
        TMP_FontAsset sharedFont = FindFirstObjectByType<TextMeshProUGUI>()?.font;

        if (_badgeRoot == null)
        {
            Transform existingBadge = transform.Find("DifficultyBadgeButton");
            if (existingBadge != null) _badgeRoot = existingBadge.gameObject;
        }

        if (_badgeRoot == null)
        {
            _badgeRoot = new GameObject("DifficultyBadgeButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(Canvas), typeof(GraphicRaycaster));
            _badgeRoot.transform.SetParent(transform, false);
            Canvas badgeCanvas = _badgeRoot.GetComponent<Canvas>();
            badgeCanvas.overrideSorting = true;
            badgeCanvas.sortingOrder = _badgeSortingOrder;

            RectTransform badgeRt = _badgeRoot.GetComponent<RectTransform>();
            badgeRt.anchorMin = new Vector2(1f, 1f);
            badgeRt.anchorMax = new Vector2(1f, 1f);
            badgeRt.pivot = new Vector2(1f, 1f);
            badgeRt.anchoredPosition = _badgeAnchoredPosition;
            badgeRt.sizeDelta = _badgeSize;

            Image badgeBg = _badgeRoot.GetComponent<Image>();
            badgeBg.color = new Color(0.08f, 0.05f, 0.22f, 0.92f);

            var badgeTextGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            badgeTextGo.transform.SetParent(_badgeRoot.transform, false);
            RectTransform labelRt = badgeTextGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(14f, 6f);
            labelRt.offsetMax = new Vector2(-14f, -6f);

            _badgeLabel = badgeTextGo.GetComponent<TextMeshProUGUI>();
            if (sharedFont != null) _badgeLabel.font = sharedFont;
            _badgeLabel.fontSize = _badgeFontSize;
            _badgeLabel.alignment = TextAlignmentOptions.Center;
            _badgeLabel.color = Color.white;
            _badgeLabel.raycastTarget = false;
        }
        else if (_badgeLabel == null)
        {
            _badgeLabel = _badgeRoot.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        if (bindRuntimeListeners)
        {
            Button badgeBtn = _badgeRoot.GetComponent<Button>();
            if (badgeBtn != null)
            {
                badgeBtn.onClick.RemoveAllListeners();
                badgeBtn.onClick.AddListener(() => OpenPanel());
            }
        }

        if (_modalRoot == null)
        {
            Transform existingModal = transform.Find("DifficultyModalPanel");
            if (existingModal != null) _modalRoot = existingModal.gameObject;
        }

        if (_modalRoot == null)
        {
            _modalRoot = new GameObject("DifficultyModalPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Canvas), typeof(GraphicRaycaster));
            _modalRoot.transform.SetParent(transform, false);
            Canvas modalCanvas = _modalRoot.GetComponent<Canvas>();
            modalCanvas.overrideSorting = true;
            modalCanvas.sortingOrder = _modalSortingOrder;

            RectTransform modalRt = _modalRoot.GetComponent<RectTransform>();
            modalRt.anchorMin = Vector2.zero;
            modalRt.anchorMax = Vector2.one;
            modalRt.offsetMin = Vector2.zero;
            modalRt.offsetMax = Vector2.zero;

            Image modalBg = _modalRoot.GetComponent<Image>();
            modalBg.color = new Color(0.03f, 0.02f, 0.10f, 0.94f);

            CreateLabel(_modalRoot.transform, sharedFont, "SELECT DIFFICULTY", _titleAnchoredPosition, _titleSize, _titleFontSize, Color.white);
        }

        string[] cardSprites = { "Difficulty/Easy_UI", "Difficulty/Normal_UI", "Difficulty/Hard_UI" };
        if (_cardSelectionOutlines == null || _cardSelectionOutlines.Length != 3)
            _cardSelectionOutlines = new Image[3];

        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            string cardName = $"Card_{(GameDifficulty.Level)i}";
            Transform cardTr = _modalRoot.transform.Find(cardName);
            GameObject cardGo;
            if (cardTr == null)
            {
                float cardX = (_cardAnchoredX != null && i < _cardAnchoredX.Length) ? _cardAnchoredX[i] : (i - 1) * 420f;
                cardGo = new GameObject(cardName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                cardGo.transform.SetParent(_modalRoot.transform, false);
                RectTransform rt = cardGo.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(cardX, _cardAnchoredY);
                rt.sizeDelta = _cardSize;

                Image img = cardGo.GetComponent<Image>();
                img.sprite = LoadResourceSprite(cardSprites[i]);
                img.preserveAspect = true;

                var selGo = new GameObject("SelectedOutline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                selGo.transform.SetParent(cardGo.transform, false);
                RectTransform selRt = selGo.GetComponent<RectTransform>();
                selRt.anchorMin = Vector2.zero;
                selRt.anchorMax = Vector2.one;
                selRt.offsetMin = new Vector2(-14f, -14f);
                selRt.offsetMax = new Vector2(14f, 14f);

                Image selImg = selGo.GetComponent<Image>();
                selImg.sprite = LoadResourceSprite("Difficulty/selected_UI");
                selImg.preserveAspect = true;
                selImg.raycastTarget = false;
                _cardSelectionOutlines[i] = selImg;
            }
            else
            {
                cardGo = cardTr.gameObject;
                if (_cardSelectionOutlines[i] == null)
                {
                    Transform selTr = cardTr.Find("SelectedOutline");
                    if (selTr != null) _cardSelectionOutlines[i] = selTr.GetComponent<Image>();
                }
            }

            if (bindRuntimeListeners)
            {
                Button btn = cardGo.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(() => OpenDetailPopup((GameDifficulty.Level)idx));
                }
            }
        }

        Transform tutTr = _modalRoot.transform.Find("TutorialToggleBtn");
        GameObject tutBtnGo;
        if (tutTr == null)
        {
            tutBtnGo = new GameObject("TutorialToggleBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            tutBtnGo.transform.SetParent(_modalRoot.transform, false);
            RectTransform tutRt = tutBtnGo.GetComponent<RectTransform>();
            tutRt.anchoredPosition = _tutorialBtnAnchoredPosition;
            tutRt.sizeDelta = _tutorialBtnSize;

            Image tutImg = tutBtnGo.GetComponent<Image>();
            tutImg.sprite = LoadResourceSprite("Difficulty/Tutorial_UI");
            tutImg.preserveAspect = true;

            var tutSelGo = new GameObject("TutorialSelectedOutline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            tutSelGo.transform.SetParent(tutBtnGo.transform, false);
            RectTransform tutSelRt = tutSelGo.GetComponent<RectTransform>();
            tutSelRt.anchorMin = Vector2.zero;
            tutSelRt.anchorMax = Vector2.one;
            tutSelRt.offsetMin = new Vector2(-12f, -12f);
            tutSelRt.offsetMax = new Vector2(12f, 12f);

            _tutorialSelectionOutline = tutSelGo.GetComponent<Image>();
            _tutorialSelectionOutline.sprite = LoadResourceSprite("Difficulty/selected_tutorial_UI");
            _tutorialSelectionOutline.preserveAspect = true;
            _tutorialSelectionOutline.raycastTarget = false;
        }
        else
        {
            tutBtnGo = tutTr.gameObject;
            if (_tutorialSelectionOutline == null)
            {
                Transform tutSelTr = tutTr.Find("TutorialSelectedOutline");
                if (tutSelTr != null) _tutorialSelectionOutline = tutSelTr.GetComponent<Image>();
            }
        }

        if (bindRuntimeListeners)
        {
            Button tutBtn = tutBtnGo.GetComponent<Button>();
            if (tutBtn != null)
            {
                tutBtn.onClick.RemoveAllListeners();
                tutBtn.onClick.AddListener(OpenTutorialDetailPopup);
            }
        }

        if (_tutorialStateText == null)
        {
            Transform tutStateTr = _modalRoot.transform.Find("TutorialStateText");
            if (tutStateTr != null)
            {
                _tutorialStateText = tutStateTr.GetComponent<TextMeshProUGUI>();
            }
            else
            {
                _tutorialStateText = CreateLabel(_modalRoot.transform, sharedFont, "", _tutorialStateAnchoredPosition, _tutorialStateSize, _tutorialStateFontSize, new Color(0.8f, 1f, 0.8f, 1f));
                _tutorialStateText.gameObject.name = "TutorialStateText";
            }
        }

        Transform closeTr = _modalRoot.transform.Find("CloseModalBtn");
        GameObject closeBtnGo;
        if (closeTr == null)
        {
            closeBtnGo = new GameObject("CloseModalBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            closeBtnGo.transform.SetParent(_modalRoot.transform, false);
            RectTransform closeRt = closeBtnGo.GetComponent<RectTransform>();
            closeRt.anchoredPosition = _closeBtnAnchoredPosition;
            closeRt.sizeDelta = _closeBtnSize;
            closeBtnGo.GetComponent<Image>().color = new Color(0.25f, 0.2f, 0.45f, 1f);
            _closeModalBtnLabel = CreateLabel(closeBtnGo.transform, sharedFont, "BACK", Vector2.zero, _closeBtnSize, _closeBtnFontSize, Color.white);
        }
        else
        {
            closeBtnGo = closeTr.gameObject;
            if (_closeModalBtnLabel == null)
                _closeModalBtnLabel = closeBtnGo.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        if (bindRuntimeListeners)
        {
            Button closeBtn = closeBtnGo.GetComponent<Button>();
            if (closeBtn != null)
            {
                closeBtn.onClick.RemoveAllListeners();
                closeBtn.onClick.AddListener(() => ClosePanel());
            }
        }

        if (_detailPopupRoot == null)
        {
            Transform detailTr = _modalRoot.transform.Find("DifficultyDetailPopup");
            if (detailTr != null) _detailPopupRoot = detailTr.gameObject;
        }

        if (_detailPopupRoot == null)
        {
            _detailPopupRoot = new GameObject("DifficultyDetailPopup", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _detailPopupRoot.transform.SetParent(_modalRoot.transform, false);
            RectTransform detailRootRt = _detailPopupRoot.GetComponent<RectTransform>();
            detailRootRt.anchorMin = Vector2.zero;
            detailRootRt.anchorMax = Vector2.one;
            detailRootRt.offsetMin = Vector2.zero;
            detailRootRt.offsetMax = Vector2.zero;
            _detailPopupRoot.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.82f);

            var detailCardGo = new GameObject("DetailCard", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            detailCardGo.transform.SetParent(_detailPopupRoot.transform, false);
            RectTransform detailCardRt = detailCardGo.GetComponent<RectTransform>();
            detailCardRt.anchoredPosition = _detailCardAnchoredPosition;
            detailCardRt.sizeDelta = _detailCardSize;
            _detailCardImage = detailCardGo.GetComponent<Image>();
            _detailCardImage.preserveAspect = true;

            _detailDescText = CreateLabel(detailCardGo.transform, sharedFont, "", _detailDescAnchoredPosition, _detailDescSize, _detailDescFontSize, Color.white);
            _detailDescText.gameObject.name = "DetailDescText";

            var okBtnGo = new GameObject("ConfirmBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            okBtnGo.transform.SetParent(detailCardGo.transform, false);
            RectTransform okRt = okBtnGo.GetComponent<RectTransform>();
            okRt.anchoredPosition = _detailOkAnchoredPosition;
            okRt.sizeDelta = _detailActionBtnSize;
            okBtnGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);
            _detailOkLabel = CreateLabel(okBtnGo.transform, sharedFont, "OK", Vector2.zero, _detailActionBtnSize, _detailActionFontSize, new Color(0.05f, 0.02f, 0.15f, 1f));

            var okSelGo = new GameObject("OkSelectedOutline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            okSelGo.transform.SetParent(okBtnGo.transform, false);
            RectTransform okSelRt = okSelGo.GetComponent<RectTransform>();
            okSelRt.anchorMin = Vector2.zero;
            okSelRt.anchorMax = Vector2.one;
            okSelRt.offsetMin = new Vector2(-12f, -12f);
            okSelRt.offsetMax = new Vector2(12f, 12f);
            _detailOkSelectedOutline = okSelGo.GetComponent<Image>();
            _detailOkSelectedOutline.sprite = LoadResourceSprite("Difficulty/ui_difficulty_selected");
            _detailOkSelectedOutline.preserveAspect = true;
            _detailOkSelectedOutline.raycastTarget = false;

            var backBtnGo = new GameObject("BackBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            backBtnGo.transform.SetParent(detailCardGo.transform, false);
            RectTransform backRt = backBtnGo.GetComponent<RectTransform>();
            backRt.anchoredPosition = _detailBackAnchoredPosition;
            backRt.sizeDelta = _detailActionBtnSize;
            backBtnGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);
            _detailBackLabel = CreateLabel(backBtnGo.transform, sharedFont, "BACK", Vector2.zero, _detailActionBtnSize, _detailActionFontSize, new Color(0.05f, 0.02f, 0.15f, 1f));

            var backSelGo = new GameObject("BackSelectedOutline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            backSelGo.transform.SetParent(backBtnGo.transform, false);
            RectTransform backSelRt = backSelGo.GetComponent<RectTransform>();
            backSelRt.anchorMin = Vector2.zero;
            backSelRt.anchorMax = Vector2.one;
            backSelRt.offsetMin = new Vector2(-12f, -12f);
            backSelRt.offsetMax = new Vector2(12f, 12f);
            _detailBackSelectedOutline = backSelGo.GetComponent<Image>();
            _detailBackSelectedOutline.sprite = LoadResourceSprite("Difficulty/ui_difficulty_selected");
            _detailBackSelectedOutline.preserveAspect = true;
            _detailBackSelectedOutline.raycastTarget = false;
        }
        else
        {
            Transform detailCardTr = _detailPopupRoot.transform.Find("DetailCard");
            if (detailCardTr != null)
            {
                if (_detailCardImage == null) _detailCardImage = detailCardTr.GetComponent<Image>();
                if (_detailDescText == null)
                {
                    Transform descTr = detailCardTr.Find("DetailDescText") ?? detailCardTr.Find("Text");
                    if (descTr != null) _detailDescText = descTr.GetComponent<TextMeshProUGUI>();
                }
                Transform okTr = detailCardTr.Find("ConfirmBtn");
                if (okTr != null)
                {
                    if (_detailOkLabel == null) _detailOkLabel = okTr.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (_detailOkSelectedOutline == null)
                    {
                        Transform okSelTr = okTr.Find("OkSelectedOutline");
                        if (okSelTr != null) _detailOkSelectedOutline = okSelTr.GetComponent<Image>();
                    }
                }
                Transform backTr = detailCardTr.Find("BackBtn");
                if (backTr != null)
                {
                    if (_detailBackLabel == null) _detailBackLabel = backTr.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (_detailBackSelectedOutline == null)
                    {
                        Transform backSelTr = backTr.Find("BackSelectedOutline");
                        if (backSelTr != null) _detailBackSelectedOutline = backSelTr.GetComponent<Image>();
                    }
                }
            }
        }

        if (bindRuntimeListeners && _detailPopupRoot != null)
        {
            Transform detailCardTr = _detailPopupRoot.transform.Find("DetailCard");
            if (detailCardTr != null)
            {
                Transform okTr = detailCardTr.Find("ConfirmBtn");
                if (okTr != null)
                {
                    Button okBtn = okTr.GetComponent<Button>();
                    if (okBtn != null)
                    {
                        okBtn.onClick.RemoveAllListeners();
                        okBtn.onClick.AddListener(() =>
                        {
                            _detailCursorIndex = 0;
                            ConfirmDetailSelection();
                        });
                    }
                }
                Transform backTr = detailCardTr.Find("BackBtn");
                if (backTr != null)
                {
                    Button backBtn = backTr.GetComponent<Button>();
                    if (backBtn != null)
                    {
                        backBtn.onClick.RemoveAllListeners();
                        backBtn.onClick.AddListener(OnDetailBackButtonClicked);
                    }
                }
            }
        }

        if (_detailPopupRoot != null) _detailPopupRoot.SetActive(false);
        if (_modalRoot != null) _modalRoot.SetActive(false);
    }

    private static TextMeshProUGUI CreateLabel(Transform parent, TMP_FontAsset font, string text, Vector2 anchoredPos, Vector2 size, float fontSize, Color color)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        TextMeshProUGUI tmp = go.GetComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        return tmp;
    }

    private void RefreshVisuals()
    {
        GameDifficulty.Level lv = GameDifficulty.Current;
        int minutes = Mathf.RoundToInt(GameDifficulty.GetTimeLimitSeconds(lv) / 60f);
        bool isTutMode = GameDifficulty.IncludeTutorial;

        if (_badgeLabel != null)
        {
            _badgeLabel.text = isTutMode
                ? "MODE: TUTORIAL\n[Tab / Y]"
                : $"MODE: {GameDifficulty.GetDisplayName(lv)} ({minutes}m)\n[Tab / Y]";
        }

        if (_closeModalBtnLabel != null)
        {
            _closeModalBtnLabel.text = "BACK";
        }

        for (int i = 0; i < _cardSelectionOutlines.Length; i++)
        {
            if (_cardSelectionOutlines[i] == null) continue;
            bool isCurrentMode = !isTutMode && ((int)lv == i);
            bool isCursor = _isOpen && _cursorIndex == i;
            _cardSelectionOutlines[i].gameObject.SetActive(isCurrentMode || isCursor);
            _cardSelectionOutlines[i].color = isCursor
                ? Color.white
                : new Color(1f, 1f, 1f, 0.55f);
        }

        if (_tutorialSelectionOutline != null)
        {
            bool isCursor = _isOpen && _cursorIndex == 3;
            _tutorialSelectionOutline.gameObject.SetActive(isTutMode || isCursor);
            _tutorialSelectionOutline.color = isCursor
                ? Color.white
                : new Color(1f, 1f, 1f, 0.6f);
        }

        if (_tutorialStateText != null)
        {
            _tutorialStateText.text = isTutMode
                ? GameLanguage.Pick("現在のモード：TUTORIAL（チュートリアル専用モード）", "Current Mode: TUTORIAL")
                : GameLanguage.Pick($"現在のモード：{GameDifficulty.GetDisplayName(lv)}（チュートリアルなしで本編開始）", $"Current Mode: {GameDifficulty.GetDisplayName(lv)} (No tutorial)");
        }
    }
}
