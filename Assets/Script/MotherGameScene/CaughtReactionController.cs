using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// CaughtReactionController: Main game over loop referee.
/// Monitors mother's look state and player's sleeping status.
/// Manages suspicion gauge and triggers game over when caught.
/// </summary>
public class CaughtReactionController : MonoBehaviour
{
    [Header("System References")]
    [SerializeField] private ParentDetectionV2 parentDetection;
    [SerializeField] private SleepingController sleepingController;
    [SerializeField] private DoorController doorController;
    [SerializeField] private ParentUdpSender udpSender;

    [Header("Suspicion Gauge Settings")]
    [SerializeField, Range(0f, 100f)] private float suspicionGauge = 0f;
    [SerializeField] private float gaugeRiseSpeed = 50f;    // Increase when caught looking
    [SerializeField] private float gaugeDropSpeed = 15f;    // Decrease when safe

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;

    // Game over state flag
    private bool hasTriggeredGameOver = false;
    private Coroutine gameOverRoutine = null;

    /// <summary>
    /// Read-only property: Current suspicion level (0-100)
    /// </summary>
    public float SuspicionGauge => suspicionGauge;

    /// <summary>
    /// Read-only property: Whether game over has been triggered
    /// </summary>
    public bool IsGameOver => hasTriggeredGameOver;

    void Start()
    {
        // Auto-find references if not assigned
        if (parentDetection == null)
        {
            parentDetection = Object.FindFirstObjectByType<ParentDetectionV2>();
        }

        if (sleepingController == null)
        {
            sleepingController = Object.FindFirstObjectByType<SleepingController>();
        }

        if (doorController == null)
        {
            doorController = Object.FindFirstObjectByType<DoorController>();
        }

        if (udpSender == null)
        {
            udpSender = Object.FindFirstObjectByType<ParentUdpSender>();
        }

        suspicionGauge = 0f;
        hasTriggeredGameOver = false;

        if (fadeCanvasGroup != null)
        {
            fadeCanvasGroup.alpha = 0f;
            fadeCanvasGroup.interactable = false;
            fadeCanvasGroup.blocksRaycasts = false;
        }

        if (showDebugLogs)
            Debug.Log("?? CaughtReactionController initialized");
    }

    void Update()
    {
        // If game over already triggered, skip gauge logic
        if (hasTriggeredGameOver) return;

        // If references missing, skip
        if (parentDetection == null || sleepingController == null) return;

        // Gauge progression logic
        bool motherIsLooking = parentDetection.isMotherLookingNow;
        bool playerIsSleeping = sleepingController.IsSleeping;

        if (motherIsLooking && !playerIsSleeping)
        {
            // Mother is staring and player is NOT sleeping: gauge rises rapidly
            suspicionGauge += gaugeRiseSpeed * Time.deltaTime;

            if (showDebugLogs)
                Debug.Log($"?? Suspicion Rising: {suspicionGauge:F1}");
        }
        else
        {
            // Safe: mother not looking or player is sleeping: gauge decreases
            suspicionGauge -= gaugeDropSpeed * Time.deltaTime;

            if (showDebugLogs)
                Debug.Log($"?? Suspicion Dropping: {suspicionGauge:F1}");
        }

        // Clamp gauge between 0 and 100
        suspicionGauge = Mathf.Clamp(suspicionGauge, 0f, 100f);

        // Check for game over condition
        if (suspicionGauge >= 100f)
        {
            TriggerGameOver();
        }
    }

    /// <summary>
    /// Triggers the game over sequence
    /// </summary>
    private void TriggerGameOver()
    {
        if (hasTriggeredGameOver) return;

        hasTriggeredGameOver = true;

        if (showDebugLogs)
            Debug.LogError("?? GAME OVER TRIGGERED! Suspicion reached 100!");

        // Disable game logic components
        DisableGameLogic();

        // Start the game over routine (send CAUGHT and load scene)
        if (gameOverRoutine != null)
            StopCoroutine(gameOverRoutine);
        gameOverRoutine = StartCoroutine(GameOverRoutine());
    }

    /// <summary>
    /// Disables door and sleeping controllers to prevent further input
    /// </summary>
    private void DisableGameLogic()
    {
        // Disable door controller
        if (doorController != null)
        {
            doorController.enabled = false;
            if (showDebugLogs)
                Debug.Log("?? Door Controller disabled");
        }

        // Disable sleeping controller
        if (sleepingController != null)
        {
            sleepingController.enabled = false;
            if (showDebugLogs)
                Debug.Log("?? Sleeping Controller disabled");
        }

        // Disable parent detection
        if (parentDetection != null)
        {
            parentDetection.enabled = false;
            if (showDebugLogs)
                Debug.Log("??? Parent Detection disabled");
        }
    }

    /// <summary>
    /// Coroutine: Sends CAUGHT signal and loads game over scene
    /// </summary>
    private IEnumerator GameOverRoutine()
    {
        if (showDebugLogs)
            Debug.Log("?? Sending CAUGHT signal via UDP...");

        // Send "CAUGHT" to parent device
        if (udpSender != null)
        {
            udpSender.SendState("CAUGHT");
        }

        // Wait for realtime delay (0.1 seconds)
        yield return new WaitForSeconds(0.1f);

        if (showDebugLogs)
            Debug.Log("?? Loading MotherGameOver scene...");

        // Load game over scene
        SceneManager.LoadScene("MotherGameOver");

        gameOverRoutine = null;
    }

    /// <summary>
    /// Manual method to trigger game over (if needed from external source)
    /// </summary>
    public void ForceGameOver()
    {
        if (!hasTriggeredGameOver)
        {
            if (showDebugLogs)
                Debug.Log("?? Force game over called!");

            TriggerGameOver();
        }
    }

    /// <summary>
    /// Resets the suspicion gauge (for testing/debugging)
    /// </summary>
    public void ResetSuspicionGauge()
    {
        suspicionGauge = 0f;

        if (showDebugLogs)
            Debug.Log("?? Suspicion gauge reset to 0");
    }

    /// <summary>
    /// Manually set suspicion gauge to a specific value
    /// </summary>
    public void SetSuspicionGauge(float value)
    {
        suspicionGauge = Mathf.Clamp(value, 0f, 100f);

        if (showDebugLogs)
            Debug.Log($"??? Suspicion gauge set to: {suspicionGauge}");
    }
}
