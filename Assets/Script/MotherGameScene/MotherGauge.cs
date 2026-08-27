using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// MotherGauge：正規の疑惑ゲージ（0～maxGauge）とUI表示を管理する。
///
/// ゲージの管理：
///   疑惑値（0～maxGauge）は警告サイクルをまたいで保持される。
///   増加：ParentDetectionV2からのAddGauge()呼び出し（大きな音、チェックイベント）。
///   減少：HandleAutoDecrease() — decreaseIntervalSecondsごとに1段階（本番動作）。
///   矢印キー入力はエディターデバッグ専用。
/// </summary>
public class MotherGauge : MonoBehaviour
{
    [Header("シーン参照")]
    [SerializeField] private CaughtReactionController caughtReactionController;

    // 疑惑段階の数（デフォルト10）
    public int maxGauge = 10;

    // 現在の疑惑段階（0～maxGauge）
    public int currentGauge = 0;

    [Header("接近ゲージフレーム（表示）")]
    [Tooltip("接近／疑惑の進行を表示するフレームImageを左から順に設定します。")]
    // 既存のシーン／プレハブとの互換性のため、シリアライズ済みフィールド名を維持する。
    // 'suspiciousFrames'は以前は疑惑メーターを意味していたが、現在は接近進行フレームを表す。
    public Image[] suspiciousFrames;

    [Header("フレームスプライト")]
    [Tooltip("空の（未充填）接近フレームに使用するSprite")]
    public Sprite frameOffSprite;
    [Tooltip("充填済みの接近フレームに使用するSprite")]
    public Sprite frameOnSprite;

    [Header("入力設定（エディターデバッグ専用）")]
    [Tooltip("デバッグ専用。エディターで矢印キーを1回押したときの変化量。HandleInputを削除した本番環境では効果なし。")]
    public int gaugeStep = 1;

    [Header("自動減少設定")]
    [Tooltip("decreaseIntervalSecondsごとに疑惑を1段階自動減少させます。通常のゲームプレイではtrueにしてください。")]
    public bool enableAutoDecrease = true;
    [Tooltip("自動で段階を減少させる間隔（秒）。enableAutoDecreaseがtrueの場合のみ使用。")]
    public float decreaseIntervalSeconds = 20f;

    [Header("オーディオ")]
    [Tooltip("ゲージが1段階以上増加するたびにワンショット音を再生するAudioSource。インスペクターで設定します。")]
    [SerializeField] private AudioSource gaugeStepAudioSource;

    [Header("デバッグ")]
    [Tooltip("接近／疑惑ゲージの変化をコンソールに記録する")]
    public bool logOnChange = false;

    private float _decreaseTimer = 0f;

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
        // インスペクターの変更を即座に反映する
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

        _decreaseTimer += Time.deltaTime;

        if (_decreaseTimer >= decreaseIntervalSeconds)
        {
            _decreaseTimer = 0f;
            Debug.Log($"[MotherGauge-AutoDecrease] 間隔到達 | ゲージを1減少 | currentGauge BEFORE={currentGauge}");
            AddGauge(-1);
        }
    }

    private void HandleInput()
    {
        if (Keyboard.current != null && Keyboard.current.rightArrowKey.isPressed)
        {
            Debug.Log($"[MotherGauge-Input] 右矢印キーを押下 | gaugeStep={gaugeStep}を加算 | currentGauge BEFORE={currentGauge}");
            AddGauge(gaugeStep);
        }
        if (Keyboard.current != null && Keyboard.current.leftArrowKey.isPressed)
        {
            Debug.Log($"[MotherGauge-Input] 左矢印キーを押下 | gaugeStep={gaugeStep}を減算 | currentGauge BEFORE={currentGauge}");
            AddGauge(-gaugeStep);
        }
    }

    /// <summary>
    /// currentGaugeを変更せず、UIフレームを現在値に合わせて更新する。
    /// 表示だけを同期したい場合はAddGauge(0)ではなくこちらを使う。
    /// </summary>
    public void RefreshUIOnly()
    {
        currentGauge = Mathf.Clamp(currentGauge, 0, maxGauge);
        UpdateGaugeUI();
        Debug.Log($"[MotherGauge-RefreshUIOnly] UIを更新 | currentGauge={currentGauge}/{maxGauge} | この呼び出しでは値を変更していません");
    }

    /// <summary>
    /// 疑惑ゲージを指定値に直接設定する。
    /// 変更が常に記録されるよう、currentGaugeへの直接代入ではなくこちらを使う。
    /// </summary>
    public void SetGaugeDirect(int newValue)
    {
        int previous = currentGauge;
        currentGauge = Mathf.Clamp(newValue, 0, maxGauge);
        UpdateGaugeUI();
        Debug.Log($"[F:{Time.frameCount}][MotherGauge-SetGaugeDirect] 直接設定 | newValue={newValue} | currentGauge BEFORE={previous} | currentGauge AFTER={currentGauge}/{maxGauge}");
    }

    /// <summary>
    /// 疑惑ゲージを指定した段階数だけ変更する（正数で疑惑が増加）。
    /// 値は[0, maxGauge]に収められ、表示フレームも直ちに更新される。
    /// </summary>
    public void AddGauge(int amount)
    {
        int previous = currentGauge;
        Debug.Log($"[F:{Time.frameCount}][MotherGauge-AddGauge] 呼び出し | amount={amount} | currentGauge BEFORE={previous}");
        currentGauge += amount;
        currentGauge = Mathf.Clamp(currentGauge, 0, maxGauge);
        UpdateGaugeUI();
        Debug.Log($"[F:{Time.frameCount}][MotherGauge-AddGauge] 完了   | currentGauge AFTER={currentGauge}/{maxGauge}");

        if (currentGauge > previous)
        {
            if (gaugeStepAudioSource != null)
                gaugeStepAudioSource.Play();
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

        // currentGauge（0～maxGauge）をフレーム数に変換する
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