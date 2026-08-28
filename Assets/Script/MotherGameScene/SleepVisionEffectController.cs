using UnityEngine;

/// <summary>
/// SleepVisionEffectController：
/// SleepingController.IsSleepingに応じて、2つのUIまぶたRectTransformを動かす。
/// 睡眠中：上まぶたを下へ、下まぶたを上へ動かして閉じた位置に近づける。
/// 起きているとき：両方のまぶたを開いた位置へ戻す。
/// 表示専用で、ゲームプレイのロジックは持たない。
/// </summary>
public class SleepVisionEffectController : MonoBehaviour
{
    // ── 参照 ────────────────────────────────────────────────────────────────
    [Header("参照")]
    [Tooltip("IsSleeping状態の参照元。未設定の場合はStart時に自動検索します。")]
    public SleepingController sleepingController;

    [Tooltip("上まぶた画像のRectTransform。")]
    public RectTransform upperEyelid;

    [Tooltip("下まぶた画像のRectTransform。")]
    public RectTransform lowerEyelid;

    // ── 上まぶたの位置 ────────────────────────────────────────────────────────
    [Header("上まぶたの位置")]
    [Tooltip("目が完全に開いているときの上まぶたのanchoredPosition。")]
    public Vector2 upperOpenAnchoredPos = new Vector2(0f, 100f);

    [Tooltip("睡眠／閉じた状態のときの上まぶたのanchoredPosition。")]
    public Vector2 upperClosedAnchoredPos = new Vector2(0f, 0f);

    // ── 下まぶたの位置 ────────────────────────────────────────────────────────
    [Header("下まぶたの位置")]
    [Tooltip("目が完全に開いているときの下まぶたのanchoredPosition。")]
    public Vector2 lowerOpenAnchoredPos = new Vector2(0f, -100f);

    [Tooltip("睡眠／閉じた状態のときの下まぶたのanchoredPosition。")]
    public Vector2 lowerClosedAnchoredPos = new Vector2(0f, 0f);

    // ── 遷移 ────────────────────────────────────────────────────────────────
    [Header("遷移")]
    [Tooltip("MoveTowards補間の1秒あたりの移動量。大きいほど速くスライドします。")]
    public float transitionSpeed = 300f;

    // ──────────────────────────────────────────────────────────────────────────
    //  Unityのライフサイクル
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (sleepingController == null)
            sleepingController = Object.FindFirstObjectByType<SleepingController>();

        if (upperEyelid != null)
            upperEyelid.anchoredPosition = upperOpenAnchoredPos;

        if (lowerEyelid != null)
            lowerEyelid.anchoredPosition = lowerOpenAnchoredPos;
    }

    private void Update()
    {
        bool sleeping = sleepingController != null && sleepingController.IsSleeping;

        Vector2 upperTarget = sleeping ? upperClosedAnchoredPos : upperOpenAnchoredPos;
        Vector2 lowerTarget = sleeping ? lowerClosedAnchoredPos : lowerOpenAnchoredPos;

        float step = transitionSpeed * Time.deltaTime;

        if (upperEyelid != null)
            upperEyelid.anchoredPosition = Vector2.MoveTowards(upperEyelid.anchoredPosition, upperTarget, step);

        if (lowerEyelid != null)
            lowerEyelid.anchoredPosition = Vector2.MoveTowards(lowerEyelid.anchoredPosition, lowerTarget, step);
    }
}
