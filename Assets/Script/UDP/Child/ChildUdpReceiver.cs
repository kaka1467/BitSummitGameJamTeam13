using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// ChildUdpReceiver:
/// Handles all UDP communication on the child side.
///
/// Outbound messages (child → parent, port 8002):
///   TEAM13_START_GAME        — child requests game start
///   TEAM13_TIME_UP           — child timer expired
///   TEAM13_CHILD_DEAD        — child died
///   TEAM13_CHILD_SCORE:<val> — child final score for parent ranking
///   TEAM13_LOUD_ITEM         — child picked up loud item
///   TEAM13_PING              — heartbeat
///
/// Inbound messages (parent → child, port 8000):
///   TEAM13_START_GAME   — load game scene
///   TEAM13_CAUGHT       — parent game-over → child game-over
///   TEAM13_SLEEP_LOCK   — disable child player input
///   TEAM13_SLEEP_UNLOCK — re-enable child player input
///   TEAM13_PING         — heartbeat from parent
/// </summary>
public class ChildUdpReceiver : MonoBehaviour
{
    private const string MAGIC_NUMBER = "TEAM13_";
    private const string CMD_START    = "START_GAME";

    public enum ConnectionState { Disconnected, Connecting, Connected }

    // ── Inspector ─────────────────────────────────────────────────────────────
    public int    normalPort        = 8000;
    public int    broadcastPort     = 8001;
    public int    parentReceivePort = 8002;
    public string targetIP          = "127.0.0.1";
    public ConnectionState currentState = ConnectionState.Disconnected;
    public string lastMessage   = "";
    public string gameSceneName = "GameScene";

    public SleepingManager     sleepingManager;
    public Button              connectButton;
    public TextMeshProUGUI     connectButtonLabel;
    public Button              creditsButton;
    public Button              settingsButton;
    public Button              startButton;
    public TextMeshProUGUI     statusText;
    public Button              cancelButton;

    [Header("Connect Button Position Settings")]
    [Tooltip("Disconnected/Connecting状態のときのconnectButtonの位置")]
    public Vector2 connectButtonDefaultPosition = new Vector2(0f, 0f);
    [Tooltip("Connected（START!）状態のときのconnectButtonの位置")]
    public Vector2 connectButtonStartPosition = new Vector2(0f, 0f);

    [Header("New Cancel UI Object Support")]
    [Tooltip("Cancelの背景画像や枠など、消したい装飾がすべて含まれている一番外側の親オブジェクトをアサインしてください")]
    public GameObject cancelUiObject;

    [Header("Game References")]
    [Tooltip("Auto-found at Start if not assigned. Used for SLEEP_LOCK / SLEEP_UNLOCK.")]
    public PlayerMove playerMove;

    [SerializeField] private GameObject   creditsPanel;
    [SerializeField] private GameObject   settingsPanel;
    [SerializeField] private GameObject[] animatedSpriteObjects;
    [SerializeField] private GameObject   titleImageObject;
    [SerializeField] private string connectLabel   = "Connect";
    [SerializeField] private string connectingLabel = "接続中";

    // ── Private networking ────────────────────────────────────────────────────
    private UdpClient udpClient;
    private UdpClient sendClient;
    private Thread    receiveThread;
    private volatile bool isRunning = false;

    private readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<Action> actionQueue  = new ConcurrentQueue<Action>();

    private Coroutine discoveryCoroutine;
    private Coroutine heartbeatCoroutine;
    private float     lastReceiveTime;
    private float     pingInterval = 1.0f;
    private float     timeoutLimit = 3.0f;
    private bool      gameSceneLoaded = false;

    public static ChildUdpReceiver instance { get; private set; }

    // ── Button callbacks ──────────────────────────────────────────────────────
    public void OnConnectButtonClicked()
    {
        if (currentState == ConnectionState.Connected)
            OnStartButtonClicked();
        else
            currentState = ConnectionState.Connecting;
    }

    public void OnCancelButtonClicked()  { currentState = ConnectionState.Disconnected; }

    public void OnCreditsButtonClicked()
    {
        if (creditsPanel == null) return;
        creditsPanel.SetActive(!creditsPanel.activeSelf);
        UpdateAnimatedSpritesVisibility();
    }

    public void OnCloseCreditsClicked()
    {
        if (creditsPanel == null) return;
        creditsPanel.SetActive(false);
        UpdateAnimatedSpritesVisibility();
    }

    public void OnSettingsButtonClicked()
    {
        if (settingsPanel == null) return;
        settingsPanel.SetActive(!settingsPanel.activeSelf);
        UpdateAnimatedSpritesVisibility();
    }

    public void OnCloseSettingsClicked()
    {
        if (settingsPanel == null) return;
        settingsPanel.SetActive(false);
        UpdateAnimatedSpritesVisibility();
    }

    public void OnStartButtonClicked()
    {
        SendState(CMD_START);
        Debug.Log($"[ChildUdpReceiver] Sent START_GAME to parent at {targetIP}:{parentReceivePort}");
        LoadGameScene();
    }

    // ── Public send API ───────────────────────────────────────────────────────
    public void SendState(string message)
    {
        Debug.Log($"[ChildUdpReceiver] → '{message}' to {targetIP}:{parentReceivePort}");
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + message);
            sendClient.Send(data, data.Length, targetIP, parentReceivePort);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ChildUdpReceiver] SendState error: {e.Message}");
        }
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────
    void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.Log("[ChildUdpReceiver] Duplicate detected — destroying self.");
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        Debug.Log("[ChildUdpReceiver] Start");

        SceneManager.sceneLoaded += OnSceneLoaded;
        RefreshSceneReferences();

        isRunning = true;

        udpClient  = new UdpClient(normalPort);
        sendClient = new UdpClient();
        sendClient.EnableBroadcast = true;

        receiveThread = new Thread(ReceiveData) { IsBackground = true };
        receiveThread.Start();

        if (connectButton != null)
        {
            connectButton.onClick.RemoveAllListeners();
            connectButton.onClick.AddListener(OnConnectButtonClicked);
        }

        UpdateAnimatedSpritesVisibility();
        UpdateUi();
    }

    void Update()
    {
        while (messageQueue.TryDequeue(out string msg))
            HandleIncoming(msg);

        while (actionQueue.TryDequeue(out Action action))
            action();

        // Timeout
        if (currentState == ConnectionState.Connected &&
            Time.time - lastReceiveTime > timeoutLimit)
        {
            currentState = ConnectionState.Disconnected;
            Debug.LogWarning("[ChildUdpReceiver] Connection timed out — parent heartbeat lost.");
        }

        // Discovery coroutine lifecycle
        if (currentState == ConnectionState.Connecting && discoveryCoroutine == null)
            discoveryCoroutine = StartCoroutine(DiscoveryCoroutine());
        else if (currentState != ConnectionState.Connecting && discoveryCoroutine != null)
        {
            StopCoroutine(discoveryCoroutine);
            discoveryCoroutine = null;
        }

        // Heartbeat coroutine lifecycle
        if (currentState == ConnectionState.Connected && heartbeatCoroutine == null)
            heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
        else if (currentState != ConnectionState.Connected && heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }

        UpdateUi();
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (instance == this) instance = null;
        isRunning = false;

        if (discoveryCoroutine != null) { StopCoroutine(discoveryCoroutine); discoveryCoroutine = null; }
        if (heartbeatCoroutine  != null) { StopCoroutine(heartbeatCoroutine);  heartbeatCoroutine  = null; }

        // Close sockets — unblocks blocking Receive() so the thread exits naturally.
        CloseClient(ref udpClient,  "udpClient");
        CloseClient(ref sendClient, "sendClient");
    }

    // ── Scene reference refresh ────────────────────────────────────────────────
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Debug.Log($"[ChildUdpReceiver] Scene loaded: '{scene.name}' — refreshing scene references.");
        RefreshSceneReferences();
    }

    private void RefreshSceneReferences()
    {
        playerMove = UnityEngine.Object.FindFirstObjectByType<PlayerMove>();
        if (playerMove != null)
            Debug.Log($"[ChildUdpReceiver] playerMove found: '{playerMove.gameObject.name}'.");
        else
            Debug.Log("[ChildUdpReceiver] playerMove not found in current scene (OK on title/loading scenes).");

        sleepingManager = UnityEngine.Object.FindFirstObjectByType<SleepingManager>();
        if (sleepingManager != null)
            Debug.Log($"[ChildUdpReceiver] sleepingManager found: '{sleepingManager.gameObject.name}'.");
        else
            Debug.Log("[ChildUdpReceiver] sleepingManager not found in current scene (OK on game/loading scenes).");
    }

    // ── Incoming message dispatch (main thread) ───────────────────────────────
    private void HandleIncoming(string raw)
    {
        if (!raw.StartsWith(MAGIC_NUMBER)) return;
        string msg = raw.Substring(MAGIC_NUMBER.Length);
        lastMessage = msg;
        Debug.Log($"[ChildUdpReceiver] HandleIncoming: '{msg}' | scene='{SceneManager.GetActiveScene().name}' | playerMove={(playerMove != null ? playerMove.gameObject.name : "NULL")} | GameManager={(GameManager.instance != null ? "present" : "NULL")}");

        if (msg == "PING")
        {
            lastReceiveTime = Time.time;
            return;
        }

        if (msg == CMD_START)
        {
            Debug.Log("[ChildUdpReceiver] Received START_GAME from parent — loading game scene.");
            LoadGameScene();
            return;
        }

        if (msg == "CAUGHT")
        {
            Debug.Log($"[ChildUdpReceiver] Received CAUGHT — GameManager.instance={(GameManager.instance != null ? "present" : "NULL")}.");
            
            // Prefer GameManager flow so score saving and UDP are consistent.
            if (GameManager.instance != null)
            {
                GameManager.instance.TriggerResult(GameManager.ResultType.GameOver);
            }
            else
            {
                // Fallback: save a minimal score and notify parent, then load result.
                int finalScore = 0;
                PlayerPrefs.SetInt("LastGameOverScore", finalScore);
                PlayerPrefs.Save();
                SendState($"CHILD_SCORE:GAME_OVER:{finalScore}");
                SceneManager.LoadScene("GameOverResult");
            }
            
            return;
        }

        if (msg == "SLEEP_LOCK")
        {
            Debug.Log($"[ChildUdpReceiver] Received SLEEP_LOCK — playerMove={(playerMove != null ? playerMove.gameObject.name : "NULL")}.");
            PlayerInputLock.SetLocked(true);
            if (playerMove != null)
                playerMove.SetInputEnabled(false);
            else
                Debug.LogWarning("[ChildUdpReceiver] SLEEP_LOCK received but playerMove is null — global input lock is still enabled.");
            return;
        }

        if (msg == "SLEEP_UNLOCK")
        {
            Debug.Log($"[ChildUdpReceiver] Received SLEEP_UNLOCK — playerMove={(playerMove != null ? playerMove.gameObject.name : "NULL")}.");
            PlayerInputLock.SetLocked(false);
            if (playerMove != null)
                playerMove.SetInputEnabled(true);
            else
                Debug.LogWarning("[ChildUdpReceiver] SLEEP_UNLOCK received but playerMove is null — global input lock is still disabled.");
            return;
        }

        Debug.Log($"[ChildUdpReceiver] Unhandled message: '{msg}'");
    }

    // ── Coroutines ────────────────────────────────────────────────────────────
    private IEnumerator DiscoveryCoroutine()
    {
        while (currentState == ConnectionState.Connecting)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + "DISCOVERY_REQUEST");
                sendClient.Send(data, data.Length, "255.255.255.255", broadcastPort);
                Debug.Log($"[ChildUdpReceiver] Sent DISCOVERY_REQUEST (broadcast)");
            }
            catch (Exception e) { Debug.LogError($"[ChildUdpReceiver] Discovery send error: {e.Message}"); }
            yield return new WaitForSeconds(0.5f);
        }
    }

    private IEnumerator HeartbeatCoroutine()
    {
        while (currentState == ConnectionState.Connected)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + "PING");
                sendClient.Send(data, data.Length, targetIP, parentReceivePort);
            }
            catch (Exception e) { Debug.LogError($"[ChildUdpReceiver] Heartbeat error: {e.Message}"); }
            yield return new WaitForSeconds(pingInterval);
        }
    }

    // ── Background receive thread ─────────────────────────────────────────────
    private void ReceiveData()
    {
        while (isRunning)
        {
            try
            {
                IPEndPoint ep   = new IPEndPoint(IPAddress.Any, normalPort);
                byte[]     data = udpClient.Receive(ref ep);
                string     msg  = Encoding.UTF8.GetString(data);
                Debug.Log($"[ChildUdpReceiver] Received: '{msg}' from {ep.Address}");

                if (msg == MAGIC_NUMBER + "DISCOVERY_ACCEPT")
                {
                    string parentIP = ep.Address.ToString();
                    Debug.Log($"[ChildUdpReceiver] DISCOVERY_ACCEPT from {parentIP} — now Connected.");
                    actionQueue.Enqueue(() =>
                    {
                        targetIP        = parentIP;
                        currentState    = ConnectionState.Connected;
                        lastReceiveTime = Time.time;
                        gameSceneLoaded = false;
                    });
                }

                messageQueue.Enqueue(msg);
            }
            catch (Exception e)
            {
                if (isRunning) Debug.LogError($"[ChildUdpReceiver] ReceiveData error: {e.Message}");
            }
        }
    }

    // ── UI ────────────────────────────────────────────────────────────────────
    private void UpdateUi()
    {
        if (connectButtonLabel != null)
        {
            switch (currentState)
            {
                case ConnectionState.Disconnected: connectButtonLabel.text = connectLabel;    break;
                case ConnectionState.Connecting:   connectButtonLabel.text = connectingLabel; break;
                case ConnectionState.Connected:    connectButtonLabel.text = "START!";        break;
            }
        }

        if (connectButton != null)
        {
            connectButton.gameObject.SetActive(true);
            connectButton.interactable = currentState != ConnectionState.Connecting;

            Transform moveTarget = connectButton.transform.parent != null
                ? connectButton.transform.parent
                : connectButton.transform;
            RectTransform rt = moveTarget.GetComponent<RectTransform>();
            if (rt != null)
                rt.anchoredPosition = currentState == ConnectionState.Connected
                    ? connectButtonStartPosition
                    : connectButtonDefaultPosition;
        }

        if (cancelUiObject != null)
            cancelUiObject.SetActive(currentState == ConnectionState.Connecting);
        else if (cancelButton != null)
            cancelButton.gameObject.SetActive(currentState == ConnectionState.Connecting);

        SetActiveForButton(creditsButton,  currentState != ConnectionState.Connected);
        SetActiveForButton(settingsButton, currentState != ConnectionState.Connected);
    }

    private void UpdateAnimatedSpritesVisibility()
    {
        bool shouldShow = !(creditsPanel  != null && creditsPanel.activeSelf) &&
                          !(settingsPanel != null && settingsPanel.activeSelf);

        if (titleImageObject != null)
            titleImageObject.SetActive(shouldShow);

        if (animatedSpriteObjects == null) return;
        foreach (GameObject go in animatedSpriteObjects)
            if (go != null) go.SetActive(shouldShow);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void LoadGameScene()
    {
        if (gameSceneLoaded) return;
        gameSceneLoaded = true;
        SceneManager.LoadScene(gameSceneName);
    }

    private static void SetActiveForButton(Button button, bool active)
    {
        if (button == null) return;
        GameObject target = button.transform.parent != null
            ? button.transform.parent.gameObject
            : button.gameObject;
        target.SetActive(active);
    }

    private static void CloseClient(ref UdpClient client, string label)
    {
        if (client == null) return;
        try   { client.Close(); client.Dispose(); }
        catch (Exception e) { Debug.LogWarning($"[ChildUdpReceiver] Error closing {label}: {e.Message}"); }
        client = null;
    }
}