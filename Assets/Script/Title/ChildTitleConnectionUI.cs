using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// ChildTitleConnectionUI（子機タイトル専用）：
/// 子機タイトル（ChildeTitle）の接続UIを ChildUdpReceiver から分離したコンポーネント。
///
/// 責務の境界：
///   本クラス           — タイトルUIの参照保持・表示更新・パネル開閉・タイトル用リスナー登録
///   ChildUdpReceiver   — ソケット・受信スレッド・接続状態／PING・タイムアウト・Discovery・
///                        START_GAME・プレイID・シーン遷移・ランキング同期（挙動は従来のまま）
///
///  ・このコンポーネントはタイトルシーンにのみ置く（DontDestroyOnLoad にしない）。
///  ・接続先は ChildUdpReceiver.instance（DDOLの生存個体）を基準にする。
///    シーンの重複UDP個体は Awake で自壊するため掴まない（Inspector参照も持たない）。
///  ・Inspector参照はすべて本コンポーネントが保持し、通常動作で名前検索は行わない。
///  ・Connected→START の判断や START_GAME 送信は UDP 側の既存API（OnConnectButtonClicked）が持つ。
/// </summary>
public class ChildTitleConnectionUI : MonoBehaviour
{
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
    private ChildUdpReceiver _receiver;

    // 本コンポーネントが動的に追加したリスナー（OnDisable/OnDestroy で自分のものだけ外す）
    private readonly List<(Button button, UnityAction action)> _dynamicListeners =
        new List<(Button button, UnityAction action)>();

    // 最後にUIへ反映した接続状態（同値の再適用を避ける）
    private ChildUdpReceiver.ConnectionState? _lastAppliedState;

    private void Awake()
    {
        // シーンで置いた接続ボタンの位置を、未接続時の位置として覚える（表示更新より先に行う）。
        CaptureSceneLayoutPosition();

        // Inspector参照だけを使い、初期状態（パネルを閉じる）へ整える。名前検索はしない。
        ApplyInitialPanelState();
    }

    private void OnEnable()
    {
        // 有効化し直したときも再接続・再購読・再登録できるようにする。
        TryBindReceiver();
        RegisterTitleListeners();
        ApplyCurrentStateIfNeeded();
    }

    private void Start()
    {
        // Awake/OnEnable の時点では ChildUdpReceiver.instance が未設定のことがある
        // （同一シーン内の Awake 実行順は不定）。Start でも必ず試す。
        TryBindReceiver();
        RegisterTitleListeners();
        ApplyCurrentStateIfNeeded();
    }

    private void Update()
    {
        // 接続済みなら何もしない（毎フレームの検索は行わない）。
        // 未接続の間だけ、static フィールドを見て再接続を試す。
        if (_receiver != null) return;

        TryBindReceiver();
        ApplyCurrentStateIfNeeded();
    }

    private void OnDisable()
    {
        UnbindReceiver();
        RemoveDynamicListeners();
    }

    private void OnDestroy()
    {
        UnbindReceiver();
        RemoveDynamicListeners();
    }

    /// <summary>
    /// 接続ボタン（の親）の現在位置＝シーンで置いた位置を、Disconnected/Connecting 時の位置として採用する。
    /// （Inspector に残った古い座標で、再生時にボタンが動いてしまうのを防ぐ）
    /// </summary>
    private void CaptureSceneLayoutPosition()
    {
        if (!useSceneLayoutAsDefaultPosition || connectButton == null) return;

        Transform moveTarget = connectButton.transform.parent != null
            ? connectButton.transform.parent
            : connectButton.transform;

        if (moveTarget is RectTransform rect)
            connectButtonDefaultPosition = rect.anchoredPosition;
    }

    // ── UDPへの接続（生存個体＝DDOLを基準にする）──────────────────────────────

    /// <summary>
    /// 生存UDP（ChildUdpReceiver.instance）へ接続し、状態通知を購読する。
    /// まだ存在しない場合は何もしない（Start/Update/操作時に再試行する）。
    /// </summary>
    private void TryBindReceiver()
    {
        if (_receiver != null) return;

        ChildUdpReceiver alive = ChildUdpReceiver.instance;
        if (alive == null) return;

        _receiver = alive;
        _receiver.ConnectionStateChanged += HandleConnectionStateChanged;
        Debug.Log($"[ChildTitleConnectionUI] 生存UDPへ接続 '{alive.gameObject.name}' (id={alive.GetInstanceID()})", this);
    }

    private void UnbindReceiver()
    {
        if (_receiver != null)
            _receiver.ConnectionStateChanged -= HandleConnectionStateChanged;

        _receiver = null;
        _lastAppliedState = null;
    }

    /// <summary>操作直前に接続先を保証する（未接続なら解決を試みる）。</summary>
    private bool EnsureReceiver()
    {
        if (_receiver == null) TryBindReceiver();

        if (_receiver == null)
        {
            Debug.LogWarning("[ChildTitleConnectionUI] ChildUdpReceiver.instance が未生成のため操作を実行できません。", this);
            return false;
        }

        return true;
    }

    /// <summary>UDPからの状態通知（メインスレッドで発火される）。</summary>
    private void HandleConnectionStateChanged(ChildUdpReceiver.ConnectionState state)
    {
        ApplyConnectionState(state);
    }

    /// <summary>
    /// 初回同期：購読前に状態が変わっていても正しい表示になるよう、現在値を読み取って適用する。
    /// （ResetForNewSession より先に初期化が走った場合も、この同期で正しい表示に揃う）
    /// </summary>
    private void ApplyCurrentStateIfNeeded()
    {
        if (_receiver == null) return;
        ApplyConnectionState(_receiver.CurrentConnectionState);
    }


    // ── 表示更新（従来の ChildUdpReceiver.UpdateUi と同じ見た目）───────────────

    private void ApplyConnectionState(ChildUdpReceiver.ConnectionState state)
    {
        if (_lastAppliedState.HasValue && _lastAppliedState.Value == state) return; // 同値では再適用しない
        _lastAppliedState = state;

        if (connectButtonLabel != null)
        {
            switch (state)
            {
                case ChildUdpReceiver.ConnectionState.Disconnected:
                    connectButtonLabel.text = connectLabel;
                    break;
                case ChildUdpReceiver.ConnectionState.Connecting:
                    connectButtonLabel.text = connectingLabel;
                    break;
                case ChildUdpReceiver.ConnectionState.Connected:
                    connectButtonLabel.text = "START!";
                    break;
            }
        }

        if (connectButton != null)
        {
            connectButton.gameObject.SetActive(true);
            connectButton.interactable = state != ChildUdpReceiver.ConnectionState.Connecting;

            Transform moveTarget = connectButton.transform.parent != null
                ? connectButton.transform.parent
                : connectButton.transform;

            if (moveTarget is RectTransform rect)
            {
                rect.anchoredPosition = state == ChildUdpReceiver.ConnectionState.Connected
                    ? connectButtonStartPosition
                    : connectButtonDefaultPosition;
            }
        }

        if (cancelUiObject != null)
            cancelUiObject.SetActive(state == ChildUdpReceiver.ConnectionState.Connecting);
        else if (cancelButton != null)
            cancelButton.gameObject.SetActive(state == ChildUdpReceiver.ConnectionState.Connecting);

        SetActiveForButton(creditsButton, state != ChildUdpReceiver.ConnectionState.Connected);
        SetActiveForButton(settingsButton, state != ChildUdpReceiver.ConnectionState.Connected);

        // 単独開始ボタンは未接続／接続中だけ表示する。接続済み（START!）では通常の開始に任せ、
        // 親機と子機の開始がずれないよう隠す。ボタンの親は触らず、ボタン自身だけを切り替える。
        if (soloStartButton != null)
            ResolveToggleTarget(soloStartButton).SetActive(state != ChildUdpReceiver.ConnectionState.Connected);
    }

    /// <summary>タイトル表示時の初期状態：各パネルを閉じ、装飾を表示する。</summary>
    private void ApplyInitialPanelState()
    {
        if (creditsPanel != null) creditsPanel.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (cancelUiObject != null) cancelUiObject.SetActive(false);

        // 旧実装（ChildUdpReceiver.RefreshUiReferences）と同じく、タイトル画像と装飾は
        // クリックを遮らないようにする（ボタン操作を阻害しない既存挙動の維持）。
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

    /// <summary>
    /// ボタンの表示切替の対象を返す。ボタンが装飾コンテナ（Connect/Credit など）の子ならコンテナごと、
    /// 親が Canvas だったり他のボタンだったりする場合は、巻き込まないようボタン自身だけを返す。
    /// </summary>
    private static GameObject ResolveToggleTarget(Button button)
    {
        Transform parent = button.transform.parent;
        if (parent != null && parent.GetComponent<Canvas>() == null && parent.GetComponent<Selectable>() == null)
            return parent.gameObject;

        return button.gameObject;
    }

    /// <summary>クリックを遮らないようにする（旧 ChildUdpReceiver.RefreshUiReferences と同じ処理）。</summary>
    private static void DisableRaycastTargets(GameObject target)
    {
        if (target == null) return;

        foreach (Graphic graphic in target.GetComponentsInChildren<Graphic>(true))
            graphic.raycastTarget = false;
    }


    // ── ボタン操作（通信・開始はUDP側の既存APIを1回だけ呼ぶ）─────────────────

    /// <summary>
    /// 接続ボタン。Connected なら START、でなければ接続開始、という分岐は UDP 側
    /// （ChildUdpReceiver.OnConnectButtonClicked）が持つ。ここでは重複実装しない。
    /// </summary>
    public void OnConnectClicked()
    {
        if (!EnsureReceiver()) return;
        _receiver.OnConnectButtonClicked();
    }

    /// <summary>Cancelボタン。接続状態を戻す処理は UDP 側が持つ。</summary>
    public void OnCancelClicked()
    {
        if (!EnsureReceiver()) return;
        _receiver.OnCancelButtonClicked();
    }

    /// <summary>子機だけで開始。START_GAME を送らずにゲームへ進む処理は UDP 側が持つ。</summary>
    public void OnSoloStartClicked()
    {
        if (!EnsureReceiver()) return;
        _receiver.OnSoloStartButtonClicked();
    }

    /// <summary>クレジットの開閉（タイトルUIのみの処理）。</summary>
    public void OnCreditsClicked()
    {
        if (creditsPanel == null)
        {
            Debug.LogWarning("[ChildTitleConnectionUI] Credits パネルが未設定です。", this);
            return;
        }

        creditsPanel.SetActive(!creditsPanel.activeSelf);
        UpdateAnimatedSpritesVisibility();
    }

    public void OnCloseCreditsClicked()
    {
        if (creditsPanel == null) return;
        creditsPanel.SetActive(false);
        UpdateAnimatedSpritesVisibility();
    }

    /// <summary>設定の開閉（タイトルUIのみの処理）。</summary>
    public void OnSettingsClicked()
    {
        if (settingsPanel == null)
        {
            Debug.LogWarning("[ChildTitleConnectionUI] Settings パネルが未設定です。", this);
            return;
        }

        settingsPanel.SetActive(!settingsPanel.activeSelf);
        UpdateAnimatedSpritesVisibility();
    }

    public void OnCloseSettingsClicked()
    {
        if (settingsPanel == null) return;
        settingsPanel.SetActive(false);
        UpdateAnimatedSpritesVisibility();
    }

    // ── タイトル用リスナー（Inspectorの永続リスナーと二重登録しない）──────────

    /// <summary>
    /// タイトルのボタンに動的リスナーを登録する。
    ///  ・初回ロードではシーンの永続リスナーが本コンポーネントを指しているため登録しない
    ///    （同一処理の二重実行を防ぐ）。
    ///  ・タイトル復帰後は前回のUIインスタンスが破棄され、永続リスナーのターゲットが
    ///    別インスタンス（破棄済み）になるため、そのときだけ動的登録される。
    /// </summary>
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

    /// <summary>
    /// 動的リスナーを1つだけ登録する。
    ///  ・既に永続リスナーに「本コンポーネント＋同じメソッド」があれば何もしない。
    ///  ・他コンポーネントのリスナー（SE再生など）は削除しない（RemoveAllListenersは使わない）。
    /// </summary>
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

    /// <summary>本コンポーネントが追加した動的リスナーだけを外す。</summary>
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