// AudioManager: シーンを跨いでBGMを管理するシンプルなシングルトン
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [SerializeField]
    private AudioSource bgmSource;

    [SerializeField]
    private AudioSource seSource;

    // ── BGM音量の正本（設定パネルのBGMスライダー）──────────────────────────────
    //  ・「まだ設定されていない（未設定）」状態を区別して持つ。
    //    未設定の間は、各BGMは自分のInspector設定値のまま鳴らす（初期値1で上書きしない）。
    //  ・AudioManager は用途ごとに生成タイミングが違う（設定パネル／アイテムSE／タイトルなど）ため、
    //    生成順に左右されず、ユーザーが決めた音量だけを各BGMへ配る。
    private bool hasBgmVolume;
    private float bgmVolume = 1f;

    [Tooltip("BGM音量の変更をConsoleへ出す（確認用。通常はOFFのままにしてください）。")]
    [SerializeField]
    private bool logBgmVolumeChanges = false;

    // ── 本編音の親機通知用フック ────────────────────────────────────────────────
    //   ここは「子機のローカル再生を壊さないための後段フック」。再生処理そのものは変更しない。
    //   親機への送信は購読側（ChildGameAudioSync）が行い、AudioManager は何も送らない。

    /// <summary>SEを再生した直後に発火（音量は0〜1）。</summary>
    public static event Action<AudioClip, float> SePlayed;

    /// <summary>BGMの再生状態が変わった直後に発火（clip / 再生中か / ループか / 音量）。BGMController からも発火する。</summary>
    public static event Action<AudioClip, bool, bool, float> BgmStateChanged;

    /// <summary>SE再生の通知（購読側の例外で子機のローカル再生を壊さないよう、ここで握る）。</summary>
    public static void NotifySePlayed(AudioClip clip, float volume)
    {
        try { SePlayed?.Invoke(clip, volume); }
        catch (Exception e) { Debug.LogWarning($"AudioManager.NotifySePlayed: subscriber error: {e.Message}"); }
    }

    /// <summary>BGM状態の通知（購読側の例外で子機のローカル再生を壊さないよう、ここで握る）。</summary>
    public static void NotifyBgmStateChanged(AudioClip clip, bool playing, bool loop, float volume)
    {
        try { BgmStateChanged?.Invoke(clip, playing, loop, volume); }
        catch (Exception e) { Debug.LogWarning($"AudioManager.NotifyBgmStateChanged: subscriber error: {e.Message}"); }
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        // AudioListener には一切触らない。Main Camera 側の AudioListener を使用する前提にする。

        // BGM 用 AudioSource を必ず 2D / 非ミュート / Mixer なしで生成・初期化
        if (bgmSource == null)
        {
            bgmSource = gameObject.AddComponent<AudioSource>();
        }

        bgmSource.playOnAwake = false;
        bgmSource.loop = true;
        bgmSource.spatialBlend = 0f;          // 2D 固定
        bgmSource.mute = false;               // ミュート禁止
        bgmSource.dopplerLevel = 0f;
        bgmSource.priority = 128;
        bgmSource.volume = 1f;
        bgmSource.outputAudioMixerGroup = null;   // Mixer 非使用

        // SE 用 AudioSource も常駐させる（PlayOneShot 用）
        if (seSource == null)
        {
            seSource = gameObject.AddComponent<AudioSource>();
        }

        seSource.playOnAwake = false;
        seSource.loop = false;
        seSource.spatialBlend = 0f;
        seSource.mute = false;
        seSource.dopplerLevel = 0f;
        seSource.priority = 128;
        seSource.volume = 1f;
        seSource.outputAudioMixerGroup = null;

        Debug.Log($"AudioManager.Awake: bgmSource created; spatialBlend={bgmSource.spatialBlend}, mute={bgmSource.mute}, volume={bgmSource.volume}");
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        StopBGM();
    }

    public void PlayBGM(AudioClip clip, bool loop = true, float volume = 1f)
    {
        if (clip == null)
        {
            Debug.LogWarning("AudioManager.PlayBGM: clip is null");
            return;
        }

        if (bgmSource == null)
        {
            // 念のため再初期化
            bgmSource = gameObject.AddComponent<AudioSource>();
            bgmSource.playOnAwake = false;
            bgmSource.loop = true;
            bgmSource.spatialBlend = 0f;
            bgmSource.mute = false;
            bgmSource.dopplerLevel = 0f;
            bgmSource.priority = 128;
            bgmSource.volume = 1f;
            bgmSource.outputAudioMixerGroup = null;
        }

        Debug.Log($"AudioManager.PlayBGM: play clip='{clip.name}', loop={loop}, volReq={volume}");

        bgmSource.clip = clip;
        bgmSource.loop = loop;
        bgmSource.volume = Mathf.Clamp01(volume);
        bgmSource.spatialBlend = 0f;
        bgmSource.mute = false;
        bgmSource.outputAudioMixerGroup = null;

        try
        {
            if (clip.loadState == AudioDataLoadState.Unloaded)
            {
                clip.LoadAudioData();
            }
        }
        catch { }

        bgmSource.Stop();
        bgmSource.Play();

        Debug.Log($"AudioManager.PlayBGM: Play() called; isPlaying={bgmSource.isPlaying}, volume={bgmSource.volume}, spatialBlend={bgmSource.spatialBlend}, mute={bgmSource.mute}");

        // ローカル再生の直後に、同じ処理経路から親機通知用に状態を流す。
        NotifyBgmStateChanged(clip, true, loop, bgmSource.volume);
    }

    public void StopBGM()
    {
        if (bgmSource == null) return;

        AudioClip stoppedClip = bgmSource.clip;
        bool stoppedLoop = bgmSource.loop;
        float stoppedVolume = bgmSource.volume;

        bgmSource.Stop();

        // 停止も状態として流す（子機の既存音はそのまま。親機側だけ止める）。
        NotifyBgmStateChanged(stoppedClip, false, stoppedLoop, stoppedVolume);
    }

    public void PlaySE(AudioClip clip, float volume = 1f)
    {
        if (clip == null)
        {
            return;
        }

        if (seSource == null)
        {
            seSource = gameObject.AddComponent<AudioSource>();
            seSource.playOnAwake = false;
            seSource.loop = false;
            seSource.spatialBlend = 0f;
            seSource.mute = false;
            seSource.dopplerLevel = 0f;
            seSource.priority = 128;
            seSource.volume = 1f;
            seSource.outputAudioMixerGroup = null;
        }

        seSource.PlayOneShot(clip, Mathf.Clamp01(volume));

        // ローカル再生の直後に、同じ処理経路から親機通知用に流す（まとめ送りしない）。
        NotifySePlayed(clip, Mathf.Clamp01(volume));
    }

    public void SetBgmVolume(float volume)
    {
        EnsureBgmSource();

        // ここで初めて「ユーザー設定」として確定する（この値だけが各BGMへ配られる）。
        float applied = Mathf.Clamp01(volume);
        bool changed = !hasBgmVolume || !Mathf.Approximately(bgmVolume, applied);

        hasBgmVolume = true;
        bgmVolume = applied;
        bgmSource.volume = applied;

        if (logBgmVolumeChanges && changed)
            Debug.Log($"[AudioManager] BGM音量の正本を {applied:F3} に設定しました（この値を各BGMへ配ります）。");
    }

    public void SetSeVolume(float volume)
    {
        EnsureSeSource();
        seSource.volume = Mathf.Clamp01(volume);
    }

    /// <summary>
    /// 設定パネルで決まったBGM音量（正本）。まだ決まっていなければ既定の1f。
    ///  ・各BGMへ流してよい値かどうかは TryGetBgmVolume で判定する（未設定の間は流さない）。
    ///  ・AudioManager自身のbgmSource音量（アイテムBGMの再生音量など）とは別に保持する。
    /// </summary>
    public float GetBgmVolume()
    {
        return bgmVolume;
    }

    /// <summary>
    /// 設定パネルでBGM音量が確定しているか。false の間は、各BGMは自分のInspector設定値をそのまま使う。
    /// </summary>
    public bool HasBgmVolume => hasBgmVolume;

    /// <summary>確認用ログのON/OFF（BGM音量の変更をConsoleへ出す）。</summary>
    public bool LogBgmVolumeChanges => logBgmVolumeChanges;

    /// <summary>
    /// 確定済みのBGM音量を取り出す。まだ決まっていなければ false を返す（volume には既定値を入れる）。
    ///  呼び出し側（BGMController／BgmVolumeSync）は false の間、自分のAudioSource音量を変更しない。
    /// </summary>
    public bool TryGetBgmVolume(out float volume)
    {
        volume = bgmVolume;
        return hasBgmVolume;
    }

    public float GetSeVolume()
    {
        return seSource == null ? 1f : seSource.volume;
    }

    public bool IsPlaying()
    {
        return bgmSource != null && bgmSource.isPlaying;
    }

    private void EnsureBgmSource()
    {
        if (bgmSource != null)
        {
            return;
        }

        bgmSource = gameObject.AddComponent<AudioSource>();
        bgmSource.playOnAwake = false;
        bgmSource.loop = true;
        bgmSource.spatialBlend = 0f;
        bgmSource.mute = false;
        bgmSource.dopplerLevel = 0f;
        bgmSource.priority = 128;
        bgmSource.volume = 1f;
        bgmSource.outputAudioMixerGroup = null;
    }

    private void EnsureSeSource()
    {
        if (seSource != null)
        {
            return;
        }

        seSource = gameObject.AddComponent<AudioSource>();
        seSource.playOnAwake = false;
        seSource.loop = false;
        seSource.spatialBlend = 0f;
        seSource.mute = false;
        seSource.dopplerLevel = 0f;
        seSource.priority = 128;
        seSource.volume = 1f;
        seSource.outputAudioMixerGroup = null;
    }
}
