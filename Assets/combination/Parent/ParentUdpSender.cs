using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class ParentUdpSender : MonoBehaviour
{
    public string host = "127.0.0.1";
    public int port = 12345;

    private UdpClient udpClient;

    void Start()
    {
        udpClient = new UdpClient();
    }

    public void SendState(string message)
    {
        Debug.Log("SendState caught");
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            udpClient.Send(data, data.Length, host, port);
        }
        catch (Exception e)
        {
            Debug.LogError("Error sending UDP message: " + e.Message);
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            SendState("CAUGHT");
        }
    }

    void OnDestroy()
    {
        if (udpClient != null)
        {
            udpClient.Close();
        }
    }
}

// Inspector Setup Notes:
// - Attach this script to a GameObject in the parent Unity app.
// - Set the 'Host' field to '127.0.0.1' for localhost testing.
// - Set the 'Port' field to match the port used by the ChildUdpReceiver (default: 12345).
// - Call SendState("CAUGHT") or SendState("SAFE") from other scripts to send messages.