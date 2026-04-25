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

public class ParentUdpSender : MonoBehaviour
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
    public Button connectButton;
    public TextMeshProUGUI statusText;
    public GameObject startButtonObject;
    public string gameSceneName = "GameScene";
    public Button cancelButton;

    public void OnConnectButtonClicked()
    {
        currentState = ConnectionState.Connecting;
    }

    public void OnCancelButtonClicked()
    {
        currentState = ConnectionState.Disconnected;
    }

    public void OnStartButtonClicked()
    {
        StartCoroutine(StartGameRoutine());
    }

    private IEnumerator StartGameRoutine()
    {
        SendState("START_GAME");
        yield return new WaitForSeconds(0.1f);
        SceneManager.LoadScene(gameSceneName);
    }

    private UdpClient udpClient;
    private UdpClient receiveClient;
    private UdpClient normalReceiveClient;
    private Thread receiveThread;
    private Thread normalReceiveThread;
    private bool isRunning = true;
    private ConcurrentQueue<Action> actionQueue = new ConcurrentQueue<Action>();
    private ConcurrentQueue<string> receiveQueue = new ConcurrentQueue<string>();
    private Coroutine heartbeatCoroutine;
    private float lastReceiveTime;
    private float pingInterval = 1.0f;
    private float timeoutLimit = 3.0f;

    void Start()
    {
        udpClient = new UdpClient();
        receiveClient = new UdpClient(broadcastPort);
        normalReceiveClient = new UdpClient(parentReceivePort);
        receiveThread = new Thread(new ThreadStart(ReceiveData));
        receiveThread.IsBackground = true;
        receiveThread.Start();
        normalReceiveThread = new Thread(new ThreadStart(ReceiveNormalData));
        normalReceiveThread.IsBackground = true;
        normalReceiveThread.Start();
    }

    public void SendState(string message)
    {
        Debug.Log("SendState caught");
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + message);
            udpClient.Send(data, data.Length, targetIP, normalPort);
        }
        catch (Exception e)
        {
            Debug.LogError("Error sending UDP message: " + e.Message);
        }
    }

    void Update()
    {
        while (actionQueue.TryDequeue(out Action action))
        {
            action();
        }

        while (receiveQueue.TryDequeue(out string message))
        {
            if (message == MAGIC_NUMBER + "PING")
            {
                lastReceiveTime = Time.time;
            }
            else if (message == MAGIC_NUMBER + "TIME_UP" || message == MAGIC_NUMBER + "CHILD_DEAD")
            {
                Debug.Log("Child game ended. Transitioning to Game Over...");
                SceneManager.LoadScene("MotherGameOver");
            }
        }

        if (currentState == ConnectionState.Connected)
        {
            if (Time.time - lastReceiveTime > timeoutLimit)
            {
                currentState = ConnectionState.Disconnected;
                Debug.LogWarning("Connection lost!");
            }
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

        if (statusText != null)
        {
            statusText.text = currentState == ConnectionState.Connecting ? "Connecting..." : currentState.ToString();
        }
        if (connectButton != null)
        {
            connectButton.gameObject.SetActive(currentState == ConnectionState.Disconnected);
        }
        if (cancelButton != null)
        {
            cancelButton.gameObject.SetActive(currentState == ConnectionState.Connecting);
        }
        if (startButtonObject != null)
        {
            startButtonObject.SetActive(currentState == ConnectionState.Connected);
        }

        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            SendState("CAUGHT");
        }
    }

    private IEnumerator HeartbeatCoroutine()
    {
        while (currentState == ConnectionState.Connected)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + "PING");
                udpClient.Send(data, data.Length, targetIP, normalPort);
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
                IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, broadcastPort);
                byte[] data = receiveClient.Receive(ref remoteEndPoint);
                string message = Encoding.UTF8.GetString(data);
                Debug.Log("Received: " + message + " from " + remoteEndPoint.Address);
                if (message == MAGIC_NUMBER + "DISCOVERY_REQUEST")
                {
                    string senderIP = remoteEndPoint.Address.ToString();
                    actionQueue.Enqueue(() =>
                    {
                        targetIP = senderIP;
                        currentState = ConnectionState.Connected;
                        lastReceiveTime = Time.time;
                        SendDiscoveryAccept(senderIP);
                    });
                }
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

    private void ReceiveNormalData()
    {
        while (isRunning)
        {
            try
            {
                IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, parentReceivePort);
                byte[] data = normalReceiveClient.Receive(ref remoteEndPoint);
                string message = Encoding.UTF8.GetString(data);
                Debug.Log("Received normal: " + message + " from " + remoteEndPoint.Address);
                receiveQueue.Enqueue(message);
            }
            catch (Exception e)
            {
                if (isRunning)
                {
                    Debug.LogError("Error receiving normal UDP message: " + e.Message);
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
            Debug.Log("Sent DISCOVERY_ACCEPT to " + ip);
        }
        catch (Exception e)
        {
            Debug.LogError("Error sending discovery accept: " + e.Message);
        }
    }

    void OnDestroy()
    {
        isRunning = false;
        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Abort();
        }
        if (normalReceiveThread != null && normalReceiveThread.IsAlive)
        {
            normalReceiveThread.Abort();
        }
        if (udpClient != null)
        {
            udpClient.Close();
        }
        if (receiveClient != null)
        {
            receiveClient.Close();
        }
        if (normalReceiveClient != null)
        {
            normalReceiveClient.Close();
        }
        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
        }
    }
}

// Inspector Setup Notes:
// - Attach this script to a GameObject in the parent Unity app.
// - Set the 'Target IP' field to '127.0.0.1' for localhost testing.
// - Set the 'Normal Port' field to match the port used by the ChildUdpReceiver (default: 8000).
// - Call SendState("CAUGHT") or SendState("SAFE") from other scripts to send messages.