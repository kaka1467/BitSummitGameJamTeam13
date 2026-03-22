using UnityEngine;
using UnityEngine.UI;

public class MotherGauge : MonoBehaviour
{
    public int maxGauge = 100;
    public int currentGauge = 0;

    [Header("怪しさゲージ PNG")]
    [Tooltip("Image Type を Filled / Horizontal にしたゲージ用 Image")]
    public Image suspiciousFill; // ゲージ本体

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
        if (Input.GetKey(KeyCode.RightArrow))
        {
            AddGauge(gaugeStep);
        }
        if (Input.GetKey(KeyCode.LeftArrow))
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
        if (suspiciousFill == null)
        {
            Debug.LogWarning($"[{nameof(MotherGauge)}] suspiciousFill が未設定です。Image を Inspector で割り当ててください。", this);
            return;
        }

        if (maxGauge <= 0)
        {
            Debug.LogWarning($"[{nameof(MotherGauge)}] maxGauge が 0 以下です。正の値にしてください。", this);
            suspiciousFill.fillAmount = 0f;
            return;
        }

        // Filled 以外だと fillAmount が見た目に反映されない
        if (suspiciousFill.type != Image.Type.Filled)
        {
            Debug.LogWarning($"[{nameof(MotherGauge)}] suspiciousFill の Image Type が Filled ではありません（現在: {suspiciousFill.type}）。Filled にしてください。", suspiciousFill);
        }

        float ratio = Mathf.Clamp01((float)currentGauge / maxGauge);
        suspiciousFill.fillAmount = ratio;

        if (logOnChange && _lastLoggedGauge != currentGauge)
        {
            _lastLoggedGauge = currentGauge;
            Debug.Log($"[{nameof(MotherGauge)}] currentGauge={currentGauge}/{maxGauge} fillAmount={ratio}", this);
        }
    }
}
