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
    public string             gameOverSceneName = "GameOverResult";
    public string             timeUpSceneName   = "TimeUpResult";
    public Button             cancelButton;

    // PlayerPrefs keys — mirrored from GameManager constants
    private const string KeyGameOverScore  = "LastGameOverScore";
    private const string KeyTimeUpScore    = "LastTimeUpScore";
    private const string KeyGameOverRank   = "GameOverRank_";
    private const string KeyTimeUpRank     = "TimeUpRank_";
    private const int    RankingSize       = 5;

    [Header("Game References")]
    [Tooltip("Auto-found at Start if not assigned. Used to trigger rush-in on LOUD_ITEM.")]
    public ParentDetectionV2 parentDetection;

    // ── Private networking ────────────────────────────────────────────────────
    private UdpClient udpClient;
    private UdpClient receiveClient;
    private UdpClient normalReceiveClient;
    private Thread    receiveThread;
    private Thread    normalReceiveThread;
    private volatile bool isRunning = false;

    private readonly ConcurrentQueue<Action> actionQueue  = new ConcurrentQueue<Action>();
    private readonly ConcurrentQueue<string> receiveQueue = new ConcurrentQueue<string>();

    private Coroutine heartbeatCoroutine;
    private float     lastReceiveTime;
    private float     pingInterval  = 1.0f;
    private float     timeoutLimit  = 3.0f;
    private bool      gameStarted        = false;
    private bool      resultProcessed    = false; // GAME_OVER wins race
    public  bool      ChildLoadingComplete { get; private set; } = false;

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

        // Timeout check
        if (currentState == ConnectionState.Connected &&
            Time.time - lastReceiveTime > timeoutLimit)
        {
            currentState = ConnectionState.Disconnected;
            Debug.LogWarning("[ParentUdpSender] Connection timed out — child heartbeat lost.");
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
            SendState("CAUGHT");

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

        // Close sockets — this unblocks the blocking Receive() calls so threads exit naturally.
        CloseClient(ref udpClient,           "udpClient");
        CloseClient(ref receiveClient,       "receiveClient");
        CloseClient(ref normalReceiveClient, "normalReceiveClient");
    }

    // ── Scene reference refresh ──────────────────────────────────────────────
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Debug.Log($"[ParentUdpSender] Scene loaded: '{scene.name}' — refreshing scene references.");
        RefreshSceneReferences();
        // Reset result guard for new game session
        if (scene.name == gameSceneName)
        {
            resultProcessed = false;
            Debug.Log("[ParentUdpSender] resultProcessed reset for new game session.");
        }
    }

    private void RefreshSceneReferences()
    {
        parentDetection = UnityEngine.Object.FindFirstObjectByType<ParentDetectionV2>();
        if (parentDetection != null)
            Debug.Log($"[ParentUdpSender] parentDetection found: '{parentDetection.gameObject.name}'.");
        else
            Debug.Log("[ParentUdpSender] parentDetection not found in current scene (OK on title/connect scenes).");
    }

    // ── Public send API ───────────────────────────────────────────────────────
    public void SendState(string message)
    {
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

    // ── Incoming message dispatch (main thread) ───────────────────────────────
    private void HandleIncoming(string raw)
    {
        if (!raw.StartsWith(MAGIC_NUMBER)) return;
        string msg = raw.Substring(MAGIC_NUMBER.Length);

        if (msg != "PING")
            Debug.Log($"[ParentUdpSender] HandleIncoming: '{msg}' | scene='{SceneManager.GetActiveScene().name}' | parentDetection={(parentDetection != null ? parentDetection.gameObject.name : "NULL")} | resultProcessed={resultProcessed}");

        if (msg == "PING")
        {
            lastReceiveTime = Time.time;
            return;
        }

        if (msg == CMD_START)
        {
            if (!gameStarted && currentState == ConnectionState.Connected)
            {
                gameStarted = true;
                Debug.Log("[ParentUdpSender] Received START_GAME from child — loading game scene.");
                SceneManager.LoadScene(gameSceneName);
            }
            return;
        }

        if (msg == "TIME_UP" || msg == "CHILD_DEAD")
        {
            // Legacy bare TIME_UP / CHILD_DEAD — treat as TIME_UP result
            if (!resultProcessed)
            {
                resultProcessed = true;
                Debug.Log($"[ParentUdpSender] Received {msg} — TIME_UP result. Loading {timeUpSceneName}.");
                SceneManager.LoadScene(timeUpSceneName);
            }
            return;
        }

        if (msg.StartsWith("CHILD_SCORE:"))
        {
            // Format: CHILD_SCORE:GAME_OVER:<val>  or  CHILD_SCORE:TIME_UP:<val>
            string payload = msg.Substring("CHILD_SCORE:".Length);
            bool isGameOver = payload.StartsWith("GAME_OVER:");
            bool isTimeUp   = payload.StartsWith("TIME_UP:");

            if (!isGameOver && !isTimeUp)
            {
                Debug.LogWarning($"[ParentUdpSender] Unrecognised CHILD_SCORE format: '{payload}'");
                return;
            }

            string numStr = payload.Substring(isGameOver ? "GAME_OVER:".Length : "TIME_UP:".Length);
            if (!int.TryParse(numStr, out int childScore))
            {
                Debug.LogWarning($"[ParentUdpSender] Could not parse score in CHILD_SCORE: '{numStr}'");
                return;
            }

            if (isGameOver)
            {
                // GAME_OVER wins the race unconditionally
                resultProcessed = true;
                Debug.Log($"[ParentUdpSender] CHILD_SCORE GAME_OVER {childScore} — saving and loading {ResultGameOverScene}.");
                PlayerPrefs.SetInt(KeyGameOverScore, childScore);
                UpdateRanking(KeyGameOverRank, childScore);
                PlayerPrefs.Save();
                SceneManager.LoadScene(ResultGameOverScene);
            }
            else // TIME_UP
            {
                if (resultProcessed)
                {
                    Debug.Log("[ParentUdpSender] TIME_UP result ignored — GAME_OVER already processed.");
                    return;
                }
                resultProcessed = true;
                Debug.Log($"[ParentUdpSender] CHILD_SCORE TIME_UP {childScore} — saving and loading {ResultTimeUpScene}.");
                PlayerPrefs.SetInt(KeyTimeUpScore, childScore);
                UpdateRanking(KeyTimeUpRank, childScore);
                PlayerPrefs.Save();
                SceneManager.LoadScene(ResultTimeUpScene);
            }
            return;
        }

        if (msg == "LOADING_COMPLETE")
        {
            Debug.Log("[ParentUdpSender] Received LOADING_COMPLETE from child.");
            ChildLoadingComplete = true;
            return;
        }

        if (msg == "LOUD_ITEM")
        {
            Debug.Log($"[ParentUdpSender] Received LOUD_ITEM — parentDetection={(parentDetection != null ? parentDetection.gameObject.name : "NULL")}.");
            if (parentDetection != null)
                parentDetection.OnLoudItemTriggered();
            else
                Debug.LogWarning("[ParentUdpSender] LOUD_ITEM received but parentDetection is null — rush-in NOT triggered.");
            return;
        }

        Debug.Log($"[ParentUdpSender] Unhandled message: '{msg}'");
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
                Debug.Log($"[ParentUdpSender] Broadcast received: '{msg}' from {ep.Address}");

                if (msg == MAGIC_NUMBER + "DISCOVERY_REQUEST")
                {
                    string senderIP = ep.Address.ToString();
                    Debug.Log($"[ParentUdpSender] DISCOVERY_REQUEST from {senderIP} — queuing targetIP update and DISCOVERY_ACCEPT.");
                    actionQueue.Enqueue(() =>
                    {
                        string oldIP = targetIP;
                        targetIP         = senderIP;
                        currentState     = ConnectionState.Connected;
                        lastReceiveTime  = Time.time;
                        gameStarted      = false;
                        Debug.Log($"[ParentUdpSender] targetIP updated: '{oldIP}' → '{targetIP}' | state=Connected");
                        SendDiscoveryAccept(senderIP);
                    });
                }
            }
            catch (Exception e)
            {
                if (isRunning) Debug.LogError($"[ParentUdpSender] ReceiveDiscovery error: {e.Message}");
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
                Debug.Log($"[ParentUdpSender] Normal received: '{msg}' from {ep.Address}");
                receiveQueue.Enqueue(msg);
            }
            catch (Exception e)
            {
                if (isRunning) Debug.LogError($"[ParentUdpSender] ReceiveNormalData error: {e.Message}");
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
}