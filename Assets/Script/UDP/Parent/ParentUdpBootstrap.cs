using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentUdpBootstrap:
/// 親機UDPの「起動・設定」専用コンポーネント（R2で縮退）。
///
/// 残す責任：
///  ・既存のシリアライズ設定（ポート・接続先・シーン名・キー設定・診断出力）を保持し、
///    その**現在値**を Host（ParentUdpRuntimeHost）へ供給する設定アダプタ
///  ・同一GameObject上の Host の取得／追加、初期化、明示起動
///  ・ルートの一意性（重複個体は Host 生成・初期化・通信開始の前に自壊する）と DDOL
///  ・自分の終了接続（所有者登録の解除のみ。Hostの解放は Host 自身が行う）
///  ・起動失敗時の具体的エラー（旧通信へのフォールバックは行わない）
///
/// Hostが持つ責任（本クラスは委譲しない）：
///  ・通信（ソケット・受信ポンプ・周期・境界処理）／公開API／通知／状態問い合わせ／enum変換
///
/// 型名・ファイル名・スクリプトGUID・Inspector設定は R2 では変更しない（R3／R4で扱う）。
/// </summary>
public class ParentUdpBootstrap : MonoBehaviour
{
    private const string ResultGameOverScene = "ParentGameOver";
    private const string ResultTimeUpScene   = "ParentGameClear";

    public enum ConnectionState { Disconnected, Connecting, Connected }

    // ── Inspector（既存のシリアライズ設定。値・既定値・意味は変えない）────────
    [Header("通信")]
    public int    normalPort       = 8000;
    public int    broadcastPort    = 8001;
    public int    parentReceivePort = 8002;
    public string targetIP         = "127.0.0.1";

    [Header("起動時設定（初期接続状態）")]
    public ConnectionState currentState = ConnectionState.Disconnected;

    [Header("遷移先（Solo Start を含む）")]
    public string             gameSceneName    = "ParentLoading";
    [Tooltip("Solo Start（子機接続なし）の遷移先シーン。ParentLoadingは子機を無期限に待つため経由せず、直接このシーンへ行く。")]
    public string             soloGameSceneName = "ParentGameScene";
    [Tooltip("実際にゲームプレイが行われるシーン名。LOUD_ITEM（ラッシュイン）の処理や MotherSuspicionSystem の参照検索はこのシーンでだけ行う。" +
             "gameSceneName は『ゲーム開始時に最初に読み込むシーン（ParentLoading）』で、プレイ中のシーンとは別物。")]
    public string             gameplaySceneName = "ParentGameScene";

    [Header("タイトル復帰（キーボード）")]
    [Tooltip("ゲーム中などに、キーを長押しして親機をタイトル画面へ戻す。タイトル画面では無効。")]
    public bool               enableReturnToTitleKey = true;
    public Key                returnToTitleKey = Key.Escape;
    [Tooltip("誤操作防止のため、このキーをこの秒数だけ押し続けるとタイトルへ戻る。")]
    public float              returnToTitleHoldSeconds = 1.5f;
    [Tooltip("戻る先の親機タイトルシーン名。")]
    public string             motherTitleSceneName = "ParentTitle";
    [Tooltip("子機のタイトルシーン名。親機のタイトル復帰では motherTitleSceneName を使う。この値は子機向けの判定にのみ使用する。")]
    public string             titleSceneName   = "ChildTitle";
    public string             gameOverSceneName = "ParentGameOver";
    public string             timeUpSceneName   = "ParentGameClear";

    [Header("Debug")]
    [Tooltip("通信ログなどの詳細出力を有効にする")]
    [SerializeField] private bool showDebugLogs = true;

    // ── Host（実行責任）／所有者管理（ルートの一意性）──────────────────────────
    private ParentUdpRuntimeHost _host;
    private readonly ParentUdpRuntimeHostSettings _settingsAdapter = new ParentUdpRuntimeHostSettings();
    private float _pingInterval = 1.0f;
    private float _timeoutLimit = 3.0f;

    /// <summary>生存個体（ルートの一意性のための所有者）。重複個体は Awake で自壊し、ここへ来ない。</summary>
    private static ParentUdpBootstrap _owner;

    /// <summary>
    /// 設定アダプタ：旧シリアライズ値の**現在値**を、単一の設定オブジェクトへ**in-place更新**して返す。
    ///  ・provider 呼び出しごとに new しない（値の複製もしない＝同じインスタンスを更新して返す）。
    ///  ・通信・保存・通知などの副作用は持たない（読み取りのみ）。
    ///  ・bind時点（起動時）と、その後の送信・判定時点の違いは、呼ばれた時点の値で維持される。
    /// </summary>
    private ParentUdpRuntimeHostSettings RefreshSettingsAdapter()
    {
        _settingsAdapter.NormalPort = normalPort;
        _settingsAdapter.BroadcastPort = broadcastPort;
        _settingsAdapter.ParentReceivePort = parentReceivePort;
        _settingsAdapter.TargetIP = targetIP;
        _settingsAdapter.TimeoutLimit = _timeoutLimit;
        _settingsAdapter.PingInterval = _pingInterval;
        _settingsAdapter.ShowDebugLogs = showDebugLogs;
        _settingsAdapter.GameSceneName = gameSceneName;
        _settingsAdapter.SoloGameSceneName = soloGameSceneName;
        _settingsAdapter.GameplaySceneName = gameplaySceneName;
        _settingsAdapter.MotherTitleSceneName = motherTitleSceneName;
        _settingsAdapter.TitleSceneName = titleSceneName;
        _settingsAdapter.GameOverSceneName = gameOverSceneName;
        _settingsAdapter.TimeUpSceneName = timeUpSceneName;
        _settingsAdapter.EnableReturnToTitleKey = enableReturnToTitleKey;
        _settingsAdapter.ReturnToTitleKey = returnToTitleKey;
        _settingsAdapter.ReturnToTitleHoldSeconds = returnToTitleHoldSeconds;
        return _settingsAdapter;
    }

    // ── Unity lifecycle（生存窓口の管理とHost接続・起動だけを行う）──────────────
    private void Awake()
    {
        // 1) 重複判定を最初に行う。重複個体は Host 生成・初期化・通信開始の前に戻る。
        if (_owner != null && _owner != this)
        {
            Debug.Log("[ParentUdpBootstrap] Duplicate detected — destroying self.");
            Destroy(gameObject);
            return;
        }
        _owner = this;
        DontDestroyOnLoad(gameObject);   // 窓口とHostを含む同一ルートで一経路にする

        // 2) 生存窓口だけが、同一GameObject上の Host を取得／追加する。
        _host = GetComponent<ParentUdpRuntimeHost>();
        if (_host == null) _host = gameObject.AddComponent<ParentUdpRuntimeHost>();

        // 3) 設定参照（使用時点のシリアライズ値）と診断出力を接続する。
        _host.BindSettingsProvider(RefreshSettingsAdapter);
        _host.BindShowDebugProvider(() => showDebugLogs);

        // 4) 担当（Link／Session／Hub／RankSync）の生成はここ（通信は開始しない）。
        if (!_host.Initialize(RefreshSettingsAdapter(), out string initError))
        {
            Debug.LogError($"[ParentUdpBootstrap] Host の初期化に失敗しました: {initError}。通信は開始しません（旧実装へのフォールバックは行いません）。");
            return;
        }

        // 5) Inspectorから読み込んだ接続先を、リンクへ先に渡す（リンクの既定値でInspector値を上書きしない）。
        if (_host.Link != null) _host.Link.SeedTargetIP(targetIP);
    }

    private void Start()
    {
        // Force result scenes to shared names across parent/child.
        gameOverSceneName = ResultGameOverScene;
        timeUpSceneName = ResultTimeUpScene;

        // ── 明示的な通信開始（旧Startに相当する時点。HostのStartからは重ねて起動しない）──
        if (_host == null)
        {
            Debug.LogError("[ParentUdpBootstrap] Host が存在しないため通信を開始しません（旧実装へのフォールバックは行いません）。");
            return;
        }

        // 接続先の初期値をリンクへ渡してから起動する（Inspector値を優先する）。
        if (_host.Link != null) _host.Link.SeedTargetIP(targetIP);

        if (!_host.TryStartRuntime(out string startError))
        {
            Debug.LogError($"[ParentUdpBootstrap] Host の通信開始に失敗しました: {startError}。通信は開始しません（旧実装へのフォールバックは行いません）。");
            return;
        }
    }

    private void OnDestroy()
    {
        // 所有者登録の解除は本人条件に限定する（重複個体の破棄で生存個体の管理を壊さない）。
        //  Host の購読・Coroutine・チャネル・キューの解放は Host 自身が行う。
        if (_owner == this) _owner = null;
    }
}
