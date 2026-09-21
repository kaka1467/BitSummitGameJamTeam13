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
///
/// Inbound messages (child → parent, port 8002):
///   TEAM13_PING              — heartbeat from child
///   TEAM13_START_GAME        — child requests game start
///   TEAM13_TIME_UP           — child timer expired → parent game over
///   TEAM13_CHILD_DEAD        — child died → parent game over
///   TEAM13_CHILD_SCORE:<val> — child final score, write to PlayerPrefs for ranking
///   TEAM13_LOUD_ITEM         — child picked up loud item → trigger rush-in
/// </summary>
public class ParentUdpSender : MonoBehaviour
{
    private const string MAGIC_NUMBER = "TEAM13_";
    private const string CMD_START    = "START_GAME";
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
    public string             gameSceneName    = "GameScene";
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
    public ParentDetectionV2 parentDetection;

    [Header("Debug")]
    [Tooltip("通信ログなどの詳細出力を有効にする")]
    [SerializeField] private bool showDebugLogs = true;

    // ── Private networking ────────────────────────────────────────────────────
    private UdpClient udpClient;
    private UdpClient receiveClient;
    private UdpClient normalReceiveClient;
    private Thread    receiveThread;
    private Thread    normalReceiveThread;
    private volatile bool isRunning = false;

    // ログ用のエントリ構造体
    private struct LogEntry
    {
        public LogType type;
        public string message;

        public LogEntry(LogType type, string message)
        {
            this.type = type;
            this.message = message;
        }
    }

    private readonly ConcurrentQueue<Action> actionQueue  = new ConcurrentQueue<Action>();
    private readonly ConcurrentQueue<string> receiveQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<LogEntry> logQueue   = new ConcurrentQueue<LogEntry>();

    private Coroutine heartbeatCoroutine;
    private Coroutine caughtRetryCoroutine;
    private float     lastReceiveTime;
    private float     pingInterval  = 1.0f;
    private float     timeoutLimit  = 3.0f;
    private bool      gameStarted        = false;
    private bool      resultProcessed    = false; // GAME_OVER wins race
    public  bool      ChildLoadingComplete { get; set; } = false;
    private bool      _shouldTriggerLoudItem = false;

    public static ParentUdpSender instance { get; private set; }

    // ── Singleton / DontDestroyOnLoad ──────────────────────────────────────────
    void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.Log("[ParentUdpSender] Duplicate detected — destroying self.");
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ── Button callbacks ──────────────────────────────────────────────────────
    public void OnConnectButtonClicked()
    {
        currentState = ConnectionState.Connecting;
        Debug.Log($"[ParentUdpSender] OnConnectButtonClicked — state=Connecting, listening on broadcastPort={broadcastPort}. Waiting for child DISCOVERY_REQUEST.");
    }
    public void OnCancelButtonClicked()   { currentState = ConnectionState.Disconnected; }
    public void OnStartButtonClicked()    { StartCoroutine(StartGameRoutine()); }

    private IEnumerator StartGameRoutine()
    {
        SendState(CMD_START);
        Debug.Log($"[ParentUdpSender] Sent START_GAME to child at {targetIP}:{normalPort}");
        yield return new WaitForSeconds(0.1f);
        SceneManager.LoadScene(gameSceneName);
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

        isRunning = true;

        udpClient           = new UdpClient();
        receiveClient       = new UdpClient(broadcastPort);
        normalReceiveClient = new UdpClient(parentReceivePort);
        Debug.Log($"[ParentUdpSender] Sockets open — listening for discovery on :{broadcastPort}, normal data on :{parentReceivePort}. Initial targetIP='{targetIP}'");

        receiveThread = new Thread(ReceiveDiscovery) { IsBackground = true };
        receiveThread.Start();

        normalReceiveThread = new Thread(ReceiveNormalData) { IsBackground = true };
        normalReceiveThread.Start();
    }

    void Update()
    {
        while (actionQueue.TryDequeue(out Action action))
            action();

        while (receiveQueue.TryDequeue(out string raw))
            HandleIncoming(raw);

        ProcessLogQueue();

        if (_shouldTriggerLoudItem)
        {
            string activeScene = SceneManager.GetActiveScene().name;
            if (activeScene != gameSceneName)
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
            Time.time - lastReceiveTime > timeoutLimit)
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName == "MotherLoad")
            {
                // Keep connection alive during loading to avoid false timeouts.
                lastReceiveTime = Time.time;
            }
            else
            {
                currentState = ConnectionState.Disconnected;
                Debug.LogWarning("[ParentUdpSender] Connection timed out — child heartbeat lost.");
            }
        }

        // Heartbeat coroutine lifecycle
        if (currentState == ConnectionState.Connected && heartbeatCoroutine == null)
            heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
        else if (currentState != ConnectionState.Connected && heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }

        // UI
        UpdateUI();

        // Debug / hardware input: I key sends CAUGHT (Space and Gamepad A reserved for SleepingController)
        if (Keyboard.current != null && Keyboard.current.iKey.wasPressedThisFrame)
            NotifyGameOverFromParentCatch();

        // Debug keys: Y = SLEEP_LOCK, U = SLEEP_UNLOCK
        // (O/P/L are reserved by ParentDetectionV2)
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.yKey.wasPressedThisFrame)
            {
                Debug.Log("[ParentUdpSender] Debug key Y pressed — sending SLEEP_LOCK.");
                SendStateSLEEP_LOCK();
            }
            if (keyboard.uKey.wasPressedThisFrame)
            {
                Debug.Log("[ParentUdpSender] Debug key U pressed — sending SLEEP_UNLOCK.");
                SendStateSLEEP_UNLOCK();
            }
        }
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (instance == this) instance = null;
        isRunning = false;

        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }

        if (caughtRetryCoroutine != null)
        {
            StopCoroutine(caughtRetryCoroutine);
            caughtRetryCoroutine = null;
        }

        // Close sockets — this unblocks the blocking Receive() calls so threads exit naturally.
        CloseClient(ref udpClient,           "udpClient");
        CloseClient(ref receiveClient,       "receiveClient");
        CloseClient(ref normalReceiveClient, "normalReceiveClient");
    }

    // ── Scene reference refresh ──────────────────────────────────────────────
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
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
        if (scene.name == gameSceneName)
        {
            resultProcessed = false;
            _shouldTriggerLoudItem = false;
            if (caughtRetryCoroutine != null)
            {
                StopCoroutine(caughtRetryCoroutine);
                caughtRetryCoroutine = null;
            }
            if (showDebugLogs)
                Debug.Log("[ParentUdpSender] resultProcessed reset for new game session.");
        }
        else
        {
            // GameScene 以外へ遷移したときは _shouldTriggerLoudItem を安全に初期化
            _shouldTriggerLoudItem = false;
        }
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
        lastReceiveTime = 0f;
        gameStarted = false;
        resultProcessed = false;
        ChildLoadingComplete = false;
        _shouldTriggerLoudItem = false;

        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }

        if (caughtRetryCoroutine != null)
        {
            StopCoroutine(caughtRetryCoroutine);
            caughtRetryCoroutine = null;
        }

        Debug.Log("[ParentUdpSender] ResetForNewSession: session flags cleared.");
    }

    private void RefreshSceneReferences()
    {
        string currentScene = SceneManager.GetActiveScene().name;
        if (currentScene == gameSceneName)
        {
            parentDetection = UnityEngine.Object.FindFirstObjectByType<ParentDetectionV2>();
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
            byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + message);
            udpClient.Send(data, data.Length, targetIP, normalPort);
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
    /// 親機の捕獲によるGame Over確定を通知する。
    /// 初回呼び出し時のみ resultProcessed を true にし、CAUGHT を即時送信および短時間再送する。
    /// </summary>
    public void NotifyGameOverFromParentCatch()
    {
        if (resultProcessed && caughtRetryCoroutine != null)
            return;

        resultProcessed = true;

        if (showDebugLogs)
            Debug.Log($"[ParentUdpSender] NotifyGameOverFromParentCatch: 親機の捕獲によるゲームオーバー確定。CAUGHT送信・再送を開始します。targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='TEAM13_CAUGHT'");

        // 即時送信
        SendCaughtNotification();

        // 短時間再送コルーチン（DontDestroyOnLoadのParentUdpSender上で実行）
        if (caughtRetryCoroutine != null)
        {
            StopCoroutine(caughtRetryCoroutine);
        }
        caughtRetryCoroutine = StartCoroutine(CaughtRetryRoutine());
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

        caughtRetryCoroutine = null;
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

        if (udpClient == null)
        {
            Debug.LogError($"[ParentUdpSender] CAUGHT send failed: udpClient is null, targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}'");
            return;
        }

        Debug.Log($"[ParentUdpSender] CAUGHT send attempt: targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}'");

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            udpClient.Send(data, data.Length, targetIP, normalPort);
            Debug.Log($"[ParentUdpSender] CAUGHT send succeeded: targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}'");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ParentUdpSender] CAUGHT send failed: targetIP='{targetIP}', targetPort={normalPort}, connectionState={currentState}, message='{message}', error='{e.Message}'");
        }
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
            Debug.Log($"[ParentUdpSender] HandleIncoming: '{message.RawPayload}' | scene='{SceneManager.GetActiveScene().name}' | parentDetection={(parentDetection != null ? parentDetection.gameObject.name : "NULL")} | resultProcessed={resultProcessed}");

        if (message.Type == ParentMessageType.Ping)
        {
            lastReceiveTime = Time.time;
            return;
        }

        if (message.Type == ParentMessageType.StartGame)
        {
            if (!gameStarted && currentState == ConnectionState.Connected)
            {
                gameStarted = true;
                if (showDebugLogs)
                    Debug.Log("[ParentUdpSender] Received START_GAME from child — loading game scene.");
                SceneManager.LoadScene(gameSceneName);
            }
            return;
        }

        if (message.Type == ParentMessageType.TimeUp || message.Type == ParentMessageType.ChildDead)
        {
            // Legacy bare TIME_UP / CHILD_DEAD — treat as TIME_UP result
            if (!resultProcessed)
            {
                resultProcessed = true;
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
                // GAME_OVER wins the race unconditionally
                resultProcessed = true;
                Debug.Log($"[ParentUdpSender] CHILD_SCORE GAME_OVER {message.Score} — saving and loading {ResultGameOverScene}.");
                PlayerPrefs.SetInt(KeyGameOverScore, message.Score);
                UpdateRanking(KeyGameOverRank, message.Score);
                PlayerPrefs.Save();
                if (SceneManager.GetActiveScene().name != ResultGameOverScene)
                {
                    SceneManager.LoadScene(ResultGameOverScene);
                }
            }
            else // TIME_UP
            {
                if (resultProcessed)
                {
                    Debug.Log("[ParentUdpSender] TIME_UP result ignored — result (e.g. GAME_OVER) already processed.");
                    return;
                }
                resultProcessed = true;
                Debug.Log($"[ParentUdpSender] CHILD_SCORE TIME_UP {message.Score} — saving and loading {ResultTimeUpScene}.");
                PlayerPrefs.SetInt(KeyTimeUpScore, message.Score);
                UpdateRanking(KeyTimeUpRank, message.Score);
                PlayerPrefs.Save();
                SceneManager.LoadScene(ResultTimeUpScene);
            }
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
            if (activeScene != gameSceneName)
            {
                if (showDebugLogs)
                    Debug.Log("[ParentUdpSender] LOUD_ITEM received outside GameScene — ignored.");
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
                byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + "PING");
                udpClient.Send(data, data.Length, targetIP, normalPort);
            }
            catch (Exception e) { Debug.LogError($"[ParentUdpSender] Heartbeat error: {e.Message}"); }
            yield return new WaitForSeconds(pingInterval);
        }
    }

    // ── Log queue processing ──────────────────────────────────────────────────
    /// <summary>
    /// 受信スレッドなどの別スレッドからログをキューに追加する
    /// </summary>
    private void EnqueueLog(LogType type, string message)
    {
        logQueue.Enqueue(new LogEntry(type, message));
    }

    /// <summary>
    /// 受信スレッドからキューイングされたログをUnityメインスレッドで出力する
    /// </summary>
    private void ProcessLogQueue()
    {
        while (logQueue.TryDequeue(out LogEntry log))
        {
            switch (log.type)
            {
                case LogType.Log:
                    if (showDebugLogs)
                        Debug.Log(log.message);
                    break;
                case LogType.Warning:
                    if (showDebugLogs)
                        Debug.LogWarning(log.message);
                    break;
                case LogType.Error:
                    Debug.LogError(log.message);
                    break;
            }
        }
    }

    // ── Background receive threads ────────────────────────────────────────────
    private void ReceiveDiscovery()
    {
        while (isRunning)
        {
            try
            {
                IPEndPoint ep   = new IPEndPoint(IPAddress.Any, broadcastPort);
                byte[]     data = receiveClient.Receive(ref ep);
                string     msg  = Encoding.UTF8.GetString(data);
                EnqueueLog(LogType.Log, $"[ParentUdpSender] Broadcast received: '{msg}' from {ep.Address}");

                ParentUdpMessage message = ParentUdpMessageParser.Parse(msg);
                if (message.Type == ParentMessageType.DiscoveryRequest)
                {
                    string senderIP = ep.Address.ToString();
                    EnqueueLog(LogType.Log, $"[ParentUdpSender] DISCOVERY_REQUEST from {senderIP} — queuing targetIP update and DISCOVERY_ACCEPT.");
                    actionQueue.Enqueue(() =>
                    {
                        string oldIP = targetIP;
                        targetIP         = senderIP;
                        currentState     = ConnectionState.Connected;
                        lastReceiveTime  = Time.time;
                        gameStarted      = false;
                        if (showDebugLogs)
                            Debug.Log($"[ParentUdpSender] targetIP updated: '{oldIP}' → '{targetIP}' | state=Connected");
                        SendDiscoveryAccept(senderIP);
                    });
                }
            }
            catch (Exception e)
            {
                if (isRunning)
                {
                    EnqueueLog(LogType.Error, $"[ParentUdpSender] ReceiveDiscovery error: {e.Message}");
                }
            }
        }
    }

    private void ReceiveNormalData()
    {
        while (isRunning)
        {
            try
            {
                IPEndPoint ep   = new IPEndPoint(IPAddress.Any, parentReceivePort);
                byte[]     data = normalReceiveClient.Receive(ref ep);
                string     msg  = Encoding.UTF8.GetString(data);

                // PING 以外のメッセージのみログキューへ積む（毎秒のPINGによる文字列生成・ログ出力を抑制）
                if (ParentUdpMessageParser.Parse(msg).Type != ParentMessageType.Ping)
                {
                    EnqueueLog(LogType.Log, $"[ParentUdpSender] Normal received: '{msg}' from {ep.Address}");
                }

                receiveQueue.Enqueue(msg);
            }
            catch (Exception e)
            {
                if (isRunning)
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
            byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + "DISCOVERY_ACCEPT");
            udpClient.Send(data, data.Length, ip, normalPort);
            Debug.Log($"[ParentUdpSender] Sent DISCOVERY_ACCEPT to {ip}:{normalPort}");
        }
        catch (Exception e) { Debug.LogError($"[ParentUdpSender] SendDiscoveryAccept error: {e.Message}"); }
    }

    // ── UI ────────────────────────────────────────────────────────────────────
    private void UpdateUI()
    {
        if (connectButtonLabel != null)
        {
            switch (currentState)
            {
                case ConnectionState.Disconnected: connectButtonLabel.text = "Connect";     break;
                case ConnectionState.Connecting:   connectButtonLabel.text = "Connecting..."; break;
                case ConnectionState.Connected:    connectButtonLabel.text = "STARTING...";  break;
            }
        }

        if (connectButton   != null) connectButton.gameObject.SetActive(true);
        if (cancelButton    != null) cancelButton.gameObject.SetActive(currentState == ConnectionState.Connecting);
        if (startButtonObject != null) startButtonObject.SetActive(currentState == ConnectionState.Connected);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static void UpdateRanking(string keyPrefix, int newScore)
    {
        int[] ranking = new int[RankingSize];
        for (int i = 0; i < RankingSize; i++)
            ranking[i] = PlayerPrefs.GetInt(keyPrefix + i, 0);

        for (int i = 0; i < RankingSize; i++)
        {
            if (newScore > ranking[i])
            {
                for (int j = RankingSize - 1; j > i; j--)
                    ranking[j] = ranking[j - 1];
                ranking[i] = newScore;
                break;
            }
        }

        for (int i = 0; i < RankingSize; i++)
            PlayerPrefs.SetInt(keyPrefix + i, ranking[i]);
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