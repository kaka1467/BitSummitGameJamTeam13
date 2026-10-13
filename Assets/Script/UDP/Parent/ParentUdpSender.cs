using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// ParentUdpSender:
/// Handles all UDP communication on the parent (mother) side.
///
/// Outbound messages (parent → child, port 8000):
///   TEAM13_START_GAME   — start the game on child
///   TEAM13_CAUGHT       — parent game-over; child must also end
///   TEAM13_SLEEP_LOCK   — parent sleeping; disable child input
///   TEAM13_SLEEP_UNLOCK — parent awake; re-enable child input
///   TEAM13_PING         — heartbeat
///   TEAM13_RETURN_TO_TITLE — return-to-title sync → child also returns
///   TEAM13_RANK_SYNC_ACK:<version>:<sourceId>:<updateNo> — parent applied the child's ranking snapshot
///
/// Inbound messages (child → parent, port 8002):
///   TEAM13_PING              — heartbeat from child
///   TEAM13_START_GAME        — child requests game start
///   TEAM13_TIME_UP           — child timer expired → parent game over
///   TEAM13_CHILD_DEAD        — child died → parent game over
///   TEAM13_RANK_SYNC:<version>:<sourceId>:<updateNo>:<GOlast>:<GO0..GO4>:<TUlast>:<TU0..TU4>:<latestPlayId>:<latestType>
///                            — 子機を唯一の基準とするランキング／スコアのスナップショット（親機は表示用コピーとして保持。旧 CHILD_SCORE は使用しない）
///   TEAM13_LOUD_ITEM         — child picked up loud item → trigger rush-in
///   TEAM13_RETURN_TO_TITLE   — child returned to title → parent also returns
/// </summary>
public class ParentUdpSender : MonoBehaviour
{

    /// <summary>
    /// 「タイトルへ戻る」同期通知。親機⇄子機の双方向で同じメッセージを使用する。
    /// 完全なメッセージは "TEAM13_RETURN_TO_TITLE"。SendState は MAGIC_NUMBER("TEAM13_") を
    /// 前置するため、ここでは payload 部分の "RETURN_TO_TITLE" のみを保持する。
    /// </summary>
    private const string ResultGameOverScene = "ParentGameOver";
    private const string ResultTimeUpScene   = "ParentGameClear";

    public enum ConnectionState { Disconnected, Connecting, Connected }

    // ── Inspector ─────────────────────────────────────────────────────────────
    public int    normalPort       = 8000;
    public int    broadcastPort    = 8001;
    public int    parentReceivePort = 8002;
    public string targetIP         = "127.0.0.1";
    public ConnectionState currentState = ConnectionState.Disconnected;

    [Header("Solo Start（子機接続なしで開始）")]
    public string             gameSceneName    = "ParentLoading";
    [Tooltip("Solo Start（子機接続なし）の遷移先シーン。ParentLoadingは子機を無期限に待つため経由せず、直接このシーンへ行く。")]
    public string             soloGameSceneName = "ParentGameScene";
    [Tooltip("実際にゲームプレイが行われるシーン名。LOUD_ITEM（ラッシュイン）の処理や MotherSuspicionSystem の参照検索はこのシーンでだけ行う。" +
             "gameSceneName は『ゲーム開始時に最初に読み込むシーン（ParentLoading）』で、プレイ中のシーンとは別物。")]
    public string             gameplaySceneName = "ParentGameScene";

    [Header("タイトルへ戻る（親機のキーボード）")]
    [Tooltip("ゲーム中などに、キーを長押しして親機をタイトル画面へ戻す。タイトル画面では無効。")]
    public bool               enableReturnToTitleKey = true;
    public Key                returnToTitleKey = Key.Escape;
    [Tooltip("誤操作防止のため、このキーをこの秒数だけ押し続けるとタイトルへ戻る。")]
    public float              returnToTitleHoldSeconds = 1.5f;
    [Tooltip("戻る先の親機タイトルシーン名。")]
    public string             motherTitleSceneName = "ParentTitle";
    [Tooltip("子機のタイトルシーン名。親機のタイトル復帰では motherTitleSceneName を使う。この値は子機向けの判定にのみ使用する。")]
    public string             titleSceneName   = "ChildeTitle";
    public string             gameOverSceneName = "ParentGameOver";
    public string             timeUpSceneName   = "ParentGameClear";

    // ── PlayerPrefs Keys（結果・ランキング保存用） ─────────────────────────────
    // 親機・子機で同じPlayerPrefs保存形式（キー文字列・降順TOP5）を使用します。
    // 既存セーブデータおよびランキングとの互換性を保つため、キー文字列の値は絶対に変更しないでください。
    private const string KeyGameOverScore  = "LastGameOverScore";
    private const string KeyTimeUpScore    = "LastTimeUpScore";
    private const string KeyGameOverRank   = "GameOverRank_";
    private const string KeyTimeUpRank     = "TimeUpRank_";
    private const int    RankingSize       = 5;

    [Header("Debug")]
    [Tooltip("通信ログなどの詳細出力を有効にする")]
    [SerializeField] private bool showDebugLogs = true;

    // ── Private networking ────────────────────────────────────────────────────
    // ソケット・受信スレッド・受信データのキューは通信基盤（UdpChannel）が所有する。
    // 本クラスは受信内容の解釈とゲーム処理だけを行い、ソケットを別途生成しない。


    // 接続管理（状態遷移・接続先確定の判断・PING復帰・最終受信時刻・タイムアウト判定・開始/キャンセル）は
    // ParentConnectionLink が保持する。実行時の状態の保持元はリンク側で、
    // currentState / targetIP は互換のためのミラー（HandleLinkStateChanged で同期する）。
    // 実行時の状態の保持元は Host（この窓口は別インスタンスを生成しない＝二重保持しない）。
    //  ・問い合わせ用の委譲プロパティ（使用してよい）。
    private ParentConnectionLink _link => _host != null ? _host.Link : null;

    // ── 実行時Host（同一GameObject上に生成し、通信・周期・境界処理を一本化する）──────────
    private ParentUdpRuntimeHost _host;
    private bool _bridgesConnected;              // 通知橋渡しを接続済みか（解除と対で管理する）
    private bool _hostUnavailableLogged;         // Host未初期化ログの連発防止（同じ個体で1回だけ）
    private readonly ParentUdpRuntimeHostSettings _settingsAdapter = new ParentUdpRuntimeHostSettings();

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
    private void LogHostUnavailable() { if (_hostUnavailableLogged) return; _hostUnavailableLogged = true; Debug.LogError("[ParentUdpSender] Host が利用できないため、このAPIは未初期化扱いで何もしません（旧実装へのフォールバックは行いません）。"); }
    private float     _pingInterval  = 1.0f;
    private float     _timeoutLimit  = 3.0f;

    // ── RETURN_TO_TITLE のプレイ識別（古い再送・遅延パケットの無視用） ─────────────────────
    // 連番だけでは「前回プレイの、まだ届いていない番号の通知」を区別できない
    // （例: 最後に受信した番号が7 → 前回の8は未受信 → 次プレイ中に古い8が届くと 8>7 で誤受理）。
    // また片端末の再起動で連番が1に戻ると大小比較が破綻する。
    // そのため「プレイ識別子（両端末で共有）」＋「そのプレイ内の連番」の2段で判定する。
    //
    // プレイ識別子は、開始処理（子機 START_GAME / 親機 Solo Start）で必ず新しく作り、
    // 子機→親機は START_GAME:<sid> で伝える。これにより既存の開始フローと整合する。
    // プレイセッション管理（プレイID・開始済み・ロード完了記録・結果処理済み・捕獲確定プレイID）は
    // ParentPlaySession が保持する。以下は互換のためのミラー（同じ値へ読み書きする）。
    private ParentPlaySession _session => _host != null ? _host.Session : null;

    private string _currentPlaySessionId
    {
        get => _session.PlaySessionId;
        set => _session.PlaySessionId = value;
    }

    // 親機（このプロセス）が RETURN_TO_TITLE を送るたびに単調増加する連番。アプリ実行中は決してリセットしない。
    // 同じプレイ内での重複・再送を弾くために使う（プレイ識別子が一致する場合のみ意味を持つ）。


    /// <summary>
    /// 親機が子機から受信した最終スコア（CHILD_SCORE）を PlayerPrefs へ保存し、
    /// ランキング更新まで完了したときに発火する静的イベント。
    /// 結果シーンの表示（ResultScoreUI など）は、このイベントで表示を再読み込みして
    /// 親機のUDP受信・保存完了と結果UIを同期する（子機GameManagerのイベントには依存しない）。
    /// </summary>
    public static event System.Action ResultDataCommitted;
    // ── 子機のロード完了（現在プレイの受信記録）────────────────────────────────
    // LOADING_COMPLETE の受信結果そのもの（＝通信層が受け取った事実）。待機するかどうかの判断はロード側が持つ。
    //   ・購読前に届いた通知も参照できるよう、現在プレイの記録として暫定保持する（読み取り専用）。
    //   ・書き込みは本クラス内のみ（公開setterは無い）。新しいロード処理からは記録を書き換えない。
    //   ・初期化は「プレイの開始を受理した時点（ParentLoadingへ進む前）」と「タイトル復帰時」に行う。
    public  bool      ChildLoadingComplete => _session.ChildLoadingComplete;

    /// <summary>
    /// 子機の LOADING_COMPLETE を受信して、現在プレイの受信記録が false→true になったときに
    /// メインスレッドで発火する通知（同値＝再送では発火しない）。
    /// 購読前に届いていた通知は ChildLoadingComplete の初回読み取りで同期する。
    /// 受信処理（HandleIncoming）は Update から呼ばれるため、この通知もメインスレッドで発火する。
    /// </summary>
    public event System.Action LoadingCompleteReceived;

    // ── LOUD_ITEM（子機の大きな音）の通知 ─────────────────────────────────────
    // 受信後の反応（親機の突入演出など）はゲームシーン側の購読者が行う。
    // 本クラスは「LOUD_ITEM を受信した」ことだけをメインスレッドで通知する
    // （受信処理 HandleIncoming は Update から呼ばれるため、この通知もメインスレッドで発火する）。
    // 通知・保留（LOUD_ITEM／GAME_PROGRESS）の**保留状態**は ParentNotificationHub が保持する。
    //  ・イベントは従来どおり field 風イベントとして本クラスが持つ（宣言クラス内から購読者有無を読めるため、
    //    「実際の購読者が居るか」を正しく判定できる＝転送ハンドラで誤認しない）。
    //  ・「イベント購読の新担当への委譲」は、購読者有無の読み取りと通知呼び出しを新担当APIへ置き換える
    //    作業（次段階）とセットで行う。ここでは保留フラグ／最新値だけを新担当へ移す。
    // 通知購読の実体は ParentNotificationHub が持つ（add/remove を委譲）。
    //  ・転送ハンドラを挟まないため、ハブの HasLoudItemSubscriber は実際のゲーム側購読者の有無と一致する。
    //  ・旧側の null 判定／Invoke はハブAPI（HasLoudItemSubscriber／NotifyLoudItemNow）へ置換済み。
    public event System.Action LoudItemReceived
    {
        add => _notifications.LoudItemReceived += value;
        remove => _notifications.LoudItemReceived -= value;
    }

    // ── GAME_PROGRESS（子機のゲーム進行率）の通知 ─────────────────────────────
    // 受信後の反応（片付け演出の開始条件判定など）はゲームシーン側の購読者が行う。
    // 本クラスは「受信した進行率」だけをメインスレッドで通知する（MotherSuspicionSystemの直接呼び出しは行わない）。
    //  ・進行率の変換・範囲（Clamp01(Score/1000f)）は従来どおり。通信形式は変更しない。
    //  ・ゲームシーン以外の受信は従来どおり破棄する。
    public event System.Action<float> GameProgressReceived
    {
        add => _notifications.GameProgressReceived += value;
        remove => _notifications.GameProgressReceived -= value;
    }

    // 購読者がまだ居ないときに受信した LOUD_ITEM を1件だけ保留する（購読準備前の受信を落とさない）。
    //   ・消費は Update で「ゲームシーン」かつ「有効な購読者が居る」ことを確認してから1回だけ。
    //   ・シーン読込／タイトル復帰でクリアし、次シーン・次プレイへ持ち越さない。
    // （保留フラグ／最新値の保持元＝ParentNotificationHub。以下は互換のためのプロパティ）
    private ParentNotificationHub _notifications => _host != null ? _host.Notifications : null;

    // 保留状態はハブ（ParentNotificationHub）が保持する（旧側からの直接書き込みは無し）。

    // ── 片付け演出：ゲーム進行率の保留 ───────────────────────────────────────
    //   購読者（ゲームシーン側）が準備できる前に受信した進行率の「最新値」を1件だけ保持する。
    //   ・履歴は蓄積しない（保留中に新値が届いたら上書き＝常に最新のみ）。
    //   ・消費は Update で「ゲームシーン」かつ「有効な購読者が居る」ことを確認してから1回だけ。
    //   ・シーン読込／タイトル復帰でクリアし、次シーン・次プレイへ持ち越さない。
    // 保留状態（GAME_PROGRESS）はハブが保持する（旧側からの直接書き込みは無し）。
    // 保留値（GAME_PROGRESS 最新値）はハブが保持する（旧側からの直接書き込みは無し）。

    // ── 最終結果（CHILD_SCORE）のプレイ紐付け・重複防止 ──────────────────────
    // 「現在のプレイ」または「既知の終了プレイ」の結果だけを受理し、
    // 保存（1プレイにつき1結果・捕獲優先）と画面遷移（現在プレイのみ）を分けて扱う。
    // ── 子機ランキングの表示用コピー ────────────────────────────────────────
    // 親機は独自にランキングを持たない（子機が唯一の基準）。古い ParentResult_* は残すが新表示には使わない。

    // ランキング同期の状態（同期ストア・ピア検証・接続ごとの検証済み・表示状態）の保持元は
    // ParentRankSyncCoordinator。値の参照は Coordinator の読み取り専用問い合わせ経由で行う。
    private ParentRankSyncCoordinator _rankSyncCoordinator => _host != null ? _host.RankSync : null;
    public static ParentUdpSender Instance { get; private set; }

    // ── 接続状態の通知（タイトルUIなどへ状態変化だけを伝える）─────────────────
    /// <summary>
    /// 接続状態が変化したときにメインスレッドで発火する（同値では発火しない）。
    /// 受信スレッドからUIイベントを直接呼ばないよう、状態変更は必ず SetConnectionState 経由で行う。
    /// </summary>
    public event System.Action<ConnectionState> ConnectionStateChanged;

    /// <summary>現在の接続状態（タイトルUIの初回同期用の読み取りAPI）。</summary>
    public ConnectionState CurrentConnectionState => currentState;

    /// <summary>開始済みか（診断ログ用の読み取りAPI。UI側からは書き換えない）。</summary>
    public bool IsGameStarted => _host != null && _host.IsGameStarted;






    // ── Singleton / DontDestroyOnLoad ──────────────────────────────────────────
    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.Log("[ParentUdpSender] Duplicate detected — destroying self.");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // ── 生存窓口だけが、同一GameObject上の Host を取得／生成して接続する（重複個体はここへ来ない）──
        _host = GetComponent<ParentUdpRuntimeHost>();
        if (_host == null) _host = gameObject.AddComponent<ParentUdpRuntimeHost>();

        // 設定参照（使用時点の旧シリアライズ値）と診断出力を接続する。
        _host.BindSettingsProvider(RefreshSettingsAdapter);
        _host.BindShowDebugProvider(() => showDebugLogs);

        // 担当（Link／Session／Hub／RankSync）の生成はここ（通信は開始しない）。
        if (!_host.Initialize(RefreshSettingsAdapter(), out string initError))
        {
            Debug.LogError($"[ParentUdpSender] Host の初期化に失敗しました: {initError}。通信は開始しません（旧実装へのフォールバックは行いません）。");
            return;
        }

        // 通知の橋渡し（解除と対で管理する）。Hostの通知を既存イベントへ一経路だけ流す。
        ConnectHostBridges();

        // Inspectorから読み込んだ接続先を、リンクへ先に渡す（リンクの既定値でInspector値を上書きしない）。
        //  ※状態変化の購読と互換フィールドへのミラーは Host 側の担当（Hostが自分のLinkで行う）。
        _link.SeedTargetIP(targetIP);
    }

    // ── Button callbacks ──────────────────────────────────────────────────────
    public void OnConnectButtonClicked()
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.OnConnectButtonClicked();
    }
    public void OnCancelButtonClicked()
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.OnCancelButtonClicked();
    }
    public void OnStartButtonClicked()
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.OnStartButtonClicked();
    }

    /// <summary>
    /// 子機の接続状態に関係なく、親機だけでゲームを開始する。
    /// Connect Button の隣などに置く専用ボタンの OnClick から呼ぶ想定。
    /// タイトルBGMのフェードアウトなど、子機起点の開始（START_GAME受信）と同じ後処理を通る。
    /// </summary>
    public void OnSoloStartButtonClicked()
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.OnSoloStartButtonClicked();
    }


    // フェード付き遷移（タイトルBGMフェード開始＋画面暗転の完了待ち＋LoadScene）の実行は
    // ParentSceneTransitionRunner へ移した。既存の入口（ゲーム開始／ソロ開始／START_GAME受理）は
    // 従来どおり StartCoroutine／yield return で起動し、起動・停止とライフサイクルは本クラスが持つ。

    // ── Host通知の橋渡し（既存イベントへ一経路だけ流す。購読は解除と対で管理）────────
    /// <summary>Hostの通知を既存イベントへ接続する（二重接続しない）。</summary>
    private void ConnectHostBridges()
    {
        if (_bridgesConnected || _host == null) return;

        _host.ConnectionStateChanged += HandleHostConnectionStateChanged;
        _host.LoadingCompleteReceived += HandleHostLoadingCompleteReceived;
        _host.ResultDataCommitted += HandleHostResultDataCommitted;

        _bridgesConnected = true;
    }

    /// <summary>自分の橋渡し購読だけを解除する（他個体の購読を外さない）。</summary>
    private void DisconnectHostBridges()
    {
        if (!_bridgesConnected || _host == null) return;

        _host.ConnectionStateChanged -= HandleHostConnectionStateChanged;
        _host.LoadingCompleteReceived -= HandleHostLoadingCompleteReceived;
        _host.ResultDataCommitted -= HandleHostResultDataCommitted;

        _bridgesConnected = false;
    }

    /// <summary>接続状態の橋渡し（型付き値のenum変換。detail文字列は使わない）。</summary>
    private void HandleHostConnectionStateChanged(ParentConnectionLink.LinkState state)
    {
        currentState = MapToCompatConnectionState(state);
        ConnectionStateChanged?.Invoke(currentState);
    }

    /// <summary>子機ロード完了の橋渡し（発生条件・回数は Host 側で担保）。</summary>
    private void HandleHostLoadingCompleteReceived() => LoadingCompleteReceived?.Invoke();

    /// <summary>結果データ通知の橋渡し（static イベントの唯一の発火入口）。</summary>
    private void HandleHostResultDataCommitted() => ResultDataCommitted?.Invoke();

    /// <summary>LinkState → 既存 ConnectionState の変換。</summary>
    private static ConnectionState MapToCompatConnectionState(ParentConnectionLink.LinkState state)
    {
        switch (state)
        {
            case ParentConnectionLink.LinkState.Connecting: return ConnectionState.Connecting;
            case ParentConnectionLink.LinkState.Connected: return ConnectionState.Connected;
            default: return ConnectionState.Disconnected;
        }
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────
    void Start()
    {
        // Force result scenes to shared names across parent/child.
        gameOverSceneName = ResultGameOverScene;
        timeUpSceneName = ResultTimeUpScene;

        // ── 明示的な通信開始（旧Startに相当する時点。HostのStartからは重ねて起動しない）──
        if (_host == null)
        {
            Debug.LogError("[ParentUdpSender] Host が存在しないため通信を開始しません（旧実装へのフォールバックは行いません）。");
            return;
        }

        // 接続先の初期値をリンクへ渡してから起動する（Inspector値を優先する）。
        if (_host.Link != null) _host.Link.SeedTargetIP(targetIP);

        if (!_host.TryStartRuntime(out string startError))
        {
            Debug.LogError($"[ParentUdpSender] Host の通信開始に失敗しました: {startError}。通信は開始しません（旧実装へのフォールバックは行いません）。");
            return;
        }

        // 以降の旧Start処理（ソケット生成・復元・sceneLoaded購読）は Host が行うため実行しない。
        return;
    }




    void OnDestroy()
    {
        // 自分の橋渡し購読と自分の登録だけを解除する（他個体の購読・Hostのリソースには触れない）。
        DisconnectHostBridges();
        if (Instance == this) Instance = null;
    }


    // ── Scene reference refresh ──────────────────────────────────────────────

    // ゲーム中（タイトル以外のシーン）で returnToTitleKey を returnToTitleHoldSeconds 秒押し続けると、
    // 親機をタイトル画面へ戻す。誤操作防止のため長押し式。

    /// <summary>
    /// 親機をタイトル画面へ戻す。タイトルに入ると OnSceneLoaded → ResetForNewSession が走り、
    /// 接続状態・リザルト判定などのセッション状態がすべて初期化される。
    /// notifyPeer=true（ユーザー操作・長押し復帰など、この端末が起点の場合）は子機へも TEAM13_RETURN_TO_TITLE を送信する。
    /// notifyPeer=false（子機からの RETURN_TO_TITLE 受信が起点の場合）は送り返さない（無限往復の防止）。
    /// </summary>
    public void ReturnToTitle(bool notifyPeer = true)
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.ReturnToTitlePrepared(notifyPeer);
    }

    /// <summary>
    /// 親機起点で「タイトルへ戻る」ことを子機へ通知する（TEAM13_RETURN_TO_TITLE、再送付き）。
    /// 親機のリザルト画面（ResultSceneChamger）とキーボードのタイトル復帰（ReturnToTitle）から呼ばれる。
    /// </summary>
    public void NotifyReturnToTitleToChild(bool fast = false)
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.NotifyReturnToTitleToChildPrepared(fast);
    }



    // 実際のゲームプレイシーンか。gameSceneName は最初に読み込む ParentLoading を指すため、
    // プレイ中の判定には使えない（使うと LOUD_ITEM が常に無視され、ラッシュインが起きない）。


    // タイトルのボタンへのリスナー登録は ParentTitleConnectionUI が担当する
    // （旧実装は RemoveAllListeners で他コンポーネントの永続リスナーまで消していたため、
    //   本クラスからは一切登録しない）。


    public void ResetForNewSession()
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.ResetForNewSessionPrepared();
    }

    // タイトルUIの参照取得は ParentTitleConnectionUI が担当する（Inspector参照のみ・名前検索なし）。

    // ── Public send API ───────────────────────────────────────────────────────
    // ── 演出完了通知（子機の待機解除）──────────────────────────────────────────

    /// <summary>
    /// 捕獲演出が完了（または安全に打ち切り）し、結果画面へ進めることが確定した時点で子機へ通知する。
    ///  ・Jumpscare開始時には送らない（呼び出し元がカメラ完了待ちまで終えた後に呼ぶ）。
    ///  ・UDPなので1回送りにせず複数回送る（子機は重複を無視してACKを返す）。
    ///  ・プレイ識別（playSessionId）付きなので、前回プレイの遅延通知で今回の待機は解けない。
    /// </summary>
    public void NotifyGameOverSequenceCompleted(string reason)
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.NotifyGameOverSequenceCompletedPrepared(reason);
    }


    /// <summary>
    /// 子機からの受領確認（SEQUENCE_DONE_ACK:&lt;playSessionId&gt;）を処理する。
    /// 戻り値：今回のプレイのACKとして受理したら true。
    /// </summary>
    public bool HandleSequenceDoneAck(string playId)
    {
        if (_host == null) { LogHostUnavailable(); return false; }
        return _host.HandleSequenceDoneAckPrepared(playId);
    }

    public void SendState(string message)
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.SendState(message);
    }

    public void SendStateSLEEP_LOCK()
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.SendStateSLEEP_LOCK();
    }

    public void SendStateSLEEP_UNLOCK()
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.SendStateSLEEP_UNLOCK();
    }

    /// <summary>
    /// 親機側で「タイトルへ戻る」ことを子機へ通知する（TEAM13_RETURN_TO_TITLE:<連番>）。
    /// 子機のリザルト画面（ResultSceneChamger）や親機のキーボード復帰から呼ばれる。
    /// 連番はこのプロセス起動後ただただ単調増加し、前回プレイの再送・遅延パケットの識別に使う。
    /// </summary>
    public string NotifyReturnToTitle(bool fast = false)
    {
        if (_host == null) { LogHostUnavailable(); return ""; }
        return _host.NotifyReturnToTitleMessagePrepared(fast);
    }

    /// <summary>
    /// 親機の捕獲によるGame Over確定を通知する。
    /// 初回呼び出し時のみ _resultProcessed を true にし、CAUGHT を即時送信および短時間再送する。
    /// </summary>
    public void NotifyGameOverFromParentCatch()
    {
        if (_host == null) { LogHostUnavailable(); return; }
        _host.NotifyGameOverFromParentCatchPrepared();
    }



    // ゲーム中に子機側の一時的なフリーズ等で PING が途絶えると、親機は Timeout で Disconnected になり、
    // 以後 SendState（SLEEP_LOCK / SLEEP_UNLOCK など）が全て無視されてしまう。
    // PING が再開した場合は接続を復帰する。タイトル画面での Cancel（意図的な切断）は復帰させない。

    // ── Incoming message dispatch (main thread) ───────────────────────────────

    // ── Final result (CHILD_SCORE): 保存と画面遷移の分離 ──────────────────────

    /// <summary>
    // ── 子機ランキングの表示用コピー（同期）─────────────────────────────────




    /// <summary>今接続している子機の同期元（未確定なら空）。</summary>
    public string ConnectedPeerSourceId => _rankSyncCoordinator.ConfirmedPeerSourceId;

    /// <summary>
    /// 表示用コピーの状態（未同期／前回同期データ／同期完了）。
    /// Current は「今の接続相手が確定済み」「コピーの sourceId がそれと一致」
    /// 「今回の接続で適用または同一コピーを確認済み」のすべてを満たすときだけ。
    /// </summary>
    public RankSyncDisplayState GetSyncDisplayState() => _rankSyncCoordinator.GetDisplayState();

    /// <summary>表示用コピーの状態ラベル（「未同期」「前回同期データ」「同期完了」）。</summary>
    public string GetSyncDisplayLabel() => _rankSyncCoordinator.GetDisplayLabel();

    /// <summary>
    /// 表示用コピーが「今の接続相手の最新」と言えるか。
    /// false のときは前回同期データ／未同期であり、最新の子機データとして見せてはいけない。
    /// </summary>
    public bool IsSyncCurrent => _rankSyncCoordinator.IsSyncCurrent;





    /// <summary>
    /// 表示状態が変わったときだけ ResultScoreUI へ更新通知を出す（結果画面を開いたままでも反映される）。
    /// </summary>
    public void NotifySyncStateChanged()
    {
        // 通知要否の判断と直前表示状態の更新は Coordinator（順序は既存どおり：評価→要否→更新→ログ→発火）。
        if (!_rankSyncCoordinator.ShouldNotifySyncState()) return;

        RankSyncDisplayState previous = _rankSyncCoordinator.CommitSyncState();
        RankSyncDisplayState state = _rankSyncCoordinator.GetDisplayState();

        if (showDebugLogs)
            Debug.Log($"[ParentUdpSender] Sync display state {previous} → {state} — notifying ResultScoreUI.");

        // 実発火（互換イベント）は旧側に残す。
        ResultDataCommitted?.Invoke();
    }


    /// <summary>表示用コピーの有無（「未同期」を判別するため）。</summary>
    public bool HasRankSyncData => _rankSyncCoordinator.HasStoreData;

    /// <summary>表示用コピーの更新番号・同期元・受信時刻（診断／鮮度表示用）。</summary>
    public int RankSyncUpdateNo => _rankSyncCoordinator.StoreUpdateNo;
    public string RankSyncSourceId => _rankSyncCoordinator.StoreSourceId;
    public int RankSyncReceivedAtUnix => PlayerPrefs.GetInt(PrefRankSyncAt, 0);

    /// <summary>保存キー（旧側と同一のキー文字列。値の出所を変えない）。</summary>
    private const string PrefRankSyncAt = "RankSync_ReceivedAt";

    /// <summary>表示用コピーから、指定種別の最新スコアとTOP5を取り出す（ResultScoreUI 用）。</summary>
    public bool TryGetSyncedResult(bool gameOver, out int lastScore, out int[] top5)
    {
        // 取得・成立条件は Coordinator（既存の戻り値・out値の意味をそのまま維持）。
        return _rankSyncCoordinator.TryGetSyncedResult(gameOver, out lastScore, out top5);
    }

    // ── Coroutines ────────────────────────────────────────────────────────────

    // ── Log queue processing ──────────────────────────────────────────────────


    // ── 受信スレッドからの受け渡し（メインスレッド）────────────────────────────
    //   受信経路ごとのデータと通信エラーを、受信順に取り出して既存の経路へ流す。
    //   ・Discovery経路: 従来 ReceiveDiscovery が受信スレッドで行っていた種別判定をここで行い、
    //   ・通信エラー: 従来 EnqueueLog(Error, ...) と同じ内容でログキューへ積む。


    // タイトルUIの表示更新（ラベル・Connect/Cancelボタンの表示）は ParentTitleConnectionUI が
    // ParentUdpSender.ConnectionStateChanged を受けて行う。

    // ── Helpers ───────────────────────────────────────────────────────────────
    // 挿入ロジック本体は RankingUtil に集約（子機と共有・単体テスト可能）。挙動は従来と同一。
}
