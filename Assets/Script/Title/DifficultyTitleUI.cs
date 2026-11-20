using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// DifficultyTitleUI：
/// 子機タイトル画面（ChildTitle）にモード選択UI（EASY / NORMAL / HARD / TUTORIAL）を表示する。
/// ・親機タイトル画面（ParentTitle）では難易度選択UIを一切生成・表示しない。
/// ・EASY / NORMAL / HARD を選んだ場合はチュートリアルなし（IncludeTutorial = false）で本編を開始する。
/// ・TUTORIAL を選んだ場合はチュートリアルモード（IncludeTutorial = true）として子機・親機チュートリアルを実行する。
/// </summary>
public class DifficultyTitleUI : MonoBehaviour
{
    private static readonly Dictionary<string, Sprite> s_spriteCache = new Dictionary<string, Sprite>();

    public static DifficultyTitleUI Instance { get; private set; }

    /// <summary>難易度選択モーダルが開いているか（MenuNavigationController 等の背景入力抑止用）。</summary>
    public static bool IsAnyModalOpen => Instance != null && Instance._isOpen;

    public bool IsOpen => _isOpen;

    [Header("UI References (未設定時は自動生成または子階層から自動取得)")]
    [SerializeField] private GameObject _badgeRoot;
    [SerializeField] private TextMeshProUGUI _badgeLabel;
    [SerializeField] private GameObject _modalRoot;
    [SerializeField] private GameObject _detailPopupRoot;
    [SerializeField] private Image _detailCardImage;
    [SerializeField] private TextMeshProUGUI _detailDescText;
    [SerializeField] private TextMeshProUGUI _detailOkLabel;
    [SerializeField] private TextMeshProUGUI _detailBackLabel;
    [SerializeField] private Image _detailOkSelectedOutline;
    [SerializeField] private Image _detailBackSelectedOutline;
    [SerializeField] private TextMeshProUGUI _closeModalBtnLabel;
    [SerializeField] private Image _closeModalSelectedOutline;
    [SerializeField] private Image[] _cardSelectionOutlines = new Image[3];
    [SerializeField] private Image _tutorialSelectionOutline;
    [SerializeField] private TextMeshProUGUI _tutorialStateText;

    [Header("Layout Settings - Badge (右上のモード表示ボタン)")]
    [SerializeField] private int _badgeSortingOrder = 60;
    [SerializeField] private Vector2 _badgeAnchoredPosition = new Vector2(-36f, -28f);
    [SerializeField] private Vector2 _badgeSize = new Vector2(420f, 76f);
    [SerializeField] private float _badgeFontSize = 22f;

    [Header("Layout Settings - Modal & Difficulty Cards")]
    [SerializeField] private int _modalSortingOrder = 100;
    [SerializeField] private float[] _cardAnchoredX = new float[] { -460f, 0f, 460f };
    [SerializeField] private float _cardAnchoredY = -150f;
    [SerializeField] private Vector2 _cardSize = new Vector2(380f, 480f);

    [Header("Layout Settings - Tutorial Button & Back Button")]
    [SerializeField] private Vector2 _tutorialBtnAnchoredPosition = new Vector2(0f, 260f);
    [SerializeField] private Vector2 _tutorialBtnSize = new Vector2(1000f, 260f);
    [SerializeField] private Vector2 _closeBtnAnchoredPosition = new Vector2(0f, -455f);
    [SerializeField] private Vector2 _closeBtnSize = new Vector2(320f, 64f);
    [SerializeField] private float _closeBtnFontSize = 26f;

    [Header("Layout Settings - Detail Popup (確認ポップアップ)")]
    [SerializeField] private Vector2 _detailCardAnchoredPosition = Vector2.zero;
    [SerializeField] private Vector2 _detailCardSize = new Vector2(1280f, 720f);
    [SerializeField] private Vector2 _detailDescAnchoredPosition = new Vector2(148f, 15f);
    [SerializeField] private Vector2 _detailDescSize = new Vector2(580f, 220f);
    [SerializeField] private float _detailDescFontSize = 28f;
    [SerializeField] private Vector2 _detailOkAnchoredPosition = new Vector2(-15f, -182f);
    [SerializeField] private Vector2 _detailBackAnchoredPosition = new Vector2(310f, -182f);
    [SerializeField] private Vector2 _detailActionBtnSize = new Vector2(250f, 95f);
    [SerializeField] private float _detailActionFontSize = 32f;

    private bool _isOpen;
    private bool _isDetailOpen;
    private bool _isTutorialDetailPopup;
    private GameDifficulty.Level _pendingLevel = GameDifficulty.Level.Normal;
    private int _cursorIndex = 1; // 0=Easy, 1=Normal, 2=Hard, 3=TutorialMode, 4=CloseModalBtn(BACK)
    private int _lastCardCursorIndex = 1; // 0=Easy, 1=Normal, 2=Hard
    private int _detailCursorIndex = 0; // 0=OK, 1=BACK
    private int _openedFrame = -1;
    private int _detailOpenedFrame = -1;
    private float _lastNavTime;
    private Action _pendingGameStartCallback;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void BootstrapOnLoad()
    {
        SceneManager.sceneLoaded -= OnSceneLoadedStatic;
        SceneManager.sceneLoaded += OnSceneLoadedStatic;
        TryAttachToActiveScene(SceneManager.GetActiveScene().name);
    }

    private static void OnSceneLoadedStatic(Scene scene, LoadSceneMode mode)
    {
        TryAttachToActiveScene(scene.name);
    }

    public static DifficultyTitleUI EnsureAttached()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        if (sceneName != "ChildTitle") return null;

        DifficultyTitleUI existing = FindFirstObjectByType<DifficultyTitleUI>(FindObjectsInactive.Include);
        if (existing != null)
        {
            if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
            return existing;
        }

        TryAttachToActiveScene(sceneName);
        return FindFirstObjectByType<DifficultyTitleUI>(FindObjectsInactive.Include);
    }

    private static void TryAttachToActiveScene(string sceneName)
    {
        if (sceneName != "ChildTitle")
        {
            // ParentTitle 等では難易度選択UIを一切表示しない。もしシーン内に配置されている場合はすべて非表示にする。
            HideAllDifficultyUiInCurrentScene();
            return;
        }

        DifficultyTitleUI existing = FindFirstObjectByType<DifficultyTitleUI>(FindObjectsInactive.Include);
        if (existing != null)
        {
            if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
            existing.ForceHideAllModalsInScene();
            return;
        }

        Canvas targetCanvas = FindMainUiCanvas();
        if (targetCanvas == null) return;

        // もし既にシーン上に孤立した DifficultyModalPanel 等がある場合は既存の親を利用するか統合する
        Transform strayModal = FindTransformInSceneByName("DifficultyModalPanel");
        if (strayModal != null && strayModal.parent != null && strayModal.parent.GetComponent<Canvas>() == null)
        {
            GameObject hostObj = strayModal.parent.gameObject;
            if (hostObj.GetComponent<DifficultyTitleUI>() == null)
                hostObj.AddComponent<DifficultyTitleUI>();
            return;
        }

        var hostGo = new GameObject("DifficultyTitleUI", typeof(RectTransform));
        hostGo.transform.SetParent(targetCanvas.transform, false);
        RectTransform hostRt = hostGo.GetComponent<RectTransform>();
        hostRt.anchorMin = Vector2.zero;
        hostRt.anchorMax = Vector2.one;
        hostRt.offsetMin = Vector2.zero;
        hostRt.offsetMax = Vector2.zero;
        hostGo.AddComponent<DifficultyTitleUI>();
    }

    private static void HideAllDifficultyUiInCurrentScene()
    {
        string[] targetNames = { "DifficultyTitleUI", "DifficultyBadgeButton", "DifficultyModalPanel", "DifficultyDetailPopup" };
        for (int n = 0; n < targetNames.Length; n++)
        {
            List<Transform> found = FindAllTransformsInSceneByName(targetNames[n]);
            for (int i = 0; i < found.Count; i++)
            {
                if (found[i] != null && found[i].gameObject.activeSelf)
                    found[i].gameObject.SetActive(false);
            }
        }

        DifficultyTitleUI[] uis = FindObjectsByType<DifficultyTitleUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < uis.Length; i++)
        {
            if (uis[i] != null && uis[i].gameObject.activeSelf)
                uis[i].gameObject.SetActive(false);
        }
    }

    private static Transform FindTransformInSceneByName(string targetName)
    {
        Scene activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid()) return null;

        GameObject[] roots = activeScene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            Transform found = FindTransformRecursive(roots[i].transform, targetName);
            if (found != null) return found;
        }
        return null;
    }

    private static Transform FindTransformRecursive(Transform parent, string targetName)
    {
        if (parent == null) return null;
        if (string.Equals(parent.name, targetName, StringComparison.Ordinal))
            return parent;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform found = FindTransformRecursive(parent.GetChild(i), targetName);
            if (found != null) return found;
        }
        return null;
    }

    private static List<Transform> FindAllTransformsInSceneByName(string targetName)
    {
        var list = new List<Transform>();
        Scene activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid()) return list;

        GameObject[] roots = activeScene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            CollectTransformsRecursive(roots[i].transform, targetName, list);
        }
        return list;
    }

    private static void CollectTransformsRecursive(Transform parent, string targetName, List<Transform> results)
    {
        if (parent == null) return;
        if (string.Equals(parent.name, targetName, StringComparison.Ordinal))
            results.Add(parent);

        for (int i = 0; i < parent.childCount; i++)
        {
            CollectTransformsRecursive(parent.GetChild(i), targetName, results);
        }
    }

    private static Canvas FindMainUiCanvas()
    {
        Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
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
            if (string.Equals(c.gameObject.name, "Canvas", StringComparison.OrdinalIgnoreCase)) score += 15;
            if (string.Equals(c.gameObject.name, "Title", StringComparison.OrdinalIgnoreCase)) score -= 25;

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best != null ? best : FindFirstObjectByType<Canvas>();
    }

    public static Sprite LoadResourceSprite(string resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath)) return null;
        if (s_spriteCache.TryGetValue(resourcePath, out Sprite cached) && cached != null)
            return cached;

        Sprite sp = Resources.Load<Sprite>(resourcePath);
        if (sp == null)
        {
            Texture2D tex = Resources.Load<Texture2D>(resourcePath);
            if (tex != null)
            {
                sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
        }

        if (sp != null) s_spriteCache[resourcePath] = sp;
        return sp;
    }

    private void Awake()
    {
        // ParentTitle 等、ChildTitle 以外のシーンに配置されている場合は完全に非表示・無効化する
        if (SceneManager.GetActiveScene().name != "ChildTitle")
        {
            ForceHideAllModalsInScene();
            if (_badgeRoot != null) _badgeRoot.SetActive(false);
            HideAllDifficultyUiInCurrentScene();
            gameObject.SetActive(false);
            return;
        }

        // シーン内に重複した DifficultyTitleUI が存在する場合は1つに統合し、余分な方を無効化する
        DifficultyTitleUI[] allInstances = FindObjectsByType<DifficultyTitleUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (Instance != null && Instance != this)
        {
            // 既に有効な Instance がある場合は、自分自身の配下のモーダルをすべて非表示にして自身を破棄する
            if (_detailPopupRoot != null) _detailPopupRoot.SetActive(false);
            if (_modalRoot != null) _modalRoot.SetActive(false);
            Transform myModal = transform.Find("DifficultyModalPanel");
            if (myModal != null) myModal.gameObject.SetActive(false);
            Destroy(gameObject);
            return;
        }

        Instance = this;
        for (int i = 0; i < allInstances.Length; i++)
        {
            DifficultyTitleUI other = allInstances[i];
            if (other != null && other != this)
            {
                Transform otherModal = other.transform.Find("DifficultyModalPanel");
                if (otherModal != null) otherModal.gameObject.SetActive(false);
                other.gameObject.SetActive(false);
            }
        }

        _isOpen = false;
        _isDetailOpen = false;
        _isTutorialDetailPopup = false;

        BuildUI(bindRuntimeListeners: true);
        ForceHideAllModalsInScene();

        GameDifficulty.Changed += OnDifficultyChanged;
        GameDifficulty.IncludeTutorialChanged += OnTutorialChanged;
        GameLanguage.Changed += OnLanguageChanged;
        RefreshVisuals();
    }

    private void Start()
    {
        // エディタ上でモーダルを表示ONにしたまま保存されたシーンでも、再生開始時は必ず閉じた状態でスタートする
        if (!_isOpen)
        {
            ForceHideAllModalsInScene();
        }
    }

    /// <summary>
    /// シーン上に配置・複製されたすべての DifficultyModalPanel / DifficultyDetailPopup を確実に非表示にし、
    /// 内部状態も閉じた状態（_isOpen = false, _isDetailOpen = false）に初期化する。
    /// </summary>
    public void ForceHideAllModalsInScene()
    {
        _isOpen = false;
        _isDetailOpen = false;
        _isTutorialDetailPopup = false;

        if (_detailPopupRoot != null) _detailPopupRoot.SetActive(false);
        if (_modalRoot != null) _modalRoot.SetActive(false);

        List<Transform> allDetails = FindAllTransformsInSceneByName("DifficultyDetailPopup");
        for (int i = 0; i < allDetails.Count; i++)
        {
            if (allDetails[i] != null && allDetails[i].gameObject.activeSelf)
                allDetails[i].gameObject.SetActive(false);
        }

        List<Transform> allModals = FindAllTransformsInSceneByName("DifficultyModalPanel");
        for (int i = 0; i < allModals.Count; i++)
        {
            if (allModals[i] != null && allModals[i].gameObject.activeSelf)
                allModals[i].gameObject.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        GameDifficulty.Changed -= OnDifficultyChanged;
        GameDifficulty.IncludeTutorialChanged -= OnTutorialChanged;
        GameLanguage.Changed -= OnLanguageChanged;
    }

    private void OnDifficultyChanged(GameDifficulty.Level _) => RefreshVisuals();
    private void OnTutorialChanged(bool _) => RefreshVisuals();
    private void OnLanguageChanged(GameLanguage.Language _) => RefreshVisuals();

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        Gamepad gp = Gamepad.current;

        if (!_isOpen)
        {
            bool openShortcut = (kb != null && kb.tabKey.wasPressedThisFrame) ||
                                (gp != null && gp.buttonNorth.wasPressedThisFrame);
            if (openShortcut)
            {
                OpenPanel();
                return;
            }
            return;
        }

        if (Time.frameCount <= _openedFrame)
            return;

        if (_isDetailOpen)
        {
            if (Time.frameCount <= _detailOpenedFrame)
                return;

            if (Time.unscaledTime - _lastNavTime > 0.16f)
            {
                int moveX = 0;
                if (kb != null)
                {
                    if (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame) moveX = -1;
                    else if (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame) moveX = 1;
                }
                if (gp != null)
                {
                    if (gp.dpad.left.wasPressedThisFrame || gp.leftStick.x.ReadValue() < -0.55f) moveX = -1;
                    else if (gp.dpad.right.wasPressedThisFrame || gp.leftStick.x.ReadValue() > 0.55f) moveX = 1;
                }

                if (moveX != 0)
                {
                    _detailCursorIndex = (_detailCursorIndex + 1) % 2;
                    _lastNavTime = Time.unscaledTime;
                    RefreshDetailSelectionVisuals();
                }
            }

            bool confirm = (kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)) ||
                           (gp != null && gp.buttonSouth.wasPressedThisFrame);
            bool cancel = (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame)) ||
                          (gp != null && gp.buttonEast.wasPressedThisFrame);

            if (confirm)
            {
                if (_detailCursorIndex == 0)
                    ConfirmDetailSelection();
                else
                    CloseDetailPopup();
            }
            else if (cancel)
            {
                CloseDetailPopup();
            }
            return;
        }

        if (Time.unscaledTime - _lastNavTime > 0.18f)
        {
            int moveX = 0;
            int moveY = 0;

            if (kb != null)
            {
                if (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame) moveX = -1;
                else if (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame) moveX = 1;
                if (kb.upArrowKey.wasPressedThisFrame || kb.wKey.wasPressedThisFrame) moveY = -1;
                else if (kb.downArrowKey.wasPressedThisFrame || kb.sKey.wasPressedThisFrame) moveY = 1;
            }

            if (gp != null)
            {
                if (gp.dpad.left.wasPressedThisFrame || gp.leftStick.x.ReadValue() < -0.55f) moveX = -1;
                else if (gp.dpad.right.wasPressedThisFrame || gp.leftStick.x.ReadValue() > 0.55f) moveX = 1;
                if (gp.dpad.up.wasPressedThisFrame || gp.leftStick.y.ReadValue() > 0.55f) moveY = -1;
                else if (gp.dpad.down.wasPressedThisFrame || gp.leftStick.y.ReadValue() < -0.55f) moveY = 1;
            }

            if (moveX != 0)
            {
                if (_cursorIndex < 3)
                {
                    _cursorIndex = (_cursorIndex + moveX + 3) % 3;
                    _lastCardCursorIndex = _cursorIndex;
                }
                _lastNavTime = Time.unscaledTime;
                RefreshVisuals();
            }
            else if (moveY != 0)
            {
                if (moveY < 0) // Up
                {
                    if (_cursorIndex == 4)
                    {
                        _cursorIndex = Mathf.Clamp(_lastCardCursorIndex, 0, 2);
                    }
                    else if (_cursorIndex < 3)
                    {
                        _lastCardCursorIndex = _cursorIndex;
                        _cursorIndex = 3;
                    }
                    else if (_cursorIndex == 3)
                    {
                        _cursorIndex = 4;
                    }
                }
                else // Down (moveY > 0)
                {
                    if (_cursorIndex == 3)
                    {
                        _cursorIndex = Mathf.Clamp(_lastCardCursorIndex, 0, 2);
                    }
                    else if (_cursorIndex < 3)
                    {
                        _lastCardCursorIndex = _cursorIndex;
                        _cursorIndex = 4;
                    }
                    else if (_cursorIndex == 4)
                    {
                        _cursorIndex = 3;
                    }
                }
                _lastNavTime = Time.unscaledTime;
                RefreshVisuals();
            }
        }

        bool submit = (kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)) ||
                      (gp != null && gp.buttonSouth.wasPressedThisFrame);
        bool close = (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame)) ||
                     (gp != null && (gp.buttonEast.wasPressedThisFrame || gp.buttonNorth.wasPressedThisFrame));

        if (submit)
        {
            if (_cursorIndex == 4)
                ClosePanel();
            else if (_cursorIndex == 3)
                OpenTutorialDetailPopup();
            else
                OpenDetailPopup((GameDifficulty.Level)Mathf.Clamp(_cursorIndex, 0, 2));
        }
        else if (close)
        {
            ClosePanel();
        }
    }

    public void OpenForGameStart(Action onConfirmed)
    {
        _pendingGameStartCallback = onConfirmed;
        OpenPanel(fromRemote: false, keepPendingCallback: true);
    }

    public void OpenPanel(bool fromRemote = false, bool keepPendingCallback = false)
    {
        if (_modalRoot == null) return;
        if (!keepPendingCallback)
            _pendingGameStartCallback = null;

        // 背後のタイトルボタン（SOLOSTART / START!）に EventSystem フォーカスが残っていると、
        // 最初の決定キーで背後のボタンが再発火して OpenPanel がやり直され、NORMAL が押せなくなるため必ず解除する
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);

        _isOpen = true;
        _isDetailOpen = false;
        _isTutorialDetailPopup = false;
        _openedFrame = Time.frameCount;
        _lastCardCursorIndex = 1;
        _cursorIndex = 1;
        if (_detailPopupRoot != null) _detailPopupRoot.SetActive(false);
        _modalRoot.SetActive(true);
        _modalRoot.transform.SetAsLastSibling();
        RefreshVisuals();

        if (!fromRemote)
            GameDifficulty.SendTitleCommandToParent("OPEN_DIFFICULTY");
    }

    public void ClosePanel(bool fromRemote = false)
    {
        _isOpen = false;
        _isDetailOpen = false;
        _isTutorialDetailPopup = false;
        _pendingGameStartCallback = null;
        if (_detailPopupRoot != null) _detailPopupRoot.SetActive(false);
        if (_modalRoot != null) _modalRoot.SetActive(false);
        RefreshVisuals();

        if (!fromRemote)
            GameDifficulty.SendTitleCommandToParent("CLOSE_DIFFICULTY");
    }

    private void OpenDetailPopup(GameDifficulty.Level level)
    {
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);

        _cursorIndex = (int)level;
        _lastCardCursorIndex = _cursorIndex;
        _pendingLevel = level;

        if (_detailPopupRoot == null) return;
        _isDetailOpen = true;
        _isTutorialDetailPopup = false;
        _detailCursorIndex = 0;
        _detailOpenedFrame = Time.frameCount;

        string spritePath = level switch
        {
            GameDifficulty.Level.Easy => "Difficulty/ui_difficulty_easy",
            GameDifficulty.Level.Hard => "Difficulty/ui_difficulty_hard",
            _ => "Difficulty/ui_difficulty_normal"
        };

        if (_detailCardImage != null)
            _detailCardImage.sprite = LoadResourceSprite(spritePath);

        if (_detailDescText != null)
        {
            _detailDescText.text = GameDifficulty.GetDescription(level);
            _detailDescText.color = Color.white;
        }

        if (_detailOkLabel != null)
        {
            _detailOkLabel.text = "OK";
            _detailOkLabel.color = new Color(0.05f, 0.02f, 0.15f, 1f);
        }

        if (_detailBackLabel != null)
        {
            _detailBackLabel.text = "BACK";
            _detailBackLabel.color = new Color(0.05f, 0.02f, 0.15f, 1f);
        }

        _detailPopupRoot.SetActive(true);
        RefreshDetailSelectionVisuals();
        RefreshVisuals();
    }

    private void OpenTutorialDetailPopup()
    {
        _cursorIndex = 3;
        if (_detailPopupRoot == null)
        {
            GameDifficulty.SetIncludeTutorial(true);
            return;
        }

        _isDetailOpen = true;
        _isTutorialDetailPopup = true;
        _detailCursorIndex = 0;
        _detailOpenedFrame = Time.frameCount;

        if (_detailCardImage != null)
            _detailCardImage.sprite = LoadResourceSprite("Difficulty/ui_tutorial_desc");

        if (_detailDescText != null)
        {
            _detailDescText.text = "Tutorial Mode.\nPractice both Child and Parent controls step by step.";
            _detailDescText.color = new Color(0.05f, 0.02f, 0.18f, 1f);
        }

        if (_detailOkLabel != null)
        {
            _detailOkLabel.text = "OK";
            _detailOkLabel.color = Color.white;
        }

        if (_detailBackLabel != null)
        {
            _detailBackLabel.text = "BACK";
            _detailBackLabel.color = Color.white;
        }

        _detailPopupRoot.SetActive(true);
        RefreshDetailSelectionVisuals();
        RefreshVisuals();
    }

    private void CloseDetailPopup()
    {
        _isDetailOpen = false;
        _isTutorialDetailPopup = false;
        if (_detailPopupRoot != null)
            _detailPopupRoot.SetActive(false);
        RefreshVisuals();
    }

    private void OnDetailBackButtonClicked()
    {
        _detailCursorIndex = 1;
        CloseDetailPopup();
    }

    private void ConfirmDetailSelection()
    {
        if (_isTutorialDetailPopup)
        {
            // TUTORIAL モードのみチュートリアルを実行する
            GameDifficulty.SetIncludeTutorial(true);
        }
        else
        {
            // EASY / NORMAL / HARD ではチュートリアルを流さず、そのまま本編を開始する
            GameDifficulty.Set(_pendingLevel);
            GameDifficulty.SetIncludeTutorial(false);
        }

        Action startCallback = _pendingGameStartCallback;
        _pendingGameStartCallback = null;

        _isDetailOpen = false;
        _isTutorialDetailPopup = false;

        if (startCallback != null)
        {
            // ゲーム開始時：ロード画面（ChildLoading等）へ遷移するまでの1フレーム間に
            // 背後のタイトル画面が一瞬映るのを防ぐため、_modalRoot / _detailPopupRoot は非表示にせず
            // 暗転カバーを表示したまま（CLOSE_DIFFICULTY も送らずに）ゲーム開始処理を実行する
            _isOpen = false;
            startCallback.Invoke();
        }
        else
        {
            // 単なる難易度設定変更時（右上バッジやTabキーから開いた場合）は従来どおりパネルを閉じる
            if (_detailPopupRoot != null)
                _detailPopupRoot.SetActive(false);
            ClosePanel();
        }
    }

    private void RefreshDetailSelectionVisuals()
    {
        if (_detailOkSelectedOutline != null)
            _detailOkSelectedOutline.gameObject.SetActive(_detailCursorIndex == 0);
        if (_detailBackSelectedOutline != null)
            _detailBackSelectedOutline.gameObject.SetActive(_detailCursorIndex == 1);
    }

    [ContextMenu("難易度UIをシーン上に生成（Unity内で配置・サイズ調整用）")]
    public void BuildUIInSceneForEditing()
    {
        BuildUI(bindRuntimeListeners: Application.isPlaying);
        RefreshVisuals();

        // エディタ編集時は Scene ビューでそのまま配置・サイズを調整できるようモーダルを表示状態にする
        if (!Application.isPlaying && _modalRoot != null)
        {
            _modalRoot.SetActive(true);
            if (_detailPopupRoot != null)
            {
                if (_detailCardImage != null && _detailCardImage.sprite == null)
                    _detailCardImage.sprite = LoadResourceSprite("Difficulty/ui_difficulty_normal");
                if (_detailDescText != null && string.IsNullOrEmpty(_detailDescText.text))
                    _detailDescText.text = GameDifficulty.GetDescription(GameDifficulty.Level.Normal);
                _detailPopupRoot.SetActive(false);
            }
        }
    }

    [ContextMenu("確認ポップアップをプレビュー編集 (EASY)")]
    public void PreviewDetailEasyInEditor() => PreviewDetailPopupInEditor(0);

    [ContextMenu("確認ポップアップをプレビュー編集 (NORMAL)")]
    public void PreviewDetailNormalInEditor() => PreviewDetailPopupInEditor(1);

    [ContextMenu("確認ポップアップをプレビュー編集 (HARD)")]
    public void PreviewDetailHardInEditor() => PreviewDetailPopupInEditor(2);

    [ContextMenu("確認ポップアップをプレビュー編集 (TUTORIAL)")]
    public void PreviewDetailTutorialInEditor() => PreviewDetailPopupInEditor(3);

    /// <summary>
    /// Unityエディタ上で DifficultyDetailPopup を表示し、指定モード（0=Easy, 1=Normal, 2=Hard, 3=Tutorial）の
    /// 背景カード画像・説明文・ボタン文字色に切り替えて Scene ビューで直接位置やサイズを編集できるようにする。
    /// </summary>
    public GameObject PreviewDetailPopupInEditor(int modeIndex)
    {
        BuildUI(bindRuntimeListeners: Application.isPlaying);
        if (_modalRoot != null) _modalRoot.SetActive(true);
        if (_detailPopupRoot == null) return null;

        _detailPopupRoot.SetActive(true);
        _detailPopupRoot.transform.SetAsLastSibling();

        if (modeIndex == 3)
        {
            if (_detailCardImage != null)
                _detailCardImage.sprite = LoadResourceSprite("Difficulty/ui_tutorial_desc");
            if (_detailDescText != null)
            {
                _detailDescText.text = "Tutorial Mode.\nPractice both Child and Parent controls step by step.";
                _detailDescText.color = new Color(0.05f, 0.02f, 0.18f, 1f);
            }
            if (_detailOkLabel != null)
            {
                _detailOkLabel.text = "OK";
                _detailOkLabel.color = Color.white;
            }
            if (_detailBackLabel != null)
            {
                _detailBackLabel.text = "BACK";
                _detailBackLabel.color = Color.white;
            }
        }
        else
        {
            GameDifficulty.Level lv = (GameDifficulty.Level)Mathf.Clamp(modeIndex, 0, 2);
            string spritePath = lv switch
            {
                GameDifficulty.Level.Easy => "Difficulty/ui_difficulty_easy",
                GameDifficulty.Level.Hard => "Difficulty/ui_difficulty_hard",
                _ => "Difficulty/ui_difficulty_normal"
            };
            if (_detailCardImage != null)
                _detailCardImage.sprite = LoadResourceSprite(spritePath);
            if (_detailDescText != null)
            {
                _detailDescText.text = GameDifficulty.GetDescription(lv);
                _detailDescText.color = Color.white;
            }
            if (_detailOkLabel != null)
            {
                _detailOkLabel.text = "OK";
                _detailOkLabel.color = new Color(0.05f, 0.02f, 0.15f, 1f);
            }
            if (_detailBackLabel != null)
            {
                _detailBackLabel.text = "BACK";
                _detailBackLabel.color = new Color(0.05f, 0.02f, 0.15f, 1f);
            }
        }

        _detailCursorIndex = 0;
        RefreshDetailSelectionVisuals();
        return _detailPopupRoot;
    }

    /// <summary>
    /// DifficultyDetailPopup 内の DetailCard / DetailDescText / ConfirmBtn / BackBtn / 選択枠の位置・サイズを
    /// 背景カード画像（ui_difficulty_* / ui_tutorial_desc）の枠にぴったり合う既定値に整列する。
    /// </summary>
    [ContextMenu("確認ポップアップの位置・サイズを背景画像枠に自動整列")]
    public void ResetDetailPopupLayoutToArtwork()
    {
        _detailCardAnchoredPosition = Vector2.zero;
        _detailCardSize = new Vector2(1280f, 720f);
        _detailDescAnchoredPosition = new Vector2(148f, 15f);
        _detailDescSize = new Vector2(580f, 220f);
        _detailOkAnchoredPosition = new Vector2(-15f, -182f);
        _detailBackAnchoredPosition = new Vector2(310f, -182f);
        _detailActionBtnSize = new Vector2(250f, 95f);

        BuildUI(bindRuntimeListeners: Application.isPlaying);
        if (_detailPopupRoot == null) return;

        Transform detailCardTr = _detailPopupRoot.transform.Find("DetailCard");
        if (detailCardTr == null) return;

        RectTransform cardRt = detailCardTr.GetComponent<RectTransform>();
        if (cardRt != null)
        {
            cardRt.anchoredPosition = _detailCardAnchoredPosition;
            cardRt.sizeDelta = _detailCardSize;
        }

        if (_detailDescText != null)
        {
            RectTransform descRt = _detailDescText.rectTransform;
            descRt.anchoredPosition = _detailDescAnchoredPosition;
            descRt.sizeDelta = _detailDescSize;
        }

        Transform okTr = detailCardTr.Find("ConfirmBtn");
        if (okTr != null)
        {
            RectTransform okRt = okTr.GetComponent<RectTransform>();
            if (okRt != null)
            {
                okRt.anchoredPosition = _detailOkAnchoredPosition;
                okRt.sizeDelta = _detailActionBtnSize;
            }
            if (_detailOkLabel != null)
            {
                _detailOkLabel.rectTransform.anchoredPosition = Vector2.zero;
                _detailOkLabel.rectTransform.sizeDelta = _detailActionBtnSize;
            }
            Transform okSelTr = okTr.Find("OkSelectedOutline");
            if (okSelTr != null)
            {
                RectTransform okSelRt = okSelTr.GetComponent<RectTransform>();
                if (okSelRt != null)
                {
                    okSelRt.anchorMin = Vector2.zero;
                    okSelRt.anchorMax = Vector2.one;
                    okSelRt.offsetMin = new Vector2(-46f, -26f);
                    okSelRt.offsetMax = new Vector2(46f, 26f);
                }
                Image okSelImg = okSelTr.GetComponent<Image>();
                if (okSelImg != null) okSelImg.preserveAspect = false;
            }
        }

        Transform backTr = detailCardTr.Find("BackBtn");
        if (backTr != null)
        {
            RectTransform backRt = backTr.GetComponent<RectTransform>();
            if (backRt != null)
            {
                backRt.anchoredPosition = _detailBackAnchoredPosition;
                backRt.sizeDelta = _detailActionBtnSize;
            }
            if (_detailBackLabel != null)
            {
                _detailBackLabel.rectTransform.anchoredPosition = Vector2.zero;
                _detailBackLabel.rectTransform.sizeDelta = _detailActionBtnSize;
            }
            Transform backSelTr = backTr.Find("BackSelectedOutline");
            if (backSelTr != null)
            {
                RectTransform backSelRt = backSelTr.GetComponent<RectTransform>();
                if (backSelRt != null)
                {
                    backSelRt.anchorMin = Vector2.zero;
                    backSelRt.anchorMax = Vector2.one;
                    backSelRt.offsetMin = new Vector2(-46f, -26f);
                    backSelRt.offsetMax = new Vector2(46f, 26f);
                }
                Image backSelImg = backSelTr.GetComponent<Image>();
                if (backSelImg != null) backSelImg.preserveAspect = false;
            }
        }
    }

    private void BuildUI(bool bindRuntimeListeners = true)
    {
        TMP_FontAsset sharedFont = FindFirstObjectByType<TextMeshProUGUI>()?.font;

        if (_badgeRoot == null)
        {
            Transform existingBadge = transform.Find("DifficultyBadgeButton") ?? FindTransformInSceneByName("DifficultyBadgeButton");
            if (existingBadge != null) _badgeRoot = existingBadge.gameObject;
        }

        if (_badgeRoot == null)
        {
            _badgeRoot = new GameObject("DifficultyBadgeButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(Canvas), typeof(GraphicRaycaster));
            _badgeRoot.transform.SetParent(transform, false);
            Canvas badgeCanvas = _badgeRoot.GetComponent<Canvas>();
            badgeCanvas.overrideSorting = true;
            badgeCanvas.sortingOrder = _badgeSortingOrder;

            RectTransform badgeRt = _badgeRoot.GetComponent<RectTransform>();
            badgeRt.anchorMin = new Vector2(1f, 1f);
            badgeRt.anchorMax = new Vector2(1f, 1f);
            badgeRt.pivot = new Vector2(1f, 1f);
            badgeRt.anchoredPosition = _badgeAnchoredPosition;
            badgeRt.sizeDelta = _badgeSize;

            Image badgeBg = _badgeRoot.GetComponent<Image>();
            badgeBg.color = new Color(0.08f, 0.05f, 0.22f, 0.92f);

            var badgeTextGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            badgeTextGo.transform.SetParent(_badgeRoot.transform, false);
            RectTransform labelRt = badgeTextGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(14f, 6f);
            labelRt.offsetMax = new Vector2(-14f, -6f);

            _badgeLabel = badgeTextGo.GetComponent<TextMeshProUGUI>();
            if (sharedFont != null) _badgeLabel.font = sharedFont;
            _badgeLabel.fontSize = _badgeFontSize;
            _badgeLabel.alignment = TextAlignmentOptions.Center;
            _badgeLabel.color = Color.white;
            _badgeLabel.raycastTarget = false;
        }
        else if (_badgeLabel == null)
        {
            _badgeLabel = _badgeRoot.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        if (bindRuntimeListeners)
        {
            Button badgeBtn = _badgeRoot.GetComponent<Button>();
            if (badgeBtn == null) badgeBtn = _badgeRoot.AddComponent<Button>();
            badgeBtn.onClick.RemoveAllListeners();
            badgeBtn.onClick.AddListener(() => OpenPanel());
        }

        if (_modalRoot == null)
        {
            Transform existingModal = transform.Find("DifficultyModalPanel") ?? FindTransformInSceneByName("DifficultyModalPanel");
            if (existingModal != null) _modalRoot = existingModal.gameObject;
        }

        if (_modalRoot == null)
        {
            _modalRoot = new GameObject("DifficultyModalPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Canvas), typeof(GraphicRaycaster));
            _modalRoot.transform.SetParent(transform, false);
            Canvas modalCanvas = _modalRoot.GetComponent<Canvas>();
            modalCanvas.overrideSorting = true;
            modalCanvas.sortingOrder = _modalSortingOrder;

            RectTransform modalRt = _modalRoot.GetComponent<RectTransform>();
            modalRt.anchorMin = Vector2.zero;
            modalRt.anchorMax = Vector2.one;
            modalRt.offsetMin = Vector2.zero;
            modalRt.offsetMax = Vector2.zero;

            Image modalBg = _modalRoot.GetComponent<Image>();
            modalBg.color = new Color(0.03f, 0.02f, 0.10f, 0.97f);
        }
        else
        {
            Image modalBg = _modalRoot.GetComponent<Image>();
            if (modalBg != null && modalBg.color.a < 0.97f)
            {
                Color c = modalBg.color;
                c.a = 0.97f;
                modalBg.color = c;
            }

            // 旧バージョンの不要な上部タイトルラベル・下部モード表示テキストがあれば非表示にする
            Transform oldTitleTr = _modalRoot.transform.Find("Text");
            if (oldTitleTr != null) oldTitleTr.gameObject.SetActive(false);
            Transform oldStateTr = _modalRoot.transform.Find("TutorialStateText");
            if (oldStateTr != null) oldStateTr.gameObject.SetActive(false);
        }

        // 1) 上段中央：TutorialToggleBtn
        Transform tutTr = _modalRoot.transform.Find("TutorialToggleBtn");
        GameObject tutBtnGo;
        if (tutTr == null)
        {
            tutBtnGo = new GameObject("TutorialToggleBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Button));
            tutBtnGo.transform.SetParent(_modalRoot.transform, false);
            RectTransform tutRt = tutBtnGo.GetComponent<RectTransform>();
            tutRt.anchoredPosition = _tutorialBtnAnchoredPosition;
            tutRt.sizeDelta = _tutorialBtnSize;

            var tutSelGo = new GameObject("TutorialSelectedOutline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            tutSelGo.transform.SetParent(tutBtnGo.transform, false);
            RectTransform tutSelRt = tutSelGo.GetComponent<RectTransform>();
            tutSelRt.anchorMin = Vector2.zero;
            tutSelRt.anchorMax = Vector2.one;
            tutSelRt.offsetMin = new Vector2(-16f, -16f);
            tutSelRt.offsetMax = new Vector2(16f, 16f);

            _tutorialSelectionOutline = tutSelGo.GetComponent<Image>();
            _tutorialSelectionOutline.sprite = LoadResourceSprite("Difficulty/selected_tutorial_UI");
            _tutorialSelectionOutline.preserveAspect = true;
            _tutorialSelectionOutline.raycastTarget = false;

            var tutArtGo = new GameObject("CardVisual", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            tutArtGo.transform.SetParent(tutBtnGo.transform, false);
            RectTransform tutArtRt = tutArtGo.GetComponent<RectTransform>();
            tutArtRt.anchorMin = Vector2.zero;
            tutArtRt.anchorMax = Vector2.one;
            tutArtRt.offsetMin = Vector2.zero;
            tutArtRt.offsetMax = Vector2.zero;

            Image tutImg = tutArtGo.GetComponent<Image>();
            tutImg.sprite = LoadResourceSprite("Difficulty/Tutorial_UI");
            tutImg.preserveAspect = true;
            tutImg.raycastTarget = true;

            Button tutBtn = tutBtnGo.GetComponent<Button>();
            tutBtn.targetGraphic = tutImg;
        }
        else
        {
            tutBtnGo = tutTr.gameObject;
            if (_tutorialSelectionOutline == null)
            {
                Transform tutSelTr = tutTr.Find("TutorialSelectedOutline");
                if (tutSelTr != null) _tutorialSelectionOutline = tutSelTr.GetComponent<Image>();
            }
            EnsureCardVisualAboveOutline(tutBtnGo, "Difficulty/Tutorial_UI", _tutorialSelectionOutline);
        }

        if (bindRuntimeListeners)
        {
            Button tutBtn = tutBtnGo.GetComponent<Button>();
            if (tutBtn == null) tutBtn = tutBtnGo.AddComponent<Button>();
            tutBtn.onClick.RemoveAllListeners();
            tutBtn.onClick.AddListener(() =>
            {
                if (Time.frameCount > _openedFrame + 1)
                    OpenTutorialDetailPopup();
            });
        }

        // 2) 下段横並び：Card_Easy (左), Card_Normal (中), Card_Hard (右)
        string[] cardSprites = { "Difficulty/Easy_UI", "Difficulty/Normal_UI", "Difficulty/Hard_UI" };
        if (_cardSelectionOutlines == null || _cardSelectionOutlines.Length != 3)
            _cardSelectionOutlines = new Image[3];

        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            string cardName = $"Card_{(GameDifficulty.Level)i}";
            Transform cardTr = _modalRoot.transform.Find(cardName);
            GameObject cardGo;
            if (cardTr == null)
            {
                float cardX = (_cardAnchoredX != null && i < _cardAnchoredX.Length) ? _cardAnchoredX[i] : (i - 1) * 460f;
                cardGo = new GameObject(cardName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Button));
                cardGo.transform.SetParent(_modalRoot.transform, false);
                RectTransform rt = cardGo.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(cardX, _cardAnchoredY);
                rt.sizeDelta = _cardSize;

                var selGo = new GameObject("SelectedOutline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                selGo.transform.SetParent(cardGo.transform, false);
                RectTransform selRt = selGo.GetComponent<RectTransform>();
                selRt.anchorMin = Vector2.zero;
                selRt.anchorMax = Vector2.one;
                selRt.offsetMin = new Vector2(-16f, -16f);
                selRt.offsetMax = new Vector2(16f, 16f);

                Image selImg = selGo.GetComponent<Image>();
                selImg.sprite = LoadResourceSprite("Difficulty/selected_UI");
                selImg.preserveAspect = true;
                selImg.raycastTarget = false;
                _cardSelectionOutlines[i] = selImg;

                var artGo = new GameObject("CardVisual", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                artGo.transform.SetParent(cardGo.transform, false);
                RectTransform artRt = artGo.GetComponent<RectTransform>();
                artRt.anchorMin = Vector2.zero;
                artRt.anchorMax = Vector2.one;
                artRt.offsetMin = Vector2.zero;
                artRt.offsetMax = Vector2.zero;

                Image img = artGo.GetComponent<Image>();
                img.sprite = LoadResourceSprite(cardSprites[i]);
                img.preserveAspect = true;
                img.raycastTarget = true;

                Button btn = cardGo.GetComponent<Button>();
                btn.targetGraphic = img;
            }
            else
            {
                cardGo = cardTr.gameObject;
                if (_cardSelectionOutlines[i] == null)
                {
                    Transform selTr = cardTr.Find("SelectedOutline");
                    if (selTr != null) _cardSelectionOutlines[i] = selTr.GetComponent<Image>();
                }
                EnsureCardVisualAboveOutline(cardGo, cardSprites[i], _cardSelectionOutlines[i]);
            }

            if (bindRuntimeListeners)
            {
                Button btn = cardGo.GetComponent<Button>();
                if (btn == null) btn = cardGo.AddComponent<Button>();
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() =>
                {
                    if (Time.frameCount > _openedFrame + 1)
                        OpenDetailPopup((GameDifficulty.Level)idx);
                });
            }
        }

        if (_tutorialStateText != null)
        {
            _tutorialStateText.gameObject.SetActive(false);
        }

        Transform closeTr = _modalRoot.transform.Find("CloseModalBtn");
        GameObject closeBtnGo;
        if (closeTr == null)
        {
            closeBtnGo = new GameObject("CloseModalBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            closeBtnGo.transform.SetParent(_modalRoot.transform, false);
            RectTransform closeRt = closeBtnGo.GetComponent<RectTransform>();
            closeRt.anchoredPosition = _closeBtnAnchoredPosition;
            closeRt.sizeDelta = _closeBtnSize;
            closeBtnGo.GetComponent<Image>().color = new Color(0.25f, 0.2f, 0.45f, 1f);
            _closeModalBtnLabel = CreateLabel(closeBtnGo.transform, sharedFont, "BACK", Vector2.zero, _closeBtnSize, _closeBtnFontSize, Color.white);
        }
        else
        {
            closeBtnGo = closeTr.gameObject;
            if (_closeModalBtnLabel == null)
                _closeModalBtnLabel = closeBtnGo.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        Transform closeSelTr = closeBtnGo.transform.Find("CloseSelectedOutline");
        if (closeSelTr == null)
        {
            var closeSelGo = new GameObject("CloseSelectedOutline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            closeSelGo.transform.SetParent(closeBtnGo.transform, false);
            RectTransform closeSelRt = closeSelGo.GetComponent<RectTransform>();
            closeSelRt.anchorMin = Vector2.zero;
            closeSelRt.anchorMax = Vector2.one;
            closeSelRt.offsetMin = new Vector2(-46f, -18f);
            closeSelRt.offsetMax = new Vector2(46f, 18f);
            _closeModalSelectedOutline = closeSelGo.GetComponent<Image>();
            _closeModalSelectedOutline.sprite = LoadResourceSprite("Difficulty/ui_difficulty_selected");
            _closeModalSelectedOutline.preserveAspect = false;
            _closeModalSelectedOutline.raycastTarget = false;
            _closeModalSelectedOutline.gameObject.SetActive(false);
        }
        else if (_closeModalSelectedOutline == null)
        {
            _closeModalSelectedOutline = closeSelTr.GetComponent<Image>();
        }

        if (bindRuntimeListeners)
        {
            Button closeBtn = closeBtnGo.GetComponent<Button>();
            if (closeBtn == null) closeBtn = closeBtnGo.AddComponent<Button>();
            closeBtn.onClick.RemoveAllListeners();
            closeBtn.onClick.AddListener(() =>
            {
                _cursorIndex = 4;
                ClosePanel();
            });
        }

        if (_detailPopupRoot == null)
        {
            Transform detailTr = _modalRoot.transform.Find("DifficultyDetailPopup") ?? FindTransformInSceneByName("DifficultyDetailPopup");
            if (detailTr != null) _detailPopupRoot = detailTr.gameObject;
        }

        if (_detailPopupRoot == null)
        {
            _detailPopupRoot = new GameObject("DifficultyDetailPopup", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _detailPopupRoot.transform.SetParent(_modalRoot.transform, false);
            RectTransform detailRootRt = _detailPopupRoot.GetComponent<RectTransform>();
            detailRootRt.anchorMin = Vector2.zero;
            detailRootRt.anchorMax = Vector2.one;
            detailRootRt.offsetMin = Vector2.zero;
            detailRootRt.offsetMax = Vector2.zero;
            _detailPopupRoot.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.82f);

            var detailCardGo = new GameObject("DetailCard", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            detailCardGo.transform.SetParent(_detailPopupRoot.transform, false);
            RectTransform detailCardRt = detailCardGo.GetComponent<RectTransform>();
            detailCardRt.anchoredPosition = _detailCardAnchoredPosition;
            detailCardRt.sizeDelta = _detailCardSize;
            _detailCardImage = detailCardGo.GetComponent<Image>();
            _detailCardImage.preserveAspect = true;

            _detailDescText = CreateLabel(detailCardGo.transform, sharedFont, "", _detailDescAnchoredPosition, _detailDescSize, _detailDescFontSize, Color.white);
            _detailDescText.gameObject.name = "DetailDescText";

            var okBtnGo = new GameObject("ConfirmBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            okBtnGo.transform.SetParent(detailCardGo.transform, false);
            RectTransform okRt = okBtnGo.GetComponent<RectTransform>();
            okRt.anchoredPosition = _detailOkAnchoredPosition;
            okRt.sizeDelta = _detailActionBtnSize;
            okBtnGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);
            _detailOkLabel = CreateLabel(okBtnGo.transform, sharedFont, "OK", Vector2.zero, _detailActionBtnSize, _detailActionFontSize, new Color(0.05f, 0.02f, 0.15f, 1f));

            var okSelGo = new GameObject("OkSelectedOutline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            okSelGo.transform.SetParent(okBtnGo.transform, false);
            RectTransform okSelRt = okSelGo.GetComponent<RectTransform>();
            okSelRt.anchorMin = Vector2.zero;
            okSelRt.anchorMax = Vector2.one;
            okSelRt.offsetMin = new Vector2(-46f, -26f);
            okSelRt.offsetMax = new Vector2(46f, 26f);
            _detailOkSelectedOutline = okSelGo.GetComponent<Image>();
            _detailOkSelectedOutline.sprite = LoadResourceSprite("Difficulty/ui_difficulty_selected");
            _detailOkSelectedOutline.preserveAspect = false;
            _detailOkSelectedOutline.raycastTarget = false;

            var backBtnGo = new GameObject("BackBtn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            backBtnGo.transform.SetParent(detailCardGo.transform, false);
            RectTransform backRt = backBtnGo.GetComponent<RectTransform>();
            backRt.anchoredPosition = _detailBackAnchoredPosition;
            backRt.sizeDelta = _detailActionBtnSize;
            backBtnGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);
            _detailBackLabel = CreateLabel(backBtnGo.transform, sharedFont, "BACK", Vector2.zero, _detailActionBtnSize, _detailActionFontSize, new Color(0.05f, 0.02f, 0.15f, 1f));

            var backSelGo = new GameObject("BackSelectedOutline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            backSelGo.transform.SetParent(backBtnGo.transform, false);
            RectTransform backSelRt = backSelGo.GetComponent<RectTransform>();
            backSelRt.anchorMin = Vector2.zero;
            backSelRt.anchorMax = Vector2.one;
            backSelRt.offsetMin = new Vector2(-46f, -26f);
            backSelRt.offsetMax = new Vector2(46f, 26f);
            _detailBackSelectedOutline = backSelGo.GetComponent<Image>();
            _detailBackSelectedOutline.sprite = LoadResourceSprite("Difficulty/ui_difficulty_selected");
            _detailBackSelectedOutline.preserveAspect = false;
            _detailBackSelectedOutline.raycastTarget = false;
        }
        else
        {
            Transform detailCardTr = _detailPopupRoot.transform.Find("DetailCard");
            if (detailCardTr != null)
            {
                if (_detailCardImage == null) _detailCardImage = detailCardTr.GetComponent<Image>();
                if (_detailDescText == null)
                {
                    Transform descTr = detailCardTr.Find("DetailDescText") ?? detailCardTr.Find("Text");
                    if (descTr != null) _detailDescText = descTr.GetComponent<TextMeshProUGUI>();
                }
                if (_detailDescText != null)
                {
                    RectTransform descRt = _detailDescText.rectTransform;
                    // 旧デフォルト値 (150, 40) / (680, 220) のまま未調整のオブジェクトであれば新レイアウトへ自動移行する
                    if (Vector2.Distance(descRt.anchoredPosition, new Vector2(150f, 40f)) < 1f &&
                        Vector2.Distance(descRt.sizeDelta, new Vector2(680f, 220f)) < 1f)
                    {
                        descRt.anchoredPosition = new Vector2(148f, 15f);
                        descRt.sizeDelta = new Vector2(580f, 220f);
                    }
                }
                Transform okTr = detailCardTr.Find("ConfirmBtn");
                if (okTr != null)
                {
                    RectTransform okRt = okTr.GetComponent<RectTransform>();
                    if (okRt != null &&
                        Vector2.Distance(okRt.anchoredPosition, new Vector2(-16f, -152f)) < 1f &&
                        Vector2.Distance(okRt.sizeDelta, new Vector2(250f, 84f)) < 1f)
                    {
                        okRt.anchoredPosition = new Vector2(-15f, -182f);
                        okRt.sizeDelta = new Vector2(250f, 95f);
                    }
                    if (_detailOkLabel == null) _detailOkLabel = okTr.GetComponentInChildren<TextMeshProUGUI>(true);
                    Transform okSelTr = okTr.Find("OkSelectedOutline");
                    if (okSelTr != null)
                    {
                        if (_detailOkSelectedOutline == null) _detailOkSelectedOutline = okSelTr.GetComponent<Image>();
                        RectTransform okSelRt = okSelTr.GetComponent<RectTransform>();
                        if (okSelRt != null && Vector2.Distance(okSelRt.offsetMin, new Vector2(-12f, -12f)) < 1f)
                        {
                            okSelRt.offsetMin = new Vector2(-46f, -26f);
                            okSelRt.offsetMax = new Vector2(46f, 26f);
                            if (_detailOkSelectedOutline != null) _detailOkSelectedOutline.preserveAspect = false;
                        }
                    }
                }
                Transform backTr = detailCardTr.Find("BackBtn");
                if (backTr != null)
                {
                    RectTransform backRt = backTr.GetComponent<RectTransform>();
                    if (backRt != null &&
                        Vector2.Distance(backRt.anchoredPosition, new Vector2(316f, -152f)) < 1f &&
                        Vector2.Distance(backRt.sizeDelta, new Vector2(250f, 84f)) < 1f)
                    {
                        backRt.anchoredPosition = new Vector2(310f, -182f);
                        backRt.sizeDelta = new Vector2(250f, 95f);
                    }
                    if (_detailBackLabel == null) _detailBackLabel = backTr.GetComponentInChildren<TextMeshProUGUI>(true);
                    Transform backSelTr = backTr.Find("BackSelectedOutline");
                    if (backSelTr != null)
                    {
                        if (_detailBackSelectedOutline == null) _detailBackSelectedOutline = backSelTr.GetComponent<Image>();
                        RectTransform backSelRt = backSelTr.GetComponent<RectTransform>();
                        if (backSelRt != null && Vector2.Distance(backSelRt.offsetMin, new Vector2(-12f, -12f)) < 1f)
                        {
                            backSelRt.offsetMin = new Vector2(-46f, -26f);
                            backSelRt.offsetMax = new Vector2(46f, 26f);
                            if (_detailBackSelectedOutline != null) _detailBackSelectedOutline.preserveAspect = false;
                        }
                    }
                }
            }
        }

        if (bindRuntimeListeners && _detailPopupRoot != null)
        {
            Transform detailCardTr = _detailPopupRoot.transform.Find("DetailCard");
            if (detailCardTr != null)
            {
                Transform okTr = detailCardTr.Find("ConfirmBtn");
                if (okTr != null)
                {
                    Button okBtn = okTr.GetComponent<Button>();
                    if (okBtn == null) okBtn = okTr.gameObject.AddComponent<Button>();
                    okBtn.onClick.RemoveAllListeners();
                    okBtn.onClick.AddListener(() =>
                    {
                        _detailCursorIndex = 0;
                        ConfirmDetailSelection();
                    });
                }
                Transform backTr = detailCardTr.Find("BackBtn");
                if (backTr != null)
                {
                    Button backBtn = backTr.GetComponent<Button>();
                    if (backBtn == null) backBtn = backTr.gameObject.AddComponent<Button>();
                    backBtn.onClick.RemoveAllListeners();
                    backBtn.onClick.AddListener(OnDetailBackButtonClicked);
                }
            }
        }

        // 実行時（Awake等）はエディタで表示ONのまま保存されていても必ず非表示で初期化する
        if (Application.isPlaying)
        {
            if (_detailPopupRoot != null) _detailPopupRoot.SetActive(false);
            if (_modalRoot != null) _modalRoot.SetActive(false);
        }
    }

    /// <summary>
    /// SelectedOutline（selected_UI / selected_tutorial_UI）は塗りつぶし画像のため、
    /// 親オブジェクト自身の Image よりも手前（子）にあるとカード本体を覆い隠してしまう。
    /// SelectedOutline を背面（FirstSibling）に置き、カード本体（CardVisual）を前面（LastSibling）に配置して外枠だけが見えるようにする。
    /// </summary>
    private static void EnsureCardVisualAboveOutline(GameObject cardGo, string spriteResourcePath, Image outlineImg)
    {
        if (cardGo == null) return;

        if (outlineImg != null)
        {
            outlineImg.transform.SetAsFirstSibling();
        }

        Transform visualTr = cardGo.transform.Find("CardVisual");
        Image rootImg = cardGo.GetComponent<Image>();

        if (visualTr == null)
        {
            Sprite sp = (rootImg != null && rootImg.sprite != null) ? rootImg.sprite : LoadResourceSprite(spriteResourcePath);
            var artGo = new GameObject("CardVisual", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            artGo.transform.SetParent(cardGo.transform, false);
            RectTransform artRt = artGo.GetComponent<RectTransform>();
            artRt.anchorMin = Vector2.zero;
            artRt.anchorMax = Vector2.one;
            artRt.offsetMin = Vector2.zero;
            artRt.offsetMax = Vector2.zero;

            Image artImg = artGo.GetComponent<Image>();
            artImg.sprite = sp;
            artImg.preserveAspect = true;
            artImg.raycastTarget = true;

            if (rootImg != null)
            {
                rootImg.enabled = false;
            }

            Button btn = cardGo.GetComponent<Button>();
            if (btn != null) btn.targetGraphic = artImg;
            artGo.transform.SetAsLastSibling();
        }
        else
        {
            if (rootImg != null) rootImg.enabled = false;
            visualTr.SetAsLastSibling();
        }
    }

    private static TextMeshProUGUI CreateLabel(Transform parent, TMP_FontAsset font, string text, Vector2 anchoredPos, Vector2 size, float fontSize, Color color)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        TextMeshProUGUI tmp = go.GetComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        return tmp;
    }

    private void RefreshVisuals()
    {
        GameDifficulty.Level lv = GameDifficulty.Current;
        int minutes = Mathf.RoundToInt(GameDifficulty.GetTimeLimitSeconds(lv) / 60f);
        bool isTutMode = GameDifficulty.IncludeTutorial;

        if (_badgeLabel != null)
        {
            _badgeLabel.text = isTutMode
                ? "MODE: TUTORIAL\n[Tab / Y]"
                : $"MODE: {GameDifficulty.GetDisplayName(lv)} ({minutes}m)\n[Tab / Y]";
        }

        bool isCloseBtnCursor = _isOpen && _cursorIndex == 4;
        if (_closeModalBtnLabel != null)
        {
            _closeModalBtnLabel.text = "BACK";
            _closeModalBtnLabel.color = isCloseBtnCursor ? new Color(1f, 0.96f, 0.55f, 1f) : Color.white;
            Transform closeBtnTr = _closeModalBtnLabel.transform.parent;
            if (closeBtnTr != null)
            {
                Image closeBtnBg = closeBtnTr.GetComponent<Image>();
                if (closeBtnBg != null)
                {
                    closeBtnBg.color = isCloseBtnCursor
                        ? new Color(0.45f, 0.35f, 0.78f, 1f)
                        : new Color(0.25f, 0.2f, 0.45f, 1f);
                }
            }
        }

        if (_closeModalSelectedOutline != null)
        {
            _closeModalSelectedOutline.gameObject.SetActive(isCloseBtnCursor);
        }

        for (int i = 0; i < _cardSelectionOutlines.Length; i++)
        {
            if (_cardSelectionOutlines[i] == null) continue;
            _cardSelectionOutlines[i].transform.SetAsFirstSibling();
            bool isCurrentMode = !isTutMode && ((int)lv == i);
            bool isCursor = _isOpen && _cursorIndex == i;
            _cardSelectionOutlines[i].gameObject.SetActive(isCurrentMode || isCursor);
            _cardSelectionOutlines[i].color = isCursor
                ? Color.white
                : new Color(1f, 1f, 1f, 0.55f);
        }

        if (_tutorialSelectionOutline != null)
        {
            _tutorialSelectionOutline.transform.SetAsFirstSibling();
            bool isCursor = _isOpen && _cursorIndex == 3;
            _tutorialSelectionOutline.gameObject.SetActive(isTutMode || isCursor);
            _tutorialSelectionOutline.color = isCursor
                ? Color.white
                : new Color(1f, 1f, 1f, 0.6f);
        }

        if (_tutorialStateText != null)
        {
            _tutorialStateText.gameObject.SetActive(false);
        }
    }
}
