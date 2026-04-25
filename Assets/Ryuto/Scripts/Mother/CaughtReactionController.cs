using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
/// <summary>
/// Reacts when the player is caught by the parent, without using UI elements.
/// Disables game logic once caught and provides a state flag for other scripts.
/// </summary>
public class CaughtReactionController : MonoBehaviour
{
    public ParentDetection parentDetection;
    public DoorController doorController;
    public SleepingController sleepingController;
    public ParentUdpSender udpSender;

    private bool hasHandledCaught = false;
    private bool caughtMode = false;

    public bool CaughtMode => caughtMode;

    private IEnumerator GameOverRoutine()
    {
        if (udpSender != null)
        {
            udpSender.SendState("CAUGHT");
        }
        yield return new WaitForSeconds(0.1f);
        SceneManager.LoadScene("MotherGameOver");
    }

    void Update()
    {
        if (parentDetection == null || hasHandledCaught)
            return;

        if (parentDetection.isCaught)
        {
            hasHandledCaught = true;
            caughtMode = true;
            Debug.Log("Player caught!");

            // Disable door logic
            if (doorController != null)
                doorController.enabled = false;

            // Disable sleeping input
            if (sleepingController != null)
                sleepingController.enabled = false;
            
            StartCoroutine(GameOverRoutine());
        }
    }
}

/*
Setup:
- Attach this script to an empty GameObject like "GameManager".
- In the Inspector, assign ParentDetection, DoorController, and SleepingController references.
- Check CaughtMode property to know if the player is caught (useful for future UI or retry logic).
*/