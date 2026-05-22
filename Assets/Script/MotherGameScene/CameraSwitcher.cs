using UnityEngine;
using UnityEngine.InputSystem;

public class CameraSwitcher : MonoBehaviour
{
    [SerializeField] private Camera[] cameras;
    [SerializeField] private Key switchKey = Key.K;

    // 初期値として Numpad1 から Numpad9 までを自動設定
    [SerializeField]
    private Key[] extraSwitchKeys = new[]
    {
        Key.Numpad1, Key.Numpad2, Key.Numpad3,
        Key.Numpad4, Key.Numpad5, Key.Numpad6,
        Key.Numpad7, Key.Numpad8, Key.Numpad9
    };

    [Header("Door")]
    [SerializeField] private Transform door;
    [SerializeField] private float closedYAngle = 0f;
    [SerializeField] private float openYAngle = 90f;
    [SerializeField] private float doorRotateSpeed = 8f;

    private int activeCameraIndex;
    private bool isDoorOpen;
    private Quaternion targetDoorRotation;

    /// <summary>True while the player is in the K-key peek view (door open state).</summary>
    public bool IsPeeking => isDoorOpen;

    private void Start()
    {
        if (cameras == null || cameras.Length == 0)
        {
            Debug.LogWarning("CameraSwitcher: cameras is empty. Assign cameras in the Inspector.");
            return;
        }

        activeCameraIndex = Mathf.Clamp(activeCameraIndex, 0, cameras.Length - 1);
        ApplyCameraState(activeCameraIndex);

        isDoorOpen = false;
        targetDoorRotation = Quaternion.Euler(0f, closedYAngle, 0f);
        ApplyDoorStateImmediate();
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }

        if (IsSwitchKeyPressed(keyboard))
        {
            Debug.Log($"[CameraSwitcher] Switch key detected | cameras={(cameras != null ? cameras.Length : 0)} | isDoorOpen before={isDoorOpen}");
            SwitchToNextCamera();
        }

        UpdateDoorRotation();
    }

    private void SwitchToNextCamera()
    {
        // Always toggle the door/peek state regardless of camera assignment.
        ToggleDoor();

        if (cameras == null || cameras.Length == 0)
        {
            Debug.Log("[CameraSwitcher] No cameras assigned — door toggled only");
            return;
        }

        activeCameraIndex = (activeCameraIndex + 1) % cameras.Length;
        ApplyCameraState(activeCameraIndex);
    }

    private bool IsSwitchKeyPressed(Keyboard keyboard)
    {
        if (keyboard[switchKey].wasPressedThisFrame)
        {
            return true;
        }

        if (extraSwitchKeys == null)
        {
            return false;
        }

        for (int i = 0; i < extraSwitchKeys.Length; i++)
        {
            if (keyboard[extraSwitchKeys[i]].wasPressedThisFrame)
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyCameraState(int activeIndex)
    {
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera cam = cameras[i];
            if (cam == null)
            {
                continue;
            }

            bool isActive = i == activeIndex;
            cam.enabled = isActive;

            AudioListener listener = cam.GetComponent<AudioListener>();
            if (listener != null)
            {
                listener.enabled = isActive;
            }
        }
    }

    private void ToggleDoor()
    {
        isDoorOpen = !isDoorOpen;
        float targetY = isDoorOpen ? openYAngle : closedYAngle;
        targetDoorRotation = Quaternion.Euler(0f, targetY, 0f);
        Debug.Log($"[CameraSwitcher] IsPeeking changed to {isDoorOpen}");
    }

    private void ApplyDoorStateImmediate()
    {
        if (door == null)
        {
            return;
        }

        door.localRotation = targetDoorRotation;
    }

    private void UpdateDoorRotation()
    {
        if (door == null)
        {
            return;
        }

        float speed = Mathf.Max(0f, doorRotateSpeed);
        if (speed <= 0f)
        {
            door.localRotation = targetDoorRotation;
            return;
        }

        door.localRotation = Quaternion.Lerp(door.localRotation, targetDoorRotation, Time.deltaTime * speed);
    }
}