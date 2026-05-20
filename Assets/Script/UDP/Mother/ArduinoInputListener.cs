using System;
using System.IO.Ports;
using UnityEngine;

/// <summary>
/// ArduinoInputListener:
/// Reads lines from a USB serial port (ESP32/Arduino) each frame.
/// DtrEnable and RtsEnable are set for reliable ESP32 auto-reset handshake.
/// Logs every non-empty received line for debugging.
/// Recognised commands:
///   "BOOT_OK"       — device ready confirmation, logged and ignored
///   "CAUGHT"        — calls ParentUdpSender.SendState("CAUGHT")
///   "ERR_NOT_FOUND" — warns that the HX711 load-cell sensor is not ready
/// All other lines are logged verbatim and ignored.
/// </summary>
public class ArduinoInputListener : MonoBehaviour
{
    // ── Serial port settings ──────────────────────────────────────────────────
    [Header("Serial Port")]
    [Tooltip("Serial port name, e.g. COM3 on Windows or /dev/ttyUSB0 on Linux/Mac.")]
    [SerializeField] private string portName = "COM3";

    [Tooltip("Baud rate — must match Serial.begin() in the Arduino sketch (e.g. 115200).")]
    [SerializeField] private int baudRate = 115200;

    // ── References ────────────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("ParentUdpSender that will transmit CAUGHT over UDP when triggered.")]
    [SerializeField] private ParentUdpSender udpSender;

    // ── Private ───────────────────────────────────────────────────────────────
    private SerialPort _port;

    // ──────────────────────────────────────────────────────────────────────────
    //  Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        try
        {
            _port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 20,
                DtrEnable   = true,
                RtsEnable   = true
            };
            _port.Open();

            // Flush any stale bytes left in the hardware buffers after open.
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();

            Debug.Log($"[ArduinoInputListener] Serial port '{portName}' opened successfully at {baudRate} baud.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ArduinoInputListener] Failed to open serial port '{portName}': {e.Message}");
            _port = null;
        }
    }

    private void Update()
    {
        if (_port == null || !_port.IsOpen) return;

        try
        {
            string line = _port.ReadLine().Trim();

            if (string.IsNullOrEmpty(line)) return;

            Debug.Log($"[ArduinoInputListener] Received: '{line}'");

            if (line == "BOOT_OK")
            {
                // Device ready — no action needed.
            }
            else if (line == "ERR_NOT_FOUND")
            {
                Debug.LogWarning("[ArduinoInputListener] HX711 sensor not ready (ERR_NOT_FOUND).");
            }
            else if (line == "CAUGHT")
            {
                Debug.Log("[ArduinoInputListener] Sending CAUGHT via ParentUdpSender");
                if (udpSender != null)
                    udpSender.SendState("CAUGHT");
                else
                    Debug.LogWarning("[ArduinoInputListener] udpSender is not assigned — CAUGHT not sent.");
            }
        }
        catch (TimeoutException)
        {
            // No data available this frame — expected and safe to ignore.
        }
        catch (Exception e)
        {
            Debug.LogError($"[ArduinoInputListener] Serial read error: {e.Message}");
        }
    }

    private void OnDestroy()
    {
        if (_port != null)
        {
            try
            {
                if (_port.IsOpen)
                    _port.Close();

                _port.Dispose();
                Debug.Log("[ArduinoInputListener] Serial port closed.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ArduinoInputListener] Error closing serial port: {e.Message}");
            }
            _port = null;
        }
    }
}
