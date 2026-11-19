using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 追加した難易度選択UI・子機／親機チュートリアルUIをUnityエディタのHierarchy上に生成し、
/// SceneビューやInspectorで配置・大きさを直接編集できるようにするエディタ拡張。
/// </summary>
public static class TutorialAndDifficultyUiEditor
{
    [MenuItem("Tools/Friday13th/難易度・チュートリアルUIをシーンに生成")]
    public static void GenerateUiInCurrentScene()
    {
        int createdCount = 0;

        // 1. タイトル画面用: DifficultyTitleUI
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        DifficultyTitleUI diffUi = Object.FindFirstObjectByType<DifficultyTitleUI>();
        if (diffUi == null && (sceneName == "ChildTitle" || sceneName == "ParentTitle"))
        {
            Canvas canvas = FindMainCanvas();
            if (canvas != null)
            {
                GameObject host = new GameObject("DifficultyTitleUI", typeof(RectTransform));
                Undo.RegisterCreatedObjectUndo(host, "Create DifficultyTitleUI");
                host.transform.SetParent(canvas.transform, false);
                RectTransform rt = host.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                diffUi = host.AddComponent<DifficultyTitleUI>();
            }
        }

        if (diffUi != null)
        {
            Undo.RegisterFullObjectHierarchyUndo(diffUi.gameObject, "Build DifficultyTitleUI");
            diffUi.BuildUIInSceneForEditing();
            EditorUtility.SetDirty(diffUi);
            Selection.activeGameObject = diffUi.gameObject;
            createdCount++;
        }

        // 2. 子機ゲーム画面用: TutorialFlow スライドUI
        TutorialFlow tutorialFlow = Object.FindFirstObjectByType<TutorialFlow>();
        if (tutorialFlow != null)
        {
            Image slideImg = tutorialFlow.EnsureTutorialSlideOverlay();
            if (slideImg != null)
            {
                Undo.RegisterCreatedObjectUndo(slideImg.gameObject, "Create ChildTutorialSlideOverlay");
                EditorUtility.SetDirty(tutorialFlow);
                Selection.activeGameObject = slideImg.gameObject;
                createdCount++;
            }
        }

        // 3. 親機ゲーム画面用: ParentGameTimeManager スライドUI
        ParentGameTimeManager parentTime = Object.FindFirstObjectByType<ParentGameTimeManager>();
        if (parentTime != null)
        {
            Image slideImg = parentTime.EnsureParentTutorialOverlay();
            if (slideImg != null)
            {
                Canvas parentCanvas = slideImg.GetComponentInParent<Canvas>();
                if (parentCanvas != null)
                    Undo.RegisterCreatedObjectUndo(parentCanvas.gameObject, "Create ParentTutorialOverlayCanvas");
                EditorUtility.SetDirty(parentTime);
                Selection.activeGameObject = slideImg.gameObject;
                createdCount++;
            }
        }

        if (createdCount == 0)
        {
            EditorUtility.DisplayDialog(
                "UI生成",
                "現在のシーンに DifficultyTitleUI / TutorialFlow / ParentGameTimeManager が見つかりませんでした。\n" +
                "タイトルシーンまたはゲームシーンを開いた状態で実行してください。",
                "OK");
        }
        else
        {
            Debug.Log($"[TutorialAndDifficultyUiEditor] シーンに編集用UIを生成/更新しました（対象={createdCount}件）。Hierarchyから位置・サイズを調整してシーンを保存してください。");
        }
    }

    private static Canvas FindMainCanvas()
    {
        Canvas[] canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
        Canvas best = null;
        int bestScore = int.MinValue;
        foreach (Canvas c in canvases)
        {
            if (c == null || !c.isActiveAndEnabled) continue;
            int score = 0;
            if (c.isRootCanvas) score += 10;
            if (c.GetComponent<GraphicRaycaster>() != null) score += 20;
            if (c.GetComponent<CanvasScaler>() != null) score += 10;
            if (c.GetComponentsInChildren<Button>(true).Length > 0) score += 30;
            if (c.gameObject.name == "Canvas") score += 15;
            if (c.gameObject.name == "Title") score -= 25;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best != null ? best : Object.FindFirstObjectByType<Canvas>();
    }
}
