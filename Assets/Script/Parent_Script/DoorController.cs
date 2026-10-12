using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// DoorController：Lerpアニメーションでドアの回転を管理する。
/// 新しい入力システムによる手動切り替え（Pキー）と、MotherSuspicionSystemからの外部命令に対応する。
/// </summary>
public class DoorController : MonoBehaviour
{
    /// <summary>
    /// ドア状態の列挙
    /// </summary>
    public enum DoorState
    {
        Closed,  // ドアが完全に閉じた状態（0度）
        Peek,    // 覗き見用に少し開いた状態（-15～-30度）
        Full     // ドアが完全に開いた状態（-180度）
    }

    [Header("ドア設定")]
    [SerializeField] private Transform door;           // 回転させるドアのTransform

    [Header("回転設定")]
    [SerializeField] private float closedAngle;   // 閉じた位置（0度）
    [SerializeField] private float peekAngle = -15f;   // 覗き見位置（テスト用に-15度）
    [SerializeField] private float openAngle = -180f;  // 完全に開いた位置（-180度）
    [SerializeField] private float openSpeed = 5f;     // 回転速度の倍率

    [Header("片付け演出用")]
    // 片付けの全開で使う回転速度は MotherChoreController 側の設定（choreDoorOpenSpeed）から
    // BeginChoreFullOpen() で受け取る（旧 choreFullOpenSpeed は撤去。通常の openSpeed は変更しない）。

    /// <summary>片付けの全開待ちの間だけ使う回転速度の上書き（0以下で無効＝通常の openSpeed）。</summary>
    private float _speedOverride = -1f;

    // ── 時間指定の移動（ゲームオーバー演出用）───────────────────────────────
    //  ・「秒」で全開までの時間を指定して開ける方式。値が大きいほどゆっくり開く。
    //  ・通常の覗き／片付けは既存の速度方式（openSpeed / _speedOverride）のまま。
    //  ・ドア角度の書き込み元はこのクラスのみ（UpdateDoorRotation が両方式を排他で扱う）。
    private bool _timedMoveActive;
    private float _timedElapsed;
    private float _timedDuration = 1f;
    private Quaternion _timedFromRotation = Quaternion.identity;
    private AnimationCurve _timedCurve;

    /// <summary>時間指定の移動が進行中か（読み取り専用）。</summary>
    public bool IsTimedMoveActive => _timedMoveActive;

    /// <summary>
    /// 【ゲームオーバー演出用】目標状態へ「指定秒」で動かす（値が大きいほどゆっくり開く）。
    ///  ・すでに途中まで開いていても、現在角度から目標角度まで動かす。
    ///  ・curve が null / キー0件なら線形で動かす（空カーブは Evaluate が0を返すため）。
    ///  ・既存の速度方式（BeginChoreFullOpen / SetDoorState）は変更しない。
    ///  ・呼び出し元は MotherGameOverSequenceController だけで、同時に複数のコルーチンから呼ばない。
    /// </summary>
    public void BeginTimedMove(DoorState targetState, float durationSeconds, AnimationCurve curve)
    {
        if (door == null)
        {
            Debug.LogWarning("[DoorController] door が未設定のため時間指定の移動を開始できません", this);
            return;
        }

        _timedFromRotation = door.localRotation;
        _targetDoorState = targetState;      // SetDoorState の同一判定を通さず、途中角度からでも開始する
        _timedDuration = Mathf.Max(0.01f, durationSeconds);
        _timedElapsed = 0f;
        _timedCurve = curve;
        _timedMoveActive = true;
        _speedOverride = -1f;                // 時間方式では速度上書きを使わない

        if (showDebugLogs)
            Debug.Log($"[DoorController] 時間指定の移動を開始: {targetState} / 時間={_timedDuration:F2}s");
    }

    [Header("デバッグ")]
    public bool showDebugLogs;

    // 現在のドア状態
    private DoorState _currentDoorState = DoorState.Closed;
    private DoorState _targetDoorState = DoorState.Closed;

    /// <summary>
    /// 読み取り専用プロパティ：現在のドア状態を取得
    /// </summary>
    public DoorState CurrentDoorState => _currentDoorState;

    /// <summary>
    /// 手動入力（Pキー）を受け付けるか。
    ///  ・ゲームオーバー演出中は「入力だけ」を止めたい。enabled を false にすると Update ごと止まり
    ///    ドア本体の回転（演出のドア開け）まで止まってしまうため、入力停止と回転制御を分ける。
    ///  ・既定は true（従来どおり入力可能）。捕獲演出の開始時に false にする。
    /// </summary>
    public bool ManualInputEnabled { get; set; } = true;

    private void Start()
    {
        if (door != null)
            door.localRotation = Quaternion.Euler(0f, closedAngle, 0f);

        _currentDoorState = DoorState.Closed;
        _targetDoorState = DoorState.Closed;
        _speedOverride = -1f;
        // シーン開始時は手動入力を受け付ける（再プレイで停止状態を持ち越さない）。
        ManualInputEnabled = true;
    }

    private void OnDisable()
    {
        // 無効化（シーン変更・ゲームオーバー等）で速度上書き・時間指定移動を残さない。
        _speedOverride = -1f;
        _timedMoveActive = false;
    }

    /// <summary>
    /// 片付けの速度上書きを解除する（中断・失敗・無効化で、通常の覗き・閉め速度に影響を残さない）。
    /// 呼び出し元：片付けの中断・終了時（MotherChoreController）。
    /// </summary>
    public void ClearChoreSpeedOverride()
    {
        _speedOverride = -1f;
    }

    /// <summary>
    /// 片付けの全開を「開始」する（回転を開始させるだけ。完了待ちは WaitForDoorReached）。
    /// 速度の上書きは回転を開始させる SetDoorState の前に適用する（回転開始時から効かせる）。
    /// </summary>
    public void BeginChoreFullOpen(float speedOverride)
    {
        if (speedOverride > 0f) _speedOverride = speedOverride;
        SetDoorState(DoorState.Full);

        // 既に Full へ到達済み（＝回転が始まらない）場合は、速度上書きが使われない旨を残す。
        if (IsDoorRotationReached() && showDebugLogs)
            Debug.Log("[DoorController] ドアは既に Full へ到達済み（回転開始なし。速度上書きは未使用）");
    }

    /// <summary>
    /// 現在の目標状態へ実際に到達するまで待つ（状態・速度上書きは変更しない）。
    /// BeginChoreFullOpen の後、ドア全開の完了待ちに使う。
    ///  ・到達できたら onResult(true)、タイムアウトなら onResult(false)（呼び出し側が正常終了と区別する）。
    /// </summary>
    public IEnumerator WaitForDoorReached(float timeoutSeconds, System.Action<bool> onResult = null)
    {
        float elapsed = 0f;
        float timeout = Mathf.Max(0f, timeoutSeconds);

        while (!IsDoorRotationReached())
        {
            if (timeout > 0f && elapsed >= timeout)
            {
                Debug.LogWarning($"[DoorController] ドアが目標角度へ到達しませんでした（{timeout:F1}s で打ち切り）");
                onResult?.Invoke(false);
                yield break;
            }
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (showDebugLogs)
            Debug.Log($"[DoorController] ドアが目標角度へ到達（{elapsed:F2}s）");
        onResult?.Invoke(true);
    }

    private void Update()
    {
        // Pキーによる手動切り替え（新しい入力システム）。ドアのClosed↔Full切替のみで、怪しさへの加算は行わない。
        // 捕獲演出中（ManualInputEnabled=false）は入力だけを止める。回転は下の UpdateDoorRotation が継続する。
        if (ManualInputEnabled && Keyboard.current != null && Keyboard.current.pKey.wasPressedThisFrame)
        {
            if (showDebugLogs)
                Debug.Log("?? Pキーを押しました：ドアを切り替えます");

            // ClosedとFullを切り替える
            if (_targetDoorState == DoorState.Closed)
            {
                SetDoorState(DoorState.Full);
            }
            else
            {
                SetDoorState(DoorState.Closed);
            }
        }

        // 目標角度に向けてドアを滑らかに回転させる
        UpdateDoorRotation();
    }

    /// <summary>
    /// Lerpを使用して目標角度に向けてドアを回転させる
    /// </summary>
    private void UpdateDoorRotation()
    {
        if (door == null) return;

        float targetAngleY = GetTargetAngle(_targetDoorState);
        Quaternion targetRotation = Quaternion.Euler(0f, targetAngleY, 0f);

        // ── 時間指定の移動（ゲームオーバー演出用）────────────────────────────
        //   開始角度から目標角度まで「指定秒」で動かす（値が大きいほどゆっくり）。
        if (_timedMoveActive)
        {
            _timedElapsed += Time.deltaTime;
            float k = Mathf.Clamp01(_timedElapsed / _timedDuration);
            // キー0件の空カーブは Evaluate が0を返すため、線形として扱う。
            float e = (_timedCurve != null && _timedCurve.length > 0) ? Mathf.Clamp01(_timedCurve.Evaluate(k)) : k;

            door.localRotation = Quaternion.Slerp(_timedFromRotation, targetRotation, e);

            if (k >= 1f)
            {
                door.localRotation = targetRotation;   // 到達時に誤差を残さない
                _currentDoorState = _targetDoorState;
                _timedMoveActive = false;
            }
            return;
        }

        // ── 既存の速度方式（通常の覗き・片付け）──────────────────────────────
        // 片付けの全開待ち中は上書き速度を使う（通常の覗き・閉め速度は変更しない）。
        float speed = (_speedOverride > 0f) ? _speedOverride : openSpeed;

        // 目標回転へLerpする
        door.localRotation = Quaternion.Lerp(door.localRotation, targetRotation, Time.deltaTime * speed);

        // 回転が目標に十分近づいたら現在状態を更新する
        if (Quaternion.Angle(door.localRotation, targetRotation) < 1f)
        {
            _currentDoorState = _targetDoorState;
        }
    }

    /// <summary>
    /// 指定したドア状態の目標Y回転角を取得する
    /// </summary>
    private float GetTargetAngle(DoorState state)
    {
        return state switch
        {
            DoorState.Closed => closedAngle,
            DoorState.Peek => peekAngle,
            DoorState.Full => openAngle,
            _ => closedAngle
        };
    }

    /// <summary>
    /// ドアを指定した状態にする（MotherSuspicionSystemおよび手動入力から呼び出される）
    /// </summary>
    public void SetDoorState(DoorState newState)
    {
        if (_targetDoorState == newState) return;

        _targetDoorState = newState;

        if (showDebugLogs)
            Debug.Log($"?? ドア状態を変更しました：{newState}");
    }

    /// <summary>
    /// 後方互換用メソッド：boolをDoorStateに変換する
    /// trueの場合は完全に開き、falseの場合は閉じる。
    /// </summary>
    public void SetDoorOpen(bool isOpen)
    {
        DoorState newState = isOpen ? DoorState.Full : DoorState.Closed;
        SetDoorState(newState);

        if (showDebugLogs)
            Debug.Log($"?? SetDoorOpen({isOpen}) -> {newState}");
    }

    /// <summary>
    /// ドアの現在の回転角（Y軸の度数）を取得する
    /// </summary>
    public float GetCurrentDoorAngle()
    {
        if (door == null) return 0f;
        return door.localEulerAngles.y;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  片付け演出用：角度指定の開閉と「到達待ち」
    //   ・モデルの Door_Open 再生と連携し、開け終わる前に通り抜けないようにするため、
    //     目標角度へ到達するまで待てる IEnumerator を用意する。
    //   ・既存の SetDoorState / UpdateDoorRotation（Lerp）をそのまま使う。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>openAngle（完全に開いた位置）へ向かう目標角度。片付けの「fullopen」に対応する。</summary>
    public float FullOpenAngle => openAngle;

    /// <summary>peekAngle（覗き用に少し開いた位置）へ向かう目標角度。</summary>
    public float PeekAngle => peekAngle;

    /// <summary>closedAngle（閉じた位置）へ向かう目標角度。</summary>
    public float ClosedAngle => closedAngle;

    /// <summary>
    /// 現在のドア回転が「目標角度」へ十分近づいているか（到達判定のしきい値は1度）。
    /// UpdateDoorRotation と同じしきい値を使い、判定が食い違わないようにする。
    /// </summary>
    public bool IsDoorRotationReached()
    {
        if (door == null) return true;

        float targetAngleY = GetTargetAngle(_targetDoorState);
        Quaternion targetRotation = Quaternion.Euler(0f, targetAngleY, 0f);
        return Quaternion.Angle(door.localRotation, targetRotation) < 1f;
    }

    /// <summary>
    /// 指定したドア状態へ動かし、実際にその角度へ到達するまで待つ（通常速度）。
    /// タイムアウト付きで、到達しない場合も永久に待たない（呼び出し側が警告を出す）。
    /// </summary>
    public IEnumerator WaitForDoorState(DoorState newState, float timeoutSeconds)
        => WaitForDoorState(newState, timeoutSeconds, -1f);

    /// <summary>
    /// 指定したドア状態へ動かし、到達するまで待つ（速度上書き付き）。
    ///  ・speedOverride が0より大きい場合だけ、待機中はその速度を使う（通常の覗き・閉め速度は変えない）。
    ///  ・完了後に速度の上書きは必ず元へ戻す。
    /// 片付け演出（Door_Open とドア回転の連携）で使用する。
    /// </summary>
    public IEnumerator WaitForDoorState(DoorState newState, float timeoutSeconds, float speedOverride)
    {
        // 速度の上書きは「回転を開始させる SetDoorState の前」に適用する（回転の最初のフレームから効かせる）。
        float previousOverride = _speedOverride;
        if (speedOverride > 0f) _speedOverride = speedOverride;

        SetDoorState(newState);

        // 既に目標角度へ到達済み（＝回転が始まらない）なら、速度上書きは使われない。その旨を残す。
        if (IsDoorRotationReached())
        {
            if (showDebugLogs)
                Debug.Log($"[DoorController] ドアは既に {newState} へ到達済み — 回転開始なし（速度上書きは未使用）");
            _speedOverride = previousOverride;
            yield break;
        }

        float elapsed = 0f;
        float timeout = Mathf.Max(0f, timeoutSeconds);
        bool reached = true;

        while (!IsDoorRotationReached())
        {
            if (timeout > 0f && elapsed >= timeout)
            {
                Debug.LogWarning($"[DoorController] ドアが {newState} へ到達しませんでした（{timeout:F1}s で打ち切り）");
                reached = false;
                break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        // 速度の上書きを必ず元へ戻す（通常の覗き・閉めへ影響を残さない）。
        _speedOverride = previousOverride;

        if (reached && showDebugLogs)
            Debug.Log($"[DoorController] ドアが {newState} へ到達（{elapsed:F2}s）");
    }
}