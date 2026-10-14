using UnityEngine;

/// <summary>
/// ChildUdpBootstrap:
/// 子機UDPの「起動・設定」専用コンポーネント（R2で縮退）。
///
/// 残す責任：
///  ・既存のシリアライズ設定（ポート・接続先・シーン名・Inspector参照・診断出力）を保持し、
///    その**現在値**を Host（ChildUdpRuntimeHost）へ供給する設定アダプタ
///  ・同一GameObject上の Host の取得／追加、初期化、明示起動（リンクの初期値seedを含む）
///  ・ルートの一意性（重複個体は Host 生成・初期化・通信開始の前に自壊する）と DDOL
///  ・自分の終了接続（所有者登録の解除のみ。Hostの解放は Host 自身が行う）
///  ・起動失敗時の具体的エラー（旧通信へのフォールバックは行わない）
///
/// Hostが持つ責任（本クラスは委譲しない）：
///  ・通信（ソケット・受信ポンプ・Discovery／Heartbeat／再送）／公開API／通知／状態問い合わせ
///
/// 型名・ファイル名・スクリプトGUID・Inspector設定は R2 では変更しない（R3／R4で扱う）。
/// </summary>
public class ChildUdpBootstrap : MonoBehaviour
{
    public enum ConnectionState { Disconnected, Connecting, Connected }

    // ── Inspector（既存のシリアライズ設定。値・既定値・意味は変えない）────────
    [Header("通信")]
    public int normalPort = 8000;
    public int broadcastPort = 8001;
    public int parentReceivePort = 8002;
    public string targetIP = "127.0.0.1";

    [Header("起動時設定（初期接続状態）")]
    public ConnectionState currentState = ConnectionState.Disconnected;

    [Header("遷移先")]
    public string gameSceneName = "ChildLoading";
    public string titleSceneName = "ChildTitle";

    [Header("Debug")]
    [Tooltip("通信ログなどの詳細出力を有効にする")]
    [SerializeField] private bool showDebugLogs = true;

    // ── Host（実行責任）／所有者管理（ルートの一意性）──────────────────────────
    private ChildUdpRuntimeHost _host;
    private readonly ChildUdpRuntimeHostSettings _settingsAdapter = new ChildUdpRuntimeHostSettings();

    /// <summary>生存個体（ルートの一意性のための所有者）。重複個体は Awake で自壊し、ここへ来ない。</summary>
    private static ChildUdpBootstrap _owner;

    /// <summary>
    /// 設定アダプタ：旧シリアライズ値の**現在値**を、単一の設定オブジェクトへ**in-place更新**して返す。
    ///  ・provider 呼び出しごとに new しない（値の複製もしない＝同じインスタンスを更新して返す）。
    ///  ・通信・保存・通知などの副作用は持たない（読み取りのみ）。
    /// </summary>
    private ChildUdpRuntimeHostSettings BuildSettings()
    {
        _settingsAdapter.NormalPort = normalPort;
        _settingsAdapter.BroadcastPort = broadcastPort;
        _settingsAdapter.ParentReceivePort = parentReceivePort;
        _settingsAdapter.TargetIP = targetIP;
        _settingsAdapter.GameSceneName = gameSceneName;
        _settingsAdapter.TitleSceneName = titleSceneName;
        _settingsAdapter.ShowDebugLogs = showDebugLogs;
        _settingsAdapter.PingInterval = 1.0f;   // 旧側の private 既定値と同じ
        _settingsAdapter.TimeoutLimit = 3.0f;   // 旧側の private 既定値と同じ
        return _settingsAdapter;
    }

    // ── Unity lifecycle（生存窓口の管理とHost接続・起動だけを行う）──────────────
    private void Awake()
    {
        // 1) 重複判定を最初に行う。重複個体は Host 生成・初期化・通信開始の前に戻る。
        if (_owner != null && _owner != this)
        {
            Debug.Log("[ChildUdpBootstrap] Duplicate detected — destroying self.");
            Destroy(gameObject);
            return;
        }
        _owner = this;
        DontDestroyOnLoad(gameObject);   // 窓口とHostを含む同一ルートで一経路にする

        // 2) 生存窓口だけが、同一GameObject上で Host を取得または追加する。
        _host = GetComponent<ChildUdpRuntimeHost>();
        if (_host == null) _host = gameObject.AddComponent<ChildUdpRuntimeHost>();

        // 3) 設定参照（Inspector値の現在値）と診断出力の現在値をHostへ接続する。
        _host.BindSettingsProvider(BuildSettings);
        _host.BindShowDebugProvider(() => showDebugLogs);

        // 4) 初期化（担当生成・設定接続・購読先準備まで。通信は開始しない）。
        if (!_host.Initialize(BuildSettings(), out string initError))
        {
            Debug.LogError($"[ChildUdpBootstrap] Host の初期化に失敗しました: {initError}（旧通信へのフォールバックは行いません）");
            return;
        }
    }

    private void Start()
    {
        if (_host == null)
        {
            Debug.LogError("[ChildUdpBootstrap] Host が存在しないため通信を開始しません（旧実装へのフォールバックは行いません）。");
            return;
        }

        // 旧 Awake の位置に対応：Inspector の接続先・接続状態をリンクの初期値として渡す（イベントは出さない）。
        ChildConnectionLink link = _host.Link;
        if (link != null)
        {
            link.SeedTargetIP(targetIP);
            link.SeedState(MapToLinkState(currentState));
        }

        // 旧 Start 相当の時点で明示起動する（HostのAwake／OnEnable／Startからは開始しない）。
        if (!_host.TryStartRuntime(out string startError))
            Debug.LogError($"[ChildUdpBootstrap] Host の通信開始に失敗しました: {startError}（旧通信へのフォールバックは行いません）");
    }

    private void OnDestroy()
    {
        // 所有者登録の解除は本人条件に限定する（重複個体の破棄で生存個体の管理を壊さない）。
        //  Host の購読・Coroutine・チャネル・キューの解放は Host 自身が行う。
        if (_owner == this) _owner = null;
    }

    private static ChildConnectionLink.LinkState MapToLinkState(ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connecting: return ChildConnectionLink.LinkState.Connecting;
            case ConnectionState.Connected:  return ChildConnectionLink.LinkState.Connected;
            default:                         return ChildConnectionLink.LinkState.Disconnected;
        }
    }
}
