using System.Collections;
using UnityEngine;

/// <summary>
/// タイトル画面のBGMをループ再生し、フェードアウト／フェードインを行う。
/// - BGM の曲・音量は、同じ GameObject の AudioSource に Inspector で設定する（音量は基準値として記憶する）。
/// - TitleScreenVideoPlayer: 放置動画の再生開始で FadeOut()、動画終了・スキップで FadeIn() を呼ぶ。
/// - ParentUdpSender: 子機から START_GAME 受信時に FadeOut(false) を呼ぶ（以後は FadeIn しない）。
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class TitleBgmFader : MonoBehaviour
{
    [Header("Fade")]
    [Tooltip("フェードアウトにかける秒数。0 なら即停止。")]
    [SerializeField, Min(0f)] private float fadeOutSeconds = 1f;
    [Tooltip("フェードイン（BGM復帰）にかける秒数。0 なら即復帰。")]
    [SerializeField, Min(0f)] private float fadeInSeconds = 1f;

    public float FadeOutSeconds => fadeOutSeconds;

    private AudioSource audioSource;
    private Coroutine fadeRoutine;
    private float baseVolume = 1f;
    private float _stagingVolume = 1f;   // 設定倍率を掛ける前のフェード音量（演出用）
    private bool isFadingOut;
    private bool resumeLocked;

    // コンポーネント追加時（エディタ）に、タイトルBGM向けの初期値を入れる
    private void Reset()
    {
        AudioSource source = GetComponent<AudioSource>();
        source.loop = true;
        source.playOnAwake = true;
        source.spatialBlend = 0f;
    }

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        baseVolume = audioSource.volume;   // Inspector の基準音量（ユーザー設定倍率は掛けない）
        _stagingVolume = baseVolume;
    }

    private void OnEnable()
    {
        // ユーザー音量設定の変化に追従する（書き合いしない：下の ApplyVolume が唯一の書き込み）。
        ParentAudioSettings.Changed += OnAudioSettingsChanged;
    }

    private void OnDisable()
    {
        ParentAudioSettings.Changed -= OnAudioSettingsChanged;
    }

    /// <summary>
    /// 演出用のフェード音量（設定倍率を掛ける前）へ、最新のBGM設定倍率を一度だけ掛けて AudioSource へ適用する。
    ///  ・フェード中に倍率が変わっても、次回の ApplyVolume から出力へ即反映される（フェードはやり直さない）。
    ///  ・倍率0なら出力も0（フェード完了を待たずに無音）。倍率を戻せば、その時点の演出音量へ戻る。
    ///  ・現在値へ倍率を掛け直さない（必ず「演出音量 × 倍率」を算出する）。
    /// </summary>
    private void ApplyVolume()
    {
        if (audioSource == null) return;
        audioSource.volume = Mathf.Clamp01(_stagingVolume * ParentAudioSettings.BgmVolume);
    }

    /// <summary>設定倍率の変更に追従する（演出用のフェード音量はそのまま、最新倍率だけを掛け直す）。</summary>
    private void OnAudioSettingsChanged()
    {
        ApplyVolume();
    }

    /// <summary>
    /// フェードアウトして停止する。allowResume=false なら以後 FadeIn を受け付けない。
    /// 戻り値: フェード中（または開始した）か。BGM が鳴っていなければ false。
    /// </summary>
    public bool FadeOut(bool allowResume = true)
    {
        if (!allowResume) resumeLocked = true;
        if (isFadingOut) return true;
        if (audioSource == null || !audioSource.isPlaying) return false;

        isFadingOut = true;
        StartFade(0f, fadeOutSeconds, stopAtEnd: true);
        return true;
    }

    /// <summary>止まっている BGM を頭から再生し、基準音量までフェードインする。</summary>
    public void FadeIn()
    {
        if (resumeLocked || audioSource == null || audioSource.clip == null) return;
        if (audioSource.isPlaying && !isFadingOut) return; // すでに通常再生中

        if (!audioSource.isPlaying)
        {
            _stagingVolume = 0f;
            audioSource.volume = 0f;
            audioSource.Play();
        }

        isFadingOut = false;
        StartFade(baseVolume, fadeInSeconds, stopAtEnd: false);
    }

    private void StartFade(float stagingTarget, float seconds, bool stopAtEnd)
    {
        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(FadeRoutine(stagingTarget, seconds, stopAtEnd));
    }

    private IEnumerator FadeRoutine(float stagingTarget, float seconds, bool stopAtEnd)
    {
        // 演出用のフェード音量（設定倍率を掛ける前）を進める。最終適用時に最新倍率を一度だけ掛ける。
        float from = _stagingVolume;

        if (seconds > 0f)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                _stagingVolume = Mathf.Lerp(from, stagingTarget, Mathf.Clamp01(elapsed / seconds));
                ApplyVolume();   // 毎フレーム最新倍率を掛けて適用（倍率0なら即0）
                yield return null;
            }
        }

        _stagingVolume = stagingTarget;

        if (stopAtEnd)
        {
            audioSource.Stop();
        }
        ApplyVolume();

        isFadingOut = false;
        fadeRoutine = null;
    }
}
