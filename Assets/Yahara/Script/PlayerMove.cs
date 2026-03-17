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
        if (playerBoost == null)
            playerBoost = GetComponent<PlayerBoost>() ?? GetComponentInParent<PlayerBoost>();

        float speedMultiplier = (playerBoost != null) ? playerBoost.CurrentMultiplier : 1f;

        if (Keyboard.current.upArrowKey.wasPressedThisFrame)
        {
            lane--;
            if (lane < 0) lane = 0;
        }

        if (Keyboard.current.downArrowKey.wasPressedThisFrame)
        {
            lane++;
            if (lane > 2) lane = 2;
        }

        Vector3 pos = transform.position;
        float horizontalInput = 0f;

        if (Keyboard.current.leftArrowKey.isPressed)
        {
            horizontalInput -= 1f;
        }

        if (Keyboard.current.rightArrowKey.isPressed)
        {
            horizontalInput += 1f;
        }

        pos.x += horizontalInput * horizontalMoveSpeed * speedMultiplier * Time.deltaTime;
        pos.x = Mathf.Clamp(pos.x, minX, maxX);
        pos.y = Mathf.Lerp(pos.y, laneY[lane], Time.deltaTime * moveSpeed * speedMultiplier);
        transform.position = pos;
    }
}
