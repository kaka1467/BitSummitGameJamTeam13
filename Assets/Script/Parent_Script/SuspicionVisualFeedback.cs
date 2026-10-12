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

    [Header("Heartbeat Link")]
    [Tooltip("設定すると、鼓動中は赤い点滅が心音の拍に同期する。未設定ならStart時に自動検索（無ければ従来のサイン波）。")]
    [SerializeField] private SuspicionHeartbeat heartbeat;

    [Header("Red Pulse")]
    [SerializeField, Range(0f, 1f)] private float redVignetteMinAlpha = 0.12f;
    [SerializeField, Range(0f, 1f)] private float redVignetteMaxAlpha = 0.35f;
    [SerializeField, Range(0f, 1f)] private float redMeterMinAlpha = 0.45f;
    [SerializeField, Range(0f, 1f)] private float redMeterMaxAlpha = 1f;
    [SerializeField, Min(0f)] private float redPulseSpeed = 1.2f;

    [Header("捕獲後（ゲームオーバー）の表示")]
    [Tooltip("【必須】「Meter Frame」オブジェクトを指定します（メーターの枠）。\n" +
             "捕獲確定時に非表示にします。他のメーターUI（Meter Color / Meter Gauge / Mother Face）は\n" +
             "既存の画像参照から自動で親をたどって非表示にするため、指定不要です。")]
    [SerializeField] private GameObject meterFrameRoot;

    [Tooltip("追加で非表示にしたいUI（「怪しさ」の文字・数値など）。\n" +
             "※DangerVignette（Frame blue / purple / red）や、このコンポーネント自身が付いているオブジェクトは指定しないでください。\n" +
             "  指定された場合は安全のため非表示にせず、アルファ0で隠します（警告を出します）。")]
    [SerializeField] private GameObject[] hideOnCapture = new GameObject[0];

    [Header("デバッグ")]
    [Tooltip("捕獲後の表示切り替え（メーター非表示／DangerVignette保持）をログに記録する。")]
    [SerializeField] private bool showDebugLogs = true;

    private bool _capturedForGameOver;
    private bool _hideTargetsWarningShown;
    private bool _meterUiWarningShown;

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
        if (heartbeat == null)
        {
            heartbeat = Object.FindFirstObjectByType<SuspicionHeartbeat>();
        }

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
        int colorIndex = GetColorIndex(motherGauge, currentGauge);

        // 捕獲後：ゲージの自動減少や通常更新で段階が下がっても、危険（赤）表示を結果画面まで保持する。
        //  ・メーター／文字だけを隠す（DangerVignetteは残す）
        //  ・このコンポーネントは表示だけを担当する（捕獲判定や他のシステムは停止しない）
        if (_capturedForGameOver)
        {
            HideMeterImages();
            UpdateRedPulse(2);
            _lastGaugeValue = currentGauge;
            return;
        }

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

    /// <summary>
    /// ゲージ値からUI色段階（0=Blue / 1=Purple / 2=Red）を決める。
    ///
    /// 判定の定義元は MotherGauge.CurrentColorState ひとつ。
    /// UIの色Imageを読んで判定するのではなく、同じゲージ値から同じ境界で求める
    /// （母親の目の色とUIが必ず一致する）。
    /// </summary>
    private static int GetColorIndex(MotherGauge gauge, int currentGauge)
    {
        // MotherGaugeがあれば共通判定を使う（境界の二重定義を避ける）。
        if (gauge != null)
            return (int)gauge.CurrentColorState;

        // MotherGaugeが無い場合のフォールバック（境界は MotherGauge と同一に保つ）。
        if (currentGauge <= 3) return 0;
        if (currentGauge <= 6) return 1;
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

        float pulse;
        if (heartbeat != null && heartbeat.IsBeating)
        {
            // 鼓動中は心音の拍に同期（拍の瞬間に最大、次の拍に向けて減衰）
            pulse = heartbeat.BeatPulse;
        }
        else
        {
            pulse = redPulseSpeed <= 0f
                ? 1f
                : (Mathf.Sin(Time.time * redPulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;
        }

        SetAlpha(frameRed, Mathf.Lerp(redVignetteMinAlpha, redVignetteMaxAlpha, pulse));
        SetAlpha(meterRed, Mathf.Lerp(redMeterMinAlpha, redMeterMaxAlpha, pulse));
    }

    /// <summary>
    /// 捕獲確定時の表示切り替え（GameOverPresentationController から呼ばれる唯一の入口）。
    ///  ・true：怪しさメーター／「怪しさ」表示を隠し、DangerVignette（赤）は結果画面まで保持する。
    ///  ・false：表示を通常へ戻す（再プレイ用）。
    ///  ・捕獲判定や他のシステムは停止しない（このコンポーネントは表示だけを担当する）。
    ///  ・hideOnCapture 未設定でも、参照を持っているメーター画像はアルファ0で隠す（警告を出す）。
    /// </summary>
    public void SetCapturedForGameOver(bool captured)
    {
        _capturedForGameOver = captured;

        if (EnsureMotherGauge())
        {
            UpdateVisuals();   // captured=true のときはメーターを隠し、赤（Danger）を保持する
        }

        ApplyHideTargets(captured);          // 追加指定（「怪しさ」の文字など）
        ApplyMeterUiVisibility(captured);    // 枠・色・ゲージ・顔（既存参照から導出＋Meter Frame）

        if (showDebugLogs)
        {
            string meterState = captured ? "非表示" : "表示";
            string vignetteState = captured ? "保持" : "通常";
            Debug.Log($"[{nameof(SuspicionVisualFeedback)}] 捕獲後の表示切り替え: captured={captured} " +
                      $"（メーター={meterState} / DangerVignette={vignetteState}）", this);
        }
    }

    /// <summary>
    /// hideOnCapture に指定されたオブジェクトの表示／非表示を切り替える。
    ///  ・DangerVignette を含むオブジェクトは絶対に非表示にしない（捕獲時も危険表示を残すため）。
    ///  ・このコンポーネント自身が付いているオブジェクトも対象外（表示を戻せなくなるため）。
    ///  ・未設定なら警告のみ（メーター画像はアルファ0で隠れるので演出は止まらない）。
    /// </summary>
    private void ApplyHideTargets(bool hide)
    {
        if (hideOnCapture == null || hideOnCapture.Length == 0)
        {
            if (hide && !_hideTargetsWarningShown)
            {
                _hideTargetsWarningShown = true;
                Debug.LogWarning($"[{nameof(SuspicionVisualFeedback)}] 【設定不足】捕獲時に非表示にするUI（hideOnCapture）が未設定です。" +
                                 "参照を持っているメーター画像はアルファ0で隠しますが、メーター枠や文字などは隠れません。" +
                                 "Inspectorで『Suspicion UI』配下のメーター／文字のオブジェクトを設定してください" +
                                 "（DangerVignette とこのコンポーネント自身のオブジェクトは指定しない）", this);
            }
            return;
        }

        foreach (GameObject target in hideOnCapture)
        {
            if (target == null)
            {
                continue;
            }

            if (hide && target == gameObject)
            {
                Debug.LogWarning($"[{nameof(SuspicionVisualFeedback)}] hideOnCapture に自身のオブジェクト（{target.name}）が指定されています。" +
                                 "このコンポーネントが停止して表示を戻せなくなるため、非表示にしません", this);
                continue;
            }

            if (hide && ContainsVignette(target))
            {
                Debug.LogWarning($"[{nameof(SuspicionVisualFeedback)}] hideOnCapture に指定された '{target.name}' は DangerVignette を含むため、" +
                                 "非表示にしません（捕獲時も危険表示を残すため）", this);
                continue;
            }

            if (target.activeSelf != !hide)
            {
                target.SetActive(!hide);
            }
        }
    }

    /// <summary>
    /// メーター側（Meter Color / Meter Gauge / Mother Face）の画像だけをアルファ0にする。
    ///  ・DangerVignette（frameBlue / framePurple / frameRed）には触れない（捕獲時も残す）。
    ///  ・Meter Frame など参照を持たない画像は hideOnCapture による非表示で対応する。
    /// </summary>
    private void HideMeterImages()
    {
        SetAlpha(meterBlue, 0f);
        SetAlpha(meterPurple, 0f);
        SetAlpha(meterRed, 0f);
        SetAlpha(faceBlue, 0f);
        SetAlpha(facePurple, 0f);
        SetAlpha(faceRed, 0f);

        if (meterSegments == null)
        {
            return;
        }

        foreach (RawImage segment in meterSegments)
        {
            SetAlpha(segment, 0f);
        }
    }

    /// <summary>
    /// 捕獲時に非表示にするメーターUI一式（枠・色・ゲージ・顔）の表示／非表示を切り替える。
    ///  ・Meter Frame       → meterFrameRoot（必須参照）
    ///  ・Meter Color       → meterBlue の親
    ///  ・Meter Gauge       → meterSegments[0] の親
    ///  ・Mother Face       → faceBlue の親
    ///  ・既存参照から親をたどるため、hideOnCapture の設定漏れでも Gauge 以外が残らない。
    ///  ・DangerVignette / このコンポーネント自身のオブジェクトは対象外（警告のみ）。
    /// </summary>
    private void ApplyMeterUiVisibility(bool hide)
    {
        int applied = 0;
        int missing = 0;

        if (!HideRootIfSafe(meterFrameRoot, hide, "Meter Frame")) missing++;
        else applied++;

        GameObject meterColorRoot = ResolveParentRoot(meterBlue);
        if (!HideRootIfSafe(meterColorRoot, hide, "Meter Color")) missing++;
        else applied++;

        GameObject meterGaugeRoot = ResolveParentRoot(FirstMeterSegment());
        if (!HideRootIfSafe(meterGaugeRoot, hide, "Meter Gauge")) missing++;
        else applied++;

        GameObject motherFaceRoot = ResolveParentRoot(faceBlue);
        if (!HideRootIfSafe(motherFaceRoot, hide, "Mother Face")) missing++;
        else applied++;

        if (hide && missing > 0 && !_meterUiWarningShown)
        {
            _meterUiWarningShown = true;
            Debug.LogWarning($"[{nameof(SuspicionVisualFeedback)}] 【設定不足】捕獲時に非表示にするメーターUIの参照が {missing} 件解決できませんでした" +
                             "（Meter Frame=Inspectorの meterFrameRoot / 色・ゲージ・顔=各画像参照）。" +
                             "解決できた分だけ非表示にします。未解決のUIは表示されたままになります", this);
        }

        if (showDebugLogs)
            Debug.Log($"[{nameof(SuspicionVisualFeedback)}] メーターUIの表示切り替え: hide={hide} / 適用={applied}件 / 未解決={missing}件", this);
    }

    /// <summary>meterSegments の先頭の有効な要素（Meter Gauge の親をたどるために使う）。</summary>
    private RawImage FirstMeterSegment()
    {
        if (meterSegments == null) return null;

        foreach (RawImage segment in meterSegments)
        {
            if (segment != null) return segment;
        }
        return null;
    }

    /// <summary>画像の親オブジェクト（Meter Color / Meter Gauge / Mother Face の各コンテナ）を返す。無ければ null。</summary>
    private static GameObject ResolveParentRoot(RawImage image)
    {
        if (image == null || image.transform.parent == null) return null;
        return image.transform.parent.gameObject;
    }

    /// <summary>
    /// ルートオブジェクトを安全に表示／非表示する。危険な対象（DangerVignette を含む／自身）は触らない。
    /// 戻り値：対象として扱えた（＝安全に適用できた）なら true。
    /// </summary>
    private bool HideRootIfSafe(GameObject target, bool hide, string label)
    {
        if (target == null) return false;

        if (target == gameObject)
        {
            Debug.LogWarning($"[{nameof(SuspicionVisualFeedback)}] {label} に自身のオブジェクト（{target.name}）が指定されています。" +
                             "このコンポーネントが停止して表示を戻せなくなるため、非表示にしません", this);
            return false;
        }

        if (ContainsVignette(target))
        {
            Debug.LogWarning($"[{nameof(SuspicionVisualFeedback)}] {label} に指定/解決された '{target.name}' は DangerVignette を含むため、" +
                             "非表示にしません（捕獲時も危険表示を残すため）", this);
            return false;
        }

        if (target.activeSelf != !hide)
        {
            target.SetActive(!hide);
        }

        return true;
    }

    /// <summary>
    /// 指定オブジェクト（自身を含む子）が DangerVignette の対象画像を持っているか。
    ///  hideOnCapture で危険表示まで消してしまわないための安全確認に使う。
    /// </summary>
    private bool ContainsVignette(GameObject target)
    {
        if (target == null)
        {
            return false;
        }

        Transform root = target.transform;
        RawImage[] frames = { frameBlue, framePurple, frameRed };

        foreach (RawImage frame in frames)
        {
            if (frame != null && (frame.transform == root || frame.transform.IsChildOf(root)))
            {
                return true;
            }
        }

        return false;
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
