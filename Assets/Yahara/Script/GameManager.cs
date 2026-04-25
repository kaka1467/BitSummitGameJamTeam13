using System.Collections;
using UnityEngine;
using TMPro;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    [Header("Game Over")]
    public string gameOverSceneName = "GameOver"; // 遷移先のシーン名をインスペクタで設定
    public float gameOverDelay = 0f; // 遷移までの待機（実時間）

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
    public TextMeshProUGUI feverText;
    public ChildUdpReceiver udpReceiver;

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

        if (feverText != null)
        {
            feverText.text = string.Format("{0}/{1}", feverCount, feverNeeded);
        }
    }

    public void AddScore(int amount)
    {
        score += amount;
    }

    public void GameOver()
    {
        GameOver("TIME_UP");
    }

    public void GameOver(string udpMessage)
    {
        if (isGameOver) return;

        isGameOver = true;

        // リザルト用にスコアを保存
        PlayerPrefs.SetInt("ResultScore", score);
        PlayerPrefs.SetInt("ResultScorePending", 1);
        PlayerPrefs.Save();

        // 一時的に時間を止める（UI表示などがある場合）。遷移はRealtimeで行う。
        Time.timeScale = 0f;
        StartCoroutine(HandleGameOver(udpMessage));
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

    private IEnumerator HandleGameOver(string udpMessage)
    {
        if (udpReceiver != null)
        {
            udpReceiver.SendState(udpMessage);
        }

        yield return new WaitForSecondsRealtime(0.1f);

        // シーン遷移の前にタイムスケールを復帰させる
        Time.timeScale = 1f;

        if (!string.IsNullOrEmpty(gameOverSceneName))
        {
            // シーンをロード
            SceneManager.LoadScene(gameOverSceneName);
        }
    }

    public void AddFeverCount()
    {
        feverCount++;
        if (feverCount >= feverNeeded)
        {
            feverCount = 0; // Reset fever count
            StartCoroutine(ActivateFeverEffects());
        }
    }

    public void AddTime(float amount)
    {
        time += amount;
        if (time > maxTime) time = maxTime;
    }
}