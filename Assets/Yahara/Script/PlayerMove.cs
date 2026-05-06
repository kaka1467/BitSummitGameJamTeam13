using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMove : MonoBehaviour
{
    int lane = 1;

    [SerializeField]
    [Tooltip("各レーンのY座標 [上レーン, 中レーン, 下レーン]")]
    private float[] laneY = { 0.41f, -0.08f, -0.56f };

    public float moveSpeed = 5f;
    [Tooltip("X方向の移動速度")]
    public float horizontalMoveSpeed = 5f;

    [SerializeField]
    [Tooltip("画面端に近づき過ぎないための余白")]
    private float horizontalPadding = 0.2f;

    [Header("Control")]
    [SerializeField] private bool inputEnabled = true;
    [SerializeField] private bool autoDriveEnabled = false;
    [SerializeField] private float autoHorizontalSpeed = 5f;

    private int autoLane = 1;
    private bool autoUseTargetX = false;
    private float autoTargetX = 0f;

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

        float horizontalInput = 0f;

        if (inputEnabled)
        {
            bool upPressed = (keyboard != null && keyboard.upArrowKey.wasPressedThisFrame) ||
                             (gamepad != null && (gamepad.dpad.up.wasPressedThisFrame || gamepad.leftStick.up.wasPressedThisFrame));

            bool downPressed = (keyboard != null && keyboard.downArrowKey.wasPressedThisFrame) ||
                               (gamepad != null && (gamepad.dpad.down.wasPressedThisFrame || gamepad.leftStick.down.wasPressedThisFrame));

            if (upPressed) lane = Mathf.Max(0, lane - 1);
            if (downPressed) lane = Mathf.Min(2, lane + 1);

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
        }
        else if (autoDriveEnabled)
        {
            lane = Mathf.Clamp(autoLane, 0, laneY.Length - 1);
        }

        // アニメーションがPositionを書き換えた後にスクリプトで上書きする
        Vector3 pos = transform.position;
        if (inputEnabled)
        {
            pos.x += horizontalInput * horizontalMoveSpeed * speedMultiplier * Time.deltaTime;
        }
        else if (autoDriveEnabled && autoUseTargetX)
        {
            float target = autoTargetX;
            float step = autoHorizontalSpeed * speedMultiplier * Time.deltaTime;
            pos.x = Mathf.MoveTowards(pos.x, target, step);
        }

        if (mainCam != null)
        {
            float halfHeight = mainCam.orthographicSize;
            float halfWidth = halfHeight * mainCam.aspect;

            float minX = mainCam.transform.position.x - halfWidth + horizontalPadding;
            float maxX = mainCam.transform.position.x + halfWidth - horizontalPadding;
            pos.x = Mathf.Clamp(pos.x, minX, maxX);
        }

        pos.y = Mathf.Lerp(pos.y, laneY[lane], Time.deltaTime * moveSpeed * speedMultiplier);
        pos.z = 609.47f; 
        transform.position = pos;
    }

    public void SetInputEnabled(bool enabled)
    {
        inputEnabled = enabled;
    }

    public void SetAutoDrive(bool enabled)
    {
        autoDriveEnabled = enabled;
    }

    public void SetAutoLane(int laneIndex)
    {
        autoLane = laneIndex;
    }

    public void SetAutoTargetX(float? targetX)
    {
        if (targetX.HasValue)
        {
            autoTargetX = targetX.Value;
            autoUseTargetX = true;
        }
        else
        {
            autoUseTargetX = false;
        }
    }
}