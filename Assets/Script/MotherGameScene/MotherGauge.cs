using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// MotherGauge: Canonical suspicion gauge (0..maxGauge) and UI visualizer.
///
/// GAUGE OWNERSHIP — strict single-writer model:
///   ParentDetectionV2 is the only gameplay script that writes to this gauge
///   (continuous rise/drop via SetGaugeDirect every frame).
///   Auto-decrease and arrow-key input are DEBUG/LEGACY features; both are
///   disabled by default and must not be enabled in production gameplay.
/// </summary>
public class MotherGauge : MonoBehaviour
{
    // Number of suspicion stages (default 10)
    public int maxGauge = 10;

    // Current suspicion stage (0..maxGauge)
    public int currentGauge = 0;

    [Header("Approach Gauge Frames (visual)")]
    [Tooltip("Assign the frame Images (left-to-right) used to visualize the approach/suspicion progression.")]
    // Legacy serialized field name kept for compatibility with existing scenes/prefabs:
    // 'suspiciousFrames' previously implied a suspicion meter; it now represents approach-progress frames.
    public Image[] suspiciousFrames;

    [Header("Frame Sprites")]
    [Tooltip("Sprite used for an empty (unfilled) approach frame")]
    public Sprite frameOffSprite;
    [Tooltip("Sprite used for a filled approach frame")]
    public Sprite frameOnSprite;

    [Header("Input Settings (Editor Debug Only)")]
    [Tooltip("DEBUG ONLY. Amount to change per arrow-key press in the editor. Has no effect in production if HandleInput is removed.")]
    public int gaugeStep = 1;

    [Header("Auto Decrease Settings (DEBUG/LEGACY — disabled during gameplay)")]
    [Tooltip("DEBUG ONLY. ParentDetectionV2 sets this false at runtime. Do not enable in production.")]
    public bool enableAutoDecrease = false;
    [Tooltip("How many seconds between each automatic stage decrease (only used when enableAutoDecrease is true)")]
    public float decreaseIntervalSeconds = 20f;

    [Header("Debug")]
    [Tooltip("Log approach/suspicion gauge changes to the console")]
    public bool logOnChange = false;

    private int _lastLoggedGauge = int.MinValue;
    private float _decreaseTimer = 0f;

    private void Start()
    {
        UpdateGaugeUI();
    }

    private void OnValidate()
    {
        // Reflect inspector changes immediately
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
        // Auto-decrease is disabled by default and is overridden to false by
        // ParentDetectionV2.Start(). It exists only as a legacy/debug fallback.
        if (!enableAutoDecrease || decreaseIntervalSeconds <= 0)
        {
            return;
        }

        _decreaseTimer += Time.deltaTime;

        if (_decreaseTimer >= decreaseIntervalSeconds)
        {
            _decreaseTimer = 0f;
            Debug.Log($"[MotherGauge-AutoDecrease] INTERVAL HIT | decreasing gauge by 1 | currentGauge BEFORE={currentGauge}");
            AddGauge(-1);
        }
    }

    private void HandleInput()
    {
        if (Keyboard.current != null && Keyboard.current.rightArrowKey.isPressed)
        {
            Debug.Log($"[MotherGauge-Input] RIGHT ARROW pressed | adding gaugeStep={gaugeStep} | currentGauge BEFORE={currentGauge}");
            AddGauge(gaugeStep);
        }
        if (Keyboard.current != null && Keyboard.current.leftArrowKey.isPressed)
        {
            Debug.Log($"[MotherGauge-Input] LEFT ARROW pressed | subtracting gaugeStep={gaugeStep} | currentGauge BEFORE={currentGauge}");
            AddGauge(-gaugeStep);
        }
    }

    /// <summary>
    /// Refreshes the UI frames to match the current currentGauge value without changing it.
    /// Use this instead of AddGauge(0) when you only need to sync the visuals.
    /// </summary>
    public void RefreshUIOnly()
    {
        currentGauge = Mathf.Clamp(currentGauge, 0, maxGauge);
        UpdateGaugeUI();
        Debug.Log($"[MotherGauge-RefreshUIOnly] UI refreshed | currentGauge={currentGauge}/{maxGauge} | value NOT changed by this call");
    }

    /// <summary>
    /// Directly sets the suspicion gauge to a specific value.
    /// Use this instead of assigning currentGauge directly, so the change is always logged.
    /// </summary>
    public void SetGaugeDirect(int newValue)
    {
        int previous = currentGauge;
        currentGauge = Mathf.Clamp(newValue, 0, maxGauge);
        UpdateGaugeUI();
        Debug.Log($"[F:{Time.frameCount}][MotherGauge-SetGaugeDirect] DIRECT SET | newValue={newValue} | currentGauge BEFORE={previous} | currentGauge AFTER={currentGauge}/{maxGauge}");
    }

    /// <summary>
    /// Adjust the suspicion gauge by a discrete number of stages (positive to increase suspicion).
    /// Changes are clamped to [0, maxGauge] and immediately update the visual frames.
    /// </summary>
    public void AddGauge(int amount)
    {
        int previous = currentGauge;
        Debug.Log($"[F:{Time.frameCount}][MotherGauge-AddGauge] CALLED | amount={amount} | currentGauge BEFORE={previous}");
        currentGauge += amount;
        currentGauge = Mathf.Clamp(currentGauge, 0, maxGauge);
        UpdateGaugeUI();
        Debug.Log($"[F:{Time.frameCount}][MotherGauge-AddGauge] DONE   | currentGauge AFTER={currentGauge}/{maxGauge}");

        if (logOnChange && previous != currentGauge)
        {
            Debug.Log($"[{nameof(MotherGauge)}] suspicion={currentGauge}/{maxGauge}", this);
        }
    }

    private void UpdateGaugeUI()
    {
        if (suspiciousFrames == null || suspiciousFrames.Length == 0)
        {
            Debug.LogWarning($"[{nameof(MotherGauge)}] approach gauge frames are not assigned. Assign Image elements in the Inspector.", this);
            return;
        }

        if (maxGauge <= 0)
        {
            Debug.LogWarning($"[{nameof(MotherGauge)}] maxGauge is zero or negative. Set a positive value.", this);
            SetFrames(0);
            return;
        }

        // Map currentGauge (0..maxGauge) to frame count
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