using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// ParentUdpRuntimeHost：親機UDPランタイムの「実行所有者」になるための準備クラス。
///
/// ★今回の段階では非稼働（5a）：
///  ・シーンへ配置しない／旧側から AddComponent で生成しない／RuntimeInitializeOnLoadMethod を使わない／
///    自動生成の static getter を持たない。
///  ・明示的な入口（Initialize → TryStartRuntime → ShutdownRuntime）を呼ばない限り、
///    ソケット・受信スレッド・sceneLoaded購読・Coroutine・PlayerPrefs復元は一切始まらない。
///  ・Awake は意図的に空。未起動の Update／OnDestroy は通信・購読に触れない。
///  ・**実行責任は引き続き旧 ParentUdpSender が持つ**（本クラスは稼働していない）。
///
/// 将来このクラスが所有するもの（切替時に旧側から移す）：
///  ・UdpChannel（ソケット・受信スレッド）と、受信・ログキューのポンプ（メインスレッドへの受け渡し）
///  ・Coroutine の起動・停止（heartbeat／CAUGHT再送／SEQUENCE_DONE再送／RETURN_TO_TITLE通知）
///  ・SceneManager.sceneLoaded の購読・解除と境界処理本体（OnSceneLoaded の各担当リセット）
///  ・抽出済み担当の保持：ParentConnectionLink／ParentPlaySession／ParentNotificationHub／ParentRankSyncCoordinator
///  ・シーン遷移の実行依頼（既存 ParentSceneTransitionRunner をそのまま使う）
///
/// このクラスが持たないもの（再実装しない）：
///  ・シーン分類（ParentSceneBoundaryPolicy）／結果遷移の可否（RankSyncNavigation）／遷移の実行（Runner）／
///    同期・表示・接続の判断（各Coordinator・Link・Hub）
///  ・通信メッセージの解析（ParentUdpMessageParser）とゲーム処理（切替時に旧側から移す。ここで書き直さない）
///
/// 互換計画（切替時に旧側で行う）：
///  ・旧 public API・enum（ParentUdpSender.ConnectionState）・イベントの型と名前を維持する
///    （consumer の参照型は変更しない。Host 側の新しい enum へ一括変更しない）。
///  ・static event ParentUdpSender.ResultDataCommitted の実発火元は切替時に一方だけにする
///    （Host が発火元になり旧側は発火しない。両方が発火する状態を作らない）。
///  ・Inspector の既定値で既存シーンの値を代替しない。切替時は旧コンポーネントのシリアライズ値を
///    ParentUdpRuntimeHostSettings へ1対1で写して Initialize に渡す。
/// </summary>
public sealed class ParentUdpRuntimeHost : MonoBehaviour
{
    /// <summary>ライフサイクル状態（明示的な入口だけで遷移する）。</summary>
    public enum RuntimeState
    {
        /// <summary>生成直後（設定も通信も無い）。</summary>
        Created = 0,

        /// <summary>設定済み（通信はまだ開始していない）。</summary>
        Configured = 1,

        /// <summary>稼働中（ソケット・購読・Coroutineが生きている）。</summary>
        Running = 2,

        /// <summary>停止済み（自分の所有リソースは閉じた。再初期化しない）。</summary>
        Stopped = 3,
    }

    /// <summary>稼働中の個体。設定するのは TryStartRuntime を成功させた本人だけ、消すのはその本人の Shutdown だけ。</summary>
    public static ParentUdpRuntimeHost Running { get; private set; }

    /// <summary>現在のライフサイクル状態。</summary>
    public RuntimeState State { get; private set; } = RuntimeState.Created;

    /// <summary>設定済みか（通信を開始してよい前提が揃っているか）。</summary>
    public bool IsConfigured => State == RuntimeState.Configured || State == RuntimeState.Running;

    /// <summary>稼働中か。</summary>
    public bool IsRunning => State == RuntimeState.Running;

    // ── 所有（Initialize でのみ生成する。Awake では生成しない）──────────────────
    private ParentUdpRuntimeHostSettings _settingsFallback;        // 初期化時に渡された設定（フォールバック）
    private Func<ParentUdpRuntimeHostSettings> _settingsProvider;  // 使用時点の現在値を返す参照（切替時に互換窓口が接続）

    /// <summary>
    /// 使用時点の設定。互換窓口の参照があればその現在値、無ければ初期化時の設定を返す。
    ///  ・値の複製や状態を持たない（毎フレーム全設定をコピーする方式にしない）。
    ///  ・ソケットbindに使う値は起動時点の値（自動再bindは行わない）。送信・判定等はこの参照を都度読む。
    /// </summary>
    private ParentUdpRuntimeHostSettings _settings => _settingsProvider != null ? _settingsProvider() : _settingsFallback;
    private UdpChannel _channel;
    private ParentConnectionLink _link;
    private ParentPlaySession _session;
    private ParentNotificationHub _notifications;
    private ParentRankSyncCoordinator _rankSync;

    /// <summary>接続管理（設定済みのときだけ非null）。</summary>
    public ParentConnectionLink Link => _link;

    /// <summary>プレイセッション管理（設定済みのときだけ非null）。</summary>
    public ParentPlaySession Session => _session;

    /// <summary>通知ハブ（設定済みのときだけ非null）。</summary>
    public ParentNotificationHub Notifications => _notifications;

    /// <summary>ランキング同期の状態保持（設定済みのときだけ非null）。</summary>
    public ParentRankSyncCoordinator RankSync => _rankSync;

    /// <summary>設定（設定済みのときだけ非null）。</summary>
    public ParentUdpRuntimeHostSettings Settings => _settings;

    // ── メインスレッドへの受け渡し（ポンプ用キュー）────────────────────────────
    //  受信スレッド（UdpChannel）→ここへ取り込み→メインスレッド（Update）で取り出す。
    //  内容の解釈（マジック判定・解析・ゲーム処理）は切替時に旧側から移す（この段階では行わない）。
    private readonly ConcurrentQueue<string> _receiveQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<UdpChannel.Issue> _issueQueue = new ConcurrentQueue<UdpChannel.Issue>();
    private readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();

    /// <summary>送信メッセージの接頭辞（旧側と同じ値。通信形式を変えない）。</summary>
    private const string MagicNumber = "TEAM13_";

    // ── 保存（旧側と同じキー。値の出所と形式を変えない）────────────────────────
    private const string PrefRankSyncData = "RankSync_Data";        // 子機から受信した最新スナップショット
    private const string PrefRankSyncAt = "RankSync_ReceivedAt";    // 鮮度表示用（Unix秒）

    // ── 受信ディスパッチ用のアクションキュー（旧側と同じ：Discovery受理は Update 後段で実行）──
    private readonly ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();

    /// <summary>チャレンジ再送Coroutineのハンドル（自分のものだけを停止する）。</summary>
    private Coroutine _challengeResendCoroutine;

    // ── 周期処理・境界処理の実行用状態（旧側だけに残っている実行用の値。将来Hostが所有する）──
    private Coroutine _heartbeatCoroutine;          // 接続中のPING送信
    private bool _sceneLoadedSubscribed;            // sceneLoaded を購読しているか
    private bool _sequenceDoneAcked;                // SEQUENCE_DONE のACKを受領済みか
    private bool _returningToTitle;                 // タイトル復帰中（キー長押しの多重起動防止）
    private float _returnToTitleHeld;               // キー長押しの累積時間
    private int _lastPeerReturnToTitleSeq;          // 子機から受けた RETURN_TO_TITLE の最終連番
    private Coroutine _caughtRetryCoroutine;        // CAUGHTの短時間再送
    private Coroutine _sequenceDoneRetryCoroutine;  // 演出完了通知のACKまで再送
    private Coroutine _returnToTitleNotifyCoroutine;// 子機へのタイトル復帰通知の再送
    private int _returnToTitleSendSeq;              // 親機が RETURN_TO_TITLE を送るたびに単調増加する連番

    // Hostが開始する遷移Coroutine（ゲーム開始・フェード遷移）。既存経路では複数同時に走り得るため
    // 単一フィールドで上書きせず、リストで追跡する（重複抑止は追加しない＝既存挙動のまま）。
    private readonly List<Coroutine> _transitionCoroutines = new List<Coroutine>();

    /// <summary>診断出力の現在値を取得する入口（互換窓口の現在値を読む。未接続なら設定値を使う）。</summary>
    private Func<bool> _showDebugProvider;

    /// <summary>
    /// 診断出力の有無を取得する入口を差し込む（切替時に互換窓口の現在値を返す Func を渡す）。
    ///  ・この段階では旧側と接続しない（渡されなければ設定値を使う）。
    ///  ・「コピーを都度読むだけ」であり、元設定の再評価済みを意味しない。
    /// </summary>
    public void BindShowDebugProvider(Func<bool> provider) => _showDebugProvider = provider;

    /// <summary>
    /// 設定の参照（使用時点の現在値を返す入口）を差し込む。切替時に互換窓口の現在値を返す Func を渡す。
    ///  ・渡さない場合は Initialize に渡された設定を使う（非稼働準備では未接続）。
    ///  ・戻り値は同じ設定型の参照でよく、状態・副作用を持たない（値の複製は行わない）。
    ///  ・旧側のシリアライズ値へ接続するのは切替バッチ（この入口に旧コンポーネントの現在値を返す Func を渡す）。
    /// </summary>
    public void BindSettingsProvider(Func<ParentUdpRuntimeHostSettings> provider) => _settingsProvider = provider;

    /// <summary>ログ用エントリ（旧側と同じ形。出力先の判断だけを持ち、ゲーム判断は持たない）。</summary>
    private struct LogEntry
    {
        public LogType Type;
        public string Message;

        public LogEntry(LogType type, string message)
        {
            Type = type;
            Message = message;
        }
    }

    // ── Unity lifecycle（この段階では何もしない）──────────────────────────────
    private void Awake()
    {
        // 意図的に空。
        //  ・ソケットを開かない／復元しない／購読しない／Coroutineを開始しない。
        //  ・DontDestroyOnLoad も行わない（シーン配置と重複個体の扱いは切替時に決める）。
    }

    private void Update()
    {
        // 未設定・未稼働のときは通信・購読・キューに一切触れない。
        if (!IsRunning) return;

        DrainChannelQueues();

        // 旧側と同じ順序：Discovery受理アクション → 通常メッセージ → ログ。
        while (_actionQueue.TryDequeue(out Action action))
            action();

        ProcessReceiveQueue();
        ProcessLogQueue();

        // ── 周期処理（旧側 Update と同じ順序・同じ評価時点）──────────────────────
        HandleReturnToTitleKey();

        // 保留（購読準備前の受信）の消費：条件は呼び出し直前に評価し、消費はハブが行う
        ParentNotificationHub.PendingOutcome loudPendingOutcome =
            _notifications.ConsumePendingLoudItem(() => ParentSceneBoundaryPolicy.IsGameplayScene(SceneManager.GetActiveScene().name, _settings.GameplaySceneName));
        if (ShowDebug && loudPendingOutcome == ParentNotificationHub.PendingOutcome.Notified)
            Debug.Log("[ParentUdpRuntimeHost] 保留していた LOUD_ITEM を購読者へ通知します（メインスレッド）。");

        //  ※LOUD_ITEM の通知でシーンが変わる可能性があるため、ここで判定し直す（冒頭のboolを使い回さない）
        ParentNotificationHub.PendingOutcome progressPendingOutcome =
            _notifications.ConsumePendingGameProgress(() => ParentSceneBoundaryPolicy.IsGameplayScene(SceneManager.GetActiveScene().name, _settings.GameplaySceneName));
        if (ShowDebug && progressPendingOutcome == ParentNotificationHub.PendingOutcome.Notified)
            Debug.Log("[ParentUdpRuntimeHost] 保留していた GAME_PROGRESS を購読者へ通知します（メインスレッド・最新値のみ）。");

        // タイムアウト（判定は接続担当、シーンによる例外は親機側で評価して渡す）
        if (_link.IsTimedOut(Time.time, _settings.TimeoutLimit))
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (ParentSceneBoundaryPolicy.IsLoadingScene(sceneName, _settings.GameSceneName))
            {
                // ロード中は接続を維持する（誤タイムアウトを避ける）
                _link.NoteReceive(Time.time);
            }
            else
            {
                _link.Disconnect();
                Debug.LogWarning("[ParentUdpRuntimeHost] Connection timed out — child heartbeat lost.");
            }
        }

        // Heartbeat の起動／停止（接続状態に追従）
        if (_link.IsConnected && _heartbeatCoroutine == null)
        {
            _heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
        }
        else if (!_link.IsConnected && _heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }
    }

    private void OnDestroy()
    {
        // 自分の所有リソースは、Running 登録の有無に関わらず解放する。
        //  ・初期化のみ／起動失敗／停止済みの個体も、自分の Link 購読・チャネル・キューを残さない。
        //  ・Running の解除だけは ShutdownRuntime 内で「自分が登録個体のとき」に限定する。
        //  ・他個体のリソース（チャネル・Running）には触れない。
        ShutdownRuntime("破棄");
    }

    // ── 明示的な入口 ───────────────────────────────────────────────────────────
    /// <summary>
    /// 設定を受け取って「通信を開始してよい前提」を整える（通信はまだ開始しない）。
    ///  ・設定が揃っていなければ false（ソケットも購読も始めない）。
    ///  ・稼働中・停止済みでは受け付けない（true を返した設定だけが有効）。
    /// </summary>
    public bool Initialize(ParentUdpRuntimeHostSettings settings, out string error)
    {
        if (State == RuntimeState.Running || State == RuntimeState.Stopped)
        {
            error = $"Initialize は状態 {State} では受け付けません（稼働中・停止済みは再初期化しない）。";
            return false;
        }

        if (settings == null)
        {
            error = "設定が null です。";
            return false;
        }

        // 設定の妥当性検証は行わない（旧 ParentUdpSender に検証が無く、拒否条件を追加しないため）。
        //  値の不備は旧側と同じ経路（TryOpen の失敗／シーン読込の失敗）として現れる。

        _settingsFallback = settings;

        // 担当インスタンスの生成はここだけ（Awake では生成しない）。
        //  ・切替時は旧側の担当インスタンスを破棄し、Host側の1つだけが生存する（新旧で二重保持しない）。
        _link = new ParentConnectionLink();
        _session = new ParentPlaySession();
        _notifications = new ParentNotificationHub();
        _rankSync = new ParentRankSyncCoordinator();

        // 接続状態の変化は自分の Link から受け取る（旧側 HandleLinkStateChanged と同じ役割）。
        //  ・同値では Link 側が発火しないため、通知回数は旧側と同じ（変化したときだけ1回）。
        _link.StateChanged += HandleLinkStateChanged;

        State = RuntimeState.Configured;
        error = "";
        return true;
    }

    /// <summary>
    /// 通信を開始する（明示的な起動）。
    ///  ・未設定なら false（設定が揃う前に通信を開始しない）。
    ///  ・自分が既に稼働中なら false（同じ個体への二重起動を防ぐ）。
    ///  ・別個体が稼働中なら false（稼働個体は常に1つ。相手のリソースには触れない）。
    ///  ・ソケットを開けなかった場合は自分のソケットだけを閉じて false。
    ///  ・旧実装へフォールバックする二重起動方式（Running の有無で旧側を動かす等）は行わない。
    /// </summary>
    public bool TryStartRuntime(out string error)
    {
        if (State == RuntimeState.Running)
        {
            error = "既に稼働中です（同じ個体への二重起動は行いません）。";
            return false;
        }

        if (!IsConfigured)
        {
            error = $"設定が揃っていません（状態 {State}）。Initialize を先に成功させてください。";
            return false;
        }

        if (Running != null && Running != this)
        {
            error = "別の個体が稼働中です（稼働個体は1つだけ。相手のリソースには触れません）。";
            return false;
        }

        UdpChannel channel = new UdpChannel();
        if (!channel.TryOpen(_settings.BroadcastPort, _settings.ParentReceivePort, out string openError))
        {
            // 自分のソケットだけを閉じる（他個体・旧側には触れない）。
            channel.Dispose();
            error = $"ソケットを開けませんでした: {openError}";
            return false;
        }

        _channel = channel;

        // 旧側 Start と同じ順序：ソケットを開いた後に、保存済みの表示用コピーを復元する。
        //  ・復元はこの明示的な起動経路からのみ実行する（Awake・Initialize では行わない）。
        RestoreRankSyncFromPrefs();

        Running = this;
        State = RuntimeState.Running;

        // sceneLoaded の購読は、この明示起動の経路だけで行う（未配置・未起動では購読しない）。
        if (!_sceneLoadedSubscribed)
        {
            SceneManager.sceneLoaded += OnSceneLoadedPrepared;
            _sceneLoadedSubscribed = true;
        }
        error = "";
        return true;
    }

    /// <summary>
    /// Hostが開始する遷移Coroutineを登録して起動する（追跡を失わないようリストで保持する）。
    ///  ・本体（フェード遷移・ゲーム開始ルーチン）を入れ子で実行し、自然終了したら追跡から外す。
    ///  ・停止は自分が開始したものだけを対象にする（外部担当のCoroutineは触らない）。
    /// </summary>
    private Coroutine StartTrackedTransition(IEnumerator routine)
    {
        Coroutine handle = null;

        IEnumerator Tracked()
        {
            yield return routine;                     // 本体を入れ子で実行（内側のyieldもそのまま流れる）
            if (handle != null) _transitionCoroutines.Remove(handle);
        }

        handle = StartCoroutine(Tracked());
        _transitionCoroutines.Add(handle);
        return handle;
    }

    /// <summary>自分が開始した遷移Coroutineだけを停止して追跡を解除する（StopAllCoroutinesは使わない）。</summary>
    private void StopTransitionCoroutines()
    {
        for (int i = 0; i < _transitionCoroutines.Count; i++)
        {
            Coroutine handle = _transitionCoroutines[i];
            if (handle != null) StopCoroutine(handle);   // 完了済みへのStopは no-op
        }
        _transitionCoroutines.Clear();
    }

    /// <summary>
    /// 稼働を終了する（明示的な終了）。
    ///  ・自分の所有リソース（ソケット・キュー）だけを処理する。
    ///  ・Running を消すのは自分が稼働個体のときだけ（重複個体は消さない）。
    ///  ・旧側のリソースには触れない。
    /// </summary>
    public void ShutdownRuntime(string reason)
    {
        // 1) 先に「新規操作を受け付けない」状態にする。
        //    以降の外部操作・送信は IsRunning=false で早期returnし、新しい遷移も開始されない（Stateは冪等）。
        bool firstShutdown = State != RuntimeState.Stopped;
        State = RuntimeState.Stopped;

        // 2) 自分が開始したCoroutineを個別に停止する（遷移Coroutineを含む）。
        StopTransitionCoroutines();

        // 自分のCoroutineだけを停止する（他個体のCoroutineには触れない）。
        if (_challengeResendCoroutine != null)
        {
            StopCoroutine(_challengeResendCoroutine);
            _challengeResendCoroutine = null;
        }

        if (_heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }

        // 明示終了では自分が開始したCoroutineをすべて個別に停止する（旧側 OnDestroy の対象に sequenceDoneRetry を加えたもの）。
        //  ※StopAllCoroutines は使わない（外部担当のCoroutineを巻き込まない）。
        if (_caughtRetryCoroutine != null)
        {
            StopCoroutine(_caughtRetryCoroutine);
            _caughtRetryCoroutine = null;
        }

        if (_returnToTitleNotifyCoroutine != null)
        {
            StopCoroutine(_returnToTitleNotifyCoroutine);
            _returnToTitleNotifyCoroutine = null;
        }

        if (_sequenceDoneRetryCoroutine != null)
        {
            StopCoroutine(_sequenceDoneRetryCoroutine);
            _sequenceDoneRetryCoroutine = null;
        }

        // 自分の sceneLoaded 購読だけを解除する。
        if (_sceneLoadedSubscribed)
        {
            SceneManager.sceneLoaded -= OnSceneLoadedPrepared;
            _sceneLoadedSubscribed = false;
        }

        // 自分の Link の購読を解除する。
        if (_link != null) _link.StateChanged -= HandleLinkStateChanged;

        // ソケットは自分のものだけを閉じる。
        if (_channel != null)
        {
            _channel.Close();
            _channel.Dispose();
            _channel = null;
        }

        // 取り込み済みの分は破棄する（次の個体へ持ち越さない）。
        while (_actionQueue.TryDequeue(out _)) { }
        while (_receiveQueue.TryDequeue(out _)) { }
        while (_issueQueue.TryDequeue(out _)) { }
        while (_logQueue.TryDequeue(out _)) { }

        if (Running == this) Running = null;

        if (firstShutdown)
        {
            Debug.Log($"[ParentUdpRuntimeHost] 稼働を終了しました（理由={reason}）。実行責任は旧側にあります。");
        }
    }

    // ── シーン遷移の実行依頼（既存 Runner の再利用。判断はここに持たない）────────
    /// <summary>
    /// フェード付き遷移の実行を既存 ParentSceneTransitionRunner へ依頼する。
    ///  ・yield・待機・フェード順序は Runner 側の既存実装のまま（ここで書き直さない）。
    ///  ・診断ログの有無は「依頼時点の設定値」を取得する最小の Func で渡す（旧側と同じ評価時点）。
    ///  ・Coroutine の起動・停止は呼び出し側（切替後は Host、現状は旧側）が行う。
    /// </summary>
    public IEnumerator LoadSceneAfterBgmFade(string sceneName)
    {
        return ParentSceneTransitionRunner.LoadSceneAfterBgmFade(
            sceneName,
            () => _settings != null && _settings.ShowDebugLogs,
            this,
            $"host={name}, sessionId={(_session != null ? _session.PlaySessionId : "")}");
    }

    // ── ポンプ（稼働中だけ動く）──────────────────────────────────────────────
    /// <summary>
    /// 受信スレッドのキューをメインスレッドへ取り込む（旧側 DrainChannelQueues と同じ順序・同じ間引き）。
    ///  ・Discovery経路：受信ログ＋種別判定（DISCOVERY_REQUEST の実行は**未準備**＝下で明示）
    ///  ・通常経路：PING 以外をログへ積み、受信キューへ渡す（処理は同じフレーム内＝遅延を増やさない）
    ///  ・通信エラー：旧側と同じ文言でログへ積む
    /// </summary>
    private void DrainChannelQueues()
    {
        if (_channel == null) return;

        while (_channel.TryDequeueReceived(out UdpChannel.Datagram datagram))
        {
            if (datagram.Route == UdpChannelRoute.Discovery)
            {
                EnqueueLog(LogType.Log, $"[ParentUdpRuntimeHost] Broadcast received: '{datagram.Text}' from {datagram.RemoteEndPoint.Address}");

                ParentUdpMessage message = ParentUdpMessageParser.Parse(datagram.Text);
                if (message.Type == ParentMessageType.DiscoveryRequest)
                {
                    string senderIP = datagram.RemoteEndPoint.Address.ToString();
                    EnqueueLog(LogType.Log, $"[ParentUdpRuntimeHost] DISCOVERY_REQUEST from {senderIP} — queuing targetIP update and DISCOVERY_ACCEPT.");

                    // 旧側と同じくアクションキューへ積み、Update 後段（通常メッセージより前）で実行する。
                    _actionQueue.Enqueue(() => HandleDiscoveryRequestPrepared(message.PlaySessionId, message.Score, senderIP));
                }

                continue;
            }

            // PING 以外のメッセージのみログキューへ積む（毎秒のPINGによる文字列生成・ログ出力を抑制）
            if (ParentUdpMessageParser.Parse(datagram.Text).Type != ParentMessageType.Ping)
            {
                EnqueueLog(LogType.Log, $"[ParentUdpRuntimeHost] Normal received: '{datagram.Text}' from {datagram.RemoteEndPoint.Address}");
            }

            _receiveQueue.Enqueue(datagram.Text);
        }

        // 運用中に受信スレッドで起きた通信エラー（停止時に想定内の例外は通信基盤側で除外済み）
        while (_channel.TryDequeueIssue(out UdpChannel.Issue issue))
        {
            string label = issue.Route == UdpChannelRoute.Discovery ? "ReceiveDiscovery" : "ReceiveNormalData";
            EnqueueLog(LogType.Error, $"[ParentUdpRuntimeHost] {label} error: {issue.Message}");
        }
    }

    /// <summary>
    /// 取り込んだ受信を順に処理する（Host内部だけが消費する＝外部取り出しと奪い合わない）。
    /// </summary>
    private void ProcessReceiveQueue()
    {
        while (_receiveQueue.TryDequeue(out string raw))
            DispatchMessage(raw);
    }

    /// <summary>
    /// 受信メッセージのディスパッチ（旧側 HandleIncoming と同じ判定順・同じ時点の生存時刻更新）。
    ///  ・解析は既存 ParentUdpMessageParser をそのまま使う（解析を書き直さない）。
    ///  ・適用は抽出済み担当（Link／Session／Hub／Coordinator／Policy）へ渡す（判定式をコピーしない）。
    ///  ・未知の種別は旧側と同じ「Unhandled message」ログのみ（未準備の業務経路として数えない）。
    /// </summary>
    private void DispatchMessage(string raw)
    {
        // 演出完了通知の受領確認（子機→親機）: 接頭辞で処理する（旧側と同じ順序）。
        if (raw != null)
        {
            const string ackToken = "SEQUENCE_DONE_ACK:";
            int ackIndex = raw.IndexOf(ackToken, StringComparison.Ordinal);
            if (ackIndex >= 0)
            {
                HandleSequenceDoneAckPrepared(raw.Substring(ackIndex + ackToken.Length).Trim());
                return;
            }
        }

        ParentUdpMessage message = ParentUdpMessageParser.Parse(raw);
        if (message.Type == ParentMessageType.Invalid)
        {
            if (!string.IsNullOrEmpty(message.ParseError))
                Debug.LogWarning(message.ParseError);
            return;
        }

        if (message.Type != ParentMessageType.Ping && _settings != null && _settings.ShowDebugLogs)
            Debug.Log($"[ParentUdpRuntimeHost] Dispatch: '{message.RawPayload}' | scene='{SceneManager.GetActiveScene().name}' | loudItemSubscribers={_notifications.HasLoudItemSubscriber} | gameProgressSubscribers={_notifications.HasGameProgressSubscriber} | resultProcessed={_session.ResultProcessed}");

        // 子機から何か届いた＝子機は生きている。PING 以外のメッセージでも生存時刻を更新する（旧側と同じ）。
        _link.NoteReceive(Time.time);

        switch (message.Type)
        {
            case ParentMessageType.Ping:
                // 生存時刻は上の共通処理で更新済み。旧側と同じく、途絶後の接続復帰判定を行う。
                TryRecoverConnectionFromPing();
                break;

            case ParentMessageType.RankSyncHello:
                HandleRankSyncHelloPrepared(message.Hello);
                break;

            case ParentMessageType.LoudItem:
                HandleLoudItemPrepared();
                break;

            case ParentMessageType.GameAudio:
                // 子機の本編音（SE／BGM状態）。受信スレッドから直接呼ばず、この Dispatch（メインスレッド）から通知する。
                HandleGameAudioPrepared(message.GameAudio);
                break;

            case ParentMessageType.GameProgress:
                HandleGameProgressPrepared(message.Score);
                break;

            case ParentMessageType.LoadingComplete:
                HandleLoadingCompletePrepared();
                break;

            case ParentMessageType.StartGame:
                HandleStartGamePrepared(message.PlaySessionId);
                break;

            case ParentMessageType.TimeUp:
            case ParentMessageType.ChildDead:
                HandleTimeUpPrepared(message.RawPayload);
                break;

            case ParentMessageType.TeamReturnToTitle:
                HandleTeamReturnToTitlePrepared(message.PlaySessionId, message.Score, message.ReturnToTitleFast);
                break;

            case ParentMessageType.ChildScore:
                Debug.LogWarning("[ParentUdpRuntimeHost] Legacy CHILD_SCORE received — ignored (RANK_SYNC is authoritative). Both ends must be updated.");
                break;

            case ParentMessageType.Unknown:
                Debug.Log($"[ParentUdpRuntimeHost] Unhandled message: '{message.RawPayload}'");
                break;

            case ParentMessageType.RankingSync:
                HandleRankSyncPrepared(message.Snapshot);
                break;

            default:
                // 旧側と同じ扱い（既知の種別は上のcaseで処理済み。ここへ来るのは未知の種別のみ）
                Debug.Log($"[ParentUdpRuntimeHost] Unhandled message: '{message.RawPayload}'");
                break;
        }
    }

    // ── 残りの受信経路（旧側と同じ条件・同じ順序）────────────────────────────
    /// <summary>
    /// SEQUENCE_DONE_ACK：別プレイのACKは無視し、今回のプレイのACKだけ受理して記録する（旧側と同じ条件・同じログ・同じ記録）。
    /// 戻り値：今回のプレイのACKとして受理したら true（別プレイのACKは false）。判定実装はこの1箇所だけ。
    /// </summary>
    public bool HandleSequenceDoneAckPrepared(string playId)
    {
        if (!string.IsNullOrEmpty(playId) && !string.IsNullOrEmpty(_session.PlaySessionId) && playId != _session.PlaySessionId)
        {
            Debug.Log($"[ParentUdpRuntimeHost] SEQUENCE_DONE_ACK ignored（別プレイのACK）: playId='{playId}' / current='{_session.PlaySessionId}'");
            return false;
        }

        _sequenceDoneAcked = true;
        Debug.Log($"[ParentUdpRuntimeHost] SEQUENCE_DONE_ACK を受信しました（playId='{playId}'）。");
        return true;
    }

    /// <summary>
    /// 演出完了通知（SEQUENCE_DONE）の送信を開始する（旧側と同じ：本文作成→ACKフラグ初期化→既存停止→再送開始→ログ）。
    ///  playId は呼び出し時点の現在プレイ（旧側と同じ取得時点）。
    /// </summary>
    public void NotifyGameOverSequenceCompletedPrepared(string reason)
    {
        if (!IsRunning) return;

        string message = $"SEQUENCE_DONE:{_session.PlaySessionId}:{reason}";
        _sequenceDoneAcked = false;

        if (_sequenceDoneRetryCoroutine != null)
        {
            StopCoroutine(_sequenceDoneRetryCoroutine);
            _sequenceDoneRetryCoroutine = null;
        }

        _sequenceDoneRetryCoroutine = StartCoroutine(SequenceDoneRetryRoutine(message));

        Debug.Log($"[ParentUdpRuntimeHost] 演出完了通知の送信を開始します: '{message}' → {_link.TargetIP}:{_settings.NormalPort}（ACKまで再送）");
    }

    /// <summary>演出完了通知の再送（旧側と同じ：8回・0.25秒、ACK受領で早期終了）。</summary>
    private IEnumerator SequenceDoneRetryRoutine(string message)
    {
        const int retryCount = 8;
        const float interval = 0.25f;

        for (int i = 0; i < retryCount; i++)
        {
            if (_sequenceDoneAcked)
            {
                Debug.Log($"[ParentUdpRuntimeHost] SEQUENCE_DONE のACKを受領したため再送を終了します（送信 {i} 回）。");
                break;
            }

            SendState(message);
            yield return new WaitForSecondsRealtime(interval);
        }

        _sequenceDoneRetryCoroutine = null;
    }

    /// <summary>START_GAME：受理判断は ParentPlaySession。受理時のみ受信記録を初期化して遷移を開始する（旧側と同じ）。</summary>
    private void HandleStartGamePrepared(string playSessionId)
    {
        ParentPlaySession.StartGameDecision startDecision =
            _session.AcceptStartGame(playSessionId, _link.IsConnected);

        if (startDecision == ParentPlaySession.StartGameDecision.AcceptNewPlay ||
            startDecision == ParentPlaySession.StartGameDecision.AcceptRepeatOfAcceptedPlay)
        {
            if (startDecision == ParentPlaySession.StartGameDecision.AcceptNewPlay)
                ResetChildLoadingCompleteRecordPrepared("START_GAME受理");

            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Received START_GAME from child — loading game scene. playSessionId={_session.PlaySessionId}");

            StartTrackedTransition(LoadSceneAfterBgmFade(_settings.GameSceneName));
        }
        else if (ShowDebug)
        {
            Debug.Log($"[ParentUdpRuntimeHost] START_GAME 分岐=開始しない — 条件不成立: gameStarted={_session.GameStarted}, connected={_link.IsConnected}");
        }
    }

    /// <summary>TIME_UP／CHILD_DEAD：結果処理ガードは ParentPlaySession。二重結果では遷移しない（旧側と同じ）。</summary>
    private void HandleTimeUpPrepared(string rawPayload)
    {
        if (!_session.ResultProcessed)
        {
            _session.ResultProcessed = true;
            Debug.Log($"[ParentUdpRuntimeHost] Received {rawPayload} — TIME_UP result. Loading {_settings.TimeUpSceneName}.");
            ParentSceneTransitionRunner.LoadSceneNow(_settings.TimeUpSceneName);
        }
        else if (ShowDebug)
        {
            Debug.Log($"[ParentUdpRuntimeHost] {rawPayload} result ignored — result already processed.");
        }
    }

    /// <summary>
    /// RETURN_TO_TITLE：プレイ識別の照合 → 連番の重複抑止 → タイトル在否 → フェード付き復帰（既存 ResultSceneChamger）／即時復帰。
    /// 連番（_lastPeerReturnToTitleSeq）は旧側だけに残っていた実行用の値で、将来Hostが所有する。
    /// </summary>
    private void HandleTeamReturnToTitlePrepared(string playSessionId, int seq, bool fast)
    {
        bool sessionMatches = !string.IsNullOrEmpty(playSessionId)
            ? playSessionId == _session.PlaySessionId
            : string.IsNullOrEmpty(_session.PlaySessionId);

        if (!sessionMatches)
        {
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RETURN_TO_TITLE ignored — different play session (recv='{playSessionId}', current='{_session.PlaySessionId}').");
            return;
        }

        if (seq <= _lastPeerReturnToTitleSeq)
        {
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RETURN_TO_TITLE ignored — stale/duplicate seq={seq} (last processed={_lastPeerReturnToTitleSeq}).");
            return;
        }
        _lastPeerReturnToTitleSeq = seq;

        string currentScene = SceneManager.GetActiveScene().name;
        if (ParentSceneBoundaryPolicy.IsTitleScene(currentScene, _settings.MotherTitleSceneName, _settings.TitleSceneName))
        {
            if (ShowDebug)
                Debug.Log("[ParentUdpRuntimeHost] RETURN_TO_TITLE ignored — already on title scene.");
            return;
        }

        ResultSceneChamger changer = FindFirstObjectByType<ResultSceneChamger>();
        if (changer != null)
        {
            if (changer.IsReturningToTitle)
            {
                if (ShowDebug)
                    Debug.Log("[ParentUdpRuntimeHost] RETURN_TO_TITLE ignored — already returning to title (fade in progress).");
                return;
            }

            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RETURN_TO_TITLE received — starting result fade to title (fast={fast}).");
            changer.StartFadeToTitle(notifyPeer: false, skipWaits: fast);
            return;
        }

        Debug.Log("[ParentUdpRuntimeHost] RETURN_TO_TITLE received — returning to title immediately.");
        ReturnToTitlePrepared(notifyPeer: false);
    }

    /// <summary>親機をタイトルへ戻す（旧側 ReturnToTitle と同じ：多重起動防止・timeScale・遷移）。</summary>
    public void ReturnToTitlePrepared(bool notifyPeer)
    {
        if (!IsRunning) return;   // 明示起動したHostの実行経路に限定する
        if (_returningToTitle) return;
        _returningToTitle = true;
        _returnToTitleHeld = 0f;

        Debug.Log($"[ParentUdpRuntimeHost] ReturnToTitle — '{SceneManager.GetActiveScene().name}' から '{_settings.MotherTitleSceneName}' へ戻ります。");

        Time.timeScale = 1f;

        if (notifyPeer)
        {
            NotifyReturnToTitleToChildPrepared(fast: false);
        }

        ParentSceneTransitionRunner.LoadSceneNowResolved(_settings.MotherTitleSceneName);
    }

    /// <summary>
    /// 親機起点で「タイトルへ戻る」ことを子機へ通知する（旧側と同じ：連番→本文作成→送信→再送開始）。
    /// </summary>
    public void NotifyReturnToTitleToChildPrepared(bool fast)
    {
        if (!IsRunning) return;

        string message = NotifyReturnToTitleMessagePrepared(fast);
        Debug.Log($"[ParentUdpRuntimeHost] Sent RETURN_TO_TITLE (playSessionId={_session.PlaySessionId}, seq={_returnToTitleSendSeq}, fast={fast}) to child.");

        if (_returnToTitleNotifyCoroutine != null)
        {
            StopCoroutine(_returnToTitleNotifyCoroutine);
        }
        _returnToTitleNotifyCoroutine = StartCoroutine(ReturnToTitleNotifyRoutine(message));
    }

    /// <summary>連番を進めて本文を作り、送信する（旧側 NotifyReturnToTitle と同じ順序・同じ形式）。</summary>
    public string NotifyReturnToTitleMessagePrepared(bool fast)
    {
        _returnToTitleSendSeq++;

        string message = fast
            ? $"RETURN_TO_TITLE:{_session.PlaySessionId}:{_returnToTitleSendSeq}:FAST"
            : $"RETURN_TO_TITLE:{_session.PlaySessionId}:{_returnToTitleSendSeq}";

        SendState(message);
        return message;
    }

    /// <summary>タイトル復帰通知の再送（旧側と同じ：3回・0.15秒、同じ文字列で送る）。</summary>
    private IEnumerator ReturnToTitleNotifyRoutine(string message)
    {
        const int retryCount = 3;
        const float retryInterval = 0.15f;

        // 再送は同じ文字列（同じ識別子・同じ番号・同じ復帰モード）で送る＝受信側は2回目以降を重複として無視できる
        for (int i = 0; i < retryCount; i++)
        {
            yield return new WaitForSecondsRealtime(retryInterval);
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Sending RETURN_TO_TITLE retry ({i + 1}/{retryCount}) — '{message}'");
            SendState(message);
        }

        _returnToTitleNotifyCoroutine = null;
    }

    /// <summary>タイトル復帰キーの長押し（旧側 HandleReturnToTitleKey と同じ判定・同じ評価時点）。</summary>
    private void HandleReturnToTitleKey()
    {
        if (!_settings.EnableReturnToTitleKey || _returningToTitle) return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || ParentSceneBoundaryPolicy.IsTitleScene(
                SceneManager.GetActiveScene().name, _settings.MotherTitleSceneName, _settings.TitleSceneName))
        {
            _returnToTitleHeld = 0f;
            return;
        }

        if (!keyboard[_settings.ReturnToTitleKey].isPressed)
        {
            _returnToTitleHeld = 0f;
            return;
        }

        _returnToTitleHeld += Time.unscaledDeltaTime;
        if (_returnToTitleHeld >= _settings.ReturnToTitleHoldSeconds)
        {
            ReturnToTitlePrepared(notifyPeer: true);
        }
    }

    /// <summary>接続中のPING送信（旧側 HeartbeatCoroutine と同じ：接続中のときだけ、同じ間隔）。</summary>
    private IEnumerator HeartbeatCoroutine()
    {
        while (_link.IsConnected)
        {
            try
            {
                if (_channel != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(MagicNumber + "PING");
                    _channel.Send(data, data.Length, _link.TargetIP, _settings.NormalPort);
                }
            }
            catch (Exception e) { Debug.LogError($"[ParentUdpRuntimeHost] Heartbeat error: {e.Message}"); }

            yield return new WaitForSecondsRealtime(_settings.PingInterval);
        }
    }

    /// <summary>現在プレイの受信記録（ChildLoadingComplete）を初期化する（旧側と同じ：開始受理時・タイトル復帰時）。</summary>
    private void ResetChildLoadingCompleteRecordPrepared(string reason)
    {
        bool previous = _session.SetChildLoadingComplete(false);
        Debug.Log($"[ParentUdpRuntimeHost] 現在プレイの受信記録(ChildLoadingComplete)を初期化 — {previous} → false, 理由={reason}");
    }

    // ── シーン境界（sceneLoaded の購読は TryStartRuntime だけで行う）──────────────
    /// <summary>
    /// シーン読込時の境界処理（旧側 OnSceneLoaded と同じ分岐順・同じリセット順）：
    ///   タイトル → セッション初期化して return ／ 新セッション境界 → 結果ガードと保留を初期化 ／ その他 → 保留のみ初期化。
    /// </summary>
    private void OnSceneLoadedPrepared(Scene scene, LoadSceneMode mode)
    {
        _returningToTitle = false;
        _returnToTitleHeld = 0f;

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] Scene loaded: '{scene.name}' — refreshing scene references.");

        if (ParentSceneBoundaryPolicy.IsTitleScene(scene.name, _settings.MotherTitleSceneName, _settings.TitleSceneName))
        {
            ResetForNewSessionPrepared();
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Title scene loaded ('{scene.name}') — reset state for next round.");
            return;
        }

        if (ParentSceneBoundaryPolicy.IsNewSessionScene(scene.name, _settings.GameSceneName, _settings.SoloGameSceneName))
        {
            _session.ResetResultProcessed();
            _notifications.ClearPending();

            // 旧側と同じ位置でCoroutineを停止する（前プレイの再送を持ち越さない）
            if (_caughtRetryCoroutine != null)
            {
                StopCoroutine(_caughtRetryCoroutine);
                _caughtRetryCoroutine = null;
            }

            if (_sequenceDoneRetryCoroutine != null)
            {
                StopCoroutine(_sequenceDoneRetryCoroutine);
                _sequenceDoneRetryCoroutine = null;
            }

            _sequenceDoneAcked = false;

            if (ShowDebug)
                Debug.Log("[ParentUdpRuntimeHost] resultProcessed reset for new game session.");
        }
        else
        {
            // GameScene 以外へ遷移したときは保留を安全に初期化する
            _notifications.ClearPending();
        }
    }

    /// <summary>
    /// タイトル復帰時のセッション初期化（旧側 ResetForNewSession と同じ：接続→セッション→保留→識別子→Coroutine停止）。
    ///  ※旧側の returnToTitleNotify／sequenceDoneRetry の停止は未準備（Coroutine未準備）。
    /// </summary>
    public void ResetForNewSessionPrepared()
    {
        _link.ResetForNewSession();
        _session.ResetForNewSession();
        ResetChildLoadingCompleteRecordPrepared("タイトル復帰");
        _notifications.ClearPending();
        _lastPeerReturnToTitleSeq = 0;

        if (_heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }

        if (_caughtRetryCoroutine != null)
        {
            StopCoroutine(_caughtRetryCoroutine);
            _caughtRetryCoroutine = null;
        }

        if (_returnToTitleNotifyCoroutine != null)
        {
            StopCoroutine(_returnToTitleNotifyCoroutine);
            _returnToTitleNotifyCoroutine = null;
        }

        if (_sequenceDoneRetryCoroutine != null)
        {
            StopCoroutine(_sequenceDoneRetryCoroutine);
            _sequenceDoneRetryCoroutine = null;
        }

        if (_challengeResendCoroutine != null)
        {
            StopCoroutine(_challengeResendCoroutine);
            _challengeResendCoroutine = null;
        }

        _sequenceDoneAcked = false;

        Debug.Log("[ParentUdpRuntimeHost] ResetForNewSession: session flags cleared.");
    }

    // ── Discovery 受理（旧側の順序をそのまま。実行は Update 後段）────────────────
    /// <summary>
    /// DISCOVERY_REQUEST の受理（旧側アクションと同一順序）：
    ///   接続先確定 → 生存時刻 → 開始済み解除 → ピア検証(OnDiscovery) →
    ///   NewChallenge時のみ検証済みfalse＋通知 → CHALLENGE送信 → NoteChallengeSent → 再送開始 →
    ///   ログ → DISCOVERY_ACCEPT送信 → 接続状態更新（Link、変化時のみ通知）。
    /// </summary>
    private void HandleDiscoveryRequestPrepared(string childSourceId, int childSessionSeq, string senderIP)
    {
        _link.SetTargetIP(senderIP);
        _link.NoteReceive(Time.time);
        _session.GameStarted = false;

        RankSyncPeerGate.DiscoveryResult dr = _rankSync.OnDiscovery(childSourceId, childSessionSeq, out string challengeId);

        if (dr == RankSyncPeerGate.DiscoveryResult.NewChallenge)
        {
            _rankSync.ResetSyncVerifiedForNewChallenge();
            NotifySyncStateChanged();
        }

        if (!string.IsNullOrEmpty(challengeId))
        {
            SendRankSyncChallenge(senderIP, challengeId);
            _rankSync.NoteChallengeSent();
            StartChallengeResend(senderIP, challengeId);
        }

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] Discovery({dr}) — challenge='{challengeId}', confirmed='{_rankSync.ConfirmedPeerSourceId}', childSource='{childSourceId}', sessionSeq={childSessionSeq}.");

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost][Diag] DISCOVERY_REQUEST受信により Connected, targetIP '{_link.TargetIP}'");

        SendDiscoveryAccept(senderIP);

        // 接続状態の確定（同値なら Link が発火しないため、通知は変化時のみ＝旧側と同じ）
        _link.SetState(ParentConnectionLink.LinkState.Connected);
    }

    /// <summary>接続状態の変化を内部通知へ流す（旧側 HandleLinkStateChanged と同じ役割）。</summary>
    private void HandleLinkStateChanged(ParentConnectionLink.LinkState state)
        => RaiseConnectionStateChanged(state);

    /// <summary>
    /// PING再開による接続復帰（旧側 TryRecoverConnectionFromPing と同じ条件）。
    ///  ・タイムアウトで Disconnected になった後のPING再開だけ復帰させる（タイトルでの意図的なCancelは復帰させない）。
    /// </summary>
    private void TryRecoverConnectionFromPing()
    {
        if (_link.State != ParentConnectionLink.LinkState.Disconnected) return;
        if (ParentSceneBoundaryPolicy.IsTitleScene(SceneManager.GetActiveScene().name, _settings.MotherTitleSceneName, _settings.TitleSceneName)) return;

        _link.SetState(ParentConnectionLink.LinkState.Connected);
        Debug.LogWarning("[ParentUdpRuntimeHost] PING resumed after timeout — connection restored.");
    }

    /// <summary>表示状態が変わったときだけ通知する（旧側 NotifySyncStateChanged と同じ順序）。</summary>
    private void NotifySyncStateChanged()
    {
        if (!_rankSync.ShouldNotifySyncState()) return;

        RankSyncDisplayState previous = _rankSync.CommitSyncState();
        RankSyncDisplayState state = _rankSync.GetDisplayState();

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] Sync display state {previous} → {state} — notifying ResultScoreUI.");

        RaiseResultDataCommitted($"sync state {previous} → {state}");
    }

    /// <summary>接続確認の要求（チャレンジ）を子機へ送る（旧側と同じ形式・同じログ）。</summary>
    private void SendRankSyncChallenge(string ip, string challengeId)
    {
        if (!IsRunning || _channel == null) return;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + RankSyncChallenge.Format(challengeId));
            _channel.Send(data, data.Length, ip, _settings.NormalPort);
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Sent RANK_SYNC_CHALLENGE '{challengeId}' to {ip}:{_settings.NormalPort} ({_rankSync.ChallengeSendCount}/{RankSyncPeerGate.MaxChallengeSends}).");
        }
        catch (Exception e) { Debug.LogError($"[ParentUdpRuntimeHost] SendRankSyncChallenge error: {e.Message}"); }
    }

    /// <summary>DISCOVERY_ACCEPT を送る（旧側と同じ形式・同じログ）。</summary>
    private void SendDiscoveryAccept(string ip)
    {
        if (!IsRunning || _channel == null) return;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + "DISCOVERY_ACCEPT");
            _channel.Send(data, data.Length, ip, _settings.NormalPort);
            Debug.Log($"[ParentUdpRuntimeHost] Sent DISCOVERY_ACCEPT to {ip}:{_settings.NormalPort}");
        }
        catch (Exception e) { Debug.LogError($"[ParentUdpRuntimeHost] SendDiscoveryAccept error: {e.Message}"); }
    }

    /// <summary>ACKが来るまで同じチャレンジを再送する（明示起動したHostの実行経路に限定する）。</summary>
    private void StartChallengeResend(string ip, string challengeId)
    {
        if (!IsRunning) return;

        if (_challengeResendCoroutine != null) StopCoroutine(_challengeResendCoroutine);
        _challengeResendCoroutine = StartCoroutine(ChallengeResendRoutine(ip, challengeId));
    }

    /// <summary>再送ループ（旧側と同じ間隔・上限・各時点での評価）。</summary>
    private IEnumerator ChallengeResendRoutine(string ip, string challengeId)
    {
        const float interval = 0.5f;

        while (IsRunning && _rankSync.HasPendingChallenge && _rankSync.PendingPeerChallengeId == challengeId)
        {
            yield return new WaitForSecondsRealtime(interval);

            if (_rankSync.ChallengeExhausted)
            {
                if (ShowDebug)
                    Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC_CHALLENGE '{challengeId}' 自動再送の上限（{RankSyncPeerGate.MaxChallengeSends}回）に達しました。接続確認できていないため同期待ちとして扱います。");
                break;
            }

            SendRankSyncChallenge(ip, challengeId);
            _rankSync.NoteChallengeSent();
        }

        _challengeResendCoroutine = null;
    }

    /// <summary>別スレッドからログを積む（受信スレッドからの積み込み口）。</summary>
    public void EnqueueLog(LogType type, string message) => _logQueue.Enqueue(new LogEntry(type, message));

    /// <summary>積まれたログをメインスレッドで出力する（旧側と同じ役割。文言は切替時に移す）。</summary>
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

    // ── 準備済みの受信経路（抽出済み担当へ委譲。判定式をコピーしない）────────────
    /// <summary>RANK_SYNC_HELLO：接続確認の応答（判定は既存 RankSyncPeerGate。親機側の検証であり子機の ChildRankSyncPolicy とは別）。</summary>
    private void HandleRankSyncHelloPrepared(RankSyncHello hello)
    {
        RankSyncPeerGate.HelloResult r = _rankSync.OnHello(hello.ChallengeId, hello.SourceId, hello.SessionSeq);

        switch (r)
        {
            case RankSyncPeerGate.HelloResult.Accepted:
                Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO accepted — peer sourceId confirmed: '{_rankSync.ConfirmedPeerSourceId}' (challenge='{hello.ChallengeId}', sessionSeq={hello.SessionSeq}).");
                break;
            case RankSyncPeerGate.HelloResult.Duplicate:
                if (ShowDebug)
                    Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO duplicate — peer sourceId stays '{_rankSync.ConfirmedPeerSourceId}'.");
                break;
            case RankSyncPeerGate.HelloResult.StaleResponse:
                Debug.LogWarning($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO ignored — challenge='{hello.ChallengeId}' is not the pending one ('{_rankSync.PendingPeerChallengeId}'). 古い接続の応答は受理しません。");
                break;
            case RankSyncPeerGate.HelloResult.NoPending:
                Debug.LogWarning($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO ignored — no pending challenge (sourceId='{hello.SourceId}').");
                break;
            default:
                Debug.LogWarning($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO invalid — challengeId='{hello.ChallengeId}', sourceId='{hello.SourceId}', sessionSeq={hello.SessionSeq}.");
                break;
        }

        // 旧側と同じ：受理時は表示状態の通知経路（ShouldNotify→Commit→GetDisplayState→通知）を通る
        if (r == RankSyncPeerGate.HelloResult.Accepted)
            NotifySyncStateChanged();
    }

    /// <summary>LOUD_ITEM：購読者有無・保留の判断は既存 ParentNotificationHub（ゲームシーン判定は既存 Policy）。</summary>
    private void HandleLoudItemPrepared()
    {
        if (ShowDebug)
            Debug.Log("[ParentUdpRuntimeHost] Received LOUD_ITEM network packet from Child.");

        ParentNotificationHub.LoudItemOutcome outcome = _notifications.HandleLoudItemReceived(
            () => ParentSceneBoundaryPolicy.IsGameplayScene(SceneManager.GetActiveScene().name, _settings.GameplaySceneName));

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] LOUD_ITEM outcome={outcome}（通知／保留／破棄の判断はハブが行う）。");
    }

    /// <summary>GAME_PROGRESS：進行率の解釈（0〜1への変換）は旧側と同じ。購読者有無・保留は既存ハブ。</summary>
    private void HandleGameProgressPrepared(int rawScore)
    {
        float progressRate = Mathf.Clamp01(rawScore / 1000f);
        _notifications.HandleGameProgressReceived(progressRate);
    }

    /// <summary>
    /// GAME_AUDIO：子機の本編音（SE／BGM状態）。
    ///  ・通信側は受信を通知するだけ。実際の再生は ParentGameScene 側（ParentGameAudioPlayer）が行う。
    ///  ・ゲームシーン以外では通知しない（タイトル・ロード・結果へ音を持ち越さない）。
    ///  ・親機から子機へ再通知しない（音が往復するループを作らない）。
    /// </summary>
    private void HandleGameAudioPrepared(ParentGameAudioEvent evt)
    {
        if (!ParentSceneBoundaryPolicy.IsGameplayScene(SceneManager.GetActiveScene().name, _settings.GameplaySceneName))
        {
            if (ShowDebug)
                Debug.Log("[ParentUdpRuntimeHost] GAME_AUDIO discarded — ゲームシーンではないため再生しません。");
            return;
        }

        _notifications.HandleGameAudioReceived(evt);
    }

    /// <summary>LOADING_COMPLETE：受信記録は ParentPlaySession。通知発生点を通す（旧イベント橋渡しは切替時）。</summary>
    private void HandleLoadingCompletePrepared()
    {
        bool previous = _session.SetChildLoadingComplete(true);
        Debug.Log("[ParentUdpRuntimeHost] Received LOADING_COMPLETE from child. Property set to true.");

        if (!previous)
            RaiseLoadingCompleteReceived();
    }

    // ── 実送信の入口（稼働中のみ。接続済みのときだけ送る条件は旧側と同じ）────────
    /// <summary>
    /// 状態文字列の送信（旧側 SendState と同じ：Connected のときだけ送る・MAGIC接頭辞・失敗はログ）。
    /// 送信先は Link の接続先（TargetIP）と設定の normalPort（旧側と同じ値の出所）。
    /// </summary>
    public void SendState(string message)
    {
        if (!IsRunning || _channel == null) return;   // 未稼働では送らない（Hostの前提）
        if (!_link.IsConnected) return;               // 旧側と同じ条件

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] → '{message}' to {_link.TargetIP}:{_settings.NormalPort} | state={_link.State}");

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + message);
            _channel.Send(data, data.Length, _link.TargetIP, _settings.NormalPort);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ParentUdpRuntimeHost] SendState error: {e.Message}");
        }
    }

    // ── RANK_SYNC（旧側 HandleRankSync と同じ順序）────────────────────────────
    /// <summary>
    /// ランキング／スコアのスナップショット適用（旧側と同一順序）：
    ///   受理拒否→警告のみ／Mismatch→Errorのみ（ACKなし）／Duplicate・Stale→ログ→(Duplicateのみ)検証済み→ACK→通知要否／
    ///   新規適用→保存→ログ→検証済み→結果データ通知→CommitSyncState→遷移判断→実遷移→ACK。
    /// </summary>
    private void HandleRankSyncPrepared(RankingSnapshot snap)
    {
        string activeScene = SceneManager.GetActiveScene().name;
        bool onTitle = ParentSceneBoundaryPolicy.IsTitleScene(activeScene, _settings.MotherTitleSceneName, _settings.TitleSceneName);

        ParentRankSyncCoordinator.SnapshotDecision decision = _rankSync.EvaluateSnapshot(snap, out RankSyncApply result);

        if (decision == ParentRankSyncCoordinator.SnapshotDecision.NotAccepted)
        {
            Debug.LogWarning($"[ParentUdpRuntimeHost] RANK_SYNC ignored — source='{snap.SourceId}' does not match the confirmed peer ('{_rankSync.ConfirmedPeerSourceId}'). 接続確認前・旧同期元のデータは適用しません。");
            return;
        }

        if (result == RankSyncApply.Mismatch)
        {
            Debug.LogError($"[ParentUdpRuntimeHost] RANK_SYNC MISMATCH — source='{snap.SourceId}', update={snap.UpdateNo} は同じ更新番号で内容が違います。適用せずACKも返しません。");
            return;
        }

        if (result == RankSyncApply.Duplicate || result == RankSyncApply.Stale)
        {
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC {result} — source='{snap.SourceId}', update={snap.UpdateNo} (copy stays at update={_rankSync.StoreUpdateNo}).");

            // Duplicate は「同じ保存コピーを今回の接続で確認した」ことなので Current になり得る
            if (result == RankSyncApply.Duplicate) _rankSync.NoteSnapshotVerified();

            SendRankSyncAck(snap);
            NotifySyncStateChanged();
            return;
        }

        PlayerPrefs.SetString(PrefRankSyncData, _rankSync.SerializeForSave());
        PlayerPrefs.SetInt(PrefRankSyncAt, (int)System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        PlayerPrefs.Save();

        Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC {result} — source='{_rankSync.StoreSourceId}', update={_rankSync.StoreUpdateNo}, "
                + $"GO(last={_rankSync.StoreGameOverLastScore}, top=[{string.Join(",", _rankSync.StoreGameOverTop)}]), "
                + $"TU(last={_rankSync.StoreTimeUpLastScore}, top=[{string.Join(",", _rankSync.StoreTimeUpTop)}]), "
                + $"latest={_rankSync.StoreLatestResultType}:{_rankSync.StoreLatestResultPlayId}");

        _rankSync.NoteSnapshotVerified();

        // 結果データ通知 → 表示状態の記録 → 遷移判断 → 実遷移 → ACK の順序（旧側と同じ）
        RaiseResultDataCommitted("RANK_SYNC applied");
        _rankSync.CommitSyncState();

        TryNavigateForSyncedResult(result, onTitle);

        SendRankSyncAck(snap);
    }

    /// <summary>同期結果に「現在のプレイ」の最新結果が含まれるときだけ結果シーンへ進める（判断は既存 RankSyncNavigation）。</summary>
    private void TryNavigateForSyncedResult(RankSyncApply result, bool onTitle)
    {
        bool shouldNavigate = RankSyncNavigation.ShouldNavigate(
            result, onTitle, _rankSync.StoreMatchesConfirmedPeer(),
            _rankSync.StoreLatestResultPlayId, _rankSync.StoreLatestResultType,
            _session.PlaySessionId, _session.CaptureConfirmedPlayId);

        if (!shouldNavigate)
        {
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC no screen transition (apply={result}, onTitle={onTitle}, latest={_rankSync.StoreLatestResultType}:{_rankSync.StoreLatestResultPlayId}, current='{_session.PlaySessionId}').");
            return;
        }

        // 遷移先は設定（旧側の gameOverSceneName／timeUpSceneName のシリアライズ値を写す）
        string targetScene = _rankSync.StoreLatestResultType == RankingSnapshot.TypeGameOver
            ? _settings.GameOverSceneName : _settings.TimeUpSceneName;

        if (SceneManager.GetActiveScene().name != targetScene)
        {
            ParentSceneTransitionRunner.LoadSceneNow(targetScene);
        }
    }

    /// <summary>ACK送信（旧側と同じ形式。Connected のときだけ送る条件は SendState 側）。</summary>
    private void SendRankSyncAck(RankingSnapshot snap)
        => SendState($"RANK_SYNC_ACK:{RankingSnapshot.VersionName}:{snap.SourceId}:{snap.UpdateNo}");

    /// <summary>
    /// 保存済みの表示用コピーを復元する（旧側 LoadRankSyncFromPrefs と同じキー・同じログ）。
    ///  ・呼ぶのは明示的な起動経路（TryStartRuntime）だけ。Awake／Initialize では実行しない。
    /// </summary>
    public void RestoreRankSyncFromPrefs()
    {
        if (_rankSync == null) return;

        if (_rankSync.RestoreFromSaveData(PlayerPrefs.GetString(PrefRankSyncData, "")))
        {
            Debug.Log($"[ParentUdpRuntimeHost] RankSync copy restored: source='{_rankSync.StoreSourceId}', update={_rankSync.StoreUpdateNo}.");
        }
        else if (ShowDebug)
        {
            Debug.Log("[ParentUdpRuntimeHost] RankSync copy: no data (未同期).");
        }
    }

    /// <summary>表示用コピーの受信時刻（旧側 RankSyncReceivedAtUnix と同じキー・同じ既定値）。</summary>
    public int RankSyncReceivedAtUnix => PlayerPrefs.GetInt(PrefRankSyncAt, 0);

    // ── 外部操作の入口（旧側 public API と同じ条件・同じ順序）──────────────────
    /// <summary>接続操作（旧側 OnConnectButtonClicked と同じ：Connecting へ）。</summary>
    public void OnConnectButtonClicked()
    {
        if (!IsRunning) return;   // 明示起動したHostの実行経路に限定する
        ParentConnectionLink.LinkState before = _link.State;
        _link.BeginConnecting();
        Debug.Log($"[ParentUdpRuntimeHost][Diag] OnConnectButtonClicked called — scene='{SceneManager.GetActiveScene().name}', state {before} → Connecting, gameStarted={_session.GameStarted}, listening on broadcastPort={_settings.BroadcastPort}.");
    }

    /// <summary>キャンセル操作（旧側 OnCancelButtonClicked と同じ：Disconnected へ）。</summary>
    public void OnCancelButtonClicked()
    {
        if (!IsRunning) return;   // 明示起動したHostの実行経路に限定する
        Debug.Log($"[ParentUdpRuntimeHost][Diag] OnCancelButtonClicked called — scene='{SceneManager.GetActiveScene().name}', state {_link.State} → Disconnected.");
        _link.Disconnect();
    }

    /// <summary>親機起点の開始（旧側 OnStartButtonClicked → StartGameRoutine と同じ順序）。</summary>
    public void OnStartButtonClicked()
    {
        if (!IsRunning) return;   // 明示起動したHostの実行経路に限定する（未起動では遷移も送信もしない）
        if (_session.GameStarted)
        {
            if (ShowDebug) Debug.Log("[ParentUdpRuntimeHost] OnStartButtonClicked ignored — already started.");
            return;
        }

        _session.GameStarted = true;
        StartTrackedTransition(StartGameRoutinePrepared());
    }

    /// <summary>親機起点の開始本体（旧側 StartGameRoutine と同じ：識別子発行→送信→待機→遷移）。</summary>
    private IEnumerator StartGameRoutinePrepared()
    {
        _session.PlaySessionId = ParentPlaySession.NewPlaySessionId();
        _session.ClearCaptureConfirmed();
        SendState($"START_GAME:{_session.PlaySessionId}");
        Debug.Log($"[ParentUdpRuntimeHost] Sent START_GAME (playSessionId={_session.PlaySessionId}) to child at {_link.TargetIP}:{_settings.NormalPort}");
        yield return new WaitForSeconds(0.1f);
        yield return LoadSceneAfterBgmFade(_settings.GameSceneName);
    }

    /// <summary>ソロ開始（旧側 OnSoloStartButtonClicked と同じ順序。子機接続を待たずに直接ゲームシーンへ）。</summary>
    public void OnSoloStartButtonClicked()
    {
        if (!IsRunning) return;   // 明示起動したHostの実行経路に限定する（未起動では遷移も送信もしない）
        if (_session.GameStarted)
        {
            if (ShowDebug) Debug.Log("[ParentUdpRuntimeHost] OnSoloStartButtonClicked ignored — already started.");
            return;
        }

        _session.GameStarted = true;
        _session.PlaySessionId = ParentPlaySession.NewPlaySessionId();
        _session.ClearCaptureConfirmed();

        Debug.Log($"[ParentUdpRuntimeHost] OnSoloStartButtonClicked — starting without waiting for child connection. playSessionId={_session.PlaySessionId}");

        SendState($"START_GAME:{_session.PlaySessionId}");
        StartTrackedTransition(LoadSceneAfterBgmFade(_settings.SoloGameSceneName));
    }

    /// <summary>SLEEP_LOCK の送信（旧側と同じコマンド文字列）。</summary>
    public void SendStateSLEEP_LOCK() => SendState("SLEEP_LOCK");

    /// <summary>SLEEP_UNLOCK の送信（旧側と同じコマンド文字列）。</summary>
    public void SendStateSLEEP_UNLOCK() => SendState("SLEEP_UNLOCK");

    /// <summary>
    /// 捕獲確定（親機の捕獲によるGame Over確定）。旧側と同じ：結果処理フラグ→捕獲確定プレイID→即時CAUGHT送信。
    ///  ※CAUGHTの再送Coroutineと演出完了通知（NotifyGameOverSequenceCompleted）は未準備（下の一覧）。
    ///  ※旧側の再入ガードは「処理済み かつ 再送Coroutineが生存」だが、Coroutine未準備のため処理済みのみで判定する（差は報告済み）。
    /// </summary>
    public void NotifyGameOverFromParentCatchPrepared()
    {
        if (!IsRunning) return;   // 明示起動したHostの実行経路に限定する

        // 旧側と同じ再入ガード：処理済み かつ 再送Coroutineが生存しているときだけ弾く
        if (_session.ResultProcessed && _caughtRetryCoroutine != null) return;

        _session.ResultProcessed = true;
        _session.ConfirmCaptureForCurrentPlay();

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] NotifyGameOverFromParentCatch: 親機の捕獲によるゲームオーバー確定。CAUGHT送信・再送を開始します。targetIP='{_link.TargetIP}', connectionState={_link.State}");

        // 即時送信
        SendCaughtPrepared();

        // 短時間再送（旧側と同じ：参照の停止・設定位置）
        if (_caughtRetryCoroutine != null)
        {
            StopCoroutine(_caughtRetryCoroutine);
        }
        _caughtRetryCoroutine = StartCoroutine(CaughtRetryRoutine());
    }

    /// <summary>CAUGHTの短時間再送（旧側 CaughtRetryRoutine と同じ回数・間隔・参照解除位置）。</summary>
    private IEnumerator CaughtRetryRoutine()
    {
        const int retryCount = 3;
        const float retryInterval = 0.1f;

        for (int i = 0; i < retryCount; i++)
        {
            yield return new WaitForSecondsRealtime(retryInterval);
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Sending CAUGHT retry ({i + 1}/{retryCount})...");
            SendCaughtPrepared();
        }

        _caughtRetryCoroutine = null;
    }

    /// <summary>CAUGHTの送信（旧側 SendCaughtNotification と同じ検証・同じメッセージ＝MAGIC込み）。</summary>
    private void SendCaughtPrepared()
    {
        const string message = "TEAM13_CAUGHT";

        if (string.IsNullOrWhiteSpace(_link.TargetIP) || !System.Net.IPAddress.TryParse(_link.TargetIP, out _))
        {
            Debug.LogWarning($"[ParentUdpRuntimeHost] CAUGHT send skipped: invalid targetIP='{_link.TargetIP}', message='{message}'");
            return;
        }

        if (_channel == null || !_channel.IsOpen)
        {
            Debug.LogError($"[ParentUdpRuntimeHost] CAUGHT send failed: udp channel is not open, targetIP='{_link.TargetIP}', message='{message}'");
            return;
        }

        Debug.Log($"[ParentUdpRuntimeHost] CAUGHT send attempt: targetIP='{_link.TargetIP}', targetPort={_settings.NormalPort}, message='{message}'");

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            _channel.Send(data, data.Length, _link.TargetIP, _settings.NormalPort);
            Debug.Log($"[ParentUdpRuntimeHost] CAUGHT send succeeded: targetIP='{_link.TargetIP}', targetPort={_settings.NormalPort}, connectionState={_link.State}, message='{message}'");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ParentUdpRuntimeHost] CAUGHT send failed: targetIP='{_link.TargetIP}', targetPort={_settings.NormalPort}, message='{message}', error='{e.Message}'");
        }
    }

    // ── consumerへ返す状態問い合わせ（既存担当の値をそのまま返す。複製しない）────
    /// <summary>接続状態（保持元は ParentConnectionLink）。</summary>
    public ParentConnectionLink.LinkState ConnectionState => _link != null ? _link.State : ParentConnectionLink.LinkState.Disconnected;

    /// <summary>接続済みか（保持元は ParentConnectionLink）。</summary>
    public bool IsConnected => _link != null && _link.IsConnected;

    /// <summary>開始済みか（保持元は ParentPlaySession）。</summary>
    public bool IsGameStarted => _session != null && _session.GameStarted;

    /// <summary>現在プレイのロード完了記録（保持元は ParentPlaySession）。</summary>
    public bool ChildLoadingComplete => _session != null && _session.ChildLoadingComplete;

    /// <summary>
    /// 指定シーンが「現在の設定でのゲームプレイシーン」か（判定は既存 ParentSceneBoundaryPolicy を再利用）。
    ///  ・読み取り専用の問い合わせ。設定の書き込み口・新しい設定保持元は作らない。
    ///  ・設定（GameplaySceneName）の現在値を使う（開始時コピーへ固定しない）。
    /// </summary>
    public bool IsGameplayScene(string sceneName)
        => _settings != null && ParentSceneBoundaryPolicy.IsGameplayScene(sceneName, _settings.GameplaySceneName);

    /// <summary>確定済みの接続相手（保持元は ParentRankSyncCoordinator）。</summary>
    public string ConnectedPeerSourceId => _rankSync != null ? _rankSync.ConfirmedPeerSourceId : "";

    /// <summary>表示用コピーの有無（保持元は ParentRankSyncCoordinator）。</summary>
    public bool HasRankSyncData => _rankSync != null && _rankSync.HasStoreData;

    /// <summary>表示用コピーの更新番号（保持元は ParentRankSyncCoordinator）。</summary>
    public int RankSyncUpdateNo => _rankSync != null ? _rankSync.StoreUpdateNo : 0;

    /// <summary>表示用コピーの同期元（保持元は ParentRankSyncCoordinator）。</summary>
    public string RankSyncSourceId => _rankSync != null ? _rankSync.StoreSourceId : "";

    /// <summary>表示状態（判定は既存 ParentRankSyncCoordinator）。</summary>
    public RankSyncDisplayState GetSyncDisplayState() => _rankSync != null ? _rankSync.GetDisplayState() : RankSyncDisplayState.NoData;

    /// <summary>同期済み結果の取得（判定・戻り値は既存 ParentRankSyncCoordinator のまま）。</summary>
    public bool TryGetSyncedResult(bool gameOver, out int lastScore, out int[] top5)
    {
        if (_rankSync == null)
        {
            lastScore = 0;
            top5 = null;
            return false;
        }

        return _rankSync.TryGetSyncedResult(gameOver, out lastScore, out top5);
    }

    // ── Host内部の通知発生点（旧 static イベントの直接 Invoke はしない）──────────
    /// <summary>通知の種類（旧側が実際に発火しているイベントだけを型で表す。汎用イベント基盤にしない）。</summary>
    public enum RuntimeNotification
    {
        /// <summary>接続状態が変化した（旧 ConnectionStateChanged。Link の StateChanged 経由）。</summary>
        ConnectionStateChanged = 0,

        /// <summary>LOADING_COMPLETE を受理して記録が false→true になった（旧 LoadingCompleteReceived）。</summary>
        LoadingCompleteReceived = 1,

        /// <summary>結果データの表示更新（旧 static ResultDataCommitted。表示状態変化と新規適用の2経路）。</summary>
        ResultDataCommitted = 2,
    }

    // ── 通知の出口（型付き。互換窓口が購読できる最小限）────────────────────────
    /// <summary>接続状態が変化した（旧 ConnectionStateChanged と同じ引数・同じ発生条件＝変化時のみ1回）。</summary>
    public event Action<ParentConnectionLink.LinkState> ConnectionStateChanged;

    /// <summary>子機のロード完了を受理し、記録が false→true になった（旧 LoadingCompleteReceived と同じ）。</summary>
    public event Action LoadingCompleteReceived;

    /// <summary>結果データの確定／更新（旧 static ResultDataCommitted と同じ発生点。表示状態変化と新規適用の2経路）。</summary>
    public event Action ResultDataCommitted;

    /// <summary>診断出力の有無（設定の現在値を都度読む。開始時固定しない）。</summary>
    private bool ShowDebug => _showDebugProvider != null ? _showDebugProvider() : (_settings != null && _settings.ShowDebugLogs);

    /// <summary>通知の発生点を記録する（ログのみ。旧側イベントへの実接続は切替時）。</summary>
    private void LogNotification(RuntimeNotification kind, string detail)
    {
        if (!ShowDebug) return;
        Debug.Log($"[ParentUdpRuntimeHost] 通知: {kind} ({detail}) — 互換窓口への接続は切替時に行います。");
    }

    /// <summary>接続状態変更の通知（型付き値。detail文字列のパースで復元しない）。</summary>
    private void RaiseConnectionStateChanged(ParentConnectionLink.LinkState state)
    {
        LogNotification(RuntimeNotification.ConnectionStateChanged, state.ToString());
        ConnectionStateChanged?.Invoke(state);
    }

    /// <summary>子機ロード完了の通知（false→true のときだけ＝旧側と同じ発生回数）。</summary>
    private void RaiseLoadingCompleteReceived()
    {
        LogNotification(RuntimeNotification.LoadingCompleteReceived, "LOADING_COMPLETE");
        LoadingCompleteReceived?.Invoke();
    }

    /// <summary>結果データ確定／更新の通知（表示状態変化と新規適用の各1回）。</summary>
    private void RaiseResultDataCommitted(string detail)
    {
        LogNotification(RuntimeNotification.ResultDataCommitted, detail);
        ResultDataCommitted?.Invoke();
    }
}

/// <summary>
/// ParentUdpRuntimeHostSettings：Host の初期化に渡す最小限の設定受け渡し型。
///
/// 切替時の受け渡し（既定値で代替しない）：
///  ・**開始時コピーでよい値**：ポート／targetIP／タイムアウト／ピング間隔／シーン名（プレイ中・ロード・ソロ・タイトル・結果）／
///    タイトル復帰キー設定。切替時に旧 ParentUdpSender の**シリアライズ値（Inspector値）を1対1で写す**。
///    例：NormalPort = sender.normalPort／BroadcastPort = sender.broadcastPort／
///        ParentReceivePort = sender.parentReceivePort／TargetIP = sender.targetIP／
///        GameSceneName = sender.gameSceneName／SoloGameSceneName = sender.soloGameSceneName／
///        GameplaySceneName = sender.gameplaySceneName／MotherTitleSceneName = sender.motherTitleSceneName／
///        TitleSceneName = sender.titleSceneName／GameOverSceneName = sender.gameOverSceneName／
///        TimeUpSceneName = sender.timeUpSceneName …
///  ・**再評価が必要な値**：診断出力（旧 showDebugLogs）は Inspector で実行中にも変わり得るため、
///    この型の値へ固定せず ParentUdpRuntimeHost.BindShowDebugProvider(Func&lt;bool&gt;) で
///    **互換窓口の現在値を取得する入口**から読む（未接続の現在は設定値にフォールバック）。
///  ・この型は Inspector にシリアライズしない（シーンへ値を複製しない＝新しい保持元を作らない）。
/// </summary>
public sealed class ParentUdpRuntimeHostSettings
{
    // ── 通信（旧側の public フィールドと同じ意味・同じ既定値）──────────────────
    public int NormalPort = 8000;
    public int BroadcastPort = 8001;
    public int ParentReceivePort = 8002;
    public string TargetIP = "127.0.0.1";

    // ── 接続・タイムアウト（旧側の private 設定と同じ意味・同じ既定値）──────────
    public float TimeoutLimit = 3.0f;
    public float PingInterval = 1.0f;

    // ── 診断 ──────────────────────────────────────────────────────────────────
    public bool ShowDebugLogs = true;

    // ── シーン名（境界処理・遷移で使う。旧側のシリアライズ値を写す）──────────────
    public string GameSceneName = "ParentLoading";
    public string SoloGameSceneName = "ParentGameScene";
    public string GameplaySceneName = "ParentGameScene";
    public string MotherTitleSceneName = "ParentTitle";
    public string TitleSceneName = "ChildTitle";

    // 結果シーン（旧側 gameOverSceneName／timeUpSceneName のシリアライズ値を写す。既定値で代替しない）
    public string GameOverSceneName = "ParentGameOver";
    public string TimeUpSceneName = "ParentGameClear";

    // ── タイトル復帰キー（Unityライフサイクル側の入力。旧側のシリアライズ値を写す）──
    public bool EnableReturnToTitleKey = true;
    public UnityEngine.InputSystem.Key ReturnToTitleKey = UnityEngine.InputSystem.Key.Escape;
    public float ReturnToTitleHoldSeconds = 1.5f;
}
