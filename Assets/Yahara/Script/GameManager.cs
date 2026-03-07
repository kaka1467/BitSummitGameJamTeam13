using UnityEngine;
using TMPro;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;

    public int score = 0;
    public float time = 0;

    public TextMeshProUGUI scoreText;
    public TextMeshProUGUI timeText;

    bool isGameOver = false;

    void Awake()
    {
        instance = this;
    }
    void Update()
    {
        if (isGameOver) return;
        time += Time.deltaTime;
        scoreText.text = score.ToString("000000");
        timeText.text = ((int)time).ToString();
    }

    public void AddScore(int amount)
    {
        score += amount;
    }

    public void GameOver()
    {
        isGameOver = true;
        Time.timeScale = 0;
    }
}