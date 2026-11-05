using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// ParentSettingsUiBuilder:
/// ParentTitle シーンへ「設定パネル一式」を Unity の UI API で作成し、
/// ParentVolumeSettingsPanel の SerializeField 参照を割り当てる Editor ツール。
///
/// メニュー: Tools/Team13/Create Parent Settings UI
///
/// 前提と安全策:
///   - Play中は実行不可。
///   - 開いているSceneが ParentTitle であること（Sceneの自動切替・再読込はしない）。
///   - 対象 Canvas と ParentVolumeSettingsPanel がシーン内で一意に見つかること。
///   - 同名 SettingsPanel が既にある場合は削除・上書き・重複生成せず停止する
///     （再実行でユーザー調整値を初期化しない）。
///   - Scene は変更済み状態にするが自動保存しない（ユーザーが確認してから保存する）。
///   - 作成・参照割り当ては Undo 1回で戻せる。
///
/// コンポーネントGUIDやfileIDは一切手書きしない（すべて API 経由）。
/// ここで設定する位置・サイズ・色は「作成時の初期値」にすぎず、以後は Inspector で自由に調整できる。
/// </summary>
public static class ParentSettingsUiBuilder
{
    private const string MenuPath = "Tools/Team13/Create Parent Settings UI";
    private const string TargetSceneName = "ParentTitle";
    private const string PanelName = "SettingsPanel";

    // ── 初期レイアウト（前回指定の「大きめ」レイアウト）──
    private static readonly Vector2 PanelSize = new Vector2(900f, 620f);
    private static readonly Color PanelColor = new Color(0.07f, 0.07f, 0.10f, 0.90f);
    private static readonly Vector2 LabelSize = new Vector2(220f, 56f);
    private static readonly Vector2 SliderSize = new Vector2(440f, 36f);
    private static readonly Vector2 ButtonSize = new Vector2(260f, 64f);
    private const float LabelX = -300f;
    private const float SliderX = 60f;
    private const float TitleY = 240f;
    private const float BgmY = 110f;
    private const float SeY = 10f;
    private const float ButtonY = -220f;
    private const float ButtonX = 180f;

    [MenuItem(MenuPath)]
    public static void Create()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Fail("Play中は実行できません。停止してから実行してください。");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || scene.name != TargetSceneName)
        {
            Fail($"開いているSceneが {TargetSceneName} ではありません（現在: {(scene.IsValid() ? scene.name : "<無効>")}）。Sceneは切り替えていません。");
            return;
        }

        ParentVolumeSettingsPanel[] panels = FindAllInScene<ParentVolumeSettingsPanel>(scene);
        if (panels.Length != 1)
        {
            Fail($"ParentVolumeSettingsPanel が {panels.Length} 個見つかりました。シーン内で一意になるよう整理してから実行してください。");
            return;
        }
        ParentVolumeSettingsPanel panel = panels[0];

        Canvas canvas = panel.GetComponentInParent<Canvas>(true);
        if (canvas == null)
        {
            Fail("ParentVolumeSettingsPanel の親に Canvas が見つかりません。");
            return;
        }

        if (FindChildByName(canvas.transform, PanelName) != null)
        {
            Fail($"Canvas 配下に '{PanelName}' が既に存在します。削除・上書き・重複生成はしません。既存の設定パネルを確認してください。");
            return;
        }

        TMP_Text fontSource = FindFontSource(canvas);
        if (fontSource == null)
        {
            Fail("フォント参照元となる TMP テキストが Canvas 内に見つかりません。タイトルUIのテキストを残した状態で実行してください。");
            return;
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Create Parent Settings UI");

        GameObject panelRoot = BuildPanel(canvas.transform, fontSource);
        AssignReferences(panel, panelRoot);

        Undo.CollapseUndoOperations(undoGroup);

        // Scene は変更済みにするが、保存はしない（ユーザーが見てから保存する）。
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = panelRoot;
        EditorGUIUtility.PingObject(panelRoot);

        Debug.Log($"[ParentSettingsUiBuilder] '{PanelName}' を作成し、ParentVolumeSettingsPanel へ参照を割り当てました（非アクティブ）。" +
                  "レイアウト・文字は Inspector で調整できます。保存はご自身で行ってください。", panelRoot);
    }

    private static void Fail(string message)
    {
        Debug.LogError($"[ParentSettingsUiBuilder] {message}");
        EditorUtility.DisplayDialog("Create Parent Settings UI", message, "OK");
    }

    private static T[] FindAllInScene<T>(Scene scene) where T : Component
    {
        var all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var list = new List<T>();
        foreach (var c in all)
        {
            if (c != null && c.gameObject.scene == scene) list.Add(c);
        }
        return list.ToArray();
    }

    private static GameObject FindChildByName(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child.gameObject;
        }
        return null;
    }

    /// <summary>既存タイトルUIの TMP からフォント参照を借りる（共有フォント・Material 自体は変更しない）。</summary>
    private static TMP_Text FindFontSource(Canvas canvas)
    {
        foreach (var t in canvas.GetComponentsInChildren<TMP_Text>(true))
        {
            if (t != null && t.font != null) return t;
        }
        return null;
    }

    // ── UI 生成（Unity の UI API のみ。GUID/fileID は書かない）──
    private static GameObject BuildPanel(Transform canvas, TMP_Text fontSource)
    {
        GameObject root = CreateUiObject(PanelName, canvas);
        var rootRt = (RectTransform)root.transform;
        Place(rootRt, Vector2.zero, PanelSize);
        var bg = root.AddComponent<Image>();
        bg.color = PanelColor;

        // 見出し（上部中央）
        TextMeshProUGUI title = CreateLabel("Title", rootRt, fontSource, "設定", 48f);
        Place((RectTransform)title.transform, new Vector2(0f, TitleY), new Vector2(560f, 72f));

        // BGM 行（ラベル左／スライダー右）
        TextMeshProUGUI bgmLabel = CreateLabel("BgmLabel", rootRt, fontSource, "BGM", 36f);
        Place((RectTransform)bgmLabel.transform, new Vector2(LabelX, BgmY), LabelSize);
        Slider bgmSlider = CreateSlider("BgmSlider", rootRt, new Vector2(SliderX, BgmY));

        // SE 行（ラベル左／スライダー右）
        TextMeshProUGUI seLabel = CreateLabel("SeLabel", rootRt, fontSource, "SE", 36f);
        Place((RectTransform)seLabel.transform, new Vector2(LabelX, SeY), LabelSize);
        Slider seSlider = CreateSlider("SeSlider", rootRt, new Vector2(SliderX, SeY));

        // 下段（Reset 左／Close 右。パネル内に収まる余白を確保）
        CreateButton("ResetButton", rootRt, fontSource, "リセット", new Vector2(-ButtonX, ButtonY));
        CreateButton("CloseButton", rootRt, fontSource, "閉じる", new Vector2(ButtonX, ButtonY));

        // 作成直後は非アクティブ（実行開始時は閉じた状態にする）
        root.SetActive(false);
        return root;
    }

    private static GameObject CreateUiObject(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
        Undo.RegisterCreatedObjectUndo(go, "Create Parent Settings UI");
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer >= 0) go.layer = uiLayer;
        go.transform.SetParent(parent, false);
        return go;
    }

    private static TextMeshProUGUI CreateLabel(string name, Transform parent, TMP_Text fontSource, string text, float fontSize)
    {
        GameObject go = CreateUiObject(name, parent);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.font = fontSource.font;                                   // 共有フォントを参照するだけ（変更しない）
        if (fontSource.fontSharedMaterial != null) tmp.fontSharedMaterial = fontSource.fontSharedMaterial;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        return tmp;
    }

    private static Slider CreateSlider(string name, Transform parent, Vector2 anchoredPos)
    {
        GameObject go = CreateUiObject(name, parent);
        var rt = (RectTransform)go.transform;
        Place(rt, anchoredPos, SliderSize);

        var slider = go.AddComponent<Slider>();
        slider.transition = Selectable.Transition.None;

        var bg = go.AddComponent<Image>();
        bg.color = new Color(1f, 1f, 1f, 0.18f);

        // Fill（塗り）
        GameObject fillArea = CreateUiObject("Fill Area", rt);
        var fillAreaRt = (RectTransform)fillArea.transform;
        fillAreaRt.anchorMin = Vector2.zero;
        fillAreaRt.anchorMax = Vector2.one;
        fillAreaRt.offsetMin = new Vector2(10f, 10f);
        fillAreaRt.offsetMax = new Vector2(-10f, -10f);

        GameObject fill = CreateUiObject("Fill", fillAreaRt);
        var fillRt = (RectTransform)fill.transform;
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = new Vector2(0f, 1f);
        fillRt.pivot = new Vector2(0.5f, 0.5f);
        fillRt.sizeDelta = new Vector2(10f, 0f);
        var fillImg = fill.AddComponent<Image>();
        fillImg.color = new Color(1f, 0.85f, 0.35f, 1f);

        // Handle（つまみ）
        GameObject handleArea = CreateUiObject("Handle Slide Area", rt);
        var handleAreaRt = (RectTransform)handleArea.transform;
        handleAreaRt.anchorMin = Vector2.zero;
        handleAreaRt.anchorMax = Vector2.one;
        handleAreaRt.offsetMin = new Vector2(10f, 0f);
        handleAreaRt.offsetMax = new Vector2(-10f, 0f);

        GameObject handle = CreateUiObject("Handle", handleAreaRt);
        var handleRt = (RectTransform)handle.transform;
        handleRt.anchorMin = Vector2.zero;
        handleRt.anchorMax = new Vector2(0f, 1f);
        handleRt.pivot = new Vector2(0.5f, 0.5f);
        handleRt.sizeDelta = new Vector2(28f, 0f);
        var handleImg = handle.AddComponent<Image>();
        handleImg.color = Color.white;

        slider.fillRect = fillRt;
        slider.handleRect = handleRt;
        slider.targetGraphic = handleImg;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.wholeNumbers = false;
        slider.value = 1f;
        return slider;
    }

    private static Button CreateButton(string name, Transform parent, TMP_Text fontSource, string text, Vector2 anchoredPos)
    {
        GameObject go = CreateUiObject(name, parent);
        var rt = (RectTransform)go.transform;
        Place(rt, anchoredPos, ButtonSize);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.20f, 0.22f, 0.30f, 0.95f);

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        TextMeshProUGUI label = CreateLabel("Label", rt, fontSource, text, 32f);
        var labelRt = (RectTransform)label.transform;
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;
        return btn;
    }

    private static void Place(RectTransform rt, Vector2 anchoredPos, Vector2 size)
    {
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
    }

    // ── ParentVolumeSettingsPanel の SerializeField へ参照を割り当てる ──
    private static void AssignReferences(ParentVolumeSettingsPanel panel, GameObject panelRoot)
    {
        Transform root = panelRoot.transform;

        Undo.RecordObject(panel, "Create Parent Settings UI");
        var so = new SerializedObject(panel);

        SetRef(so, "settingsPanel", panelRoot);
        SetRef(so, "titleText", FindComponentInChild<TextMeshProUGUI>(root, "Title"));
        SetRef(so, "bgmLabel", FindComponentInChild<TextMeshProUGUI>(root, "BgmLabel"));
        SetRef(so, "bgmSlider", FindComponentInChild<Slider>(root, "BgmSlider"));
        SetRef(so, "seLabel", FindComponentInChild<TextMeshProUGUI>(root, "SeLabel"));
        SetRef(so, "seSlider", FindComponentInChild<Slider>(root, "SeSlider"));
        SetRef(so, "resetButton", FindComponentInChild<Button>(root, "ResetButton"));
        SetRef(so, "closeButton", FindComponentInChild<Button>(root, "CloseButton"));

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(panel);
    }

    private static T FindComponentInChild<T>(Transform root, string name) where T : Component
    {
        GameObject go = FindChildByName(root, name);
        return go != null ? go.GetComponent<T>() : null;
    }

    private static void SetRef(SerializedObject so, string propertyName, Object value)
    {
        if (value == null)
        {
            Debug.LogWarning($"[ParentSettingsUiBuilder] 参照 '{propertyName}' の割り当て対象が見つかりませんでした。");
            return;
        }

        var prop = so.FindProperty(propertyName);
        if (prop == null)
        {
            Debug.LogWarning($"[ParentSettingsUiBuilder] ParentVolumeSettingsPanel に '{propertyName}' フィールドがありません。");
            return;
        }

        prop.objectReferenceValue = value;
    }
}
