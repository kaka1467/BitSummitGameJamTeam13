using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneChanger : MonoBehaviour
{
    void Start()
    {
        Invoke("ChangeScene", 3f);
    }

    void ChangeScene()
    {
        SceneManager.LoadScene("Result");
    }
}