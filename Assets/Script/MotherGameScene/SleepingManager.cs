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
        // Scene transition is now handled by GameManager.TriggerResult(GameOver)
        // so that score is saved and typed UDP message is sent before loading.
    }

    void Update()
    {
        if (!isCaught)
        {
            isSleeping = Input.GetKey(KeyCode.Space);
        }
        else
        {
            isSleeping = false;
        }
    }
}

// Inspector Setup Notes:
// - Attach this script to a GameObject in the child Unity app.
// - This manager tracks whether the child has been caught by the parent.
// - When caught, it stops reacting to sleep input (Space key).
// - ChildUdpReceiver will call SetCaughtState() when receiving "CAUGHT" message.

