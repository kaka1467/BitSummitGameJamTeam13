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
///   TEAM13_RETURN_TO_TITLE   — child returned to title → parent also returns
///
/// Inbound messages (parent → child, port 8000):
///   TEAM13_START_GAME   — load game scene
///   TEAM13_CAUGHT       — parent game-over → child game-over
///   TEAM13_SLEEP_LOCK   — disable child player input
///   TEAM13_SLEEP_UNLOCK — re-enable child player input
///   TEAM13_PING         — heartbeat from parent
///   TEAM13_RETURN_TO_TITLE — parent returned to title → child also returns
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
    public string gameSceneName = "ChildLoading";
    public string titleSceneName = "ChildeTitle";

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

    [Header("Sleep Lock Image")]
    [Tooltip("親機が睡眠中のとき、子機側に表示する画像UI。GameObjectをアサインしてください。")]
    [SerializeField] private GameObject sleepLockImageObject;
    [Tooltip("未設定の場合に自動検索するGameObject名。")]
    [SerializeField] private string sleepLockImageObjectName = "SleepLockImage";

    [Header("Debug")]
    [Tooltip("通信ログなどの詳細出力を有効にする")]
    [SerializeField] private bool showDebugLogs = true;

    // 子機が現在適用している睡眠ロック状態（重複パケット処理の抑制用）
    private bool isSleepInputLocked = false;
    private bool caughtHandled = false;

    // ── RETURN_TO_TITLE のプレイ識別（古い再送・遅延パケットの無視用） ─────────────────────
    // 連番だけでは「前回プレイの、まだ届いていない番号の通知」を区別できない。
    // また片端末の再起動で連番が1に戻ると大小比較が破綻する。
    // そのため「プレイ識別子（両端末で共有）」＋「そのプレイ内の連番」の2段で判定する。
    // 子機はゲーム開始時に新しい識別子を発行し、START_GAME:<sid> で親機へ伝える。
    private string _playSessionId = "";

    // 子機（このプロセス）が RETURN_TO_TITLE を送るたびに単調増加する連番。アプリ実行中は決してリセットしない。
    // 同じプレイ内での重複・再送を弾くために使う（プレイ識別子が一致する場合のみ意味を持つ）。
    private int _returnToTitleSendSeq = 0;
    private int _lastPeerReturnToTitleSeq = 0;

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
        // このプレイの識別子を新しく発行し、開始通知に載せて親機と共有する。
        // 以降の RETURN_TO_TITLE はこの識別子で「どのプレイの通知か」を判定する。
        _playSessionId = CreatePlaySessionId();
        _lastPeerReturnToTitleSeq = 0;

        SendState($"{CMD_START}:{_playSessionId}");
        Debug.Log($"[ChildUdpReceiver] Sent START_GAME (playSessionId={_playSessionId}) to parent at {targetIP}:{parentReceivePort}");
        LoadGameScene();
    }

    /// <summary>
    /// 新しいプレイ識別子を発行する（再起動・再接続をまたいでも衝突しない一意トークン）。
    /// </summary>
    private static string CreatePlaySessionId()
    {
        return $"{System.Guid.NewGuid():N}";
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
    /// リザルト通知など、取りこぼすと困る重要メッセージ用。UDP のパケットロス対策として、
    /// 即時に1回送ったあと extraSends 回を interval 秒間隔で再送する。
    /// このオブジェクトは DontDestroyOnLoad なので、シーン遷移後も再送が続く。
    /// 受信側（親機）は重複メッセージを無視する前提。
    /// </summary>
    public void SendStateRepeated(string message, int extraSends = 3, float interval = 0.15f)
    {
        SendState(message);

        if (extraSends > 0)
        {
            StartCoroutine(ResendRoutine(message, extraSends, interval));
        }
    }

    private IEnumerator ResendRoutine(string message, int count, float interval)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new WaitForSecondsRealtime(interval);
            SendState(message);
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

    /// <summary>
    /// ゲーム進行率（0〜1）を親機に送信する。片付け演出の開始条件に使う。
    /// 送信コストを抑えるため、呼び出し側で間隔を制御する（毎フレームは送らない）。
    /// </summary>
    public void SendGameProgress(float progressRate)
    {
        int progressMilli = Mathf.RoundToInt(Mathf.Clamp01(progressRate) * 1000f);
        SendState($"GAME_PROGRESS:{progressMilli}");
        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] Sent GAME_PROGRESS:{progressMilli} packet to Parent.");
    }

    /// <summary>
    /// 子機側で「タイトルへ戻る」処理を開始したことを親機へ通知する。
    /// 完全なメッセージは "TEAM13_RETURN_TO_TITLE:&lt;プレイ識別子&gt;:&lt;連番&gt;"（SendState が MAGIC_NUMBER を前置する）。
    /// 取りこぼすと親機がリザルト画面に取り残されるため、再送付きで送る。識別子・連番は再送でも同じ値を送る。
    /// </summary>
    public void notifyReturnToTitle()
    {
        _returnToTitleSendSeq++;
        SendStateRepeated($"RETURN_TO_TITLE:{_playSessionId}:{_returnToTitleSendSeq}");
        Debug.Log($"[ChildUdpReceiver] Sent RETURN_TO_TITLE (playSessionId={_playSessionId}, seq={_returnToTitleSendSeq}) with retries to parent.");
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
            // シーン遷移時にSLEEP_LOCK状態を勝手に解除しない。
            // 親機がすでに寝ている場合は、子機側の入力停止と画像表示を維持する。
            PlayerInputLock.SetLocked(isSleepInputLocked);
            if (playerMove != null)
                playerMove.SetInputEnabled(!isSleepInputLocked);

            StartCoroutine(RefreshSleepLockImageNextFrame());
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
               sceneName.Contains("Title");
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
        caughtHandled = false;
        isSleepInputLocked = false;

        // プレイ識別子をクリアする。タイトルに戻った時点で前のプレイは終了しているため、
        // 以降に届く前回プレイの RETURN_TO_TITLE は識別子不一致で無視される
        // （次プレイ開始時に OnStartButtonClicked で新しい識別子を必ず発行する）。
        _playSessionId = "";
        _lastPeerReturnToTitleSeq = 0;
        PlayerInputLock.SetLocked(false);
        SetSleepLockImageVisible(false);

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

        // シーンごとにUIオブジェクトが作り直されるため、毎回取り直す。
        sleepLockImageObject = FindGameObject(null, sleepLockImageObjectName,
            "SleepLockImage", "SleepLockUI", "SleepImage", "SleepLockPanel");

        if (sleepLockImageObject != null && showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] Sleep lock image found: '{sleepLockImageObject.name}' | active={sleepLockImageObject.activeSelf}");

        SetSleepLockImageVisible(isSleepInputLocked);
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

        if (msg == CMD_START || msg.StartsWith(CMD_START + ":", StringComparison.Ordinal))
        {
            // 親機が起点で開始した場合、親機が発行したプレイ識別子を採用する
            // （子機起点の場合は OnStartButtonClicked で既に発行済み）。
            if (msg.StartsWith(CMD_START + ":", StringComparison.Ordinal))
            {
                string sid = msg.Substring(CMD_START.Length + 1);
                if (!string.IsNullOrEmpty(sid))
                {
                    _playSessionId = sid;
                    _lastPeerReturnToTitleSeq = 0;
                }
            }

            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] Received START_GAME from parent — loading game scene. playSessionId={_playSessionId}");
            LoadGameScene();
            return;
        }

        if (msg == "CAUGHT")
        {
            string activeScene = SceneManager.GetActiveScene().name;
            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] Received TEAM13_CAUGHT — scene='{activeScene}', GameManager.instance={(GameManager.instance != null ? "present" : "NULL")}, playerMove={(playerMove != null ? playerMove.gameObject.name : "NULL")}, sleepingManager={(sleepingManager != null ? sleepingManager.gameObject.name : "NULL")}.");

            if (caughtHandled)
            {
                if (showDebugLogs)
                    Debug.Log($"[ChildUdpReceiver] TEAM13_CAUGHT ignored — scene='{activeScene}', game-over handling already started.");
                return;
            }

            caughtHandled = true;

            // GameManagerフローを優先し、スコア保存とUDP送信の整合性を保つ
            if (GameManager.instance != null)
            {
                if (showDebugLogs)
                    Debug.Log($"[ChildUdpReceiver] CAUGHT fallback skipped — scene='{activeScene}', GameManager.instance is present.");
                GameManager.instance.TriggerResult(GameManager.ResultType.GameOver);
            }
            else
            {
                // フォールバック：既に結果画面やタイトル画面にいる場合の二重ロードを防止
                bool shouldRunFallback = activeScene != "ChildGameOver" &&
                                         activeScene != "ChildGameClear" &&
                                         activeScene != "ChildLoading" &&
                                         !IsTitleScene(activeScene);
                if (shouldRunFallback)
                {
                    if (showDebugLogs)
                        Debug.Log($"[ChildUdpReceiver] CAUGHT fallback executed — scene='{activeScene}', loading 'ChildGameOver'.");
                    // GameManager.instance が未設定でも、シーン内に実体があればそこから実スコアを取得する。
                    // それも見つからない場合のみ 0 点として扱う（最終手段）。
                    int finalScore = 0;
                    GameManager gm = GameManager.instance != null ? GameManager.instance : FindFirstObjectByType<GameManager>();
                    if (gm != null)
                    {
                        finalScore = gm.score;
                    }
                    PlayerPrefs.SetInt("LastGameOverScore", finalScore);
                    PlayerPrefs.Save();
                    SendState($"CHILD_SCORE:GAME_OVER:{finalScore}");
                    SceneManager.LoadScene("ChildGameOver");
                }
                else if (showDebugLogs)
                {
                    Debug.Log($"[ChildUdpReceiver] CAUGHT fallback skipped — scene='{activeScene}'.");
                }
            }

            return;
        }
        if (msg.StartsWith("RETURN_TO_TITLE:", StringComparison.Ordinal))
        {
            // RETURN_TO_TITLE:<playSessionId>:<seq>（旧: RETURN_TO_TITLE:<seq> / RETURN_TO_TITLE）
            string rest = msg.Substring("RETURN_TO_TITLE:".Length);
            string sid = "";
            string seqText = rest;

            int sep = rest.IndexOf(':');
            if (sep >= 0)
            {
                sid = rest.Substring(0, sep);
                seqText = rest.Substring(sep + 1);
            }

            int seq = 0;
            if (!int.TryParse(seqText, out seq))
                seq = 0;

            HandleTeamReturnToTitle(sid, seq);
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

            // 親機が寝ている間は、子機側にも睡眠中画像を表示する。
            SetSleepLockImageVisible(true);

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

            // 親機が起きたら、子機側の睡眠中画像を非表示にする。
            SetSleepLockImageVisible(false);

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

    /// <summary>
    /// 親機からの「タイトルへ戻る」通知（TEAM13_RETURN_TO_TITLE:&lt;識別子&gt;:&lt;連番&gt;）を受信したときの処理。
    /// 親機に合わせて子機もタイトル画面へ戻る。
    /// ・識別子が現在のプレイと異なる → 別プレイ（前回の再送・遅延、再起動前の通知）として無視する。
    /// ・識別子が一致し、連番が「最後に処理した連番」以下 → 同プレイ内の重複として無視する。
    /// リザルト画面にいる場合は ResultSceneChamger のフェード演出を流用し、
    /// ゲームプレイ中などの演出がない場合は即座に遷移する。
    /// </summary>
    private void HandleTeamReturnToTitle(string playSessionId, int seq)
    {
        // 旧フォーマット（識別子なし）は、こちらに識別子が無い場合のみ受け付ける（後方互換）。
        bool sessionMatches = !string.IsNullOrEmpty(playSessionId)
            ? playSessionId == _playSessionId
            : string.IsNullOrEmpty(_playSessionId);

        if (!sessionMatches)
        {
            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] RETURN_TO_TITLE ignored — different play session (recv='{playSessionId}', current='{_playSessionId}').");
            return;
        }

        if (seq <= _lastPeerReturnToTitleSeq)
        {
            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] RETURN_TO_TITLE ignored — stale/duplicate seq={seq} (last processed={_lastPeerReturnToTitleSeq}).");
            return;
        }
        _lastPeerReturnToTitleSeq = seq;

        string activeScene = SceneManager.GetActiveScene().name;
        if (IsTitleScene(activeScene))
        {
            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] RETURN_TO_TITLE ignored — already on title scene.");
            return;
        }

        ResultSceneChamger changer = FindFirstObjectByType<ResultSceneChamger>();
        if (changer != null)
        {
            if (changer.IsReturningToTitle)
            {
                if (showDebugLogs)
                    Debug.Log("[ChildUdpReceiver] RETURN_TO_TITLE ignored — already returning to title (fade in progress).");
                return;
            }

            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] RETURN_TO_TITLE received — starting result fade to title.");
            changer.StartFadeToTitle(notifyPeer: false);
            return;
        }

        Debug.Log($"[ChildUdpReceiver] RETURN_TO_TITLE received — returning to title from scene={activeScene}.");
        SceneManager.LoadScene(SceneNameResolver.Resolve(titleSceneName));
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

            // QTE中などで Time.timeScale が 0 になっても PING を止めないよう、実時間で待つ。
            // （scaled time だと PING が途絶え、親機が接続切れと誤判定して
            //   SLEEP_LOCK / SLEEP_UNLOCK などを以後すべて送らなくなる）
            yield return new WaitForSecondsRealtime(pingInterval);
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

    /// <summary>
    /// 親機のSLEEP_LOCK状態に合わせて、子機側の睡眠中画像を表示／非表示にする。
    /// </summary>
    private void SetSleepLockImageVisible(bool visible)
    {
        // シーン遷移後などで参照が切れていた場合は再検索する。
        if (sleepLockImageObject == null)
            sleepLockImageObject = FindGameObject(null, sleepLockImageObjectName,
                "SleepLockImage", "SleepLockUI", "SleepImage", "SleepLockPanel");

        if (sleepLockImageObject != null)
        {
            sleepLockImageObject.SetActive(visible);

            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] Sleep lock image {(visible ? "shown" : "hidden")}.");
        }
        else if (showDebugLogs && visible)
        {
            Debug.LogWarning(
                $"[ChildUdpReceiver] Sleep lock image not found. " +
                $"Assign it in Inspector or create a GameObject named '{sleepLockImageObjectName}'.");
        }
    }

    private IEnumerator RefreshSleepLockImageNextFrame()
    {
        // Canvas/UIがシーンロード直後に生成される場合に備えて1フレーム待つ。
        yield return null;

        sleepLockImageObject = FindGameObject(null, sleepLockImageObjectName,
            "SleepLockImage", "SleepLockUI", "SleepImage", "SleepLockPanel");
        SetSleepLockImageVisible(isSleepInputLocked);
    }

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