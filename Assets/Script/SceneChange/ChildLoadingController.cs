using System.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ChildLoadingController:
/// Attach to a GameObject in the child's loading scene.
/// Waits for a minimum display time, optionally sends LOADING_COMPLETE to the parent,
/// then immediately loads the child game scene.
/// </summary>
public class ChildLoadingController : MonoBehaviour
{
    // ── 計測用マーカー（Profilerで処理時間と呼び出し回数を見る。動作は変更しない）──
    private static readonly ProfilerMarker s_loadChildGameSceneMarker =
        new ProfilerMarker("Loading.LoadChildGameScene");

    [Header("Scene Settings")]
    [Tooltip("Name of the child game scene to load after loading is done.")]
    public string gameSceneName = "ChildGameScene";

    [Header("Timing")]
    [Tooltip("Minimum seconds to show the loading screen before transitioning.")]
    public float minimumDisplaySeconds = 1.0f;

    [Header("Parent Sync")]
    [Tooltip("If true, send LOADING_COMPLETE to the parent when the loading screen ends.")]
    [SerializeField] private bool sendLoadingCompleteOnFinish = false;

    [Header("Gauge Sync")]
    [Tooltip("If true, wait for GaugeSceneChanger to complete before loading the next scene.")]
    [SerializeField] private bool waitForGaugeComplete = true;
    [SerializeField] private GaugeSceneChanger gaugeSceneChanger;

    private void Start()
    {
        if (waitForGaugeComplete && gaugeSceneChanger == null)
        {
            gaugeSceneChanger = UnityEngine.Object.FindFirstObjectByType<GaugeSceneChanger>();
        }
        StartCoroutine(LoadingRoutine());
    }

    private IEnumerator LoadingRoutine()
    {
        float elapsed = 0f;

        // Wait for minimum display time
        Debug.Log($"[StartTrace][子機] ChildLoadingController.LoadingRoutine 開始 — 最小表示待ち開始 (minimumDisplaySeconds={minimumDisplaySeconds}, scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', id={GetInstanceID()})", this);
        while (elapsed < minimumDisplaySeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
        Debug.Log($"[StartTrace][子機] ChildLoadingController.LoadingRoutine 最小表示待ち解除 (elapsed={elapsed:F2}s)", this);

        if (waitForGaugeComplete && gaugeSceneChanger != null)
        {
            Debug.Log($"[StartTrace][子機] ChildLoadingController.LoadingRoutine ゲージ完了待ち開始 (gauge='{gaugeSceneChanger.name}')", this);
            gaugeSceneChanger.SetChangeSceneOnComplete(false);
            while (!gaugeSceneChanger.IsComplete)
            {
                yield return null;
            }
            Debug.Log($"[StartTrace][子機] ChildLoadingController.LoadingRoutine ゲージ完了待ち解除 (IsComplete={gaugeSceneChanger.IsComplete})", this);
        }
        else
        {
            string gaugeName = gaugeSceneChanger != null ? gaugeSceneChanger.name : "null";
            Debug.Log($"[StartTrace][子機] ChildLoadingController.LoadingRoutine ゲージ完了待ちなし (waitForGaugeComplete={waitForGaugeComplete}, gaugeSceneChanger={gaugeName})", this);
        }

        // ─── 修正箇所 ───
        // ここでの送信は従来どおり無効のまま（sendLoadingCompleteOnFinish を true にしても送らない）。
        // 旧型（ChildUdpReceiver）の取得は有効利用が無かったため整理した（送信の有効化は行わない）。
        if (sendLoadingCompleteOnFinish)
        {
            // Notify parent that child loading is complete（送信は無効のまま維持）
            Debug.Log("[ChildLoadingController] Loading done — LOADING_COMPLETE send is disabled (unchanged).");
            // _host.SendState("LOADING_COMPLETE"); // ← コメントアウト（移行のついでに有効化しない）
        }
        // ─────────────────

        // Small delay to allow UDP packet to be sent before scene change
        yield return new WaitForSecondsRealtime(0.1f);

        Debug.Log($"[ChildLoadingController] Loading {gameSceneName}.");
        Debug.Log($"[StartTrace][子機] ChildLoadingController → SceneManager.LoadScene('{gameSceneName}') 直前 (現在scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}')", this);
        using (s_loadChildGameSceneMarker.Auto())
        {
            SceneManager.LoadScene(gameSceneName);
        }
    }
}
