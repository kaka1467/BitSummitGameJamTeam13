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

    // ── Debug ─────────────────────────────────────────────────────────────────
    [Header("Debug")]
    [Tooltip("Enable verbose console logging for on-site verification. Disable in production.")]
    [SerializeField] private bool showDebugLogs = true;

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
            if (showDebugLogs) Debug.Log($"[ArduinoInputListener] Subscribed to PillowSensor.OnRawLine (port: {pillowSensor.PortName}, baud: {pillowSensor.BaudRate}).");
        }
        else
        {
            Debug.LogError("[ArduinoInputListener] PillowSensor not found — serial lines will NOT be received. Assign it in the Inspector.");
        }

        if (udpSender == null)
            Debug.LogError("[ArduinoInputListener] ParentUdpSender (udpSender) is not assigned — CAUGHT will never be forwarded over UDP.");
    }

    private void OnDestroy()
    {
        if (pillowSensor != null)
            pillowSensor.OnRawLine -= HandleLine;
    }

    private void HandleLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        if (showDebugLogs) Debug.Log($"[ArduinoInputListener] Raw line received: '{line}'");

        if (line == "BOOT_OK")
        {
            if (showDebugLogs) Debug.Log("[ArduinoInputListener] BOOT_OK — Arduino is ready.");
        }
        else if (line == "ERR_NOT_FOUND")
        {
            Debug.LogWarning("[ArduinoInputListener] HX711 sensor not ready (ERR_NOT_FOUND).");
        }
        else if (line == "CAUGHT")
        {
            if (showDebugLogs) Debug.Log("[ArduinoInputListener] CAUGHT detected — forwarding to ParentUdpSender.");
            if (udpSender != null)
                udpSender.SendState("CAUGHT");
            else
                Debug.LogError("[ArduinoInputListener] udpSender is not assigned — CAUGHT was detected but NOT sent over UDP.");
        }
        else
        {
            if (showDebugLogs) Debug.Log($"[ArduinoInputListener] Unrecognised line (ignored): '{line}'");
        }
    }
}
