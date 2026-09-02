using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

public class TutorialFlow : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerMove playerMove;
    [SerializeField] private ItemSpawner itemSpawner;
    [SerializeField] private GameManager gameManager;
    [SerializeField] private ChildUdpReceiver udpReceiver;
    [SerializeField] private TextMeshProUGUI countdownText;
    [SerializeField] private TextMeshProUGUI startText;
    [SerializeField] private BGMController bgmController;

    [Header("Settings")]
    [SerializeField] private bool runOnStart = true;
    [SerializeField] private bool disableGameManagerDuringTutorial = true;
    [SerializeField] private float initialDelay = 0.5f;

    [Header("Auto Collect")]
    [SerializeField] private float collectTimeout = 5f;
    [SerializeField] private List<GameObject> tutorialItems = new List<GameObject>();

    [Header("Auto Avoid")]
    [SerializeField] private float avoidTimeout = 5f;
    [SerializeField] private List<GameObject> tutorialObstacles = new List<GameObject>();

    [Header("QTE")]
    [SerializeField] private float qteWaitTimeout = 12f;
    [SerializeField] private float afterQteDelay = 0.5f;

    [Header("Countdown")]
    [SerializeField] private int countdownFrom = 3;
    [SerializeField] private float countdownStepSeconds = 1f;
    [SerializeField] private float goDisplaySeconds = 0.5f;

    [Header("Start Text Animation")]
    [SerializeField] private Vector3 startScaleFrom = new Vector3(0.2f, 0.2f, 1f);
    [SerializeField] private Vector3 startScaleTo = new Vector3(1.2f, 1.2f, 1f);
    [SerializeField] private float startTweenSeconds = 0.35f;
    [SerializeField] private Ease startEase = Ease.OutBack;

    [Header("Auto Horizontal Move")]
    [SerializeField] private bool autoHorizontalEnabled = true;
    [SerializeField, Min(0f)] private float autoHorizontalRange = 1.5f;
    [SerializeField, Min(0.02f)] private float autoTargetUpdateSeconds = 0.05f;
    [SerializeField, Min(0f)] private float autoCollectCenterRange = 0.8f;

    [Header("Skip (prototype)")]
    [Tooltip("skipKey を holdToSkipSeconds 秒ホールドすると演示フェーズをスキップする。")]
    [SerializeField] private bool enableHoldToSkip = true;
    [SerializeField] private Key skipKey = Key.K;
    [SerializeField, Min(0.05f)] private float holdToSkipSeconds = 1f;

    [Header("Interactive Collect (prototype)")]
    [Tooltip("アイテム取得ステップをプレイヤー操作にする。false で従来どおりの自動演示。")]
    [SerializeField] private bool interactiveCollect = true;
    [Tooltip("1回の試行でプレイヤーが取れなかった場合に打ち切るまでの秒数。")]
    [SerializeField, Min(1f)] private float playerCollectTimeout = 8f;
    [Tooltip("取り逃したときに再挑戦させる回数。使い切ったら自動演示（お手本）へ。")]
    [SerializeField, Min(0)] private int collectMaxRetries = 2;
    [Tooltip("取得／回避の成否メッセージを見せる秒数。")]
    [SerializeField, Min(0f)] private float collectSuccessPauseSeconds = 0.4f;
    [Tooltip("失敗したあと、次のアイテム／障害物が出てくるまでの待ち時間（秒）。取得・回避で共通。")]
    [SerializeField, Min(0f)] private float retryDelaySeconds = 1.2f;
    [Tooltip("操作ガイドの表示先。未割り当てなら文字は出ない（機能自体は動く）。")]
    [SerializeField] private TextMeshProUGUI interactiveHintText;
    [Tooltip("矢印記号は多くのフォントに無いため、文字で書くのを推奨。")]
    [SerializeField] private string collectHintMessage = "キーで動かしてアイテムを取ろう！";
    [SerializeField] private string collectRetryMessage = "とりのがした… もういちど！";
    [SerializeField] private string collectSuccessMessage = "nice!";
    [Tooltip("アイテム／障害物がプレイヤーのX座標からこの距離だけ左へ流れたら『通過した』とみなす。")]
    [SerializeField, Min(0.05f)] private float collectMissMargin = 0.5f;

    [Header("Interactive Avoid (prototype)")]
    [Tooltip("障害物の回避ステップをプレイヤー操作にする。false で従来どおりの自動演示。")]
    [SerializeField] private bool interactiveAvoid = true;
    [Tooltip("1回の試行でプレイヤーがよけ切れなかった場合に打ち切るまでの秒数。")]
    [SerializeField, Min(1f)] private float playerAvoidTimeout = 8f;
    [Tooltip("ぶつかったときに再挑戦させる回数。使い切ったら自動演示（お手本）へ。")]
    [SerializeField, Min(0)] private int avoidMaxRetries = 2;
    [Tooltip("回避の成否メッセージ／リトライ前に見せる秒数。")]
    [SerializeField, Min(0f)] private float avoidResultPauseSeconds = 0.4f;
    [SerializeField] private string avoidHintMessage = "しょうがいぶつが くる！レーンをかえて よけよう";
    [SerializeField] private string avoidRetryMessage = "ぶつかった… もういちど！";
    [SerializeField] private string avoidSuccessMessage = "よけた！";

    [Header("Interactive QTE (prototype)")]
    [Tooltip("QTE ステップにガイド文を出す。QTE の入力自体は元から実操作。false でガイドなし。")]
    [SerializeField] private bool explainQte = true;
    [Tooltip("QTE 突破メッセージを見せる秒数。")]
    [SerializeField, Min(0f)] private float qteResultPauseSeconds = 0.5f;
    [SerializeField] private string qteApproachMessage = "おおきな しょうがいぶつ！ ぶつかると ボタンれんだ";
    [Tooltip("QTE 中に出すガイド。空にすると QTE 側の表示だけになる。")]
    [SerializeField] private string qteActiveMessage = "ボタンを ひょうじの じゅんに おそう！ まちがえても OK";
    [SerializeField] private string qteSuccessMessage = "とっぱ！";

    private bool tutorialRunning;
    private bool skipRequested;
    private PlayerAnimator playerAnimator;
    private Tween startTween;
    private Coroutine autoTargetRoutine;
    private Coroutine loadingCompleteRoutine;
    private Coroutine startSignalRoutine;
    private bool startSignalSent;

    private enum AutoTargetMode
    {
        Collect,
        Avoid,
        Qte
    }

    private void Start()
    {
        if (runOnStart)
        {
            StartTutorial();
        }
    }

    public void StartTutorial()
    {
        if (tutorialRunning) return;
        StartCoroutine(TutorialRoutine());
    }

    private IEnumerator TutorialRoutine()
    {
        tutorialRunning = true;
        startSignalSent = false;

        if (playerMove == null)
        {
            playerMove = FindFirstObjectByType<PlayerMove>();
        }

        if (itemSpawner == null)
        {
            itemSpawner = FindFirstObjectByType<ItemSpawner>();
        }

        if (gameManager == null)
        {
            gameManager = GameManager.instance;
        }

        if (bgmController == null)
        {
            bgmController = FindFirstObjectByType<BGMController>();
        }

        if (udpReceiver == null)
        {
            udpReceiver = FindFirstObjectByType<ChildUdpReceiver>();
        }

        if (playerAnimator == null && playerMove != null)
        {
            playerAnimator = playerMove.GetComponent<PlayerAnimator>()
                             ?? playerMove.GetComponentInChildren<PlayerAnimator>()
                             ?? FindFirstObjectByType<PlayerAnimator>();
        }

        if (playerMove == null || itemSpawner == null)
        {
            Debug.LogError("TutorialFlow: Missing PlayerMove or ItemSpawner.");
            tutorialRunning = false;
            yield break;
        }

        if (disableGameManagerDuringTutorial && gameManager != null)
        {
            gameManager.SetTutorialMode(true);
        }

        itemSpawner.SpawnEnabled = false;
        playerMove.SetInputEnabled(false);
        playerMove.SetAutoDrive(true);
        playerMove.SetAutoTargetX(null);
        playerMove.SetAutoHorizontalSpeed(playerMove.horizontalMoveSpeed);

        ClearActiveItems();

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(false);
            countdownText.text = string.Empty;
        }

        if (startText != null)
        {
            startText.gameObject.SetActive(false);
            startText.text = string.Empty;
        }

        if (initialDelay > 0f)
        {
            yield return new WaitForSecondsRealtime(initialDelay);
        }

        // 演示フェーズ（skipKey ホールドでスキップ可能。監視はこの区間のみ）
        skipRequested = false;
        Coroutine skipWatch = enableHoldToSkip ? StartCoroutine(WatchForSkipRoutine()) : null;

        yield return RunAutoCollect();
        yield return RunAutoAvoid();
        ClearActiveItems();
        yield return RunQteStep();

        if (!skipRequested && afterQteDelay > 0f)
        {
            yield return new WaitForSecondsRealtime(afterQteDelay);
        }

        if (skipWatch != null)
        {
            StopCoroutine(skipWatch);
        }

        if (skipRequested)
        {
            HandleSkipCleanup();
        }

        // スキップ時もカウントダウンは実行する。
        // 親機への LOADING_COMPLETE 送信（NotifyParentStartShown）とゲームBGM開始が
        // RunCountdown 内にあり、飛ばすと親機が遷移できなくなるため。
        yield return RunCountdown();

        playerMove.SetInputEnabled(true);
        playerMove.SetAutoDrive(false);
        playerMove.SetAutoTargetX(null);

        itemSpawner.SpawnEnabled = true;
        itemSpawner.RestartSchedule();

        if (disableGameManagerDuringTutorial && gameManager != null)
        {
            gameManager.SetTutorialMode(false);
            gameManager.ResetScoreAndFever();
        }

        HideInteractiveHint();
        tutorialRunning = false;
    }

    private IEnumerator WatchForSkipRoutine()
    {
        while (!skipRequested)
        {
            float held = 0f;
            while (!skipRequested && Keyboard.current != null && Keyboard.current[skipKey].isPressed)
            {
                held += Time.unscaledDeltaTime;
                if (held >= holdToSkipSeconds)
                {
                    skipRequested = true;
                    Debug.Log("[TutorialFlow] Skip requested (hold key).");
                    yield break;
                }
                yield return null;
            }
            yield return null;
        }
    }

    // スキップ確定時の後始末。演示用コルーチンは skipRequested チェックで自然終了するため
    // ここでは外部で走っているものだけを止める。
    private void HandleSkipCleanup()
    {
        if (autoTargetRoutine != null)
        {
            StopCoroutine(autoTargetRoutine);
            autoTargetRoutine = null;
        }

        // 進行中の Huge QTE を成功扱いで終了（Time.timeScale / QTE UI / ダメージロックを復帰）
        if (QTEManager.Instance != null && QTEManager.Instance.IsQteActive)
        {
            QTEManager.RegisterHugeQteSuccess();
        }

        ClearActiveItems();
        HideInteractiveHint();
        if (playerMove != null)
        {
            playerMove.SetAutoTargetX(null);
        }
    }

    private IEnumerator RunAutoCollect()
    {
        ClearActiveItems();
        List<GameObject> items = tutorialItems != null && tutorialItems.Count > 0
            ? tutorialItems
            : new List<GameObject>();

        for (int i = 0; i < items.Count && !skipRequested; i++)
        {
            GameObject prefab = items[i];
            if (prefab == null) continue;

            yield return CollectOneItem(prefab);
        }

        HideInteractiveHint();
    }

    private IEnumerator CollectOneItem(GameObject prefab)
    {
        int attempts = interactiveCollect ? (1 + Mathf.Max(0, collectMaxRetries)) : 0;
        bool collected = false;

        for (int attempt = 0; attempt < attempts && !collected && !skipRequested; attempt++)
        {
            GameObject item = null;
            if (!itemSpawner.TrySpawnByPrefab(prefab, out item) || item == null)
            {
                yield return new WaitForSecondsRealtime(0.2f);
                continue;
            }

            ClearActiveItemsExcept(item);
            yield return WaitForItemToEnterScreen(item, collectTimeout);
            if (skipRequested) yield break;

            // --- プレイヤーが取る番 ---
            playerMove.SetAutoTargetX(null);
            playerMove.SetAutoDrive(false);
            playerMove.SetInputEnabled(true);
            ShowInteractiveHint(attempt == 0 ? collectHintMessage : collectRetryMessage);

            float waited = 0f;
            while (!skipRequested
                   && item != null && item.activeInHierarchy
                   && !IsItemPastPlayer(item)
                   && waited < playerCollectTimeout)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            collected = !skipRequested && (item == null || !item.activeInHierarchy);

            // 自動運転へ戻す
            playerMove.SetInputEnabled(false);
            playerMove.SetAutoDrive(true);

            if (collected && !skipRequested)
            {
                ShowInteractiveHint(collectSuccessMessage);
                if (collectSuccessPauseSeconds > 0f)
                {
                    yield return new WaitForSecondsRealtime(collectSuccessPauseSeconds);
                }
                ClearActiveItems();
                HideInteractiveHint();
            }
            else if (!skipRequested)
            {
                ClearActiveItems();
                if (attempt + 1 < attempts)
                {
                    // 取り逃しメッセージを出したまま、次が来るまで一拍おく
                    ShowInteractiveHint(collectRetryMessage);
                    if (retryDelaySeconds > 0f)
                    {
                        yield return new WaitForSecondsRealtime(retryDelaySeconds);
                    }
                }
                else
                {
                    HideInteractiveHint();
                }
            }
        }

        // 実操作で取れなかった / interactiveCollect=false → 従来の自動お手本
        if (!collected && !skipRequested)
        {
            GameObject item = null;
            if (!itemSpawner.TrySpawnByPrefab(prefab, out item) || item == null)
            {
                yield break;
            }

            ClearActiveItemsExcept(item);
            yield return WaitForItemToEnterScreen(item, collectTimeout);

            int laneIndex = GetNearestLaneIndex(item.transform.position.y);
            playerMove.SetAutoLane(laneIndex);

            yield return TrackAutoTargetToItem(item, collectTimeout, AutoTargetMode.Collect);
        }
    }

    private void ShowInteractiveHint(string message)
    {
        if (interactiveHintText == null) return;
        interactiveHintText.text = message;
        if (!interactiveHintText.gameObject.activeSelf)
        {
            interactiveHintText.gameObject.SetActive(true);
        }
    }

    private void HideInteractiveHint()
    {
        if (interactiveHintText == null) return;
        if (interactiveHintText.gameObject.activeSelf)
        {
            interactiveHintText.gameObject.SetActive(false);
        }
    }

    // アイテムがプレイヤーの左（＝もう取れない位置）まで流れたか
    private bool IsItemPastPlayer(GameObject item)
    {
        if (item == null || playerMove == null) return false;
        return item.transform.position.x < playerMove.transform.position.x - collectMissMargin;
    }

    private IEnumerator RunAutoAvoid()
    {
        if (tutorialObstacles == null || tutorialObstacles.Count == 0) yield break;

        ClearActiveItems();

        for (int i = 0; i < tutorialObstacles.Count && !skipRequested; i++)
        {
            GameObject prefab = tutorialObstacles[i];
            if (prefab == null) continue;

            yield return AvoidOneObstacle(prefab);
        }

        HideInteractiveHint();
    }

    private IEnumerator AvoidOneObstacle(GameObject prefab)
    {
        int attempts = interactiveAvoid ? (1 + Mathf.Max(0, avoidMaxRetries)) : 0;
        bool dodged = false;

        for (int attempt = 0; attempt < attempts && !dodged && !skipRequested; attempt++)
        {
            GameObject obstacle = null;
            if (!itemSpawner.TrySpawnByPrefab(prefab, out obstacle) || obstacle == null)
            {
                yield return new WaitForSecondsRealtime(0.2f);
                continue;
            }

            ClearActiveItemsExcept(obstacle);
            yield return WaitForItemToEnterScreen(obstacle, avoidTimeout);
            if (skipRequested) yield break;

            // 直前のダメージ演出が残っていたら終わるまで待つ（誤検知防止）
            while (!skipRequested && playerAnimator != null && playerAnimator.IsDamaging)
            {
                yield return null;
            }

            // --- プレイヤーがよける番 ---
            playerMove.SetAutoTargetX(null);
            playerMove.SetAutoDrive(false);
            playerMove.SetInputEnabled(true);
            ShowInteractiveHint(attempt == 0 ? avoidHintMessage : avoidRetryMessage);

            float waited = 0f;
            while (!skipRequested
                   && obstacle != null && obstacle.activeInHierarchy
                   && !IsItemPastPlayer(obstacle)
                   && !(playerAnimator != null && playerAnimator.IsDamaging)
                   && waited < playerAvoidTimeout)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            bool hit = !skipRequested
                       && ((obstacle == null || !obstacle.activeInHierarchy)
                           || (playerAnimator != null && playerAnimator.IsDamaging));
            dodged = !skipRequested && !hit && obstacle != null && IsItemPastPlayer(obstacle);

            // 自動運転へ戻す
            playerMove.SetInputEnabled(false);
            playerMove.SetAutoDrive(true);

            if (dodged && !skipRequested)
            {
                ShowInteractiveHint(avoidSuccessMessage);
                if (avoidResultPauseSeconds > 0f)
                {
                    yield return new WaitForSecondsRealtime(avoidResultPauseSeconds);
                }
                ClearActiveItems();
                HideInteractiveHint();
            }
            else if (!skipRequested)
            {
                ClearActiveItems();
                if (attempt + 1 < attempts)
                {
                    // 「ぶつかった」メッセージを出したまま、次が来るまで一拍おく
                    ShowInteractiveHint(avoidRetryMessage);
                    if (retryDelaySeconds > 0f)
                    {
                        yield return new WaitForSecondsRealtime(retryDelaySeconds);
                    }
                }
                else
                {
                    HideInteractiveHint();
                }
            }
        }

        // 実操作でよけ切れなかった / interactiveAvoid=false → 従来の自動お手本
        if (!dodged && !skipRequested)
        {
            GameObject obstacle = null;
            if (!itemSpawner.TrySpawnByPrefab(prefab, out obstacle) || obstacle == null)
            {
                yield break;
            }

            ClearActiveItemsExcept(obstacle);
            yield return WaitForItemToEnterScreen(obstacle, avoidTimeout);

            int obstacleLane = GetNearestLaneIndex(obstacle.transform.position.y);
            int safeLane = PickSafeLane(obstacleLane);
            playerMove.SetAutoLane(safeLane);

            yield return TrackAutoTargetToItem(obstacle, avoidTimeout, AutoTargetMode.Avoid);
        }
    }

    private IEnumerator RunQteStep()
    {
        bool qteDone = false;
        System.Action<bool> handler = success =>
        {
            if (success) qteDone = true;
        };
        QTEManager.HugeQteFinished += handler;

        // skipRequested で自然にループを抜けるので、-= handler は必ず実行され購読は残らない。
        while (!qteDone && !skipRequested)
        {
            ClearActiveItems();
            bool qteHintSwapped = false;

            GameObject huge = null;
            if (!itemSpawner.TrySpawnHugeObstacle(out huge) || huge == null)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                continue;
            }

            ClearActiveItemsExcept(huge);

            yield return WaitForItemToEnterScreen(huge, qteWaitTimeout);

            if (explainQte && !skipRequested)
            {
                ShowInteractiveHint(qteApproachMessage);
            }

            int laneIndex = GetNearestLaneIndex(huge.transform.position.y);
            playerMove.SetAutoLane(laneIndex);

            float timer = 0f;
            float nextUpdate = 0f;
            float interval = Mathf.Max(0.02f, autoTargetUpdateSeconds);

            while (!qteDone && !skipRequested && timer < qteWaitTimeout)
            {
                timer += Time.unscaledDeltaTime;
                qteHintSwapped = SwapQteHintIfActive(qteHintSwapped);
                if (autoHorizontalEnabled && huge != null && huge.activeInHierarchy && timer >= nextUpdate)
                {
                    nextUpdate = timer + interval;
                    UpdateAutoTargetForItem(huge, AutoTargetMode.Qte);
                }
                yield return null;
            }

            if (!qteDone && !skipRequested && QTEManager.Instance != null && QTEManager.Instance.IsQteActive)
            {
                qteHintSwapped = SwapQteHintIfActive(qteHintSwapped);
                while (!qteDone && !skipRequested && QTEManager.Instance != null && QTEManager.Instance.IsQteActive)
                {
                    yield return null;
                }
            }
        }

        QTEManager.HugeQteFinished -= handler;
        playerMove.SetAutoTargetX(null);

        if (explainQte && qteDone && !skipRequested)
        {
            ShowInteractiveHint(qteSuccessMessage);
            if (qteResultPauseSeconds > 0f)
            {
                yield return new WaitForSecondsRealtime(qteResultPauseSeconds);
            }
        }

        HideInteractiveHint();
    }

    // QTE が始まった瞬間に一度だけガイド文を切り替える（qteActiveMessage が空なら隠す）。戻り値＝切替済みか
    private bool SwapQteHintIfActive(bool alreadySwapped)
    {
        if (alreadySwapped || !explainQte || skipRequested) return alreadySwapped;
        if (QTEManager.Instance == null || !QTEManager.Instance.IsQteActive) return false;

        if (string.IsNullOrEmpty(qteActiveMessage))
        {
            HideInteractiveHint();
        }
        else
        {
            ShowInteractiveHint(qteActiveMessage);
        }
        return true;
    }

    private IEnumerator RunCountdown()
    {
        int start = Mathf.Max(1, countdownFrom);
        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(true);
            for (int i = start; i > 0; i--)
            {
                countdownText.text = i.ToString();
                yield return new WaitForSecondsRealtime(countdownStepSeconds);
            }
            countdownText.gameObject.SetActive(false);

            if (startText != null)
            {
                PlayTutorialBgmIfNeeded();
                startText.gameObject.SetActive(true);
                startText.text = "Start";

                RectTransform rect = startText.rectTransform;
                rect.localScale = startScaleFrom;
                startTween?.Kill();
                startTween = rect.DOScale(startScaleTo, startTweenSeconds)
                    .SetEase(startEase)
                    .SetUpdate(true)
                    .OnComplete(NotifyParentStartShown);

                if (startSignalRoutine != null)
                {
                    StopCoroutine(startSignalRoutine);
                }
                startSignalRoutine = StartCoroutine(EnsureStartSignalAfterSeconds(startTweenSeconds));

                // ここでStartの表示時間分待機している間に、親機側はMotherLoadを抜けてシーン遷移を開始します
                yield return new WaitForSecondsRealtime(goDisplaySeconds);
                startText.gameObject.SetActive(false);
                yield break;
            }

            // startTextがNullだった場合の安全策
            NotifyParentStartShown();
            yield return new WaitForSecondsRealtime(goDisplaySeconds);
            yield break;
        }

        // countdownText自体がセットされていない場合の安全策
        NotifyParentStartShown();
        float total = start * Mathf.Max(0f, countdownStepSeconds) + Mathf.Max(0f, goDisplaySeconds);
        if (total > 0f)
        {
            yield return new WaitForSecondsRealtime(total);
        }
    }

    private void NotifyParentStartShown()
    {
        if (startSignalSent) return;
        startSignalSent = true;

        if (udpReceiver == null)
        {
            udpReceiver = ChildUdpReceiver.instance != null
                ? ChildUdpReceiver.instance
                : FindFirstObjectByType<ChildUdpReceiver>();
        }

        if (udpReceiver != null)
        {
            udpReceiver.SendState("LOADING_COMPLETE");
            Debug.Log("[TutorialFlow] Start shown — sent LOADING_COMPLETE to parent.");
            if (loadingCompleteRoutine != null)
            {
                StopCoroutine(loadingCompleteRoutine);
            }
            loadingCompleteRoutine = StartCoroutine(ResendLoadingComplete());
        }
        else
        {
            Debug.LogWarning("[TutorialFlow] Start shown but ChildUdpReceiver not found — LOADING_COMPLETE not sent.");
        }
    }

    private IEnumerator ResendLoadingComplete()
    {
        const int repeatCount = 4;
        const float intervalSeconds = 0.2f;

        for (int i = 0; i < repeatCount; i++)
        {
            yield return new WaitForSecondsRealtime(intervalSeconds);
            if (udpReceiver != null)
            {
                udpReceiver.SendState("LOADING_COMPLETE");
            }
        }
    }

    private IEnumerator EnsureStartSignalAfterSeconds(float seconds)
    {
        if (seconds > 0f)
        {
            yield return new WaitForSecondsRealtime(seconds);
        }
        NotifyParentStartShown();
    }

    private void PlayTutorialBgmIfNeeded()
    {
        if (bgmController == null) return;

        bgmController.PlayBGM();
    }

    private int GetNearestLaneIndex(float y)
    {
        if (itemSpawner == null || itemSpawner.lanesY == null || itemSpawner.lanesY.Length == 0) return 0;

        int bestIndex = 0;
        float bestDist = Mathf.Abs(itemSpawner.lanesY[0] - y);
        for (int i = 1; i < itemSpawner.lanesY.Length; i++)
        {
            float dist = Mathf.Abs(itemSpawner.lanesY[i] - y);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private int PickSafeLane(int obstacleLane)
    {
        if (itemSpawner == null || itemSpawner.lanesY == null || itemSpawner.lanesY.Length == 0) return obstacleLane;

        int laneCount = itemSpawner.lanesY.Length;
        if (laneCount == 1) return obstacleLane;

        for (int i = 0; i < laneCount; i++)
        {
            int index = (obstacleLane + 1 + i) % laneCount;
            if (index != obstacleLane) return index;
        }

        return obstacleLane;
    }

    private void ClearActiveItems()
    {
        ClearActiveItemsExcept(null);
    }

    private void ClearActiveItemsExcept(GameObject keep)
    {
        ItemPool pool = ItemPool.Instance;
        Item[] items = FindObjectsByType<Item>(FindObjectsSortMode.None);
        foreach (Item item in items)
        {
            if (item == null) continue;
            GameObject obj = item.gameObject;
            if (obj == null || !obj.activeInHierarchy) continue;
            if (keep != null && obj == keep) continue;

            if (pool != null)
            {
                pool.ReturnToPool(obj);
            }
            else
            {
                obj.SetActive(false);
            }
        }
    }

    private IEnumerator WaitForItemToDeactivate(GameObject item, float timeout)
    {
        float timer = 0f;
        while (!skipRequested && item != null && item.activeInHierarchy && timer < timeout)
        {
            timer += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private IEnumerator TrackAutoTargetToItem(GameObject item, float timeout, AutoTargetMode mode)
    {
        if (!autoHorizontalEnabled || playerMove == null || item == null)
        {
            yield return WaitForItemToDeactivate(item, timeout);
            yield break;
        }

        if (autoTargetRoutine != null)
        {
            StopCoroutine(autoTargetRoutine);
        }

        autoTargetRoutine = StartCoroutine(UpdateAutoTargetRoutine(item, timeout, mode));
        yield return autoTargetRoutine;
        autoTargetRoutine = null;
        playerMove.SetAutoTargetX(null);
    }

    private IEnumerator UpdateAutoTargetRoutine(GameObject item, float timeout, AutoTargetMode mode)
    {
        float timer = 0f;
        float interval = Mathf.Max(0.02f, autoTargetUpdateSeconds);
        float nextUpdate = 0f;

        while (!skipRequested && item != null && item.activeInHierarchy && timer < timeout)
        {
            timer += Time.unscaledDeltaTime;
            if (timer >= nextUpdate)
            {
                nextUpdate = timer + interval;
                UpdateAutoTargetForItem(item, mode);
            }

            yield return null;
        }
    }

    private void UpdateAutoTargetForItem(GameObject item, AutoTargetMode mode)
    {
        if (playerMove == null || item == null)
        {
            return;
        }

        float targetX = item.transform.position.x;
        if (mode == AutoTargetMode.Avoid)
        {
            float playerX = playerMove.transform.position.x;
            float dir = playerX <= targetX ? -1f : 1f;
            targetX = playerX + dir * autoHorizontalRange;
            playerMove.SetAutoTargetX(targetX);
            return;
        }

        Camera cam = Camera.main;
        float centerX = cam != null ? cam.transform.position.x : 0f;
        if (Mathf.Abs(targetX - centerX) <= autoCollectCenterRange)
        {
            playerMove.SetAutoTargetX(targetX);
        }
        else
        {
            playerMove.SetAutoTargetX(null);
        }
    }

    private IEnumerator WaitForItemToEnterScreen(GameObject item, float timeout)
    {
        float timer = 0f;
        while (!skipRequested && item != null && item.activeInHierarchy && timer < timeout)
        {
            if (IsItemInsideScreen(item))
            {
                yield break;
            }

            timer += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private bool IsItemInsideScreen(GameObject item)
    {
        if (item == null) return false;

        Camera cam = Camera.main;
        if (cam == null) return true;

        float halfHeight = cam.orthographicSize;
        float halfWidth = halfHeight * cam.aspect;
        float rightEdgeX = cam.transform.position.x + halfWidth;
        float leftEdgeX = cam.transform.position.x - halfWidth;

        float x = item.transform.position.x;
        return x <= rightEdgeX && x >= leftEdgeX;
    }
}