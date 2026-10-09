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

    [Header("デバッグ")]
    public bool showDebugLogs;

    // 現在のドア状態
    private DoorState _currentDoorState = DoorState.Closed;
    private DoorState _targetDoorState = DoorState.Closed;

    /// <summary>
    /// 読み取り専用プロパティ：現在のドア状態を取得
    /// </summary>
    public DoorState CurrentDoorState => _currentDoorState;

    private void Start()
    {
        if (door != null)
            door.localRotation = Quaternion.Euler(0f, closedAngle, 0f);

        _currentDoorState = DoorState.Closed;
        _targetDoorState = DoorState.Closed;
        _speedOverride = -1f;
    }

    private void OnDisable()
    {
        // 無効化（シーン変更・ゲームオーバー等）で速度上書きを残さない。
        _speedOverride = -1f;
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
        if (Keyboard.current != null && Keyboard.current.pKey.wasPressedThisFrame)
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