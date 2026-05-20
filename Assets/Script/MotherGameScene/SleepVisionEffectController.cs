using UnityEngine;

/// <summary>
/// SleepVisionEffectController:
/// Animates two UI eyelid RectTransforms based on SleepingController.IsSleeping.
/// When sleeping: slides upper eyelid down and lower eyelid up toward their closed positions.
/// When awake:    slides both eyelids back to their open positions.
/// Pure presentation — no gameplay logic.
/// </summary>
public class SleepVisionEffectController : MonoBehaviour
{
    // ── References ────────────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("Source of the IsSleeping state. Auto-found at Start if not assigned.")]
    public SleepingController sleepingController;

    [Tooltip("RectTransform for the upper eyelid image.")]
    public RectTransform upperEyelid;

    [Tooltip("RectTransform for the lower eyelid image.")]
    public RectTransform lowerEyelid;

    // ── Upper eyelid positions ────────────────────────────────────────────────
    [Header("Upper Eyelid Positions")]
    [Tooltip("anchoredPosition of the upper eyelid when the eye is fully open.")]
    public Vector2 upperOpenAnchoredPos = new Vector2(0f, 100f);

    [Tooltip("anchoredPosition of the upper eyelid when the eye is in the sleep/closed position.")]
    public Vector2 upperClosedAnchoredPos = new Vector2(0f, 0f);

    // ── Lower eyelid positions ────────────────────────────────────────────────
    [Header("Lower Eyelid Positions")]
    [Tooltip("anchoredPosition of the lower eyelid when the eye is fully open.")]
    public Vector2 lowerOpenAnchoredPos = new Vector2(0f, -100f);

    [Tooltip("anchoredPosition of the lower eyelid when the eye is in the sleep/closed position.")]
    public Vector2 lowerClosedAnchoredPos = new Vector2(0f, 0f);

    // ── Transition ────────────────────────────────────────────────────────────
    [Header("Transition")]
    [Tooltip("Units per second for MoveTowards interpolation. Higher = faster slide.")]
    public float transitionSpeed = 300f;

    // ──────────────────────────────────────────────────────────────────────────
    //  Unity lifecycle
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
