using System.Collections;
using UnityEngine;

[RequireComponent(typeof(CanvasGroup))]
public class ScreenFadeIn : MonoBehaviour
{
    [SerializeField] private CanvasGroup canvasGroup;
    [SerializeField] private float duration = 1.5f;
    [SerializeField] private bool playOnStart = true;
    [SerializeField] private bool useUnscaledTime = true;

    private Coroutine running;

    private void Awake()
    {
        if (canvasGroup == null)
        {
            canvasGroup = GetComponent<CanvasGroup>();
        }
    }

    private void Start()
    {
        if (playOnStart)
        {
            Play();
        }
    }

    public void Play()
    {
        if (canvasGroup == null)
        {
            Debug.LogWarning("ScreenFadeIn: CanvasGroup is missing.");
            return;
        }

        if (running != null)
        {
            StopCoroutine(running);
        }

        running = StartCoroutine(FadeIn());
    }

    private IEnumerator FadeIn()
    {
        canvasGroup.alpha = 1f;
        canvasGroup.blocksRaycasts = true;
        canvasGroup.interactable = false;

        float t = 0f;
        while (t < duration)
        {
            t += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            float a = 1f - Mathf.Clamp01(t / duration);
            canvasGroup.alpha = a;
            yield return null;
        }

        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;
        running = null;
    }
}
