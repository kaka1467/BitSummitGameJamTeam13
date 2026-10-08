using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
///
/// Inbound messages (child → parent, port 8002):
///   TEAM13_PING              — heartbeat from child
///   TEAM13_START_GAME        — child requests game start
///   TEAM13_TIME_UP           — child timer expired → parent game over
///   TEAM13_CHILD_DEAD        — child died → parent game over
///   TEAM13_CHILD_SCORE:&lt;val&gt; — child final score, write to PlayerPrefs for ranking
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
    private const string ResultGameOverScene = "GameOverResult";
    private const string ResultTimeUpScene   = "TimeUpResult";

    public enum ConnectionState { Disconnected, Connecting, Connected }

    // ── Inspector ─────────────────────────────────────────────────────────────
    public int    normalPort       = 8000;
    public int    broadcastPort    = 8001;
    public int    parentReceivePort = 8002;
    public string targetIP         = "127.0.0.1";
    public ConnectionState currentState = ConnectionState.Disconnected;

    public Button             connectButton;
    public TextMeshProUGUI    connectButtonLabel;
    public GameObject         startButtonObject;

    [Header("Solo Start（子機接続なしで開始）")]
    [Tooltip("子機の接続・START_GAME受信を待たずに、親機だけでゲームを開始するボタン。未設定でも動作するが、割り当てると押した瞬間に自動で非表示になる。")]
    public GameObject         soloStartButtonObject;
    public string             gameSceneName    = "GameScene";
    [Tooltip("Solo Start（子機接続なし）の遷移先シーン。MotherLoadは子機を無期限に待つため経由せず、直接このシーンへ行く。")]
    public string             soloGameSceneName = "GameScene";
    [Tooltip("実際にゲームプレイが行われるシーン名。LOUD_ITEM（ラッシュイン）の処理や ParentDetection の参照検索はこのシーンでだけ行う。" +
             "gameSceneName は『ゲーム開始時に最初に読み込むシーン（MotherLoad）』で、プレイ中のシーンとは別物。")]
    public string             gameplaySceneName = "GameScene";

    [Header("タイトルへ戻る（親機のキーボード）")]
    [Tooltip("ゲーム中などに、キーを長押しして親機をタイトル画面へ戻す。タイトル画面では無効。")]
    public bool               enableReturnToTitleKey = true;
    public Key                returnToTitleKey = Key.Escape;
    [Tooltip("誤操作防止のため、このキーをこの秒数だけ押し続けるとタイトルへ戻る。")]
    public float              returnToTitleHoldSeconds = 1.5f;
    [Tooltip("戻る先の親機タイトルシーン名。")]
    public string             motherTitleSceneName = "MotherTitle";
    public string             titleSceneName   = "Mini Title";
    public string             gameOverSceneName = "GameOverResult";
    public string             timeUpSceneName   = "TimeUpResult";
    public Button             cancelButton;

    // ── PlayerPrefs Keys（結果・ランキング保存用） ─────────────────────────────
    // 親機・子機で同じPlayerPrefs保存形式（キー文字列・降順TOP5）を使用します。
    // 既存セーブデータおよびランキングとの互換性を保つため、キー文字列の値は絶対に変更しないでください。
    private const string KeyGameOverScore  = "LastGameOverScore";
    private const string KeyTimeUpScore    = "LastTimeUpScore";
    private const string KeyGameOverRank   = "GameOverRank_";
    private const string KeyTimeUpRank     = "TimeUpRank_";
    private const int    RankingSize       = 5;

    [Header("Game References")]
    [Tooltip("Auto-found at Start if not assigned. Used to trigger rush-in on LOUD_ITEM.")]
    public ParentDetection parentDetection;

    [Header("Debug")]
    [Tooltip("通信ログなどの詳細出力を有効にする")]
    [SerializeField] private bool showDebugLogs = true;

    // ── Private networking ────────────────────────────────────────────────────
    private UdpClient _udpClient;
    private UdpClient _receiveClient;
    private UdpClient _normalReceiveClient;
    private Thread    _receiveThread;
    private Thread    _normalReceiveThread;
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
    private float     _lastReceiveTime;
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
    private bool      _gameOverScoreHandled = false; // 子機からの CHILD_SCORE:GAME_OVER の再送（重複）を無視するため
    public  bool      ChildLoadingComplete { get; set; } = false;
    private bool      _shouldTriggerLoudItem = false;

    public static ParentUdpSender Instance { get; private set; }

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
    }

    // ── Button callbacks ──────────────────────────────────────────────────────
    public void OnConnectButtonClicked()
    {
        ConnectionState before = currentState;
        currentState = ConnectionState.Connecting;
        Debug.Log($"[ParentUdpSender][Diag] OnConnectButtonClicked called — scene='{SceneManager.GetActiveScene().name}', " +
                  $"state {before} → Connecting, _gameStarted={_gameStarted}, listening on broadcastPort={broadcastPort}.");
    }
    public void OnCancelButtonClicked()
    {
        Debug.Log($"[ParentUdpSender][Diag] OnCancelButtonClicked called — scene='{SceneManager.GetActiveScene().name}', state {currentState} → Disconnected.");
        currentState = ConnectionState.Disconnected;
    }
    public void OnStartButtonClicked()
    {
        // 二重開始防止: すでに開始済みなら何もしない（Start/SoloStart を連打しても遷移は1回だけ）。
        if (_gameStarted) return;
        _gameStarted = true;
        StartCoroutine(StartGameRoutine());
    }

    /// <summary>
    /// 子機の接続状態に関係なく、親機だけでゲームを開始する。
    /// Connect Button の隣などに置く専用ボタンの OnClick から呼ぶ想定。
    /// タイトルBGMのフェードアウトなど、子機起点の開始（START_GAME受信）と同じ後処理を通る。
    /// </summary>
    public void OnSoloStartButtonClicked()
    {
        // 単体開始が押されたことと、その時点の状態を必ず記録する（「Connecting表示」診断用）。
        Debug.Log($"[ParentUdpSender][Diag] OnSoloStartButtonClicked called — scene='{SceneManager.GetActiveScene().name}', " +
                  $"state={currentState}, _gameStarted={_gameStarted}, " +
                  $"soloStartButtonObject={(soloStartButtonObject != null ? soloStartButtonObject.activeSelf.ToString() : "null")}.");

        if (_gameStarted)
        {
            Debug.Log("[ParentUdpSender][Diag] OnSoloStartButtonClicked ignored — already started (_gameStarted=true).");
            return;
        }
        _gameStarted = true;

        // 単体開始は親機が起点なので、親機が新しいプレイ識別子を発行する。
        // （子機接続済みで START_GAME を送る場合も、この識別子を共有させる）
        _currentPlaySessionId = CreatePlaySessionId();

        Debug.Log($"[ParentUdpSender] OnSoloStartButtonClicked — starting without waiting for child connection. playSessionId={_currentPlaySessionId}");

        // 子機がたまたま接続済みなら合わせて開始通知を送る（未接続時はSendState内で無視される）
        SendState($"{CmdStart}:{_currentPlaySessionId}");

        // soloStartButtonObject の非表示は TitleMenuHighlight.FlashAndDeactivate 側が
        // フラッシュ演出の完了後に行う（ここで即座に隠すと演出が表示されないため、外してある）。

        // MotherLoad（子機を無期限に待つ）は経由せず、直接ゲームシーンへ遷移する
        StartCoroutine(LoadSceneAfterBgmFade(soloGameSceneName));
    }

    private IEnumerator StartGameRoutine()
    {
        // 親機が起点の開始でも、プレイ識別子を親機が発行して子機と共有する。
        _currentPlaySessionId = CreatePlaySessionId();
        SendState($"{CmdStart}:{_currentPlaySessionId}");
        Debug.Log($"[ParentUdpSender] Sent START_GAME (playSessionId={_currentPlaySessionId}) to child at {targetIP}:{normalPort}");
        yield return new WaitForSeconds(0.1f);
        yield return LoadSceneAfterBgmFade(gameSceneName);
    }

    // タイトルBGMのフェードアウトと画面の暗転フェードを両方走らせる。
    //
    // 重要（ロード画面表示の遅延対策）:
    //   以前は「BGMフェード完了」と「画面フェード完了」の長い方（= BGMの3秒）まで待ってから
    //   遷移していたため、子機が開始しても親機のロード画面（MotherLoad）が出るまで3秒かかっていた。
    //   BGMフェードは音の演出であり、ロード画面の表示を待たせる必要はない。
    //   そのため待機時間は「画面フェード（暗転）の完了」だけを基準にし、BGMフェードは待たない。
    //
    //   TitleBgmFader の AudioSource はタイトルシーン内のオブジェクトにあり、DontDestroyOnLoad ではない。
    //   そのためシーン遷移時に AudioSource ごと破棄され、フェード途中でも安全に終了する（音源リークや例外は無い）。
    //   暗転が完了してから遷移するので、フェード途中で切れるBGMは画面が黒い状態で途切れ、耳障りになりにくい。
    private IEnumerator LoadSceneAfterBgmFade(string sceneName)
    {
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
            yield return new WaitForSecondsRealtime(waitSeconds);
        }

        SceneManager.LoadScene(sceneName);
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────
    void Start()
    {
        // Force result scenes to shared names across parent/child.
        gameOverSceneName = ResultGameOverScene;
        timeUpSceneName = ResultTimeUpScene;

        SceneManager.sceneLoaded += OnSceneLoaded;
        RefreshSceneReferences();
        RefreshUiReferences();
        AttachUiListeners();

        _isRunning = true;

        _udpClient           = new UdpClient();
        _receiveClient       = new UdpClient(broadcastPort);
        _normalReceiveClient = new UdpClient(parentReceivePort);
        Debug.Log($"[ParentUdpSender] Sockets open — listening for discovery on :{broadcastPort}, normal data on :{parentReceivePort}. Initial targetIP='{targetIP}'");

        _receiveThread = new Thread(ReceiveDiscovery) { IsBackground = true };
        _receiveThread.Start();

        _normalReceiveThread = new Thread(ReceiveNormalData) { IsBackground = true };
        _normalReceiveThread.Start();
    }

    void Update()
    {
        while (_actionQueue.TryDequeue(out Action action))
            action();

        while (_receiveQueue.TryDequeue(out string raw))
            HandleIncoming(raw);

        ProcessLogQueue();

        HandleReturnToTitleKey();

        if (_shouldTriggerLoudItem)
        {
            string activeScene = SceneManager.GetActiveScene().name;
            if (!IsGameplayScene(activeScene))
            {
                // GameScene以外ではLOUD_ITEM処理を行わずリセット
                _shouldTriggerLoudItem = false;
            }
            else if (parentDetection != null)
            {
                _shouldTriggerLoudItem = false;
                if (showDebugLogs)
                    Debug.Log("[ParentUdpSender] Executing OnLoudItemTriggered on Main Thread!");
                parentDetection.OnLoudItemTriggered();
            }
            else
            {
                // GameScene内で万が一parentDetectionがnullの場合は毎フレーム再検索せず1度だけ警告して破棄
                _shouldTriggerLoudItem = false;
                if (showDebugLogs)
                    Debug.LogWarning("[ParentUdpSender] LOUD_ITEM triggered but parentDetection is null in GameScene.");
            }
        }

        // Timeout check
        if (currentState == ConnectionState.Connected &&
            Time.time - _lastReceiveTime > _timeoutLimit)
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName == "MotherLoad")
            {
                // Keep connection alive during loading to avoid false timeouts.
                _lastReceiveTime = Time.time;
            }
            else
            {
                currentState = ConnectionState.Disconnected;
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

        // UI
        UpdateUI();

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

        // Close sockets — this unblocks the blocking Receive() calls so threads exit naturally.
        CloseClient(ref _udpClient,           "_udpClient");
        CloseClient(ref _receiveClient,       "_receiveClient");
        CloseClient(ref _normalReceiveClient, "_normalReceiveClient");
    }

    // ── Scene reference refresh ──────────────────────────────────────────────
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _returningToTitle = false;
        _returnToTitleHeld = 0f;

        if (showDebugLogs)
            Debug.Log($"[ParentUdpSender] Scene loaded: '{scene.name}' — refreshing scene references.");
        RefreshSceneReferences();
        RefreshUiReferences();
        AttachUiListeners();

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
            _gameOverScoreHandled = false;
            _shouldTriggerLoudItem = false;
            if (_caughtRetryCoroutine != null)
            {
                StopCoroutine(_caughtRetryCoroutine);
                _caughtRetryCoroutine = null;
            }
            if (showDebugLogs)
                Debug.Log("[ParentUdpSender] _resultProcessed reset for new game session.");
        }
        else
        {
            // GameScene 以外へ遷移したときは _shouldTriggerLoudItem を安全に初期化
            _shouldTriggerLoudItem = false;
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
    public void NotifyReturnToTitleToChild()
    {
        NotifyReturnToTitle();

        if (_returnToTitleNotifyCoroutine != null)
        {
            StopCoroutine(_returnToTitleNotifyCoroutine);
        }
        _returnToTitleNotifyCoroutine = StartCoroutine(ReturnToTitleNotifyRoutine());
    }

    /// <summary>
    /// RETURN_TO_TITLE の再送処理（パケットロス対策）。
    /// 0.15秒間隔で計3回再送（即時送信と合わせて計4回送信）。
    /// 再送は同じ連番（直近の _returnToTitleSendSeq）で行うため、受信側は2回目以降を重複として正しく無視できる。
    /// </summary>
    private IEnumerator ReturnToTitleNotifyRoutine()
    {
        const int retryCount = 3;
        const float retryInterval = 0.15f;

        // 再送は「同じ識別子・同じ番号」で送る（受信側は2回目以降を重複として無視できる）。
        int seq = _returnToTitleSendSeq;
        string sid = _currentPlaySessionId;
        for (int i = 0; i < retryCount; i++)
        {
            yield return new WaitForSecondsRealtime(retryInterval);
            if (showDebugLogs)
                Debug.Log($"[ParentUdpSender] Sending RETURN_TO_TITLE retry ({i + 1}/{retryCount}) sid={sid} seq={seq}...");
            SendState($"{parentReturnToTitle}:{sid}:{seq}");
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
    private void HandleTeamReturnToTitle(string playSessionId, int seq)
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
                Debug.Log("[ParentUdpSender] RETURN_TO_TITLE received — starting result fade to title.");
            changer.StartFadeToTitle(notifyPeer: false);
            return;
        }

        Debug.Log("[ParentUdpSender] RETURN_TO_TITLE received — returning to title immediately.");
        ReturnToTitle(notifyPeer: false);
    }

    // 実際のゲームプレイシーンか。gameSceneName は最初に読み込む MotherLoad を指すため、
    // プレイ中の判定には使えない（使うと LOUD_ITEM が常に無視され、ラッシュインが起きない）。
    private bool IsGameplayScene(string sceneName)
    {
        return sceneName == gameplaySceneName;
    }

    private bool IsTitleScene(string sceneName)
    {
        return sceneName == titleSceneName ||
               sceneName == "TitleScene" ||
               sceneName == "Title" ||
               sceneName.Contains("Title") ||
               sceneName == "Mini Title";
    }

    private void AttachUiListeners()
    {
        if (connectButton != null)
        {
            connectButton.onClick.RemoveAllListeners();
            connectButton.onClick.AddListener(OnConnectButtonClicked);
        }

        if (cancelButton != null)
        {
            cancelButton.onClick.RemoveAllListeners();
            cancelButton.onClick.AddListener(OnCancelButtonClicked);
        }
    }

    public void ResetForNewSession()
    {
        currentState = ConnectionState.Disconnected;
        targetIP = "127.0.0.1";
        _lastReceiveTime = 0f;
        _gameStarted = false;
        _resultProcessed = false;
        _gameOverScoreHandled = false;
        ChildLoadingComplete = false;
        _shouldTriggerLoudItem = false;

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

        Debug.Log("[ParentUdpSender] ResetForNewSession: session flags cleared.");
    }

    private void RefreshSceneReferences()
    {
        string currentScene = SceneManager.GetActiveScene().name;
        if (IsGameplayScene(currentScene))
        {
            parentDetection = UnityEngine.Object.FindFirstObjectByType<ParentDetection>();
            if (parentDetection != null)
            {
                if (showDebugLogs)
                    Debug.Log($"[ParentUdpSender] parentDetection found: '{parentDetection.gameObject.name}'.");
            }
            else
            {
                if (showDebugLogs)
                    Debug.LogWarning("[ParentUdpSender] parentDetection not found in GameScene.");
            }
        }
        else
        {
            parentDetection = null;
            // GameScene以外ではparentDetectionを探さずログも出さない（Missingログの大量出力を防止）
        }
    }

    private void RefreshUiReferences()
    {
        connectButton = FindButton(connectButton, "Connect Button", "ConnectButton");
        if (connectButtonLabel == null && connectButton != null)
            connectButtonLabel = connectButton.GetComponentInChildren<TextMeshProUGUI>(true);

        cancelButton = FindButton(cancelButton, "cancel button", "CancelButton");

        if (startButtonObject == null)
        {
            GameObject startButton = GameObject.Find("Start Button");
            if (startButton == null)
                startButton = GameObject.Find("StartButton");
            if (startButton != null)
                startButtonObject = startButton;
        }
    }

    // ── Public send API ───────────────────────────────────────────────────────
    public void SendState(string message)
    {
        if (currentState != ConnectionState.Connected)
            return;

        if (showDebugLogs)
            Debug.Log($"[ParentUdpSender] → '{message}' to {targetIP}:{normalPort} | connectionState={currentState}");

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + message);
            _udpClient.Send(data, data.Length, targetIP, normalPort);
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
    public void NotifyReturnToTitle()
    {
        _returnToTitleSendSeq++;
        SendState($"{parentReturnToTitle}:{_currentPlaySessionId}:{_returnToTitleSendSeq}");
        Debug.Log($"[ParentUdpSender] Sent RETURN_TO_TITLE (playSessionId={_currentPlaySessionId}, seq={_returnToTitleSendSeq}) to child.");
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

        if (_udpClient == null)
        {
            Debug.LogError($"[ParentUdpSender] CAUGHT send failed: _udpClient is null, targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}'");
            return;
        }

        Debug.Log($"[ParentUdpSender] CAUGHT send attempt: targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}'");

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            _udpClient.Send(data, data.Length, targetIP, normalPort);
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
        if (currentState != ConnectionState.Disconnected) return;
        if (IsTitleScene(SceneManager.GetActiveScene().name)) return;

        currentState = ConnectionState.Connected;
        Debug.LogWarning("[ParentUdpSender] PING resumed after timeout — connection restored.");
    }

    // ── Incoming message dispatch (main thread) ───────────────────────────────
    private void HandleIncoming(string raw)
    {
        ParentUdpMessage message = ParentUdpMessageParser.Parse(raw);
        if (message.Type == ParentMessageType.Invalid)
        {
            if (!string.IsNullOrEmpty(message.ParseError))
                Debug.LogWarning(message.ParseError);
            return;
        }

        if (message.Type != ParentMessageType.Ping && showDebugLogs)
            Debug.Log($"[ParentUdpSender] HandleIncoming: '{message.RawPayload}' | scene='{SceneManager.GetActiveScene().name}' | parentDetection={(parentDetection != null ? parentDetection.gameObject.name : "NULL")} | _resultProcessed={_resultProcessed}");

        // 子機から何か届いた＝子機は生きている。PING 以外のメッセージでも生存時刻を更新する。
        _lastReceiveTime = Time.time;

        if (message.Type == ParentMessageType.Ping)
        {
            TryRecoverConnectionFromPing();
            return;
        }

        if (message.Type == ParentMessageType.StartGame)
        {
            if (!_gameStarted && currentState == ConnectionState.Connected)
            {
                _gameStarted = true;

                // 子機が発行したプレイ識別子を採用する（旧フォーマットで未付与なら親機側で発行）。
                // これで両端末が同じ識別子を共有し、次プレイの RETURN_TO_TITLE と区別できる。
                _currentPlaySessionId = string.IsNullOrEmpty(message.PlaySessionId)
                    ? CreatePlaySessionId()
                    : message.PlaySessionId;

                if (showDebugLogs)
                    Debug.Log($"[ParentUdpSender] Received START_GAME from child — loading game scene. playSessionId={_currentPlaySessionId}");
                StartCoroutine(LoadSceneAfterBgmFade(gameSceneName));
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
            if (message.ResultType == ChildGameResultType.GameOver)
            {
                // 子機はパケットロス対策で同じリザルトを複数回送ってくる。2回目以降は無視する
                // （無視しないとランキングに同じスコアが重複登録されてしまう）。
                if (_gameOverScoreHandled)
                {
                    if (showDebugLogs)
                        Debug.Log("[ParentUdpSender] CHILD_SCORE GAME_OVER ignored — already handled (retransmission).");
                    return;
                }
                _gameOverScoreHandled = true;

                // GAME_OVER wins the race unconditionally
                _resultProcessed = true;
                Debug.Log($"[ParentUdpSender] CHILD_SCORE GAME_OVER {message.Score} — saving and loading {ResultGameOverScene}.");
                PlayerPrefs.SetInt(KeyGameOverScore, message.Score);
                UpdateRanking(KeyGameOverRank, message.Score);
                PlayerPrefs.Save();
                ResultDataCommitted?.Invoke();
                if (SceneManager.GetActiveScene().name != ResultGameOverScene)
                {
                    SceneManager.LoadScene(ResultGameOverScene);
                }
            }
            else // TIME_UP
            {
                if (_resultProcessed)
                {
                    Debug.Log("[ParentUdpSender] TIME_UP result ignored — result (e.g. GAME_OVER) already processed.");
                    return;
                }
                _resultProcessed = true;
                Debug.Log($"[ParentUdpSender] CHILD_SCORE TIME_UP {message.Score} — saving and loading {ResultTimeUpScene}.");
                PlayerPrefs.SetInt(KeyTimeUpScore, message.Score);
                UpdateRanking(KeyTimeUpRank, message.Score);
                PlayerPrefs.Save();
                ResultDataCommitted?.Invoke();
                SceneManager.LoadScene(ResultTimeUpScene);
            }
            return;
        }

        if (message.Type == ParentMessageType.TeamReturnToTitle)
        {
            HandleTeamReturnToTitle(message.PlaySessionId, message.Score);
            return;
        }

        if (message.Type == ParentMessageType.LoadingComplete)
        {
            ChildLoadingComplete = true;
            Debug.Log("[ParentUdpSender] Received LOADING_COMPLETE from child. Property set to true.");
            return;
        }

        if (message.Type == ParentMessageType.LoudItem)
        {
            if (showDebugLogs)
                Debug.Log("[ParentUdpSender] Received LOUD_ITEM network packet from Child.");

            string activeScene = SceneManager.GetActiveScene().name;
            if (!IsGameplayScene(activeScene))
            {
                if (showDebugLogs)
                    Debug.Log($"[ParentUdpSender] LOUD_ITEM received outside gameplay scene (active='{activeScene}', expected='{gameplaySceneName}') — ignored.");
                return;
            }

            if (parentDetection != null)
            {
                parentDetection.OnLoudItemTriggered();
            }
            else
            {
                if (showDebugLogs)
                    Debug.LogWarning("[ParentUdpSender] LOUD_ITEM received but parentDetection is null in GameScene — will retry in Update.");
                _shouldTriggerLoudItem = true;
            }
            return;
        }

        if (message.Type == ParentMessageType.Unknown)
            Debug.Log($"[ParentUdpSender] Unhandled message: '{message.RawPayload}'");
    }

    // ── Coroutines ────────────────────────────────────────────────────────────
    private IEnumerator HeartbeatCoroutine()
    {
        while (currentState == ConnectionState.Connected)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(MagicNumber + "PING");
                _udpClient.Send(data, data.Length, targetIP, normalPort);
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

    // ── Background receive threads ────────────────────────────────────────────
    private void ReceiveDiscovery()
    {
        while (_isRunning)
        {
            try
            {
                IPEndPoint ep   = new IPEndPoint(IPAddress.Any, broadcastPort);
                byte[]     data = _receiveClient.Receive(ref ep);
                string     msg  = Encoding.UTF8.GetString(data);
                EnqueueLog(LogType.Log, $"[ParentUdpSender] Broadcast received: '{msg}' from {ep.Address}");

                ParentUdpMessage message = ParentUdpMessageParser.Parse(msg);
                if (message.Type == ParentMessageType.DiscoveryRequest)
                {
                    string senderIP = ep.Address.ToString();
                    EnqueueLog(LogType.Log, $"[ParentUdpSender] DISCOVERY_REQUEST from {senderIP} — queuing targetIP update and DISCOVERY_ACCEPT.");
                    _actionQueue.Enqueue(() =>
                    {
                        string oldIP = targetIP;
                        ConnectionState oldState = currentState;
                        targetIP         = senderIP;
                        currentState     = ConnectionState.Connected;
                        _lastReceiveTime  = Time.time;
                        _gameStarted      = false;
                        if (showDebugLogs)
                            Debug.Log($"[ParentUdpSender][Diag] DISCOVERY_REQUEST受信により state {oldState} → Connected, targetIP '{oldIP}' → '{targetIP}'");
                        SendDiscoveryAccept(senderIP);
                    });
                }
            }
            catch (Exception e)
            {
                if (_isRunning)
                {
                    EnqueueLog(LogType.Error, $"[ParentUdpSender] ReceiveDiscovery error: {e.Message}");
                }
            }
        }
    }

    private void ReceiveNormalData()
    {
        while (_isRunning)
        {
            try
            {
                IPEndPoint ep   = new IPEndPoint(IPAddress.Any, parentReceivePort);
                byte[]     data = _normalReceiveClient.Receive(ref ep);
                string     msg  = Encoding.UTF8.GetString(data);

                // PING 以外のメッセージのみログキューへ積む（毎秒のPINGによる文字列生成・ログ出力を抑制）
                if (ParentUdpMessageParser.Parse(msg).Type != ParentMessageType.Ping)
                {
                    EnqueueLog(LogType.Log, $"[ParentUdpSender] Normal received: '{msg}' from {ep.Address}");
                }

                _receiveQueue.Enqueue(msg);
            }
            catch (Exception e)
            {
                if (_isRunning)
                {
                    EnqueueLog(LogType.Error, $"[ParentUdpSender] ReceiveNormalData error: {e.Message}");
                }
            }
        }
    }

    private void SendDiscoveryAccept(string ip)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + "DISCOVERY_ACCEPT");
            _udpClient.Send(data, data.Length, ip, normalPort);
            Debug.Log($"[ParentUdpSender] Sent DISCOVERY_ACCEPT to {ip}:{normalPort}");
        }
        catch (Exception e) { Debug.LogError($"[ParentUdpSender] SendDiscoveryAccept error: {e.Message}"); }
    }

    // ── UI ────────────────────────────────────────────────────────────────────
    // 診断用: 直前に connectButtonLabel へ書き込んだ文字列。変化時のみログを出す（毎フレームの出力を防ぐ）。
    private string _lastAppliedConnectLabel = null;

    private void UpdateUI()
    {
        if (connectButtonLabel != null)
        {
            string newLabel;
            switch (currentState)
            {
                case ConnectionState.Disconnected: newLabel = "Connect";     break;
                case ConnectionState.Connecting:   newLabel = "Connecting..."; break;
                case ConnectionState.Connected:    newLabel = "STARTING...";  break;
                default:                           newLabel = connectButtonLabel.text; break;
            }

            // 「Connecting...」がどのタイミングで表示されたかを追えるようにする（単体開始の診断用）。
            if (newLabel != _lastAppliedConnectLabel)
            {
                Debug.Log($"[ParentUdpSender][Diag] connectButtonLabel: '{_lastAppliedConnectLabel}' → '{newLabel}' " +
                          $"| state={currentState} | scene='{SceneManager.GetActiveScene().name}' " +
                          $"| soloStartButtonObject={(soloStartButtonObject != null ? soloStartButtonObject.activeSelf.ToString() : "null")} " +
                          $"| _gameStarted={_gameStarted}");
                _lastAppliedConnectLabel = newLabel;
            }

            connectButtonLabel.text = newLabel;
        }

        if (connectButton   != null) connectButton.gameObject.SetActive(true);
        if (cancelButton    != null) cancelButton.gameObject.SetActive(currentState == ConnectionState.Connecting);
        if (startButtonObject != null) startButtonObject.SetActive(currentState == ConnectionState.Connected);
    }

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

    private static void CloseClient(ref UdpClient client, string label)
    {
        if (client == null) return;
        try   { client.Close(); client.Dispose(); }
        catch (Exception e) { Debug.LogWarning($"[ParentUdpSender] Error closing {label}: {e.Message}"); }
        client = null;
    }

    private static Button FindButton(Button current, params string[] candidateNames)
    {
        if (current != null)
            return current;

        foreach (string candidateName in candidateNames)
        {
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!candidate.scene.IsValid() || !candidate.scene.isLoaded)
                    continue;

                if (candidate.name != candidateName)
                    continue;

                Button button = candidate.GetComponent<Button>();
                if (button != null)
                    return button;
            }
        }

        return null;
    }
}