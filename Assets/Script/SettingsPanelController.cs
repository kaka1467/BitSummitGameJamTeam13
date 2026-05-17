using UnityEngine;
using UnityEngine.UI;

public class SettingsPanelController : MonoBehaviour
{
    [SerializeField] private Slider bgmSlider;
    [SerializeField] private Slider seSlider;
    [SerializeField] private Button resetButton;
    [SerializeField] private float defaultBgmVolume = 1f;
    [SerializeField] private float defaultSeVolume = 1f;

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

    private void HandleBgmChanged(float value)
    {
        EnsureAudioManager();
        AudioManager.Instance.SetBgmVolume(value);
    }

    private void HandleSeChanged(float value)
    {
        EnsureAudioManager();
        AudioManager.Instance.SetSeVolume(value);
    }

    public void ResetToDefaults()
    {
        EnsureAudioManager();
        AudioManager.Instance.SetBgmVolume(defaultBgmVolume);
        AudioManager.Instance.SetSeVolume(defaultSeVolume);

        if (bgmSlider != null)
        {
            bgmSlider.value = defaultBgmVolume;
        }

        if (seSlider != null)
        {
            seSlider.value = defaultSeVolume;
        }
    }

    private void SyncFromAudio()
    {
        EnsureAudioManager();

        if (bgmSlider != null)
        {
            bgmSlider.value = AudioManager.Instance.GetBgmVolume();
        }

        if (seSlider != null)
        {
            seSlider.value = AudioManager.Instance.GetSeVolume();
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
