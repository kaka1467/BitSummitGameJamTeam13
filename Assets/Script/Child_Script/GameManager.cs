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
    // 親機・子機で同じPlayerPrefs保存形式（キー文字列・降順TOP5）を使用します。
    // 既存セーブデータおよびランキングとの互換性を保つため、キー文字列の値は絶対に変更しないでください。
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

    /// <summary>
    /// 結果データ（LastGameOverScore / LastTimeUpScore / ResultTypePending / ランキング）への書き込みが完了したときに発火する静的イベント。
    /// 結果シーンの表示（ResultScoreUI など）はシーン読み込み時の表示リフレッシュに加え、このイベントでも表示を再読み込みして同期する。
    /// （データ書き込みがシーン読み込み後の表示リフレッシュより後に行われるケースに対応するため）
    /// </summary>
    public static event System.Action ResultDataCommitted;

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

    // ── 片付け演出用：ゲーム進行率の通知 ─────────────────────────────────────
    //   親機の片付け開始条件（後半に入室するか）に使う。
    //   送信頻度を抑えるため、更新は間隔制御して行う（毎フレームは送らない）。
    [Header("Game Progress Notification")]
    [Tooltip("親機へ進行率（0〜1）を送信する間隔（秒）。片付け演出の開始判定に使用。")]
    [SerializeField, Min(0.1f)] private float gameProgressSendInterval = 1f;
    [Tooltip("進行率通知を有効にする。OFFにすると片付け演出の開始は親機側の経過時間で判定される。")]
    [SerializeField] private bool sendGameProgress = true;

    private float _nextGameProgressSendTime;

    bool isGameOver = false;
    public bool IsGameOver => isGameOver;
    private bool isTutorialMode = false;

    // ── CAUGHT（親機の捕獲）の購読 ────────────────────────────────────────────
    //   受信後の結果処理（TriggerResult(GameOver)）は本クラスが担当する。
    //   UDP側（ChildUdpReceiver）は「受理した」ことを通知するだけ（保留・受理済みはUDP側が保持）。
    //   ・購読は生存個体（ChildUdpReceiver.instance）を基準にする。
    //   ・結果処理に必要な初期化（Awake）が終わった「Start完了後」に購読を開始し、
    //     初期化途中に TriggerResult を実行して Start に上書きされる構造を避ける。
    //   ・Instance取得遅延には Update で再試行する。
    //   ・GameManager.instance に対応する正当な担当だけが購読する（二重購読しない）。
    private ChildUdpReceiver _caughtReceiver;
    private bool _caughtSubscribed;

    // ── 捕獲演出の待機（親機の演出完了まで結果画面へ進ませない）────────────────
    [Header("捕獲演出の待機")]
    [Tooltip("親機の演出完了通知（SEQUENCE_DONE）を受け取るまでの上限（秒）。\n" +
             "超えたら通信断とみなし、理由をログに出して確定得点のまま結果画面へ進みます（永久待機しない）。")]
    [SerializeField, Min(1f)] private float sequenceWaitTimeoutSeconds = 45f;

    private bool _scoreFrozen;                 // 捕獲通知で確定したか（以後の加算を停止）
    private int _frozenScore;                  // 確定得点（結果・ランキングはこの値）
    private bool _waitingSequenceDone;         // CaptureSequenceReleased を購読済みか
    private bool _sequenceDoneReceived;        // 演出完了（または上限）で遷移済みか
    private Coroutine _sequenceWaitRoutine;
    private bool _started;   // Start完了フラグ（初回の購読をStart後へ遅らせるため）

    public enum ResultType { GameOver, TimeUp }

    private void OnEnable()
    {
        // 初回は Start 完了まで購読しない（結果処理に必要な初期化が終わってから受け取る）。
        // 再有効化（disable→enable）では Start が再実行されないため、ここで購読を再開する。
        if (_started) TrySubscribeCaught();
    }

    private void Start()
    {
        EnsureUdpReceiver();

        // 初期化完了を記録してから CAUGHT の購読を開始する。
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

    private void EnsureUdpReceiver()
    {
        if (udpReceiver == null)
            udpReceiver = UnityEngine.Object.FindFirstObjectByType<ChildUdpReceiver>();
    }

    /// <summary>
    /// CAUGHT（親機の捕獲）を購読する。Start完了後の正当な GameManager だけが購読する。
    /// Instance が未取得の場合は購読せず false を返す（Update で再試行する）。
    /// </summary>
    private bool TrySubscribeCaught()
    {
        if (_caughtSubscribed) return true;
        if (!isActiveAndEnabled) return false;
        if (instance != this) return false;   // GameManager.instance に対応する正当な担当だけ

        ChildUdpReceiver receiver = ChildUdpReceiver.instance;
        if (receiver == null) return false;   // 取得できたら Update で再試行する

        _caughtReceiver = receiver;
        _caughtReceiver.CaughtReceived += HandleCaughtReceived;
        _caughtSubscribed = true;
        Debug.Log($"[GameManager] CAUGHT の購読を開始しました（receiver='{receiver.gameObject.name}'）。");
        return true;
    }

    /// <summary>CAUGHT の購読を解除する（OnDisable/OnDestroy から呼ぶ。二重に呼んでも安全）。</summary>
    private void UnsubscribeCaught()
    {
        if (_caughtSubscribed)
        {
            if (_caughtReceiver != null)
                _caughtReceiver.CaughtReceived -= HandleCaughtReceived;

            _caughtSubscribed = false;
        }

        // 演出完了通知の購読も解除する（破棄後に呼ばれないようにする）。
        if (_waitingSequenceDone)
        {
            ChildUdpReceiver receiver = ChildUdpReceiver.instance;
            if (receiver != null)
                receiver.CaptureSequenceReleased -= HandleCaptureSequenceReleased;

            _waitingSequenceDone = false;
        }

        if (_sequenceWaitRoutine != null)
        {
            StopCoroutine(_sequenceWaitRoutine);
            _sequenceWaitRoutine = null;
        }

        _caughtReceiver = null;
    }

    /// <summary>
    /// 親機の捕獲（CAUGHT）を受理したときに、メインスレッドで1回だけ呼ばれる。
    /// 既存の TriggerResult(GameOver) をそのまま呼ぶ（本体・保存・ランキング・遷移は変更しない）。
    /// </summary>
    private void HandleCaughtReceived()
    {
        if (!isActiveAndEnabled) return;
        if (isGameOver) return;   // TriggerResult 側のガードと同じ（無駄な再入を避けるだけ）

        // 1) 捕獲時点の得点を確定し、以後の加算を停止する（ランキング・最終得点はこの値を使う）。
        FreezeScoreForCapture("捕獲通知（CAUGHT）");

        // 2) 親機の演出完了通知を待ってから結果画面へ進む（入力・得点は既に停止済み）。
        //    通知が来ない（通信断）場合は上限で解除するため、永久には待たない。
        if (WaitForSequenceDone())
            return;

        TriggerResult(ResultType.GameOver);
    }

    /// <summary>捕獲時点の得点を確定得点として保存し、以後の加算を止める（二重実行しても値は変わらない）。</summary>
    private void FreezeScoreForCapture(string reason)
    {
        if (_scoreFrozen) return;

        _scoreFrozen = true;
        _frozenScore = score;   // score 自体は以後変更されないため、この値が最終得点になる

        Debug.Log($"[GameManager] 得点を確定しました（理由={reason}）: {_frozenScore}点。結果画面まで加算しません");
    }

    /// <summary>
    /// 親機の演出完了通知（ChildUdpReceiver.CaptureSequenceReleased）を待つ。
    ///  ・既に受信済み／購読できる相手が居ない場合は false（呼び出し側が即座に遷移する）。
    ///  ・二重購読しない。
    /// </summary>
    private bool WaitForSequenceDone()
    {
        ChildUdpReceiver receiver = ChildUdpReceiver.instance != null ? ChildUdpReceiver.instance : udpReceiver;
        if (receiver == null)
        {
            Debug.LogWarning("[GameManager] ChildUdpReceiver が見つからないため、演出完了を待てません（即座に結果画面へ進みます）", this);
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
            receiver.CaptureSequenceReleased += HandleCaptureSequenceReleased;
            Debug.Log("[GameManager] 親機の演出完了通知を待機します（入力ロック・得点停止は適用済み）");
        }

        if (_sequenceWaitRoutine == null)
            _sequenceWaitRoutine = StartCoroutine(SequenceWaitTimeoutRoutine());

        return true;
    }

    /// <summary>演出完了通知の受信（または子機側の上限到達）で、確定得点のまま結果画面へ進む。</summary>
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

    /// <summary>
    /// 【異常時フォールバック】親機の演出完了通知が来ない場合（通信断など）の上限。
    /// 理由をログに出して結果画面へ進む（永久待機しない）。
    /// </summary>
    private IEnumerator SequenceWaitTimeoutRoutine()
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(1f, sequenceWaitTimeoutSeconds));

        _sequenceWaitRoutine = null;

        if (_sequenceDoneReceived) yield break;

        _sequenceDoneReceived = true;
        Debug.LogWarning($"[GameManager] 【フォールバック】親機の演出完了通知を {sequenceWaitTimeoutSeconds:F1}s で受信できませんでした" +
                         "（通信断の可能性）。確定得点のまま結果画面へ進みます", this);

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
        // CAUGHT の購読がまだ張れていない場合は再試行する（ChildUdpReceiver.Instance の取得遅延）。
        if (_started && !_caughtSubscribed)
            TrySubscribeCaught();

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

        // 片付け演出用：進行率を親機へ定期通知する（出現率・得点には影響しない）。
        SendGameProgressIfDue();
    }

    /// <summary>
    /// 現在のゲーム進行率（0〜1）を算出し、間隔どおりに親機へ送信する。
    /// 片付け演出（母親の後半入室）の開始条件に使用する。
    /// </summary>
    private void SendGameProgressIfDue()
    {
        if (!sendGameProgress) return;
        if (isGameOver || isTutorialMode) return;
        if (Time.unscaledTime < _nextGameProgressSendTime) return;

        EnsureUdpReceiver();
        if (udpReceiver == null) return;

        float maxTimeSafe = Mathf.Max(1f, maxTime);
        float progressRate = Mathf.Clamp01(1f - (time / maxTimeSafe));
        udpReceiver.SendGameProgress(progressRate);

        _nextGameProgressSendTime = Time.unscaledTime + Mathf.Max(0.1f, gameProgressSendInterval);
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
        // 捕獲通知後は、結果画面まで一切ポイントを増やさない（毎フレーム加算・アイテム・コンボ・
        // ボーナス・遅延処理などの全加算入口はここを通る）。
        if (_scoreFrozen)
        {
            Debug.Log($"[GameManager] 捕獲後のためポイント加算を停止中（amount={amount} を無視）。確定得点={_frozenScore}");
            return;
        }

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
            ? KeyGameOverScore
            : KeyTimeUpScore;
        string rankKey = resultType == ResultType.GameOver
            ? KeyGameOverRank
            : KeyTimeUpRank;

        PlayerPrefs.SetInt(scoreKey, score);
        PlayerPrefs.SetString(KeyResultTypePending, resultType == ResultType.GameOver ? "GAME_OVER" : "TIME_UP");

        // ランキングの更新
        UpdateRanking(rankKey, score);

        PlayerPrefs.Save();
        Debug.Log($"[GameManager] Result={resultType} score={score} saved to '{scoreKey}'");

        // 書き込み完了を通知（結果シーンの表示がシーン読み込み後でも最新データに同期する）
        ResultDataCommitted?.Invoke();

        // 一時的に時間を止める（UI表示などがある場合）。遷移はRealtimeで行う。
        Time.timeScale = 0f;

        // 最終結果は親機へ「プレイID付き」で送る（親機の保存確認 ACK が来るまで子機側で保持・再送される）
        string resultTypeName = resultType == ResultType.GameOver ? "GAME_OVER" : "TIME_UP";
        string targetScene = resultType == ResultType.GameOver ? gameOverSceneName : timeUpSceneName;
        StartCoroutine(HandleGameOver(resultTypeName, score, targetScene));
    }

    // 【追加】PlayerPrefsを用いたランキング保存メソッド
    // 挿入ロジック本体は RankingUtil に集約（親機と共有・単体テスト可能）。挙動は従来と同一。
    private static void UpdateRanking(string keyPrefix, int newScore)
    {
        // 保存前の全順位（2位がどこで0になるかの追跡用）
        int[] before = RankingUtil.ReadFromPlayerPrefs(keyPrefix, RankingSize);
        RankingUtil.InsertScoreToPlayerPrefs(keyPrefix, newScore, RankingSize);
        int[] after = RankingUtil.ReadFromPlayerPrefs(keyPrefix, RankingSize);

        Debug.Log($"[GameManager] UpdateRanking key='{keyPrefix}', before=[{string.Join(", ", before)}], " +
                  $"newScore={newScore}, after=[{string.Join(", ", after)}]");
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

    private IEnumerator HandleGameOver(string resultTypeName, int finalScore, string targetScene)
    {
        EnsureUdpReceiver();
        if (udpReceiver != null)
        {
            // 結果確定後、子機を唯一の基準としてランキング／スコアのスナップショットを親機へ同期する。
            // （親機の確認返信は待たず、子機側の保存はここで完了している）
            udpReceiver.NotifyRankingChanged(resultTypeName);
            Debug.Log($"[GameManager] Notified ranking change (snapshot sync): {resultTypeName}:{finalScore}");
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
        // 再プレイ：得点ロックと演出待機の状態も初期化する（前回プレイの待機を残さない）。
        _scoreFrozen = false;
        _frozenScore = 0;
        _sequenceDoneReceived = false;
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