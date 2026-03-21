using UnityEngine;
using UnityEngine.SceneManagement;

public class TitleManager : MonoBehaviour
{
    public string SceneName = "GameOver";
    public void OnStartButtonClick()
    {
        // "GameScene" という名前のシーンを読み込む
        SceneManager.LoadScene(SceneName);
    }
}