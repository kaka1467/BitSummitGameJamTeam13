using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 追加した難易度選択UI・子機／親機チュートリアルUIをUnityエディタのHierarchy上に生成し、
/// SceneビューやInspectorで配置・大きさを直接編集できるようにするエディタ拡張。
/// </summary>
public static class TutorialAndDifficultyUiEditor
{
    [MenuItem("Tools/Friday13th/難易度・チュートリアルUIをシーンに生成（プレビュー表示）")]
    public static void GenerateUiInCurrentScene()
    {
        int createdCount = 0;
        Scene activeScene = SceneManager.GetActiveScene();
        string sceneName = activeScene.name;

        // 1. タイトル画面用: DifficultyTitleUI
        DifficultyTitleUI diffUi = Object.FindFirstObjectByType<DifficultyTitleUI>(FindObjectsInactive.Include);
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
                diffUi = Undo.AddComponent<DifficultyTitleUI>(host);
            }
        }

        if (diffUi != null)
        {
            Undo.RecordObject(diffUi, "Build DifficultyTitleUI");
            Undo.RegisterFullObjectHierarchyUndo(diffUi.gameObject, "Build DifficultyTitleUI Hierarchy");
            diffUi.BuildUIInSceneForEditing();

            SerializedObject so = new SerializedObject(diffUi);
            so.Update();
            so.ApplyModifiedProperties();

            Transform modalTr = diffUi.transform.Find("DifficultyModalPanel");
            if (modalTr != null)
            {
                modalTr.gameObject.SetActive(true);
                EditorUtility.SetDirty(modalTr.gameObject);
                Selection.activeGameObject = modalTr.gameObject;
            }
            else
            {
                Selection.activeGameObject = diffUi.gameObject;
            }
            EditorUtility.SetDirty(diffUi);
            EditorUtility.SetDirty(diffUi.gameObject);
            createdCount++;
        }

        // 2. 子機ゲーム画面用: TutorialFlow スライドUI
        TutorialFlow tutorialFlow = Object.FindFirstObjectByType<TutorialFlow>(FindObjectsInactive.Include);
        if (tutorialFlow != null)
        {
            Undo.RecordObject(tutorialFlow, "Build TutorialFlow Slide UI");
            Image slideImg = tutorialFlow.EnsureTutorialSlideOverlay();
            if (slideImg != null)
            {
                Canvas childOverlayCanvas = slideImg.GetComponentInParent<Canvas>();
                if (childOverlayCanvas != null)
                    Undo.RegisterCreatedObjectUndo(childOverlayCanvas.gameObject, "Create ChildTutorialOverlayCanvas");
                if (slideImg.sprite == null)
                    slideImg.sprite = Resources.Load<Sprite>("Tutorial/Child/tutorial_gooditems");
                slideImg.color = Color.white;
                slideImg.gameObject.SetActive(true);

                Transform parentTr = slideImg.transform.parent;
                if (parentTr != null)
                {
                    Transform dimTr = parentTr.Find("ChildTutorialDimBackground");
                    if (dimTr != null)
                    {
                        dimTr.SetSiblingIndex(0);
                        dimTr.gameObject.SetActive(true);
                    }
                    slideImg.transform.SetSiblingIndex(1);
                    Transform okTr = parentTr.Find("ChildTutorialOkButton");
                    if (okTr != null)
                    {
                        okTr.SetAsLastSibling();
                        okTr.gameObject.SetActive(true);
                    }
                }

                SerializedObject so = new SerializedObject(tutorialFlow);
                so.Update();
                so.ApplyModifiedProperties();

                EditorUtility.SetDirty(tutorialFlow);
                EditorUtility.SetDirty(slideImg.gameObject);
                Selection.activeGameObject = slideImg.gameObject;
                createdCount++;
            }
        }

        // 3. 親機ゲーム画面用: ParentGameTimeManager スライドUI
        ParentGameTimeManager parentTime = Object.FindFirstObjectByType<ParentGameTimeManager>(FindObjectsInactive.Include);
        if (parentTime != null)
        {
            Undo.RecordObject(parentTime, "Build ParentGameTimeManager Slide UI");
            Image slideImg = parentTime.EnsureParentTutorialOverlay();
            if (slideImg != null)
            {
                Canvas parentCanvas = slideImg.GetComponentInParent<Canvas>();
                if (parentCanvas != null)
                    Undo.RegisterCreatedObjectUndo(parentCanvas.gameObject, "Create ParentTutorialOverlayCanvas");
                if (slideImg.sprite == null)
                    slideImg.sprite = Resources.Load<Sprite>("Tutorial/Parent/Tutorial01");
                slideImg.color = Color.white;
                slideImg.gameObject.SetActive(true);

                Transform parentTr = slideImg.transform.parent;
                if (parentTr != null)
                {
                    Transform dimTr = parentTr.Find("TutorialDimBackground");
                    if (dimTr != null)
                    {
                        dimTr.SetSiblingIndex(0);
                        dimTr.gameObject.SetActive(true);
                    }
                    slideImg.transform.SetSiblingIndex(1);
                    Transform okTr = parentTr.Find("TutorialOkButton");
                    if (okTr != null)
                    {
                        okTr.SetAsLastSibling();
                        okTr.gameObject.SetActive(true);
                    }
                }

                SerializedObject so = new SerializedObject(parentTime);
                so.Update();
                so.ApplyModifiedProperties();

                EditorUtility.SetDirty(parentTime);
                EditorUtility.SetDirty(slideImg.gameObject);
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
            if (activeScene.IsValid())
                EditorSceneManager.MarkSceneDirty(activeScene);
            Debug.Log($"[TutorialAndDifficultyUiEditor] シーンに編集用UIを生成/更新しました（対象={createdCount}件）。Hierarchyから位置・サイズを調整してシーンを保存してください（表示ONのまま保存しても再生時は自動で非表示になります）。");
        }
    }

    [MenuItem("Tools/Friday13th/難易度確認ポップアップをプレビュー編集 (EASY)")]
    public static void PreviewDifficultyDetailEasy() => PreviewDifficultyDetailPopup(0);

    [MenuItem("Tools/Friday13th/難易度確認ポップアップをプレビュー編集 (NORMAL)")]
    public static void PreviewDifficultyDetailNormal() => PreviewDifficultyDetailPopup(1);

    [MenuItem("Tools/Friday13th/難易度確認ポップアップをプレビュー編集 (HARD)")]
    public static void PreviewDifficultyDetailHard() => PreviewDifficultyDetailPopup(2);

    [MenuItem("Tools/Friday13th/難易度確認ポップアップをプレビュー編集 (TUTORIAL)")]
    public static void PreviewDifficultyDetailTutorial() => PreviewDifficultyDetailPopup(3);

    [MenuItem("Tools/Friday13th/難易度確認ポップアップの位置を画像枠に自動整列（リセット）")]
    public static void ResetDifficultyDetailPopupLayout()
    {
        DifficultyTitleUI diffUi = EnsureDifficultyTitleUiInEditor();
        if (diffUi == null)
        {
            EditorUtility.DisplayDialog("確認ポップアップ整列", "現在のシーンに Canvas または DifficultyTitleUI が見つかりませんでした。タイトルシーンで実行してください。", "OK");
            return;
        }

        Undo.RecordObject(diffUi, "Reset DifficultyDetailPopup Layout");
        Undo.RegisterFullObjectHierarchyUndo(diffUi.gameObject, "Reset DifficultyDetailPopup Hierarchy");
        diffUi.ResetDetailPopupLayoutToArtwork();
        GameObject detailRoot = diffUi.PreviewDetailPopupInEditor(1);

        SerializedObject so = new SerializedObject(diffUi);
        so.Update();
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(diffUi);
        if (detailRoot != null)
        {
            EditorUtility.SetDirty(detailRoot);
            Selection.activeGameObject = detailRoot;
        }

        Scene activeScene = SceneManager.GetActiveScene();
        if (activeScene.IsValid())
            EditorSceneManager.MarkSceneDirty(activeScene);

        Debug.Log("[TutorialAndDifficultyUiEditor] DifficultyDetailPopup の説明テキスト・OKボタン・BACKボタンの位置を背景画像枠に自動整列しました。");
    }

    private static void PreviewDifficultyDetailPopup(int modeIndex)
    {
        DifficultyTitleUI diffUi = EnsureDifficultyTitleUiInEditor();
        if (diffUi == null)
        {
            EditorUtility.DisplayDialog("確認ポップアップ編集", "現在のシーンに Canvas または DifficultyTitleUI が見つかりませんでした。タイトルシーンで実行してください。", "OK");
            return;
        }

        Undo.RecordObject(diffUi, "Preview DifficultyDetailPopup");
        Undo.RegisterFullObjectHierarchyUndo(diffUi.gameObject, "Preview DifficultyDetailPopup Hierarchy");
        GameObject detailRoot = diffUi.PreviewDetailPopupInEditor(modeIndex);

        SerializedObject so = new SerializedObject(diffUi);
        so.Update();
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(diffUi);
        if (detailRoot != null)
        {
            EditorUtility.SetDirty(detailRoot);
            Transform cardTr = detailRoot.transform.Find("DetailCard");
            Selection.activeGameObject = cardTr != null ? cardTr.gameObject : detailRoot;
        }

        Scene activeScene = SceneManager.GetActiveScene();
        if (activeScene.IsValid())
            EditorSceneManager.MarkSceneDirty(activeScene);

        string modeName = modeIndex switch { 0 => "EASY", 1 => "NORMAL", 2 => "HARD", _ => "TUTORIAL" };
        Debug.Log($"[TutorialAndDifficultyUiEditor] DifficultyDetailPopup ({modeName}) をプレビュー表示しました。DetailCard 配下の DetailDescText / ConfirmBtn / BackBtn を Scene ビューで直接ドラッグして位置調整できます（そのまま保存しても再生時は自動で非表示になります）。");
    }

    private static DifficultyTitleUI EnsureDifficultyTitleUiInEditor()
    {
        DifficultyTitleUI diffUi = Object.FindFirstObjectByType<DifficultyTitleUI>(FindObjectsInactive.Include);
        if (diffUi == null)
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
                diffUi = Undo.AddComponent<DifficultyTitleUI>(host);
            }
        }
        return diffUi;
    }

    [MenuItem("Tools/Friday13th/プレビュー表示をすべて隠す（初期状態に戻す）")]
    public static void HideAllPreviewUisInCurrentScene()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        int hiddenCount = 0;

        DifficultyTitleUI[] diffUis = Object.FindObjectsByType<DifficultyTitleUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < diffUis.Length; i++)
        {
            if (diffUis[i] == null) continue;
            Undo.RecordObject(diffUis[i], "Hide DifficultyTitleUI Preview");
            diffUis[i].ForceHideAllModalsInScene();
            EditorUtility.SetDirty(diffUis[i]);
            hiddenCount++;
        }

        TutorialFlow[] tutorialFlows = Object.FindObjectsByType<TutorialFlow>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < tutorialFlows.Length; i++)
        {
            if (tutorialFlows[i] == null) continue;
            Undo.RecordObject(tutorialFlows[i], "Hide TutorialFlow Preview");
            tutorialFlows[i].HideTutorialSlide();
            tutorialFlows[i].HideTutorialPauseOverlay();
            EditorUtility.SetDirty(tutorialFlows[i]);
            hiddenCount++;
        }

        ParentGameTimeManager[] parentTimes = Object.FindObjectsByType<ParentGameTimeManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < parentTimes.Length; i++)
        {
            if (parentTimes[i] == null) continue;
            Undo.RecordObject(parentTimes[i], "Hide ParentGameTimeManager Preview");
            parentTimes[i].HideParentTutorialSlide();
            EditorUtility.SetDirty(parentTimes[i]);
            hiddenCount++;
        }

        if (hiddenCount > 0 && activeScene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(activeScene);
            Debug.Log($"[TutorialAndDifficultyUiEditor] プレビュー表示を非表示（初期状態）に戻しました（対象={hiddenCount}件）。");
        }
    }

    private static Canvas FindMainCanvas()
    {
        Canvas[] canvases = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Canvas best = null;
        int bestScore = int.MinValue;
        foreach (Canvas c in canvases)
        {
            if (c == null || !c.gameObject.activeInHierarchy) continue;
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
