using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMove : MonoBehaviour
{
    int lane = 1;

    float[] laneY = { 4.52f, 1.06f, -2.46f };

    public float moveSpeed = 10f;
    public float horizontalMoveSpeed = 5f;
    public float minX = -8f;
    public float maxX = 8f;

    private PlayerBoost playerBoost;

    void Update()
    {
        // ゲームパッドとキーボードの両方を取得
        var gamepad = Gamepad.current;
        var keyboard = Keyboard.current;

        if (playerBoost == null)
            playerBoost = GetComponent<PlayerBoost>() ?? GetComponentInParent<PlayerBoost>();

        float speedMultiplier = (playerBoost != null) ? playerBoost.CurrentMultiplier : 1f;

        // --- レーン移動 (上下) ---
        // キーボードの↑ または ゲームパッドの十字キー上/左スティック上が押された瞬間
        bool upPressed = (keyboard != null && keyboard.upArrowKey.wasPressedThisFrame) ||
                         (gamepad != null && (gamepad.dpad.up.wasPressedThisFrame || gamepad.leftStick.up.wasPressedThisFrame));

        // キーボードの↓ または ゲームパッドの十字キー下/左スティック下が押された瞬間
        bool downPressed = (keyboard != null && keyboard.downArrowKey.wasPressedThisFrame) ||
                           (gamepad != null && (gamepad.dpad.down.wasPressedThisFrame || gamepad.leftStick.down.wasPressedThisFrame));

        if (upPressed)
        {
            lane = Mathf.Max(0, lane - 1);
        }

        if (downPressed)
        {
            lane = Mathf.Min(2, lane + 1);
        }

        // --- 左右移動 ---
        float horizontalInput = 0f;
        if (keyboard != null)
        {
            if (keyboard.leftArrowKey.isPressed) horizontalInput -= 1f;
            if (keyboard.rightArrowKey.isPressed) horizontalInput += 1f;
        }

        if (gamepad != null)
        {
            // スティックの傾き、または十字キーの入力を加算
            horizontalInput += gamepad.leftStick.x.ReadValue();
            if (gamepad.dpad.left.isPressed) horizontalInput -= 1f;
            if (gamepad.dpad.right.isPressed) horizontalInput += 1f;
        }

        horizontalInput = Mathf.Clamp(horizontalInput, -1f, 1f);

        Vector3 pos = transform.position;
        pos.x += horizontalInput * horizontalMoveSpeed * speedMultiplier * Time.deltaTime;
        pos.x = Mathf.Clamp(pos.x, minX, maxX);
        pos.y = Mathf.Lerp(pos.y, laneY[lane], Time.deltaTime * moveSpeed * speedMultiplier);
        transform.position = pos;
    }
}
