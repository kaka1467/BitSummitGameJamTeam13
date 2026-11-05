using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SettingsPanelController : MonoBehaviour
{
    [SerializeField] private Slider bgmSlider;
    [SerializeField] private Slider seSlider;
    [SerializeField] private Button resetButton;
    [SerializeField] private float defaultBgmVolume = 1f;
    [SerializeField] private float defaultSeVolume = 1f;

    [Header("Language（日本語／英語。チュートリアルの文言が切り替わる）")]
    [Tooltip("押すたびに日本語⇄英語を切り替えるボタン。未割当なら言語切り替えは出ない。")]
    [SerializeField] private Button languageButton;
    [Tooltip("現在の言語名を表示するテキスト（任意。ボタン内の文字を割り当てる）。")]
    [SerializeField] private TMP_Text languageLabel;
    [Tooltip("言語名の横幅を、ボタンの枠の幅の何割までにするか。大きいと枠いっぱい、小さいと余白が増える。")]
    [SerializeField, Range(0.3f, 1f)] private float languageLabelMaxWidthRatio = 0.8f;

    private float _languageLabelBaseSize;

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

        // 実行時に結び付ける（Inspectorの固定リスナーは、タイトルへ戻った後に破棄済みの実体を指すため使わない）。
        if (languageButton != null)
        {
            languageButton.onClick.AddListener(ToggleLanguage);
        }

        PrepareLanguageLabel();
        RefreshLanguageLabel();
    }

    private void OnEnable()
    {
        SyncFromAudio();
        RefreshLanguageLabel();
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

        if (languageButton != null)
        {
            languageButton.onClick.RemoveListener(ToggleLanguage);
        }
    }

    /// <summary>日本語⇄英語を切り替えて保存する（音量・スコアなど他の保存値には触れない）。</summary>
    public void ToggleLanguage()
    {
        GameLanguage.Toggle();
        RefreshLanguageLabel();
    }

    /// <summary>
    /// ラベルを折り返さない設定にし、基準の文字サイズ（シーンの設定値）を覚える。
    /// 言語名が枠からはみ出さないよう縮める処理は FitLanguageLabelToButton が行う。
    /// </summary>
    private void PrepareLanguageLabel()
    {
        if (languageLabel == null) return;

        _languageLabelBaseSize = languageLabel.fontSize;
        languageLabel.enableAutoSizing = false;
        languageLabel.textWrappingMode = TextWrappingModes.NoWrap;
        languageLabel.overflowMode = TextOverflowModes.Overflow;
    }

    /// <summary>
    /// 言語名の横幅が、ボタンの枠の幅（の一定割合）に収まるよう文字サイズを調整する。
    /// 文字の入っている矩形ではなく「ボタン自身の枠」を基準にする（矩形が枠より広くても収まる）。
    /// 収まるときは基準サイズのまま。
    /// </summary>
    private void FitLanguageLabelToButton()
    {
        if (languageLabel == null || languageButton == null || _languageLabelBaseSize <= 0f) return;

        languageLabel.fontSize = _languageLabelBaseSize;

        RectTransform frame = languageButton.transform as RectTransform;
        if (frame == null) return;

        float frameWidth = frame.rect.width * Mathf.Abs(frame.lossyScale.x);
        float labelScale = Mathf.Abs(languageLabel.rectTransform.lossyScale.x);
        if (frameWidth <= 0f || labelScale <= 0f) return;

        float textWidth = languageLabel.GetPreferredValues(languageLabel.text, 0f, 0f).x * labelScale;
        float allowed = frameWidth * languageLabelMaxWidthRatio;
        if (textWidth <= allowed || textWidth <= 0f) return;

        languageLabel.fontSize = Mathf.Max(_languageLabelBaseSize * 0.2f, _languageLabelBaseSize * allowed / textWidth);
    }

    private void RefreshLanguageLabel()
    {
        if (languageLabel == null) return;

        languageLabel.text = GameLanguage.IsEnglish ? "English" : "日本語";
        FitLanguageLabelToButton();
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

        // 表示を合わせるだけなので、変更通知（onValueChanged）を起こさない方法で設定する。
        // ここで通知を起こすと、未確定（各BGMが自分のInspector値のまま鳴っている状態）の
        // 既定値1が「ユーザーが決めた音量」として確定してしまい、本編BGMの音量を上書きしてしまう。
        if (bgmSlider != null)
        {
            bgmSlider.SetValueWithoutNotify(AudioManager.Instance.GetBgmVolume());
        }

        // SEスライダーも同じ方針（表示だけ。操作されるまで確定しない）。
        if (seSlider != null)
        {
            seSlider.SetValueWithoutNotify(AudioManager.Instance.GetSeVolume());
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
