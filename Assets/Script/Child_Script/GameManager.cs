using System.Collections;
using UnityEngine;
using TMPro;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    [Header("Game Over")]
    public string gameOverSceneName = "ChildGameOver"; // GAME_OVER (caught) result scene
    public string timeUpSceneName = "ChildGameClear";   // TIME_UP result scene
    public float gameOverDelay = 0f; // 遷移までの待機（実時間）

    // ── PlayerPrefs Keys（結果・ランキング保存用） ─────────────────────────────
    private const string KeyGameOverScore  = "LastGameOverScore";
    private const string KeyTimeUpScore    = "LastTimeUpScore";
    private const string KeyGameOverRank   = "GameOverRank_";
    private const string KeyTimeUpRank     = "TimeUpRank_";
    private const int    RankingSize       = 5;

    // 子機側専用: シーン遷移中に確定した結果種別（"GAME_OVER" または "TIME_UP"）を保持するキー
    private const string KeyResultTypePending = "ResultTypePending";

    // 互換性保持用の公開定数
    public const string PlayerPrefsGameOverScore = KeyGameOverScore;
    public const string PlayerPrefsTimeUpScore = KeyTimeUpScore;
    public const string PlayerPrefsResultTypePending = KeyResultTypePending;
    [SerializeField] private CanvasGroup fadeCanvasGroup;
    [SerializeField, Min(0f)] private float fadeSeconds = 0.5f;

    public static GameManager instance;

    public static event System.Action ResultDataCommitted;
    public static event System.Action<int, bool> ScoreAdded;

    public int score = 0;

    [System.Serializable]
    public struct ScoreSpeedStep
    {
        [Tooltip("この倍率になる最低スコア（最高到達スコアで判定）。")]
        [Min(0)] public int score;
        [Tooltip("速度倍率（アイテム・背景の元の速度に掛ける絶対値）。")]
        [Min(0f)] public float multiplier;
    }

    [Header("得点段階による速度倍率（最高到達スコア → 倍率）")]
    [Tooltip("ONならレーン出現に合わせた緩やかな加速カーブを使う（シーン保存の旧テーブルより優先）。")]
    [SerializeField] private bool useBalancedRunSpeedCurve = true;

    private static readonly ScoreSpeedStep[] BalancedRunSpeedSteps = new ScoreSpeedStep[]
    {
        // 序盤はテンポよく上がり、後半ほど必要スコア幅を広げて「高速化→高得点→さらに高速化」の暴走を防ぐ
        new ScoreSpeedStep { score = 0,      multiplier = 1.00f },
        new ScoreSpeedStep { score = 1500,   multiplier = 1.12f },
        new ScoreSpeedStep { score = 3500,   multiplier = 1.24f },
        new ScoreSpeedStep { score = 6500,   multiplier = 1.36f },
        new ScoreSpeedStep { score = 10500,  multiplier = 1.48f },
        new ScoreSpeedStep { score = 16000,  multiplier = 1.60f },
        new ScoreSpeedStep { score = 23000,  multiplier = 1.72f },
        new ScoreSpeedStep { score = 32000,  multiplier = 1.84f },
        new ScoreSpeedStep { score = 44000,  multiplier = 1.96f },
        new ScoreSpeedStep { score = 58000,  multiplier = 2.08f },
        new ScoreSpeedStep { score = 76000,  multiplier = 2.20f },
        new ScoreSpeedStep { score = 100000, multiplier = 2.32f },
        new ScoreSpeedStep { score = 130000, multiplier = 2.44f },
        new ScoreSpeedStep { score = 168000, multiplier = 2.56f },
        new ScoreSpeedStep { score = 215000, multiplier = 2.68f },
        new ScoreSpeedStep { score = 275000, multiplier = 2.82f },
        new ScoreSpeedStep { score = 350000, multiplier = 2.98f },
        new ScoreSpeedStep { score = 440000, multiplier = 3.15f },
    };

    [SerializeField] private ScoreSpeedStep[] scoreSpeedSteps = new ScoreSpeedStep[]
    {
        new ScoreSpeedStep { score = 0,      multiplier = 1.00f },
        new ScoreSpeedStep { score = 1125,   multiplier = 1.10f },
        new ScoreSpeedStep { score = 2250,   multiplier = 1.20f },
        new ScoreSpeedStep { score = 3625,   multiplier = 1.30f },
        new ScoreSpeedStep { score = 5250,   multiplier = 1.40f },
        new ScoreSpeedStep { score = 8500,   multiplier = 1.50f },
        new ScoreSpeedStep { score = 11750,  multiplier = 1.60f },
        new ScoreSpeedStep { score = 15000,  multiplier = 1.70f },
        new ScoreSpeedStep { score = 18250,  multiplier = 1.80f },
        new ScoreSpeedStep { score = 21500,  multiplier = 1.90f },
        new ScoreSpeedStep { score = 24750,  multiplier = 2.00f },
        new ScoreSpeedStep { score = 28000,  multiplier = 2.10f },
        new ScoreSpeedStep { score = 31250,  multiplier = 2.20f },
        new ScoreSpeedStep { score = 33250,  multiplier = 2.30f },
        new ScoreSpeedStep { score = 36250,  multiplier = 2.40f },
        new ScoreSpeedStep { score = 39250,  multiplier = 2.50f },
        new ScoreSpeedStep { score = 42250,  multiplier = 2.60f },
        new ScoreSpeedStep { score = 45250,  multiplier = 2.70f },
        new ScoreSpeedStep { score = 48250,  multiplier = 2.80f },
        new ScoreSpeedStep { score = 54250,  multiplier = 2.90f },
        new ScoreSpeedStep { score = 60250,  multiplier = 3.00f },
        new ScoreSpeedStep { score = 75250,  multiplier = 3.50f },
        new ScoreSpeedStep { score = 90250,  multiplier = 4.00f },
        new ScoreSpeedStep { score = 105250, multiplier = 4.50f },
        new ScoreSpeedStep { score = 120250, multiplier = 5.00f },
        new ScoreSpeedStep { score = 135250, multiplier = 5.50f },
        new ScoreSpeedStep { score = 150250, multiplier = 6.00f },
        new ScoreSpeedStep { score = 165250, multiplier = 6.50f },
        new ScoreSpeedStep { score = 180250, multiplier = 7.00f },
        new ScoreSpeedStep { score = 195250, multiplier = 7.50f },
        new ScoreSpeedStep { score = 210250, multiplier = 8.00f },
        new ScoreSpeedStep { score = 225250, multiplier = 8.50f },
        new ScoreSpeedStep { score = 240250, multiplier = 9.00f },
        new ScoreSpeedStep { score = 255250, multiplier = 9.50f },
        new ScoreSpeedStep { score = 270250, multiplier = 10.00f },
    };

    private int maxScore;
    private float scoreSpeedMultiplier = 1f;

    public int MaxScore => maxScore;

    /// <summary>
    /// 現在の得点段階倍率 × 難易度別の移動速度倍率（読み取り専用）。チュートリアル中は 1.0。
    /// </summary>
    public float ScoreSpeedMultiplier => isTutorialMode
        ? 1f
        : scoreSpeedMultiplier * GameDifficulty.ChildItemSpeedMultiplier;

    public static float CurrentScoreSpeedMultiplier
    {
        get
        {
            GameManager gm = instance;
            return gm != null ? gm.ScoreSpeedMultiplier : 1f;
        }
    }

    [Header("Fever")]
    public int feverCount = 0;
    public int feverNeeded = 5;
    [Tooltip("フィーバーの継続時間（秒）。")]
    public float feverBoostDuration = 8f;
    public FeverLoopEffect feverLoopEffect;
    public FeverLoopEffect feverLoopEffect2;
    bool isFeverActive = false;
    public bool IsFeverActive => isFeverActive;

    public float FeverRemainingRatio =>
        isFeverActive ? Mathf.Clamp01((feverEndTime - Time.time) / Mathf.Max(0.01f, feverBoostDuration)) : 0f;

    private Coroutine feverRoutine;
    private float feverEndTime = -1f;

    [Header("Score Digits (最上位桁[0] → 最下位桁[5])")]
    public TextMeshProUGUI[] scoreDigitTexts = new TextMeshProUGUI[6];

    private const int SCORE_DIGITS = 6;

    public TextMeshProUGUI feverText;
    public GameObject udpReceiver;

    bool isGameOver = false;
    public bool IsGameOver => isGameOver;
    private bool isTutorialMode = false;

    public bool IsTutorialMode => isTutorialMode;

    private bool isMainPlayActive;
    private float mainPlayStartUnscaledTime;
    private float mainPlayPausedBase;

    public bool IsMainPlayActive => isMainPlayActive;

    public float MainPlayElapsedSeconds =>
        isMainPlayActive
            ? Mathf.Max(0f, (Time.unscaledTime - mainPlayStartUnscaledTime) - Mathf.Max(0f, ChildGamePause.TotalPausedSeconds - mainPlayPausedBase))
            : 0f;

    public void MarkMainPlayStarted()
    {
        if (isMainPlayActive) return;

        isMainPlayActive = true;
        mainPlayStartUnscaledTime = Time.unscaledTime;
        mainPlayPausedBase = ChildGamePause.TotalPausedSeconds;
        Debug.Log("[GameManager] 本編開始を記録しました（チュートリアル終了）。", this);
    }

    private ChildUdpRuntimeHost _host;
    private ChildUdpRuntimeHost _caughtHost;
    private ChildNotificationHub _caughtHub;
    private bool _caughtSubscribed;

    [Header("捕獲演出の待機")]
    [SerializeField, Min(1f)] private float sequenceWaitTimeoutSeconds = 45f;

    private bool _scoreFrozen;
    private int _frozenScore;
    private bool _capturePending;
    private bool _waitingSequenceDone;
    private bool _sequenceDoneReceived;
    private ChildUdpRuntimeHost _releaseSubscribedHost;
    private ChildNotificationHub _releaseSubscribedHub;
    private Coroutine _sequenceWaitRoutine;
    private bool _started;

    public enum ResultType { GameOver, TimeUp }

    private void OnEnable()
    {
        if (_started) TrySubscribeCaught();
    }

    private void Start()
    {
        ResolveHost();

        if (FindFirstObjectByType<ScoreMultiplierDisplay>() == null)
            gameObject.AddComponent<ScoreMultiplierDisplay>();

        if (FindFirstObjectByType<PlayerScorePopup>() == null)
            gameObject.AddComponent<PlayerScorePopup>();

        if (FindFirstObjectByType<LaneRestriction>() == null)
            gameObject.AddComponent<LaneRestriction>();

        _started = true;
        TrySubscribeCaught();
    }

    private void OnDisable()
    {
        UnsubscribeCaught();
    }

    private void OnDestroy()
    {
        UnsubscribeCaught();
    }

    private ChildUdpRuntimeHost ResolveHost()
    {
        if (_host != null) return _host;

        if (udpReceiver != null)
        {
            ChildUdpRuntimeHost onRoot = udpReceiver.GetComponent<ChildUdpRuntimeHost>();
            if (onRoot != null && onRoot.IsConfigured) { _host = onRoot; return _host; }
        }

        ChildUdpRuntimeHost running = ChildUdpRuntimeHost.Running;
        if (running != null) { _host = running; return _host; }

        ChildUdpRuntimeHost found = UnityEngine.Object.FindFirstObjectByType<ChildUdpRuntimeHost>();
        if (found != null && found.IsConfigured) _host = found;
        return _host;
    }

    private bool TrySubscribeCaught()
    {
        if (_caughtSubscribed) return true;
        if (!isActiveAndEnabled) return false;
        if (instance != this) return false;

        ChildUdpRuntimeHost host = ResolveHost();
        if (host == null || host.Notifications == null) return false;

        _caughtHost = host;
        _caughtHub = host.Notifications;
        _caughtHub.CaughtReceived += HandleCaughtReceived;
        _caughtHub.TimeUpReceived += HandleTimeUpReceived;
        _caughtSubscribed = true;
        Debug.Log($"[GameManager] CAUGHT / TIME_UP の購読を開始しました（Host='{host.gameObject.name}'）。");
        return true;
    }

    private void UnsubscribeCaught()
    {
        if (_caughtSubscribed)
        {
            if (_caughtHub != null)
            {
                _caughtHub.CaughtReceived -= HandleCaughtReceived;
                _caughtHub.TimeUpReceived -= HandleTimeUpReceived;
            }

            _caughtSubscribed = false;
        }

        _caughtHub = null;
        _caughtHost = null;

        if (_waitingSequenceDone)
        {
            if (_releaseSubscribedHub != null)
                _releaseSubscribedHub.CaptureSequenceReleased -= HandleCaptureSequenceReleased;

            _waitingSequenceDone = false;
        }

        _releaseSubscribedHub = null;
        _releaseSubscribedHost = null;

        if (_sequenceWaitRoutine != null)
        {
            StopCoroutine(_sequenceWaitRoutine);
            _sequenceWaitRoutine = null;
        }

        _caughtHost = null;
    }

    private void HandleTimeUpReceived()
    {
        if (!isActiveAndEnabled) return;
        if (isGameOver) return;

        Debug.Log("[GameManager] 親機の時間切れクリア（TIME_UP）を受信しました。結果画面へ進みます。");
        TriggerResult(ResultType.TimeUp);
    }

    private void HandleCaughtReceived()
    {
        if (!isActiveAndEnabled) return;
        if (isGameOver) return;

        FreezeScoreForCapture("捕獲通知（CAUGHT）");

        if (WaitForSequenceDone())
            return;

        TriggerResult(ResultType.GameOver);
    }

    private void FreezeScoreForCapture(string reason)
    {
        if (_scoreFrozen) return;

        _scoreFrozen = true;
        _capturePending = true;
        _frozenScore = score;

        Debug.Log($"[GameManager] 得点を確定しました（理由={reason}）: {_frozenScore}点。結果画面まで加算しません");

        QTEManager qte = QTEManager.Instance;
        if (qte != null && qte.CancelQteForResult())
        {
            Time.timeScale = 1f;
        }
    }

    private bool WaitForSequenceDone()
    {
        ChildUdpRuntimeHost host = ResolveHost();
        if (host == null || host.Notifications == null)
        {
            Debug.LogWarning("[GameManager] 子機Hostが見つからないため、演出完了を待てません（即座に結果画面へ進みます）", this);
            return false;
        }

        if (_sequenceDoneReceived)
        {
            Debug.Log("[GameManager] 演出完了通知は既に受信済みのため、待たずに結果画面へ進みます");
            return false;
        }

        if (!_waitingSequenceDone)
        {
            _waitingSequenceDone = true;
            _releaseSubscribedHost = host;
            _releaseSubscribedHub = host.Notifications;
            _releaseSubscribedHub.CaptureSequenceReleased += HandleCaptureSequenceReleased;
            Debug.Log($"[GameManager] 親機の演出完了通知を購読しました（Host='{host.gameObject.name}'）");
        }

        if (host.IsCaptureWaitCompletedForCurrentPlay)
        {
            _sequenceDoneReceived = true;
            Debug.Log("[GameManager] 演出完了は購読成立時に既に確定済みでした（初期同期）。待たずに結果画面へ進みます");
            return false;
        }

        if (_sequenceWaitRoutine == null)
            _sequenceWaitRoutine = StartCoroutine(SequenceWaitTimeoutRoutine());

        return true;
    }

    private void HandleCaptureSequenceReleased(string reason)
    {
        if (_sequenceDoneReceived) return;
        _sequenceDoneReceived = true;

        Debug.Log($"[GameManager] 親機の演出完了通知を受信しました（理由={reason}）。確定得点 {_frozenScore}点 で結果画面へ進みます");

        if (_sequenceWaitRoutine != null)
        {
            StopCoroutine(_sequenceWaitRoutine);
            _sequenceWaitRoutine = null;
        }

        TriggerResult(ResultType.GameOver);
    }

    private IEnumerator SequenceWaitTimeoutRoutine()
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(1f, sequenceWaitTimeoutSeconds));

        _sequenceWaitRoutine = null;

        if (_sequenceDoneReceived) yield break;

        _sequenceDoneReceived = true;
        Debug.LogWarning($"[GameManager] 【フォールバック】親機の演出完了通知を {sequenceWaitTimeoutSeconds:F1}s で受信できませんでした" +
                         "（通信断の可能性）。確定得点のまま結果画面へ進みます", this);

        if (_releaseSubscribedHost != null)
            _releaseSubscribedHost.ReleaseCaptureWaitByLocalTimeout("GameManagerの待機タイムアウト");

        TriggerResult(ResultType.GameOver);
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
        if (_started && !_caughtSubscribed)
            TrySubscribeCaught();

        if (isGameOver) return;

        // 【M-F06 / C18】子機単独スタート（親機なし）時は、選択難易度の制限時間（EASY=4分 / NORMAL=6分 / HARD=8分）でクリアへ遷移する
        if (isMainPlayActive && !isTutorialMode)
        {
            ChildUdpRuntimeHost host = ResolveHost();
            bool isSolo = host == null || host.IsSoloPlayActive || !host.Link.IsConnected;
            if (isSolo && MainPlayElapsedSeconds >= GameDifficulty.TimeLimitSeconds)
            {
                Debug.Log($"[GameManager] 子機単独プレイの制限時間（{GameDifficulty.TimeLimitSeconds:F0}s）に到達しました。TIME_UPクリアへ進みます。");
                TriggerResult(ResultType.TimeUp);
                return;
            }
        }

        UpdateScoreDigits();

        if (feverText != null)
        {
            feverText.text = string.Format("{0}/{1}", feverCount, feverNeeded);
        }
    }

    private void UpdateScoreDigits()
    {
        if (scoreDigitTexts == null || scoreDigitTexts.Length == 0) return;

        int displayScore = Mathf.Clamp(score, 0, 999999);

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
        ApplyScoreChange(amount, notify: true);
    }

    public void DrainScore(int amount)
    {
        ApplyScoreChange(amount, notify: false);
    }

    private void ApplyScoreChange(int amount, bool notify)
    {
        if (_scoreFrozen)
        {
            Debug.Log($"[GameManager] 捕獲後のためポイント加算を停止中（amount={amount} を無視）。確定得点={_frozenScore}");
            return;
        }

        if (isGameOver) return;

        if (ChildGamePause.IsPaused)
        {
            Debug.Log($"[GameManager] 停止中のためポイント加算を無視します（amount={amount}）");
            return;
        }

        bool doubled = false;
        if (isFeverActive && amount > 0)
        {
            amount *= 2;
            doubled = true;
        }

        int before = score;
        score = Mathf.Max(0, score + amount);

        if (notify && score != before)
            ScoreAdded?.Invoke(score - before, doubled);

        if (!isTutorialMode && score > maxScore)
        {
            maxScore = score;
            UpdateScoreSpeedStage();
        }
    }

    private void UpdateScoreSpeedStage()
    {
        float selected = 1f;
        int bestThreshold = -1;
        bool found = false;

        ScoreSpeedStep[] activeSteps = useBalancedRunSpeedCurve ? BalancedRunSpeedSteps : scoreSpeedSteps;
        if (activeSteps != null)
        {
            for (int i = 0; i < activeSteps.Length; i++)
            {
                ScoreSpeedStep step = activeSteps[i];

                if (step.score < 0) continue;
                if (step.multiplier <= 0f || float.IsNaN(step.multiplier) || float.IsInfinity(step.multiplier)) continue;
                if (step.score > maxScore) continue;

                bool isBetter = !found
                                || step.score > bestThreshold
                                || (step.score == bestThreshold && step.multiplier > selected);
                if (!isBetter) continue;

                bestThreshold = step.score;
                selected = step.multiplier;
                found = true;
            }
        }

        scoreSpeedMultiplier = selected;
    }

    public void GameOver(string udpMessage)
    {
        string legacyTimeUpToken = string.Concat("CHILD_", "DEAD");
        if (udpMessage == "TIME_UP" || udpMessage == legacyTimeUpToken)
            TriggerResult(ResultType.TimeUp);
        else
            TriggerResult(ResultType.GameOver);
    }

    public void TriggerResult(ResultType resultType)
    {
        if (isGameOver) return;

        if (_capturePending && resultType == ResultType.TimeUp)
        {
            Debug.LogWarning("[GameManager] 【捕獲優先】TIME_UP要求を GameOver として確定します（捕獲時点の確定得点を使用）", this);
            resultType = ResultType.GameOver;
        }

        isGameOver = true;

        QTEManager qte = QTEManager.Instance;
        if (qte != null) qte.CancelQteForResult();

        string scoreKey = resultType == ResultType.GameOver
            ? KeyGameOverScore
            : KeyTimeUpScore;

        PlayerPrefs.SetString(KeyResultTypePending, resultType == ResultType.GameOver ? "GAME_OVER" : "TIME_UP");

        // 【D-Q03 / M-R06】難易度別のランキング・最新スコア保存とミラーキー同期を一括で行う。
        // GameOver の場合は参考スコア（LastGameOverScore）のみ更新し、正式ランキング（TOP5）には登録しない。
        GameDifficulty.RecordResultScore(resultType == ResultType.GameOver, score);

        Debug.Log($"[GameManager] Result={resultType} diff={GameDifficulty.DisplayName} score={score} saved to '{scoreKey}'");

        ResultDataCommitted?.Invoke();

        Time.timeScale = 0f;

        string resultTypeName = resultType == ResultType.GameOver ? "GAME_OVER" : "TIME_UP";
        string targetScene = resultType == ResultType.GameOver ? gameOverSceneName : timeUpSceneName;
        StartCoroutine(HandleGameOver(resultTypeName, score, targetScene));
    }

    private void ActivateFeverEffects()
    {
        float duration = Mathf.Max(0f, feverBoostDuration);

        isFeverActive = true;
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

    private IEnumerator HandleGameOver(string resultTypeName, int finalScore, string targetScene)
    {
        ChildUdpRuntimeHost host = ResolveHost();
        if (host != null)
        {
            host.NotifyRankingChanged(resultTypeName);
            Debug.Log($"[GameManager] Notified ranking change (snapshot sync): {resultTypeName}:{finalScore}");
        }
        else
        {
            Debug.LogWarning("[GameManager] 子機Hostが見つからないため、結果を親機へ送信できません。");
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
            feverCount = 0;
            ActivateFeverEffects();
        }
    }

    public void SetTutorialMode(bool active)
    {
        isTutorialMode = active;
    }

    public void ResetScoreAndFever()
    {
        _scoreFrozen = false;
        _frozenScore = 0;
        _capturePending = false;
        _sequenceDoneReceived = false;
        score = 0;
        feverCount = 0;

        maxScore = 0;
        scoreSpeedMultiplier = 1f;

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

        isFeverActive = false;
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
