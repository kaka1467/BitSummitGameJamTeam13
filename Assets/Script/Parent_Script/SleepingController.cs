using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// SleepingController：PCデバッグ上書きを含むプレイヤーの睡眠状態を監視する。
/// PCデバッグ入力（Spaceキー）は最優先で、ハードウェアセンサーを上書きする。
/// 補助入力としてハードウェア枕センサー（シリアル／ESP32経由のPillowSensor）に対応する。
/// 新しい入力システムを統合し、PCでシームレスにテストできる。
/// 状態遷移時に親機Host経由でSLEEP_LOCK／SLEEP_UNLOCKを自動送信し、
/// 起きている間は誤ロック状態を回復するため定期的にSLEEP_UNLOCKを送信する。
/// </summary>
public class SleepingController : MonoBehaviour
{
    [Header("ハードウェアセンサー")]
    [SerializeField] private PillowSensor pillowSensor;

    [Header("安全用ハートビート")]
    [Tooltip("親機が起きている間にSLEEP_UNLOCKを再送する間隔（秒）。")]
    [SerializeField] private float awakeHeartbeatInterval = 1.0f;
    [Tooltip("親機が寝たふりをしている間にSLEEP_LOCKを再送する間隔（秒）。UDPの取りこぼしで子機が操作可能なままになるのを防ぐ。")]
    [SerializeField] private float sleepHeartbeatInterval = 1.0f;

    [Header("オーディオ")]
    [Tooltip("起きている状態から寝たふりに切り替わった瞬間に1回鳴らすAudioSource。未設定なら鳴らさない。")]
    [SerializeField] private AudioSource sleepStartAudioSource;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs;
    [Tooltip("trueの場合、枕センサーを無視し、Space／GamepadのみでisSleepingを制御します。デバッグ時にセンサーが不安定な場合に便利です。")]
    [SerializeField] private bool ignoreSensorForDebug;

    // プレイヤーの睡眠状態
    private bool _isSleeping;
    private bool _wasSleeping;

    // 安全用ハートビートタイマー
    private float _awakeHeartbeatTimer;
    private float _sleepHeartbeatTimer;

    // 毎フレーム大量に出力せず、Spaceの有効化を記録するための前回デバッグ入力状態
    private bool _wasDebugInputActive;

    // 診断用トラッカー：変化時のみ出力するため、最後に記録した値を保持する
    private bool _diagLastDebugInput;
    private bool _diagLastSensorSleeping;
    private bool _diagLastIsSleeping;
    private bool _diagLastWasSleeping;

    // 実行時解決する親機Host（Inspector割当なし・非シリアライズ）
    private ParentUdpRuntimeHost _host;

    /// <summary>
    /// 公開読み取り専用プロパティ：プレイヤーが睡眠中か（MotherSuspicionSystemとCaughtReactionControllerが使用）
    /// </summary>
    public bool IsSleeping => _isSleeping;

    private void Start()
    {
        GetHost();
        _isSleeping = false;
        _wasSleeping = false;

        // 起動時に親機Hostの状態を記録する。nullのままならGetHost()が実行時に再取得する。
        if (_host != null)
            Debug.Log($"[SC-DIAG] 親機Hostを取得しました: '{_host.gameObject.name}'");
        else
            Debug.LogWarning("[SC-DIAG] 親機Host（未稼働／未初期化）- GetHost()で実行時に再取得します。");

        // 設定されていれば枕センサーを初期化する
        if (pillowSensor != null)
        {
            pillowSensor.ResetBaseline();
            Debug.Log("SleepingController: 枕センサーの基準値を較正しました。");
        }
        else
        {
            Debug.LogWarning("SleepingController: PillowSensor参照が設定されていません！");
            Debug.Log("SleepingController: 代わりにPCデバッグ入力（SpaceキーまたはGamepad Button South）を使用します。");
        }
    }

    private void Update()
    {
        // 最優先のSpaceキーを考慮して睡眠状態を判定する
        DetermineSleepingState();

        // --- 診断：isSleepingとwasSleepingが変化したときだけ記録する ---
        if (_isSleeping != _diagLastIsSleeping)
        {
            Debug.Log($"[SC-DIAG] isSleeping changed: {_diagLastIsSleeping} -> {_isSleeping}  |  wasSleeping={_wasSleeping}  |  host={(_host != null ? _host.gameObject.name : "NULL")}");
            _diagLastIsSleeping = _isSleeping;
        }
        if (_wasSleeping != _diagLastWasSleeping)
        {
            Debug.Log($"[SC-DIAG] wasSleeping changed: {_diagLastWasSleeping} -> {_wasSleeping}");
            _diagLastWasSleeping = _wasSleeping;
        }

        // --- エッジ検出：状態遷移 ---
        if (_isSleeping && !_wasSleeping)
        {
            // 起床 -> 睡眠の遷移
            Debug.Log("[SleepingController] State changed: AWAKE -> SLEEPING. Sending SLEEP_LOCK.");
            if (sleepStartAudioSource != null)
                sleepStartAudioSource.Play();
            ParentUdpRuntimeHost host = GetHost();
            if (host != null)
            {
                Debug.Log($"[SC-DIAG] >>> Calling SendStateSLEEP_LOCK() on '{host.gameObject.name}'");
                host.SendStateSLEEP_LOCK();
            }
            else
            {
                Debug.LogWarning("[SC-DIAG] *** 親機Host（未稼働／未初期化）- SLEEP_LOCK NOT sent! ***");
            }
            _awakeHeartbeatTimer = 0f;
            _sleepHeartbeatTimer = 0f;
        }
        else if (!_isSleeping && _wasSleeping)
        {
            // 睡眠 -> 起床の遷移
            Debug.Log("[SleepingController] State changed: SLEEPING -> AWAKE. Sending SLEEP_UNLOCK.");
            ParentUdpRuntimeHost host = GetHost();
            if (host != null)
            {
                Debug.Log($"[SC-DIAG] >>> Calling SendStateSLEEP_UNLOCK() on '{host.gameObject.name}'");
                host.SendStateSLEEP_UNLOCK();
            }
            else
            {
                Debug.LogWarning("[SC-DIAG] *** 親機Host（未稼働／未初期化）- SLEEP_UNLOCK NOT sent! ***");
            }
            _awakeHeartbeatTimer = 0f;
            _sleepHeartbeatTimer = 0f;
        }

        // --- 安全用ハートビート：起きている間、SLEEP_UNLOCKを定期的に再送する ---
        if (!_isSleeping)
        {
            ParentUdpRuntimeHost host = GetHost();
            bool isConnected = host != null && host.IsConnected;

            if (isConnected)
            {
                _awakeHeartbeatTimer += Time.deltaTime;
                if (_awakeHeartbeatTimer >= awakeHeartbeatInterval)
                {
                    _awakeHeartbeatTimer = 0f;
                    if (showDebugLogs)
                        Debug.Log("[SleepingController] Awake heartbeat: sending SLEEP_UNLOCK.");
                    host.SendStateSLEEP_UNLOCK();
                }
            }
            else
            {
                // 切断中はタイマーをインターバル値に保持し、接続復帰時に速やかに再送できるようにする
                _awakeHeartbeatTimer = awakeHeartbeatInterval;
            }
        }
        else
        {
            // --- 安全用ハートビート：寝たふり中、SLEEP_LOCKを定期的に再送する ---
            // LOCK は状態遷移時に1回しか送っていないため、UDP で取りこぼすと子機が
            // 「親が寝ているのに操作できる」状態のままになる。子機側は既にロック済みの
            // LOCK を無視するので、再送しても副作用はない。
            ParentUdpRuntimeHost host = GetHost();
            bool isConnected = host != null && host.IsConnected;

            if (isConnected)
            {
                _sleepHeartbeatTimer += Time.deltaTime;
                if (_sleepHeartbeatTimer >= sleepHeartbeatInterval)
                {
                    _sleepHeartbeatTimer = 0f;
                    if (showDebugLogs)
                        Debug.Log("[SleepingController] Sleep heartbeat: sending SLEEP_LOCK.");
                    host.SendStateSLEEP_LOCK();
                }
            }
        }

        _wasSleeping = _isSleeping;

        if (showDebugLogs)
        {
            Debug.Log($"SleepingController: isSleeping={_isSleeping}");
        }
    }

    /// <summary>
    /// 毎フレーム現在の睡眠状態を判定する。
    ///
    /// 通常モード（ignoreSensorForDebug = false）：
    ///   isSleeping = debugInput || sensorSleeping
    ///
    /// デバッグ専用モード（ignoreSensorForDebug = true）：
    ///   isSleeping = debugInput
    ///   枕センサーを完全に無視するため、不安定なハードウェアが
    ///   Spaceキーを離したときの睡眠状態解除を妨げない。
    /// </summary>
    private void DetermineSleepingState()
    {
        bool debugInput = CheckPCDebugInput();
        bool sensorSleeping = (pillowSensor != null) && pillowSensor.isSleeping;

        // --- 診断：debugInputとsensorSleepingが変化したときだけ記録する ---
        if (debugInput != _diagLastDebugInput)
        {
            Debug.Log($"[SC-DIAG] debugInput changed: {_diagLastDebugInput} -> {debugInput}  (Keyboard.current={(Keyboard.current != null ? "OK" : "NULL")}  spaceKey.isPressed={(Keyboard.current != null ? Keyboard.current.spaceKey.isPressed.ToString() : "N/A")})");
            _diagLastDebugInput = debugInput;
        }
        if (sensorSleeping != _diagLastSensorSleeping)
        {
            Debug.Log($"[SC-DIAG] sensorSleeping changed: {_diagLastSensorSleeping} -> {sensorSleeping}  (ignoreSensorForDebug={ignoreSensorForDebug})");
            _diagLastSensorSleeping = sensorSleeping;
        }

        // Space／Gamepadの有効化エッジを記録する（毎フレームではなく押下ごとに1回）
        if (debugInput && !_wasDebugInputActive)
            Debug.Log("[SleepingController] Space/Gamepad debug override ACTIVE - forcing sleeping.");
        else if (!debugInput && _wasDebugInputActive)
            Debug.Log("[SleepingController] Space/Gamepad debug override RELEASED.");
        _wasDebugInputActive = debugInput;

        // センサーを無視している間、毎フレーム警告する（showDebugLogsが有効な場合のみ）
        if (ignoreSensorForDebug && sensorSleeping && showDebugLogs)
            Debug.Log("[SleepingController] ignoreSensorForDebug=true: sensor reports sleeping but is being ignored.");

        // 最終的な睡眠状態
        if (ignoreSensorForDebug)
            _isSleeping = debugInput;              // Space/Gamepadのみ — センサーは影響しない
        else
            _isSleeping = debugInput || sensorSleeping; // 通常：どちらの入力元でも睡眠状態になる
    }

    /// <summary>
    /// 送信に使用する親機Hostを、次の順で取得する（結果はキャッシュする）：
    ///   1. すでに取得済みの Host
    ///   2. ParentUdpRuntimeHost.Running（明示起動に成功した稼働個体を最優先）
    ///   3. FindFirstObjectByType（初期化済みHostのみ採用。未初期化Hostは使わない）
    /// </summary>
    private ParentUdpRuntimeHost GetHost()
    {
        // レベル1：すでに取得済み
        if (_host != null)
            return _host;

        // レベル2：稼働個体（Running）
        ParentUdpRuntimeHost running = ParentUdpRuntimeHost.Running;
        if (running != null)
        {
            _host = running;
            Debug.Log($"[SC-DIAG] GetHost: found via ParentUdpRuntimeHost.Running ('{_host.gameObject.name}') - caching.");
            return _host;
        }

        // レベル3：シーン検索（初期化済みのみ）
        ParentUdpRuntimeHost found = Object.FindFirstObjectByType<ParentUdpRuntimeHost>();
        if (found != null && found.IsConfigured)
        {
            _host = found;
            Debug.Log($"[SC-DIAG] GetHost: found via FindFirstObjectByType ('{_host.gameObject.name}') - caching.");
            return _host;
        }

        // どの方法でも見つからなかった
        Debug.LogWarning($"[SC-DIAG] GetHost: 親機Host（未稼働／未初期化）が見つかりません (called from '{gameObject.name}')");
        return null;
    }

    /// <summary>
    /// 睡眠状態のPCデバッグ入力を確認する。
    /// wasPressedThisFrameではなくisPressed（押下状態）を使用するため、
    /// Space／Button Southを押している間はtrueを維持する。
    /// </summary>
    private bool CheckPCDebugInput()
    {
        // Spaceキーを押している（新しい入力システム）
        if (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
            return true;

        // Gamepad Button Southを押している（XboxのA／PlayStationのCrossボタン）
        if (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed)
            return true;

        return false;
    }

    /// <summary>
    /// テスト用に睡眠状態を強制する。
    /// 注意：内部状態を設定するが、次のフレームにSpaceキーで上書きされる。
    /// </summary>
    public void ForceSleep(bool shouldSleep)
    {
        _isSleeping = shouldSleep;

        if (showDebugLogs)
            Debug.Log($"SleepingController: Force sleep set to: {shouldSleep} (will be overridden by Space key if pressed)");
    }

    /// <summary>
    /// 現在の枕センサーインスタンスを取得する（利用可能な場合）。
    /// センサー未設定時はnullを返す。
    /// </summary>
    public PillowSensor GetPillowSensor()
    {
        return pillowSensor;
    }

    /// <summary>
    /// 利用可能なら枕センサーの基準値較正をリセットする。
    /// 必要に応じてゲーム中にセンサーを再較正するために使用する。
    /// </summary>
    public void ResetPillowSensorBaseline()
    {
        if (pillowSensor != null)
        {
            pillowSensor.ResetBaseline();
            Debug.Log("SleepingController: 枕センサーの基準値をリセットしました。");
        }
        else
        {
            Debug.LogWarning("SleepingController: 枕センサーが設定されていないため、基準値をリセットできません。");
        }
    }
}
