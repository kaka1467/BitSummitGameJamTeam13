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

    /// <summary>現在のBGM素材（親機への状態同期用・読み取り専用）。</summary>
    public AudioClip CurrentClip => audioSource != null ? audioSource.clip : null;

    /// <summary>現在BGMを再生中か（読み取り専用）。</summary>
    public bool IsPlaying => audioSource != null && audioSource.isPlaying;

    /// <summary>現在のループ設定（読み取り専用）。</summary>
    public bool CurrentLoop => audioSource == null || audioSource.loop;

    /// <summary>現在の音量（読み取り専用）。</summary>
    public float CurrentVolume => audioSource != null ? audioSource.volume : 0f;

    // 設定パネル（BGMスライダー）の音量をこのBGMへ反映する
    private void ApplyVolume()
    {
        if (audioSource == null) return;
        if (AudioManager.Instance == null) return;

        // 設定パネルで音量がまだ決まっていない間は、このBGMのInspector設定値をそのまま使う。
        // （AudioManagerが後から生成されても、初期値1で上書きしない）
        if (!AudioManager.Instance.TryGetBgmVolume(out float volume)) return;

        if (Mathf.Approximately(audioSource.volume, volume)) return;

        float before = audioSource.volume;
        audioSource.volume = volume;

        if (AudioManager.Instance.LogBgmVolumeChanges)
            Debug.Log($"[BGMController] '{name}' の音量を {before:F3} → {volume:F3} へ変更（設定パネルの値）。", this);
    }

    // BGMを止めるメソッド
    public void StopBGM()
    {
        if (audioSource == null) return;

        bool wasPlaying = audioSource.isPlaying;
        AudioClip clip = audioSource.clip;
        bool loop = audioSource.loop;
        float volume = audioSource.volume;

        audioSource.Stop();

        // 本編BGMの停止を親機へ伝える（子機の既存音はそのまま）。
        if (wasPlaying) AudioManager.NotifyBgmStateChanged(clip, false, loop, volume);
    }

    // BGMを再生するメソッド
    public void PlayBGM()
    {
        if (audioSource == null) return;

        ApplyVolume();

        if (!audioSource.isPlaying)
        {
            audioSource.Play();

            // 本編BGMの開始を親機へ伝える（親機は状態として同期し、再通知で先頭へ戻さない）。
            AudioManager.NotifyBgmStateChanged(audioSource.clip, true, audioSource.loop, audioSource.volume);
        }
    }
}
