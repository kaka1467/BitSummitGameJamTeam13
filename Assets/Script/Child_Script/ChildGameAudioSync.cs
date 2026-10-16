using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 子機の「本編プレイ中の音」を親機へ通知する（親機が同じAudioClipをローカル再生する）。
///
///  ・子機のローカル再生（AudioManager／BGMController）は従来どおり即時。本クラスはその同じ処理経路の
///    後段で短いイベントだけを送る（音声データは送らない・まとめ送りしない・ACK待ちをしない）。
///  ・対象は本編のみ。チュートリアル／開始前カウントダウン／リザルト／終了演出は送らない。
///  ・「ChildGameSceneにいる」だけでは判断せず、本編開始（GameManager.IsMainPlayActive）を条件にする。
///  ・送信に失敗しても子機のゲームとローカル音は止めない（ChildUdpRuntimeHost.SendState が握る）。
/// </summary>
[DisallowMultipleComponent]
public class ChildGameAudioSync : MonoBehaviour
{
    [Header("対応表（親機と同じアセットを参照する）")]
    [SerializeField]
    private GameAudioCatalog catalog;

    [Header("通知")]
    [Tooltip("OFFにすると親機への通知を止める（子機のローカル再生には影響しない）。")]
    [SerializeField]
    private bool syncEnabled = true;

    [Tooltip("BGM状態の再送間隔（秒）。親機のシーン読み込み・再接続時に現在のBGMへ追いつくために使う。0以下で無効。")]
    [SerializeField]
    private float bgmStateHeartbeatSeconds = 1f;

    [Tooltip("送信のたびにログを出す（本番はOFF推奨）。")]
    [SerializeField]
    private bool showDebugLog = false;

    [Tooltip("本編BGMの音源（未設定なら起動時に1回だけ探す）。本編開始の直前から鳴っているBGMを取りこぼさないために使う。")]
    [SerializeField]
    private BGMController bgmController;

    /// <summary>対応表に無い音を1回だけ警告するための上限（履歴を無限に増やさない）。</summary>
    private const int MaxWarnedClips = 32;

    /// <summary>プレイIDが取得できない場合の代替キー（プレイごとに必ず変えるため static）。</summary>
    private static int fallbackPlayKeySeq;

    private string playId = "";
    private int seSeq;
    private int stateSeq;
    private bool wasMainPlayActive;

    // 送信中の本編BGM状態（最新状態のスナップショット）
    private bool hasBgmState;
    private GameAudioId bgmId = GameAudioId.None;
    private bool bgmPlaying;
    private bool bgmLoop = true;
    private float bgmVolume = 1f;
    private float heartbeatTimer;

    private bool subscribed;
    private readonly HashSet<AudioClip> warnedClips = new HashSet<AudioClip>();
    private bool bgmControllerSearched;

    private void OnEnable()
    {
        Subscribe();
    }

    private void OnDisable()
    {
        // シーンを離れる／無効化される時に、親機側の複製再生を終わらせてから購読を外す。
        SendBgmStopIfNeeded();
        Unsubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    private void Update()
    {
        bool active = IsMainPlayActiveNow();

        if (active)
        {
            if (!wasMainPlayActive) BeginPlay();
        }
        else if (wasMainPlayActive)
        {
            EndPlay();
        }

        wasMainPlayActive = active;

        if (!active || !hasBgmState) return;
        if (bgmStateHeartbeatSeconds <= 0f) return;

        // 時間停止中でも送れるように実時間で数える（帯域に影響しない程度に低い頻度）。
        heartbeatTimer += Time.unscaledDeltaTime;
        if (heartbeatTimer < bgmStateHeartbeatSeconds) return;

        heartbeatTimer = 0f;
        SendBgmState();
    }

    // ── 本編の開始／終了 ────────────────────────────────────────────────────────

    private void BeginPlay()
    {
        seSeq = 0;
        stateSeq = 0;
        heartbeatTimer = 0f;
        hasBgmState = false;
        bgmId = GameAudioId.None;
        bgmPlaying = false;
        bgmLoop = true;
        bgmVolume = 1f;

        // 前回プレイのIDを引き継がない（親機側もプレイIDの変化で重複履歴を捨てる）。
        playId = ResolvePlayId();
        if (string.IsNullOrEmpty(playId))
            playId = "local" + (++fallbackPlayKeySeq);

        // 本編開始の直前（カウントダウン末尾）から鳴っているBGMも取りこぼさない。
        SyncCurrentBgmFromController();

        if (showDebugLog)
            Debug.Log($"[ChildGameAudioSync] 本編開始を検出しました（playId='{playId}'）。");
    }

    private void EndPlay()
    {
        // 親機の複製再生だけを終了させる（子機の既存音はそのまま）。
        SendBgmStopIfNeeded();

        hasBgmState = false;
        bgmId = GameAudioId.None;
        seSeq = 0;
        stateSeq = 0;
        heartbeatTimer = 0f;

        if (showDebugLog)
            Debug.Log("[ChildGameAudioSync] 本編終了を検出しました（親機の複製再生を停止）。");
    }

    /// <summary>
    /// 本編中か（タイトル・チュートリアル・開始前カウントダウン・終了演出・捕獲演出は本編ではない）。
    /// </summary>
    private static bool IsMainPlayActiveNow()
    {
        GameManager gm = GameManager.instance;
        if (gm == null) return false;              // ゲームシーンではない
        if (gm.IsTutorialMode) return false;       // チュートリアル／開始前カウントダウン
        if (gm.IsGameOver) return false;           // 結果確定後の終了演出
        if (!gm.IsMainPlayActive) return false;    // 本編開始がまだ通知されていない

        // 捕獲演出（終了演出）に入ったら複製再生を終わらせる。
        ChildUdpRuntimeHost host = ChildUdpRuntimeHost.Running;
        ChildNotificationHub hub = host != null ? host.Notifications : null;
        if (hub != null && hub.WaitPhase == ChildNotificationHub.CaptureWaitPhase.Waiting) return false;

        return true;
    }

    // ── 子機のローカル再生と同じ処理経路から呼ばれる（再生の直後）────────────

    private void HandleSePlayed(AudioClip clip, float volume)
    {
        if (!syncEnabled || catalog == null || clip == null) return;
        if (!IsMainPlayActiveNow()) return;

        GameAudioCatalog.Entry entry = catalog.Find(clip);
        if (entry == null || entry.clip == null)
        {
            WarnUnmapped(clip);
            return;
        }

        // 対応表で「BGM」とされた音は状態側（HandleBgmStateChanged）で扱う。
        if (entry.isBgm) return;

        SendSe(entry.id, volume);
    }

    private void HandleBgmStateChanged(AudioClip clip, bool playing, bool loop, float volume)
    {
        if (!syncEnabled || catalog == null) return;
        if (!IsMainPlayActiveNow()) return;

        GameAudioId id = GameAudioId.None;
        if (clip != null)
        {
            GameAudioCatalog.Entry entry = catalog.Find(clip);
            if (entry != null && entry.clip != null) id = entry.id;
        }

        if (id == GameAudioId.None && clip != null)
        {
            WarnUnmapped(clip);
            return;
        }

        // 対応表が「BGM」でない音（ラウドアイテムの音楽など、PlayBGM経由の単発）はSEとして送る。
        if (id != GameAudioId.None && !catalog.IsBgm(id))
        {
            if (!playing) return;
            SendSe(id, volume);
            return;
        }

        // clip不明の停止通知（StopBGM）は、直前のBGMを停止として送る。
        if (id == GameAudioId.None) id = bgmId;
        if (id == GameAudioId.None) return;

        bgmId = id;
        bgmPlaying = playing;
        bgmLoop = loop;
        bgmVolume = volume;
        hasBgmState = true;

        // 曲の変更・停止は待たせない（再通知されても親機は先頭へ戻さない）。
        SendBgmState();
    }

    // ── 送信 ───────────────────────────────────────────────────────────────────

    private void SendSe(GameAudioId id, float volume)
    {
        if (id == GameAudioId.None) return;

        ChildUdpRuntimeHost host = ChildUdpRuntimeHost.Running;
        if (host == null || string.IsNullOrEmpty(playId)) return;

        seSeq++;
        host.SendGameAudioSe(playId, seSeq, (int)id, volume);

        if (showDebugLog)
            Debug.Log($"[ChildGameAudioSync] → SE id={(int)id} seq={seSeq} vol={volume:0.###}");
    }

    private void SendBgmState()
    {
        ChildUdpRuntimeHost host = ChildUdpRuntimeHost.Running;
        if (host == null || string.IsNullOrEmpty(playId)) return;

        // イベントを取りこぼしていても、再送のたびに実状態へ寄せる（鳴り続け・鳴らないの固着を防ぐ）。
        SyncCurrentBgmFromController();

        stateSeq++;
        host.SendGameAudioState(playId, stateSeq, (int)bgmId, bgmPlaying, bgmLoop, bgmVolume);

        if (showDebugLog)
            Debug.Log($"[ChildGameAudioSync] → BGM id={(int)bgmId} seq={stateSeq} playing={bgmPlaying} loop={bgmLoop} vol={bgmVolume:0.###}");
    }

    /// <summary>親機の複製再生を止める（BGM停止の状態を1件送る）。</summary>
    private void SendBgmStopIfNeeded()
    {
        if (!syncEnabled) return;
        if (!wasMainPlayActive && !hasBgmState) return;
        if (string.IsNullOrEmpty(playId)) return;

        ChildUdpRuntimeHost host = ChildUdpRuntimeHost.Running;
        if (host == null) return;

        stateSeq++;
        host.SendGameAudioState(playId, stateSeq, 0, false, false, 0f);

        if (showDebugLog)
            Debug.Log($"[ChildGameAudioSync] → BGM停止 seq={stateSeq}（親機の複製再生を終了）");
    }

    private static string ResolvePlayId()
    {
        ChildUdpRuntimeHost host = ChildUdpRuntimeHost.Running;
        if (host == null || host.Session == null) return "";
        return host.Session.PlaySessionId ?? "";
    }

    /// <summary>
    /// 本編BGMの音源から現在状態を取り込む（本編開始時点の取りこぼしと、状態の古いまま固まるのを防ぐ）。
    /// 対応表で「BGM」とされた素材のときだけ反映する。
    /// </summary>
    private void SyncCurrentBgmFromController()
    {
        BGMController controller = ResolveBgmController();
        if (controller == null || catalog == null) return;

        AudioClip clip = controller.CurrentClip;
        if (clip == null) return;

        GameAudioCatalog.Entry entry = catalog.Find(clip);
        if (entry == null || entry.clip == null) return;
        if (!entry.isBgm) return;

        bgmId = entry.id;
        bgmPlaying = controller.IsPlaying;
        bgmLoop = controller.CurrentLoop;
        bgmVolume = controller.CurrentVolume;
        hasBgmState = true;
    }

    /// <summary>本編BGMの音源を解決する（毎回は探さない。起動時の1回だけ）。</summary>
    private BGMController ResolveBgmController()
    {
        if (bgmController != null) return bgmController;
        if (bgmControllerSearched) return null;

        bgmControllerSearched = true;
        bgmController = FindFirstObjectByType<BGMController>();
        return bgmController;
    }

    private void WarnUnmapped(AudioClip clip)
    {
        if (clip == null) return;
        if (warnedClips.Count >= MaxWarnedClips) return;   // 上限（ログと履歴の増加を止める）
        if (!warnedClips.Add(clip)) return;                // 同じ素材は1回だけ

        Debug.LogWarning(
            $"[ChildGameAudioSync] 対応表に無い音は親機へ通知しません: '{clip.name}'。" +
            "GameAudioCatalog に追加してください（判定はAudioClipの参照で行うため、同名素材でも衝突しません）。",
            this);
    }

    private void Subscribe()
    {
        if (subscribed) return;

        AudioManager.SePlayed += HandleSePlayed;
        AudioManager.BgmStateChanged += HandleBgmStateChanged;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed) return;

        AudioManager.SePlayed -= HandleSePlayed;
        AudioManager.BgmStateChanged -= HandleBgmStateChanged;
        subscribed = false;
    }
}
