using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMove : MonoBehaviour
{
    int lane = 1;

    float[] laneY = { 0.51f, 0f, -0.55f };

    public float moveSpeed = 5f;
    public float horizontalMoveSpeed = 5f;

    [SerializeField]
    private float horizontalPadding = 0.2f; // 画面端からの余白（ワールド単位）

    private PlayerBoost playerBoost;
    private Camera mainCam;

    private void Awake()
    {
        mainCam = Camera.main;
    }

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

        // カメラの画角から表示可能なX範囲を計算してClamp
        if (mainCam != null)
        {
            float halfHeight = mainCam.orthographicSize;
            float halfWidth = halfHeight * mainCam.aspect;

            float minX = mainCam.transform.position.x - halfWidth + horizontalPadding;
            float maxX = mainCam.transform.position.x + halfWidth - horizontalPadding;
            pos.x = Mathf.Clamp(pos.x, minX, maxX);
        }

        pos.y = Mathf.Lerp(pos.y, laneY[lane], Time.deltaTime * moveSpeed * speedMultiplier);
        transform.position = pos;
    }
}
