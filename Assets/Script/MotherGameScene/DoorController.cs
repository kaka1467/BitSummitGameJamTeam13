using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// DoorController：Lerpアニメーションでドアの回転を管理する。
/// 新しい入力システムによる手動切り替え（Eキー）と、ParentDetectionV2からの外部命令に対応する。
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
    [SerializeField] private float closedAngle = 0f;   // 閉じた位置（0度）
    [SerializeField] private float peekAngle = -15f;   // 覗き見位置（テスト用に-15度）
    [SerializeField] private float openAngle = -180f;  // 完全に開いた位置（-180度）
    [SerializeField] private float openSpeed = 5f;     // 回転速度の倍率

    [Header("デバッグ")]
    public bool showDebugLogs = false;

    // 現在のドア状態
    private DoorState currentDoorState = DoorState.Closed;
    private DoorState targetDoorState = DoorState.Closed;

    /// <summary>
    /// 読み取り専用プロパティ：現在のドア状態を取得
    /// </summary>
    public DoorState CurrentDoorState => currentDoorState;

    void Start()
    {
        if (door != null)
            door.localRotation = Quaternion.Euler(0f, closedAngle, 0f);

        currentDoorState = DoorState.Closed;
        targetDoorState  = DoorState.Closed;
    }

    void Update()
    {
        // Eキーによる手動切り替え（新しい入力システム）
        if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
        {
            if (showDebugLogs)
                Debug.Log("?? Eキーを押しました：ドアを切り替えます");

            // ClosedとFullを切り替える
            if (targetDoorState == DoorState.Closed)
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

        float targetAngleY = GetTargetAngle(targetDoorState);
        Quaternion targetRotation = Quaternion.Euler(0f, targetAngleY, 0f);

        // 目標回転へLerpする
        door.localRotation = Quaternion.Lerp(door.localRotation, targetRotation, Time.deltaTime * openSpeed);

        // 回転が目標に十分近づいたら現在状態を更新する
        if (Quaternion.Angle(door.localRotation, targetRotation) < 1f)
        {
            currentDoorState = targetDoorState;
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
    /// ドアを指定した状態にする（ParentDetectionV2および手動入力から呼び出される）
    /// </summary>
    public void SetDoorState(DoorState newState)
    {
        if (targetDoorState == newState) return;

        targetDoorState = newState;

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
}
