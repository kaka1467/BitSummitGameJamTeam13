using UnityEngine;

/// <summary>
/// フィーバー中だけ、フィーバー用のBGMをループ再生する。
///  ・フィーバーが始まると、本編BGM（BGMController）をフェードアウトして一時停止し、フィーバーBGMを頭から再生する（ループ）。
///  ・フィーバーが終わると、フィーバーBGMをフェードアウトして止め、本編BGMを止めた位置から再開する。
///  ・音量は、設定パネルのBGM音量（AudioManager）に追従する。未設定の間は feverVolume のまま。
///  ・子機のローカル再生のみ。親機への音の同期（ChildGameAudioSync）は変えない。
///  ・動きは実時間で進める（寝たふり中の停止やQTE中でも、BGMのフェードは止まらない）。
/// </summary>
public class FeverBgmPlayer : MonoBehaviour
{
    [Header("フィーバーBGM")]
    [Tooltip("フィーバー中にループ再生するBGM。")]
    [SerializeField] private AudioClip feverClip;
    [Tooltip("フィーバーBGMの基準の音量（設定パネルでBGM音量が決まっていれば、そちらを優先する）。")]
    [SerializeField, Range(0f, 1f)] private float feverVolume = 1f;

    [Header("切り替え（秒）")]
    [Tooltip("フィーバー開始時、本編BGMが消えるまでの秒数。")]
    [SerializeField, Min(0f)] private float mainFadeOutSeconds = 0.25f;
    [Tooltip("フィーバー開始時、フィーバーBGMが立ち上がるまでの秒数。")]
    [SerializeField, Min(0f)] private float feverFadeInSeconds = 0.25f;
    [Tooltip("フィーバー終了時、フィーバーBGMが消えるまでの秒数。")]
    [SerializeField, Min(0f)] private float feverFadeOutSeconds = 0.4f;
    [Tooltip("フィーバー終了時、本編BGMが戻るまでの秒数。")]
    [SerializeField, Min(0f)] private float mainFadeInSeconds = 0.4f;

    [Header("参照（未設定なら自動で探す）")]
    [SerializeField] private BGMController mainBgm;

    private AudioSource _source;
    private bool _wasFever;
    private bool _mainPaused;        // 本編BGMを、このクラスが一時停止中か
    private float _feverScale;       // フィーバーBGMのフェード（0〜1）
    private float _feverTarget;      // フェードの行き先（1=鳴らす、0=止める）
    private float _mainScale = 1f;   // 本編BGMのフェード（0〜1）
    private float _mainTarget = 1f;

    public bool IsFeverPlaying => _wasFever && _source != null && _source.isPlaying && _source.clip != null;
    public AudioClip CurrentFeverClip => _source != null ? _source.clip : feverClip;
    public float CurrentFeverVolume => GetBaseVolume(feverVolume);

    private void Awake()
    {
        _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = true;
        _source.spatialBlend = 0f;
        _source.mute = AudioManager.ShouldRedirectAudioToParent();
        _source.dopplerLevel = 0f;
        _source.priority = 128;
        _source.volume = 0f;
        _source.clip = feverClip;
    }

    private void OnDestroy()
    {
        // シーンを離れるときに、止めたままの本編BGMを残さない。
        ResumeMain();
    }

    private void Update()
    {
        if (_source != null)
        {
            bool muteLocal = AudioManager.ShouldRedirectAudioToParent();
            if (_source.mute != muteLocal) _source.mute = muteLocal;
        }

        GameManager gm = GameManager.instance;
        bool fever = gm != null && gm.IsFeverActive && !gm.IsGameOver;

        if (fever != _wasFever)
        {
            _wasFever = fever;
            if (fever) OnFeverStart();
            else OnFeverEnd();
        }

        float dt = Time.unscaledDeltaTime;

        // フィーバーBGMのフェード
        float feverStep = (_feverTarget > _feverScale ? feverFadeInSeconds : feverFadeOutSeconds);
        _feverScale = Approach(_feverScale, _feverTarget, dt, feverStep);
        if (_source != null && _source.clip != null)
        {
            _source.volume = GetBaseVolume(feverVolume) * _feverScale;

            // 止める行き先で、音が消えきったら停止する
            if (_feverTarget <= 0f && _feverScale <= 0f && _source.isPlaying) _source.Stop();
        }

        // 本編BGMのフェード
        float mainStep = (_mainTarget > _mainScale ? mainFadeInSeconds : mainFadeOutSeconds);
        _mainScale = Approach(_mainScale, _mainTarget, dt, mainStep);
        if (mainBgm != null) mainBgm.VolumeScale = _mainScale;

        // 本編BGMが消えきったら一時停止する（戻すときは再開してから音量を上げる）
        if (_wasFever && _mainTarget <= 0f && _mainScale <= 0f && !_mainPaused && mainBgm != null)
        {
            if (mainBgm.PauseLocal()) _mainPaused = true;
        }
    }

    private void OnFeverStart()
    {
        if (feverClip == null)
        {
            Debug.LogWarning("[FeverBgmPlayer] フィーバーBGMが未設定です（Fever Clip を割り当ててください）。", this);
            return;
        }

        ResolveMain();

        // フィーバーBGMは頭から再生し直す。
        _source.clip = feverClip;
        _source.time = 0f;
        _source.volume = 0f;
        _source.mute = AudioManager.ShouldRedirectAudioToParent();
        _source.Play();
        _feverTarget = 1f;

        _mainTarget = 0f;   // 本編BGMはフェードアウト後に一時停止する

        // 親機へもフィーバーBGMへの切り替えを即時通知する
        AudioManager.NotifyBgmStateChanged(feverClip, true, true, GetBaseVolume(feverVolume));
    }

    private void OnFeverEnd()
    {
        _feverTarget = 0f;   // フェードアウト後に停止する

        // 本編BGMを止めた位置から再開して、音量を戻す。
        ResumeMain();
        _mainTarget = 1f;

        // 親機側のBGMも本編BGMへ戻す
        if (mainBgm != null && mainBgm.CurrentClip != null)
        {
            AudioManager.NotifyBgmStateChanged(mainBgm.CurrentClip, true, mainBgm.CurrentLoop, GetBaseVolume(1f));
        }
        else if (feverClip != null)
        {
            AudioManager.NotifyBgmStateChanged(feverClip, false, true, 0f);
        }
    }

    private void ResolveMain()
    {
        if (mainBgm == null) mainBgm = FindFirstObjectByType<BGMController>();
    }

    private void ResumeMain()
    {
        if (_mainPaused && mainBgm != null) mainBgm.ResumeLocal();
        _mainPaused = false;
    }

    /// <summary>設定パネルでBGM音量が決まっていればその値、決まっていなければ指定の既定値。</summary>
    private static float GetBaseVolume(float fallback)
    {
        if (AudioManager.Instance != null && AudioManager.Instance.TryGetBgmVolume(out float v)) return v;
        return fallback;
    }

    private static float Approach(float current, float target, float deltaTime, float seconds)
    {
        if (seconds <= 0f) return target;
        return Mathf.MoveTowards(current, target, deltaTime / seconds);
    }
}
