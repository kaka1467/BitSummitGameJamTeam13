using UnityEngine;

/// <summary>
/// ArduinoInputListener:
/// Handles ESP32/Arduino command strings forwarded from PillowSensor.OnRawLine.
/// Does NOT open a SerialPort — PillowSensor is the sole port owner.
/// Recognised commands:
///   "BOOT_OK"       — device ready confirmation, logged and ignored
///   "CAUGHT"        — calls ParentUdpSender.SendState("CAUGHT")
///   "ERR_NOT_FOUND" — warns that the HX711 load-cell sensor is not ready
/// All other lines are logged verbatim and ignored.
/// </summary>
public class ArduinoInputListener : MonoBehaviour
{
    // ── References ────────────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("PillowSensor that owns the serial port. ArduinoInputListener subscribes to its OnRawLine event.")]
    [SerializeField] private PillowSensor pillowSensor;

    [Tooltip("ParentUdpSender that will transmit CAUGHT over UDP when triggered.")]
    [SerializeField] private ParentUdpSender udpSender;

    // ──────────────────────────────────────────────────────────────────────
    //  Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (pillowSensor == null)
            pillowSensor = Object.FindFirstObjectByType<PillowSensor>();

        if (pillowSensor != null)
        {
            pillowSensor.OnRawLine += HandleLine;
            Debug.Log("[ArduinoInputListener] Subscribed to PillowSensor.OnRawLine.");
        }
        else
        {
            Debug.LogWarning("[ArduinoInputListener] PillowSensor not found — command lines will not be received.");
        }
    }

    private void OnDestroy()
    {
        if (pillowSensor != null)
            pillowSensor.OnRawLine -= HandleLine;
    }

    private void HandleLine(string line)
    {
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
            Debug.Log("[ArduinoInputListener] Sending CAUGHT via ParentUdpSender.");
            if (udpSender != null)
                udpSender.SendState("CAUGHT");
            else
                Debug.LogWarning("[ArduinoInputListener] udpSender is not assigned — CAUGHT not sent.");
        }
        else
        {
            Debug.Log($"[ArduinoInputListener] Unrecognised line (ignored): '{line}'");
        }
    }
}
