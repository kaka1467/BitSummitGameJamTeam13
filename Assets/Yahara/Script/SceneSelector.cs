using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using System.Collections.Generic;

public class SceneSelector : MonoBehaviour
{
    private TMP_Dropdown dropdown;

    void Start()
    {
        dropdown = GetComponent<TMP_Dropdown>();
        
        // 1. ドロップダウンを初期化
        dropdown.ClearOptions();

        // 2. Build Settingsにある全シーン名を取得
        List<string> sceneNames = new List<string>();
        int sceneCount = SceneManager.sceneCountInBuildSettings;

        for (int i = 0; i < sceneCount; i++)
        {
            // パスからファイル名だけを抽出してリストに追加
            string path = SceneUtility.GetScenePathByBuildIndex(i);
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(path);
            sceneNames.Add(sceneName);
        }

        // 3. ドロップダウンにリストを設定
        dropdown.AddOptions(sceneNames);

        // 4. 値が変更された時のイベントを登録
        dropdown.onValueChanged.AddListener(delegate {
            OnDropdownValueChanged(dropdown);
        });
    }

    void OnDropdownValueChanged(TMP_Dropdown change)
    {
        // 選択されたシーン名でロード
        string selectedScene = change.options[change.value].text;
        SceneManager.LoadScene(selectedScene);
    }
}