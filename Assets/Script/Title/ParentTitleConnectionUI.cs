using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// ParentTitleConnectionUI（親機タイトル専用）：
/// 親機タイトル（ParentTitle）の接続UIを ParentUdpSender から分離したコンポーネント。
///
/// 責務の境界：
///   本クラス            — タイトルUIの参照保持・表示更新・ボタン登録（タイトル専用）
///   ParentUdpSender     — ソケット・受信スレッド・Discovery／PING・タイムアウト・接続状態・
///                         START_GAME・単独開始・プレイID・シーン遷移・ランキング同期（挙動は従来のまま）
///
///  ・このコンポーネントはタイトルシーンにのみ置く（DontDestroyOnLoad にしない）。
///  ・接続先は稼働中の ParentUdpRuntimeHost（DDOLの生存個体）を基準にする。
///    シーンの重複UDP個体は Awake で自壊するため掴まない（Inspector参照も持たない）。
///  ・Connection Panel の子にはしない（TitleMenuHighlight.FlashAndDeactivate が
///    押されたボタン自身を非表示にするため、UI処理と初期化が失われない配置にする）。
///  ・Connect／Cancel／SoloStart は既存の UDP API を1回だけ呼ぶ（開始判断はUDP側が持つ）。
/// </summary>
public class ParentTitleConnectionUI : MonoBehaviour
{
    [Header("接続UI")] [Tooltip("Connect Button（Connect／Connecting...／STARTING... を表示するボタン）。")] [SerializeField]
    private Button connectButton;

    [Tooltip("接続ボタンのラベル（Connect Button の子 Text (TMP)）。")] [SerializeField]
    private TextMeshProUGUI connectButtonLabel;

    [Tooltip("Connecting中だけ表示する cancel button。")] [SerializeField]
    private Button cancelButton;

    [Header("単独開始")]
    [Tooltip("子機の接続・START_GAME受信を待たずに親機だけで開始する SoloStartButton。\n" +
             "押下時の非表示は既存の TitleMenuHighlight.FlashAndDeactivate が行う。")]
    [SerializeField]
    private GameObject soloStartButtonObject;

    // ── 接続先（実行時に解決する。Inspectorでは指定しない）────────────────────
    private ParentUdpRuntimeHost _host;

    // 本コンポーネントが動的に追加したリスナー（OnDisable/OnDestroy で自分のものだけ外す）
    private readonly List<(Button button, UnityAction action)> _dynamicListeners =
        new List<(Button button, UnityAction action)>();

    // 最後にUIへ反映した接続状態（同値の再適用を避ける）
    private ParentConnectionLink.LinkState? _lastAppliedState;

    // 診断用：直前にラベルへ書き込んだ文字列（変化時のみログを出す）
    private string _lastAppliedConnectLabel;

    private void OnEnable()
    {
        // 有効化し直したときも再接続・再購読・再登録できるようにする。
        TryBindHost();
        RegisterTitleListeners();
        ApplyCurrentStateIfNeeded();
    }

    private void Start()
    {
        // Awake/OnEnable の時点では ParentUdpSender.Instance が未設定のことがある
        // （同一シーン内の Awake 実行順は不定）。Start でも必ず試す。
        TryBindHost();
        RegisterTitleListeners();
        ApplyCurrentStateIfNeeded();
    }

    private void Update()
    {
        // 接続済みなら何もしない（毎フレームの検索は行わない）。
        // 未接続の間だけ、static フィールドを見て再接続を試す。
        if (_host != null) return;

        TryBindHost();
        ApplyCurrentStateIfNeeded();
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

    // ── UDPへの接続（生存個体＝DDOLを基準にする）──────────────────────────────

    /// <summary>
    /// 稼働中の親機Hostへ接続し、状態通知を購読する。
    /// まだ取得できない場合は何もしない（Start/Update/操作時に再試行する）。
    /// </summary>
    private void TryBindHost()
    {
        if (_host != null) return;

        ParentUdpRuntimeHost host = ResolveHost();
        if (host == null) return;

        _host = host;
        _host.ConnectionStateChanged += HandleConnectionStateChanged;
        Debug.Log($"[ParentTitleConnectionUI] 親機Hostへ接続 '{host.gameObject.name}' (id={host.GetInstanceID()})", this);
    }

    /// <summary>稼働中のHostを優先し、無ければ初期化済みHostを探す（未初期化Hostは購読先にしない）。</summary>
    private static ParentUdpRuntimeHost ResolveHost()
    {
        ParentUdpRuntimeHost host = ParentUdpRuntimeHost.Running;
        if (host != null) return host;

        ParentUdpRuntimeHost found = FindFirstObjectByType<ParentUdpRuntimeHost>();
        return (found != null && found.IsConfigured) ? found : null;
    }

    /// <summary>購読した同じ個体から解除する。</summary>
    private void UnbindHost()
    {
        if (_host != null)
            _host.ConnectionStateChanged -= HandleConnectionStateChanged;

        _host = null;
        _lastAppliedState = null;
        _lastAppliedConnectLabel = null;
    }

    /// <summary>操作直前に接続先を保証する（未接続なら解決を試みる）。</summary>
    private bool EnsureHost()
    {
        if (_host == null) TryBindHost();

        if (_host == null)
        {
            Debug.LogWarning("[ParentTitleConnectionUI] 親機Hostが未稼働のため操作を実行できません。", this);
            return false;
        }

        return true;
    }

    /// <summary>UDPからの状態通知（メインスレッドで発火される）。</summary>
    private void HandleConnectionStateChanged(ParentConnectionLink.LinkState state)
    {
        ApplyConnectionState(state);
    }

    /// <summary>
    /// 初回同期：購読前に状態が変わっていても正しい表示になるよう、現在値を読み取って適用する。
    /// （ResetForNewSession より先に初期化が走った場合も、この同期で正しい表示に揃う）
    /// </summary>
    private void ApplyCurrentStateIfNeeded()
    {
        if (_host == null) return;
        ApplyConnectionState(_host.ConnectionState);
    }

    // ── 表示更新（従来の ParentUdpSender.UpdateUI と同じ見た目）───────────────

    private void ApplyConnectionState(ParentConnectionLink.LinkState state)
    {
        if (_lastAppliedState.HasValue && _lastAppliedState.Value == state) return; // 同値では再適用しない
        _lastAppliedState = state;

        if (connectButtonLabel != null)
        {
            string newLabel;
            switch (state)
            {
                case ParentConnectionLink.LinkState.Disconnected:
                    newLabel = "Connect";
                    break;
                case ParentConnectionLink.LinkState.Connecting:
                    newLabel = "Connecting...";
                    break;
                case ParentConnectionLink.LinkState.Connected:
                    newLabel = "STARTING...";
                    break;
                default:
                    newLabel = connectButtonLabel.text;
                    break;
            }

            // 旧実装（ParentUdpSender.UpdateUI）と同じ診断ログ。表示文字が変わったときだけ出す。
            if (newLabel != _lastAppliedConnectLabel)
            {
                bool gameStarted = _host != null && _host.IsGameStarted;
                Debug.Log(
                    $"[ParentTitleConnectionUI][Diag] connectButtonLabel: '{_lastAppliedConnectLabel}' → '{newLabel}' " +
                    $"| state={state} | scene='{SceneManager.GetActiveScene().name}' " +
                    $"| soloStartButtonObject={(soloStartButtonObject != null ? soloStartButtonObject.activeSelf.ToString() : "null")} " +
                    $"| _gameStarted={gameStarted}");
                _lastAppliedConnectLabel = newLabel;
            }

            connectButtonLabel.text = newLabel;
        }

        if (connectButton != null) connectButton.gameObject.SetActive(true);
        if (cancelButton != null)
            cancelButton.gameObject.SetActive(state == ParentConnectionLink.LinkState.Connecting);
    }


    // ── ボタン操作（接続・開始はUDP側の既存APIを1回だけ呼ぶ）─────────────────

    /// <summary>Connect Button。接続状態の遷移は UDP 側（OnConnectButtonClicked）が持つ。</summary>
    public void OnConnectClicked()
    {
        if (!EnsureHost()) return;
        _host.OnConnectButtonClicked();
    }

    /// <summary>cancel button。接続状態を戻す処理は UDP 側が持つ。</summary>
    public void OnCancelClicked()
    {
        if (!EnsureHost()) return;
        _host.OnCancelButtonClicked();
    }

    /// <summary>
    /// SoloStartButton。子機の応答を待たずに単独開始する処理は UDP 側
    /// （OnSoloStartButtonClicked）が持つ（ParentLoading を経由しない既存仕様のまま）。
    /// </summary>
    public void OnSoloStartClicked()
    {
        if (!EnsureHost()) return;
        _host.OnSoloStartButtonClicked();
    }

    // ── タイトル用リスナー（Inspectorの永続リスナーと二重登録しない）──────────

    /// <summary>
    /// タイトルのボタンに動的リスナーを登録する。
    ///  ・シーンの永続リスナーが本コンポーネントを指している間は登録しない
    ///    （Inspector登録＋動的登録の二重実行を防ぐ）。
    ///  ・タイトル復帰後は新シーンのボタンが新UIを指すため、その場合も登録は不要
    ///    （永続リスナーが新しいUIインスタンスを解決して呼ぶ）。
    /// </summary>
    private void RegisterTitleListeners()
    {
        BindButton(connectButton, OnConnectClicked, nameof(OnConnectClicked));
        BindButton(cancelButton, OnCancelClicked, nameof(OnCancelClicked));

        if (soloStartButtonObject == null) return;
        Button soloButton = soloStartButtonObject.GetComponent<Button>();
        BindButton(soloButton, OnSoloStartClicked, nameof(OnSoloStartClicked));
    }

    /// <summary>
    /// 動的リスナーを1つだけ登録する。
    ///  ・既に永続リスナーに「本コンポーネント＋同じメソッド」があれば何もしない。
    ///  ・他コンポーネントのリスナー（FlashAndDeactivate／SE再生など）は削除しない
    ///    （RemoveAllListenersは使わない）。
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