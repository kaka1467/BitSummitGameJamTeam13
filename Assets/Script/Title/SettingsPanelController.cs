using UnityEngine;
using UnityEngine.UI;

public class SettingsPanelController : MonoBehaviour
{
    [SerializeField] private Slider bgmSlider;
    [SerializeField] private Slider seSlider;
    [SerializeField] private Button resetButton;
    [SerializeField] private float defaultBgmVolume = 1f;
    [SerializeField] private float defaultSeVolume = 1f;

    [Header("親機")]
    [Tooltip("ON にすると、子機用 AudioManager ではなく親機の ParentAudioSettings（保存付き）へ適用する。")]
    [SerializeField] private bool useParentAudioSettings = false;

    private void Start()
    {
        if (bgmSlider != null)
        {
            bgmSlider.onValueChanged.AddListener(HandleBgmChanged);
        }

        if (seSlider != null)
        {
            seSlider.onValueChanged.AddListener(HandleSeChanged);
        }

        if (resetButton != null)
        {
            resetButton.onClick.AddListener(ResetToDefaults);
        }
    }

    private void OnEnable()
    {
        SyncFromAudio();
    }

    private void OnDestroy()
    {
        if (bgmSlider != null)
        {
            bgmSlider.onValueChanged.RemoveListener(HandleBgmChanged);
        }

        if (seSlider != null)
        {
            seSlider.onValueChanged.RemoveListener(HandleSeChanged);
        }

        if (resetButton != null)
        {
            resetButton.onClick.RemoveListener(ResetToDefaults);
        }
    }

    private void HandleBgmChanged(float value) => SetBgmVolume(value);

    private void HandleSeChanged(float value) => SetSeVolume(value);

    private void SetBgmVolume(float value)
    {
        if (useParentAudioSettings)
        {
            ParentAudioSettings.Instance?.SetBgmVolume(value);
            return;
        }

        EnsureAudioManager();
        AudioManager.Instance.SetBgmVolume(value);
    }

    private void SetSeVolume(float value)
    {
        if (useParentAudioSettings)
        {
            ParentAudioSettings.Instance?.SetSeVolume(value);
            return;
        }

        EnsureAudioManager();
        AudioManager.Instance.SetSeVolume(value);
    }

    private float GetBgmVolume()
    {
        if (useParentAudioSettings)
            return ParentAudioSettings.Instance != null ? ParentAudioSettings.BgmVolume : ParentAudioSettings.DefaultVolume;

        EnsureAudioManager();
        return AudioManager.Instance.GetBgmVolume();
    }

    private float GetSeVolume()
    {
        if (useParentAudioSettings)
            return ParentAudioSettings.Instance != null ? ParentAudioSettings.SeVolume : ParentAudioSettings.DefaultVolume;

        EnsureAudioManager();
        return AudioManager.Instance.GetSeVolume();
    }

    public void ResetToDefaults()
    {
        // 親機は既定値を子機と同じ 1.0 に合わせる（未保存時と同値）。
        float bgmDefault = useParentAudioSettings ? ParentAudioSettings.DefaultVolume : defaultBgmVolume;
        float seDefault  = useParentAudioSettings ? ParentAudioSettings.DefaultVolume : defaultSeVolume;

        SetBgmVolume(bgmDefault);
        SetSeVolume(seDefault);

        if (bgmSlider != null)
        {
            bgmSlider.value = bgmDefault;
        }

        if (seSlider != null)
        {
            seSlider.value = seDefault;
        }
    }

    private void SyncFromAudio()
    {
        if (!useParentAudioSettings) EnsureAudioManager();

        // 表示を合わせるだけなので、変更通知（onValueChanged）を起こさない方法で設定する。
        // ここで通知を起こすと、未確定（各BGMが自分のInspector値のまま鳴っている状態）の
        // 既定値1が「ユーザーが決めた音量」として確定してしまい、本編BGMの音量を上書きしてしまう。
        if (bgmSlider != null)
        {
            bgmSlider.SetValueWithoutNotify(GetBgmVolume());
        }

        // SEスライダーも同じ方針（表示だけ。操作されるまで確定しない）。
        if (seSlider != null)
        {
            seSlider.SetValueWithoutNotify(GetSeVolume());
        }
    }

    private static void EnsureAudioManager()
    {
        if (AudioManager.Instance != null)
        {
            return;
        }

        new GameObject("AudioManager").AddComponent<AudioManager>();
    }
}
