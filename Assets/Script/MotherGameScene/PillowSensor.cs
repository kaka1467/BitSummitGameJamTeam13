using System.Collections;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using UnityEngine;

/// <summary>
/// USB（シリアル）経由でESP32から整数値を読み取り、
/// ロードセル値が自動較正した基準値から閾値以上ずれたとき「寝たふり」を検出する。
/// - シリアル読み取りはバックグラウンドスレッドで実行する。
/// - lockを使って最新値をメインスレッドへ公開する。
/// - 自動較正処理と公開ResetBaseline()メソッドを提供する。
/// - 終了／破棄時にシリアルポートを安全に閉じる。
/// </summary>
public class PillowSensor : MonoBehaviour
{
    [Header("シリアル設定")]
    [Tooltip("COMポート名（必要に応じてインスペクターで変更）")]
    [SerializeField] private string portName = "COM3";
    public string PortName => portName;
    [Tooltip("シリアル通信のボーレート")]
    [SerializeField] private int baudRate = 115200;
    public int BaudRate => baudRate;
    [Tooltip("シリアル読み取りタイムアウト（ミリ秒）")]
    [SerializeField] private int readTimeout = 50;

    [Header("検出設定")]
    [Tooltip("「睡眠中」と判定する基準値からの絶対差分閾値")]
    [System.Obsolete("Use onThreshold/offThreshold hysteresis settings instead.")]
    [SerializeField] private long threshold = 50000;
    [SerializeField] private long onThreshold = 100000;
    [SerializeField] private long offThreshold = 70000;

    [Header("較正設定")]
    [Tooltip("基準値の較正時に平均するサンプル数")]
    [SerializeField] private int calibrationSamples = 50;
    [Tooltip("較正サンプル間の遅延（秒）")]
    [SerializeField] private float calibrationSampleDelay = 0.02f;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs = false;

    // プレイヤーが「睡眠中」と検出されたかを示す公開フラグ。
    public bool isSleeping = false;

    /// <summary>
    /// シリアルポートから受信した数値以外の各行について、メインスレッドで発生する。
    /// 同じCOMポートで2つ目のSerialPortを開く代わりに、これを購読する。
    /// </summary>
    public event System.Action<string> OnRawLine;

    // 較正時に確立した基準値。
    private long baseline = 0L;
    private bool baselineReady = false;

    // バックグラウンドのシリアルスレッドと制御フラグ。
    private Thread serialThread;
    private volatile bool isRunning = false;

    // シリアルポートのインスタンス。
    private SerialPort serialPort;

    // スレッドセーフに共有する最新値。
    private readonly object valueLock = new object();
    private long latestValue = 0L;
    private bool hasValue = false;

    // バックグラウンドスレッドがキューに入れた数値以外の行。Update()でメインスレッドから取り出す。
    private readonly Queue<string> _pendingRawLines = new Queue<string>();

    // 終了時にバックグラウンドスレッドを正常結合するためのタイムアウト（ミリ秒）。
    private const int ThreadJoinTimeoutMs = 500;

    void Start()
    {
        OpenSerialPort();
        // ポートのオープンに成功した場合、バックグラウンド読み取りを開始する。
        if (serialPort != null && serialPort.IsOpen)
        {
            isRunning = true;
            serialThread = new Thread(SerialReadLoop) { IsBackground = true };
            serialThread.Start();
            // スレッドが値を生成し始めた後、自動較正を開始する。
            StartCoroutine(AutoCalibrateBaselineCoroutine());
        }
        else
        {
            Debug.LogError($"PillowSensor: シリアルポート{portName}を開けませんでした。");
        }
    }

    /// <summary>
    /// 安全な例外処理を行い、シリアルポートを開いて設定する。
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
    /// バックグラウンドループ：シリアルポートから行を読み、解析した最新の整数を公開する。
    /// isRunningがtrueの間、継続的に実行する。
    /// </summary>
    private void SerialReadLoop()
    {
        while (isRunning && serialPort != null && serialPort.IsOpen)
        {
            try
            {
                string line = serialPort.ReadLine(); // 要件によりバックグラウンドスレッドで実行する。
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
                    // 数値以外の行 — 保留キュー経由でメインスレッドの購読者へ転送する
                    string captured = line;
                    lock (valueLock) { _pendingRawLines.Enqueue(captured); }
                    if (showDebugLogs) Debug.Log($"PillowSensor: Non-numeric line queued for dispatch: '{line}'.");
                }
            }
            catch (System.TimeoutException)
            {
                // ReadTimeoutが期限切れ — 時々発生する想定内の動作。無視して続行する。
            }
            catch (System.Exception ioEx)
            {
                Debug.LogError($"PillowSensor: Serial IO exception: {ioEx.Message}");
                // IO例外時はエラーの高速ループを避けるためループを抜ける。
                break;
            }
        }

        // 例外でループを抜けた場合もポートを確実に閉じる。
        SafeClosePort();
    }

    void Update()
    {
        // 数値以外の行を取り出し、メインスレッドでOnRawLineを発生させる。
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

        // バックグラウンドスレッドの最新値をスレッドセーフに読み取る。
        long currentValue = 0L;
        bool currentHasValue = false;
        lock (valueLock)
        {
            currentHasValue = hasValue;
            currentValue = latestValue;
        }

        // 基準値の較正がまだなら検出を行わない。
        if (!currentHasValue || !baselineReady)
        {
            isSleeping = false;
            return;
        }

        // 基準値からの絶対差分を計算し、睡眠状態を判定する。
        long delta = System.Math.Abs(currentValue - baseline);
        if (!isSleeping)
        {
            isSleeping = delta >= onThreshold;
        }
        else if (delta <= offThreshold)
        {
            isSleeping = false;
        }

        if (showDebugLogs && Time.frameCount % 60 == 0) // 定期的なデバッグ記録。
        {
            Debug.Log($"PillowSensor: current={currentValue}, baseline={baseline}, delta={delta}, isSleeping={isSleeping}");
        }
    }

    /// <summary>
    /// 実行時に基準値の再較正を開始する公開メソッド。
    /// </summary>
    public void ResetBaseline()
    {
        // 実行中の較正を停止し、新しい較正を開始する。
        StopCoroutine(AutoCalibrateBaselineCoroutine());
        StartCoroutine(AutoCalibrateBaselineCoroutine());
    }

    /// <summary>
    /// メインスレッドで最近のサンプルを集め、平均基準値を計算するコルーチン。
    /// バックグラウンドスレッドが公開したlatestValueを使用する。
    /// </summary>
    private IEnumerator AutoCalibrateBaselineCoroutine()
    {
        baselineReady = false;
        isSleeping = false;
        if (showDebugLogs) Debug.Log("PillowSensor: 基準値の較正を開始...");

        // 少なくとも1つの読み取り値を得るか、約2秒後にタイムアウトするまで待つ。
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
                Debug.LogWarning("PillowSensor: シリアルデータ待機中に較正がタイムアウトしました。");
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
            Debug.LogWarning("PillowSensor: サンプルを収集できず、較正に失敗しました。");
        }
    }

    /// <summary>
    /// シリアルポートを安全に閉じる。
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
                // スレッドが正常終了するために短時間待つ。
                if (!serialThread.Join(ThreadJoinTimeoutMs))
                {
                    // 停止しない場合は記録して続行する（バックグラウンドスレッド）。
                    if (showDebugLogs) Debug.LogWarning("PillowSensor: シリアルスレッドが時間内に終了しませんでした。");
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
