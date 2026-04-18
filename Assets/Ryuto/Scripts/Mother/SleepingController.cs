using UnityEngine;
using UnityEngine.InputSystem;

public class SleepingController : MonoBehaviour
{
    // public KeyCode sleepKey = KeyCode.Space; // Not used with new input system

    [SerializeField]
    private bool isSleeping;

    public bool IsSleeping => isSleeping;

    void Update()
    {
        bool sleeping = false;
        // Check Keyboard
        if (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
        {
            sleeping = true;
        }
        // Check Gamepad (Android Handheld)
        if (Gamepad.current != null && Gamepad.current.buttonSouth.isPressed)
        {
            sleeping = true;
        }
        isSleeping = sleeping;
    }
}