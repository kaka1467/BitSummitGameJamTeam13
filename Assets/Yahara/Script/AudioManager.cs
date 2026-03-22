// AudioManager: シーンを跨いでBGMを管理するシンプルなシングルトン
using UnityEngine;

public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [SerializeField]
    private AudioSource bgmSource;

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

        Debug.Log($"AudioManager.Awake: bgmSource created; spatialBlend={bgmSource.spatialBlend}, mute={bgmSource.mute}, volume={bgmSource.volume}");
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
    }

    public void StopBGM()
    {
        if (bgmSource == null) return;
        bgmSource.Stop();
    }

    public bool IsPlaying()
    {
        return bgmSource != null && bgmSource.isPlaying;
    }
}
