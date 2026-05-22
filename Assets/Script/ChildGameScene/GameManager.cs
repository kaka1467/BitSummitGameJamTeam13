using System.Collections;
using UnityEngine;
using TMPro;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    [Header("Game Over")]
    public string gameOverSceneName = "GameOverResult"; // GAME_OVER (caught) result scene
    public string timeUpSceneName = "TimeUpResult";   // TIME_UP result scene
    public float gameOverDelay = 0f; // 遷移までの待機（実時間）

    public const string PlayerPrefsGameOverScore = "LastGameOverScore";
    public const string PlayerPrefsTimeUpScore = "LastTimeUpScore";
    public const string PlayerPrefsResultTypePending = "ResultTypePending";
    [SerializeField] private CanvasGroup fadeCanvasGroup;
    [SerializeField, Min(0f)] private float fadeSeconds = 0.5f;

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
    public FeverLoopEffect feverLoopEffect;
    public FeverLoopEffect feverLoopEffect2;
    bool isFeverMagnetActive = false;
    public bool IsFeverMagnetActive => isFeverMagnetActive;

    private Coroutine feverRoutine;
    private float feverEndTime = -1f;

    // --- Score: 1桁ずつ別のTextに表示 ---
    // scoreText は削除し、6桁分の配列に置き換え。
    // インスペクタで scoreDigitTexts[0] = 最上位桁、scoreDigitTexts[5] = 最下位桁 の順に設定してください。
    [Header("Score Digits (最上位桁[0] → 最下位桁[5])")]
    public TextMeshProUGUI[] scoreDigitTexts = new TextMeshProUGUI[6];

    private const int SCORE_DIGITS = 6;

    public TextMeshProUGUI timeText;
    public TextMeshProUGUI feverText;
    public ChildUdpReceiver udpReceiver;

    [Header("Time Change Display")]
    [SerializeField] private TextMeshProUGUI timeChangeText;
    [SerializeField] private float timeChangeDisplaySeconds = 1.5f;

    private Coroutine timeChangeRoutine;

    bool isGameOver = false;
    public bool IsGameOver => isGameOver;
    private bool isTutorialMode = false;

    public enum ResultType { GameOver, TimeUp }

    private void Start()
    {
        EnsureUdpReceiver();
    }

    private void EnsureUdpReceiver()
    {
        if (udpReceiver == null)
            udpReceiver = UnityEngine.Object.FindFirstObjectByType<ChildUdpReceiver>();
    }

    void Awake()
    {
        instance = this;

        if (fadeCanvasGroup != null)
        {
            fadeCanvasGroup.alpha = 0f;
            fadeCanvasGroup.interactable = false;
            fadeCanvasGroup.blocksRaycasts = false;
        }

        if (timeChangeText != null)
        {
            timeChangeText.gameObject.SetActive(false);
        }

        if (feverLoopEffect != null)
        {
            feverLoopEffect.StopEffect();
        }

        if (feverLoopEffect2 != null)
        {
            feverLoopEffect2.StopEffect();
        }
    }

    void Update()
    {
        if (isGameOver) return;

        if (!isTutorialMode)
        {
            time -= Time.deltaTime;

            if (time <= 0f)
            {
                time = 0f;
                GameOver();
            }
        }

        UpdateScoreDigits();

        int minutes = (int)(time / 60);
        int seconds = (int)(time % 60);
        timeText.text = string.Format("{0:00}:{1:00}", minutes, seconds);

        if (feverText != null)
        {
            feverText.text = string.Format("{0}/{1}", feverCount, feverNeeded);
        }
    }

    /// <summary>
    /// score を1桁ずつ分解して scoreDigitTexts の各要素に書き込む。
    /// 上限は 999999。それを超えた場合は 999999 として表示する。
    /// </summary>
    private void UpdateScoreDigits()
    {
        if (scoreDigitTexts == null || scoreDigitTexts.Length == 0) return;

        // 表示上限クランプ（6桁 = 999999）
        int displayScore = Mathf.Clamp(score, 0, 999999);

        // 最下位桁から順に取り出し、配列の末尾から埋める
        int remaining = displayScore;
        for (int i = SCORE_DIGITS - 1; i >= 0; i--)
        {
            int digit = remaining % 10;
            remaining /= 10;

            if (i < scoreDigitTexts.Length && scoreDigitTexts[i] != null)
            {
                scoreDigitTexts[i].text = digit.ToString();
            }
        }
    }

    public void AddScore(int amount)
    {
        score += amount;
    }

    // Called when timer runs out — TIME_UP result
    // 【修正済み】ゲームオーバー時の不要なフィーバー加算バグを除去
    public void GameOver()
    {
        TriggerResult(ResultType.TimeUp);
    }

    // Called when parent catches child — GAME_OVER result
    public void GameOver(string udpMessage)
    {
        // Legacy entry point: "TIME_UP" and "CHILD_DEAD" use TIME_UP result;
        // "CAUGHT" and anything else uses GAME_OVER result.
        if (udpMessage == "TIME_UP" || udpMessage == "CHILD_DEAD")
            TriggerResult(ResultType.TimeUp);
        else
            TriggerResult(ResultType.GameOver);
    }

    // 【修正済み】ランキング更新ロジックを追加
    public void TriggerResult(ResultType resultType)
    {
        if (isGameOver) return;

        isGameOver = true;

        // Save score under type-specific key
        string scoreKey = resultType == ResultType.GameOver
            ? PlayerPrefsGameOverScore
            : PlayerPrefsTimeUpScore;
        string rankKey = resultType == ResultType.GameOver
            ? "GameOverRank_"
            : "TimeUpRank_";

        PlayerPrefs.SetInt(scoreKey, score);
        PlayerPrefs.SetString(PlayerPrefsResultTypePending, resultType == ResultType.GameOver ? "GAME_OVER" : "TIME_UP");

        // ランキングの更新
        UpdateRanking(rankKey, score);

        PlayerPrefs.Save();
        Debug.Log($"[GameManager] Result={resultType} score={score} saved to '{scoreKey}'");

        // 一時的に時間を止める（UI表示などがある場合）。遷移はRealtimeで行う。
        Time.timeScale = 0f;

        string udpMsg = resultType == ResultType.GameOver
            ? $"CHILD_SCORE:GAME_OVER:{score}"
            : $"CHILD_SCORE:TIME_UP:{score}";
        string targetScene = resultType == ResultType.GameOver ? gameOverSceneName : timeUpSceneName;
        StartCoroutine(HandleGameOver(udpMsg, targetScene));
    }

    // 【追加】PlayerPrefsを用いたランキング保存メソッド
    private static void UpdateRanking(string keyPrefix, int newScore)
    {
        const int RankingSize = 5;
        int[] ranking = new int[RankingSize];
        for (int i = 0; i < RankingSize; i++)
            ranking[i] = PlayerPrefs.GetInt(keyPrefix + i, 0);

        for (int i = 0; i < RankingSize; i++)
        {
            if (newScore > ranking[i])
            {
                for (int j = RankingSize - 1; j > i; j--)
                    ranking[j] = ranking[j - 1];
                ranking[i] = newScore;
                break;
            }
        }

        for (int i = 0; i < RankingSize; i++)
            PlayerPrefs.SetInt(keyPrefix + i, ranking[i]);
    }

    private void ActivateFeverEffects()
    {
        AddScore(feverScoreBonus);

        float duration = Mathf.Max(0f, feverBoostDuration);
        float multiplier = Mathf.Max(1f, feverBoostMultiplier);

        GameObject player = GameObject.FindWithTag("Player");
        if (player != null)
        {
            PlayerBoost boost = player.GetComponent<PlayerBoost>() ?? player.GetComponentInParent<PlayerBoost>();
            if (boost == null)
            {
                boost = player.AddComponent<PlayerBoost>();
            }

            boost.StartBoost(duration, multiplier);
        }

        isFeverMagnetActive = true;
        feverEndTime = Mathf.Max(feverEndTime, Time.time + duration);

        if (feverLoopEffect != null)
        {
            feverLoopEffect.StartEffect();
        }

        if (feverLoopEffect2 != null)
        {
            feverLoopEffect2.StartEffect();
        }

        if (feverRoutine == null)
        {
            feverRoutine = StartCoroutine(FeverEffectRoutine());
        }
    }

    private IEnumerator FeverEffectRoutine()
    {
        while (Time.time < feverEndTime)
        {
            yield return null;
        }

        StopFeverEffects();
    }

    private IEnumerator HandleGameOver(string udpMessage, string targetScene)
    {
        EnsureUdpReceiver();
        if (udpReceiver != null)
        {
            udpReceiver.SendState(udpMessage);
            Debug.Log($"[GameManager] Sent UDP: '{udpMessage}'");
        }
        else
        {
            Debug.LogWarning("[GameManager] ChildUdpReceiver not found — result not sent to parent.");
        }

        yield return new WaitForSecondsRealtime(0.1f);

        if (fadeCanvasGroup != null)
        {
            if (!fadeCanvasGroup.gameObject.activeSelf)
            {
                fadeCanvasGroup.gameObject.SetActive(true);
            }

            fadeCanvasGroup.interactable = false;
            fadeCanvasGroup.blocksRaycasts = true;
            yield return StartCoroutine(FadeOutRoutine());
        }

        float delay = Mathf.Max(0f, gameOverDelay);
        if (delay > 0f)
        {
            yield return new WaitForSecondsRealtime(delay);
        }

        // シーン遷移の前にタイムスケールを復帰させる
        Time.timeScale = 1f;

        if (!string.IsNullOrEmpty(targetScene))
        {
            SceneManager.LoadScene(targetScene);
        }
    }

    private IEnumerator FadeOutRoutine()
    {
        if (fadeCanvasGroup == null)
        {
            yield break;
        }

        float duration = Mathf.Max(0f, fadeSeconds);
        if (duration <= 0f)
        {
            fadeCanvasGroup.alpha = 1f;
            yield break;
        }

        float startAlpha = fadeCanvasGroup.alpha;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            fadeCanvasGroup.alpha = Mathf.Lerp(startAlpha, 1f, t);
            yield return null;
        }

        fadeCanvasGroup.alpha = 1f;
    }

    public void AddFeverCount()
    {
        feverCount++;
        if (feverCount >= feverNeeded)
        {
            feverCount = 0; // Reset fever count
            ActivateFeverEffects();
        }
    }

    public void AddTime(float amount)
    {
        if (amount < 0f)
        {
            ShowTimeDecrease(amount);
        }

        time += amount;
        if (time > maxTime) time = maxTime;
    }

    private void ShowTimeDecrease(float amount)
    {
        if (timeChangeText == null) return;

        int totalSeconds = Mathf.Abs(Mathf.RoundToInt(amount));
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;

        timeChangeText.text = $"-{minutes:00}:{seconds:00}";
        timeChangeText.gameObject.SetActive(true);

        if (timeChangeRoutine != null)
        {
            StopCoroutine(timeChangeRoutine);
        }

        timeChangeRoutine = StartCoroutine(HideTimeChangeAfterDelay());
    }

    private IEnumerator HideTimeChangeAfterDelay()
    {
        float duration = Mathf.Max(0f, timeChangeDisplaySeconds);
        if (duration > 0f)
        {
            yield return new WaitForSecondsRealtime(duration);
        }

        if (timeChangeText != null)
        {
            timeChangeText.gameObject.SetActive(false);
        }

        timeChangeRoutine = null;
    }

    public void SetTutorialMode(bool active)
    {
        isTutorialMode = active;
    }

    public void ResetScoreAndFever()
    {
        score = 0;
        feverCount = 0;
        StopFeverEffects();
        UpdateScoreDigits();

        if (feverText != null)
        {
            feverText.text = string.Format("{0}/{1}", feverCount, feverNeeded);
        }
    }

    private void StopFeverEffects()
    {
        if (feverRoutine != null)
        {
            StopCoroutine(feverRoutine);
            feverRoutine = null;
        }

        isFeverMagnetActive = false;
        feverEndTime = -1f;

        if (feverLoopEffect != null)
        {
            feverLoopEffect.StopEffect();
        }

        if (feverLoopEffect2 != null)
        {
            feverLoopEffect2.StopEffect();
        }
    }
}