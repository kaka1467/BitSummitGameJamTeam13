using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class MotherGauge : MonoBehaviour
{
    public int maxGauge = 100;
    public int currentGauge = 0;

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

    [Header("デバッグ")]
    public bool logOnChange = false;

    private int _lastLoggedGauge = int.MinValue;

    private void Start()
    {
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
