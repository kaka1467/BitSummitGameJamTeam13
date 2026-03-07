using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMove : MonoBehaviour
{
    int lane = 1;

    float[] laneY = { 4.52f, 1.06f, -2.46f };

    public float moveSpeed = 10f;

    void Update()
    {
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
        pos.y = Mathf.Lerp(pos.y, laneY[lane], Time.deltaTime * moveSpeed);
        transform.position = pos;
    }
}
