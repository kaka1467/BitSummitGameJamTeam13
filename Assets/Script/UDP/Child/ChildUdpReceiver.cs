using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    private const string CMD_START = "START_GAME";

    public enum ConnectionState { Disconnected, Connecting, Connected }

    // ── Inspector ─────────────────────────────────────────────────────────────
    public int normalPort = 8000;
    public int broadcastPort = 8001;
    public int parentReceivePort = 8002;
    public string targetIP = "127.0.0.1";
    public ConnectionState currentState = ConnectionState.Disconnected;
    public string lastMessage = "";
    public string gameSceneName = "GameScene";
    public string titleSceneName = "Mini Title";

    public SleepingManager sleepingManager;
    public Button connectButton;
    public TextMeshProUGUI connectButtonLabel;
    public Button creditsButton;
    public Button settingsButton;
    public Button startButton;
    public TextMeshProUGUI statusText;
    public Button cancelButton;

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

    [Header("Debug")]
    [Tooltip("通信ログなどの詳細出力を有効にする")]
    [SerializeField] private bool showDebugLogs = true;

    // 子機が現在適用している睡眠ロック状態（重複パケット処理の抑制用）
    private bool isSleepInputLocked = false;
    [SerializeField] private GameObject creditsPanel;
    [SerializeField] private GameObject settingsPanel;
    [SerializeField] private GameObject[] animatedSpriteObjects;
    [SerializeField] private GameObject titleImageObject;
    [SerializeField] private string connectLabel = "Connect";
    [SerializeField] private string connectingLabel = "接続中";

    // ── Private networking ────────────────────────────────────────────────────
    private UdpClient udpClient;
    private UdpClient sendClient;
    private Thread receiveThread;
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

    private readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<Action> actionQueue = new ConcurrentQueue<Action>();
    private readonly ConcurrentQueue<LogEntry> logQueue = new ConcurrentQueue<LogEntry>();

    private Coroutine discoveryCoroutine;
    private Coroutine heartbeatCoroutine;
    private float lastReceiveTime;
    private float pingInterval = 1.0f;
    private float timeoutLimit = 3.0f;
    private bool gameSceneLoaded = false;

    // 最後にUIに反映した接続状態（状態変更時のみUI更新を行うためのキャッシュ）
    private ConnectionState? lastAppliedUiState = null;

    public static ChildUdpReceiver instance { get; private set; }

    // ── Button callbacks ──────────────────────────────────────────────────────
    public void OnConnectButtonClicked()
    {
        if (currentState == ConnectionState.Connected)
            OnStartButtonClicked();
        else
            currentState = ConnectionState.Connecting;
    }

    public void OnCancelButtonClicked() { currentState = ConnectionState.Disconnected; }

    public void OnCreditsButtonClicked()
    {
        creditsPanel = FindGameObject(null, "Credits");
        if (creditsPanel == null)
        {
            Debug.LogWarning("[ChildUdpReceiver] Credits panel was not found.");
            return;
        }

        creditsPanel.SetActive(!creditsPanel.activeSelf);
        Debug.Log($"[ChildUdpReceiver] Credits panel active: {creditsPanel.activeSelf}");
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
        settingsPanel = FindGameObject(null, "SettingsPanel");
        if (settingsPanel == null)
        {
            Debug.LogWarning("[ChildUdpReceiver] Settings panel was not found.");
            return;
        }

        settingsPanel.SetActive(!settingsPanel.activeSelf);
        Debug.Log($"[ChildUdpReceiver] Settings panel active: {settingsPanel.activeSelf}");
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
        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] → '{message}' to {targetIP}:{parentReceivePort}");
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + message);
            sendClient.Send(data, data.Length, targetIP, parentReceivePort);

            if (message == "LOADING_COMPLETE")
            {
                // Fallback: broadcast so parent can receive even if targetIP is stale.
                sendClient.Send(data, data.Length, "255.255.255.255", parentReceivePort);
                if (showDebugLogs)
                    Debug.Log($"[ChildUdpReceiver] → '{message}' broadcast to 255.255.255.255:{parentReceivePort}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[ChildUdpReceiver] SendState error: {e.Message}");
        }
    }

    /// <summary>
    /// ラウドアイテムを取得したことを親機に送信する
    /// </summary>
    public void SendLoudItem()
    {
        // MAGIC_NUMBER ("TEAM13_") + "LOUD_ITEM" で送信
        SendState("LOUD_ITEM");
        Debug.Log("[ChildUdpReceiver] Sent LOUD_ITEM packet to Parent.");
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

        if (IsTitleScene(SceneManager.GetActiveScene().name))
        {
            RefreshUiReferences();
            AttachUiListeners();
            ResetForNewSession();
            InitializeTitleUi();
        }

        isRunning = true;

        udpClient = new UdpClient(normalPort);
        sendClient = new UdpClient();
        sendClient.EnableBroadcast = true;

        receiveThread = new Thread(ReceiveData) { IsBackground = true };
        receiveThread.Start();
    }

    void Update()
    {
        while (messageQueue.TryDequeue(out string msg))
            HandleIncoming(msg);

        while (actionQueue.TryDequeue(out Action action))
            action();

        ProcessLogQueue();

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
        if (heartbeatCoroutine != null) { StopCoroutine(heartbeatCoroutine); heartbeatCoroutine = null; }

        // Close sockets — unblocks blocking Receive() so the thread exits naturally.
        CloseClient(ref udpClient, "udpClient");
        CloseClient(ref sendClient, "sendClient");
    }

    // ── Scene reference refresh ────────────────────────────────────────────────
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Debug.Log($"[ChildUdpReceiver] Scene loaded: '{scene.name}' — refreshing scene references.");
        RefreshSceneReferences();

        if (IsTitleScene(scene.name))
        {
            ResetForNewSession();
            ClearTitleUiReferences();
            StartCoroutine(RebindTitleUiNextFrame());
            Debug.Log($"[ChildUdpReceiver] Title scene loaded ('{scene.name}') — reset state for next round.");
        }
        else if (scene.name == gameSceneName)
        {
            isSleepInputLocked = false;
            PlayerInputLock.SetLocked(false);
            if (playerMove != null)
                playerMove.SetInputEnabled(true);
        }
    }

    private IEnumerator RebindTitleUiNextFrame()
    {
        yield return null;

        if (!IsTitleScene(SceneManager.GetActiveScene().name))
            yield break;

        RefreshUiReferences();
        AttachUiListeners();
        UpdateAnimatedSpritesVisibility();
        InitializeTitleUi();
    }

    private void ClearTitleUiReferences()
    {
        connectButton = null;
        connectButtonLabel = null;
        creditsButton = null;
        settingsButton = null;
        cancelButton = null;
        creditsPanel = null;
        settingsPanel = null;
        titleImageObject = null;
        cancelUiObject = null;
        lastAppliedUiState = null;
    }

    private void InitializeTitleUi()
    {
        creditsPanel = FindGameObject(creditsPanel, "Credits");
        settingsPanel = FindGameObject(settingsPanel, "SettingsPanel");
        cancelUiObject = FindGameObject(cancelUiObject, "Cancel");

        if (creditsPanel != null)
            creditsPanel.SetActive(false);

        if (settingsPanel != null)
            settingsPanel.SetActive(false);

        if (cancelUiObject != null)
            cancelUiObject.SetActive(false);

        UpdateAnimatedSpritesVisibility();
        UpdateUi(true);
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
        // シーン遷移後にリスナーを再登録する。
        // RemoveAllListeners() はInspectorで設定した他の処理まで消してしまうため使用しない。

        AttachButtonListener(connectButton, OnConnectButtonClicked, nameof(OnConnectButtonClicked));
        AttachButtonListener(cancelButton, OnCancelButtonClicked, nameof(OnCancelButtonClicked));
        AttachButtonListener(creditsButton, OnCreditsButtonClicked, nameof(OnCreditsButtonClicked));
        AttachButtonListener(settingsButton, OnSettingsButtonClicked, nameof(OnSettingsButtonClicked));

        AttachCloseButtonListener(creditsPanel, OnCloseCreditsClicked);
        AttachCloseButtonListener(settingsPanel, OnCloseSettingsClicked);
    }

    private void AttachButtonListener(Button button, UnityEngine.Events.UnityAction action, string methodName)
    {
        if (button == null)
            return;

        // On the first title load, the Inspector already calls this receiver.
        // Adding the same callback again toggles panels twice (off -> on -> off).
        // After returning to the title, the Inspector target is the destroyed
        // scene-local receiver, so a runtime callback is required instead.
        button.onClick.RemoveListener(action);
        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
        {
            if (button.onClick.GetPersistentTarget(i) == this &&
                button.onClick.GetPersistentMethodName(i) == methodName)
                return;
        }

        button.onClick.AddListener(action);
    }

    private void AttachCloseButtonListener(GameObject panel, UnityEngine.Events.UnityAction action)
    {
        if (panel == null) return;

        bool isSettingsPanel = panel == settingsPanel;
        Button closeBtn = panel.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(button =>
                button.name.Equals("CloseButton", StringComparison.OrdinalIgnoreCase) ||
                (isSettingsPanel && button.name.Equals("CancelButton", StringComparison.OrdinalIgnoreCase)));

        if (closeBtn != null)
        {
            AttachButtonListener(closeBtn, action, action.Method.Name);
        }
    }

    public void ResetForNewSession()
    {
        currentState = ConnectionState.Disconnected;
        lastReceiveTime = 0f;
        lastMessage = "";
        targetIP = "127.0.0.1";
        gameSceneLoaded = false;
        isSleepInputLocked = false;
        PlayerInputLock.SetLocked(false);

        if (discoveryCoroutine != null)
        {
            StopCoroutine(discoveryCoroutine);
            discoveryCoroutine = null;
        }

        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }

        if (playerMove != null)
            playerMove.SetInputEnabled(true);

        if (sleepingManager != null)
            Debug.Log("[ChildUdpReceiver] ResetForNewSession: sleepingManager remains as reference for next round.");
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

    private void RefreshUiReferences()
    {
        if (!IsTitleScene(SceneManager.GetActiveScene().name))
            return;

        connectButton = FindButton(connectButton, "ConnectButton", "Connect Button");
        if (connectButtonLabel == null && connectButton != null)
            connectButtonLabel = connectButton.GetComponentInChildren<TextMeshProUGUI>(true);

        if (connectButtonLabel == null)
        {
            GameObject tmpGo = FindGameObject(null, "Connect TMP");
            if (tmpGo != null)
                connectButtonLabel = tmpGo.GetComponent<TextMeshProUGUI>();
        }

        // The title scene currently uses names with a trailing space. The
        // receiver survives scene loads, so these references must be reacquired
        // whenever the title scene is loaded.
        creditsButton = FindButton(creditsButton, "CrediButton ", "Credit", "CrediButton", "CreditsButton", "CreditButton");
        settingsButton = FindButton(settingsButton, "SettingButton ", "Setting", "SettingsButton", "SettingButton");
        cancelButton = FindButton(cancelButton, "CancelButton", "cancel button");
        cancelUiObject = FindGameObject(cancelUiObject, "Cancel");

        creditsPanel = FindGameObject(creditsPanel, "Credits");
        settingsPanel = FindGameObject(settingsPanel, "SettingsPanel");
        titleImageObject = FindGameObject(titleImageObject, "Title", "TitleImage", "TitleImageObject");

        if (IsTitleScene(SceneManager.GetActiveScene().name))
        {
            animatedSpriteObjects = new[]
            {
                FindGameObject(null, "BackStar"),
                FindGameObject(null, "Character")
            }.Where(go => go != null).ToArray();
        }

        DisableRaycastTargets(titleImageObject);
        if (animatedSpriteObjects != null)
        {
            foreach (GameObject go in animatedSpriteObjects)
                DisableRaycastTargets(go);
        }
    }

    // ── Incoming message dispatch (main thread) ───────────────────────────────
    private void HandleIncoming(string raw)
    {
        if (!raw.StartsWith(MAGIC_NUMBER)) return;
        string msg = raw.Substring(MAGIC_NUMBER.Length);
        lastMessage = msg;
        if (msg != "PING" && showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] HandleIncoming: '{msg}' | scene='{SceneManager.GetActiveScene().name}' | playerMove={(playerMove != null ? playerMove.gameObject.name : "NULL")} | GameManager={(GameManager.instance != null ? "present" : "NULL")}");

        if (msg == "PING")
        {
            lastReceiveTime = Time.time;
            return;
        }

        if (msg == CMD_START)
        {
            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] Received START_GAME from parent — loading game scene.");
            LoadGameScene();
            return;
        }

        if (msg == "CAUGHT")
        {
            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] Received CAUGHT — GameManager.instance={(GameManager.instance != null ? "present" : "NULL")}.");

            // GameManagerフローを優先し、スコア保存とUDP送信の整合性を保つ
            if (GameManager.instance != null)
            {
                GameManager.instance.TriggerResult(GameManager.ResultType.GameOver);
            }
            else
            {
                // フォールバック：既に結果画面やタイトル画面にいる場合の二重ロードを防止
                string activeScene = SceneManager.GetActiveScene().name;
                if (activeScene != "GameOverResult" && activeScene != "TimeUpResult" && !IsTitleScene(activeScene))
                {
                    int finalScore = 0;
                    PlayerPrefs.SetInt("LastGameOverScore", finalScore);
                    PlayerPrefs.Save();
                    SendState($"CHILD_SCORE:GAME_OVER:{finalScore}");
                    SceneManager.LoadScene("GameOverResult");
                }
            }

            return;
        }

        if (msg == "SLEEP_LOCK")
        {
            if (isSleepInputLocked)
            {
                if (showDebugLogs)
                    Debug.Log("[ChildUdpReceiver] Received redundant SLEEP_LOCK (already locked) — skipped.");
                return;
            }

            isSleepInputLocked = true;
            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] Received SLEEP_LOCK — locking input.");

            PlayerInputLock.SetLocked(true);

            if (playerMove != null)
            {
                playerMove.SetInputEnabled(false);
            }
            else
            {
                if (showDebugLogs)
                    Debug.LogWarning("[ChildUdpReceiver] SLEEP_LOCK received but playerMove is null.");
            }

            return;
        }

        if (msg == "SLEEP_UNLOCK")
        {
            if (!isSleepInputLocked)
            {
                if (showDebugLogs)
                    Debug.Log("[ChildUdpReceiver] Received redundant SLEEP_UNLOCK (already unlocked) — skipped.");
                return;
            }

            isSleepInputLocked = false;
            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] Received SLEEP_UNLOCK — unlocking input.");

            PlayerInputLock.SetLocked(false);

            if (playerMove != null)
            {
                playerMove.SetInputEnabled(true);
            }
            else
            {
                if (showDebugLogs)
                    Debug.LogWarning("[ChildUdpReceiver] SLEEP_UNLOCK received but playerMove is null.");
            }

            return;
        }

        if (showDebugLogs)
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

    // ── Background receive thread ─────────────────────────────────────────────
    private void ReceiveData()
    {
        while (isRunning)
        {
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, normalPort);
                byte[] data = udpClient.Receive(ref ep);
                string msg = Encoding.UTF8.GetString(data);

                // PING 以外のメッセージのみログキューへ積む（毎秒のPINGによる文字列生成・ログ出力を抑制）
                if (msg != MAGIC_NUMBER + "PING")
                {
                    EnqueueLog(LogType.Log, $"[ChildUdpReceiver] Received: '{msg}' from {ep.Address}");
                }

                if (msg == MAGIC_NUMBER + "DISCOVERY_ACCEPT")
                {
                    string parentIP = ep.Address.ToString();
                    EnqueueLog(LogType.Log, $"[ChildUdpReceiver] DISCOVERY_ACCEPT from {parentIP} — now Connected.");
                    actionQueue.Enqueue(() =>
                    {
                        targetIP = parentIP;
                        currentState = ConnectionState.Connected;
                        lastReceiveTime = Time.time;
                        gameSceneLoaded = false;
                    });
                }

                messageQueue.Enqueue(msg);
            }
            catch (Exception e)
            {
                if (isRunning)
                {
                    EnqueueLog(LogType.Error, $"[ChildUdpReceiver] ReceiveData error: {e.Message}");
                }
            }
        }
    }

    // ── UI ────────────────────────────────────────────────────────────────────
    /// <summary>
    /// タイトル画面の接続UI表示を更新する。
    /// タイトル画面以外では実行されず、状態変化があった時（またはforceUpdate時）のみ更新を行う。
    /// </summary>
    private void UpdateUi(bool forceUpdate = false)
    {
        // タイトル画面以外ではUI更新を行わない
        if (!IsTitleScene(SceneManager.GetActiveScene().name))
            return;

        // 状態変化がなく、強制更新でもない場合はスキップ
        if (!forceUpdate && lastAppliedUiState.HasValue && lastAppliedUiState.Value == currentState)
            return;

        lastAppliedUiState = currentState;

        // Connectボタンのラベル更新
        if (connectButtonLabel != null)
        {
            switch (currentState)
            {
                case ConnectionState.Disconnected:
                    connectButtonLabel.text = connectLabel;
                    break;
                case ConnectionState.Connecting:
                    connectButtonLabel.text = connectingLabel;
                    break;
                case ConnectionState.Connected:
                    connectButtonLabel.text = "START!";
                    break;
            }
        }

        // Connectボタンの表示・位置更新
        if (connectButton != null)
        {
            connectButton.gameObject.SetActive(true);
            connectButton.interactable = currentState != ConnectionState.Connecting;

            Transform moveTarget = connectButton.transform.parent != null
                ? connectButton.transform.parent
                : connectButton.transform;
            RectTransform rt = moveTarget.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchoredPosition = currentState == ConnectionState.Connected
                    ? connectButtonStartPosition
                    : connectButtonDefaultPosition;
            }
        }

        // Cancelボタン / Cancel UIオブジェクトの更新
        if (cancelUiObject != null)
        {
            cancelUiObject.SetActive(currentState == ConnectionState.Connecting);
        }
        else if (cancelButton != null)
        {
            cancelButton.gameObject.SetActive(currentState == ConnectionState.Connecting);
        }

        // クレジット・設定ボタンの更新
        SetActiveForButton(creditsButton, currentState != ConnectionState.Connected);
        SetActiveForButton(settingsButton, currentState != ConnectionState.Connected);
    }

    private void UpdateAnimatedSpritesVisibility()
    {
        bool shouldShow = !(creditsPanel != null && creditsPanel.activeSelf) &&
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

    private static GameObject FindGameObject(GameObject current, params string[] candidateNames)
    {
        if (current != null)
            return current;

        foreach (string candidateName in candidateNames)
        {
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!candidate.scene.IsValid() || !candidate.scene.isLoaded)
                    continue;

                if (candidate.name == candidateName)
                    return candidate;
            }
        }

        return null;
    }

    private static void DisableRaycastTargets(GameObject target)
    {
        if (target == null)
            return;

        foreach (Graphic graphic in target.GetComponentsInChildren<Graphic>(true))
            graphic.raycastTarget = false;
    }

    private static void CloseClient(ref UdpClient client, string label)
    {
        if (client == null) return;
        try { client.Close(); client.Dispose(); }
        catch (Exception e) { Debug.LogWarning($"[ChildUdpReceiver] Error closing {label}: {e.Message}"); }
        client = null;
    }
}
