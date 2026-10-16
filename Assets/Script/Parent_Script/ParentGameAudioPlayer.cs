using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// 親機に追加した「子機ゲーム音専用」再生コンポーネント。
///
///  ・子機から届いた音IDを、同じ対応表（GameAudioCatalog）で AudioClip に解決してローカル再生する。
///  ・既存の AudioListener をそのまま使う（AudioListener は追加しない）。専用オブジェクトは実行時に生成し、
///    Inspectorの Audio Origin（ゲーム機側のTransform）の子にする。未設定のときだけ Main Camera の子にして
///    カメラ基準のローカル位置を使う（ゲーム機が動けば音源も一緒に動く）。
///  ・親機固有の音（足音・ドア・警告・捕獲演出）や既存 AudioSource には触れない（親機の聞こえ方は独立音量）。
///  ・寝たふり中はこの専用音だけをミュートする（AudioListener.pause や全体音量0は使わない）。
///  ・ParentGameScene を離れたら再生を止めて購読を解除する（タイトル・結果画面へ音を持ち越さない）。
/// </summary>
[DisallowMultipleComponent]
public class ParentGameAudioPlayer : MonoBehaviour
{
    [Header("対応表（子機と同じアセットを参照する）")]
    [SerializeField]
    private GameAudioCatalog catalog;

    [Header("音の発生位置（ゲーム機から鳴らす）")]
    [Tooltip("子機ゲーム音を鳴らす位置。ゲーム機モデルの子に作った空オブジェクトを指定してください。\n" +
             "指定した場合は、その位置からそのまま鳴ります（カメラ基準のオフセットは加算しません）。\n" +
             "ゲーム機が動けば音源も一緒に動きます。\n" +
             "未設定のときだけ、下の『カメラ基準のローカル位置』を Main Camera に対して使います。")]
    [FormerlySerializedAs("attachTo")]
    [SerializeField]
    private Transform audioOrigin;

    [Tooltip("audioOrigin が未設定のときだけ使う、Main Camera から見たローカル位置（X=0, Y=-0.35, Z=0.45）。\n" +
             "audioOrigin を指定した場合は加算されません。")]
    [FormerlySerializedAs("localPosition")]
    [SerializeField]
    private Vector3 cameraLocalPosition = new Vector3(0f, -0.35f, 0.45f);

    [Header("音源の設定")]
    [Tooltip("0=2D, 1=3D。子機ゲーム音は3D音源として鳴らす。")]
    [Range(0f, 1f)]
    [SerializeField]
    private float spatialBlend = 1f;

    [Tooltip("距離減衰の最小距離。この距離までは減衰しない（初期設定で極端に小さくならないようにする）。")]
    [SerializeField, Min(0.01f)]
    private float minDistance = 0.6f;

    [Tooltip("距離減衰の最大距離。")]
    [SerializeField, Min(0.02f)]
    private float maxDistance = 30f;

    [Tooltip("距離減衰のカーブ。Linear が分かりやすい（Logarithmic は近距離で急に大きくなる）。")]
    [SerializeField]
    private AudioRolloffMode rolloffMode = AudioRolloffMode.Linear;

    // ── 音量（子機ゲーム音だけ。親機の足音・ドア・警告・捕獲音には触れない）────────
    [Header("音量（子機ゲーム音だけ・親機の既存音とは独立）")]
    [Tooltip("子機ゲーム音の全体音量。0にすると子機由来のBGM・SEだけが無音になります。\n" +
             "再生中のBGMにもその場で反映されます。")]
    [Range(0f, 1f)]
    [SerializeField]
    private float masterVolume = 1f;

    [Tooltip("子機BGMの音量（全体音量に掛かります）。再生中のBGMにもその場で反映されます。")]
    [Range(0f, 1f)]
    [SerializeField]
    private float bgmVolume = 1f;

    [Tooltip("子機SEの音量（全体音量に掛かります）。次のSEから必ず反映されます。\n" +
             "子機から受信した音量とは別枠で掛け合わされます（ハートビート受信でこの値は変わりません）。")]
    [Range(0f, 1f)]
    [SerializeField]
    private float seVolume = 1f;

    [Header("寝たふり中")]
    [Tooltip("未設定なら起動時に1回だけ SleepingController を探す（毎フレームは探さない）。")]
    [SerializeField]
    private SleepingController sleepingController;

    [Header("安全弁")]
    [Tooltip("子機からの受信がこの秒数途絶えたら、鳴り続けているBGMを止める（0以下で無効）。")]
    [SerializeField]
    private float receiveWatchdogSeconds = 3f;

    [Header("デバッグ")]
    [Tooltip("受信〜再生呼び出しのログを出す。")]
    [SerializeField]
    private bool showDebugLog = false;

    [Tooltip("受信〜再生呼び出しがこの時間を超えたら警告する（ms）。")]
    [SerializeField]
    private float warnHandleMs = 8f;

    [Tooltip("直近の「受信→再生呼び出し」所要時間（ms）。親機内の処理時間であり、耳に届くまでの遅延ではない。")]
    [SerializeField]
    private float debugLastHandleMs;

    /// <summary>終了済みプレイの遅延パケットを弾くために覚えておくプレイID数（上限）。</summary>
    private const int MaxRecentPlayIds = 4;

    /// <summary>二重再生を防ぐための稼働中インスタンス（1台だけを想定する）。</summary>
    private static ParentGameAudioPlayer activePlayer;

    // ── 実行時生成する専用音源（BGMとSEで分ける）───────────────────────────────
    private AudioSource bgmSource;
    private AudioSource seSource;
    private GameObject audioRoot;

    // ── 子機の状態（プレイ単位）────────────────────────────────────────────────
    private string currentPlayId = "";
    private int lastSeSeq;
    private int lastStateSeq;
    private readonly string[] recentPlayIds = new string[MaxRecentPlayIds];
    private int recentPlayIdIndex;

    private GameAudioId bgmId = GameAudioId.None;
    private float bgmChildVolume = 1f;
    private float bgmEntryScale = 1f;

    // ── 受信・寝たふり・診断 ───────────────────────────────────────────────────
    private ParentNotificationHub subscribedHub;
    private bool subscribed;
    private float lastReceivedUnscaledTime;
    private bool hasReceived;

    private bool wasSleeping;
    private readonly HashSet<int> warnedSoundIds = new HashSet<int>();

    private void OnEnable()
    {
        EnsureAudioRoot();
        PreloadCatalogClips();
        TrySubscribe();

        // 寝たふり中にシーンへ入った場合も、最初のUpdateで正しく反映される。
        wasSleeping = IsSleepingNow();
        ApplyVolumes();
    }

    private void OnDisable()
    {
        // ParentGameSceneを離れる／無効化される時は、専用音を止めて購読を解除する。
        Unsubscribe();
        StopAllAudio();
        DestroyAudioRoot();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    private void Update()
    {
        TrySubscribe();
        VerifyAudioRootParent();

        bool sleeping = IsSleepingNow();
        if (sleeping && !wasSleeping) OnSleepStarted();
        wasSleeping = sleeping;

        // 音量は毎フレーム反映する（Inspectorの変更が再生中のBGMにもその場で効く＝ミュート状態は維持される）。
        ApplyVolumes();
        WatchdogTick();
    }

    // ── 専用音源の生成 ─────────────────────────────────────────────────────────

    private void EnsureAudioRoot()
    {
        if (audioRoot != null && bgmSource != null && seSource != null) return;

        Transform parent = ResolveAudioParent();
        if (parent == null)
        {
            Debug.LogWarning(
                "[ParentGameAudioPlayer] 音源を置く親（audioOrigin か Main Camera）が見つかりません。" +
                "Inspectorの Audio Origin にゲーム機側のTransformを指定してください（この場合、子機ゲーム音は鳴りません）。", this);
            return;
        }

        audioRoot = new GameObject("ChildGameAudioReceiver");
        audioRoot.transform.SetParent(parent, false);

        // 位置指定（audioOrigin）があるときは、その位置からそのまま鳴らす（カメラ基準のオフセットは加算しない）。
        // 未設定のときだけ、カメラ基準のローカル位置を使う。
        audioRoot.transform.localPosition = audioOrigin != null ? Vector3.zero : cameraLocalPosition;
        audioRoot.transform.localRotation = Quaternion.identity;
        audioRoot.transform.localScale = Vector3.one;

        bgmSource = CreateSource(audioRoot.transform, "ChildGameBgm", loop: true);
        seSource = CreateSource(audioRoot.transform, "ChildGameSe", loop: false);

        if (showDebugLog)
            Debug.Log($"[ParentGameAudioPlayer] 専用音源を生成しました（parent='{parent.name}', origin={(audioOrigin != null ? audioOrigin.name : "Main Camera(既定)")}）。", this);
    }

    /// <summary>音源をぶら下げる親。audioOrigin があればそこ、無ければ Main Camera。</summary>
    private Transform ResolveAudioParent()
    {
        if (audioOrigin != null) return audioOrigin;
        return Camera.main != null ? Camera.main.transform : null;
    }

    /// <summary>
    /// 音源の親が想定と違うときだけ付け替える。
    /// カメラ側に古い音源が残って、ゲーム機の位置と二重に鳴るのを防ぐ（負荷は参照比較のみ）。
    /// </summary>
    private void VerifyAudioRootParent()
    {
        if (audioRoot == null) return;

        Transform desired = ResolveAudioParent();
        if (desired == null) return;
        if (audioRoot.transform.parent == desired) return;

        audioRoot.transform.SetParent(desired, false);
        audioRoot.transform.localPosition = audioOrigin != null ? Vector3.zero : cameraLocalPosition;
        audioRoot.transform.localRotation = Quaternion.identity;

        if (showDebugLog)
            Debug.Log($"[ParentGameAudioPlayer] 音源位置を '{(audioOrigin != null ? audioOrigin.name : desired.name)}' へ付け替えました。", this);
    }

    private AudioSource CreateSource(Transform parent, string name, bool loop)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        AudioSource source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = Mathf.Clamp01(spatialBlend);   // 1=3D
        source.dopplerLevel = 0f;                            // ドップラーは0
        source.rolloffMode = rolloffMode;
        source.minDistance = Mathf.Max(0.01f, minDistance);
        source.maxDistance = Mathf.Max(source.minDistance + 0.01f, maxDistance);
        source.spread = 0f;
        source.priority = 128;
        source.mute = false;
        source.volume = 0f;                                  // 実音量はApplyVolumesが決める
        source.bypassReverbZones = true;
        source.spatialize = false;                           // 追加のSpatializerは使わない
        return source;
    }

    private void DestroyAudioRoot()
    {
        bgmSource = null;
        seSource = null;

        if (audioRoot != null)
        {
            Destroy(audioRoot);
            audioRoot = null;
        }
    }

    /// <summary>短いSEの初回ロードを取得の瞬間に起こさないため、本編前に読み込みを仕掛ける。</summary>
    private void PreloadCatalogClips()
    {
        if (catalog == null || catalog.Entries == null) return;

        for (int i = 0; i < catalog.Entries.Count; i++)
        {
            GameAudioCatalog.Entry entry = catalog.Entries[i];
            if (entry == null || entry.clip == null) continue;

            try
            {
                if (entry.clip.loadState == AudioDataLoadState.Unloaded)
                    entry.clip.LoadAudioData();
            }
            catch { }
        }
    }

    // ── 受信（メインスレッド。UDP側は通知するだけ・シーン側は再生するだけ）───────

    private void OnGameAudioReceived(ParentGameAudioEvent evt)
    {
        if (!Accept(evt)) return;

        hasReceived = true;
        lastReceivedUnscaledTime = Time.unscaledTime;

        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        if (evt.SoundId == 0)
        {
            // 「無音（BGM停止）」の状態だけを表す。
            if (evt.IsState) StopBgm();

            RecordHandleTime(startedAt, "(stop)");
            return;
        }

        if (evt.IsState) ApplyBgmState(evt);
        else PlayOneShotSe(evt);

        RecordHandleTime(startedAt, evt.IsState ? "state" : "se");
    }

    /// <summary>
    /// プレイIDと連番で「重複・順序逆転・前回プレイの遅延到着」を弾く。
    ///  ・同じプレイIDで seq が戻る（＝重複・古い）ものは捨てる。
    ///  ・違うプレイIDは、終了済みプレイなら捨て、そうでなければ新しいプレイとして受け入れる。
    /// </summary>
    private bool Accept(in ParentGameAudioEvent evt)
    {
        if (string.IsNullOrEmpty(evt.PlayId)) return false;

        if (evt.PlayId != currentPlayId)
        {
            if (IsRecentOldPlay(evt.PlayId)) return false;   // 前回プレイの音をリトライ後に鳴らさない
            BeginPlay(evt.PlayId);
        }

        int last = evt.IsState ? lastStateSeq : lastSeSeq;
        if (evt.Seq <= last) return false;

        if (evt.IsState) lastStateSeq = evt.Seq;
        else lastSeSeq = evt.Seq;

        return true;
    }

    private void BeginPlay(string playId)
    {
        // 前のプレイの状態・キュー・音を引き継がない。
        StopAllAudio();

        if (!string.IsNullOrEmpty(currentPlayId))
        {
            recentPlayIds[recentPlayIdIndex] = currentPlayId;
            recentPlayIdIndex = (recentPlayIdIndex + 1) % MaxRecentPlayIds;
        }

        currentPlayId = playId;
        lastSeSeq = 0;
        lastStateSeq = 0;
        bgmId = GameAudioId.None;
        bgmChildVolume = 1f;
        bgmEntryScale = 1f;

        if (showDebugLog)
            Debug.Log($"[ParentGameAudioPlayer] 子機のプレイを検出しました（playId='{playId}'）。", this);
    }

    private bool IsRecentOldPlay(string playId)
    {
        for (int i = 0; i < recentPlayIds.Length; i++)
        {
            if (recentPlayIds[i] == playId) return true;
        }

        return false;
    }

    // ── 再生 ───────────────────────────────────────────────────────────────────

    private void ApplyBgmState(in ParentGameAudioEvent evt)
    {
        if (bgmSource == null) EnsureAudioRoot();
        if (bgmSource == null) return;

        GameAudioCatalog.Entry entry = catalog != null ? catalog.Find((GameAudioId)evt.SoundId) : null;
        if (entry == null || entry.clip == null)
        {
            WarnUnknownId(evt.SoundId);
            return;
        }

        if (!evt.Playing)
        {
            StopBgm();
            return;
        }

        bgmEntryScale = entry.volumeScale;
        bgmChildVolume = Mathf.Clamp01(evt.Volume);
        bgmId = entry.id;

        if (bgmSource.clip != entry.clip)
        {
            // 曲の変更（停止→再生）。ここだけ先頭から鳴る。
            bgmSource.clip = entry.clip;
            bgmSource.loop = evt.Loop;
            PreloadClip(entry.clip);
            bgmSource.Stop();
            bgmSource.Play();
        }
        else
        {
            // 同じ曲の再通知（ハートビート・再接続）では先頭に戻さない。
            bgmSource.loop = evt.Loop;
        }

        ApplyVolumes();
    }

    private void PlayOneShotSe(in ParentGameAudioEvent evt)
    {
        if (seSource == null) EnsureAudioRoot();
        if (seSource == null) return;

        // 寝たふり中の単発SEは捨てる（起床時にまとめて鳴らさない）。
        if (IsSleepingNow()) return;

        GameAudioCatalog.Entry entry = catalog != null ? catalog.Find((GameAudioId)evt.SoundId) : null;
        if (entry == null || entry.clip == null)
        {
            WarnUnknownId(evt.SoundId);
            return;
        }

        PreloadClip(entry.clip);

        float volume = Mathf.Clamp01(entry.volumeScale * Mathf.Clamp01(evt.Volume));
        seSource.PlayOneShot(entry.clip, volume);
    }

    private static void PreloadClip(AudioClip clip)
    {
        if (clip == null) return;

        try
        {
            if (clip.loadState == AudioDataLoadState.Unloaded)
                clip.LoadAudioData();
        }
        catch { }
    }

    // ── 停止・音量・寝たふり・監視 ──────────────────────────────────────────────

    private void StopBgm()
    {
        bgmId = GameAudioId.None;

        if (bgmSource != null && bgmSource.isPlaying)
            bgmSource.Stop();
    }

    private void StopAllAudio()
    {
        bgmId = GameAudioId.None;

        if (bgmSource != null) bgmSource.Stop();
        if (seSource != null) seSource.Stop();
    }

    /// <summary>
    /// 専用音源の音量を決める。親機の既存音（足音・ドア・警告など）には触れない。
    /// 寝たふり中はBGMをミュートする（再生位置は進めたまま＝起床時に同じ位置から戻る）。
    /// </summary>
    private void ApplyVolumes()
    {
        bool sleeping = IsSleepingNow();

        if (bgmSource != null)
        {
            bgmSource.volume = Mathf.Clamp01(masterVolume * bgmVolume * bgmEntryScale * bgmChildVolume);
            bgmSource.mute = sleeping;
        }

        if (seSource != null)
        {
            seSource.volume = Mathf.Clamp01(masterVolume * seVolume);
            seSource.mute = sleeping;
        }
    }

    /// <summary>寝たふり開始：鳴っている途中のSEも聞こえなくする（起床時に再開しない）。</summary>
    private void OnSleepStarted()
    {
        if (seSource != null)
        {
            seSource.Stop();
            seSource.mute = true;
        }

        if (showDebugLog)
            Debug.Log("[ParentGameAudioPlayer] 寝たふりのため子機ゲーム音をミュートしました。", this);
    }

    private bool IsSleepingNow()
    {
        if (sleepingController == null)
        {
            // 毎フレームのFindはしない（起動時に1回だけ探す）。
            sleepingController = FindFirstObjectByType<SleepingController>();
        }

        return sleepingController != null && sleepingController.IsSleeping;
    }

    /// <summary>受信が途絶えたまま古いループ音が鳴り続けないように止める（状態は再送で復帰する）。</summary>
    private void WatchdogTick()
    {
        if (receiveWatchdogSeconds <= 0f) return;
        if (!hasReceived) return;
        if (bgmSource == null || !bgmSource.isPlaying) return;
        if (Time.unscaledTime - lastReceivedUnscaledTime <= receiveWatchdogSeconds) return;

        StopBgm();

        if (showDebugLog)
            Debug.LogWarning($"[ParentGameAudioPlayer] 子機からの受信が{receiveWatchdogSeconds:0.#}秒途絶えたため、子機ゲーム音を停止しました。", this);
    }

    /// <summary>
    /// 「受信→再生呼び出し」の所要時間を記録する（親機内の処理時間。耳に届くまでの遅延ではない）。
    /// </summary>
    private void RecordHandleTime(long startedAt, string kind)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        debugLastHandleMs = (float)ms;

        if (showDebugLog && ms >= warnHandleMs)
        {
            Debug.LogWarning($"[ParentGameAudioPlayer] 受信→再生呼び出しに {ms:0.##} ms かかりました（kind={kind}）。", this);
        }
    }

    private void WarnUnknownId(int soundId)
    {
        if (warnedSoundIds.Count >= 32) return;   // 履歴の上限（ログの増加を止める）
        if (!warnedSoundIds.Add(soundId)) return; // 同じIDは1回だけ

        Debug.LogWarning(
            $"[ParentGameAudioPlayer] 対応表に無い音IDのため再生しません: id={soundId}。" +
            "子機と親機で同じ GameAudioCatalog を参照しているか確認してください。",
            this);
    }

    // ── 通知の購読（UDP側は受信を通知するだけ）─────────────────────────────────

    private void TrySubscribe()
    {
        if (subscribed) return;
        if (!isActiveAndEnabled) return;

        ParentUdpRuntimeHost host = ResolveParentHost();
        if (host == null || host.Notifications == null) return;

        if (activePlayer != null && activePlayer != this)
        {
            Debug.LogWarning("[ParentGameAudioPlayer] 子機ゲーム音の再生は既に別の ParentGameAudioPlayer が担当しています（二重再生防止のため購読しません）。", this);
            return;
        }

        subscribedHub = host.Notifications;
        subscribedHub.GameAudioReceived += OnGameAudioReceived;
        subscribed = true;
        activePlayer = this;

        if (showDebugLog)
            Debug.Log($"[ParentGameAudioPlayer] 子機ゲーム音の購読を開始しました（Host='{host.gameObject.name}'）。", this);
    }

    private void Unsubscribe()
    {
        if (subscribed)
        {
            if (subscribedHub != null)
                subscribedHub.GameAudioReceived -= OnGameAudioReceived;

            subscribed = false;
        }

        if (activePlayer == this)
            activePlayer = null;

        subscribedHub = null;
    }

    private static ParentUdpRuntimeHost ResolveParentHost()
    {
        ParentUdpRuntimeHost host = ParentUdpRuntimeHost.Running;
        if (host != null) return host;

        ParentUdpRuntimeHost found = FindFirstObjectByType<ParentUdpRuntimeHost>();
        return (found != null && found.IsConfigured) ? found : null;
    }
}
