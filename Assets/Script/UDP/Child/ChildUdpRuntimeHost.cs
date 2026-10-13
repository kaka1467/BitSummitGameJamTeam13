using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ChildUdpRuntimeHost：子機UDPランタイムの「実行所有者」になるための準備クラス（今回は非稼働）。
///
/// ★今回の段階では非稼働：
///  ・シーンへ配置しない／旧側から AddComponent で生成しない／自動生成の static getter を持たない。
///  ・Awake／OnEnable／Start では**自動で通信を開始しない**（Initialize と明示起動を分離）。
///  ・明示的な入口（Initialize → TryStartRuntime → ShutdownRuntime）を呼ばない限り、
///    ソケット・受信スレッド・sceneLoaded購読・Coroutine・PlayerPrefs復元は始まらない。
///  ・**実行責任は引き続き旧 ChildUdpReceiver が持つ**（本クラスは稼働していない）。
///
/// 再利用（再実装しない）：
///  ・ChildConnectionLink／ChildPlaySession／ChildNotificationHub／ChildRankSyncCoordinator
///  ・ChildCaughtFallbackPolicy／ChildSceneTransitionRunner／既存パーサ（RankSyncChallenge／RankSyncHello／RankingSnapshot）
///  ・UdpChannel（ソケット・受信スレッド）
///
/// 所有するもの（切替時に旧側から移す実行責任。現在は非稼働）：
///  ・UdpChannel と受信・ログキューのポンプ／実送信／受信ディスパッチと抽出済み担当への適用
///  ・Discovery・Heartbeat・ランキング再送・送信再送のCoroutine／sceneLoaded の購読・解除と境界処理本体
///  ・PlayerPrefs の実保存・復元／シーン遷移の実行依頼（ChildSceneTransitionRunner）
///  ・互換窓口が使う問い合わせ・通知出口
///
/// 準備できている経路（非稼働・旧側の条件／順序／数値のまま）：
///  ・接続：接続操作（Connecting）／キャンセル（Disconnected）／型付き接続状態通知
///  ・Discovery：DISCOVERY_REQUEST のブロードキャスト（0.5秒間隔・接続操作中のみ）
///  ・接続確定：DISCOVERY_ACCEPT の受理（IP確定→Connected→最終受信→ロード済み解除→接続時同期）
///  ・周期：Heartbeat（実時間 pingInterval 間隔・Connected のみ）／PING 受信での生存記録／タイムアウト切断
///  ・ランキング：起動時復元（sourceId／世代／updateNo／confirmed／pending）／結果更新・接続時の
///    スナップショット生成／HELLO 応答／送信・再送（0.5秒×6回）／ACK による確定・保存・再送停止
///  ・START_GAME：プレイID採用と新プレイ境界リセット／gameSceneLoaded ガード／実遷移
///  ・CAUGHT：受理判断（タイトル・結果での破棄／ロード中のID必須／重複抑止）／購読者優先通知／
///    1件保留と消費／GameManager不在のフォールバック（保存→Save→ランキング通知→実遷移）
///  ・SEQUENCE_DONE：別プレイ判定→effectivePlayId→ACK→Hubの重複判定→解除／先着保留
///  ・捕獲待機：開始／解除／購読前完了の問い合わせ／ローカルタイムアウト入口
///  ・RETURN_TO_TITLE：識別子・連番・fast経路／ResultSceneChamger のフェード流用／即時遷移
///  ・境界：sceneLoaded 購読（起動で1回）・解除（終了時）／タイトル・結果シーンの既存リセット順序
///  ・送信API：SendStateRepeated／SendLoudItem／SendGameProgress／notifyReturnToTitle／
///    START_GAME 送信／LoadGameScene
///
/// 切替（別バッチ）で行うもの：旧 ChildUdpReceiver の互換窓口化・consumer の型変更・
///  シーン配置／旧コンポーネント削除。シーン側の参照取得（sleepingManager 等）と睡眠ロックの初期同期は
///  ChildSleepLockApplier 等のシーン担当のままにする（Hostへ持ち込まない）。
///
/// 親機との差（持ち込まないもの）：
///  ・親機のフェード付き遷移方式・結果優先条件・親機側のRankSync経路は子機へ持ち込まない。
///  ・子機の既存条件（SLEEP初期同期・CAUGHT受理と保留・捕獲待機・SEQUENCE_DONE・保存/復元）を正とする。
/// </summary>
public sealed class ChildUdpRuntimeHost : MonoBehaviour
{
    /// <summary>ライフサイクル状態（明示的な入口だけで遷移する）。</summary>
    public enum RuntimeState { Created = 0, Configured = 1, Running = 2, Stopped = 3 }

    /// <summary>稼働中の個体。設定するのは TryStartRuntime を成功させた本人だけ、消すのはその本人の Shutdown だけ。</summary>
    public static ChildUdpRuntimeHost Running { get; private set; }

    public RuntimeState State { get; private set; } = RuntimeState.Created;
    public bool IsConfigured => State == RuntimeState.Configured || State == RuntimeState.Running;
    public bool IsRunning => State == RuntimeState.Running;

    /// <summary>送信メッセージの接頭辞（旧側と同じ値。通信形式を変えない）。</summary>
    private const string MagicNumber = "TEAM13_";

    /// <summary>開始コマンド（旧側 CMD_START と同じ値）。</summary>
    private const string StartCommand = "START_GAME";

    // ── 所有（Initialize でのみ生成する。Awake では生成しない）──────────────────
    private ChildUdpRuntimeHostSettings _settingsFallback;
    private Func<ChildUdpRuntimeHostSettings> _settingsProvider;
    private Func<bool> _showDebugProvider;

    /// <summary>使用時点の設定（互換窓口の参照があればその現在値、無ければ初期化時の設定）。</summary>
    private ChildUdpRuntimeHostSettings _settings => _settingsProvider != null ? _settingsProvider() : _settingsFallback;

    private UdpChannel _channel;
    private ChildConnectionLink _link;
    private ChildPlaySession _session;
    private ChildNotificationHub _notifications;
    private ChildRankSyncCoordinator _rankSync;

    public ChildConnectionLink Link => _link;
    public ChildPlaySession Session => _session;
    public ChildNotificationHub Notifications => _notifications;
    public ChildRankSyncCoordinator RankSync => _rankSync;
    public ChildUdpRuntimeHostSettings Settings => _settings;

    /// <summary>診断出力の有無（設定の現在値を都度読む。開始時固定しない）。</summary>
    private bool ShowDebug => _showDebugProvider != null ? _showDebugProvider() : (_settings != null && _settings.ShowDebugLogs);

    /// <summary>診断出力の現在値を取得する入口（切替時に互換窓口の現在値を返す Func を渡す）。</summary>
    public void BindShowDebugProvider(Func<bool> provider) => _showDebugProvider = provider;

    /// <summary>設定の参照（使用時点の現在値を返す入口）を差し込む（値の複製は行わない）。</summary>
    public void BindSettingsProvider(Func<ChildUdpRuntimeHostSettings> provider) => _settingsProvider = provider;

    // ── メインスレッドへの受け渡し（ポンプ用キュー。旧側と同じ順序で使う）────────
    private readonly ConcurrentQueue<string> _receiveQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<UdpChannel.Issue> _issueQueue = new ConcurrentQueue<UdpChannel.Issue>();
    private readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
    private readonly ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();

    // ── Coroutineハンドル（自分が開始したものだけを停止する）────────────────────
    private Coroutine _heartbeatCoroutine;
    private Coroutine _rankSyncResendCoroutine;
    private Coroutine _discoveryCoroutine;

    // 送信の再送（SendStateRepeated）は呼び出しごとに増えるため、追跡して終了時にまとめて停止する。
    private readonly List<TrackedCoroutine> _sendResendCoroutines = new List<TrackedCoroutine>();

    private sealed class TrackedCoroutine
    {
        public Coroutine Handle;
    }

    // 子機（このプロセス）が RETURN_TO_TITLE を送るたびに単調増加する連番（実行中はリセットしない）。
    private int _returnToTitleSendSeq = 0;
    // 親機から受信した RETURN_TO_TITLE の、最後に処理した連番（同プレイ内の重複・再送を弾く）。
    private int _lastPeerReturnToTitleSeq = 0;

    // sceneLoaded の購読は明示起動で1回だけ（Initialize では行わない）。
    private bool _sceneLoadedSubscribed;

    // 起動準備（復元・購読・境界リセット）とランキング復元の一度だけ保証。
    private bool _startupPrepared;
    private bool _rankSyncRestored;

    // ── 保存キー（旧側と同じキー文字列。値の出所を変えない）────────────────────
    private const string PrefRankSyncSourceId = "RankSync_SourceId";
    private const string PrefRankSyncUpdateNo = "RankSync_UpdateNo";
    private const string PrefRankSyncConfirmed = "RankSync_Confirmed";
    private const string PrefRankSyncPending = "RankSync_PendingMessage";
    // 世代（sessionSeq）は「起動時に進めて保存」と「今回の4キー保存」で位置が違う（旧側と同じ）。
    private const string PrefRankSyncSessionSeq = "RankSync_SessionSeq";

    // ── ランキング本体（子機の既存キー。Hostは読み取りのみ・複製しない）──────────
    private const string PrefKeyLastGameOverScore = "LastGameOverScore";
    private const string PrefKeyGameOverRankPrefix = "GameOverRank_";
    private const string PrefKeyLastTimeUpScore = "LastTimeUpScore";
    private const string PrefKeyTimeUpRankPrefix = "TimeUpRank_";

    // ── 再送の数値（旧側と同じ値）──────────────────────────────────────────────
    private const float RankSyncResendInterval = 0.5f;
    private const int RankSyncMaxAttempts = 6;   // 0.5s x 6 = 約3秒で一旦停止（保持は継続）

    private struct LogEntry
    {
        public LogType Type;
        public string Message;
        public LogEntry(LogType type, string message) { Type = type; Message = message; }
    }

    // ── 明示的な入口 ───────────────────────────────────────────────────────────
    /// <summary>設定を受け取って「通信を開始してよい前提」を整える（通信はまだ開始しない）。</summary>
    public bool Initialize(ChildUdpRuntimeHostSettings settings, out string error)
    {
        // 二重初期化を許さない（起動時復元の世代+1と担当生成を一度だけにする＝採番を二重にしない）。
        if (State != RuntimeState.Created)
        {
            error = $"Initialize は状態 {State} では受け付けません（初期化は1回だけ）。";
            return false;
        }
        if (settings == null)
        {
            error = "設定が null です。";
            return false;
        }

        _settingsFallback = settings;

        // 担当インスタンスの生成はここだけ（Awake では生成しない＝別保持元を作らない）。
        _link = new ChildConnectionLink();
        _session = new ChildPlaySession();
        _notifications = new ChildNotificationHub();
        _rankSync = new ChildRankSyncCoordinator();

        // 接続担当の状態変化を、Hostの型付き通知出口へ1回だけ転送する（Hubイベントは重ねて発火しない）。
        _link.StateChanged += HandleLinkStateChanged;

        // ランキング復元・sceneLoaded購読・タイトル境界リセットは明示起動（TryStartRuntime）で行う。
        //（旧側 Start の順序＝復元 → 購読 → 境界処理 → bind をそのまま保つ）

        State = RuntimeState.Configured;
        error = "";
        return true;
    }

    /// <summary>
    /// 起動準備（旧側 Start の順序をそのまま）：ランキング復元 → sceneLoaded購読 → タイトル境界リセット。
    ///  ・**一度だけ**実行する（再試行で世代+1・updateNo++・Persist を二重に行わない）。
    ///  ・bind より前に済ませる（復元 → 境界処理 → bind の旧順序を維持）。
    ///  ・起動失敗時はここで行った購読・担当状態を保持したまま false を返す（所有は本個体、解除は ShutdownRuntime）。
    ///  ・ResetForNewSession は副作用のある処理（購読・状態・Coroutine停止）として扱う＝再試行では呼ばない。
    /// </summary>
    private void PrepareStartup()
    {
        if (_startupPrepared) return;
        _startupPrepared = true;

        RestoreRankSyncStateOnce();

        if (!_sceneLoadedSubscribed)
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneLoadedSubscribed = true;
        }

        if (IsTitleScene(SceneManager.GetActiveScene().name))
            ResetForNewSession();
    }

    /// <summary>起動時のランキング復元（一度だけ。再試行で世代・updateNoを二重更新しない）。</summary>
    private void RestoreRankSyncStateOnce()
    {
        if (_rankSyncRestored) return;
        _rankSyncRestored = true;
        RestoreRankSyncState();
    }

    /// <summary>
    /// 直近に受信したメッセージ本文（接頭辞を除いた形。旧側の lastMessage と同じ値）。
    /// 互換フィールドのミラー元として公開する（Host内部の保持元はここだけ）。
    /// </summary>
    public string LastReceivedMessage { get; private set; } = "";

    /// <summary>接続担当の状態変化の転送（同じ状態では StateChanged が発火しない＝二重通知しない）。</summary>
    private void HandleLinkStateChanged(ChildConnectionLink.LinkState state)
        => ConnectionStateChanged?.Invoke(state);

    /// <summary>通信を開始する（明示的な起動）。未設定・二重起動・別個体稼働中は false。</summary>
    public bool TryStartRuntime(out string error)
    {
        if (State == RuntimeState.Running) { error = "既に稼働中です。"; return false; }
        if (!IsConfigured) { error = $"設定が揃っていません（状態 {State}）。"; return false; }
        if (Running != null && Running != this) { error = "別の個体が稼働中です。"; return false; }

        // 旧側 Start と同じ順序で起動準備を行う（一度だけ）：ランキング復元 → sceneLoaded購読 → タイトル境界リセット。
        PrepareStartup();

        UdpChannel channel = new UdpChannel();
        if (!channel.TryOpen(0, _settings.NormalPort, true, out string openError))
        {
            channel.Dispose();   // 自分のソケットだけを閉じる
            error = $"ソケットを開けませんでした: {openError}";
            return false;
        }

        _channel = channel;
        Running = this;
        State = RuntimeState.Running;
        error = "";
        return true;
    }

    /// <summary>
    /// 稼働を終了する（明示的な終了）。
    ///  ・先に「新規操作を受け付けない」状態にしてから、自分が開始したCoroutineを停止し、
    ///    自分のチャネル・キューを解放する。Running 解除は自分が登録個体のときだけ。
    ///  ・外部担当（ResultSceneChamger等）のCoroutineは停止しない。
    /// </summary>
    public void ShutdownRuntime(string reason)
    {
        bool wasActive = State == RuntimeState.Running || State == RuntimeState.Configured;
        State = RuntimeState.Stopped;

        StopOwnCoroutines();

        // 登録解除（購読の解除）と自己リソース解放（チャネル・キュー）を分けて行う。
        if (_link != null)
            _link.StateChanged -= HandleLinkStateChanged;

        if (_sceneLoadedSubscribed)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _sceneLoadedSubscribed = false;
        }

        if (_channel != null)
        {
            _channel.Close();
            _channel.Dispose();
            _channel = null;
        }

        while (_actionQueue.TryDequeue(out _)) { }
        while (_receiveQueue.TryDequeue(out _)) { }
        while (_issueQueue.TryDequeue(out _)) { }
        while (_logQueue.TryDequeue(out _)) { }

        if (Running == this) Running = null;

        if (wasActive)
            Debug.Log($"[ChildUdpRuntimeHost] 稼働を終了しました（理由={reason}）。実行責任は旧側にあります。");
    }

    private void Awake()
    {
        // 意図的に空（ソケットを開かない・復元しない・購読しない・Coroutineを開始しない）。
    }

    private void Update()
    {
        if (!IsRunning) return;   // 未起動では通信・購読・キューに触れない

        // 旧側 Update と同じ順序：基盤から取り込み → 受信処理 → action（接続確定など）→ ログ。
        DrainChannelQueues();
        ProcessReceiveQueue();
        while (_actionQueue.TryDequeue(out Action action)) action();
        ProcessLogQueue();

        // CAUGHT：購読前受信の保留を、ゲーム側の受信担当が準備できたら1回だけ通知する
        //（旧側と同じ位置＝ログ処理の後・タイムアウト判定の前）。
        ConsumePendingCaughtIfReady();

        // タイムアウト（判定は接続担当。子機にはシーン別の例外が無いためそのまま渡す）
        if (_link.IsTimedOut(Time.time, _settings.TimeoutLimit))
        {
            _link.Disconnect();
            Debug.LogWarning("[ChildUdpRuntimeHost] Connection timed out — parent heartbeat lost.");
        }

        TickCoroutineLifecycles();
    }

    /// <summary>
    /// 周期Coroutineの開始・停止（旧側 Update と同じ条件：接続担当の状態で判定する）。
    ///  ・Connecting の間だけ Discovery（0.5秒間隔のブロードキャスト）
    ///  ・Connected の間だけ Heartbeat（実時間間隔のPING）
    /// 各ハンドルは個別に管理し、停止時に必ず null へ戻す。
    /// </summary>
    private void TickCoroutineLifecycles()
    {
        if (_link.IsConnecting && _discoveryCoroutine == null)
            _discoveryCoroutine = StartCoroutine(DiscoveryCoroutine());
        else if (!_link.IsConnecting && _discoveryCoroutine != null)
        {
            StopCoroutine(_discoveryCoroutine);
            _discoveryCoroutine = null;
        }

        if (_link.IsConnected && _heartbeatCoroutine == null)
            _heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
        else if (!_link.IsConnected && _heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }
    }

    private void OnDestroy()
    {
        // 未初期化（Created）の個体は何も所有していないので触らない。
        // 所有済みなら Running 登録の有無に関わらず解放する（Running 解除は本人条件）。
        if (State == RuntimeState.Created) return;
        ShutdownRuntime("破棄");
    }

    /// <summary>自分が開始したCoroutineだけを停止する（外部担当のCoroutineには触れない）。</summary>
    private void StopOwnCoroutines()
    {
        if (_heartbeatCoroutine != null) { StopCoroutine(_heartbeatCoroutine); _heartbeatCoroutine = null; }
        if (_rankSyncResendCoroutine != null) { StopCoroutine(_rankSyncResendCoroutine); _rankSyncResendCoroutine = null; }
        if (_discoveryCoroutine != null) { StopCoroutine(_discoveryCoroutine); _discoveryCoroutine = null; }

        for (int i = 0; i < _sendResendCoroutines.Count; i++)
        {
            if (_sendResendCoroutines[i].Handle != null) StopCoroutine(_sendResendCoroutines[i].Handle);
        }
        _sendResendCoroutines.Clear();
    }

    // ── ポンプ（稼働中だけ動く。旧側 PumpChannelQueues と同じ順序）──────────────
    /// <summary>受信スレッドのキューをメインスレッドへ取り込む（PING 以外のみログへ積む）。</summary>
    private void DrainChannelQueues()
    {
        if (_channel == null) return;

        while (_channel.TryDequeueIssue(out UdpChannel.Issue issue))
            EnqueueLog(LogType.Error, $"[ChildUdpRuntimeHost] Receive error: {issue.Message}");

        while (_channel.TryDequeueReceived(out UdpChannel.Datagram datagram))
        {
            string text = datagram.Text;
            IPEndPoint ep = datagram.RemoteEndPoint;

            if (text != MagicNumber + "PING")
                EnqueueLog(LogType.Log, $"[ChildUdpRuntimeHost] Received: '{text}' from {(ep != null ? ep.Address.ToString() : "unknown")}");

            // 接続確定は旧側と同じく「受信ポンプで action として積み、メインスレッドで適用」する。
            if (text == MagicNumber + "DISCOVERY_ACCEPT")
            {
                string parentIP = ep != null ? ep.Address.ToString() : "";
                EnqueueLog(LogType.Log, $"[ChildUdpRuntimeHost] DISCOVERY_ACCEPT from {parentIP} — now Connected.");
                _actionQueue.Enqueue(() => HandleDiscoveryAccept(parentIP));
            }

            _receiveQueue.Enqueue(text);
        }
    }

    /// <summary>
    /// DISCOVERY_ACCEPT による接続確定（旧側 PumpChannelQueues の action と同じ順序）。
    ///  ・接続先IPを確定 → Connected へ（変化したときだけ StateChanged が1回出る）
    ///  ・最終受信時刻を記録 → ゲームシーン遷移済みフラグを落とす → 接続時スナップショット送信
    /// </summary>
    private void HandleDiscoveryAccept(string parentIP)
    {
        _link.SetTargetIP(parentIP);
        _link.SetState(ChildConnectionLink.LinkState.Connected);
        _link.NoteReceive(Time.time);
        _session.GameSceneLoaded = false;

        SendRankSyncOnConnect();
    }

    /// <summary>取り込んだ受信を順に処理する（Host内部だけが消費する）。</summary>
    private void ProcessReceiveQueue()
    {
        while (_receiveQueue.TryDequeue(out string raw))
            DispatchMessage(raw);
    }

    /// <summary>
    /// 受信ディスパッチ。解析は既存の型（RankSyncChallenge／RankSyncHello／RankingSnapshot）をそのまま使い、
    /// 適用は抽出済み担当（Hub／Coordinator／Session／FallbackPolicy）へ渡す（判定式をコピーしない）。
    /// 分岐の順序は旧側 HandleIncoming と同じ（PING→START_GAME→CAUGHT→SEQUENCE_DONE→RETURN_TO_TITLE→
    /// RANK_SYNC_CHALLENGE→RANK_SYNC_ACK→SLEEP_LOCK→SLEEP_UNLOCK→未処理ログ）。
    /// </summary>
    private void DispatchMessage(string raw)
    {
        // 旧側 HandleIncoming と同じ前提：接頭辞が無い受信は扱わない。
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith(MagicNumber, StringComparison.Ordinal)) return;

        string msg = raw.Substring(MagicNumber.Length);
        LastReceivedMessage = msg;   // 旧側 HandleIncoming の lastMessage 更新と同じ位置

        if (msg == "PING") { _link.NoteReceive(Time.time); return; }

        // ── START_GAME（親機起点の開始）────────────────────────────────────────
        if (msg == StartCommand || msg.StartsWith(StartCommand + ":", StringComparison.Ordinal))
        {
            // 非空IDの付き形式のときだけ受信連番をリセットする（旧側と同じ）。
            string sid = null;
            if (msg.StartsWith(StartCommand + ":", StringComparison.Ordinal))
            {
                sid = msg.Substring(StartCommand.Length + 1);
                if (!string.IsNullOrEmpty(sid)) _lastPeerReturnToTitleSeq = 0;
            }

            bool isNewPlay =
                _session.EvaluateStartGame(sid ?? "", out _) == ChildPlaySession.StartGameBoundary.NewPlay;

            if (isNewPlay)
            {
                _session.CaughtHandled = false;
                ClearPendingCaught("新プレイ開始（START_GAME）");
                ResetCaptureWaitState("新プレイ開始（START_GAME）");
            }

            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] Received START_GAME from parent — loading game scene. playSessionId={_session.PlaySessionId}, isNewPlay={isNewPlay}");

            LoadGameScene();
            return;
        }

        // ── CAUGHT（親機の捕獲）────────────────────────────────────────────────
        if (msg == "CAUGHT") { HandleCaughtReceived(); return; }

        // ── SEQUENCE_DONE（演出完了）───────────────────────────────────────────
        if (msg.StartsWith("SEQUENCE_DONE:", StringComparison.Ordinal))
        {
            // SEQUENCE_DONE:<playSessionId>:<reason>
            string rest = msg.Substring("SEQUENCE_DONE:".Length);
            string[] parts = rest.Split(':');
            string playId = parts.Length > 0 ? parts[0].Trim() : "";
            string reason = parts.Length > 1 ? parts[1].Trim() : "unspecified";

            HandleSequenceDoneMessage(playId, reason);
            return;
        }

        // ── RETURN_TO_TITLE（親機のタイトル復帰）───────────────────────────────
        if (msg.StartsWith("RETURN_TO_TITLE:", StringComparison.Ordinal))
        {
            // RETURN_TO_TITLE:<playSessionId>:<seq>[:FAST]（旧: RETURN_TO_TITLE:<seq>）
            // 3つ目の値は "FAST" のみ許可し、それ以外の追加値は推測で処理せず不正として拒否する。
            string rest = msg.Substring("RETURN_TO_TITLE:".Length);
            string[] parts = rest.Split(':');

            string sid = "";
            string seqText;
            bool fast = false;

            if (parts.Length == 1)
            {
                seqText = parts[0];                        // 旧形式: <seq>（識別子なし）
            }
            else if (parts.Length == 2)
            {
                sid = parts[0];
                seqText = parts[1];                        // <playSessionId>:<seq>
            }
            else if (parts.Length == 3 && parts[2] == "FAST")
            {
                sid = parts[0];
                seqText = parts[1];
                fast = true;                               // <playSessionId>:<seq>:FAST（ボタン起点）
            }
            else
            {
                if (ShowDebug)
                    Debug.LogWarning($"[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — invalid format (only 'FAST' is allowed as 3rd field): '{msg}'");
                return;
            }

            if (!int.TryParse(seqText, out int seq))
            {
                if (ShowDebug)
                    Debug.LogWarning($"[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — could not parse seq: '{seqText}'");
                return;
            }

            HandleTeamReturnToTitle(sid, seq, fast);
            return;
        }

        // 準備済み：RANK_SYNC_CHALLENGE（応答の条件は Coordinator が保持）
        if (msg.StartsWith("RANK_SYNC_CHALLENGE:", StringComparison.Ordinal))
        {
            if (RankSyncChallenge.TryParse(msg, out RankSyncChallenge challenge, out string challengeError))
            {
                _rankSync.SetChallengeId(challenge.ChallengeId);
                if (ShowDebug)
                    Debug.Log($"[ChildUdpRuntimeHost] RANK_SYNC_CHALLENGE received — challenge='{challenge.ChallengeId}'. 接続確認の応答を返します。");

                // 応答（HELLO）を返す。未確認スナップショットがあれば同じ更新番号・同じ内容で再送も行う。
                // 本文・更新番号は接続確認では書き換えない（接続情報は HELLO 側で運ぶ）。
                if (_rankSync.HasPendingMessage) StartRankSyncResend("challenge");
                else SendRankSyncHello();
            }
            else if (ShowDebug)
            {
                Debug.LogWarning($"[ChildUdpRuntimeHost] RANK_SYNC_CHALLENGE ignored — {challengeError}: '{msg}'");
            }
            return;
        }

        // 準備済み：RANK_SYNC_ACK（自分の同期元・自分の更新番号のときだけ確定）
        if (msg.StartsWith("RANK_SYNC_ACK:", StringComparison.Ordinal))
        {
            // RANK_SYNC_ACK:<version>:<sourceId>:<updateNo>
            string[] ackParts = msg.Substring("RANK_SYNC_ACK:".Length).Split(':');

            if (ackParts.Length != 3)
            {
                if (ShowDebug) Debug.LogWarning($"[ChildUdpRuntimeHost] RANK_SYNC_ACK ignored — invalid format: '{msg}'");
                return;
            }

            if (ackParts[0] != RankingSnapshot.VersionName)
            {
                if (ShowDebug) Debug.LogWarning($"[ChildUdpRuntimeHost] RANK_SYNC_ACK ignored — unsupported version: '{ackParts[0]}'");
                return;
            }

            if (!int.TryParse(ackParts[2], out int ackUpdate))
            {
                if (ShowDebug) Debug.LogWarning($"[ChildUdpRuntimeHost] RANK_SYNC_ACK ignored — invalid updateNo: '{ackParts[2]}'");
                return;
            }

            HandleRankSyncAck(ackParts[1], ackUpdate);
            return;
        }

        // ── SLEEP_LOCK／SLEEP_UNLOCK（状態は ChildNotificationHub が保持）─────────
        if (msg == "SLEEP_LOCK")
        {
            if (_notifications.IsSleepLocked)
            {
                if (ShowDebug) Debug.Log("[ChildUdpRuntimeHost] Received redundant SLEEP_LOCK (already locked) — skipped.");
                return;
            }

            _notifications.SetSleepLocked(true);
            if (ShowDebug) Debug.Log("[ChildUdpRuntimeHost] Received SLEEP_LOCK — locking input.");
            return;
        }

        if (msg == "SLEEP_UNLOCK")
        {
            if (!_notifications.IsSleepLocked)
            {
                if (ShowDebug) Debug.Log("[ChildUdpRuntimeHost] Received redundant SLEEP_UNLOCK (already unlocked) — skipped.");
                return;
            }

            _notifications.SetSleepLocked(false);
            if (ShowDebug) Debug.Log("[ChildUdpRuntimeHost] Received SLEEP_UNLOCK — unlocking input.");
            return;
        }

        //  ※ DISCOVERY_ACCEPT は受信ポンプ側で action として適用済み（旧側 HandleIncoming も本文では扱わない）。
        //  ※ RANK_SYNC／CHILD_SCORE の受信処理は旧側に存在しないため追加しない（未処理ログに落とす）。
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] Unhandled message: '{msg}'");
    }

    // ── 送信（旧側 SendState／SendToUdp と同じ宛先・同じ接頭辞）────────────────
    /// <summary>
    /// 1件送信（旧側 SendToUdp と同じ：基盤が未オープンなら例外。呼び出し側の try/catch が受け止める）。
    /// </summary>
    private void SendToUdp(string message, string ip, int port)
    {
        UdpChannel channel = _channel;
        if (channel == null)
            throw new InvalidOperationException("UDP channel is not open");

        byte[] data = Encoding.UTF8.GetBytes(MagicNumber + message);
        channel.Send(data, data.Length, ip, port);
    }

    /// <summary>
    /// 状態文字列の送信（旧側と同じ：接頭辞・宛先は接続先IPと親機の受信ポート・失敗はログ）。
    ///  ・接続状態ガードは持たない（旧側と同じ。送る/送らないは呼び出し側の条件が決める）。
    ///  ・未稼働（起動失敗・停止後）は送らない＝明示終了後に送信を残さない。
    /// </summary>
    public void SendState(string message)
    {
        if (!IsRunning || _channel == null) return;

        string ip = _link != null ? _link.TargetIP : _settings.TargetIP;
        int port = _settings.ParentReceivePort;

        if (ShowDebug) Debug.Log($"[ChildUdpRuntimeHost] → '{message}' to {ip}:{port}");

        try { SendToUdp(message, ip, port); }
        catch (Exception e) { Debug.LogError($"[ChildUdpRuntimeHost] SendState error: {e.Message}"); }
    }

    // ── 接続操作・接続状態の通知出口 ───────────────────────────────────────────
    /// <summary>
    /// 接続状態の型付き通知出口（接続担当が「変化したときだけ」発火する＝二重通知しない）。
    /// 旧側の ConnectionStateChanged に対応する。Hubの既存イベントへは重ねて発火しない。
    /// </summary>
    public event Action<ChildConnectionLink.LinkState> ConnectionStateChanged;

    /// <summary>現在の接続状態（未初期化は Disconnected）。</summary>
    public ChildConnectionLink.LinkState ConnectionState
        => _link != null ? _link.State : ChildConnectionLink.LinkState.Disconnected;

    /// <summary>接続操作の開始（旧側 OnConnectButtonClicked の Connecting 側と同じ）。</summary>
    public void RequestConnect() => _link?.BeginConnecting();

    /// <summary>接続操作のキャンセル（旧側 OnCancelButtonClicked と同じ）。</summary>
    public void CancelConnect() => _link?.Disconnect();

    // ── ゲーム受信：START_GAME（旧側と同じ順序）────────────────────────────────
    /// <summary>
    /// ゲームシーンへの遷移（旧側 LoadGameScene と同じ：二重ロード防止 → 遷移済みを立ててから即時遷移）。
    /// </summary>
    private void LoadGameScene()
    {
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] LoadGameScene ENTER — gameSceneLoaded={_session.GameSceneLoaded}, 遷移先='{_settings.GameSceneName}', 現在scene='{SceneManager.GetActiveScene().name}'");

        // 二重ロード防止。ここで return する場合は遷移しない（判定値も記録する）。
        if (_session.GameSceneLoaded)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] LoadGameScene 中断 — gameSceneLoaded が既に true のため LoadScene しません。");
            return;
        }
        _session.GameSceneLoaded = true;

        ChildSceneTransitionRunner.LoadSceneNow(_settings.GameSceneName);
    }

    // ── ゲーム受信：CAUGHT（旧側と同じ順序・同じ短絡評価）──────────────────────
    /// <summary>
    /// CAUGHT の受理判断（タイトル／結果での破棄 → ロード中の有効ID必須 → 受理済みの重複抑止 → 受理）。
    /// </summary>
    private void HandleCaughtReceived()
    {
        string activeScene = SceneManager.GetActiveScene().name;
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] Received TEAM13_CAUGHT — scene='{activeScene}', GameManager.instance={(GameManager.instance != null ? "present" : "NULL")}.");

        // タイトル・結果シーンではCAUGHTを破棄する（受理済みにしない＝次プレイの捕獲を無効化しない）。
        if (IsTitleScene(activeScene) ||
            activeScene == ChildCaughtFallbackPolicy.GameOverSceneName ||
            activeScene == ChildCaughtFallbackPolicy.GameClearSceneName)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TEAM13_CAUGHT discarded — scene='{activeScene}'（タイトル／結果のため破棄）");
            return;
        }

        // ロード中は「現在プレイが開始済みで有効なプレイIDを持つ場合」だけ受理する。
        if (activeScene == _settings.GameSceneName && string.IsNullOrEmpty(_session.PlaySessionId))
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TEAM13_CAUGHT discarded — scene='{activeScene}'（ロード中かつ有効なプレイIDが無い）");
            return;
        }

        if (_session.CaughtHandled)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TEAM13_CAUGHT ignored — scene='{activeScene}', 既に受理済み（再送を無視）");
            return;
        }

        // 受理済みにしてから通知／フォールバックへ進む（再送はここで弾かれる）。
        HandleCaughtAccepted(activeScene);
    }

    /// <summary>
    /// CAUGHTを受理した（CaughtHandled=true）うえで、
    /// 「購読者へ通知」「GameManager不在時のフォールバック」「1件保留」のいずれか1つだけを行う。
    /// 同じCAUGHTを通知とフォールバックの両方で処理しない（旧側と同じ順序・同じ短絡評価）。
    /// </summary>
    private void HandleCaughtAccepted(string activeScene)
    {
        // 受理済みは「通知やフォールバックの前」に立てる（再送を弾く。保留の消費は妨げない）。
        _session.CaughtHandled = true;

        // 捕獲を受理したので、親機の演出完了まで「演出待機」に入る（通常の寝たふりとは別状態）。
        EnterCaptureWait("CAUGHT受理");

        // 1) ゲーム側の受信担当（GameManager）が準備済みなら通知する。
        if (_notifications.HasCaughtSubscriber)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] CAUGHTを購読者へ通知します（メインスレッド, scene='{activeScene}'）。");
            _notifications.NotifyCaughtNow();
            return;
        }

        // 2) GameManagerが存在しないゲームシーンでは、従来のフォールバックを実行する。
        //    （ロード・タイトル・結果では実行しない＝従来どおり）
        if (IsGameplayScene(activeScene) && GameManager.instance == null)
        {
            RunCaughtFallback(activeScene);
            return;
        }

        // 3) それ以外（ロード中／GameManagerが存在するが購読準備中）は1件保留する。
        EnqueuePendingCaught();
    }

    /// <summary>
    /// 【従来フォールバック】GameManager不在のゲームシーンでの保存・ランキング通知・結果シーン遷移。
    /// 内容と順序は従来どおり（保存 → Save → ランキング通知 → 実遷移）。
    /// </summary>
    private void RunCaughtFallback(string activeScene)
    {
        // フォールバック：既に結果画面やタイトル画面にいる場合の二重ロードを防止
        //（継続／中止の判断は ChildCaughtFallbackPolicy。既存の条件・短絡評価をそのまま移設）
        bool shouldRunFallback = ChildCaughtFallbackPolicy.ShouldRunFallback(activeScene, _settings.TitleSceneName);
        if (shouldRunFallback)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] CAUGHT fallback executed — scene='{activeScene}', loading '{ChildCaughtFallbackPolicy.GameOverSceneName}'.");

            // GameManager.instance が未設定でも、シーン内に実体があればそこから実スコアを取得する。
            // それも見つからない場合のみ 0 点として扱う（最終手段）。
            int finalScore = 0;
            GameManager gm = GameManager.instance != null ? GameManager.instance : FindFirstObjectByType<GameManager>();
            if (gm != null)
            {
                finalScore = gm.score;
            }

            PlayerPrefs.SetInt(PrefKeyLastGameOverScore, finalScore);
            PlayerPrefs.Save();
            NotifyRankingChanged(RankingSnapshot.TypeGameOver);
            ChildSceneTransitionRunner.LoadSceneNow(ChildCaughtFallbackPolicy.GameOverSceneName);
        }
        else if (ShowDebug)
        {
            Debug.Log($"[ChildUdpRuntimeHost] CAUGHT fallback skipped — scene='{activeScene}'.");
        }
    }

    /// <summary>
    /// 購読者が居ない間に受理したCAUGHTを1件だけ保留する（履歴は持たない・再送で増やさない）。
    /// 保留には受理時のプレイ識別子を記録し、同一プレイでのみ消費する。
    /// </summary>
    private void EnqueuePendingCaught()
    {
        if (_notifications.HasPendingCaught)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] CAUGHT は既に1件保留済みのため追加しません（再送は無視）。");
            return;
        }

        _notifications.EnqueuePendingCaught(_session.PlaySessionId ?? "");
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] CAUGHT を1件保留します（playId='{_notifications.PendingCaughtPlayId}'、購読成立後に通知）。");
    }

    /// <summary>CAUGHTの保留を破棄する（次プレイ・非ゲームシーンへ持ち越さない）。</summary>
    private void ClearPendingCaught(string reason)
    {
        if (!_notifications.HasPendingCaught) return;

        _notifications.ClearPendingCaught();
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] CAUGHT の保留を破棄しました（{reason}）。");
    }

    /// <summary>
    /// 購読前受信の保留を、ゲーム側の受信担当が準備できたら1回だけ通知する（旧側 Update と同じ位置・同じ条件）。
    ///  消費条件：子機ゲームシーン ／ 保留したプレイIDが現在プレイIDと一致 ／ 購読者が準備済み。
    /// </summary>
    private void ConsumePendingCaughtIfReady()
    {
        if (!_notifications.HasPendingCaught) return;

        string pendingScene = SceneManager.GetActiveScene().name;

        ChildNotificationHub.CaughtPendingOutcome outcome =
            _notifications.ConsumePendingCaught(
                isNonGameplayScene: IsTitleScene(pendingScene) ||
                                    pendingScene == ChildCaughtFallbackPolicy.GameOverSceneName ||
                                    pendingScene == ChildCaughtFallbackPolicy.GameClearSceneName,
                isGameplayScene: IsGameplayScene(pendingScene),
                currentPlayId: _session.PlaySessionId);

        if (outcome == ChildNotificationHub.CaughtPendingOutcome.Cleared)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] CAUGHT の保留を破棄しました（非ゲームシーンへ移行）。");
        }
        else if (outcome == ChildNotificationHub.CaughtPendingOutcome.Notified)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] 保留していた CAUGHT を購読者へ通知します（メインスレッド・同一プレイ）。");
        }
        // ロード中・プレイID不一致・購読者未準備の場合は保留を維持する（無制限リトライではなく1件のみ）。
    }

    /// <summary>タイトルシーン判定（判定式の保持元は ChildCaughtFallbackPolicy）。</summary>
    private bool IsTitleScene(string sceneName)
        => ChildCaughtFallbackPolicy.IsTitleScene(sceneName, _settings.TitleSceneName);

    /// <summary>子機のゲームシーン（ロード・タイトル・結果以外）判定（判定式の保持元は ChildCaughtFallbackPolicy）。</summary>
    private bool IsGameplayScene(string sceneName)
        => ChildCaughtFallbackPolicy.IsGameplayScene(sceneName, _settings.GameSceneName, _settings.TitleSceneName);

    // ── SEQUENCE_DONE（演出完了）と捕獲待機（旧側と同じ順序）────────────────────
    /// <summary>
    /// 演出完了通知（SEQUENCE_DONE:&lt;playSessionId&gt;:&lt;reason&gt;）を処理する。
    ///  ・前回プレイの遅延通知は無視する（今回の待機を解除しない。ACKも返さない）。
    ///  ・effectivePlayId を決めてから ACK を送る（重複・再送でもACKは返す）。
    ///  ・重複判定と初回受理の記録は Hub が行う（非空ID一致のみ重複、空IDは従来どおり）。
    ///  ・初回受理なら「待機中は解除／未待機は先着保留」を Hub が行う。
    /// </summary>
    private void HandleSequenceDoneMessage(string playId, string reason)
    {
        // 前回プレイの遅延通知（別プレイID）は無視する。ACKも返さない（親機の再送を今回のプレイで止めない）。
        if (!string.IsNullOrEmpty(playId) &&
            !string.IsNullOrEmpty(_session.PlaySessionId) &&
            playId != _session.PlaySessionId)
        {
            Debug.Log($"[ChildUdpRuntimeHost] SEQUENCE_DONE ignored（前回プレイの遅延通知）: playId='{playId}' / current='{_session.PlaySessionId}'");
            return;
        }

        string effectivePlayId = string.IsNullOrEmpty(playId) ? _session.PlaySessionId : playId;

        // 受領確認を返す（再送を止める）。重複時も従来どおりACKを返す。ACKはこの解析経路だけから送る。
        SendState($"SEQUENCE_DONE_ACK:{effectivePlayId}");

        // 重複判定（非空IDかつ処理済みIDと一致する場合だけ重複）と初回受理の記録はハブが行う。
        if (!_notifications.TryAcceptSequenceDone(effectivePlayId))
        {
            Debug.Log($"[ChildUdpRuntimeHost] SEQUENCE_DONE は処理済み（重複・再送）: playId='{effectivePlayId}'（ACKのみ返しました）");
            return;
        }

        if (string.IsNullOrEmpty(effectivePlayId))
        {
            Debug.LogWarning("[ChildUdpRuntimeHost] SEQUENCE_DONE のプレイIDが空です。現在プレイとして扱います");
        }

        // 初回受理後の処理（待機中なら解除／未待機なら先着保留）はハブが行う。診断ログはここで出す。
        string releaseReason = $"演出完了通知を受信（playId='{effectivePlayId}', reason='{reason}'）";
        if (_notifications.HandleSequenceDoneAccepted(effectivePlayId, releaseReason))
        {
            Debug.Log($"[ChildUdpRuntimeHost] 捕獲演出の待機を解除しました（理由={releaseReason}）。");
            return;
        }

        // 捕獲通知（CAUGHT）より先に届いた場合は保留する（待機開始時に即解除する）。
        Debug.Log($"[ChildUdpRuntimeHost] SEQUENCE_DONE を保留しました（捕獲通知より先に受信。待機開始時に解除します）: playId='{effectivePlayId}', reason='{reason}'");
    }

    /// <summary>捕獲を受理したので演出待機に入る（通常の寝たふりとは別の状態として保持する）。</summary>
    private void EnterCaptureWait(string reason)
    {
        // 1) ハブで状態（Waiting・待機ID・待機bool）を更新してから開始通知する（二重開始は既存どおり抑止）。
        //    先着確認（Waiting更新→開始通知→先着確認→Completed更新→解除通知）もハブ内で同じ順序で行う。
        if (!_notifications.BeginCaptureWait(_session.PlaySessionId)) return;

        Debug.Log($"[ChildUdpRuntimeHost] 捕獲演出の待機を開始しました（理由={reason}, playSessionId='{_session.PlaySessionId}'）。" +
                  "結果画面へは演出完了通知を受けてから進みます。");
    }

    /// <summary>演出待機を解除する（結果画面へ進める）。二重に呼んでも1回だけ通知する。</summary>
    private void ReleaseCaptureWait(string reason)
    {
        // 完了扱いは「待機中だったとき」だけ（未捕獲を完了扱いにしない）＝二重解除も抑止。状態更新→通知はハブ。
        if (!_notifications.ReleaseWait(_session.PlaySessionId, reason)) return;

        Debug.Log($"[ChildUdpRuntimeHost] 捕獲演出の待機を解除しました（理由={reason}）。");
    }

    /// <summary>
    /// 【子機側の上限】GameManager の待機タイムアウトから呼ぶ。
    ///  ・待機を解除して「完了済み」にそろえ、入力適用側（ChildSleepLockApplier）に古いロックを残さない。
    ///  ・結果遷移は呼び出し側（GameManager）が行う。この通知だけで遷移しない
    ///    （購読者がいない／タイムアウト経路でも二重に結果処理しない）。
    /// </summary>
    public void ReleaseCaptureWaitByLocalTimeout(string reason)
    {
        // 既に解除済みなら Completed にそろえる（ハブ側）。解除済みの記録ログは従来どおり出す。
        if (!_notifications.ReleaseWaitByLocalTimeout(_session.PlaySessionId, reason))
        {
            Debug.Log($"[ChildUdpRuntimeHost] 子機側の上限で待機終了を記録しました（既に解除済み。理由={reason}）");
        }
    }

    /// <summary>
    /// 待機・完了通知の状態を初期化する（再プレイで残さない）。
    ///  ・リセットではイベントを発火しない（誤って結果遷移を起こさない）。
    /// </summary>
    private void ResetCaptureWaitState(string reason)
    {
        // ハブの待機状態（None・待機ID・待機bool）。解除イベントは発火しない（誤って結果遷移させない）。
        _notifications.ResetWait();
        // 旧側の先着・重複IDは従来の順序のままリセットする（ハブ内の記録をリセット）。
        _notifications.ResetSequenceDoneRecords();
        Debug.Log($"[ChildUdpRuntimeHost] 捕獲演出の待機・完了通知の状態を初期化しました（理由={reason}）。");
    }

    /// <summary>
    /// 現在プレイの演出完了が確定済みか（ゲーム側の初期同期の問い合わせ用）。
    ///  ・Completed のときだけ true（未捕獲 None を完了扱いにしない）。
    ///  ・購読より先に完了通知が処理されていても true になるため取りこぼさない。
    /// </summary>
    public bool IsCaptureWaitCompletedForCurrentPlay => _notifications.IsWaitCompletedForPlay(_session.PlaySessionId);

    /// <summary>購読前に受理したCAUGHTの保留が残っているか（ゲーム側の初期同期の問い合わせ用）。</summary>
    public bool HasPendingCaught => _notifications.HasPendingCaught;

    // ── RETURN_TO_TITLE（親機のタイトル復帰）と境界処理 ─────────────────────────
    /// <summary>
    /// 親機からの「タイトルへ戻る」通知の処理（旧側と同じ条件・同じ順序）。
    ///  ・識別子が現在のプレイと異なる → 別プレイとして無視
    ///  ・連番が最後に処理した連番以下 → 同プレイ内の重複として無視
    ///  ・既にタイトル → 何もしない
    ///  ・リザルト画面に ResultSceneChamger があれば既存のフェード演出を流用（fast は短いフェード）
    ///  ・無ければ即時遷移（タイトル名は既存の解決を通す）
    /// </summary>
    private void HandleTeamReturnToTitle(string playSessionId, int seq, bool fast)
    {
        // 旧フォーマット（識別子なし）は、こちらに識別子が無い場合のみ受け付ける（後方互換）。
        bool sessionMatches = !string.IsNullOrEmpty(playSessionId)
            ? playSessionId == _session.PlaySessionId
            : string.IsNullOrEmpty(_session.PlaySessionId);

        if (!sessionMatches)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — different play session (recv='{playSessionId}', current='{_session.PlaySessionId}').");
            return;
        }

        if (seq <= _lastPeerReturnToTitleSeq)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — stale/duplicate seq={seq} (last processed={_lastPeerReturnToTitleSeq}).");
            return;
        }
        _lastPeerReturnToTitleSeq = seq;

        string activeScene = SceneManager.GetActiveScene().name;
        if (IsTitleScene(activeScene))
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — already on title scene.");
            return;
        }

        ResultSceneChamger changer = FindFirstObjectByType<ResultSceneChamger>();
        if (changer != null)
        {
            if (changer.IsReturningToTitle)
            {
                if (ShowDebug)
                    Debug.Log("[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — already returning to title (fade in progress).");
                return;
            }

            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] RETURN_TO_TITLE received — starting result fade to title (fast={fast}).");
            changer.StartFadeToTitle(notifyPeer: false, skipWaits: fast);
            return;
        }

        Debug.Log($"[ChildUdpRuntimeHost] RETURN_TO_TITLE received — returning to title from scene={activeScene}.");
        ChildSceneTransitionRunner.LoadSceneNowResolved(_settings.TitleSceneName);
    }

    /// <summary>
    /// シーン境界処理（旧側 OnSceneLoaded と同じ分岐。購読は明示起動で1回だけ）。
    ///  ・タイトルシーン → セッションリセット
    ///  ・結果シーン → 未消費のCAUGHT保留だけを破棄（受理済み状態は維持）
    ///  ・ロード中／ゲームシーン → 保留を維持する（購読成立後に消費）
    ///  ※ シーン側の参照取得（sleepingManager 等）は ChildSleepLockApplier 等のシーン担当が行う。
    /// </summary>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Debug.Log($"[ChildUdpRuntimeHost] Scene loaded: '{scene.name}' — refreshing scene references.");

        if (IsTitleScene(scene.name))
        {
            ResetForNewSession();
            Debug.Log($"[ChildUdpRuntimeHost] Title scene loaded ('{scene.name}') — reset state for next round.");
        }
        else if (scene.name == ChildCaughtFallbackPolicy.GameOverSceneName ||
                 scene.name == ChildCaughtFallbackPolicy.GameClearSceneName)
        {
            // 結果シーンでは遅延CAUGHTを二重処理しない。受理済み状態（CaughtHandled）は維持したまま保留だけを破棄する。
            ClearPendingCaught("結果シーン読込");
        }
    }

    /// <summary>
    /// セッション状態のリセット（旧側と同じ順序：接続 → ロード/受理済み → 保留 → 待機 → 睡眠ロック → プレイID → 周期停止）。
    ///  ・待機・完了通知のリセットではイベントを発火しない（誤って結果遷移を起こさない）。
    /// </summary>
    public void ResetForNewSession()
    {
        // 接続状態・最終受信時刻・接続先の初期化は接続担当へ委譲する（順序は従来どおり：状態 → 受信時刻 → IP）。
        _link.ResetForNewSession();
        LastReceivedMessage = "";   // 旧側 ResetForNewSession の lastMessage クリアと同じ位置
        _session.GameSceneLoaded = false;
        _session.CaughtHandled = false;
        // 購読前に受理したCAUGHTの保留もリセットする（次プレイへ持ち越さない）。
        ClearPendingCaught("ResetForNewSession（タイトル復帰）");
        // 捕獲演出の待機・完了状態もリセットする（イベントは発火しない＝結果遷移を起こさない）。
        ResetCaptureWaitState("ResetForNewSession（タイトル復帰）");

        // 睡眠ロックを解除し、購読者がいれば変更を通知する（シーン側が解除を適用できるように）。
        // 購読者が居ない場合もUDP側の保持状態は正しく解除される。
        _notifications.SetSleepLocked(false);

        // プレイ識別子をクリアする（以降に届く前回プレイの RETURN_TO_TITLE は識別子不一致で無視される）。
        _session.PlaySessionId = "";
        _lastPeerReturnToTitleSeq = 0;

        if (_discoveryCoroutine != null) { StopCoroutine(_discoveryCoroutine); _discoveryCoroutine = null; }
        if (_heartbeatCoroutine != null) { StopCoroutine(_heartbeatCoroutine); _heartbeatCoroutine = null; }
    }

    // ── 送信API（旧側と同じ名前・引数・本文・宛先）──────────────────────────────
    /// <summary>
    /// 同じ本文を追加送信する（旧側と同じ：初回送信＋実時間 interval 間隔で extraSends 回）。
    /// 追加送信は追跡して、明示終了時にまとめて停止する（終了後に送信を残さない）。
    /// </summary>
    public void SendStateRepeated(string message, int extraSends = 3, float interval = 0.15f)
    {
        SendState(message);

        if (extraSends <= 0) return;

        TrackedCoroutine tracked = new TrackedCoroutine();
        _sendResendCoroutines.Add(tracked);
        tracked.Handle = StartCoroutine(
            ResendRoutine(message, extraSends, interval, () => _sendResendCoroutines.Remove(tracked)));
    }

    private IEnumerator ResendRoutine(string message, int count, float interval, Action onCompleted)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new WaitForSecondsRealtime(interval);
            SendState(message);
        }

        onCompleted?.Invoke();
    }

    /// <summary>ラウドアイテムを取得したことを親機に送信する（旧側と同じ本文）。</summary>
    public void SendLoudItem()
    {
        SendState("LOUD_ITEM");
        Debug.Log("[ChildUdpRuntimeHost] Sent LOUD_ITEM packet to Parent.");
    }

    /// <summary>
    /// ゲーム進行率（0〜1）を親機に送信する（旧側と同じ：1000倍の整数へ丸める）。
    /// 送信コストを抑えるため、呼び出し側で間隔を制御する（毎フレームは送らない）。
    /// </summary>
    public void SendGameProgress(float progressRate)
    {
        int progressMilli = Mathf.RoundToInt(Mathf.Clamp01(progressRate) * 1000f);
        SendState($"GAME_PROGRESS:{progressMilli}");
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] Sent GAME_PROGRESS:{progressMilli} packet to Parent.");
    }

    /// <summary>
    /// 子機側で「タイトルへ戻る」処理を開始したことを親機へ通知する（旧側と同じ：連番を進めて再送付きで送る）。
    /// 取りこぼすと親機がリザルト画面に取り残されるため、識別子・連番は再送でも同じ値を送る。
    /// </summary>
    public void notifyReturnToTitle(bool fast = false)
    {
        _returnToTitleSendSeq++;

        // ボタン起点は ":FAST" を付けて送る（受信側も短いフェードにする）。
        string message = fast
            ? $"RETURN_TO_TITLE:{_session.PlaySessionId}:{_returnToTitleSendSeq}:FAST"
            : $"RETURN_TO_TITLE:{_session.PlaySessionId}:{_returnToTitleSendSeq}";

        SendStateRepeated(message);
        Debug.Log($"[ChildUdpRuntimeHost] Sent RETURN_TO_TITLE (playSessionId={_session.PlaySessionId}, seq={_returnToTitleSendSeq}, fast={fast}) with retries to parent.");
    }

    /// <summary>
    /// 開始ボタン起点の開始（旧側 OnStartButtonClicked と同じ順序：ID発行 → 連番リセット → 受理済み/保留/待機のリセット
    /// → START_GAME 送信 → ゲームシーンへ遷移）。
    /// </summary>
    public void OnStartButtonClicked()
    {
        // このプレイの識別子を新しく発行し、開始通知に載せて親機と共有する。
        // 以降の RETURN_TO_TITLE はこの識別子で「どのプレイの通知か」を判定する。
        _session.PlaySessionId = Guid.NewGuid().ToString("N");
        _lastPeerReturnToTitleSeq = 0;

        // 子機起点の新しいプレイ開始でも、前プレイの受理済み状態と保留を持ち越さない。
        _session.CaughtHandled = false;
        ClearPendingCaught("新プレイ開始（子機起点）");
        ResetCaptureWaitState("新プレイ開始（子機起点）");

        SendState($"{StartCommand}:{_session.PlaySessionId}");
        Debug.Log($"[ChildUdpRuntimeHost] Sent START_GAME (playSessionId={_session.PlaySessionId}) to parent at {_link.TargetIP}:{_settings.ParentReceivePort}");
        LoadGameScene();
    }

    /// <summary>接続ボタン起点（旧側 OnConnectButtonClicked と同じ分岐：Connected なら開始、そうでなければ接続操作）。</summary>
    public void OnConnectButtonClicked()
    {
        if (ConnectionState == ChildConnectionLink.LinkState.Connected) OnStartButtonClicked();
        else RequestConnect();
    }

    // ── 周期Coroutine（旧側と同じ間隔・同じ宛先・同じ継続条件）──────────────────
    private IEnumerator DiscoveryCoroutine()
    {
        while (_link.IsConnecting)
        {
            try
            {
                SendToUdp(BuildDiscoveryRequest(), "255.255.255.255", _settings.BroadcastPort);
                Debug.Log($"[ChildUdpRuntimeHost] Sent DISCOVERY_REQUEST (broadcast) source='{_rankSync.SourceId}' sessionSeq={_rankSync.SessionSeq}");
            }
            catch (Exception e) { Debug.LogError($"[ChildUdpRuntimeHost] Discovery send error: {e.Message}"); }

            yield return new WaitForSeconds(0.5f);   // 旧側と同じ間隔（scaled time）
        }
    }

    private IEnumerator HeartbeatCoroutine()
    {
        while (_link.IsConnected)
        {
            try { SendToUdp("PING", _link.TargetIP, _settings.ParentReceivePort); }
            catch (Exception e) { Debug.LogError($"[ChildUdpRuntimeHost] Heartbeat error: {e.Message}"); }

            // QTE中などで Time.timeScale=0 でもPINGを止めないため実時間で待つ（旧側と同じ理由）。
            yield return new WaitForSecondsRealtime(_settings.PingInterval);
        }
    }

    /// <summary>接続元識別つき DISCOVERY_REQUEST（旧側と同じ本文。親機が再送と新規接続を区別する）。</summary>
    private string BuildDiscoveryRequest()
        => "DISCOVERY_REQUEST:" + _rankSync.SourceId + ":" + _rankSync.SessionSeq;

    /// <summary>接続確認要求（challengeId）が未受信のときだけ、DISCOVERY_REQUEST を1回送る（旧側と同じ条件）。</summary>
    private void RequestChallengeIfNeeded()
    {
        if (_rankSync.HasChallengeId) return;

        try
        {
            SendToUdp(BuildDiscoveryRequest(), "255.255.255.255", _settings.BroadcastPort);
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] RANK_SYNC_CHALLENGE 未受信のため DISCOVERY_REQUEST を再送（同じ challengeId が再送されます）。");
        }
        catch (Exception e) { Debug.LogError($"[ChildUdpRuntimeHost] Challenge request error: {e.Message}"); }
    }

    // ── ランキング同期：起動時復元（旧側 LoadRankSyncState と同じ順序）──────────
    /// <summary>
    /// 同期元ID・世代・更新番号・確認状態・未確認全文を復元する（Initialize から明示的に呼ぶ）。
    ///  1) sourceId 復元／新規生成 → 2) 世代+1 を保存 → 3) updateNo／confirmed／pending 復元
    ///  → 4) verbatim再送判定（不一致なら新しい更新番号へ移行）→ 5) Persist
    ///  ※ 移行時は Coordinator 側で採番済みなので、本文の作り直しでは採番し直さない（二重採番しない）。
    /// </summary>
    private void RestoreRankSyncState()
    {
        _rankSync.RestoreSourceIdentity(
            PlayerPrefs.GetString(PrefRankSyncSourceId, ""),
            PlayerPrefs.GetInt(PrefRankSyncSessionSeq, 0),
            out bool sourceIdGenerated);

        if (sourceIdGenerated)
            Debug.LogWarning($"[ChildUdpRuntimeHost] RankSync sourceId generated: {_rankSync.SourceId}");

        // 接続確認の世代を進める（起動ごとに +1）。保存位置は旧側と同じ（今回の4キー保存とは別位置）。
        PlayerPrefs.SetInt(PrefRankSyncSessionSeq, _rankSync.SessionSeq);

        bool needsFreshSnapshot = _rankSync.RestorePendingState(
            PlayerPrefs.GetInt(PrefRankSyncUpdateNo, 0),
            PlayerPrefs.GetInt(PrefRankSyncConfirmed, 1) != 0,
            PlayerPrefs.GetString(PrefRankSyncPending, ""));

        if (needsFreshSnapshot)
        {
            // 旧状態（全文保存なし／不一致）からの移行：Coordinatorが進めた新しい更新番号で作り直す。
            BuildAndStorePendingSnapshot("", RankingSnapshot.TypeNone);
            Debug.LogWarning($"[ChildUdpRuntimeHost] RankSync migrated to a new update={_rankSync.UpdateNo} (no valid persisted full text). 古いupdateNoでは送りません。");
        }
        else if (_rankSync.IsConfirmed)
        {
            PlayerPrefs.SetString(PrefRankSyncPending, "");
        }
        else if (_rankSync.HasPendingMessage)
        {
            Debug.LogWarning($"[ChildUdpRuntimeHost] RankSync update={_rankSync.UpdateNo} pending restored verbatim — will resend on connect.");
        }

        PersistRankSyncState();
    }

    // ── ランキング同期：結果更新・接続時のスナップショット生成（旧側と同じ順序）──
    /// <summary>
    /// ランキング／今回スコアが変化したときに最新スナップショットを作成して親機へ送る
    /// （旧側と同じ：採番→本文作成→保存→再送開始。子機側の保存はACKを待たない）。
    /// </summary>
    public void NotifyRankingChanged(string latestResultType)
    {
        string type = string.IsNullOrEmpty(latestResultType) ? RankingSnapshot.TypeNone : latestResultType;
        string latestPlayId = (type == RankingSnapshot.TypeNone) ? "" : (_session.PlaySessionId ?? "");

        if (type != RankingSnapshot.TypeNone && string.IsNullOrEmpty(latestPlayId))
        {
            Debug.LogWarning("[ChildUdpRuntimeHost] NotifyRankingChanged: playId is empty — sending snapshot with latestType=NONE.");
            type = RankingSnapshot.TypeNone;
        }

        _rankSync.BeginNewUpdate();
        BuildAndStorePendingSnapshot(latestPlayId, type);
        PersistRankSyncState();

        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] RankSync update={_rankSync.UpdateNo} latest={type}:{latestPlayId} queued.");

        StartRankSyncResend("changed");
    }

    /// <summary>
    /// 接続完了・再接続時に最新スナップショットを送る（旧側と同じ）。
    /// 未確認があれば「同じ更新番号・同じ内容」を再送し、無ければ表示用の同期（latestType=NONE）を新規作成する。
    /// </summary>
    public void SendRankSyncOnConnect()
    {
        if (_rankSync.HasUnconfirmedPending)
        {
            StartRankSyncResend("reconnect");
            return;
        }

        _rankSync.BeginNewUpdate();
        BuildAndStorePendingSnapshot("", RankingSnapshot.TypeNone);
        PersistRankSyncState();

        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] RankSync update={_rankSync.UpdateNo} sent on connect.");

        StartRankSyncResend("connect");
    }

    /// <summary>子機の既存キー（不変）から送信用スナップショットを組み立て、Coordinatorへ未確認として保持させる。</summary>
    private string BuildAndStorePendingSnapshot(string latestPlayId, string latestResultType)
    {
        int goLast = PlayerPrefs.GetInt(PrefKeyLastGameOverScore, 0);
        int[] goTop = ReadRankingKeys(PrefKeyGameOverRankPrefix);
        int tuLast = PlayerPrefs.GetInt(PrefKeyLastTimeUpScore, 0);
        int[] tuTop = ReadRankingKeys(PrefKeyTimeUpRankPrefix);

        return _rankSync.SetPendingSnapshotFrom(goLast, goTop, tuLast, tuTop, latestPlayId, latestResultType);
    }

    /// <summary>ランキング本体（TOP5）を既存キーから読む（Hostは読み取りのみ・保持しない）。</summary>
    private static int[] ReadRankingKeys(string keyPrefix)
    {
        int[] r = new int[RankingSnapshot.TopSize];
        for (int i = 0; i < r.Length; i++)
            r[i] = PlayerPrefs.GetInt(keyPrefix + i, 0);
        return r;
    }

    // ── ランキング同期：送信・再送（旧側と同じ回数・間隔・上限後の保持）──────────
    /// <summary>ACKが来るまで同じスナップショットを回数・間隔を決めて再送する（旧側と同じ順序）。</summary>
    private void StartRankSyncResend(string reason)
    {
        if (!_rankSync.HasPendingMessage) return;

        // 接続確認の要求が未受信なら要求し直す → HELLO → スナップショット の順（旧側と同じ）。
        RequestChallengeIfNeeded();
        SendRankSyncHello();
        SendState(_rankSync.PendingMessage);

        if (_rankSyncResendCoroutine != null)
            StopCoroutine(_rankSyncResendCoroutine);

        _rankSyncResendCoroutine = StartCoroutine(RankSyncResendRoutine(reason));
    }

    private IEnumerator RankSyncResendRoutine(string reason)
    {
        for (int attempt = 0; attempt < RankSyncMaxAttempts; attempt++)
        {
            yield return new WaitForSecondsRealtime(RankSyncResendInterval);

            if (_rankSync.IsConfirmed || !_rankSync.HasPendingMessage)
            {
                _rankSyncResendCoroutine = null;
                yield break;
            }

            RequestChallengeIfNeeded();
            SendRankSyncHello();
            SendState(_rankSync.PendingMessage);
        }

        // 上限到達。毎フレーム送らない。未確認のまま保持し、再接続時に再送する。
        if (ShowDebug)
            Debug.LogWarning($"[ChildUdpRuntimeHost] RankSync update={_rankSync.UpdateNo} still unconfirmed after {RankSyncMaxAttempts} resends ({reason}) — kept for reconnect resend.");
        _rankSyncResendCoroutine = null;
    }

    /// <summary>接続確認の応答（親機が発行した challengeId をそのまま返す。未受信なら応答しない）。</summary>
    private void SendRankSyncHello()
    {
        if (!_rankSync.HasChallengeId) return;
        SendState(RankSyncHello.Format(_rankSync.ChallengeId, _rankSync.SourceId, _rankSync.SessionSeq));
    }

    /// <summary>親機からの同期確認（ACK）。自分の同期元・自分の更新番号のときだけ確定する（判定は Coordinator）。</summary>
    private void HandleRankSyncAck(string sourceId, int updateNo)
    {
        if (!_rankSync.ShouldAcceptAck(sourceId, updateNo)) return;

        _rankSync.NoteAckAccepted();
        PersistRankSyncState();

        if (_rankSyncResendCoroutine != null) { StopCoroutine(_rankSyncResendCoroutine); _rankSyncResendCoroutine = null; }

        if (ShowDebug) Debug.Log($"[ChildUdpRuntimeHost] RankSync confirmed: update={updateNo}.");
    }

    /// <summary>同期状態の保存（旧側と同じキー・同じ値の出所・同じ位置）。</summary>
    private void PersistRankSyncState()
    {
        PlayerPrefs.SetString(PrefRankSyncSourceId, _rankSync.SourceId);
        PlayerPrefs.SetInt(PrefRankSyncUpdateNo, _rankSync.UpdateNo);
        PlayerPrefs.SetInt(PrefRankSyncConfirmed, _rankSync.IsConfirmed ? 1 : 0);
        PlayerPrefs.SetString(PrefRankSyncPending, _rankSync.PendingMessage ?? "");
        PlayerPrefs.Save();
    }

    /// <summary>別スレッドからログを積む。</summary>
    public void EnqueueLog(LogType type, string message) => _logQueue.Enqueue(new LogEntry(type, message));

    /// <summary>積まれたログをメインスレッドで出力する。</summary>
    private void ProcessLogQueue()
    {
        while (_logQueue.TryDequeue(out LogEntry entry))
        {
            switch (entry.Type)
            {
                case LogType.Warning: Debug.LogWarning(entry.Message); break;
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert: Debug.LogError(entry.Message); break;
                default: Debug.Log(entry.Message); break;
            }
        }
    }
}

/// <summary>
/// ChildUdpRuntimeHostSettings：Host の初期化に渡す最小限の設定受け渡し型。
///
/// 切替時の受け渡し（既定値で代替しない）：
///  ・旧 ChildUdpReceiver の**シリアライズ値（Inspector値）を1対1で写して**渡す。
///  例：NormalPort = receiver.normalPort／BroadcastPort = receiver.broadcastPort／
///      ParentReceivePort = receiver.parentReceivePort／TargetIP = receiver.targetIP／
///      GameSceneName = receiver.gameSceneName／TitleSceneName = receiver.titleSceneName／
///      ShowDebugLogs = receiver.showDebugLogs …
///  ・**再評価が必要な値**（診断出力）は Parent 側と同様、`BindShowDebugProvider(Func&lt;bool&gt;)` の入口から
///    現在値を読む（未接続の現在は設定値にフォールバック）。
///  ・この型は Inspector にシリアライズしない（シーンへ値を複製しない＝新しい保持元を作らない）。
/// </summary>
public sealed class ChildUdpRuntimeHostSettings
{
    /// <summary>親機の受信ポート（子機の送信先。旧側の normalPort と同じ意味）。</summary>
    public int NormalPort = 8000;

    /// <summary>探索用ブロードキャストポート（旧側と同じ既定値）。</summary>
    public int BroadcastPort = 8001;

    /// <summary>子機の受信ポート（旧側の parentReceivePort と同じ意味）。</summary>
    public int ParentReceivePort = 8002;

    /// <summary>親機のIP（旧側の targetIP と同じ意味。DISCOVERY_ACCEPT で更新される想定）。</summary>
    public string TargetIP = "127.0.0.1";

    /// <summary>PING間隔（秒。旧側と同じ既定値）。</summary>
    public float PingInterval = 1.0f;

    /// <summary>タイムアウト（秒。旧側と同じ既定値）。</summary>
    public float TimeoutLimit = 3.0f;

    /// <summary>診断出力（再評価値。`BindShowDebugProvider` で現在値を接続する）。</summary>
    public bool ShowDebugLogs = true;

    /// <summary>子機のゲームシーン名（旧側のシリアライズ値を写す）。</summary>
    public string GameSceneName = "ChildLoading";

    /// <summary>子機のタイトルシーン名（旧側のシリアライズ値を写す）。</summary>
    public string TitleSceneName = "ChildeTitle";
}
