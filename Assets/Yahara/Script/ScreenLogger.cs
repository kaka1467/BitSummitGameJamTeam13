using UnityEngine;

public class ScreenLogger : MonoBehaviour
{
    void Start()
    {
        Debug.Log("現在の画面サイズ: " + Screen.currentResolution.width + " x " + Screen.currentResolution.height);
        Debug.Log("ウィンドウサイズ: " + Screen.width + " x " + Screen.height);
    }
}