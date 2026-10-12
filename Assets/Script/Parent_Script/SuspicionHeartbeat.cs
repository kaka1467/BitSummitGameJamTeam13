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

    [Header("デバッグ")]
    [Tooltip("ゲームオーバー演出の心拍制御（停止／開始／終了）をログに記録する。")]
    [SerializeField] private bool showDebugLogs = true;

    /// <summary>いま鼓動中か（寝たふり中。Require Sleeping がオフなら常に）。</summary>
    public bool IsBeating { get; private set; }

    /// <summary>赤い点滅の強さ（0〜1）。拍の瞬間に1、次の拍に向けて減衰する。鼓動中のみ有効。</summary>
    public float BeatPulse { get; private set; }

    /// <summary>
    /// 心拍の制御モード。
    ///  ・Normal：通常（寝たふり中だけ、ゲージの段階に応じた間隔で鳴らす）
    ///  ・SuppressedForGameOver：捕獲確定〜ドア開け完了まで（通常制御を止めて鳴らさない）
    ///  ・GameOverOverride：捕獲後（寝たふり・ゲージ段階に依存せず、赤段階と同じ間隔で鳴らし続ける）
    /// </summary>
    public enum BeatControlMode
    {
        Normal = 0,
        SuppressedForGameOver = 1,
        GameOverOverride = 2,
    }

    /// <summary>現在の制御モード。ゲームオーバー演出側が二重制御していないかの確認に使う。</summary>
    public BeatControlMode ControlMode => _mode;

    /// <summary>捕獲後のゲームオーバー演出として鳴っているか。</summary>
    public bool IsGameOverOverrideActive => _mode == BeatControlMode.GameOverOverride;

    /// <summary>
    /// 【捕獲確定】通常の心拍制御を止める（鳴らさない）。ドア開け完了まで無音にする。
    ///  ・以降は BeginGameOverBeat が呼ばれるまで鳴らない。
    ///  ・呼び出し元は GameOverPresentationController のみ（心拍の書き込み元を競合させない）。
    /// </summary>
    public void SuppressNormalBeatForGameOver()
    {
        _mode = BeatControlMode.SuppressedForGameOver;
        SetIdle();

        if (showDebugLogs)
            Debug.Log($"[{nameof(SuspicionHeartbeat)}] 捕獲確定：通常の心拍制御を停止しました（ドア開け完了まで鳴らしません）", this);
    }

    /// <summary>
    /// 【ドア開け完了】赤段階と同じテンポ（redBeatInterval）で心拍を開始する。
    ///  ・テンポはこのクラスの設定をそのまま使う（別の場所へコピーしない＝二重管理しない）。
    ///  ・寝たふり判定やゲージ段階には依存しない（捕獲後は自動減少でテンポを遅くしない）。
    ///  ・既に開始済みなら何もしない（二重再生防止）。
    /// </summary>
    public void BeginGameOverBeat()
    {
        if (_mode == BeatControlMode.GameOverOverride)
        {
            if (showDebugLogs)
                Debug.Log($"[{nameof(SuspicionHeartbeat)}] ゲームオーバー心拍は既に開始済みです（再開始しません）", this);
            return;
        }

        _mode = BeatControlMode.GameOverOverride;

        // 次のUpdateで1拍目をすぐ鳴らす（条件成立時に即1拍目を鳴らす既存挙動と同じ）。
        _wasActive = false;
        _timer = 0f;

        if (showDebugLogs)
            Debug.Log($"[{nameof(SuspicionHeartbeat)}] ドア開け完了：赤段階と同じテンポ（{redBeatInterval:F2}s間隔）で心拍を開始します", this);
    }

    /// <summary>
    /// 【終了／中断】ゲームオーバー用の心拍を止め、通常制御へ戻す。
    ///  ・シーン遷移・再プレイ・中断で呼ぶ（音を残さない）。
    /// </summary>
    public void EndGameOverBeat()
    {
        if (_mode == BeatControlMode.Normal)
        {
            SetIdle();
            return;
        }

        _mode = BeatControlMode.Normal;
        SetIdle();

        if (showDebugLogs)
            Debug.Log($"[{nameof(SuspicionHeartbeat)}] ゲームオーバー心拍を終了し、通常制御へ戻しました", this);
    }

    private float _timer;
    private bool _wasActive;
    private bool _missingSleepingWarningShown;
    private BeatControlMode _mode = BeatControlMode.Normal;

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
        // 捕獲確定〜ドア開け完了までは通常制御を止める（鳴らさない）。
        if (_mode == BeatControlMode.SuppressedForGameOver)
        {
            SetIdle();
            return;
        }

        if (_mode == BeatControlMode.Normal && sleepingController == null && !_missingSleepingWarningShown)
        {
            _missingSleepingWarningShown = true;
            Debug.LogWarning($"[{nameof(SuspicionHeartbeat)}] SleepingControllerが見つかりません。寝たふり判定ができないため鼓動しません。", this);
        }

        // 捕獲後（ゲームオーバー演出）は寝たふり判定に依存せず鳴らす
        //  ・SleepingController は捕獲時に無効化されるため、依存させると音が止まってしまう。
        bool active = _mode == BeatControlMode.GameOverOverride
                      || (sleepingController != null && sleepingController.IsSleeping);

        if (!active)
        {
            SetIdle();
            return;
        }

        // 捕獲後はゲージ段階に追従せず、赤段階と同じ間隔を使う（自動減少でテンポを遅くしない）。
        float interval = _mode == BeatControlMode.GameOverOverride ? redBeatInterval : GetBeatInterval();

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

    /// <summary>
    /// 無効化（シーン遷移・再プレイ）で、ゲームオーバー用の心拍モードと音を残さない。
    /// </summary>
    private void OnDisable()
    {
        _mode = BeatControlMode.Normal;
        SetIdle();
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
