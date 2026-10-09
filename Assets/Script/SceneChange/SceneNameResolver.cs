using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Inspector に書かれたシーン名が Build Settings に無い場合（シーン名の変更、空白の有無の違いなど）でも、
/// 空白と大文字小文字を無視して同名のシーンを探して読み替える。
/// 例："Childe Title" と書かれていても、実在する "ChildeTitle" に解決される。
/// </summary>
public static class SceneNameResolver
{
    public static string Resolve(string requestedName)
    {
        if (string.IsNullOrEmpty(requestedName) || Application.CanStreamedLevelBeLoaded(requestedName))
        {
            return requestedName;
        }

        string target = Normalize(requestedName);
        for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
        {
            string sceneName = System.IO.Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i));
            if (Normalize(sceneName) == target)
            {
                Debug.LogWarning($"[SceneNameResolver] シーン名 '{requestedName}' は Build Settings に無いため '{sceneName}' に読み替えます。Inspector の値の修正を推奨します。");
                return sceneName;
            }
        }

        Debug.LogError($"[SceneNameResolver] シーン '{requestedName}' が Build Settings に見つかりません。シーン名を確認してください。");
        return requestedName;
    }

    private static string Normalize(string name)
    {
        return name.Replace(" ", string.Empty).ToLowerInvariant();
    }
}
