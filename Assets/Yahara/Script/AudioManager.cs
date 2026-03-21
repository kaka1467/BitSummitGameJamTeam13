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

        // AudioListener が複数あると警告が出る / 音に影響することがあるため
        // 起動時に "有効な" AudioListener を1つだけ残し、他は無効化する。
        AudioListener[] listeners = FindObjectsOfType<AudioListener>();
        AudioListener primary = null;
        foreach (var l in listeners)
        {
            if (primary == null && l.enabled)
            {
                primary = l;
                continue;
            }

            if (l != primary)
            {
                // 破壊は避けて無効化
                l.enabled = false;
            }
        }

        if (primary == null)
        {
            // 有効なリスナーが見つからなければ既存のものを有効化するか、自身に追加する
            if (listeners.Length > 0)
            {
                listeners[0].enabled = true;
            }
            else
            {
                gameObject.AddComponent<AudioListener>();
            }
        }

        // マスターボリュームをデフォルトで 1 にする（音が聞こえない問題の原因になっている場合がある）
        AudioListener.volume = 1f;

        if (bgmSource == null)
        {
            bgmSource = gameObject.AddComponent<AudioSource>();
            bgmSource.playOnAwake = false;
            bgmSource.loop = true;
        }
    }

    public void PlayBGM(AudioClip clip, bool loop = true, float volume = 1f)
    {
        if (clip == null) return;

        // 同じクリップがすでに再生中なら音量/ループ設定だけ更新する
        if (bgmSource.clip == clip && bgmSource.isPlaying)
        {
            bgmSource.loop = loop;
            bgmSource.volume = Mathf.Clamp01(volume);
            return;
        }

        bgmSource.clip = clip;
        bgmSource.loop = loop;
        bgmSource.volume = Mathf.Clamp01(volume);
        bgmSource.Play();
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
