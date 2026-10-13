using System;
using UnityEngine;

/// <summary>
/// ChildUdpReceiver:
/// Handles all UDP communication on the child side.
///
/// ★シーン無変更切替（今回）：実行責任は ChildUdpRuntimeHost が持つ。
///  本クラスは互換窓口（既存の設定・公開API・イベント・enum・static instance・ミラー）だけを残し、
///  実行APIはすべて Host へ委譲する（Host不在時に旧本体へ落ちる条件付きフォールバックは持たない）。
///
/// 本クラスが残す責任：
///  ・Inspector設定（ポート・シーン名・参照・診断出力）と設定参照アダプタ
///  ・static instance と生存窓口の管理（重複判定・DDOL・instance解除）
///  ・公開enum（ConnectionState／CaptureWaitState）とその変換
///  ・公開API・公開フィールド（currentState／targetIP／lastMessage のミラー更新を含む）
///  ・Host接続（取得またはAddComponent→設定参照→Initialize→橋渡し）と起動・終了の接続
///  ・イベント橋渡し（接続状態は Host の型付き通知から旧enumへ変換、通知系は Host の Hub へ一経路で接続）
///
/// Hostが持つ責任（本クラスからは移した）：
///  ・UdpChannel（ソケット）／受信・ログポンプ／受信ディスパッチと適用
///  ・Discovery／Heartbeat／送信再送／ランキング再送のCoroutine、タイムアウト
///  ・ランキングの保存・復元・スナップショット生成・ACK確定
///  ・捕獲待機・CAUGHT保留・SEQUENCE_DONE・RETURN_TO_TITLE・シーン境界・実遷移
///
/// Outbound messages (child → parent, port 8002):
///   TEAM13_START_GAME / TEAM13_RANK_SYNC:<...> / TEAM13_LOUD_ITEM / TEAM13_PING /
///   TEAM13_GAME_PROGRESS:<milli> / TEAM13_SEQUENCE_DONE_ACK:<playId> / TEAM13_RETURN_TO_TITLE:<playId>:<seq>[:FAST] /
///   TEAM13_LOADING_COMPLETE
///
/// Inbound messages (parent → child, port 8000):
///   TEAM13_START_GAME / TEAM13_CAUGHT / TEAM13_SLEEP_LOCK / TEAM13_SLEEP_UNLOCK / TEAM13_PING /
///   TEAM13_RETURN_TO_TITLE / TEAM13_SEQUENCE_DONE:<playId>:<reason> / TEAM13_DISCOVERY_ACCEPT /
///   TEAM13_RANK_SYNC_CHALLENGE:<version>:<challengeId> / TEAM13_RANK_SYNC_ACK:<version>:<sourceId>:<updateNo>
/// </summary>
public class ChildUdpReceiver : MonoBehaviour
{
    public enum ConnectionState { Disconnected, Connecting, Connected }

    // ── Inspector（旧側の設定。値・既定値・意味は変えない）────────────────────
    public int normalPort = 8000;
    public int broadcastPort = 8001;
    public int parentReceivePort = 8002;
    public string targetIP = "127.0.0.1";
    public ConnectionState currentState = ConnectionState.Disconnected;
    public string lastMessage = "";
    public string gameSceneName = "ChildLoading";
    public string titleSceneName = "ChildeTitle";

    public SleepingManager sleepingManager;

    [Header("Game References")]
    [Tooltip("診断ログ用のPlayerMove参照（Inspector割当のみ。睡眠ロックの適用は ChildSleepLockApplier が担当）。")]
    public PlayerMove playerMove;

    [Header("Debug")]
    [Tooltip("通信ログなどの詳細出力を有効にする")]
    [SerializeField] private bool showDebugLogs = true;

    public static ChildUdpReceiver instance { get; private set; }

    // ── 実行責任（Host）───────────────────────────────────────────────────────
    //  Host は本 GameObject 上に1つだけ存在する（窓口が取得またはAddComponentする）。
    //  ・窓口は Host の内部状態を複製しない（別Link／Session／Hub／Coordinatorを生成しない）。
    private ChildUdpRuntimeHost _host;
    private ChildUdpRuntimeHostSettings _settingsAdapter;
    private bool _bridgesConnected;
    private bool _hostUnavailableLogged;

    // ── 購読順序に依存しないための窓口側デリゲート保持 ─────────────────────────
    //  Host（＝Hub）がまだ生成されていない時点で consumer が add しても取りこぼさないよう、
    //  窓口が登録済みデリゲートを保持し、Host／Hub の準備後に**一度だけ**転送ハンドラを接続する。
    //  ・複数登録・同一デリゲートの重複登録はマルチキャストのまま保持する（勝手に重複排除しない）。
    //  ・準備前の remove はそのまま減算され、後から復活しない（残数0なら接続しない）。
    //  ・接続処理を複数回通っても二重購読しない（イベントごとのフラグで管理）。
    //  ・破棄時は自分が接続した転送ハンドラだけを解除する。
    private Action<bool> _sleepLockHandlers;
    private Action _caughtHandlers;
    private Action _captureWaitStartedHandlers;
    private Action<string> _captureSequenceReleasedHandlers;
    private bool _sleepLockAttached;
    private bool _caughtAttached;
    private bool _captureWaitAttached;
    private bool _captureReleasedAttached;

    /// <summary>Host が初期化済みか（未初期化・初期化失敗のときは実行APIを未初期化扱いにする）。</summary>
    private bool HostReady => _host != null && _host.IsConfigured;

    /// <summary>Hostが保持する通知ハブ（イベントの実体。窓口は別Hubを作らない）。</summary>
    private ChildNotificationHub Hub => _host != null ? _host.Notifications : null;

    // ── 接続状態の通知（タイトルUIなどへ状態変化だけを伝える）─────────────────
    /// <summary>
    /// 接続状態が変化したときにメインスレッドで発火する（同値では発火しない）。
    /// 実体は Host の型付き通知（ChildConnectionLink.LinkState）で、ここで旧enumへ変換して発火する。
    /// </summary>
    public event Action<ConnectionState> ConnectionStateChanged;

    /// <summary>現在の接続状態（タイトルUIの初回同期用の読み取りAPI）。</summary>
    public ConnectionState CurrentConnectionState => currentState;

    // ── 睡眠ロックの通知（SLEEP_LOCK / SLEEP_UNLOCK）─────────────────────────
    /// <summary>
    /// 親機の睡眠ロック状態（SLEEP_LOCK=true / SLEEP_UNLOCK=false）が変化したときに発火する。
    /// 同値（冗長なLOCK/UNLOCK・同値の再送）では発火しない。適用（入力停止・PlayerMove・睡眠中画像）は購読者が行う。
    /// イベント実体は Host が保持する ChildNotificationHub（add/remove を直接委譲＝別Hubを作らない）。
    /// </summary>
    public event Action<bool> SleepLockChanged
    {
        add { _sleepLockHandlers += value; AttachHubIfNeeded(); }
        remove { _sleepLockHandlers -= value; DetachHubIfNeeded(); }
    }

    /// <summary>現在の睡眠ロック状態（購読前の初期同期用の読み取りAPI）。true=親機が睡眠中でロック中。</summary>
    public bool IsSleepInputLocked => HostReady && _host.Notifications.IsSleepLocked;

    // ── CAUGHT（親機の捕獲）の通知 ────────────────────────────────────────────
    /// <summary>
    /// 親機の捕獲（CAUGHT）を受理したときにメインスレッドで発火する。
    /// ゲーム側の受信担当（GameManager）が購読し、結果処理を行う。購読者が居ない間の受理は1件だけ保留される。
    /// イベント実体は Host が保持する ChildNotificationHub（add/remove を直接委譲＝別Hubを作らない）。
    /// </summary>
    public event Action CaughtReceived
    {
        add { _caughtHandlers += value; AttachHubIfNeeded(); }
        remove { _caughtHandlers -= value; DetachHubIfNeeded(); }
    }

    /// <summary>ゲーム側の受信担当（購読者）が準備済みか（準備前は窓口が保持している登録の有無で答える）。</summary>
    public bool HasCaughtSubscriber
        => HostReady ? _host.Notifications.HasCaughtSubscriber : _caughtHandlers != null;

    // ── 捕獲演出の待機（親機の演出完了まで結果画面へ進ませない）────────────────
    /// <summary>捕獲を受理し、演出待機状態へ入ったときに発火する（入力ロック・画像表示の適用側が購読）。</summary>
    public event Action CaptureWaitStarted
    {
        add { _captureWaitStartedHandlers += value; AttachHubIfNeeded(); }
        remove { _captureWaitStartedHandlers -= value; DetachHubIfNeeded(); }
    }

    /// <summary>演出完了（または子機側の上限）で待機を解除したときに、理由付きで発火する。</summary>
    public event Action<string> CaptureSequenceReleased
    {
        add { _captureSequenceReleasedHandlers += value; AttachHubIfNeeded(); }
        remove { _captureSequenceReleasedHandlers -= value; DetachHubIfNeeded(); }
    }

    /// <summary>
    /// 捕獲演出の状態。「未捕獲」と「完了済み」を区別するため（「待機中ではない」だけで完了扱いにしない）。
    /// </summary>
    public enum CaptureWaitState
    {
        None = 0,
        Waiting = 1,
        Completed = 2,
    }

    /// <summary>ハブenum → 公開enum への明示的な対応付け（None／Waiting／Completed。数値キャストはしない）。</summary>
    private static CaptureWaitState ToPublicWaitState(ChildNotificationHub.CaptureWaitPhase phase)
    {
        switch (phase)
        {
            case ChildNotificationHub.CaptureWaitPhase.Waiting:   return CaptureWaitState.Waiting;
            case ChildNotificationHub.CaptureWaitPhase.Completed: return CaptureWaitState.Completed;
            default:                                              return CaptureWaitState.None;
        }
    }

    /// <summary>捕獲演出の待機中か（親機の演出完了待ち）。</summary>
    public bool IsWaitingGameOverSequence => HostReady && _host.Notifications.WaitingGameOverSequence;

    /// <summary>現在プレイの捕獲演出の状態（未捕獲／待機中／完了済み）。</summary>
    public CaptureWaitState CurrentCaptureWaitState
        => HostReady ? ToPublicWaitState(_host.Notifications.WaitPhase) : CaptureWaitState.None;

    /// <summary>
    /// 【初期同期】GameManager が購読成立時に確認する「現在プレイの演出完了が確定済みか」。
    ///  ・Completed のときだけ true（未捕獲 None を完了扱いにしない）。
    ///  ・購読より先に完了通知が処理されていても true になるため取りこぼさない。
    /// </summary>
    public bool IsCaptureWaitCompletedForCurrentPlay
        => HostReady && _host.IsCaptureWaitCompletedForCurrentPlay;

    /// <summary>購読前に受理したCAUGHTの保留が残っているか（ゲーム側の初期同期の問い合わせ用）。</summary>
    public bool HasPendingCaught => HostReady && _host.HasPendingCaught;

    // ── Unity lifecycle（生存窓口の管理とHost接続だけを行う）────────────────────
    private void Awake()
    {
        // 1) 既存の重複判定を最初に行う。重複個体は Host 生成・初期化・通信開始の前に戻る。
        if (instance != null && instance != this)
        {
            Debug.Log("[ChildUdpReceiver] Duplicate detected — destroying self.");
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);   // 窓口とHostを含む同一ルートで一経路にする

        // 2) 生存窓口だけが、同じGameObject上で Host を取得または追加する。
        _host = GetComponent<ChildUdpRuntimeHost>();
        if (_host == null) _host = gameObject.AddComponent<ChildUdpRuntimeHost>();

        // 3) 設定参照（Inspector値の現在値）と診断出力の現在値をHostへ接続する。
        _host.BindSettingsProvider(BuildSettings);
        _host.BindShowDebugProvider(() => showDebugLogs);

        // 4) 初期化（担当生成・設定接続・購読先準備まで。通信は開始しない）。
        if (!_host.Initialize(BuildSettings(), out string initError))
        {
            Debug.LogError($"[ChildUdpReceiver] Host の初期化に失敗しました: {initError}（旧通信へのフォールバックは行いません）");
            return;
        }

        // 5) 通知の橋渡し（Hostの接続状態通知 → 旧enumのイベント）。
        ConnectHostBridges();

        // 6) 準備前に add された Hub イベント登録を、Host／Hub の準備後に一度だけ接続する。
        AttachHubIfNeeded();
    }

    private void Start()
    {
        if (!HostReady) return;

        // 準備前に add された登録が残っていれば、ここでも接続を試みる（二重購読はしない）。
        AttachHubIfNeeded();

        // 旧 Awake の位置に対応：Inspector の接続先・接続状態をリンクの初期値として渡す
        //（イベントは出さない。設定アダプタの TargetIP はフォールバック用で、リンクの値を上書きしない）。
        ChildConnectionLink link = _host.Link;
        if (link != null)
        {
            link.SeedTargetIP(targetIP);
            link.SeedState(MapToLinkState(currentState));
            MirrorFromHost();
        }

        // 旧 Start 相当の時点で明示起動する（HostのAwake／OnEnable／Startからは開始しない）。
        if (!_host.TryStartRuntime(out string startError))
            Debug.LogError($"[ChildUdpReceiver] Host の通信開始に失敗しました: {startError}（旧通信へのフォールバックは行いません）");
    }

    /// <summary>
    /// 互換ミラーの更新だけを行う（チャネル・ポンプ・業務処理は Host が持つ）。
    ///  ・lastMessage：Host が保持する直近受信メッセージへ追従させる（残すだけで更新されない状態にしない）。
    ///  ・currentState／targetIP：接続担当の値へ追従させる（イベントは橋渡し側で1回だけ発火する）。
    /// </summary>
    private void Update()
    {
        if (!HostReady) return;

        string hostLastMessage = _host.LastReceivedMessage;
        if (!string.Equals(lastMessage, hostLastMessage, StringComparison.Ordinal))
            lastMessage = hostLastMessage;

        MirrorFromHost();
    }

    private void OnDestroy()
    {
        // 窓口は自分の橋渡しだけを解除する（HostのCoroutine・購読・チャネル・キューの解放はHost自身が行う）。
        DisconnectHostBridges();
        DetachAllHubForwarders();

        // instance 解除は本人条件に限定する（重複個体の破棄で生存個体を止めない）。
        if (instance == this) instance = null;
    }

    // ── Host接続・橋渡し ──────────────────────────────────────────────────────
    private void ConnectHostBridges()
    {
        if (_bridgesConnected || _host == null) return;
        _host.ConnectionStateChanged += HandleHostConnectionStateChanged;
        _bridgesConnected = true;
    }

    private void DisconnectHostBridges()
    {
        if (!_bridgesConnected || _host == null) return;
        _host.ConnectionStateChanged -= HandleHostConnectionStateChanged;
        _bridgesConnected = false;
    }

    /// <summary>Hostの型付き接続状態通知 → 旧enumのイベント（ミラーを先に整えてから1回だけ発火する）。</summary>
    private void HandleHostConnectionStateChanged(ChildConnectionLink.LinkState state)
    {
        MirrorFromHost();
        ConnectionStateChanged?.Invoke(currentState);
    }

    /// <summary>接続担当の値を互換フィールド（currentState／targetIP）へ写す（イベントは出さない）。</summary>
    private void MirrorFromHost()
    {
        if (_host == null) return;

        ChildConnectionLink link = _host.Link;
        if (link == null) return;

        currentState = MapLinkState(link.State);
        targetIP = link.TargetIP;
    }

    private static ConnectionState MapLinkState(ChildConnectionLink.LinkState state)
    {
        switch (state)
        {
            case ChildConnectionLink.LinkState.Connecting: return ConnectionState.Connecting;
            case ChildConnectionLink.LinkState.Connected:  return ConnectionState.Connected;
            default:                                       return ConnectionState.Disconnected;
        }
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

    /// <summary>
    /// 設定参照アダプタ：Inspector値の**現在値**を Host へ渡す（使用時点の設定を取得できる入口）。
    ///  ・アダプタは1つだけ生成し、呼び出しごとに同じインスタンスを更新する（new しない）。
    ///  ・Host 側は「開始時コピーへ固定」しない（送信・判定の都度この入口を読む）。
    /// </summary>
    private ChildUdpRuntimeHostSettings BuildSettings()
    {
        if (_settingsAdapter == null) _settingsAdapter = new ChildUdpRuntimeHostSettings();

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

    private void LogHostUnavailable()
    {
        if (_hostUnavailableLogged) return;
        _hostUnavailableLogged = true;
        Debug.LogError("[ChildUdpReceiver] Host が利用できないため、このAPIは未初期化扱いで何もしません（旧実装へのフォールバックは行いません）。");
    }

    // ── Hub接続（購読順序に依存しない）─────────────────────────────────────────
    /// <summary>
    /// Host／Hub が準備できていて、かつ登録済みデリゲートがある場合に**一度だけ**転送ハンドラを接続する。
    ///  ・準備前に add された登録もここで接続されるため失われない（準備後に再度呼ばれる）。
    ///  ・イベントごとのフラグで管理するため、複数回通っても二重購読しない。
    ///  ・発火は「Hub → 転送ハンドラ → 窓口が保持するデリゲート」の1経路（履歴再生や汎用基盤は持たない）。
    /// </summary>
    private void AttachHubIfNeeded()
    {
        ChildNotificationHub hub = Hub;
        if (hub == null) return;   // まだ準備できていない（Awake後に再度呼ばれる）

        if (!_sleepLockAttached && _sleepLockHandlers != null)
        {
            hub.SleepLockChanged += ForwardSleepLockChanged;
            _sleepLockAttached = true;
        }

        if (!_caughtAttached && _caughtHandlers != null)
        {
            hub.CaughtReceived += ForwardCaughtReceived;
            _caughtAttached = true;
        }

        if (!_captureWaitAttached && _captureWaitStartedHandlers != null)
        {
            hub.CaptureWaitStarted += ForwardCaptureWaitStarted;
            _captureWaitAttached = true;
        }

        if (!_captureReleasedAttached && _captureSequenceReleasedHandlers != null)
        {
            hub.CaptureSequenceReleased += ForwardCaptureSequenceReleased;
            _captureReleasedAttached = true;
        }
    }

    /// <summary>登録が空になったイベントだけ転送ハンドラを外す（準備前の remove が後から復活しない）。</summary>
    private void DetachHubIfNeeded()
    {
        ChildNotificationHub hub = Hub;

        if (_sleepLockAttached && _sleepLockHandlers == null)
        {
            if (hub != null) hub.SleepLockChanged -= ForwardSleepLockChanged;
            _sleepLockAttached = false;
        }

        if (_caughtAttached && _caughtHandlers == null)
        {
            if (hub != null) hub.CaughtReceived -= ForwardCaughtReceived;
            _caughtAttached = false;
        }

        if (_captureWaitAttached && _captureWaitStartedHandlers == null)
        {
            if (hub != null) hub.CaptureWaitStarted -= ForwardCaptureWaitStarted;
            _captureWaitAttached = false;
        }

        if (_captureReleasedAttached && _captureSequenceReleasedHandlers == null)
        {
            if (hub != null) hub.CaptureSequenceReleased -= ForwardCaptureSequenceReleased;
            _captureReleasedAttached = false;
        }
    }

    /// <summary>自分が接続した転送ハンドラだけをすべて外す（破棄時）。他個体・consumerの登録には触れない。</summary>
    private void DetachAllHubForwarders()
    {
        ChildNotificationHub hub = Hub;

        if (hub != null)
        {
            if (_sleepLockAttached) hub.SleepLockChanged -= ForwardSleepLockChanged;
            if (_caughtAttached) hub.CaughtReceived -= ForwardCaughtReceived;
            if (_captureWaitAttached) hub.CaptureWaitStarted -= ForwardCaptureWaitStarted;
            if (_captureReleasedAttached) hub.CaptureSequenceReleased -= ForwardCaptureSequenceReleased;
        }

        _sleepLockAttached = false;
        _caughtAttached = false;
        _captureWaitAttached = false;
        _captureReleasedAttached = false;
    }

    private void ForwardSleepLockChanged(bool locked) => _sleepLockHandlers?.Invoke(locked);
    private void ForwardCaughtReceived() => _caughtHandlers?.Invoke();
    private void ForwardCaptureWaitStarted() => _captureWaitStartedHandlers?.Invoke();
    private void ForwardCaptureSequenceReleased(string reason) => _captureSequenceReleasedHandlers?.Invoke(reason);

    // ── 公開API（すべて Host へ委譲。Host不在時は未初期化扱いで何もしない）────────
    /// <summary>接続ボタン起点（Connected なら開始、そうでなければ接続操作）。</summary>
    public void OnConnectButtonClicked()
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.OnConnectButtonClicked();
    }

    /// <summary>接続キャンセル（Disconnected へ）。</summary>
    public void OnCancelButtonClicked()
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.CancelConnect();
    }

    /// <summary>開始ボタン起点（プレイID発行 → START_GAME 送信 → ゲームシーンへ遷移）。</summary>
    public void OnStartButtonClicked()
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.OnStartButtonClicked();
    }

    /// <summary>
    /// ランキング／今回スコアが変化したときに、最新スナップショットを作成して親機へ送る。
    /// GameManager.TriggerResult（結果確定後）と RankingResetButton（リセット後）から呼ぶ。
    /// latestResultType は "GAME_OVER" / "TIME_UP" / "NONE"（リセット時）。
    /// </summary>
    public void NotifyRankingChanged(string latestResultType)
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.NotifyRankingChanged(latestResultType);
    }

    /// <summary>接続完了・再接続時に最新スナップショットを送る（未確認があれば同じ更新番号・同じ内容を再送）。</summary>
    public void SendRankSyncOnConnect()
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.SendRankSyncOnConnect();
    }

    /// <summary>状態文字列を1件送信する（接頭辞・宛先・失敗時のログはHost側で従来どおり）。</summary>
    public void SendState(string message)
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.SendState(message);
    }

    /// <summary>同じ本文を再送付きで送る（初回＋実時間 interval 間隔で extraSends 回。既定は旧側と同じ）。</summary>
    public void SendStateRepeated(string message, int extraSends = 3, float interval = 0.15f)
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.SendStateRepeated(message, extraSends, interval);
    }

    /// <summary>ラウドアイテムを取得したことを親機に送信する。</summary>
    public void SendLoudItem()
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.SendLoudItem();
    }

    /// <summary>ゲーム進行率（0〜1）を親機に送信する（間隔の制御は呼び出し側）。</summary>
    public void SendGameProgress(float progressRate)
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.SendGameProgress(progressRate);
    }

    /// <summary>
    /// 子機側で「タイトルへ戻る」処理を開始したことを親機へ通知する（再送付き。識別子・連番は再送でも同じ値）。
    /// </summary>
    public void notifyReturnToTitle(bool fast = false)
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.notifyReturnToTitle(fast);
    }

    /// <summary>
    /// 【子機側の上限】GameManager の待機タイムアウトから呼ぶ。
    ///  ・待機を解除して「完了済み」にそろえ、入力適用側（ChildSleepLockApplier）に古いロックを残さない。
    ///  ・結果遷移は呼び出し側（GameManager）が行う（この通知だけで遷移しない）。
    /// </summary>
    public void ReleaseCaptureWaitByLocalTimeout(string reason)
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.ReleaseCaptureWaitByLocalTimeout(reason);
    }

    /// <summary>セッション状態のリセット（タイトル復帰時の既存順序はHost側で維持）。</summary>
    public void ResetForNewSession()
    {
        if (!HostReady) { LogHostUnavailable(); return; }
        _host.ResetForNewSession();
    }
}
