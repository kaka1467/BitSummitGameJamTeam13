using UnityEngine;

public class SleepingController : MonoBehaviour
{
    public KeyCode sleepKey = KeyCode.Space;

    [SerializeField]
    private bool isSleeping;

    public bool IsSleeping => isSleeping;

    void Update()
    {
        isSleeping = Input.GetKey(sleepKey);
    }
}