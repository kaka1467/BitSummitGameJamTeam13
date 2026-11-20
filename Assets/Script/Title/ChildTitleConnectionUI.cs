using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// ChildTitleConnectionUI（子機タイトル専用）：
/// 子機タイトル（ChildTitle）の接続UIを ChildUdpReceiver から分離したコンポーネント。
/// </summary>
public class ChildTitleConnectionUI : MonoBehaviour
{
    private sealed class CancelClickForwarder : MonoBehaviour, IPointerClickHandler
    {
        public ChildTitleConnectionUI Owner;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (Owner == null) return;
            Owner.HandleCancelPointerClick();
        }
    }

    [Header("接続UI")] [Tooltip("接続／STARTボタン（Connect TMP の親）。")] [SerializeField]
    private Button connectButton;

    [Tooltip("接続ボタンのラベル（Connect TMP）。")] [SerializeField]
    private TextMeshProUGUI connectButtonLabel;

    [Tooltip("ONなら、シーン上に置いた接続ボタン（の親）の位置を Disconnected/Connecting 時の位置として使う。" +
             "下の Default Position は無視される。シーンで並べたレイアウトが再生時に動いてしまうのを防ぐ。")] [SerializeField]
    private bool useSceneLayoutAsDefaultPosition = true;

    [Tooltip("Disconnected/Connecting状態のときの接続ボタン位置。useSceneLayoutAsDefaultPosition が OFF のときだけ使う。")] [SerializeField]
    private Vector2 connectButtonDefaultPosition = Vector2.zero;

    [Tooltip("Connected（START!）状態のときの接続ボタン位置。")] [SerializeField]
    private Vector2 connectButtonStartPosition = Vector2.zero;

    [Tooltip("Connecting中だけ表示するCancelの装飾を含む親オブジェクト。")] [SerializeField]
    private GameObject cancelUiObject;

    [Tooltip("Cancelボタン（cancelUiObject が未設定のときの表示切替に使う）。")] [SerializeField]
    private Button cancelButton;
    [Tooltip("Cancelボタンのラベル（シーン上のTMP）。実行時に常に 'Cancel'へ揃える。")] [SerializeField]
    private TextMeshProUGUI cancelButtonLabel;

    [Header("子機だけで開始（親機なしの単独プレイ）")]
    [Tooltip("親機に接続せず、子機だけでゲームを始めるボタン。押すと START_GAME は送らずにそのままゲームへ進む。" +
             "Connected（START!）のあいだは自動で隠す。クリック処理はこのコンポーネントが自動で登録するので、OnClick の設定は不要。")]
    [SerializeField]
    private Button soloStartButton;

    [Header("ラベル文言")] [Tooltip("未接続時の接続ボタン文言。")] [SerializeField]
    private string connectLabel = "Connect";

    [Tooltip("接続中の接続ボタン文言。")] [SerializeField]
    private string connectingLabel = "接続中";

    [Header("クレジット／設定")] [Tooltip("クレジットパネル。")] [SerializeField]
    private GameObject creditsPanel;

    [Tooltip("クレジットを開くボタン。")] [SerializeField]
    private Button creditsButton;

    [Tooltip("クレジットパネル内の閉じるボタン（非表示パネル内でも参照を保持する）。")] [SerializeField]
    private Button creditsCloseButton;

    [Tooltip("設定パネル。")] [SerializeField] private GameObject settingsPanel;

    [Tooltip("設定を開くボタン。")] [SerializeField]
    private Button settingsButton;

    [Tooltip("設定パネル内の閉じるボタン（非表示パネル内でも参照を保持する）。")] [SerializeField]
    private Button settingsCloseButton;

    [Header("タイトル装飾")] [Tooltip("クレジット／設定パネルが開いている間は隠すタイトル画像。")] [SerializeField]
    private GameObject titleImageObject;

    [Tooltip("クレジット／設定パネルが開いている間は隠す装飾（BackStar／Character など）。")] [SerializeField]
    private GameObject[] animatedSpriteObjects;

    // ── 接続先（実行時に解決する。Inspectorでは指定しない）────────────────────
    private ChildUdpRuntimeHost _host;
    private MenuNavigationController _menuNavigation;
    private TitleMenuHighlight _menuHighlight;

    // 本コンポーネントが動的に追加したリスナー（OnDisable/OnDestroy で自分のものだけ外す）
    private readonly List<(Button button, UnityAction action)> _dynamicListeners =
        new List<(Button button, UnityAction action)>();

    // 最後にUIへ反映した接続状態（同値の再適用を避ける）
    private ChildConnectionLink.LinkState? _lastAppliedState;
    private bool _isWaitingForGameStart;
    // EventSystem の Submit と MenuNavigationController の Enter/Space 判定が同一フレームで二重発火し、
    // Cancel 直後に ConnectButton を即座に再押下してしまう競合を防ぐフレームガード。
    private int _lastCancelFrame = -10;
    private int _lastConnectFrame = -10;

    private void Awake()
    {
        _isWaitingForGameStart = false;
        CaptureSceneLayoutPosition();
        EnsureCancelButtonClickable();
        ApplyInitialPanelState();
    }

    private void OnEnable()
    {
        TryBindHost();
        RegisterTitleListeners();
        EnsureCancelButtonClickable();
        ApplyCurrentStateIfNeeded();
    }

    private void Start()
    {
        TryBindHost();
        RegisterTitleListeners();
        EnsureCancelButtonClickable();
        ApplyCurrentStateIfNeeded();
    }

    private void Update()
    {
        if (_host == null)
        {
            TryBindHost();
            ApplyCurrentStateIfNeeded();
        }

        if (_host != null &&
            !_isWaitingForGameStart &&
            !DifficultyTitleUI.IsAnyModalOpen &&
            (_host.ConnectionState == ChildConnectionLink.LinkState.Connecting ||
             _host.ConnectionState == ChildConnectionLink.LinkState.Connected) &&
            Keyboard.current != null &&
            Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            OnCancelClicked();
        }
    }

    private void OnDisable()
    {
        UnbindHost();
        RemoveDynamicListeners();
    }

    private void OnDestroy()
    {
        UnbindHost();
        RemoveDynamicListeners();
    }

    private void CaptureSceneLayoutPosition()
    {
        if (!useSceneLayoutAsDefaultPosition || connectButton == null) return;

        Transform moveTarget = connectButton.transform.parent != null
            ? connectButton.transform.parent
            : connectButton.transform;

        if (moveTarget is RectTransform rect)
            connectButtonDefaultPosition = rect.anchoredPosition;
    }

    private void TryBindHost()
    {
        if (_host != null) return;

        ChildUdpRuntimeHost host = ResolveHost();
        if (host == null) return;

        _host = host;
        _host.ConnectionStateChanged += HandleConnectionStateChanged;
        Debug.Log($"[ChildTitleConnectionUI] 子機Hostへ接続 '{host.gameObject.name}' (id={host.GetInstanceID()})", this);
    }

    private static ChildUdpRuntimeHost ResolveHost()
    {
        ChildUdpRuntimeHost host = ChildUdpRuntimeHost.Running;
        if (host != null) return host;

        ChildUdpRuntimeHost found = FindFirstObjectByType<ChildUdpRuntimeHost>();
        return (found != null && found.IsConfigured) ? found : null;
    }

    private void UnbindHost()
    {
        if (_host != null)
            _host.ConnectionStateChanged -= HandleConnectionStateChanged;

        _host = null;
        _lastAppliedState = null;
    }

    private bool EnsureHost()
    {
        if (_host == null) TryBindHost();

        if (_host == null)
        {
            Debug.LogWarning("[ChildTitleConnectionUI] 子機Hostが未稼働のため操作を実行できません。", this);
            return false;
        }

        return true;
    }

    private void HandleConnectionStateChanged(ChildConnectionLink.LinkState state)
    {
        if (state != ChildConnectionLink.LinkState.Connected)
        {
            _isWaitingForGameStart = false;
        }
        ApplyConnectionState(state, force: true);
    }

    private void ApplyCurrentStateIfNeeded()
    {
        if (_host == null) return;
        ApplyConnectionState(_host.ConnectionState);
    }

    private void ApplyConnectionState(ChildConnectionLink.LinkState state, bool force = false)
    {
        if (!force && _lastAppliedState.HasValue && _lastAppliedState.Value == state) return;
        _lastAppliedState = state;

        if (connectButtonLabel != null)
        {
            switch (state)
            {
                case ChildConnectionLink.LinkState.Disconnected:
                    connectButtonLabel.text = connectLabel;
                    break;
                case ChildConnectionLink.LinkState.Connecting:
                    connectButtonLabel.text = string.IsNullOrEmpty(connectingLabel) || connectingLabel == "接続中"
                        ? "Connecting..."
                        : connectingLabel;
                    break;
                case ChildConnectionLink.LinkState.Connected:
                    connectButtonLabel.text = _isWaitingForGameStart ? "Waiting..." : "START!";
                    break;
            }

            connectButtonLabel.ForceMeshUpdate();
        }

        if (connectButton != null)
        {
            connectButton.gameObject.SetActive(true);
            // Connecting / Connected 遷移直後に MenuNavigationController や EventSystem が
            // connectButton を選択不可とみなして Cancel へ飛ばないよう interactable を維持する
            // （Connecting 中の押下は OnConnectClicked 側で無視する）
            connectButton.interactable = !_isWaitingForGameStart;

            Transform moveTarget = connectButton.transform.parent != null
                ? connectButton.transform.parent
                : connectButton.transform;

            // Keep the initial vertical menu layout position even when Connected
            // so START! does not jump into CREDIT / SOLO START and break the layout.
            if (moveTarget is RectTransform rect)
            {
                rect.anchoredPosition = connectButtonDefaultPosition;
            }
        }

        bool isConnectingOrConnected = state == ChildConnectionLink.LinkState.Connecting ||
                                       state == ChildConnectionLink.LinkState.Connected;

        // 子機が Connecting または Connected 状態のときは、開いているクレジット／設定パネルも閉じて
        // START!（接続中は待機表示）と Cancel ボタンの2つだけを表示する。
        if (isConnectingOrConnected)
        {
            if (creditsPanel != null && creditsPanel.activeSelf)
            {
                creditsPanel.SetActive(false);
                GameDifficulty.SendTitleCommandToParent("CLOSE_CREDITS");
            }
            if (settingsPanel != null && settingsPanel.activeSelf)
            {
                settingsPanel.SetActive(false);
                GameDifficulty.SendTitleCommandToParent("CLOSE_SETTINGS");
            }
        }

        if (cancelUiObject != null)
            cancelUiObject.SetActive(isConnectingOrConnected);
        else if (cancelButton != null)
            cancelButton.gameObject.SetActive(isConnectingOrConnected);

        if (cancelButton != null)
            cancelButton.interactable = isConnectingOrConnected && !_isWaitingForGameStart;

        if (_menuNavigation == null)
            _menuNavigation = FindFirstObjectByType<MenuNavigationController>();
        if (_menuNavigation != null)
        {
            if (isConnectingOrConnected && cancelButton != null)
                _menuNavigation.EnsureSelectableButton(cancelButton);
            else
                _menuNavigation.RemoveSelectableButton(cancelButton);
        }

        if (cancelButton != null)
        {
            cancelButton.gameObject.SetActive(isConnectingOrConnected);
            EnsureCancelButtonClickable();
        }

        if (cancelButtonLabel != null)
        {
            cancelButtonLabel.text = "Cancel";
            cancelButtonLabel.ForceMeshUpdate();
        }
        else if (cancelButton != null)
        {
            TextMeshProUGUI foundLabel = cancelButton.GetComponentInChildren<TextMeshProUGUI>(true);
            if (foundLabel != null)
            {
                foundLabel.text = "Cancel";
                foundLabel.ForceMeshUpdate();
            }
        }

        // Connecting / Connected のときは SOLO START, CREDIT, SETTING をすべて非表示にし、
        // Disconnected に戻ったときだけ元のメニューボタン（CONNECT, SOLO START, CREDIT, SETTING）を再表示する。
        SetActiveForButton(creditsButton, !isConnectingOrConnected);
        SetActiveForButton(settingsButton, !isConnectingOrConnected);

        UpdateAnimatedSpritesVisibility();

        // 接続状態（Connecting / Connected / Disconnected）に切り替わった直後は、
        // 必ず connectButton（Connecting... / START! / Connect）をデフォルトの選択先に設定する
        SelectConnectButtonAsDefault();
    }

    private void SelectConnectButtonAsDefault()
    {
        if (connectButton == null || !connectButton.gameObject.activeInHierarchy) return;

        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(connectButton.gameObject);
        }

        if (_menuNavigation == null)
        {
            _menuNavigation = FindFirstObjectByType<MenuNavigationController>();
        }
        if (_menuNavigation != null)
        {
            _menuNavigation.SelectButton(connectButton);
        }

        if (_menuHighlight == null)
        {
            _menuHighlight = FindFirstObjectByType<TitleMenuHighlight>();
        }
        if (_menuHighlight != null)
        {
            _menuHighlight.SetArrowOnRightEdge(true);
            _menuHighlight.SetSelectedSelectable(connectButton, snap: true);
        }
    }

    private void UpdateSoloStartVisibility()
    {
        if (soloStartButton == null) return;

        bool isConnectingOrConnected = _lastAppliedState.HasValue &&
                                       (_lastAppliedState.Value == ChildConnectionLink.LinkState.Connecting ||
                                        _lastAppliedState.Value == ChildConnectionLink.LinkState.Connected);
        bool panelOpen = (creditsPanel != null && creditsPanel.activeSelf) ||
                         (settingsPanel != null && settingsPanel.activeSelf);

        ResolveToggleTarget(soloStartButton).SetActive(!isConnectingOrConnected && !panelOpen);
    }

    private void ApplyInitialPanelState()
    {
        if (creditsPanel != null) creditsPanel.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (cancelUiObject != null) cancelUiObject.SetActive(false);

        DisableRaycastTargets(titleImageObject);
        if (animatedSpriteObjects != null)
        {
            foreach (GameObject go in animatedSpriteObjects)
                DisableRaycastTargets(go);
        }

        UpdateAnimatedSpritesVisibility();
    }

    private void UpdateAnimatedSpritesVisibility()
    {
        bool shouldShow = !(creditsPanel != null && creditsPanel.activeSelf) &&
                          !(settingsPanel != null && settingsPanel.activeSelf);

        if (titleImageObject != null)
            titleImageObject.SetActive(shouldShow);

        UpdateSoloStartVisibility();

        if (animatedSpriteObjects == null) return;

        foreach (GameObject go in animatedSpriteObjects)
        {
            if (go != null) go.SetActive(shouldShow);
        }
    }

    private static void SetActiveForButton(Button button, bool active)
    {
        if (button == null) return;

        GameObject target = button.transform.parent != null
            ? button.transform.parent.gameObject
            : button.gameObject;

        target.SetActive(active);
    }

    private static GameObject ResolveToggleTarget(Button button)
    {
        Transform parent = button.transform.parent;
        if (parent != null && parent.GetComponent<Canvas>() == null && parent.GetComponent<Selectable>() == null)
            return parent.gameObject;

        return button.gameObject;
    }

    private static void DisableRaycastTargets(GameObject target)
    {
        if (target == null) return;

        foreach (Graphic graphic in target.GetComponentsInChildren<Graphic>(true))
            graphic.raycastTarget = false;
    }

    public void OnConnectClicked()
    {
        if (DifficultyTitleUI.IsAnyModalOpen) return;
        if (Time.frameCount <= _lastCancelFrame + 1) return;
        if (!EnsureHost()) return;
        if (_isWaitingForGameStart) return;

        // Connecting 中に決定が押された場合は何もしない（選択位置は connectButton に維持）
        if (_host.ConnectionState == ChildConnectionLink.LinkState.Connecting)
        {
            return;
        }

        _lastConnectFrame = Time.frameCount;

        // 接続完了後（START! ボタン押下時）も難易度選択を確認してからゲームを開始できるようにする
        if (_host.ConnectionState == ChildConnectionLink.LinkState.Connected)
        {
            DifficultyTitleUI diffUi = DifficultyTitleUI.EnsureAttached();
            if (diffUi != null)
            {
                diffUi.OpenForGameStart(() =>
                {
                    if (EnsureHost())
                    {
                        _isWaitingForGameStart = true;
                        ApplyConnectionState(_host.ConnectionState, force: true);
                        _host.OnConnectButtonClicked();
                    }
                });
                return;
            }

            _isWaitingForGameStart = true;
            ApplyConnectionState(_host.ConnectionState, force: true);
        }

        _host.OnConnectButtonClicked();
    }

    private void EnsureCancelButtonClickable()
    {
        if (cancelButton != null)
        {
            Image img = cancelButton.GetComponent<Image>();
            if (img == null) img = cancelButton.gameObject.AddComponent<Image>();
            img.enabled = true;
            img.color = new Color(1f, 1f, 1f, 0f);
            img.raycastTarget = true;
            cancelButton.targetGraphic = img;

            foreach (Graphic graphic in cancelButton.GetComponentsInChildren<Graphic>(true))
            {
                graphic.raycastTarget = true;
            }
        }

        if (cancelUiObject != null && (cancelButton == null || cancelUiObject != cancelButton.gameObject))
        {
            Graphic parentGraphic = cancelUiObject.GetComponent<Graphic>();
            if (parentGraphic != null) parentGraphic.raycastTarget = true;

            CancelClickForwarder forwarder = cancelUiObject.GetComponent<CancelClickForwarder>();
            if (forwarder == null) forwarder = cancelUiObject.AddComponent<CancelClickForwarder>();
            forwarder.Owner = this;
        }
    }

    private void HandleCancelPointerClick()
    {
        if (_isWaitingForGameStart) return;
        if (cancelButton != null && (!cancelButton.gameObject.activeInHierarchy || !cancelButton.interactable)) return;
        OnCancelClicked();
    }

    public void OnCancelClicked()
    {
        if (Time.frameCount == _lastConnectFrame) return;
        _lastCancelFrame = Time.frameCount;
        _isWaitingForGameStart = false;
        if (EnsureHost())
        {
            _host.CancelConnect();
            ApplyConnectionState(ChildConnectionLink.LinkState.Disconnected, force: true);
            return;
        }

        ApplyConnectionState(ChildConnectionLink.LinkState.Disconnected, force: true);
    }

    public void OnSoloStartClicked()
    {
        if (DifficultyTitleUI.IsAnyModalOpen) return;
        if (!EnsureHost()) return;

        // 子機ソロスタート押下時に難易度選択画面を開き、難易度決定後にソロスタートを開始する
        DifficultyTitleUI diffUi = DifficultyTitleUI.EnsureAttached();
        if (diffUi != null)
        {
            diffUi.OpenForGameStart(() =>
            {
                if (EnsureHost())
                    _host.OnSoloStartButtonClicked();
            });
            return;
        }

        _host.OnSoloStartButtonClicked();
    }

    /// <summary>クレジットの開閉（タイトルUIのみの処理＋親機へ同期 M-F04）。</summary>
    public void OnCreditsClicked()
    {
        if (creditsPanel == null)
        {
            Debug.LogWarning("[ChildTitleConnectionUI] Credits パネルが未設定です。", this);
            return;
        }

        bool nextActive = !creditsPanel.activeSelf;
        creditsPanel.SetActive(nextActive);
        UpdateAnimatedSpritesVisibility();
        GameDifficulty.SendTitleCommandToParent(nextActive ? "OPEN_CREDITS" : "CLOSE_CREDITS");
    }

    public void OnCloseCreditsClicked()
    {
        if (creditsPanel == null) return;
        creditsPanel.SetActive(false);
        UpdateAnimatedSpritesVisibility();
        GameDifficulty.SendTitleCommandToParent("CLOSE_CREDITS");
    }

    /// <summary>設定の開閉（タイトルUIのみの処理＋親機へ同期 M-F04）。</summary>
    public void OnSettingsClicked()
    {
        if (settingsPanel == null)
        {
            Debug.LogWarning("[ChildTitleConnectionUI] Settings パネルが未設定です。", this);
            return;
        }

        bool nextActive = !settingsPanel.activeSelf;
        settingsPanel.SetActive(nextActive);
        UpdateAnimatedSpritesVisibility();
        GameDifficulty.SendTitleCommandToParent(nextActive ? "OPEN_SETTINGS" : "CLOSE_SETTINGS");
    }

    public void OnCloseSettingsClicked()
    {
        if (settingsPanel == null) return;
        settingsPanel.SetActive(false);
        UpdateAnimatedSpritesVisibility();
        GameDifficulty.SendTitleCommandToParent("CLOSE_SETTINGS");
    }

    private void RegisterTitleListeners()
    {
        BindButton(connectButton, OnConnectClicked, nameof(OnConnectClicked));
        BindButton(cancelButton, OnCancelClicked, nameof(OnCancelClicked));
        BindButton(soloStartButton, OnSoloStartClicked, nameof(OnSoloStartClicked));
        BindButton(creditsButton, OnCreditsClicked, nameof(OnCreditsClicked));
        BindButton(settingsButton, OnSettingsClicked, nameof(OnSettingsClicked));
        BindButton(creditsCloseButton, OnCloseCreditsClicked, nameof(OnCloseCreditsClicked));
        BindButton(settingsCloseButton, OnCloseSettingsClicked, nameof(OnCloseSettingsClicked));
    }

    private void BindButton(Button button, UnityAction action, string methodName)
    {
        if (button == null) return;

        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
        {
            if (button.onClick.GetPersistentTarget(i) == this &&
                button.onClick.GetPersistentMethodName(i) == methodName)
            {
                return;
            }
        }

        if (_dynamicListeners.Exists(pair => pair.button == button && pair.action == action)) return;

        button.onClick.AddListener(action);
        _dynamicListeners.Add((button, action));
    }

    private void RemoveDynamicListeners()
    {
        for (int i = 0; i < _dynamicListeners.Count; i++)
        {
            (Button button, UnityAction action) = _dynamicListeners[i];
            if (button != null) button.onClick.RemoveListener(action);
        }

        _dynamicListeners.Clear();
    }
}
