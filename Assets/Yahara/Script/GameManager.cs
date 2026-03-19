using System.Collections;
using UnityEngine;
using TMPro;

public class GameManager : MonoBehaviour
{
    public static GameManager instance;

    public int score = 0;
    public float time = 180f;
    public float maxTime = 180f;

    [Header("Fever")]
    public int feverCount = 0;
    public int feverNeeded = 5;
    public int feverScoreBonus = 30;
    public float feverBoostDuration = 8f;
    public float feverBoostMultiplier = 1.5f;

    bool isFeverMagnetActive = false;
    public bool IsFeverMagnetActive => isFeverMagnetActive;

    public TextMeshProUGUI scoreText;
    public TextMeshProUGUI timeText;

    bool isGameOver = false;
    public bool IsGameOver => isGameOver;

    void Awake()
    {
        instance = this;
    }

    void Update()
    {
        if (isGameOver) return;

        float deltaTime = (QTEManager.Instance != null && QTEManager.Instance.IsQteActive)
            ? Time.unscaledDeltaTime
            : Time.deltaTime;

        time -= deltaTime;

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

    public void AddFeverCount()
    {
        feverCount++;
        if (feverCount >= feverNeeded)
        {
            feverCount = 0;
            StartCoroutine(ActivateFeverEffects());
        }
    }

    private IEnumerator ActivateFeverEffects()
    {
        AddScore(feverScoreBonus);

        GameObject player = GameObject.FindWithTag("Player");
        if (player != null)
        {
            PlayerBoost boost = player.GetComponent<PlayerBoost>() ?? player.GetComponentInParent<PlayerBoost>();
            if (boost == null)
            {
                boost = player.AddComponent<PlayerBoost>();
            }

            boost.StartBoost(feverBoostDuration, feverBoostMultiplier);
        }

        isFeverMagnetActive = true;
        float endTime = Time.time + feverBoostDuration;
        while (Time.time < endTime)
        {
            yield return null;
        }

        isFeverMagnetActive = false;
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