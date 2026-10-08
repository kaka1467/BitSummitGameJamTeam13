using UnityEngine;

public class SleepingManager : MonoBehaviour
{
    private bool _isCaught;
    private bool _isSleeping;

    public string gameOverSceneName = "GameOver";

    public bool IsCaught => _isCaught;
    public bool IsSleeping => _isSleeping;

    public void SetCaughtState()
    {
        _isCaught = true;
        Debug.Log("親に捕まりました");
        Debug.Log("IsCaught = True（捕獲状態）");
        // シーン遷移はGameManager.TriggerResult(GameOver)が処理する。
        // スコアを保存し、型付きUDPメッセージを送信してからロードするため。
    }

    private void Update()
    {
        _isSleeping = !_isCaught && Input.GetKey(KeyCode.Space);
    }
}

// インスペクター設定メモ：
// - 子機のUnityアプリ内のGameObjectにこのスクリプトをアタッチする。
// - このマネージャーは子機が親機に捕まったかを追跡する。
// - 捕まると、睡眠入力（Spaceキー）への反応を停止する。
// - ChildUdpReceiverは「CAUGHT」メッセージ受信時にSetCaughtState()を呼び出す。
