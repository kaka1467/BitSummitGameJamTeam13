using UnityEngine;
using TMPro;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;

    public int score = 0;
    public float time = 180f;
    public float maxTime = 180f;

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

        time -= Time.deltaTime;

        if (time <= 0f)
        {
            time = 0f;
            GameOver();
        }

        scoreText.text = score.ToString("000000");

        int minutes = (int)(time / 60);
        int seconds = (int)(time % 60);
        timeText.text = string.Format("{0}:{1:00}", minutes, seconds);
    }

    public void AddScore(int amount)
    {
        score += amount;
    }

    public void AddTime(float amount)
    {
        time += amount;
        if (time > maxTime) time = maxTime;
    }

    public void GameOver()
    {
        isGameOver = true;
        Time.timeScale = 0;
    }
}