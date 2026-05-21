using System.Collections;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using UnityEngine;

/// <summary>
/// Reads raw integer values from an ESP32 over USB (Serial) and detects "pretend to sleep"
/// when the load-cell value deviates from an auto-calibrated baseline by more than a threshold.
/// - Runs serial reads on a background thread.
/// - Uses a lock to publish the latest value to the main thread.
/// - Provides an auto-calibration routine and a public ResetBaseline() method.
/// - Safely closes the serial port on quit/destroy.
/// </summary>
public class PillowSensor : MonoBehaviour
{
    [Header("Serial Settings")]
    [Tooltip("COM port name (change in Inspector if needed)")]
    [SerializeField] private string portName = "COM3";
    public string PortName => portName;
    [Tooltip("Serial baud rate")]
    [SerializeField] private int baudRate = 115200;
    public int BaudRate => baudRate;
    [Tooltip("Serial read timeout (ms)")]
    [SerializeField] private int readTimeout = 50;

    [Header("Detection Settings")]
    [Tooltip("Absolute delta threshold from baseline to consider 'sleeping'")]
    [System.Obsolete("Use onThreshold/offThreshold hysteresis settings instead.")]
    [SerializeField] private long threshold = 50000;
    [SerializeField] private long onThreshold = 100000;
    [SerializeField] private long offThreshold = 70000;

    [Header("Calibration Settings")]
    [Tooltip("Number of samples to average when calibrating baseline")]
    [SerializeField] private int calibrationSamples = 50;
    [Tooltip("Delay between calibration samples (seconds)")]
    [SerializeField] private float calibrationSampleDelay = 0.02f;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;

    // Public flag indicating whether the player is detected as "sleeping".
    public bool isSleeping = false;

    /// <summary>
    /// Fired on the main thread for every non-numeric line received from the serial port.
    /// Subscribe to this instead of opening a second SerialPort on the same COM port.
    /// </summary>
    public event System.Action<string> OnRawLine;

    // The baseline value established during calibration.
    private long baseline = 0L;
    private bool baselineReady = false;

    // Background serial thread and control flag.
    private Thread serialThread;
    private volatile bool isRunning = false;

    // Serial port instance.
    private SerialPort serialPort;

    // Shared latest value with thread safety.
    private readonly object valueLock = new object();
    private long latestValue = 0L;
    private bool hasValue = false;

    // Non-numeric lines queued by background thread, drained on main thread in Update().
    private readonly Queue<string> _pendingRawLines = new Queue<string>();

    // Graceful join timeout (ms) for background thread on shutdown.
    private const int ThreadJoinTimeoutMs = 500;

    void Start()
    {
        OpenSerialPort();
        // Start background reader if port opened successfully.
        if (serialPort != null && serialPort.IsOpen)
        {
            isRunning = true;
            serialThread = new Thread(SerialReadLoop) { IsBackground = true };
            serialThread.Start();
            // Start auto-calibration after thread begins producing values.
            StartCoroutine(AutoCalibrateBaselineCoroutine());
        }
        else
        {
            Debug.LogError($"PillowSensor: Failed to open serial port {portName}.");
        }
    }

    /// <summary>
    /// Opens and configures the serial port with safe exception handling.
    /// </summary>
    private void OpenSerialPort()
    {
        try
        {
            serialPort = new SerialPort(portName, baudRate)
            {
                ReadTimeout = readTimeout,
                NewLine = "\n",
                DtrEnable = true,
                RtsEnable = true
            };
            serialPort.Open();
            if (showDebugLogs) Debug.Log($"PillowSensor: Opened serial port {portName} @ {baudRate}.");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"PillowSensor: Exception opening serial port {portName}: {ex.Message}");
            serialPort = null;
        }
    }

    /// <summary>
    /// Background loop: reads lines from the serial port and publishes the latest parsed integer.
    /// Executes continuously while isRunning is true.
    /// </summary>
    private void SerialReadLoop()
    {
        while (isRunning && serialPort != null && serialPort.IsOpen)
        {
            try
            {
                string line = serialPort.ReadLine(); // Running on background thread per requirements.
                if (string.IsNullOrEmpty(line))
                    continue;

                line = line.Trim();
                if (long.TryParse(line, out long parsed))
                {
                    lock (valueLock)
                    {
                        latestValue = parsed;
                        hasValue = true;
                    }
                }
                else
                {
                    // Non-numeric line — forward to subscribers on the main thread via pending queue
                    string captured = line;
                    lock (valueLock) { _pendingRawLines.Enqueue(captured); }
                    if (showDebugLogs) Debug.Log($"PillowSensor: Non-numeric line queued for dispatch: '{line}'.");
                }
            }
            catch (System.TimeoutException)
            {
                // ReadTimeout expired - this is expected from time to time. Ignore and continue.
            }
            catch (System.Exception ioEx)
            {
                Debug.LogError($"PillowSensor: Serial IO exception: {ioEx.Message}");
                // On IO exceptions, break the loop to avoid tight error loop.
                break;
            }
        }

        // Ensure port closure if we exit loop due to exception.
        SafeClosePort();
    }

    void Update()
    {
        // Drain non-numeric lines and fire OnRawLine on the main thread.
        while (true)
        {
            string rawLine = null;
            lock (valueLock)
            {
                if (_pendingRawLines.Count > 0)
                    rawLine = _pendingRawLines.Dequeue();
            }
            if (rawLine == null) break;
            OnRawLine?.Invoke(rawLine);
        }

        // Read the latest value from the background thread in a thread-safe manner.
        long currentValue = 0L;
        bool currentHasValue = false;
        lock (valueLock)
        {
            currentHasValue = hasValue;
            currentValue = latestValue;
        }

        // If we don't have a calibrated baseline yet, don't attempt detection.
        if (!currentHasValue || !baselineReady)
        {
            isSleeping = false;
            return;
        }

        // Calculate absolute delta from baseline and determine sleep state.
        long delta = System.Math.Abs(currentValue - baseline);
        if (!isSleeping)
        {
            isSleeping = delta >= onThreshold;
        }
        else if (delta <= offThreshold)
        {
            isSleeping = false;
        }

        if (showDebugLogs && Time.frameCount % 60 == 0) // Occasional debug log.
        {
            Debug.Log($"PillowSensor: current={currentValue}, baseline={baseline}, delta={delta}, isSleeping={isSleeping}");
        }
    }

    /// <summary>
    /// Public method to trigger baseline recalibration at runtime.
    /// </summary>
    public void ResetBaseline()
    {
        // Stop any running calibration and start a fresh one.
        StopCoroutine(AutoCalibrateBaselineCoroutine());
        StartCoroutine(AutoCalibrateBaselineCoroutine());
    }

    /// <summary>
    /// Coroutine that gathers a number of recent samples on the main thread and computes an averaged baseline.
    /// This uses the latestValue published by the background thread.
    /// </summary>
    private IEnumerator AutoCalibrateBaselineCoroutine()
    {
        baselineReady = false;
        isSleeping = false;
        if (showDebugLogs) Debug.Log("PillowSensor: Starting baseline calibration...");

        // Wait until we have at least one reading or timeout after ~2 seconds.
        float waitStart = Time.time;
        while (true)
        {
            lock (valueLock)
            {
                if (hasValue)
                    break;
            }
            if (Time.time - waitStart > 2.0f)
            {
                Debug.LogWarning("PillowSensor: Calibration timed out waiting for serial data.");
                yield break;
            }
            yield return null;
        }

        long sum = 0;
        int collected = 0;
        for (int i = 0; i < calibrationSamples; i++)
        {
            long sample = 0;
            lock (valueLock)
            {
                sample = latestValue;
            }
            sum += sample;
            collected++;
            yield return new WaitForSeconds(calibrationSampleDelay);
        }

        if (collected > 0)
        {
            baseline = sum / collected;
            baselineReady = true;
            if (showDebugLogs) Debug.Log($"PillowSensor: Baseline calibration complete. Baseline={baseline} (samples={collected}). Detection is now enabled.");
        }
        else
        {
            Debug.LogWarning("PillowSensor: Calibration failed to collect samples.");
        }
    }

    /// <summary>
    /// Attempts to close the serial port safely.
    /// </summary>
    private void SafeClosePort()
    {
        try
        {
            if (serialPort != null)
            {
                if (serialPort.IsOpen)
                {
                    serialPort.Close();
                    if (showDebugLogs) Debug.Log($"PillowSensor: Closed serial port {portName}.");
                }
                serialPort.Dispose();
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"PillowSensor: Exception closing serial port: {ex.Message}");
        }
        finally
        {
            serialPort = null;
        }
    }

    private void StopSerialThread()
    {
        isRunning = false;
        try
        {
            if (serialThread != null && serialThread.IsAlive)
            {
                // Wait briefly for the thread to finish gracefully.
                if (!serialThread.Join(ThreadJoinTimeoutMs))
                {
                    // If it doesn't stop, log and continue (thread is background).
                    if (showDebugLogs) Debug.LogWarning("PillowSensor: Serial thread did not terminate in time.");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"PillowSensor: Exception while stopping serial thread: {ex.Message}");
        }
    }

    void OnApplicationQuit()
    {
        StopSerialThread();
        SafeClosePort();
    }

    void OnDestroy()
    {
        StopSerialThread();
        SafeClosePort();
    }
}
