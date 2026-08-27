using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// SleepingController：PCデバッグ上書きを含むプレイヤーの睡眠状態を監視する。
/// PCデバッグ入力（Spaceキー）は最優先で、ハードウェアセンサーを上書きする。
/// 補助入力としてハードウェア枕センサー（シリアル／ESP32経由のPillowSensor）に対応する。
/// 新しい入力システムを統合し、PCでシームレスにテストできる。
/// 状態遷移時にParentUdpSender経由でSLEEP_LOCK／SLEEP_UNLOCKを自動送信し、
/// 起きている間は誤ロック状態を回復するため定期的にSLEEP_UNLOCKを送信する。
/// </summary>
public class SleepingController : MonoBehaviour
{
    [Header("ハードウェアセンサー")]
    [SerializeField] private PillowSensor pillowSensor;

    [Header("UDP")]
    [Tooltip("未設定の場合はStart時に自動検索。睡眠状態の変化時にSLEEP_LOCK／SLEEP_UNLOCKを送信します。")]
    [SerializeField] private ParentUdpSender udpSender;

    [Header("安全用ハートビート")]
    [Tooltip("親機が起きている間にSLEEP_UNLOCKを再送する間隔（秒）。")]
    [SerializeField] private float awakeHeartbeatInterval = 1.0f;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs = false;
    [Tooltip("trueの場合、枕センサーを無視し、Space／GamepadのみでisSleepingを制御します。デバッグ時にセンサーが不安定な場合に便利です。")]
    [SerializeField] private bool ignoreSensorForDebug = false;

    // プレイヤーの睡眠状態
    private bool isSleeping = false;
    private bool wasSleeping = false;

    // 安全用ハートビートタイマー
    private float _awakeHeartbeatTimer = 0f;

    // 毎フレーム大量に出力せず、Spaceの有効化を記録するための前回デバッグ入力状態
    private bool _wasDebugInputActive = false;

    // 診断用トラッカー：変化時のみ出力するため、最後に記録した値を保持する
    private bool _diagLastDebugInput = false;
    private bool _diagLastSensorSleeping = false;
    private bool _diagLastIsSleeping = false;
    private bool _diagLastWasSleeping = false;

    /// <summary>
    /// 公開読み取り専用プロパティ：プレイヤーが睡眠中か（ParentDetectionV2とCaughtReactionControllerが使用）
    /// </summary>
    public bool IsSleeping => isSleeping;

    void Start()
    {
        if (udpSender == null)
            udpSender = Object.FindFirstObjectByType<ParentUdpSender>();
        isSleeping = false;
        wasSleeping = false;
        _awakeHeartbeatTimer = 0f;
        _wasDebugInputActive = false;
        _diagLastDebugInput = false;
        _diagLastSensorSleeping = false;
        _diagLastIsSleeping = false;
        _diagLastWasSleeping = false;

        // 起動時にudpSenderの状態を記録する。nullのままならGetUdpSender()が実行時に再試行する。
        if (udpSender != null)
            Debug.Log($"[SC-DIAG] udpSenderはインスペクターで設定済み: '{udpSender.gameObject.name}'");
        else
            Debug.LogWarning("[SC-DIAG] udpSenderがインスペクター未設定 - GetUdpSender()で実行時に自動検索します。");

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

    void Update()
    {
        // 最優先のSpaceキーを考慮して睡眠状態を判定する
        DetermineSleepingState();

        // --- 診断：isSleepingとwasSleepingが変化したときだけ記録する ---
        if (isSleeping != _diagLastIsSleeping)
        {
            Debug.Log($"[SC-DIAG] isSleeping changed: {_diagLastIsSleeping} -> {isSleeping}  |  wasSleeping={wasSleeping}  |  udpSender={(udpSender != null ? udpSender.gameObject.name : "NULL")}");
            _diagLastIsSleeping = isSleeping;
        }
        if (wasSleeping != _diagLastWasSleeping)
        {
            Debug.Log($"[SC-DIAG] wasSleeping changed: {_diagLastWasSleeping} -> {wasSleeping}");
            _diagLastWasSleeping = wasSleeping;
        }

        // --- エッジ検出：状態遷移 ---
        if (isSleeping && !wasSleeping)
        {
            // 起床 -> 睡眠の遷移
            Debug.Log("[SleepingController] State changed: AWAKE -> SLEEPING. Sending SLEEP_LOCK.");
            ParentUdpSender sender = GetUdpSender();
            if (sender != null)
            {
                Debug.Log($"[SC-DIAG] >>> Calling SendStateSLEEP_LOCK() on '{sender.gameObject.name}'");
                sender.SendStateSLEEP_LOCK();
            }
            else
            {
                Debug.LogWarning("[SC-DIAG] *** ParentUdpSender not found - SLEEP_LOCK NOT sent! Is ParentUdpSender in the scene and enabled? ***");
            }
            _awakeHeartbeatTimer = 0f;
        }
        else if (!isSleeping && wasSleeping)
        {
            // 睡眠 -> 起床の遷移
            Debug.Log("[SleepingController] State changed: SLEEPING -> AWAKE. Sending SLEEP_UNLOCK.");
            ParentUdpSender sender = GetUdpSender();
            if (sender != null)
            {
                Debug.Log($"[SC-DIAG] >>> Calling SendStateSLEEP_UNLOCK() on '{sender.gameObject.name}'");
                sender.SendStateSLEEP_UNLOCK();
            }
            else
            {
                Debug.LogWarning("[SC-DIAG] *** ParentUdpSender not found - SLEEP_UNLOCK NOT sent! Is ParentUdpSender in the scene and enabled? ***");
            }
            _awakeHeartbeatTimer = 0f;
        }

        // --- 安全用ハートビート：起きている間、SLEEP_UNLOCKを定期的に再送する ---
        if (!isSleeping)
        {
            _awakeHeartbeatTimer += Time.deltaTime;
            if (_awakeHeartbeatTimer >= awakeHeartbeatInterval)
            {
                _awakeHeartbeatTimer = 0f;
                ParentUdpSender sender = GetUdpSender();
                if (sender != null)
                {
                    if (showDebugLogs)
                        Debug.Log("[SleepingController] Awake heartbeat: sending SLEEP_UNLOCK.");
                    sender.SendStateSLEEP_UNLOCK();
                }
                else
                {
                    Debug.LogWarning("[SleepingController] Awake heartbeat: ParentUdpSender not found - SLEEP_UNLOCK skipped.");
                }
            }
        }

        wasSleeping = isSleeping;

        if (showDebugLogs)
        {
            Debug.Log($"SleepingController: isSleeping={isSleeping}");
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
            isSleeping = debugInput;              // Space/Gamepadのみ — センサーは影響しない
        else
            isSleeping = debugInput || sensorSleeping; // 通常：どちらの入力元でも睡眠状態になる
    }

    /// <summary>
    /// 送信に使用するParentUdpSenderを、3段階のフォールバックで取得する：
    ///   1. インスペクター設定済みのudpSenderフィールド（最速、推奨）
    ///   2. ParentUdpSender.instance（ParentUdpSender自身が設定する静的シングルトン）
    ///   3. FindFirstObjectByType（シーン検索、最も遅いので最後の手段のみ）
    /// フォールバックで見つけた結果は、次回のためudpSenderにキャッシュする。
    /// </summary>
    private ParentUdpSender GetUdpSender()
    {
        // レベル1：すでにキャッシュ済み
        if (udpSender != null)
            return udpSender;

        // レベル2：静的シングルトン
        if (ParentUdpSender.instance != null)
        {
            udpSender = ParentUdpSender.instance;
            Debug.Log($"[SC-DIAG] GetUdpSender: found via ParentUdpSender.instance ('{udpSender.gameObject.name}') - caching.");
            return udpSender;
        }

        // レベル3：シーン検索
        ParentUdpSender found = Object.FindFirstObjectByType<ParentUdpSender>();
        if (found != null)
        {
            udpSender = found;
            Debug.Log($"[SC-DIAG] GetUdpSender: found via FindFirstObjectByType ('{udpSender.gameObject.name}') - caching.");
            return udpSender;
        }

        // どの方法でも見つからなかった
        Debug.LogWarning($"[SC-DIAG] GetUdpSender: ParentUdpSender NOT found in scene! (called from '{gameObject.name}')");
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
        isSleeping = shouldSleep;

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
