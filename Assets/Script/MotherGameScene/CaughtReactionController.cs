using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

/// <summary>
/// CaughtReactionController:
///
/// GAUGE OWNERSHIP — strict single-writer model:
///   ParentDetectionV2 is the ONLY script that writes to MotherGauge
///   (continuous rise/drop via SetGaugeDirect every frame, loud items via AddGauge).
///   This script MUST NOT write to MotherGauge at all.
///
/// This script is responsible for:
///   - Game-over watchdog: monitors gauge and fires scene/UDP transition when max is hit
///   - Forwarding NotifyGameOver to ParentDetectionV2 to halt its progression
///   - Disabling game logic components on game over
/// </summary>
public class CaughtReactionController : MonoBehaviour
{
    [Header("System References")]
    [SerializeField] private ParentDetectionV2 parentDetection;
    [SerializeField] private SleepingController sleepingController;
    [SerializeField] private DoorController doorController;
    [SerializeField] private ParentUdpSender udpSender;
    [SerializeField] private MotherGauge motherGauge;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;

    // Game over state flag
    private bool hasTriggeredGameOver = false;
    private Coroutine gameOverRoutine = null;

    void Start()
    {
        if (parentDetection == null)
            parentDetection = Object.FindFirstObjectByType<ParentDetectionV2>();
        if (sleepingController == null)
            sleepingController = Object.FindFirstObjectByType<SleepingController>();
        if (doorController == null)
            doorController = Object.FindFirstObjectByType<DoorController>();
        if (udpSender == null)
            udpSender = Object.FindFirstObjectByType<ParentUdpSender>();
        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        hasTriggeredGameOver = false;

        if (fadeCanvasGroup != null)
        {
            fadeCanvasGroup.alpha = 0f;
            fadeCanvasGroup.interactable = false;
            fadeCanvasGroup.blocksRaycasts = false;
        }

        if (showDebugLogs)
            Debug.Log("[CaughtReactionController] initialized - game-over watchdog only (gauge owned by PDV2)");
    }

    void Update()
    {
        if (hasTriggeredGameOver) return;
        if (motherGauge == null) return;

        // Game-over watchdog: PDV2 writes gauge every frame and calls OnPlayerCaught
        // when max is hit. This is a secondary safety net in case PDV2 is disabled
        // before it can fire its own game-over path.
        if (motherGauge.currentGauge >= motherGauge.maxGauge)
        {
            TriggerGameOver();
            return;
        }

        if (showDebugLogs)
        {
            bool isLooking  = (parentDetection != null) && parentDetection.isMotherLookingNow;
            bool isSleeping = (sleepingController != null) && sleepingController.IsSleeping;
            Debug.Log($"[CaughtReactionController-Update] isMotherLookingNow={isLooking} | IsSleeping={isSleeping} | gauge={motherGauge.currentGauge}/{motherGauge.maxGauge} | (gauge written exclusively by PDV2)");
        }
    }

    /// <summary>
    /// Notification that the mother performed a check.
    /// This script no longer modifies the gauge (PDV2 owns all writes).
    /// Kept as a stub so existing UnityEvent wiring does not break.
    /// </summary>
    public void OnMotherCheck(bool isFullCheck)
    {
        if (showDebugLogs)
            Debug.Log($"[CaughtReactionController] OnMotherCheck ({(isFullCheck ? "FULL" : "PEEK")}) received - gauge write is PDV2's responsibility, no action taken here");
    }

    /// <summary>
    /// Notification that a loud item was triggered.
    /// This script no longer modifies the gauge (PDV2.OnLoudItemTriggered owns the write).
    /// Kept as a stub so existing wiring does not break.
    /// </summary>
    public void OnLoudItemTriggered()
    {
        if (showDebugLogs)
            Debug.Log("[CaughtReactionController] OnLoudItemTriggered received - gauge write is PDV2's responsibility, no action taken here");
    }

    /// <summary>
    /// Triggers the permanent game over sequence
    /// </summary>
    private void TriggerGameOver()
    {
        if (hasTriggeredGameOver) return;

        hasTriggeredGameOver = true;

        // Notify parent detection of permanent game over so it stops progression
        if (parentDetection != null)
        {
            try { parentDetection.NotifyGameOver(); } catch { }
        }

        if (showDebugLogs)
            Debug.LogWarning("[CaughtReactionController] GAME OVER TRIGGERED - suspicion reached max");

        // Disable game logic components
        DisableGameLogic();

        // Start the game over routine (send CAUGHT and load scene)
        if (gameOverRoutine != null) StopCoroutine(gameOverRoutine);
        gameOverRoutine = StartCoroutine(GameOverRoutine());
    }

    private void DisableGameLogic()
    {
        if (doorController != null) { doorController.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Door Controller disabled"); }
        if (sleepingController != null) { sleepingController.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Sleeping Controller disabled"); }
        if (parentDetection != null) { parentDetection.enabled = false; if (showDebugLogs) Debug.Log("[CaughtReactionController] Parent Detection disabled"); }
    }

    private IEnumerator GameOverRoutine()
    {
        if (showDebugLogs) Debug.Log("[CaughtReactionController] Sending CAUGHT signal via UDP...");
        if (udpSender != null) udpSender.SendState("CAUGHT");
        yield return new WaitForSeconds(0.1f);
        if (showDebugLogs) Debug.Log("[CaughtReactionController] Loading GameOverResult scene...");
        SceneManager.LoadScene("GameOverResult");
        gameOverRoutine = null;
    }

    public void ForceGameOver()
    {
        if (!hasTriggeredGameOver) TriggerGameOver();
    }

    /// <summary>
    /// Debug-only helper to reset the gauge to zero from the Inspector or test code.
    /// In normal gameplay the gauge is reset by ParentDetectionV2.ResetCycle().
    /// </summary>
    public void DebugResetSuspicionGauge()
    {
        if (motherGauge == null) motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        if (motherGauge != null) motherGauge.SetGaugeDirect(0);
        if (showDebugLogs) Debug.Log("[CaughtReactionController] DebugResetSuspicionGauge: gauge set to 0 (debug only)");
    }
}
