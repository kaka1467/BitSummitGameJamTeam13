using UnityEngine;

public class BGMController : MonoBehaviour
{
    private AudioSource audioSource;

    void Start()
    {
        // 自身についているAudioSourceを取得
        audioSource = GetComponent<AudioSource>();
    }

    // BGMを止めるメソッド
    public void StopBGM()
    {
        audioSource.Stop();
    }

    // BGMを再生するメソッド
    public void PlayBGM()
    {
        if (!audioSource.isPlaying)
        {
            audioSource.Play();
        }
    }
}