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

public class ChildUdpReceiver : MonoBehaviour
{
    private const string MAGIC_NUMBER = "TEAM13_";

    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected
    }

    public int normalPort = 8000;
    public int broadcastPort = 8001;
    public int parentReceivePort = 8002;
    public string targetIP = "127.0.0.1";
    public ConnectionState currentState = ConnectionState.Disconnected;
    public string lastMessage = "";
    public string gameSceneName = "GameScene";
    public SleepingManager sleepingManager;
    public Button connectButton;
    public TextMeshProUGUI connectButtonLabel;
    public Button creditsButton;
    public Button settingsButton;
    public Button startButton;
    public TextMeshProUGUI statusText;
    public Button cancelButton;

    [Header("New Cancel UI Object Support")]
    [Tooltip("Cancelの背景画像や枠など、消したい装飾がすべて含まれている一番外側の親オブジェクトをアサインしてください")]
    public GameObject cancelUiObject; // ★文字だけでなく、UI全体を完全に消すために新しく追加しました

    [SerializeField] private GameObject creditsPanel;
    [SerializeField] private GameObject settingsPanel;
    [SerializeField] private string connectLabel = "Connect";
    [SerializeField] private string connectingLabel = "接続中";
    private string connectLabelDefault;

    public void OnConnectButtonClicked()
    {
        // If already connected, use the connect button as the Start button
        if (currentState == ConnectionState.Connected)
        {
            OnStartButtonClicked();
        }
        else
        {
            currentState = ConnectionState.Connecting;
        }
    }

    public void OnCancelButtonClicked()
    {
        currentState = ConnectionState.Disconnected;
    }

    public void OnCreditsButtonClicked()
    {
        if (creditsPanel == null)
        {
            return;
        }

        creditsPanel.SetActive(!creditsPanel.activeSelf);
    }

    public void OnCloseCreditsClicked()
    {
        if (creditsPanel == null)
        {
            return;
        }

        creditsPanel.SetActive(false);
    }

    public void OnSettingsButtonClicked()
    {
        if (settingsPanel == null)
        {
            return;
        }

        settingsPanel.SetActive(!settingsPanel.activeSelf);
    }

    public void OnCloseSettingsClicked()
    {
        if (settingsPanel == null)
        {
            return;
        }

        settingsPanel.SetActive(false);
    }

    public void SendState(string message)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + message);
            sendClient.Send(data, data.Length, targetIP, parentReceivePort);
        }
        catch (Exception e)
        {
            Debug.LogError("Error sending UDP message: " + e.Message);
        }
    }

    private UdpClient udpClient;
    private UdpClient sendClient;
    private Thread receiveThread;
    private bool isRunning = true;
    private ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
    private ConcurrentQueue<Action> actionQueue = new ConcurrentQueue<Action>();
    private Coroutine discoveryCoroutine;
    private Coroutine heartbeatCoroutine;
    private float lastReceiveTime;
    private float pingInterval = 1.0f;
    private float timeoutLimit = 3.0f;

    void Start()
    {
        Debug.Log("Start called");
        udpClient = new UdpClient(normalPort);
        sendClient = new UdpClient();
        receiveThread = new Thread(new ThreadStart(ReceiveData));
        receiveThread.IsBackground = true;
        receiveThread.Start();
        if (connectButtonLabel != null)
        {
            connectLabelDefault = connectButtonLabel.text;
        }
        // Wire up connect button to handle both connect and start actions
        if (connectButton != null)
        {
            connectButton.onClick.RemoveAllListeners();
            connectButton.onClick.AddListener(OnConnectButtonClicked);
        }

        UpdateUi();
    }

    public void OnStartButtonClicked()
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes("START");
            // send to parent using configured targetIP and parentReceivePort
            sendClient.Send(data, data.Length, targetIP, parentReceivePort);
            Debug.Log("Sent START to parent at " + targetIP + ":" + parentReceivePort);
        }
        catch (Exception e)
        {
            Debug.LogError("Error sending START UDP message: " + e.Message);
        }

        // Immediately transition to the gameplay scene
        SceneManager.LoadScene(gameSceneName);
    }

    void Update()
    {
        while (messageQueue.TryDequeue(out string message))
        {
            if (message.StartsWith(MAGIC_NUMBER))
            {
                message = message.Substring(MAGIC_NUMBER.Length);
                if (message == "PING")
                {
                    lastReceiveTime = Time.time;
                }
                // 親機側からの開始パケットも安全に待機
                else if (message == "START" || message == "START_GAME")
                {
                    SceneManager.LoadScene(gameSceneName);
                }
                else
                {
                    lastMessage = message;
                    if (message == "CAUGHT" && sleepingManager != null)
                    {
                        Debug.Log("calling SetCaughtState");
                        sleepingManager.SetCaughtState();
                    }
                }
            }
        }

        while (actionQueue.TryDequeue(out Action action))
        {
            action();
        }

        if (currentState == ConnectionState.Connected)
        {
            if (Time.time - lastReceiveTime > timeoutLimit)
            {
                currentState = ConnectionState.Disconnected;
                Debug.LogWarning("Connection lost!");
            }
        }

        if (currentState == ConnectionState.Connecting && discoveryCoroutine == null)
        {
            discoveryCoroutine = StartCoroutine(DiscoveryCoroutine());
        }
        else if (currentState != ConnectionState.Connecting && discoveryCoroutine != null)
        {
            StopCoroutine(discoveryCoroutine);
            discoveryCoroutine = null;
        }

        if (currentState == ConnectionState.Connected && heartbeatCoroutine == null)
        {
            heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
        }
        else if (currentState != ConnectionState.Connected && heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }

        UpdateUi();
    }

    private void UpdateUi()
    {
        // 1. 接続状態に応じて、一本化したメインボタンのラベルテキストを切り替える
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

        // 2. メインのボタン本体は常に表示（Connected時はSTARTボタンになるため）
        if (connectButton != null)
        {
            connectButton.gameObject.SetActive(true);
        }

        // 3. ★今回の修正の核心部分
        // 新しく追加した全体のゲームオブジェクト枠（cancelUiObject）が設定されていれば、それを丸ごと制御する
        if (cancelUiObject != null)
        {
            cancelUiObject.SetActive(currentState == ConnectionState.Connecting);
        }
        else if (cancelButton != null)
        {
            // 設定されていない場合は、従来のボタンコンポーネントのみを制御（安全弁）
            cancelButton.gameObject.SetActive(currentState == ConnectionState.Connecting);
        }

        // 4. クレジットや設定ボタンは、接続成功（ゲーム開始可能状態）になったら邪魔なので隠す
        if (creditsButton != null)
        {
            creditsButton.gameObject.SetActive(currentState != ConnectionState.Connected);
        }

        if (settingsButton != null)
        {
            settingsButton.gameObject.SetActive(currentState != ConnectionState.Connected);
        }

        // 5. 接続中（Connecting）のときだけボタンを一時的に連打できなくする
        if (connectButton != null)
        {
            connectButton.interactable = currentState != ConnectionState.Connecting;
        }
    }

    private static void SetActiveForButton(Button button, bool active)
    {
        if (button != null)
        {
            GameObject target = button.gameObject;
            if (button.transform.parent != null)
            {
                target = button.transform.parent.gameObject;
            }
            target.SetActive(active);
        }
    }

    private static void SetActiveForText(TextMeshProUGUI text, bool active)
    {
        if (text != null)
        {
            GameObject target = text.gameObject;
            if (text.transform.parent != null)
            {
                target = text.transform.parent.gameObject;
            }
            target.SetActive(active);
        }
    }

    private IEnumerator DiscoveryCoroutine()
    {
        while (currentState == ConnectionState.Connecting)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + "DISCOVERY_REQUEST");
                sendClient.Send(data, data.Length, "255.255.255.255", broadcastPort);
                Debug.Log("Sent DISCOVERY_REQUEST");
            }
            catch (Exception e)
            {
                Debug.LogError("Error sending discovery: " + e.Message);
            }
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
            catch (Exception e)
            {
                Debug.LogError("Error sending ping: " + e.Message);
            }
            yield return new WaitForSeconds(pingInterval);
        }
    }

    private void ReceiveData()
    {
        while (isRunning)
        {
            try
            {
                IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, normalPort);
                byte[] data = udpClient.Receive(ref remoteEndPoint);
                string message = Encoding.UTF8.GetString(data);
                Debug.Log("Received: " + message);
                if (message == MAGIC_NUMBER + "DISCOVERY_ACCEPT")
                {
                    actionQueue.Enqueue(() =>
                    {
                        targetIP = remoteEndPoint.Address.ToString();
                        currentState = ConnectionState.Connected;
                        lastReceiveTime = Time.time;
                    });
                }
                messageQueue.Enqueue(message);
            }
            catch (Exception e)
            {
                if (isRunning)
                {
                    Debug.LogError("Error receiving UDP message: " + e.Message);
                }
            }
        }
    }

    void OnDestroy()
    {
        isRunning = false;
        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Abort();
        }
        if (udpClient != null)
        {
            udpClient.Close();
        }
        if (sendClient != null)
        {
            sendClient.Close();
        }
        if (discoveryCoroutine != null)
        {
            StopCoroutine(discoveryCoroutine);
        }
        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
        }
    }
}