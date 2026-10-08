using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ParentLoadingController : MonoBehaviour
{
    [Header("Scene Settings")]
    [Tooltip("Name of the parent game scene to load after loading is done.")]
    public string gameSceneName = "MotherGameScene";

    [Header("Timing")]
    [Tooltip("Minimum seconds to show the loading screen before transitioning, even if child is already ready.")]
    public float minimumDisplaySeconds = 1.0f;

    private ParentUdpSender _udpSender;
    private bool _sceneLoaded = false;

    private void Start()
    {
        _udpSender = UnityEngine.Object.FindFirstObjectByType<ParentUdpSender>();

        if (_udpSender == null)
        {
            Debug.LogWarning("[ParentLoadingController] ParentUdpSender not found — will use timeout only.");
        }
        else
        {
            _udpSender.ChildLoadingComplete = false;
            Debug.Log("[ParentLoadingController] Reset ChildLoadingComplete to false for new loading session.");
        }

        StartCoroutine(LoadingRoutine());
    }

    private IEnumerator LoadingRoutine()
    {
        float elapsed = 0f;

        // Phase 1: 最小表示時間の待機
        while (elapsed < minimumDisplaySeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        // ─── 修正箇所 ───
        if (_udpSender != null)
        {
            Debug.Log("[ParentLoadingController] Sending START_LOADING to child.");
            
            // 内部でNullReferenceExceptionが出ないよう、念のためSender側が
            // クライアントの初期化を終えているタイミング（Startが走った後など）を見計らって送信します
            // もしこれでも即座にNullエラーが出る場合は、1フレーム待ってから送るようにします
            yield return null; 

            _udpSender.SendState("START_LOADING");
        }
        // ────────────────

        Debug.Log("[ParentLoadingController] Minimum display time reached — waiting for child LOADING_COMPLETE.");

        // Phase 2: 子機からの LOADING_COMPLETE を待つ。
        // LOADING_COMPLETE は「子機のチュートリアル（カウントダウンまで）が完了した」ことを意味するため、
        // これを待たずに親機だけゲームを開始すると子機と進行がずれる。よって自動タイムアウトでの
        // 「親機だけ開始」は行わない（ゲーム開始条件は変更しない）。従来どおり無期限に待つが、
        // 原因切り分けのため一定間隔で警告ログを出す（固まっているのか遅いだけかを判別できるようにする）。
        const float stallWarningIntervalSeconds = 10f;
        float waitElapsed = 0f;
        float nextWarningAt = stallWarningIntervalSeconds;
        while (true)
        {
            bool childReady = (_udpSender != null) && _udpSender.ChildLoadingComplete;
            if (childReady)
            {
                Debug.Log("[ParentLoadingController] Child LOADING_COMPLETE received — transitioning.");
                break;
            }

            waitElapsed += Time.unscaledDeltaTime;
            if (waitElapsed >= nextWarningAt)
            {
                Debug.LogWarning($"[ParentLoadingController] Still waiting for child LOADING_COMPLETE ({waitElapsed:F1}s). " +
                                 "子機がチュートリアル中の可能性があります。親機だけでは開始しません（進行同期のため）。");
                nextWarningAt += stallWarningIntervalSeconds;
            }

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