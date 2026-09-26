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
        baseVolume = audioSource.volume;
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
            audioSource.volume = 0f;
            audioSource.Play();
        }

        isFadingOut = false;
        StartFade(baseVolume, fadeInSeconds, stopAtEnd: false);
    }

    private void StartFade(float target, float seconds, bool stopAtEnd)
    {
        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(FadeRoutine(target, seconds, stopAtEnd));
    }

    private IEnumerator FadeRoutine(float target, float seconds, bool stopAtEnd)
    {
        float from = audioSource.volume;

        if (seconds > 0f)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                audioSource.volume = Mathf.Lerp(from, target, Mathf.Clamp01(elapsed / seconds));
                yield return null;
            }
        }

        if (stopAtEnd)
        {
            audioSource.Stop();
            audioSource.volume = baseVolume; // 次に再生されても無音にならないよう戻す
        }
        else
        {
            audioSource.volume = target;
        }

        isFadingOut = false;
        fadeRoutine = null;
    }
}
