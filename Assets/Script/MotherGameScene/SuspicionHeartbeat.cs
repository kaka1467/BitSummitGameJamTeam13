using UnityEngine;

/// <summary>
/// 寝たふり中、心臓の音を鳴らす（ゲージ値による発動条件はなし）。
/// 怪しさゲージが高いほど鼓動が速くなる。ゲージの値は読み取るだけで変更しない。
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

    [Header("鳴らす条件")]
    [Tooltip("trueなら寝たふり中だけ鼓動する。falseなら常に鼓動する。")]
    [SerializeField] private bool requireSleeping = true;

    [Header("鼓動の速さ（1分あたりの拍数）")]
    [Tooltip("ゲージが0のときの速さ。")]
    [SerializeField, Min(1f)] private float bpmAtStart = 70f;
    [Tooltip("ゲージが最大（maxGauge）のときの速さ。")]
    [SerializeField, Min(1f)] private float bpmAtMax = 160f;

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
        if (requireSleeping && sleepingController == null && !_missingSleepingWarningShown)
        {
            _missingSleepingWarningShown = true;
            Debug.LogWarning($"[{nameof(SuspicionHeartbeat)}] SleepingControllerが見つかりません。寝たふり判定ができないため鼓動しません。", this);
        }

        bool active = !requireSleeping || (sleepingController != null && sleepingController.IsSleeping);

        if (!active)
        {
            SetIdle();
            return;
        }

        float interval = 60f / GetBpm();

        // 条件を満たした瞬間に1拍目をすぐ鳴らす
        if (!_wasActive)
        {
            _wasActive = true;
            _timer = interval;
        }

        _timer += Time.deltaTime;
        if (_timer >= interval)
        {
            _timer = 0f;
            PlayBeat();
        }

        IsBeating = true;
        float progress = Mathf.Clamp01(_timer / interval);
        BeatPulse = Mathf.Pow(1f - progress, pulseDecayPower);
    }

    private void SetIdle()
    {
        _wasActive = false;
        IsBeating = false;
        BeatPulse = 0f;
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

    // ゲージ 0 → maxGauge で bpmAtStart → bpmAtMax に線形補間する（ゲージ未取得なら bpmAtStart）
    private float GetBpm()
    {
        if (motherGauge == null || motherGauge.maxGauge <= 0)
        {
            return bpmAtStart;
        }

        float t = Mathf.Clamp01((float)motherGauge.currentGauge / motherGauge.maxGauge);
        return Mathf.Lerp(bpmAtStart, bpmAtMax, t);
    }
}
