using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// MotherGaugeの値を読み取り、親機用の怪しさUIだけを表示更新する。
/// ゲージの数値は変更しない。
/// </summary>
public class SuspicionVisualFeedback : MonoBehaviour
{
    [Header("MotherGauge")]
    [SerializeField] private MotherGauge motherGauge;

    [Header("DangerVignette")]
    [SerializeField] private RawImage frameBlue;
    [SerializeField] private RawImage framePurple;
    [SerializeField] private RawImage frameRed;

    [Header("MeterColor")]
    [SerializeField] private RawImage meterBlue;
    [SerializeField] private RawImage meterPurple;
    [SerializeField] private RawImage meterRed;

    [Header("MotherFace")]
    [SerializeField] private RawImage faceBlue;
    [SerializeField] private RawImage facePurple;
    [SerializeField] private RawImage faceRed;

    [Header("MeterGauge")]
    [Tooltip("要素0を一番下、要素9を一番上に設定します。")]
    [SerializeField] private RawImage[] meterSegments = new RawImage[10];

    [Header("Blue/Purple Flash")]
    [SerializeField, Range(0f, 1f)] private float lowMidFlashStartAlpha = 0.7f;
    [SerializeField, Min(0f)] private float lowMidFlashFadeDuration = 0.8f;
    [SerializeField, Range(0f, 1f)] private float lowMidVignetteMultiplier = 0.45f;
    [SerializeField, Range(0f, 1f)] private float lowMidMeterMultiplier = 1f;

    [Header("Red Pulse")]
    [SerializeField, Range(0f, 1f)] private float redVignetteMinAlpha = 0.12f;
    [SerializeField, Range(0f, 1f)] private float redVignetteMaxAlpha = 0.35f;
    [SerializeField, Range(0f, 1f)] private float redMeterMinAlpha = 0.45f;
    [SerializeField, Range(0f, 1f)] private float redMeterMaxAlpha = 1f;
    [SerializeField, Min(0f)] private float redPulseSpeed = 1.2f;

    private bool _missingMotherGaugeWarningShown;
    private bool _hasLastGaugeValue;
    private int _lastGaugeValue;
    private float _lowMidFlashElapsed;
    private bool _lowMidFlashActive;

    private void OnEnable()
    {
        EnsureMotherGauge();
        SetRaycastTargets(false);
        InitializeGaugeTracking();
        UpdateVisuals();
    }

    private void Start()
    {
        EnsureMotherGauge();
        InitializeGaugeTracking();
        UpdateVisuals();
    }

    private void Update()
    {
        if (!EnsureMotherGauge())
        {
            return;
        }

        UpdateVisuals();
    }

    private bool EnsureMotherGauge()
    {
        if (motherGauge != null)
        {
            return true;
        }

        motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        if (motherGauge != null)
        {
            return true;
        }

        if (!_missingMotherGaugeWarningShown)
        {
            _missingMotherGaugeWarningShown = true;
            Debug.LogWarning($"[{nameof(SuspicionVisualFeedback)}] MotherGaugeが見つかりません。UI更新を停止します。", this);
        }

        return false;
    }

    private void InitializeGaugeTracking()
    {
        if (motherGauge == null)
        {
            return;
        }

        int maxGauge = Mathf.Max(0, motherGauge.maxGauge);
        _lastGaugeValue = Mathf.Clamp(motherGauge.currentGauge, 0, maxGauge);
        _hasLastGaugeValue = true;
        _lowMidFlashElapsed = 0f;
        _lowMidFlashActive = false;
    }

    private void UpdateVisuals()
    {
        int maxGauge = Mathf.Max(0, motherGauge.maxGauge);
        int currentGauge = Mathf.Clamp(motherGauge.currentGauge, 0, maxGauge);
        int filledSegments = Mathf.Clamp(currentGauge, 0, 10);
        int colorIndex = GetColorIndex(currentGauge);

        if (!_hasLastGaugeValue)
        {
            _lastGaugeValue = currentGauge;
            _hasLastGaugeValue = true;
        }

        if (currentGauge > _lastGaugeValue && colorIndex < 2)
        {
            _lowMidFlashElapsed = 0f;
            _lowMidFlashActive = true;
        }

        if (_lowMidFlashActive)
        {
            _lowMidFlashElapsed += Time.deltaTime;
            if (lowMidFlashFadeDuration <= 0f || _lowMidFlashElapsed >= lowMidFlashFadeDuration)
            {
                _lowMidFlashActive = false;
            }
        }

        UpdateMeterSegments(filledSegments);
        UpdateFaces(colorIndex);
        UpdateLowMidFlash(colorIndex);
        UpdateRedPulse(colorIndex);
        _lastGaugeValue = currentGauge;
    }

    private static int GetColorIndex(int currentGauge)
    {
        if (currentGauge <= 3)
        {
            return 0;
        }

        if (currentGauge <= 6)
        {
            return 1;
        }

        return 2;
    }

    private void UpdateMeterSegments(int filledSegments)
    {
        if (meterSegments == null)
        {
            return;
        }

        for (int i = 0; i < meterSegments.Length; i++)
        {
            RawImage segment = meterSegments[i];
            if (segment == null)
            {
                continue;
            }

            SetAlpha(segment, i < filledSegments ? 1f : 0.2f);
        }
    }

    private void UpdateFaces(int colorIndex)
    {
        RawImage[] faces = { faceBlue, facePurple, faceRed };

        for (int i = 0; i < faces.Length; i++)
        {
            float targetAlpha = i == colorIndex ? 1f : 0f;
            SetAlpha(faces[i], targetAlpha);
        }
    }

    private void UpdateLowMidFlash(int colorIndex)
    {
        float flashAlpha = 0f;
        if (_lowMidFlashActive && colorIndex < 2 && lowMidFlashFadeDuration > 0f)
        {
            float fade = 1f - Mathf.Clamp01(_lowMidFlashElapsed / lowMidFlashFadeDuration);
            flashAlpha = lowMidFlashStartAlpha * fade;
        }

        SetAlpha(colorIndex == 0 ? frameBlue : framePurple, flashAlpha * lowMidVignetteMultiplier);
        SetAlpha(colorIndex == 0 ? meterBlue : meterPurple, flashAlpha * lowMidMeterMultiplier);
        SetAlpha(colorIndex == 0 ? framePurple : frameBlue, 0f);
        SetAlpha(colorIndex == 0 ? meterPurple : meterBlue, 0f);
    }

    private void UpdateRedPulse(int colorIndex)
    {
        if (colorIndex != 2)
        {
            SetAlpha(frameRed, 0f);
            SetAlpha(meterRed, 0f);
            return;
        }

        float pulse = redPulseSpeed <= 0f
            ? 1f
            : (Mathf.Sin(Time.time * redPulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;
        SetAlpha(frameRed, Mathf.Lerp(redVignetteMinAlpha, redVignetteMaxAlpha, pulse));
        SetAlpha(meterRed, Mathf.Lerp(redMeterMinAlpha, redMeterMaxAlpha, pulse));
    }

    private void SetRaycastTargets(bool raycastTarget)
    {
        RawImage[] images =
        {
            frameBlue, framePurple, frameRed,
            meterBlue, meterPurple, meterRed,
            faceBlue, facePurple, faceRed
        };

        SetRaycastTargets(images, raycastTarget);
        SetRaycastTargets(meterSegments, raycastTarget);
    }

    private static void SetRaycastTargets(RawImage[] images, bool raycastTarget)
    {
        if (images == null)
        {
            return;
        }

        foreach (RawImage image in images)
        {
            if (image != null)
            {
                image.raycastTarget = raycastTarget;
            }
        }
    }

    private static void SetAlpha(RawImage image, float alpha)
    {
        if (image == null)
        {
            return;
        }

        Color color = image.color;
        color.a = alpha;
        image.color = color;
    }
}
