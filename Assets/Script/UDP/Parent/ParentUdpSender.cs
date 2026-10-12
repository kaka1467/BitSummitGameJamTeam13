using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
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
    private const string MagicNumber = "TEAM13_";
    private const string CmdStart    = "START_GAME";

    /// <summary>
    /// 「タイトルへ戻る」同期通知。親機⇄子機の双方向で同じメッセージを使用する。
    /// 完全なメッセージは "TEAM13_RETURN_TO_TITLE"。SendState は MAGIC_NUMBER("TEAM13_") を
    /// 前置するため、ここでは payload 部分の "RETURN_TO_TITLE" のみを保持する。
    /// </summary>
    private const string parentReturnToTitle = "RETURN_TO_TITLE";
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
    private readonly UdpChannel _udpChannel = new UdpChannel();
    private volatile bool _isRunning = false;

    // ログ用のエントリ構造体
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

    private readonly ConcurrentQueue<Action> _actionQueue  = new ConcurrentQueue<Action>();
    private readonly ConcurrentQueue<string> _receiveQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<LogEntry> _logQueue   = new ConcurrentQueue<LogEntry>();

    private Coroutine _heartbeatCoroutine;
    private Coroutine _caughtRetryCoroutine;
    private Coroutine _returnToTitleNotifyCoroutine;
    // 接続管理（状態遷移・接続先確定の判断・PING復帰・最終受信時刻・タイムアウト判定・開始/キャンセル）は
    // ParentConnectionLink が保持する。実行時の状態の保持元はリンク側で、
    // currentState / targetIP は互換のためのミラー（HandleLinkStateChanged で同期する）。
    private readonly ParentConnectionLink _link = new ParentConnectionLink();
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
    private string _currentPlaySessionId = "";

    // 親機（このプロセス）が RETURN_TO_TITLE を送るたびに単調増加する連番。アプリ実行中は決してリセットしない。
    // 同じプレイ内での重複・再送を弾くために使う（プレイ識別子が一致する場合のみ意味を持つ）。
    private int _returnToTitleSendSeq = 0;
    private int _lastPeerReturnToTitleSeq = 0;

    /// <summary>
    /// 新しいプレイ識別子を発行する。再起動・再接続をまたいでも衝突しないよう、
    /// プロセス起動ごとの一意トークンと実行時カウンタを組み合わせる。
    /// </summary>
    private static string CreatePlaySessionId()
    {
        return $"{System.Guid.NewGuid():N}";
    }

    /// <summary>
    /// 親機が子機から受信した最終スコア（CHILD_SCORE）を PlayerPrefs へ保存し、
    /// ランキング更新まで完了したときに発火する静的イベント。
    /// 結果シーンの表示（ResultScoreUI など）は、このイベントで表示を再読み込みして
    /// 親機のUDP受信・保存完了と結果UIを同期する（子機GameManagerのイベントには依存しない）。
    /// </summary>
    public static event System.Action ResultDataCommitted;
    private bool      _gameStarted        = false;
    private bool      _resultProcessed    = false; // GAME_OVER wins race
    private float     _returnToTitleHeld = 0f;
    private bool      _returningToTitle  = false;
    // ── 子機のロード完了（現在プレイの受信記録）────────────────────────────────
    // LOADING_COMPLETE の受信結果そのもの（＝通信層が受け取った事実）。待機するかどうかの判断はロード側が持つ。
    //   ・購読前に届いた通知も参照できるよう、現在プレイの記録として暫定保持する（読み取り専用）。
    //   ・書き込みは本クラス内のみ（公開setterは無い）。新しいロード処理からは記録を書き換えない。
    //   ・初期化は「プレイの開始を受理した時点（ParentLoadingへ進む前）」と「タイトル復帰時」に行う。
    public  bool      ChildLoadingComplete { get; private set; } = false;

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
    public event System.Action LoudItemReceived;

    // ── GAME_PROGRESS（子機のゲーム進行率）の通知 ─────────────────────────────
    // 受信後の反応（片付け演出の開始条件判定など）はゲームシーン側の購読者が行う。
    // 本クラスは「受信した進行率」だけをメインスレッドで通知する（MotherSuspicionSystemの直接呼び出しは行わない）。
    //  ・進行率の変換・範囲（Clamp01(Score/1000f)）は従来どおり。通信形式は変更しない。
    //  ・ゲームシーン以外の受信は従来どおり破棄する。
    public event System.Action<float> GameProgressReceived;

    // 購読者がまだ居ないときに受信した LOUD_ITEM を1件だけ保留する（購読準備前の受信を落とさない）。
    //   ・消費は Update で「ゲームシーン」かつ「有効な購読者が居る」ことを確認してから1回だけ。
    //   ・シーン読込／タイトル復帰でクリアし、次シーン・次プレイへ持ち越さない。
    private bool      _hasPendingLoudItem = false;

    // ── 片付け演出：ゲーム進行率の保留 ───────────────────────────────────────
    //   購読者（ゲームシーン側）が準備できる前に受信した進行率の「最新値」を1件だけ保持する。
    //   ・履歴は蓄積しない（保留中に新値が届いたら上書き＝常に最新のみ）。
    //   ・消費は Update で「ゲームシーン」かつ「有効な購読者が居る」ことを確認してから1回だけ。
    //   ・シーン読込／タイトル復帰でクリアし、次シーン・次プレイへ持ち越さない。
    private bool      _hasPendingGameProgress = false;
    private float     _pendingGameProgress = 0f;

    // ── 最終結果（CHILD_SCORE）のプレイ紐付け・重複防止 ──────────────────────
    // 「現在のプレイ」または「既知の終了プレイ」の結果だけを受理し、
    // 保存（1プレイにつき1結果・捕獲優先）と画面遷移（現在プレイのみ）を分けて扱う。
    // ── 子機ランキングの表示用コピー ────────────────────────────────────────
    // 親機は独自にランキングを持たない（子機が唯一の基準）。古い ParentResult_* は残すが新表示には使わない。
    private const string PrefRankSyncData = "RankSync_Data";        // 子機から受信した最新スナップショット
    private const string PrefRankSyncAt   = "RankSync_ReceivedAt";  // 鮮度表示用（Unix秒）

    private readonly RankSyncStore _rankSync = new RankSyncStore();
    private readonly RankSyncPeerGate _peerGate = new RankSyncPeerGate();   // 今接続している子機の同期元（接続確認で確定）
    // 今回の接続でスナップショット適用／同一コピー確認まで進んだか（Current 判定に使う）
    private bool _syncVerifiedThisConnection = false;
    // 表示状態が変わったときだけ ResultScoreUI へ更新通知を出すための直前値
    private RankSyncDisplayState _lastSyncDisplayState = RankSyncDisplayState.NoData;
    private Coroutine _challengeResendRoutine;
    private string _captureConfirmedPlayId = "";   // 捕獲（CAUGHT）が確定したプレイID（TIME_UPを最終結果として受理しない）

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
    public bool IsGameStarted => _gameStarted;

    /// <summary>
    /// 接続状態を更新し、変化があった場合だけ通知する。
    ///  ・同値なら何もしない（通知もログも出さない）。
    ///  ・受信スレッドから変更する場合は actionQueue 経由（メインスレッド）で呼ぶこと。
    ///  ・既存の public フィールド currentState は従来どおりの名前・型のまま残す。
    /// </summary>
    /// <summary>
    /// 互換入口：接続状態の設定は接続担当（ParentConnectionLink）へ委譲する。
    ///  ・同じ状態なら何もしない（従来の early return と同じ。イベントも出ない）。
    ///  ・状態が変わったときだけ、リンクの StateChanged 経由で
    ///    currentState（互換フィールド）と ConnectionStateChanged（既存イベント）へ1回だけ転送する。
    /// </summary>
    private void SetConnectionState(ConnectionState next)
    {
        _link.SetState(MapToLinkState(next));
    }

    /// <summary>接続担当の状態変化を、互換フィールドと既存イベントへ1回だけ転送する。</summary>
    private void HandleLinkStateChanged(ParentConnectionLink.LinkState state)
    {
        MirrorConnectionLinkToCompatFields();
        ConnectionStateChanged?.Invoke(currentState);
    }

    /// <summary>
    /// 接続担当の状態を、既存の互換フィールド（currentState／targetIP）へ写す。
    ///  ・イベントは出さない（イベントを出すのは HandleLinkStateChanged だけ＝二重通知しない）。
    /// </summary>
    private void MirrorConnectionLinkToCompatFields()
    {
        currentState = MapLinkState(_link.State);
        targetIP = _link.TargetIP;
    }

    private static ConnectionState MapLinkState(ParentConnectionLink.LinkState state)
    {
        switch (state)
        {
            case ParentConnectionLink.LinkState.Connecting: return ConnectionState.Connecting;
            case ParentConnectionLink.LinkState.Connected:  return ConnectionState.Connected;
            default:                                        return ConnectionState.Disconnected;
        }
    }

    private static ParentConnectionLink.LinkState MapToLinkState(ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connecting: return ParentConnectionLink.LinkState.Connecting;
            case ConnectionState.Connected:  return ParentConnectionLink.LinkState.Connected;
            default:                         return ParentConnectionLink.LinkState.Disconnected;
        }
    }

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

        // Inspectorから読み込んだ接続先を、リンクへ先に渡す（リンクの既定値でInspector値を上書きしない）。
        // その後で互換フィールドへミラーするため、起動時の targetIP / currentState は従来と同じ値になる。
        _link.SeedTargetIP(targetIP);

        // 接続担当の状態変化を、既存の ConnectionState フィールド・イベントへ転送する（1回だけ）。
        _link.StateChanged += HandleLinkStateChanged;
        MirrorConnectionLinkToCompatFields();
    }

    // ── Button callbacks ──────────────────────────────────────────────────────
    public void OnConnectButtonClicked()
    {
        ConnectionState before = currentState;
        SetConnectionState(ConnectionState.Connecting);
        Debug.Log($"[ParentUdpSender][Diag] OnConnectButtonClicked called — scene='{SceneManager.GetActiveScene().name}', " +
                  $"state {before} → Connecting, _gameStarted={_gameStarted}, listening on broadcastPort={broadcastPort}.");
    }
    public void OnCancelButtonClicked()
    {
        Debug.Log($"[ParentUdpSender][Diag] OnCancelButtonClicked called — scene='{SceneManager.GetActiveScene().name}', state {currentState} → Disconnected.");
        SetConnectionState(ConnectionState.Disconnected);
    }
    public void OnStartButtonClicked()
    {
        if (showDebugLogs)
            Debug.Log($"[StartTrace][親機] ParentUdpSender.OnStartButtonClicked ENTER — scene='{SceneManager.GetActiveScene().name}', currentState={currentState}, _gameStarted={_gameStarted}, sessionId={_currentPlaySessionId}, id={GetInstanceID()}", this);

        // 二重開始防止: すでに開始済みなら何もしない（Start/SoloStart を連打しても遷移は1回だけ）。
        if (_gameStarted)
        {
            if (showDebugLogs)
                Debug.Log($"[StartTrace][親機] OnStartButtonClicked 中断 — _gameStarted が既に true のため開始しません。", this);
            return;
        }
        _gameStarted = true;

        // 新しいプレイを受理した時点で、現在プレイの受信記録を初期化する（ParentLoading へ進む前）。
        ResetChildLoadingCompleteRecord("親機START受理");

        if (showDebugLogs)
            Debug.Log($"[StartTrace][親機] OnStartButtonClicked → StartGameRoutine 開始 (遷移先='{gameSceneName}')", this);
        StartCoroutine(StartGameRoutine());
    }

    /// <summary>
    /// 子機の接続状態に関係なく、親機だけでゲームを開始する。
    /// Connect Button の隣などに置く専用ボタンの OnClick から呼ぶ想定。
    /// タイトルBGMのフェードアウトなど、子機起点の開始（START_GAME受信）と同じ後処理を通る。
    /// </summary>
    public void OnSoloStartButtonClicked()
    {
        if (showDebugLogs)
            Debug.Log($"[StartTrace][親機] ParentUdpSender.OnSoloStartButtonClicked ENTER — scene='{SceneManager.GetActiveScene().name}', currentState={currentState}, _gameStarted={_gameStarted}, soloGameSceneName='{soloGameSceneName}', gameSceneName='{gameSceneName}', sessionId={_currentPlaySessionId}, id={GetInstanceID()}", this);

        // 単体開始が押されたことと、その時点の状態を必ず記録する（「Connecting表示」診断用）。
        Debug.Log($"[ParentUdpSender][Diag] OnSoloStartButtonClicked called — scene='{SceneManager.GetActiveScene().name}', " +
                  $"state={currentState}, _gameStarted={_gameStarted}.");

        if (_gameStarted)
        {
            Debug.Log("[ParentUdpSender][Diag] OnSoloStartButtonClicked ignored — already started (_gameStarted=true).");
            if (showDebugLogs)
                Debug.Log($"[StartTrace][親機] OnSoloStartButtonClicked 中断 — _gameStarted が既に true のため開始しません。", this);
            return;
        }
        _gameStarted = true;

        // 単独開始も「新しいプレイの受理」なので、現在プレイの受信記録を初期化する。
        // （単独開始は ParentLoading を経由しないため、この記録は以降ゲーム中まで読まれない）
        ResetChildLoadingCompleteRecord("単独開始受理");

        // 単体開始は親機が起点なので、親機が新しいプレイ識別子を発行する。
        // （子機接続済みで START_GAME を送る場合も、この識別子を共有させる）
        _currentPlaySessionId = CreatePlaySessionId();
        _captureConfirmedPlayId = "";

        Debug.Log($"[ParentUdpSender] OnSoloStartButtonClicked — starting without waiting for child connection. playSessionId={_currentPlaySessionId}");

        // 子機がたまたま接続済みなら合わせて開始通知を送る（未接続時はSendState内で無視される）
        SendState($"{CmdStart}:{_currentPlaySessionId}");

        // SoloStartButton の非表示は TitleMenuHighlight.FlashAndDeactivate 側が
        // フラッシュ演出の完了後に行う（ここで即座に隠すと演出が表示されないため、外してある）。

        // ParentLoading（子機を無期限に待つ）は経由せず、直接ゲームシーンへ遷移する
        if (showDebugLogs)
            Debug.Log($"[StartTrace][親機] OnSoloStartButtonClicked → LoadSceneAfterBgmFade('{soloGameSceneName}') を開始 (currentState={currentState}, sessionId={_currentPlaySessionId}, START_GAME送信先={targetIP}:{normalPort}, id={GetInstanceID()})", this);
        StartCoroutine(LoadSceneAfterBgmFade(soloGameSceneName));
    }

    private IEnumerator StartGameRoutine()
    {
        // 親機が起点の開始でも、プレイ識別子を親機が発行して子機と共有する。
        _currentPlaySessionId = CreatePlaySessionId();
        _captureConfirmedPlayId = "";
        SendState($"{CmdStart}:{_currentPlaySessionId}");
        Debug.Log($"[ParentUdpSender] Sent START_GAME (playSessionId={_currentPlaySessionId}) to child at {targetIP}:{normalPort}");
        yield return new WaitForSeconds(0.1f);
        yield return LoadSceneAfterBgmFade(gameSceneName);
    }

    // タイトルBGMのフェードアウトと画面の暗転フェードを両方走らせる。
    //
    // 重要（ロード画面表示の遅延対策）:
    //   以前は「BGMフェード完了」と「画面フェード完了」の長い方（= BGMの3秒）まで待ってから
    //   遷移していたため、子機が開始しても親機のロード画面（ParentLoading）が出るまで3秒かかっていた。
    //   BGMフェードは音の演出であり、ロード画面の表示を待たせる必要はない。
    //   そのため待機時間は「画面フェード（暗転）の完了」だけを基準にし、BGMフェードは待たない。
    //
    //   TitleBgmFader の AudioSource はタイトルシーン内のオブジェクトにあり、DontDestroyOnLoad ではない。
    //   そのためシーン遷移時に AudioSource ごと破棄され、フェード途中でも安全に終了する（音源リークや例外は無い）。
    //   暗転が完了してから遷移するので、フェード途中で切れるBGMは画面が黒い状態で途切れ、耳障りになりにくい。
    private IEnumerator LoadSceneAfterBgmFade(string sceneName)
    {
        if (showDebugLogs)
            Debug.Log($"[StartTrace][親機] ParentUdpSender.LoadSceneAfterBgmFade ENTER — 遷移先='{sceneName}', 現在scene='{SceneManager.GetActiveScene().name}', currentState={currentState}, sessionId={_currentPlaySessionId}, id={GetInstanceID()}", this);
        TitleBgmFader bgmFader = FindFirstObjectByType<TitleBgmFader>();
        TitleScreenFader screenFader = FindFirstObjectByType<TitleScreenFader>();

        // BGMフェードは開始するだけ（待たない）。シーン遷移で AudioSource ごと破棄される。
        if (bgmFader == null)
        {
            Debug.LogWarning("[ParentUdpSender] LoadSceneAfterBgmFade: TitleBgmFader が見つかりません（BGMはフェードせず遷移します）。");
        }
        else if (!bgmFader.FadeOut(allowResume: false))
        {
            Debug.LogWarning("[ParentUdpSender] LoadSceneAfterBgmFade: BGM FadeOut() が false（BGM未再生の可能性）。");
        }

        // 待機は「画面の暗転フェード」の完了のみを基準にする。
        float waitSeconds = 0f;
        if (screenFader == null)
        {
            Debug.LogWarning("[ParentUdpSender] LoadSceneAfterBgmFade: TitleScreenFader が見つかりません（画面フェードなしで遷移します）。");
        }
        else if (screenFader.FadeOut())
        {
            waitSeconds = screenFader.FadeOutSeconds;
        }

        if (waitSeconds > 0f)
        {
            Debug.Log($"[ParentUdpSender] LoadSceneAfterBgmFade: 画面フェード {waitSeconds}秒で '{sceneName}' へ遷移します（BGMフェードは待ちません）。");
            if (showDebugLogs)
                Debug.Log($"[StartTrace][親機] LoadSceneAfterBgmFade フェード待機開始 — waitSeconds={waitSeconds:F2}, 遷移先='{sceneName}'", this);
            yield return new WaitForSecondsRealtime(waitSeconds);
            if (showDebugLogs)
                Debug.Log($"[StartTrace][親機] LoadSceneAfterBgmFade フェード待機解除 — 遷移先='{sceneName}'", this);
        }
        else if (showDebugLogs)
        {
            Debug.Log($"[StartTrace][親機] LoadSceneAfterBgmFade フェード待機なし (waitSeconds=0) — 遷移先='{sceneName}'", this);
        }

        if (showDebugLogs)
            Debug.Log($"[StartTrace][親機] LoadSceneAfterBgmFade → SceneManager.LoadScene('{sceneName}') 直前", this);
        SceneManager.LoadScene(sceneName);
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────
    void Start()
    {
        // Force result scenes to shared names across parent/child.
        gameOverSceneName = ResultGameOverScene;
        timeUpSceneName = ResultTimeUpScene;

        // 親機再起動後も、以前の正当な結果（既知プレイ・保存済み）を受理できるよう復元する。
        LoadRankSyncFromPrefs();

        SceneManager.sceneLoaded += OnSceneLoaded;

        _isRunning = true;

        // ソケットの生成とbindは通信基盤（UdpChannel）が行う。ソケット数・bind先・送信元ポートは従来どおり：
        //   ・送信ソケット（bindしない＝送信元ポートはOS任せ）… SendState / CAUGHT / PING / DISCOVERY_ACCEPT / RANK_SYNC_CHALLENGE
        //   ・受信ソケット broadcastPort（Discovery用の受信スレッド）
        //   ・受信ソケット parentReceivePort（通常データ用の受信スレッド）
        if (_udpChannel.TryOpen(broadcastPort, parentReceivePort, out string openError))
        {
            Debug.Log($"[ParentUdpSender] Sockets open — listening for discovery on :{broadcastPort}, normal data on :{parentReceivePort}. Initial targetIP='{targetIP}'");
        }
        else
        {
            // 部分的な初期化失敗でも、生成済みソケットやスレッドは残さない（TryOpen 内で全て破棄済み）。
            // 以降は送受信を行わない（Send は例外になり、各呼び出し元の既存の catch がログする）。
            Debug.LogError($"[ParentUdpSender] Failed to open UDP sockets — {openError}");
        }
    }

    void Update()
    {
        // 受信スレッドから渡された「受信データ」と「通信エラー」を取り込む。
        // 旧: 受信スレッド自身が行っていたログ積みと Discovery 種別判定を、同じ順序でここへ移した。
        DrainChannelQueues();

        while (_actionQueue.TryDequeue(out Action action))
            action();

        while (_receiveQueue.TryDequeue(out string raw))
            HandleIncoming(raw);

        ProcessLogQueue();

        HandleReturnToTitleKey();

        // ── LOUD_ITEM：購読準備前の受信を、購読成立後に1回だけ通知する ─────────
        //   消費の前に「対象シーン（ゲームシーン）」と「有効な購読者」を確認する。
        if (_hasPendingLoudItem)
        {
            string activeScene = SceneManager.GetActiveScene().name;
            if (!IsGameplayScene(activeScene))
            {
                // ゲームシーン以外では保留しない（次シーン・次プレイへ持ち越さない）
                _hasPendingLoudItem = false;
            }
            else if (LoudItemReceived != null)
            {
                _hasPendingLoudItem = false;
                if (showDebugLogs)
                    Debug.Log("[ParentUdpSender] 保留していた LOUD_ITEM を購読者へ通知します（メインスレッド）。");
                LoudItemReceived?.Invoke();
            }
            // 購読者がまだ居ない場合は、同じゲームシーン内に限って保留を維持する
        }

        // ── 片付け演出：ゲーム進行率の保留通知（メインスレッドで処理）────────
        //   購読者（ゲームシーン側）が準備できる前に受信した進行率の「最新値」を、
        //   購読が成立した最初の Update で1回だけ通知する（履歴は通知しない）。
        if (_hasPendingGameProgress)
        {
            string activeScene = SceneManager.GetActiveScene().name;
            if (!IsGameplayScene(activeScene))
            {
                // ゲームシーン以外では保留しない（次シーン・次プレイへ持ち越さない）
                _hasPendingGameProgress = false;
                _pendingGameProgress = 0f;
            }
            else if (GameProgressReceived != null)
            {
                // 保持している最新値だけを1回通知する（古い保留値を後から通知しない）。
                float pendingRate = _pendingGameProgress;
                _hasPendingGameProgress = false;
                _pendingGameProgress = 0f;
                if (showDebugLogs)
                    Debug.Log("[ParentUdpSender] 保留していた GAME_PROGRESS を購読者へ通知します（メインスレッド・最新値のみ）。");
                GameProgressReceived?.Invoke(pendingRate);
            }
            // 購読者がまだ居ない場合は、同じゲームシーン内に限って保留を維持する（常に最新値のみ保持）
        }

        // Timeout check（判定は接続担当、シーンによる例外は親機側で評価して渡す）
        if (_link.IsTimedOut(Time.time, _timeoutLimit))
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName == gameSceneName)
            {
                // Keep connection alive during loading to avoid false timeouts.
                _link.NoteReceive(Time.time);
            }
            else
            {
                SetConnectionState(ConnectionState.Disconnected);
                Debug.LogWarning("[ParentUdpSender] Connection timed out — child heartbeat lost.");
            }
        }

        // Heartbeat coroutine lifecycle
        if (currentState == ConnectionState.Connected && _heartbeatCoroutine == null)
            _heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
        else if (currentState != ConnectionState.Connected && _heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }

        // タイトルUIの更新は ParentTitleConnectionUI が ConnectionStateChanged を受けて行う。

        // 親機デバッグキー（I／Y／U）は親機デバッグキー整理により削除した。
        // NotifyGameOverFromParentCatch / SendStateSLEEP_LOCK / SendStateSLEEP_UNLOCK の
        // 各メソッドは本編（CaughtReactionController／SleepingController）から引き続き
        // 呼び出されるため保持している。UDP通信処理には変更を加えていない。
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (Instance == this) Instance = null;
        _isRunning = false;

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

        // 通信基盤を停止する：停止要求 → ソケットを閉じてブロッキング受信を解除 → 受信スレッドの終了を有限時間だけ待つ。
        //   ・重複個体（Awake で自壊した個体）は自分自身の基盤しか閉じない＝生存個体の通信は止めない。
        //   ・Close は複数回呼んでも安全。
        _udpChannel.Close();
    }

    // ── Scene reference refresh ──────────────────────────────────────────────
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _returningToTitle = false;
        _returnToTitleHeld = 0f;

        if (showDebugLogs)
        {
            Debug.Log($"[ParentUdpSender] Scene loaded: '{scene.name}' — refreshing scene references.");
            Debug.Log($"[StartTrace][親機] ParentUdpSender.OnSceneLoaded 到着 — scene='{scene.name}', mode={mode}, currentState={currentState}, _gameStarted={_gameStarted}, ChildLoadingComplete={ChildLoadingComplete}, isTitle={IsTitleScene(scene.name)}, sessionId={_currentPlaySessionId}, id={GetInstanceID()}", this);
        }

        if (IsTitleScene(scene.name))
        {
            ResetForNewSession();
            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] Title scene loaded ('{scene.name}') — reset state for next round.");
            return;
        }

        // Reset result guard for new game session
        if (scene.name == gameSceneName || scene.name == soloGameSceneName)
        {
            _resultProcessed = false;
            // 前プレイの LOUD_ITEM 保留は持ち越さない（購読者はゲームシーン側で解決される）。
            _hasPendingLoudItem = false;
            // 片付け演出用の保留通知もリセットする（再プレイで前の通知を持ち越さない）。
            _hasPendingGameProgress = false;
            if (_caughtRetryCoroutine != null)
            {
                StopCoroutine(_caughtRetryCoroutine);
                _caughtRetryCoroutine = null;
            }

            // 演出完了通知の再送も、新しいプレイの開始時に終了する（前プレイのACK待ちを持ち越さない）。
            if (_sequenceDoneRetryCoroutine != null)
            {
                StopCoroutine(_sequenceDoneRetryCoroutine);
                _sequenceDoneRetryCoroutine = null;
            }
            _sequenceDoneAcked = false;

            if (showDebugLogs)
                Debug.Log("[ParentUdpSender] _resultProcessed reset for new game session.");
        }
        else
        {
            // GameScene 以外へ遷移したときは LOUD_ITEM の保留を安全に初期化する
            // （購読者が居ないまま別シーンへ移った保留を残さない）。
            _hasPendingLoudItem = false;
            // 片付け演出用の保留通知も同様に初期化する（シーン変更で状態を残さない）。
            _hasPendingGameProgress = false;
        }
    }

    // ゲーム中（タイトル以外のシーン）で returnToTitleKey を returnToTitleHoldSeconds 秒押し続けると、
    // 親機をタイトル画面へ戻す。誤操作防止のため長押し式。
    private void HandleReturnToTitleKey()
    {
        if (!enableReturnToTitleKey || _returningToTitle) return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || IsTitleScene(SceneManager.GetActiveScene().name))
        {
            _returnToTitleHeld = 0f;
            return;
        }

        if (!keyboard[returnToTitleKey].isPressed)
        {
            _returnToTitleHeld = 0f;
            return;
        }

        _returnToTitleHeld += Time.unscaledDeltaTime;
        if (_returnToTitleHeld >= returnToTitleHoldSeconds)
        {
            ReturnToTitle();
        }
    }

    /// <summary>
    /// 親機をタイトル画面へ戻す。タイトルに入ると OnSceneLoaded → ResetForNewSession が走り、
    /// 接続状態・リザルト判定などのセッション状態がすべて初期化される。
    /// notifyPeer=true（ユーザー操作・長押し復帰など、この端末が起点の場合）は子機へも TEAM13_RETURN_TO_TITLE を送信する。
    /// notifyPeer=false（子機からの RETURN_TO_TITLE 受信が起点の場合）は送り返さない（無限往復の防止）。
    /// </summary>
    public void ReturnToTitle(bool notifyPeer = true)
    {
        if (_returningToTitle) return;
        _returningToTitle = true;
        _returnToTitleHeld = 0f;

        Debug.Log($"[ParentUdpSender] ReturnToTitle — '{SceneManager.GetActiveScene().name}' から '{motherTitleSceneName}' へ戻ります。");

        Time.timeScale = 1f;

        // 子機にも「タイトルへ戻る」ことを通知する（子機側で受信して追従する）。
        // 相手からの受信が起点の場合は送り返さない。
        if (notifyPeer)
        {
            NotifyReturnToTitleToChild();
        }
        SceneManager.LoadScene(SceneNameResolver.Resolve(motherTitleSceneName));
    }

    /// <summary>
    /// 親機起点で「タイトルへ戻る」ことを子機へ通知する（TEAM13_RETURN_TO_TITLE、再送付き）。
    /// 親機のリザルト画面（ResultSceneChamger）とキーボードのタイトル復帰（ReturnToTitle）から呼ばれる。
    /// </summary>
    public void NotifyReturnToTitleToChild(bool fast = false)
    {
        // 再送は「同じ文字列（同じ sid・seq・復帰モード）」を送る。
        string message = NotifyReturnToTitle(fast);

        if (_returnToTitleNotifyCoroutine != null)
        {
            StopCoroutine(_returnToTitleNotifyCoroutine);
        }
        _returnToTitleNotifyCoroutine = StartCoroutine(ReturnToTitleNotifyRoutine(message));
    }

    /// <summary>
    /// RETURN_TO_TITLE の再送処理（パケットロス対策）。
    /// 0.15秒間隔で計3回再送（即時送信と合わせて計4回送信）。
    /// 再送は同じ連番（直近の _returnToTitleSendSeq）で行うため、受信側は2回目以降を重複として正しく無視できる。
    /// </summary>
    private IEnumerator ReturnToTitleNotifyRoutine(string message)
    {
        const int retryCount = 3;
        const float retryInterval = 0.15f;

        // 再送は「同じ文字列＝同じ識別子・同じ番号・同じ復帰モード」で送る
        // （受信側は2回目以降を重複として無視できる）。
        // DDOL の本コンポーネント上で走り、実時間で待機する（Time.timeScale=0 でも継続）。
        for (int i = 0; i < retryCount; i++)
        {
            yield return new WaitForSecondsRealtime(retryInterval);
            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] Sending RETURN_TO_TITLE retry ({i + 1}/{retryCount}) — '{message}'");
            SendState(message);
        }

        _returnToTitleNotifyCoroutine = null;
    }

    /// <summary>
    /// 子機からの「タイトルへ戻る」通知（TEAM13_RETURN_TO_TITLE:&lt;識別子&gt;:&lt;連番&gt;）を受信したときの処理。
    /// 子機に合わせて親機もタイトル画面へ戻る。
    /// ・識別子が現在のプレイと異なる → 別プレイ（前回の再送・遅延、再起動前の通知）として無視する。
    /// ・識別子が一致し、連番が「最後に処理した連番」以下 → 同プレイ内の重複として無視する。
    /// リザルト画面にいる場合は ResultSceneChamger のフェード演出を流用し、
    /// ゲームプレイ中などの演出がない場合は即座に遷移する。
    /// </summary>
    private void HandleTeamReturnToTitle(string playSessionId, int seq, bool fast)
    {
        // 旧フォーマット（識別子なし）は、こちらに識別子が無い場合のみ受け付ける（後方互換）。
        bool sessionMatches = !string.IsNullOrEmpty(playSessionId)
            ? playSessionId == _currentPlaySessionId
            : string.IsNullOrEmpty(_currentPlaySessionId);

        if (!sessionMatches)
        {
            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] RETURN_TO_TITLE ignored — different play session (recv='{playSessionId}', current='{_currentPlaySessionId}').");
            return;
        }

        if (seq <= _lastPeerReturnToTitleSeq)
        {
            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] RETURN_TO_TITLE ignored — stale/duplicate seq={seq} (last processed={_lastPeerReturnToTitleSeq}).");
            return;
        }
        _lastPeerReturnToTitleSeq = seq;

        string currentScene = SceneManager.GetActiveScene().name;
        if (IsTitleScene(currentScene))
        {
            if (showDebugLogs)
                Debug.Log("[ParentUdpSender] RETURN_TO_TITLE ignored — already on title scene.");
            return;
        }

        ResultSceneChamger changer = FindFirstObjectByType<ResultSceneChamger>();
        if (changer != null)
        {
            if (changer.IsReturningToTitle)
            {
                if (showDebugLogs)
                    Debug.Log("[ParentUdpSender] RETURN_TO_TITLE ignored — already returning to title (fade in progress).");
                return;
            }

            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] RETURN_TO_TITLE received — starting result fade to title (fast={fast}).");
            changer.StartFadeToTitle(notifyPeer: false, skipWaits: fast);
            return;
        }

        Debug.Log("[ParentUdpSender] RETURN_TO_TITLE received — returning to title immediately.");
        ReturnToTitle(notifyPeer: false);
    }

    // 実際のゲームプレイシーンか。gameSceneName は最初に読み込む ParentLoading を指すため、
    // プレイ中の判定には使えない（使うと LOUD_ITEM が常に無視され、ラッシュインが起きない）。
    private bool IsGameplayScene(string sceneName)
    {
        return sceneName == gameplaySceneName;
    }

    private bool IsTitleScene(string sceneName)
    {
        // 親機のタイトル（motherTitleSceneName）と子機のタイトル（titleSceneName）の両方を扱う。
        // 末尾の Contains("Title") は、命名ゆれの吸収用のフォールバック。
        return sceneName == motherTitleSceneName ||
               sceneName == titleSceneName ||
               sceneName == "TitleScene" ||
               sceneName == "Title" ||
               sceneName.Contains("Title");
    }

    // タイトルのボタンへのリスナー登録は ParentTitleConnectionUI が担当する
    // （旧実装は RemoveAllListeners で他コンポーネントの永続リスナーまで消していたため、
    //   本クラスからは一切登録しない）。

    /// <summary>
    /// 現在プレイの受信記録（ChildLoadingComplete）を初期化する。
    /// 「プレイの開始を受理した時点（ParentLoading へ進む前）」と「タイトル復帰時」に呼ぶ。
    ///  ・START_GAME の再送（同じプレイ識別子）では呼ばない ＝ 受理済みの記録を消さない。
    ///  ・ロード側（ParentLoadingController）からは呼ばない（受信記録の書き換えはUDP側に閉じる）。
    /// </summary>
    private void ResetChildLoadingCompleteRecord(string reason)
    {
        bool previous = ChildLoadingComplete;
        ChildLoadingComplete = false;
        Debug.Log($"[ParentUdpSender] 現在プレイの受信記録(ChildLoadingComplete)を初期化 — {previous} → false, 理由={reason}");
    }

    public void ResetForNewSession()
    {
        // 接続状態・接続先・最終受信時刻の初期化は接続担当へ委譲する（互換フィールドは即時ミラー）。
        _link.ResetForNewSession();
        MirrorConnectionLinkToCompatFields();
        _gameStarted = false;
        _resultProcessed = false;
        // タイトル復帰時のリセットは維持する（前プレイの受信記録を次プレイへ持ち越さない）。
        ResetChildLoadingCompleteRecord("タイトル復帰");
        // LOUD_ITEM の保留もクリアする（前プレイの通知を次プレイへ持ち越さない）。
        _hasPendingLoudItem = false;
        // 片付け演出用の保留通知もクリアする（前プレイの通知を次プレイへ持ち越さない）。
        _hasPendingGameProgress = false;
        _pendingGameProgress = 0f;

        // プレイ識別子をクリアする。タイトルに戻った時点で前のプレイは終了しているため、
        // 以降に届く前回プレイの RETURN_TO_TITLE は識別子不一致で無視される
        // （次プレイ開始時に START_GAME / Solo Start で新しい識別子を必ず発行する）。
        _currentPlaySessionId = "";
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

        // 演出完了通知の再送も、新セッションでは終了させる（前プレイの再送・ACK待ちを持ち越さない）。
        if (_sequenceDoneRetryCoroutine != null)
        {
            StopCoroutine(_sequenceDoneRetryCoroutine);
            _sequenceDoneRetryCoroutine = null;
        }
        _sequenceDoneAcked = false;

        Debug.Log("[ParentUdpSender] ResetForNewSession: session flags cleared.");
    }

    // タイトルUIの参照取得は ParentTitleConnectionUI が担当する（Inspector参照のみ・名前検索なし）。

    // ── Public send API ───────────────────────────────────────────────────────
    // ── 演出完了通知（子機の待機解除）──────────────────────────────────────────
    private const string CmdSequenceDone = "SEQUENCE_DONE";
    private Coroutine _sequenceDoneRetryCoroutine;
    private bool _sequenceDoneAcked;

    /// <summary>
    /// 捕獲演出が完了（または安全に打ち切り）し、結果画面へ進めることが確定した時点で子機へ通知する。
    ///  ・Jumpscare開始時には送らない（呼び出し元がカメラ完了待ちまで終えた後に呼ぶ）。
    ///  ・UDPなので1回送りにせず複数回送る（子機は重複を無視してACKを返す）。
    ///  ・プレイ識別（playSessionId）付きなので、前回プレイの遅延通知で今回の待機は解けない。
    /// </summary>
    public void NotifyGameOverSequenceCompleted(string reason)
    {
        string message = $"{CmdSequenceDone}:{_currentPlaySessionId}:{reason}";
        _sequenceDoneAcked = false;

        if (_sequenceDoneRetryCoroutine != null)
        {
            StopCoroutine(_sequenceDoneRetryCoroutine);
            _sequenceDoneRetryCoroutine = null;
        }

        _sequenceDoneRetryCoroutine = StartCoroutine(SequenceDoneRetryRoutine(message));

        Debug.Log($"[ParentUdpSender] 演出完了通知の送信を開始します: '{message}' → {targetIP}:{normalPort}（ACKまで再送）");
    }

    /// <summary>演出完了通知の再送（子機のACKを受けたら早期終了する）。</summary>
    private IEnumerator SequenceDoneRetryRoutine(string message)
    {
        const int retryCount = 8;
        const float interval = 0.25f;

        for (int i = 0; i < retryCount; i++)
        {
            if (_sequenceDoneAcked)
            {
                Debug.Log($"[ParentUdpSender] SEQUENCE_DONE のACKを受領したため再送を終了します（送信 {i} 回）。");
                break;
            }

            SendState(message);
            yield return new WaitForSecondsRealtime(interval);
        }

        _sequenceDoneRetryCoroutine = null;
    }

    /// <summary>
    /// 子機からの受領確認（SEQUENCE_DONE_ACK:&lt;playSessionId&gt;）を処理する。
    /// 戻り値：今回のプレイのACKとして受理したら true。
    /// </summary>
    public bool HandleSequenceDoneAck(string playId)
    {
        if (!string.IsNullOrEmpty(playId) && !string.IsNullOrEmpty(_currentPlaySessionId) && playId != _currentPlaySessionId)
        {
            Debug.Log($"[ParentUdpSender] SEQUENCE_DONE_ACK ignored（別プレイのACK）: playId='{playId}' / current='{_currentPlaySessionId}'");
            return false;
        }

        _sequenceDoneAcked = true;
        Debug.Log($"[ParentUdpSender] SEQUENCE_DONE_ACK を受信しました（playId='{playId}'）。");
        return true;
    }

    public void SendState(string message)
    {
        if (currentState != ConnectionState.Connected)
            return;

        if (showDebugLogs)
            Debug.Log($"[ParentUdpSender] → '{message}' to {targetIP}:{normalPort} | connectionState={currentState}");

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + message);
            _udpChannel.Send(data, data.Length, targetIP, normalPort);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ParentUdpSender] SendState error: {e.Message}");
        }
    }

    public void SendStateSLEEP_LOCK()
    {
        SendState("SLEEP_LOCK");
    }

    public void SendStateSLEEP_UNLOCK()
    {
        SendState("SLEEP_UNLOCK");
    }

    /// <summary>
    /// 親機側で「タイトルへ戻る」ことを子機へ通知する（TEAM13_RETURN_TO_TITLE:<連番>）。
    /// 子機のリザルト画面（ResultSceneChamger）や親機のキーボード復帰から呼ばれる。
    /// 連番はこのプロセス起動後ただただ単調増加し、前回プレイの再送・遅延パケットの識別に使う。
    /// </summary>
    public string NotifyReturnToTitle(bool fast = false)
    {
        _returnToTitleSendSeq++;

        // ボタン起点は ":FAST" を付けて送る（受信側も短いフェードにする）。
        string message = fast
            ? $"{parentReturnToTitle}:{_currentPlaySessionId}:{_returnToTitleSendSeq}:FAST"
            : $"{parentReturnToTitle}:{_currentPlaySessionId}:{_returnToTitleSendSeq}";

        SendState(message);
        Debug.Log($"[ParentUdpSender] Sent RETURN_TO_TITLE (playSessionId={_currentPlaySessionId}, seq={_returnToTitleSendSeq}, fast={fast}) to child.");
        return message;
    }

    /// <summary>
    /// 親機の捕獲によるGame Over確定を通知する。
    /// 初回呼び出し時のみ _resultProcessed を true にし、CAUGHT を即時送信および短時間再送する。
    /// </summary>
    public void NotifyGameOverFromParentCatch()
    {
        if (_resultProcessed && _caughtRetryCoroutine != null)
            return;

        _resultProcessed = true;

        // 捕獲（CAUGHT）が確定したプレイを記録する。
        // このプレイの TIME_UP は最終結果ではない（GAME_OVER が最終結果）ため受理しない。
        if (!string.IsNullOrEmpty(_currentPlaySessionId))
        {
            _captureConfirmedPlayId = _currentPlaySessionId;
        }

        if (showDebugLogs)
            Debug.Log($"[ParentUdpSender] NotifyGameOverFromParentCatch: 親機の捕獲によるゲームオーバー確定。CAUGHT送信・再送を開始します。targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='TEAM13_CAUGHT'");

        // 即時送信
        SendCaughtNotification();

        // 短時間再送コルーチン（DontDestroyOnLoadのParentUdpSender上で実行）
        if (_caughtRetryCoroutine != null)
        {
            StopCoroutine(_caughtRetryCoroutine);
        }
        _caughtRetryCoroutine = StartCoroutine(CaughtRetryRoutine());
    }

    /// <summary>
    /// CAUGHTメッセージの短時間再送処理（パケットロス対策）
    /// 0.1秒間隔で計3回再送（即時送信と合わせて計4回送信）
    /// </summary>
    private IEnumerator CaughtRetryRoutine()
    {
        const int retryCount = 3;
        const float retryInterval = 0.1f;

        for (int i = 0; i < retryCount; i++)
        {
            yield return new WaitForSecondsRealtime(retryInterval);
            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] Sending CAUGHT retry ({i + 1}/{retryCount})...");
            SendCaughtNotification();
        }

        _caughtRetryCoroutine = null;
    }

    private void SendCaughtNotification()
    {
        const string message = "TEAM13_CAUGHT";

        if (string.IsNullOrWhiteSpace(targetIP) ||
            !IPAddress.TryParse(targetIP, out _))
        {
            Debug.LogWarning($"[ParentUdpSender] CAUGHT send skipped: invalid targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}'");
            return;
        }

        if (!_udpChannel.IsOpen)
        {
            Debug.LogError($"[ParentUdpSender] CAUGHT send failed: udp channel is not open, targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}'");
            return;
        }

        Debug.Log($"[ParentUdpSender] CAUGHT send attempt: targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}'");

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            _udpChannel.Send(data, data.Length, targetIP, normalPort);
            Debug.Log($"[ParentUdpSender] CAUGHT send succeeded: targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}'");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ParentUdpSender] CAUGHT send failed: targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}', error='{e.Message}'");
        }
    }

    // ゲーム中に子機側の一時的なフリーズ等で PING が途絶えると、親機は Timeout で Disconnected になり、
    // 以後 SendState（SLEEP_LOCK / SLEEP_UNLOCK など）が全て無視されてしまう。
    // PING が再開した場合は接続を復帰する。タイトル画面での Cancel（意図的な切断）は復帰させない。
    private void TryRecoverConnectionFromPing()
    {
        if (_link.State != ParentConnectionLink.LinkState.Disconnected) return;
        if (IsTitleScene(SceneManager.GetActiveScene().name)) return;   // シーン条件は親機側で評価して渡す

        SetConnectionState(ConnectionState.Connected);
        Debug.LogWarning("[ParentUdpSender] PING resumed after timeout — connection restored.");
    }

    // ── Incoming message dispatch (main thread) ───────────────────────────────
    private void HandleIncoming(string raw)
    {
        // 演出完了通知の受領確認（子機→親機）。パーサーのメッセージ型を増やさず、生テキストの接頭辞で処理する。
        //  ・"MAGIC_SEQUENCE_DONE_ACK:<playSessionId>" の形（MAGIC接頭辞の有無どちらでも受理する）。
        if (raw != null)
        {
            const string ackToken = "SEQUENCE_DONE_ACK:";
            int ackIndex = raw.IndexOf(ackToken, System.StringComparison.Ordinal);
            if (ackIndex >= 0)
            {
                HandleSequenceDoneAck(raw.Substring(ackIndex + ackToken.Length).Trim());
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

        if (message.Type != ParentMessageType.Ping && showDebugLogs)
            Debug.Log($"[ParentUdpSender] HandleIncoming: '{message.RawPayload}' | scene='{SceneManager.GetActiveScene().name}' | loudItemSubscribers={(LoudItemReceived != null)} | gameProgressSubscribers={(GameProgressReceived != null)} | _resultProcessed={_resultProcessed}");

        // 子機から何か届いた＝子機は生きている。PING 以外のメッセージでも生存時刻を更新する。
        _link.NoteReceive(Time.time);

        if (message.Type == ParentMessageType.Ping)
        {
            TryRecoverConnectionFromPing();
            return;
        }

        if (message.Type == ParentMessageType.StartGame)
        {
            // 開始経路の診断: パーサが返した種別と、開始を止める条件の現在値を記録する。
            if (showDebugLogs)
                Debug.Log($"[StartTrace][親機] ParentUdpSender.HandleIncoming START_GAME — parsedType={message.Type}, playSessionId='{message.PlaySessionId}', scene='{SceneManager.GetActiveScene().name}', currentState={currentState}, _gameStarted={_gameStarted}, id={GetInstanceID()}", this);

            if (!_gameStarted && currentState == ConnectionState.Connected)
            {
                _gameStarted = true;

                // 子機が発行したプレイ識別子を採用する（旧フォーマットで未付与なら親機側で発行）。
                // これで両端末が同じ識別子を共有し、次プレイの RETURN_TO_TITLE と区別できる。
                // ・START_GAME の再送は同じ識別子を運ぶため、その場合は「新しいプレイ」ではないと判断して
                //   受信記録を初期化しない（受理済みの記録を消さない）。
                bool isRepeatOfAcceptedPlay = !string.IsNullOrEmpty(message.PlaySessionId) &&
                                              message.PlaySessionId == _currentPlaySessionId;

                _currentPlaySessionId = string.IsNullOrEmpty(message.PlaySessionId)
                    ? CreatePlaySessionId()
                    : message.PlaySessionId;
                _captureConfirmedPlayId = "";

                // 現在プレイの受信記録は、開始を受理したこの時点（ParentLoading へ進む前）で初期化する。
                if (!isRepeatOfAcceptedPlay)
                {
                    ResetChildLoadingCompleteRecord("START_GAME受理");
                }

                if (showDebugLogs)
                {
                    Debug.Log($"[ParentUdpSender] Received START_GAME from child — loading game scene. playSessionId={_currentPlaySessionId}");
                    Debug.Log($"[StartTrace][親機] START_GAME 分岐=開始 → LoadSceneAfterBgmFade('{gameSceneName}') を開始 (sessionId={_currentPlaySessionId})", this);
                }
                StartCoroutine(LoadSceneAfterBgmFade(gameSceneName));
            }
            else if (showDebugLogs)
            {
                Debug.Log($"[StartTrace][親機] START_GAME 分岐=開始しない — 条件(!_gameStarted && currentState==Connected)が不成立: _gameStarted={_gameStarted}, currentState={currentState}", this);
            }
            return;
        }

        if (message.Type == ParentMessageType.TimeUp || message.Type == ParentMessageType.ChildDead)
        {
            // Legacy bare TIME_UP / CHILD_DEAD — treat as TIME_UP result
            if (!_resultProcessed)
            {
                _resultProcessed = true;
                Debug.Log($"[ParentUdpSender] Received {message.RawPayload} — TIME_UP result. Loading {timeUpSceneName}.");
                SceneManager.LoadScene(timeUpSceneName);
            }
            else
            {
                if (showDebugLogs)
                    Debug.Log($"[ParentUdpSender] {message.RawPayload} result ignored — result already processed.");
            }
            return;
        }

        if (message.Type == ParentMessageType.ChildScore)
        {
            // 旧形式のスコア通知。ランキング全件を持たないため「同期完了」とは扱わない。
            // 親機は独自にランキングを更新しない（子機の RANK_SYNC が唯一の基準）。
            Debug.LogWarning("[ParentUdpSender] Legacy CHILD_SCORE received — ignored (RANK_SYNC is authoritative). Both ends must be updated.");
            return;
        }

        if (message.Type == ParentMessageType.RankSyncHello)
        {
            HandleRankSyncHello(message.Hello);
            return;
        }

        if (message.Type == ParentMessageType.RankingSync)
        {
            HandleRankSync(message.Snapshot);
            return;
        }

        if (message.Type == ParentMessageType.TeamReturnToTitle)
        {
            HandleTeamReturnToTitle(message.PlaySessionId, message.Score, message.ReturnToTitleFast);
            return;
        }

        if (message.Type == ParentMessageType.LoadingComplete)
        {
            bool previous = ChildLoadingComplete;
            ChildLoadingComplete = true;
            Debug.Log("[ParentUdpSender] Received LOADING_COMPLETE from child. Property set to true.");
            if (showDebugLogs)
                Debug.Log($"[StartTrace][親機] ParentUdpSender.HandleIncoming LOADING_COMPLETE — ChildLoadingComplete: {previous} → {ChildLoadingComplete}, scene='{SceneManager.GetActiveScene().name}', id={GetInstanceID()}", this);

            // 受信記録を更新した後に通知する（購読済みのロード側はローカル状態へ反映する）。
            // 再送（false→true 以外）では発火しない＝同じ処理を繰り返さない。
            if (!previous)
            {
                LoadingCompleteReceived?.Invoke();
            }

            return;
        }

        if (message.Type == ParentMessageType.LoudItem)
        {
            if (showDebugLogs)
                Debug.Log("[ParentUdpSender] Received LOUD_ITEM network packet from Child.");

            string activeScene = SceneManager.GetActiveScene().name;
            if (!IsGameplayScene(activeScene))
            {
                // ゲームシーン以外の受信は従来どおり破棄する（保留もしない）。
                if (showDebugLogs)
                    Debug.Log($"[ParentUdpSender] LOUD_ITEM received outside gameplay scene (active='{activeScene}', expected='{gameplaySceneName}') — ignored.");
                return;
            }

            // 受信後の反応（MotherSuspicionSystem.OnLoudItemTriggered など）はゲームシーン側の購読者が行う。
            // ここでは「受信した」ことだけをメインスレッドで通知する（直接呼び出しは行わない）。
            if (LoudItemReceived != null)
            {
                // 購読準備前に受信して保留していた分（A）がある場合は、「A → 新規B」の受信順で通知する。
                // 新規受信を理由に保留Aを捨てない。フラグは通知の前にクリアし、
                // Update 後段の保留処理で二重通知されないようにする。
                if (_hasPendingLoudItem)
                {
                    _hasPendingLoudItem = false;
                    if (showDebugLogs)
                        Debug.Log("[ParentUdpSender] 保留していた LOUD_ITEM を購読者へ通知します（メインスレッド・受信順を保持）。");
                    LoudItemReceived?.Invoke();
                }

                // 保留分の通知でシーン遷移・購読解除が起きた可能性があるため、新規分を通知する前に再確認する。
                string sceneAfterPending = SceneManager.GetActiveScene().name;
                if (!IsGameplayScene(sceneAfterPending))
                {
                    // 非ゲームシーンへ移った場合は新規分を持ち越さない（破棄）。
                    if (showDebugLogs)
                        Debug.Log($"[ParentUdpSender] LOUD_ITEM（新規分）は非ゲームシーンへ移ったため破棄します (active='{sceneAfterPending}')。");
                    return;
                }

                if (LoudItemReceived != null)
                {
                    if (showDebugLogs)
                        Debug.Log("[ParentUdpSender] LOUD_ITEM を購読者へ通知します（メインスレッド）。");
                    LoudItemReceived?.Invoke();
                }
                else
                {
                    // 同じゲームシーンだが購読者がいなくなった場合は、既存の1件保留ルールに従う。
                    _hasPendingLoudItem = true;
                    if (showDebugLogs)
                        Debug.Log("[ParentUdpSender] LOUD_ITEM（新規分）の購読者が居ないため1件保留します（購読成立後に通知）。");
                }
            }
            else
            {
                // 購読準備前（シーン側の OnEnable/Start が未実行など）の受信は1件だけ保留する。
                // 購読が成立した最初の Update で通知するため、ここでは破棄しない。
                _hasPendingLoudItem = true;
                if (showDebugLogs)
                    Debug.Log("[ParentUdpSender] LOUD_ITEM の購読者がまだ居ないため1件保留します（購読成立後に通知）。");
            }
            return;
        }

        // ── 片付け演出：ゲーム進行率通知 ─────────────────────────────────────
        if (message.Type == ParentMessageType.GameProgress)
        {
            string activeScene = SceneManager.GetActiveScene().name;
            if (!IsGameplayScene(activeScene))
                return;

            float progressRate = Mathf.Clamp01(message.Score / 1000f);
            if (GameProgressReceived != null)
            {
                // 購読者へ即通知する「前に」、古い保留（購読準備前に受信した値）を必ずクリアする。
                // クリアしないと、同じ Update の後段にある保留処理が古い値を後から通知してしまう。
                _hasPendingGameProgress = false;
                _pendingGameProgress = 0f;

                // メインスレッド（HandleIncoming は Update から呼ばれる）で購読者へ通知する。
                // 直接 MotherSuspicionSystem.OnGameProgress は呼ばない（通知経由に一本化する）。
                GameProgressReceived?.Invoke(progressRate);
            }
            else
            {
                // 購読者がまだ居ない場合は最新値を1件だけ保持する（履歴は蓄積しない）。
                _pendingGameProgress = progressRate;
                _hasPendingGameProgress = true;
            }
            return;
        }

        if (message.Type == ParentMessageType.Unknown)
            Debug.Log($"[ParentUdpSender] Unhandled message: '{message.RawPayload}'");
    }

    // ── Final result (CHILD_SCORE): 保存と画面遷移の分離 ──────────────────────

    /// <summary>
    // ── 子機ランキングの表示用コピー（同期）─────────────────────────────────

    /// <summary>
    /// 子機から受信したランキング／スコアのスナップショットを適用する。
    /// ・親機は独自にランキングへ挿入・合体・修復・点数削除をしない（表示用コピーのみ）
    /// ・同じ同期元の古い更新は適用しない。同じ更新の再送は適用せず ACK だけ返す
    /// ・同期を受けただけで結果画面へ勝手に遷移しない（現在プレイの結果のときだけ）
    /// </summary>
    private void HandleRankSync(RankingSnapshot snap)
    {
        string activeScene = SceneManager.GetActiveScene().name;
        bool onTitle = IsTitleScene(activeScene);

        // 接続確認（RANK_SYNC_HELLO）で確定した「今の接続相手」と一致する同期だけを適用する。
        // 一致しない（未確定・旧同期元の遅延）場合は適用もACKもしない。
        if (!_peerGate.AcceptsSnapshot(snap.SourceId))
        {
            Debug.LogWarning($"[ParentUdpSender] RANK_SYNC ignored — source='{snap.SourceId}' does not match the confirmed peer ('{_peerGate.ConnectedSourceId}'). 接続確認前・旧同期元のデータは適用しません。");
            return;
        }

        RankSyncApply result = _rankSync.Apply(snap);

        if (result == RankSyncApply.Mismatch)
        {
            // 同じ sourceId・updateNo なのに内容が違う: 適用もACKもしない（保存成功と誤認させない）
            Debug.LogError($"[ParentUdpSender] RANK_SYNC MISMATCH — source='{snap.SourceId}', update={snap.UpdateNo} は同じ更新番号で内容が違います。適用せずACKも返しません。");
            return;
        }

        if (result == RankSyncApply.Duplicate || result == RankSyncApply.Stale)
        {
            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] RANK_SYNC {result} — source='{snap.SourceId}', update={snap.UpdateNo} (copy stays at update={_rankSync.UpdateNo}).");

            // Duplicate は「同じ保存コピーを今回の接続で確認した」ことなので Current になり得る
            if (result == RankSyncApply.Duplicate) _syncVerifiedThisConnection = true;

            SendRankSyncAck(snap);   // 適用はしないが ACK は返す（子機の再送終了）。画面遷移もしない。
            NotifySyncStateChanged();
            return;
        }

        PlayerPrefs.SetString(PrefRankSyncData, _rankSync.Serialize());
        PlayerPrefs.SetInt(PrefRankSyncAt, (int)System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        PlayerPrefs.Save();

        Debug.Log($"[ParentUdpSender] RANK_SYNC {result} — source='{_rankSync.SourceId}', update={_rankSync.UpdateNo}, "
                + $"GO(last={_rankSync.GameOverLastScore}, top=[{string.Join(",", _rankSync.GameOverTop)}]), "
                + $"TU(last={_rankSync.TimeUpLastScore}, top=[{string.Join(",", _rankSync.TimeUpTop)}]), "
                + $"latest={_rankSync.LatestResultType}:{_rankSync.LatestResultPlayId}");

        _syncVerifiedThisConnection = true;

        // 表示更新（結果画面が開いていれば再表示される）＋ 表示状態の記録
        ResultDataCommitted?.Invoke();
        _lastSyncDisplayState = GetSyncDisplayState();

        TryNavigateForSyncedResult(result, onTitle);

        SendRankSyncAck(snap);
    }

    /// <summary>
    /// 同期内容に「現在のプレイ」の最新結果が含まれる場合だけ、対応する結果シーンへ進める。
    /// 遷移可否の判定は RankSyncNavigation（純粋）に集約し、ランキングコピー更新とは分離している。
    /// Stale / Duplicate / Mismatch では遷移しない。
    /// </summary>
    private void TryNavigateForSyncedResult(RankSyncApply result, bool onTitle)
    {
        bool shouldNavigate = RankSyncNavigation.ShouldNavigate(
            result, onTitle, _peerGate.AcceptsSnapshot(_rankSync.SourceId),
            _rankSync.LatestResultPlayId, _rankSync.LatestResultType,
            _currentPlaySessionId, _captureConfirmedPlayId);

        if (!shouldNavigate)
        {
            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] RANK_SYNC no screen transition (apply={result}, onTitle={onTitle}, latest={_rankSync.LatestResultType}:{_rankSync.LatestResultPlayId}, current='{_currentPlaySessionId}').");
            return;
        }

        string targetScene = _rankSync.LatestResultType == RankingSnapshot.TypeGameOver
            ? ResultGameOverScene : ResultTimeUpScene;
        if (SceneManager.GetActiveScene().name != targetScene)
        {
            SceneManager.LoadScene(targetScene);
        }
    }

    /// <summary>
    /// 接続確認の応答（RANK_SYNC_HELLO）。親機が今待っている challengeId と一致する応答だけを受理し、
    /// 今接続している子機の同期元を確定する。
    /// ・古い接続の応答は sourceId や起動回数に関係なく拒否する（巻き戻さない）
    /// ・同じ確認への再応答は冪等（Duplicate）
    /// </summary>
    private void HandleRankSyncHello(RankSyncHello hello)
    {
        RankSyncPeerGate.HelloResult r = _peerGate.OnHello(hello.ChallengeId, hello.SourceId, hello.SessionSeq);

        switch (r)
        {
            case RankSyncPeerGate.HelloResult.Accepted:
                Debug.Log($"[ParentUdpSender] RANK_SYNC_HELLO accepted — peer sourceId confirmed: '{_peerGate.ConnectedSourceId}' (challenge='{hello.ChallengeId}', sessionSeq={hello.SessionSeq}).");
                break;
            case RankSyncPeerGate.HelloResult.Duplicate:
                if (showDebugLogs)
                    Debug.Log($"[ParentUdpSender] RANK_SYNC_HELLO duplicate — peer sourceId stays '{_peerGate.ConnectedSourceId}'.");
                break;
            case RankSyncPeerGate.HelloResult.StaleResponse:
                Debug.LogWarning($"[ParentUdpSender] RANK_SYNC_HELLO ignored — challenge='{hello.ChallengeId}' is not the pending one ('{_peerGate.PendingChallengeId}'). 古い接続の応答は受理しません。");
                break;
            case RankSyncPeerGate.HelloResult.NoPending:
                Debug.LogWarning($"[ParentUdpSender] RANK_SYNC_HELLO ignored — no pending challenge (sourceId='{hello.SourceId}').");
                break;
            default:
                Debug.LogWarning($"[ParentUdpSender] RANK_SYNC_HELLO invalid — challengeId='{hello.ChallengeId}', sourceId='{hello.SourceId}', sessionSeq={hello.SessionSeq}.");
                break;
        }

        // 接続確認の結果は表示状態（前回同期データ／同期完了）に影響するので UI へ通知する
        if (r == RankSyncPeerGate.HelloResult.Accepted) NotifySyncStateChanged();
    }

    /// <summary>今接続している子機の同期元（未確定なら空）。</summary>
    public string ConnectedPeerSourceId => _peerGate.ConnectedSourceId;

    /// <summary>
    /// 表示用コピーの状態（未同期／前回同期データ／同期完了）。
    /// Current は「今の接続相手が確定済み」「コピーの sourceId がそれと一致」
    /// 「今回の接続で適用または同一コピーを確認済み」のすべてを満たすときだけ。
    /// </summary>
    public RankSyncDisplayState GetSyncDisplayState()
        => RankSyncDisplay.Evaluate(
            _rankSync.HasData,
            _peerGate.IsConfirmed,
            _peerGate.ConnectedSourceId,
            _rankSync.SourceId,
            _syncVerifiedThisConnection);

    /// <summary>表示用コピーの状態ラベル（「未同期」「前回同期データ」「同期完了」）。</summary>
    public string GetSyncDisplayLabel() => RankSyncDisplay.ToLabel(GetSyncDisplayState());

    /// <summary>
    /// 表示用コピーが「今の接続相手の最新」と言えるか。
    /// false のときは前回同期データ／未同期であり、最新の子機データとして見せてはいけない。
    /// </summary>
    public bool IsSyncCurrent => GetSyncDisplayState() == RankSyncDisplayState.Current;

    /// <summary>スナップショット受信の確認返信（子機はこれで再送を終了する）。</summary>
    private void SendRankSyncAck(RankingSnapshot snap)
    {
        SendState($"RANK_SYNC_ACK:{RankingSnapshot.VersionName}:{snap.SourceId}:{snap.UpdateNo}");
    }

    /// <summary>接続確認の要求（チャレンジ）を子機へ送る。</summary>
    private void SendRankSyncChallenge(string ip, string challengeId)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + RankSyncChallenge.Format(challengeId));
            _udpChannel.Send(data, data.Length, ip, normalPort);
            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] Sent RANK_SYNC_CHALLENGE '{challengeId}' to {ip}:{normalPort} ({_peerGate.ChallengeSendCount}/{RankSyncPeerGate.MaxChallengeSends}).");
        }
        catch (Exception e) { Debug.LogError($"[ParentUdpSender] SendRankSyncChallenge error: {e.Message}"); }
    }

    /// <summary>
    /// 接続確認の再送（回数と間隔を決めて有限回）。
    /// 確定したら／上限に達したら止める。無期限には待たず、未確定のままなら同期待ち表示のままにする。
    /// </summary>
    private void StartChallengeResend(string ip, string challengeId)
    {
        if (_challengeResendRoutine != null) StopCoroutine(_challengeResendRoutine);
        _challengeResendRoutine = StartCoroutine(ChallengeResendRoutine(ip, challengeId));
    }

    private IEnumerator ChallengeResendRoutine(string ip, string challengeId)
    {
        const float interval = 0.5f;

        while (_isRunning && _peerGate.HasPendingChallenge && _peerGate.PendingChallengeId == challengeId)
        {
            yield return new WaitForSeconds(interval);

            if (_peerGate.ChallengeExhausted)
            {
                if (showDebugLogs)
                    Debug.Log($"[ParentUdpSender] RANK_SYNC_CHALLENGE '{challengeId}' 自動再送の上限（{RankSyncPeerGate.MaxChallengeSends}回）に達しました。" +
                              "接続確認できていないため同期待ちとして扱います（子機が接続要求を再送すれば同じ ID で応えます）。");
                break;
            }

            SendRankSyncChallenge(ip, challengeId);
            _peerGate.NoteChallengeSent();
        }

        _challengeResendRoutine = null;
    }

    /// <summary>
    /// 表示状態が変わったときだけ ResultScoreUI へ更新通知を出す（結果画面を開いたままでも反映される）。
    /// </summary>
    public void NotifySyncStateChanged()
    {
        RankSyncDisplayState state = GetSyncDisplayState();
        if (!RankSyncDisplay.ShouldNotify(_lastSyncDisplayState, state)) return;

        RankSyncDisplayState previous = _lastSyncDisplayState;
        _lastSyncDisplayState = state;

        if (showDebugLogs)
            Debug.Log($"[ParentUdpSender] Sync display state {previous} → {state} — notifying ResultScoreUI.");

        ResultDataCommitted?.Invoke();
    }

    /// <summary>親機再起動後も最新の表示用コピーを復元する。</summary>
    private void LoadRankSyncFromPrefs()
    {
        if (_rankSync.Deserialize(PlayerPrefs.GetString(PrefRankSyncData, "")))
        {
            Debug.Log($"[ParentUdpSender] RankSync copy restored: source='{_rankSync.SourceId}', update={_rankSync.UpdateNo}.");
        }
        else if (showDebugLogs)
        {
            Debug.Log("[ParentUdpSender] RankSync copy: no data (未同期).");
        }
    }

    /// <summary>表示用コピーの有無（「未同期」を判別するため）。</summary>
    public bool HasRankSyncData => _rankSync.HasData;

    /// <summary>表示用コピーの更新番号・同期元・受信時刻（診断／鮮度表示用）。</summary>
    public int RankSyncUpdateNo => _rankSync.UpdateNo;
    public string RankSyncSourceId => _rankSync.SourceId;
    public int RankSyncReceivedAtUnix => PlayerPrefs.GetInt(PrefRankSyncAt, 0);

    /// <summary>表示用コピーから、指定種別の最新スコアとTOP5を取り出す（ResultScoreUI 用）。</summary>
    public bool TryGetSyncedResult(bool gameOver, out int lastScore, out int[] top5)
    {
        if (!_rankSync.HasData)
        {
            lastScore = 0;
            top5 = null;
            return false;
        }

        lastScore = gameOver ? _rankSync.GameOverLastScore : _rankSync.TimeUpLastScore;
        top5 = (int[])(gameOver ? _rankSync.GameOverTop : _rankSync.TimeUpTop).Clone();
        return true;
    }

    // ── Coroutines ────────────────────────────────────────────────────────────
    private IEnumerator HeartbeatCoroutine()
    {
        while (currentState == ConnectionState.Connected)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(MagicNumber + "PING");
                _udpChannel.Send(data, data.Length, targetIP, normalPort);
            }
            catch (Exception e) { Debug.LogError($"[ParentUdpSender] Heartbeat error: {e.Message}"); }
            yield return new WaitForSecondsRealtime(_pingInterval);
        }
    }

    // ── Log queue processing ──────────────────────────────────────────────────
    /// <summary>
    /// 受信スレッドなどの別スレッドからログをキューに追加する
    /// </summary>
    private void EnqueueLog(LogType type, string message)
    {
        _logQueue.Enqueue(new LogEntry(type, message));
    }

    /// <summary>
    /// 受信スレッドからキューイングされたログをUnityメインスレッドで出力する
    /// </summary>
    private void ProcessLogQueue()
    {
        while (_logQueue.TryDequeue(out LogEntry log))
        {
            switch (log.Type)
            {
                case LogType.Log:
                    if (showDebugLogs)
                        Debug.Log(log.Message);
                    break;
                case LogType.Warning:
                    if (showDebugLogs)
                        Debug.LogWarning(log.Message);
                    break;
                case LogType.Error:
                    Debug.LogError(log.Message);
                    break;
            }
        }
    }

    // ── 受信スレッドからの受け渡し（メインスレッド）────────────────────────────
    //   受信経路ごとのデータと通信エラーを、受信順に取り出して既存の経路へ流す。
    //   ・Discovery経路: 従来 ReceiveDiscovery が受信スレッドで行っていた種別判定をここで行い、
    //     従来どおり _actionQueue へ接続確定アクションを積む（実行は Update 後段・メインスレッド）。
    //   ・通常データ経路: 従来 ReceiveNormalData と同じく PING 以外をログへ積み、_receiveQueue へ渡す。
    //   ・通信エラー: 従来 EnqueueLog(Error, ...) と同じ内容でログキューへ積む。
    private void DrainChannelQueues()
    {
        while (_udpChannel.TryDequeueReceived(out UdpChannel.Datagram datagram))
        {
            if (datagram.Route == UdpChannelRoute.Discovery)
            {
                EnqueueLog(LogType.Log, $"[ParentUdpSender] Broadcast received: '{datagram.Text}' from {datagram.RemoteEndPoint.Address}");

                ParentUdpMessage message = ParentUdpMessageParser.Parse(datagram.Text);
                if (message.Type == ParentMessageType.DiscoveryRequest)
                {
                    string senderIP = datagram.RemoteEndPoint.Address.ToString();
                    EnqueueLog(LogType.Log, $"[ParentUdpSender] DISCOVERY_REQUEST from {senderIP} — queuing targetIP update and DISCOVERY_ACCEPT.");
                    _actionQueue.Enqueue(() =>
                    {
                        string oldIP = targetIP;
                        ConnectionState oldState = currentState;

                        // Discovery受理＝接続先の確定。接続管理は ParentConnectionLink が保持する。
                        //  ・接続確定に必要な状態（接続先IP・最終受信時刻・互換フィールド）を先に整える。
                        //  ・状態遷移（Connected）と ConnectionStateChanged の通知は、このブロックの既存処理
                        //    （ランキング検証・チャレンジ送信・DISCOVERY_ACCEPT送信）が終わった後に行い、
                        //    購読者へ不完全な状態を見せない。
                        //  ・同値（Connected→Connected）のDiscovery再送では通知しない（SetState の同値ガード）。
                        //    ただし接続先IPは同値でも更新され、互換ミラーにも反映される。
                        _link.SetTargetIP(senderIP);
                        _link.NoteReceive(Time.time);
                        MirrorConnectionLinkToCompatFields();
                        _gameStarted      = false;

                        // 接続確認: 同じ子機（同じ sourceId・起動世代）の Discovery 再送では確定状態を壊さず、
                        // 同じ challengeId を再送する。本当に新しい接続確認のときだけ新 ID を発行し、
                        // その間は未確定＝「前回同期データ」表示。IPでは同期元を確定しない。
                        RankSyncPeerGate.DiscoveryResult dr =
                            _peerGate.OnDiscovery(message.PlaySessionId, message.Score, out string challengeId);

                        if (dr == RankSyncPeerGate.DiscoveryResult.NewChallenge)
                        {
                            _syncVerifiedThisConnection = false;
                            NotifySyncStateChanged();
                        }

                        if (!string.IsNullOrEmpty(challengeId))
                        {
                            SendRankSyncChallenge(senderIP, challengeId);
                            _peerGate.NoteChallengeSent();
                            StartChallengeResend(senderIP, challengeId);
                        }

                        if (showDebugLogs)
                            Debug.Log($"[ParentUdpSender] Discovery({dr}) — challenge='{challengeId}', confirmed='{_peerGate.ConnectedSourceId}', childSource='{message.PlaySessionId}', sessionSeq={message.Score}.");

                        if (showDebugLogs)
                            Debug.Log($"[ParentUdpSender][Diag] DISCOVERY_REQUEST受信により state {oldState} → Connected, targetIP '{oldIP}' → '{targetIP}'");
                        SendDiscoveryAccept(senderIP);

                        // 接続状態を確定する：Disconnected／Connecting → Connected の変化があるときだけ
                        // ConnectionStateChanged が1回通知される（購読済みのタイトルUIはこれで Connected 表示へ更新される）。
                        //  ・この呼び出しより後に、このブロック内で状態を上書きする処理は無い
                        //    （購読者が通知中に Disconnected 等へ変えても、その状態を打ち消さない）。
                        SetConnectionState(ConnectionState.Connected);
                    });
                }

                continue;
            }

            // PING 以外のメッセージのみログキューへ積む（毎秒のPINGによる文字列生成・ログ出力を抑制）
            if (ParentUdpMessageParser.Parse(datagram.Text).Type != ParentMessageType.Ping)
            {
                EnqueueLog(LogType.Log, $"[ParentUdpSender] Normal received: '{datagram.Text}' from {datagram.RemoteEndPoint.Address}");
            }

            _receiveQueue.Enqueue(datagram.Text);
        }

        // 運用中に受信スレッドで起きた通信エラー（停止時に想定内の例外は通信基盤側で除外済み）
        while (_udpChannel.TryDequeueIssue(out UdpChannel.Issue issue))
        {
            string label = issue.Route == UdpChannelRoute.Discovery ? "ReceiveDiscovery" : "ReceiveNormalData";
            EnqueueLog(LogType.Error, $"[ParentUdpSender] {label} error: {issue.Message}");
        }
    }

    private void SendDiscoveryAccept(string ip)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + "DISCOVERY_ACCEPT");
            _udpChannel.Send(data, data.Length, ip, normalPort);
            Debug.Log($"[ParentUdpSender] Sent DISCOVERY_ACCEPT to {ip}:{normalPort}");
        }
        catch (Exception e) { Debug.LogError($"[ParentUdpSender] SendDiscoveryAccept error: {e.Message}"); }
    }

    // タイトルUIの表示更新（ラベル・Connect/Cancelボタンの表示）は ParentTitleConnectionUI が
    // ParentUdpSender.ConnectionStateChanged を受けて行う。

    // ── Helpers ───────────────────────────────────────────────────────────────
    // 挿入ロジック本体は RankingUtil に集約（子機と共有・単体テスト可能）。挙動は従来と同一。
    private static void UpdateRanking(string keyPrefix, int newScore)
    {
        int[] before = RankingUtil.ReadFromPlayerPrefs(keyPrefix, RankingSize);
        RankingUtil.InsertScoreToPlayerPrefs(keyPrefix, newScore, RankingSize);
        int[] after = RankingUtil.ReadFromPlayerPrefs(keyPrefix, RankingSize);

        Debug.Log($"[ParentUdpSender] UpdateRanking key='{keyPrefix}', before=[{string.Join(", ", before)}], " +
                  $"newScore={newScore}, after=[{string.Join(", ", after)}]");
    }
}
