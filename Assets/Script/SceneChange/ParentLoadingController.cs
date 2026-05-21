using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ParentLoadingController:
/// Attach to a GameObject in the parent's loading scene.
/// Waits for:
///   1. A minimum display time (to avoid instant flash).
///   2. LOADING_COMPLETE received from the child (via ParentUdpSender.ChildLoadingComplete).
/// Falls back to transitioning after a timeout if the child never responds.
/// </summary>
public class ParentLoadingController : MonoBehaviour
{
    [Header("Scene Settings")]
    [Tooltip("Name of the parent game scene to load after loading is done.")]
    public string gameSceneName = "MotherGameScene";

    [Header("Timing")]
    [Tooltip("Minimum seconds to show the loading screen before transitioning, even if child is already ready.")]
    public float minimumDisplaySeconds = 1.0f;

    [Tooltip("Maximum seconds to wait for child LOADING_COMPLETE before giving up and transitioning anyway.")]
    public float timeoutSeconds = 10.0f;

    private ParentUdpSender _udpSender;
    private bool _sceneLoaded = false;

    private void Start()
    {
        _udpSender = UnityEngine.Object.FindFirstObjectByType<ParentUdpSender>();

        if (_udpSender == null)
            Debug.LogWarning("[ParentLoadingController] ParentUdpSender not found — will use timeout only.");

        StartCoroutine(LoadingRoutine());
    }

    private IEnumerator LoadingRoutine()
    {
        float elapsed = 0f;

        // Phase 1: always wait for minimum display time
        while (elapsed < minimumDisplaySeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        Debug.Log("[ParentLoadingController] Minimum display time reached — waiting for child LOADING_COMPLETE.");

        // Phase 2: wait for child LOADING_COMPLETE, with timeout fallback
        float waitElapsed = 0f;
        while (true)
        {
            bool childReady = (_udpSender != null) && _udpSender.ChildLoadingComplete;
            if (childReady)
            {
                Debug.Log("[ParentLoadingController] Child LOADING_COMPLETE received — transitioning.");
                break;
            }

            if (waitElapsed >= timeoutSeconds)
            {
                Debug.LogWarning($"[ParentLoadingController] Timeout after {timeoutSeconds}s — transitioning without child confirmation.");
                break;
            }

            waitElapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        Transition();
    }

    private void Transition()
    {
        if (_sceneLoaded) return;
        _sceneLoaded = true;
        Debug.Log($"[ParentLoadingController] Loading {gameSceneName}.");
        SceneManager.LoadScene(gameSceneName);
    }
}
