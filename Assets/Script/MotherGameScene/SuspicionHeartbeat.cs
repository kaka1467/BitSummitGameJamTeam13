using UnityEngine;

/// <summary>
/// 寝たふり中だけ心臓の音を鳴らす。
/// 怪しさゲージの段階に応じて鼓動間隔を切り替える。ゲージの値は読み取るだけで変更しない。
/// - 心音の音源は heartbeatAudioSource の Audio Clip（1拍ぶんの短い音）に設定する。
/// - 鼓動のタイミングは IsBeating / BeatPulse として公開し、SuspicionVisualFeedback の赤い点滅が同期に使う。
///   （Clipが未設定でも鼓動の計時は動くので、点滅だけの確認もできる）
/// </summary>
public class SuspicionHeartbeat : MonoBehaviour
{
    [Header("参照")]
    [Tooltip("未設定の場合はStart時に自動検索します。")]
    [SerializeField] private MotherGauge motherGauge;

    [Tooltip("寝たふり中かの判定に使用。未設定の場合はStart時に自動検索します。")]
    [SerializeField] private SleepingController sleepingController;

    [Tooltip("1拍ぶんの心音のClipを設定したAudioSource。Play On Awakeはオフ、Loopもオフにしてください。")]
    [SerializeField] private AudioSource heartbeatAudioSource;

    [Header("鼓動の間隔（秒）")]
    [Tooltip("青（安全・低）の鼓動間隔。")]
    [SerializeField, Min(0.01f)] private float blueBeatInterval = 1.35f;
    [Tooltip("紫（警戒・中）の鼓動間隔。")]
    [SerializeField, Min(0.01f)] private float purpleBeatInterval = 0.9f;
    [Tooltip("赤（危険・高）の鼓動間隔。")]
    [SerializeField, Min(0.01f)] private float redBeatInterval = 0.45f;

    [Header("音量")]
    [SerializeField, Range(0f, 1f)] private float volume = 1f;

    [Header("点滅用パルス")]
    [Tooltip("1拍の直後に1になり、次の拍に向けて0へ減衰する。大きいほど「ドクッ」と素早く消える。")]
    [SerializeField, Min(0.1f)] private float pulseDecayPower = 2f;

    /// <summary>いま鼓動中か（寝たふり中。Require Sleeping がオフなら常に）。</summary>
    public bool IsBeating { get; private set; }

    /// <summary>赤い点滅の強さ（0〜1）。拍の瞬間に1、次の拍に向けて減衰する。鼓動中のみ有効。</summary>
    public float BeatPulse { get; private set; }

    private float _timer;
    private bool _wasActive;
    private bool _missingSleepingWarningShown;

    private void Start()
    {
        if (motherGauge == null)
        {
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        }

        if (sleepingController == null)
        {
            sleepingController = Object.FindFirstObjectByType<SleepingController>();
        }
    }

    private void Update()
    {
        if (sleepingController == null && !_missingSleepingWarningShown)
        {
            _missingSleepingWarningShown = true;
            Debug.LogWarning($"[{nameof(SuspicionHeartbeat)}] SleepingControllerが見つかりません。寝たふり判定ができないため鼓動しません。", this);
        }

        bool active = sleepingController != null && sleepingController.IsSleeping;

        if (!active)
        {
            SetIdle();
            return;
        }

        float interval = GetBeatInterval();

        // 条件を満たした瞬間に1拍目をすぐ鳴らす
        if (!_wasActive)
        {
            _wasActive = true;
            _timer = interval;
        }

        _timer += Time.deltaTime;
        if (_timer >= interval)
        {
            _timer -= interval;
            PlayBeat();
        }

        IsBeating = true;
        float progress = Mathf.Clamp01(_timer / interval);
        BeatPulse = Mathf.Pow(1f - progress, pulseDecayPower);
    }

    private void SetIdle()
    {
        _wasActive = false;
        _timer = 0f;
        IsBeating = false;
        BeatPulse = 0f;

        if (heartbeatAudioSource != null)
            heartbeatAudioSource.Stop();
    }

    private void PlayBeat()
    {
        if (heartbeatAudioSource == null || heartbeatAudioSource.clip == null)
        {
            return;
        }

        // PlayOneShotなので、速い鼓動でも前の拍を切らずに重ねて鳴らせる
        heartbeatAudioSource.PlayOneShot(heartbeatAudioSource.clip, volume);
    }

    // 既存の疑惑UIと同じく、ゲージを青（0～3）、紫（4～6）、赤（7以上）に分ける。
    private float GetBeatInterval()
    {
        if (motherGauge == null)
            return blueBeatInterval;

        if (motherGauge.currentGauge <= 3)
            return blueBeatInterval;

        if (motherGauge.currentGauge <= 6)
            return purpleBeatInterval;

        return redBeatInterval;
    }
}
