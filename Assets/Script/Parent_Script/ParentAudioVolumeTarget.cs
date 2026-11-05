using UnityEngine;

/// <summary>
/// ParentAudioVolumeTarget：親機の AudioSource に付け、基準音量に BGM／SE 倍率を掛けて適用する。
///  ・基準音量は有効化時に捕捉し、毎回「基準 × 倍率」で設定する（現在値へ掛けて累積しない）。
///  ・pitch・3D設定・Mixerルーティングには触れない（音量だけを変更する）。
///  ・ParentAudioSettings から自動で倍率が適用される（スライダー操作・Reset・シーンロード時）。
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class ParentAudioVolumeTarget : MonoBehaviour
{
    public enum Kind { Bgm, Se }

    [Tooltip("この音源を BGM として扱うか SE として扱うか。")]
    [SerializeField] private Kind kind = Kind.Se;

    private AudioSource _source;
    private float _baseVolume = 1f;
    private bool _captured;

    private void Awake()
    {
        _source = GetComponent<AudioSource>();
        CaptureBase();
    }

    private void OnEnable()
    {
        if (_source == null) _source = GetComponent<AudioSource>();
        if (!_captured) CaptureBase();

        ParentAudioSettings.Register(this);
        Apply();
    }

    private void OnDisable()
    {
        ParentAudioSettings.Unregister(this);
    }

    private void CaptureBase()
    {
        if (_source == null) return;
        _baseVolume = _source.volume;   // Inspector の基準音量を1回だけ捕捉
        _captured = true;
    }

    /// <summary>基準音量 × 倍率を適用する（累積しない）。</summary>
    public void Apply()
    {
        if (_source == null) return;
        if (!_captured) CaptureBase();

        float multiplier = kind == Kind.Bgm ? ParentAudioSettings.BgmVolume : ParentAudioSettings.SeVolume;
        _source.volume = Mathf.Clamp01(_baseVolume * multiplier);
    }
}
