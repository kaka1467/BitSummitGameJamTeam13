using UnityEngine;
using UnityEngine.SceneManagement;

public class TitleManager : MonoBehaviour
{
    [Tooltip("スタートボタンで読み込むシーン名。親機／子機で Inspector から指定してください。")]
    public string SceneName = "ParentGameScene";

    public void OnStartButtonClick()
    {
        // Inspector で指定されたシーンを読み込む（親機／子機で設定を分ける）。
        SceneManager.LoadScene(SceneNameResolver.Resolve(SceneName));
    }
}