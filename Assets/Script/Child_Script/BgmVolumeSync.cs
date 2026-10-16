using UnityEngine;

/// <summary>
/// このAudioSourceの音量を、設定パネルのBGMスライダー（AudioManager.SetBgmVolume）に合わせて追従させる。
/// Play On Awake でループ再生しているだけの単独BGM（AudioManager を経由しないもの）に付ける。
/// ・AudioManager がまだ存在しない場合は何もせず、Inspector で設定した元の音量のまま動く。
/// ・設定パネルで音量がまだ決まっていない間も、Inspector の音量をそのまま使う（初期値1を流し込まない）。
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

        // 設定パネルで音量が確定していない間は、この AudioSource の Inspector 値を維持する。
        if (!AudioManager.Instance.TryGetBgmVolume(out float volume)) return;

        if (Mathf.Approximately(audioSource.volume, volume)) return;

        audioSource.volume = volume;
    }
}
