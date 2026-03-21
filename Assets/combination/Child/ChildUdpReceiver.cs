using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class ChildUdpReceiver : MonoBehaviour
{
    public int port = 12345;
    public string lastMessage = "";
    public SleepingManager sleepingManager;

    private UdpClient udpClient;
    private Thread receiveThread;
    private bool isRunning = true;
    private ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();

    void Start()
    {
        Debug.Log("Start called");
        udpClient = new UdpClient(port);
        receiveThread = new Thread(new ThreadStart(ReceiveData));
        receiveThread.IsBackground = true;
        receiveThread.Start();
    }

    void Update()
    {
        while (messageQueue.TryDequeue(out string message))
        {
            lastMessage = message;
            if (message == "CAUGHT" && sleepingManager != null)
            {
                Debug.Log("calling SetCaughtState");
                sleepingManager.SetCaughtState();
            }
        }
    }

    private void ReceiveData()
    {
        while (isRunning)
        {
            try
            {
                IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, port);
                byte[] data = udpClient.Receive(ref remoteEndPoint);
                string message = Encoding.UTF8.GetString(data);
                Debug.Log("Received: " + message);
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
    }
}

// Inspector Setup Notes:
// - Attach this script to a GameObject in the child Unity app.
// - Set the 'Port' field to match the port used by the ParentUdpSender (default: 12345).
// - The 'Last Message' field will display the most recently received message.
// - Received messages are logged to the Unity console.