using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMove : MonoBehaviour
{
    int lane = 1;

    float[] laneY = { 0.16f, -0.03f, -0.22f };

    public float moveSpeed = 5f;
    public float horizontalMoveSpeed = 5f;

    [SerializeField]
    private float horizontalPadding = 0.2f;

    private PlayerBoost playerBoost;
    private Camera mainCam;
    private Animator anim;

    private void Awake()
    {
        mainCam = Camera.main;
        anim = GetComponent<Animator>();

        // アニメーションクリップがPositionを書き換えないようにする
        if (anim != null)
        {
            anim.applyRootMotion = false;
        }
    }

    void Update()
    {
        var gamepad = Gamepad.current;
        var keyboard = Keyboard.current;

        if (playerBoost == null)
            playerBoost = GetComponent<PlayerBoost>() ?? GetComponentInParent<PlayerBoost>();

        float speedMultiplier = (playerBoost != null) ? playerBoost.CurrentMultiplier : 1f;

        bool upPressed = (keyboard != null && keyboard.upArrowKey.wasPressedThisFrame) ||
                         (gamepad != null && (gamepad.dpad.up.wasPressedThisFrame || gamepad.leftStick.up.wasPressedThisFrame));

        bool downPressed = (keyboard != null && keyboard.downArrowKey.wasPressedThisFrame) ||
                           (gamepad != null && (gamepad.dpad.down.wasPressedThisFrame || gamepad.leftStick.down.wasPressedThisFrame));

        if (upPressed) lane = Mathf.Max(0, lane - 1);
        if (downPressed) lane = Mathf.Min(2, lane + 1);

        float horizontalInput = 0f;
        if (keyboard != null)
        {
            if (keyboard.leftArrowKey.isPressed) horizontalInput -= 1f;
            if (keyboard.rightArrowKey.isPressed) horizontalInput += 1f;
        }

        if (gamepad != null)
        {
            horizontalInput += gamepad.leftStick.x.ReadValue();
            if (gamepad.dpad.left.isPressed) horizontalInput -= 1f;
            if (gamepad.dpad.right.isPressed) horizontalInput += 1f;
        }

        horizontalInput = Mathf.Clamp(horizontalInput, -1f, 1f);

        // アニメーションがPositionを書き換えた後にスクリプトで上書きする
        Vector3 pos = transform.position;
        pos.x += horizontalInput * horizontalMoveSpeed * speedMultiplier * Time.deltaTime;

        if (mainCam != null)
        {
            float halfHeight = mainCam.orthographicSize;
            float halfWidth = halfHeight * mainCam.aspect;

            float minX = mainCam.transform.position.x - halfWidth + horizontalPadding;
            float maxX = mainCam.transform.position.x + halfWidth - horizontalPadding;
            pos.x = Mathf.Clamp(pos.x, minX, maxX);
        }

        pos.y = Mathf.Lerp(pos.y, laneY[lane], Time.deltaTime * moveSpeed * speedMultiplier);
        pos.z = 621.66f; // Z座標を固定（元のZ値に合わせる）
        transform.position = pos;
    }
}