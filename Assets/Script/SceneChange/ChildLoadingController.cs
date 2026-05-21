using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ChildLoadingController:
/// Attach to a GameObject in the child's loading scene.
/// Waits for a minimum display time, sends LOADING_COMPLETE to the parent,
/// then immediately loads the child game scene.
/// </summary>
public class ChildLoadingController : MonoBehaviour
{
    [Header("Scene Settings")]
    [Tooltip("Name of the child game scene to load after loading is done.")]
    public string gameSceneName = "GameScene";

    [Header("Timing")]
    [Tooltip("Minimum seconds to show the loading screen before transitioning.")]
    public float minimumDisplaySeconds = 1.0f;

    private ChildUdpReceiver _udpReceiver;

    private void Start()
    {
        _udpReceiver = UnityEngine.Object.FindFirstObjectByType<ChildUdpReceiver>();
        StartCoroutine(LoadingRoutine());
    }

    private IEnumerator LoadingRoutine()
    {
        float elapsed = 0f;

        // Wait for minimum display time
        while (elapsed < minimumDisplaySeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        // Notify parent that child loading is complete
        if (_udpReceiver != null)
        {
            Debug.Log("[ChildLoadingController] Loading done — sending LOADING_COMPLETE to parent.");
            _udpReceiver.SendState("LOADING_COMPLETE");
        }
        else
        {
            Debug.LogWarning("[ChildLoadingController] ChildUdpReceiver not found — LOADING_COMPLETE not sent.");
        }

        // Small delay to allow UDP packet to be sent before scene change
        yield return new WaitForSecondsRealtime(0.1f);

        Debug.Log($"[ChildLoadingController] Loading {gameSceneName}.");
        SceneManager.LoadScene(gameSceneName);
    }
}
