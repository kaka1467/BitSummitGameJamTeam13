using UnityEngine;

/// <summary>
/// このAudioSourceの音量を、設定パネルのBGMスライダー（AudioManager.SetBgmVolume）に合わせて追従させる。
/// Play On Awake でループ再生しているだけの単独BGM（AudioManager を経由しないもの）に付ける。
/// AudioManager がまだ存在しない場合は何もせず、Inspector で設定した元の音量のまま動く。
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class BgmVolumeSync : MonoBehaviour
{
    private AudioSource audioSource;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
    }

    private void Update()
    {
        if (AudioManager.Instance == null) return;
        audioSource.volume = AudioManager.Instance.GetBgmVolume();
    }
}
