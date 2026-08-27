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
        Debug.Log("親に捕まりました");
        Debug.Log("IsCaught = True（捕獲状態）");
        // シーン遷移はGameManager.TriggerResult(GameOver)が処理する。
        // スコアを保存し、型付きUDPメッセージを送信してからロードするため。
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

// インスペクター設定メモ：
// - 子機のUnityアプリ内のGameObjectにこのスクリプトをアタッチする。
// - このマネージャーは子機が親機に捕まったかを追跡する。
// - 捕まると、睡眠入力（Spaceキー）への反応を停止する。
// - ChildUdpReceiverは「CAUGHT」メッセージ受信時にSetCaughtState()を呼び出す。
