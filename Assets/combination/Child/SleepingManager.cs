using UnityEngine;
using UnityEngine.SceneManagement;

public class SleepingManager : MonoBehaviour
{
    private bool isCaught = false;
    private bool isSleeping = false;
    private bool hasLoadedGameOver = false;

    public string gameOverSceneName = "GameOver";

    public bool IsCaught => isCaught;
    public bool IsSleeping => isSleeping;

    public void SetCaughtState()
    {
        isCaught = true;
        Debug.Log("caught by parent");
        Debug.Log("IsCaught = True");
        if (!hasLoadedGameOver)
        {
            SceneManager.LoadScene(gameOverSceneName);
            hasLoadedGameOver = true;
        }
    }
}

// Inspector Setup Notes:
// - Attach this script to a GameObject in the child Unity app.
// - This manager tracks whether the child has been caught by the parent.
// - ChildUdpReceiver will call SetCaughtState() when receiving "CAUGHT" message.

