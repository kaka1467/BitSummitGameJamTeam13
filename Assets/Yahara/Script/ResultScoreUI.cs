using UnityEngine;
using TMPro;

public class ResultScoreUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI resultScoreText;

    private void Start()
    {
        int score = PlayerPrefs.GetInt("ResultScore", 0);
        if (resultScoreText != null)
        {
            resultScoreText.text = score.ToString("000000");
        }
    }
}