using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class MotherGauge : MonoBehaviour
{
    public int maxGauge = 100;
    public int currentGauge = 0;

    [Header("Scene Change")]
    [SerializeField] private CaughtReactionController caughtReactionController;

    [Header("怪しさゲージ 枠(10個)")]
    [Tooltip("左から順に並べた枠 Image を10個登録")]
    public Image[] suspiciousFrames;

    [Header("枠スプライト")]
    [Tooltip("ゲージが埋まっていない枠のスプライト")]
    public Sprite frameOffSprite;
    [Tooltip("ゲージが埋まった枠のスプライト")]
    public Sprite frameOnSprite;

    [Header("入力設定")]
    [Tooltip("1フレームごとに増減する量")]
    public int gaugeStep = 1;

    [Header("自動減少設定")]
    [Tooltip("自動減少を有効にするか")]
    public bool enableAutoDecrease = true;
    [Tooltip("何秒ごとに1フレーム分減少するか")]
    public float decreaseIntervalSeconds = 20f;

    [Header("デバッグ")]
    public bool logOnChange = false;

    private int _lastLoggedGauge = int.MinValue;
    private float _decreaseTimer = 0f;
    private bool _hasTriggeredMax = false;

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
        // Inspector 変更時にも反映（Play中でなくても見た目を合わせる）
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
            
            // 1フレーム分のゲージ量を計算
            int gaugePerFrame = Mathf.CeilToInt((float)maxGauge / suspiciousFrames.Length);
            AddGauge(-gaugePerFrame);
        }
    }

    private void HandleInput()
    {
        if (Keyboard.current != null && Keyboard.current.rightArrowKey.isPressed)
        {
            AddGauge(gaugeStep);
        }
        if (Keyboard.current != null && Keyboard.current.leftArrowKey.isPressed)
        {
            AddGauge(-gaugeStep);
        }
    }

    public void AddGauge(int amount)
    {
        currentGauge += amount;
        currentGauge = Mathf.Clamp(currentGauge, 0, maxGauge);
        UpdateGaugeUI();
    }

    private void UpdateGaugeUI()
    {
        if (suspiciousFrames == null || suspiciousFrames.Length == 0)
        {
            Debug.LogWarning($"[{nameof(MotherGauge)}] suspiciousFrames が未設定です。Image を Inspector で割り当ててください。", this);
            return;
        }

        if (maxGauge <= 0)
        {
            Debug.LogWarning($"[{nameof(MotherGauge)}] maxGauge が 0 以下です。正の値にしてください。", this);
            SetFrames(0);
            return;
        }

        float ratio = Mathf.Clamp01((float)currentGauge / maxGauge);
        int filledCount = Mathf.RoundToInt(ratio * suspiciousFrames.Length);
        SetFrames(filledCount);

        if (!_hasTriggeredMax && currentGauge >= maxGauge)
        {
            _hasTriggeredMax = true;
            if (caughtReactionController != null)
            {
                caughtReactionController.ForceGameOver();
            }
            else
            {
                Debug.LogWarning($"[{nameof(MotherGauge)}] CaughtReactionController が未設定です。", this);
            }
        }

        if (logOnChange && _lastLoggedGauge != currentGauge)
        {
            _lastLoggedGauge = currentGauge;
            Debug.Log($"[{nameof(MotherGauge)}] currentGauge={currentGauge}/{maxGauge} filledFrames={filledCount}/{suspiciousFrames.Length}", this);
        }
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