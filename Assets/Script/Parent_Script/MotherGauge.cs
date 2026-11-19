using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// MotherGauge：正規の疑惑ゲージ（0～maxGauge）とUI表示を管理する。
///
/// ゲージの管理：
///   疑惑値（0～maxGauge）は警告サイクルをまたいで保持される。
///   増加：MotherSuspicionSystemからのAddGauge()呼び出し（大きな音、チェックイベント）。
///   減少：HandleAutoDecrease() — decreaseIntervalSecondsごとに1段階（本番動作、難易度補正込み）。
///   I／Oキー入力はエディターデバッグ専用。
/// </summary>
public class MotherGauge : MonoBehaviour
{
    [Header("シーン参照")]
    [SerializeField] private CaughtReactionController caughtReactionController;

    // 疑惑段階の数（デフォルト10）
    public int maxGauge = 10;

    // 現在の疑惑段階（0～maxGauge）
    public int currentGauge;

    [Header("接近ゲージフレーム（表示）")]
    [Tooltip("接近／疑惑の進行を表示するフレームImageを左から順に設定します。")]
    public Image[] suspiciousFrames;

    [Header("フレームスプライト")]
    [Tooltip("空の（未充填）接近フレームに使用するSprite")]
    public Sprite frameOffSprite;
    [Tooltip("充填済みの接近フレームに使用するSprite")]
    public Sprite frameOnSprite;

    [Header("入力設定（エディターデバッグ専用）")]
    [Tooltip("デバッグ専用。現在のキー入力（I／O）では未使用（旧矢印キー処理の名残）。値はシーンで保持するため削除はしていない。")]
    public int gaugeStep = 1;

    [Header("自動減少設定")]
    [Tooltip("decreaseIntervalSecondsごとに疑惑を1段階自動減少させます。通常のゲームプレイではtrueにしてください。")]
    public bool enableAutoDecrease = true;
    [Tooltip("自動で段階を減少させる間隔（秒）。enableAutoDecreaseがtrueの場合のみ使用。")]
    public float decreaseIntervalSeconds = 20f;

    [Header("オーディオ")]
    [Tooltip("ゲージが1段階以上増加するたびにワンショット音を再生するAudioSource。インスペクターで設定します。")]
    [SerializeField] private AudioSource gaugeStepAudioSource;
    [Tooltip("ゲージ段階が上がるほど、SEの音程を上げる。")]
    [SerializeField] private bool raisePitchPerStep = true;
    [Tooltip("1段階ごとに上げる半音の数。1で半音、2で全音。")]
    [SerializeField, Min(0f)] private float semitonesPerStep = 1f;

    [Header("デバッグ")]
    [Tooltip("接近／疑惑ゲージの変化をコンソールに記録する")]
    public bool logOnChange;

    private float _decreaseTimer;
    private float _baseSePitch = 1f;

    private MotherSuspicionSystem _suspicionSystem;

    public enum SuspicionColorState
    {
        Blue = 0,
        Purple = 1,
        Red = 2,
    }

    public SuspicionColorState CurrentColorState
    {
        get
        {
            if (currentGauge <= 3) return SuspicionColorState.Blue;
            if (currentGauge <= 6) return SuspicionColorState.Purple;
            return SuspicionColorState.Red;
        }
    }

    public bool IsPurple => CurrentColorState == SuspicionColorState.Purple;

    private void Awake()
    {
        if (gaugeStepAudioSource != null)
        {
            _baseSePitch = gaugeStepAudioSource.pitch;
        }
    }

    private MotherSuspicionSystem ResolveSuspicionSystem()
    {
        if (_suspicionSystem == null)
            _suspicionSystem = Object.FindFirstObjectByType<MotherSuspicionSystem>();

        return _suspicionSystem;
    }

    private void Start()
    {
        if (caughtReactionController == null)
        {
            caughtReactionController = Object.FindFirstObjectByType<CaughtReactionController>();
        }

        UpdateGaugeUI();
    }

    private void OnValidate()
    {
        currentGauge = Mathf.Clamp(currentGauge, 0, maxGauge);
        UpdateGaugeUI();
    }

    private void Update()
    {
        HandleInput();
        HandleAutoDecrease();
    }

    private void HandleAutoDecrease()
    {
        if (!enableAutoDecrease || decreaseIntervalSeconds <= 0)
        {
            return;
        }

        // 【D-R05】難易度に応じた自然減衰速度倍率（EASY=1.25x, NORMAL=1.0x, HARD=0.5x）を適用
        _decreaseTimer += Time.deltaTime * Mathf.Max(0.1f, GameDifficulty.ParentSuspicionDecayMultiplier);

        if (_decreaseTimer >= decreaseIntervalSeconds)
        {
            _decreaseTimer = 0f;
            Debug.Log($"[MotherGauge-AutoDecrease] 間隔到達 | ゲージを1減少 | currentGauge BEFORE={currentGauge} (diff={GameDifficulty.DisplayName})");
            AddGauge(-1);
        }
    }

    private void HandleInput()
    {
        if (Keyboard.current != null && Keyboard.current.iKey.wasPressedThisFrame)
        {
            Debug.Log($"[MotherGauge-Input] Iキー押下 | 疑惑をちょうど+1 | currentGauge BEFORE={currentGauge}");
            AddGauge(1);
        }
        if (Keyboard.current != null && Keyboard.current.oKey.wasPressedThisFrame)
        {
            Debug.Log($"[MotherGauge-Input] Oキー押下 | 疑惑をちょうど-1 | currentGauge BEFORE={currentGauge}");
            AddGauge(-1);
        }
    }

    public void RefreshUIOnly()
    {
        currentGauge = Mathf.Clamp(currentGauge, 0, maxGauge);
        UpdateGaugeUI();
        Debug.Log($"[MotherGauge-RefreshUIOnly] UIを更新 | currentGauge={currentGauge}/{maxGauge} | この呼び出しでは値を変更していません");
    }

    public void SetGaugeDirect(int newValue)
    {
        int previous = currentGauge;
        currentGauge = Mathf.Clamp(newValue, 0, maxGauge);
        UpdateGaugeUI();
        Debug.Log($"[F:{Time.frameCount}][MotherGauge-SetGaugeDirect] 直接設定 | newValue={newValue} | currentGauge BEFORE={previous} | currentGauge AFTER={currentGauge}/{maxGauge}");
    }

    public void AddGauge(int amount)
    {
        MotherSuspicionSystem suspicion = ResolveSuspicionSystem();
        if (suspicion != null && suspicion.ShouldBlockPositiveGaugeChange(amount))
        {
            Debug.Log($"[F:{Time.frameCount}][MotherGauge-AddGauge] 無敵モード中のため加算を抑止 | amount={amount} | currentGauge={currentGauge}/{maxGauge}");
            return;
        }

        int previous = currentGauge;
        Debug.Log($"[F:{Time.frameCount}][MotherGauge-AddGauge] 呼び出し | amount={amount} | currentGauge BEFORE={previous}");
        currentGauge += amount;
        currentGauge = Mathf.Clamp(currentGauge, 0, maxGauge);
        UpdateGaugeUI();
        Debug.Log($"[F:{Time.frameCount}][MotherGauge-AddGauge] 完了   | currentGauge AFTER={currentGauge}/{maxGauge}");

        if (currentGauge > previous)
        {
            if (gaugeStepAudioSource != null)
            {
                if (raisePitchPerStep)
                {
                    float semitones = Mathf.Max(0, currentGauge - 1) * semitonesPerStep;
                    gaugeStepAudioSource.pitch = Mathf.Clamp(_baseSePitch * Mathf.Pow(2f, semitones / 12f), 0.1f, 3f);
                }

                gaugeStepAudioSource.Play();
            }
        }

        if (logOnChange && previous != currentGauge)
        {
            Debug.Log($"[{nameof(MotherGauge)}] 疑惑={currentGauge}/{maxGauge}", this);
        }
    }

    private void UpdateGaugeUI()
    {
        if (suspiciousFrames == null || suspiciousFrames.Length == 0)
        {
            Debug.LogWarning($"[{nameof(MotherGauge)}] 接近ゲージフレームが設定されていません。インスペクターでImage要素を設定してください。", this);
            return;
        }

        if (maxGauge <= 0)
        {
            Debug.LogWarning($"[{nameof(MotherGauge)}] maxGaugeが0以下です。正の値を設定してください。", this);
            SetFrames(0);
            return;
        }

        float ratio = Mathf.Clamp01((float)currentGauge / maxGauge);
        int filledCount = Mathf.RoundToInt(ratio * suspiciousFrames.Length);
        SetFrames(filledCount);
    }

    private void SetFrames(int filledCount)
    {
        int clampedFilled = Mathf.Clamp(filledCount, 0, suspiciousFrames.Length);

        for (int i = 0; i < suspiciousFrames.Length; i++)
        {
            Image frame = suspiciousFrames[i];
            if (frame == null)
            {
                continue;
            }

            Sprite targetSprite = i < clampedFilled ? frameOnSprite : frameOffSprite;
            if (targetSprite != null)
            {
                frame.sprite = targetSprite;
            }
        }
    }
}
