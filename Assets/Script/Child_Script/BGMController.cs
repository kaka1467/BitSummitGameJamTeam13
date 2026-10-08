using UnityEngine;

public class BGMController : MonoBehaviour
{
    private AudioSource audioSource;

    void Start()
    {
        // 自身についているAudioSourceを取得
        audioSource = GetComponent<AudioSource>();
        ApplyVolume();
    }

    void Update()
    {
        // タイトルの設定パネルで音量を変えた場合に備えて、毎フレーム追従させる
        ApplyVolume();
    }

    // AudioManager（設定パネルのBGMスライダー）の音量をこのBGMにも反映する
    private void ApplyVolume()
    {
        if (audioSource == null) return;
        if (AudioManager.Instance == null) return;

        audioSource.volume = AudioManager.Instance.GetBgmVolume();
    }

    // BGMを止めるメソッド
    public void StopBGM()
    {
        audioSource.Stop();
    }

    // BGMを再生するメソッド
    public void PlayBGM()
    {
        ApplyVolume();

        if (!audioSource.isPlaying)
        {
            audioSource.Play();
        }
    }
}
