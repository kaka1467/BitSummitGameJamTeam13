using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Unity.Profiling;

public class TutorialFlow : MonoBehaviour
{
    // ── 計測用マーカー（Profilerで処理時間と呼び出し回数を見る。動作は変更しない）──
    private static readonly ProfilerMarker s_tutorialStartupMarker =
        new ProfilerMarker("TutorialFlow.StartupSync");
    private static readonly ProfilerMarker s_mainPlayStartPrepMarker =
        new ProfilerMarker("TutorialFlow.MainPlayStartPrep");
    private static readonly ProfilerMarker s_clearActiveItemsMarker =
        new ProfilerMarker("TutorialFlow.ClearActiveItems");

    [Header("References")]
    [SerializeField] private PlayerMove playerMove;
    [SerializeField] private ItemSpawner itemSpawner;
    [SerializeField] private GameManager gameManager;
    // 既存UDP GameObject（ルート）への参照。実行時にそのルート上のHostを解決する。
    [SerializeField] private GameObject udpReceiver;

    private ChildUdpRuntimeHost _host;   // 実行時解決したHost（Inspector割当なし・非シリアライズ）
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
    [Tooltip("skipKey（キーボード）またはゲームパッドの skipGamepadButton を holdToSkipSeconds 秒ホールドすると演示フェーズをスキップする。")]
    [SerializeField] private bool enableHoldToSkip = true;
    [SerializeField] private Key skipKey = Key.K;
    [Tooltip("子機の実機はゲームパッド操作のため、キーボードに加えてゲームパッドのボタンでもスキップできるようにする。")]
    [SerializeField] private bool allowGamepadSkip = true;
    [Tooltip("スキップに使うゲームパッドのボタン。South = Xbox系のA（QTEの'A'と同じボタン）。")]
    [SerializeField] private GamepadButton skipGamepadButton = GamepadButton.South;
    [SerializeField, Min(0.05f)] private float holdToSkipSeconds = 1f;

    [Header("Skip UI")]
    [SerializeField] private TextMeshProUGUI skipHintText;
    [SerializeField] private string skipHintMessage = "Kキー長押しでスキップ";
    [SerializeField] private string skipHintMessageJa = "Aボタン長押しでスキップ";

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
    [SerializeField] private string collectHintMessageJa = "←→↑↓  アイテムをとろう！";
    [SerializeField] private string collectRetryMessage = "とりのがした… もういちど！";
    [SerializeField] private string collectRetryMessageJa = "いてて… もういちど！";
    [SerializeField] private string collectSuccessMessage = "nice!";
    [SerializeField] private string collectSuccessMessageJa = "ナイス！";
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
    [SerializeField] private string avoidHintMessageJa = "あぶない！";
    [SerializeField] private string avoidRetryMessage = "ぶつかった… もういちど！";
    [SerializeField] private string avoidRetryMessageJa = "いてて… もういちど！";
    [SerializeField] private string avoidSuccessMessage = "よけた！";
    [SerializeField] private string avoidSuccessMessageJa = "よけた！";

    [Header("Interactive QTE (prototype)")]
    [Tooltip("QTE ステップにガイド文を出す。QTE の入力自体は元から実操作。false でガイドなし。")]
    [SerializeField] private bool explainQte = true;
    [Tooltip("QTE 突破メッセージを見せる秒数。")]
    [SerializeField, Min(0f)] private float qteResultPauseSeconds = 0.5f;
    [SerializeField] private string qteApproachMessage = "おおきな しょうがいぶつ！ ぶつかると ボタンれんだ";
    [SerializeField] private string qteApproachMessageJa = "ぶつかる！";
    [Tooltip("QTE 中に出すガイド。空にすると QTE 側の表示だけになる。")]
    [SerializeField] private string qteActiveMessage = "ボタンを ひょうじの じゅんに おそう！ まちがえても OK";
    [SerializeField] private string qteActiveMessageJa = "ボタンをおせ！";
    [SerializeField] private string qteSuccessMessage = "とっぱ！";
    [SerializeField] private string qteSuccessMessageJa = "ナイス！";

    [Header("Tutorial Slide Overlay (Resources/Tutorial/Child)")]
    [SerializeField] private float slideMinDisplaySeconds = 0.25f;
    [Tooltip("シーン上で位置・サイズを調整したImageがあればここに割り当てます（未割り当て時は自動生成）。")]
    [SerializeField] private Image tutorialSlideOverlay;
    [Tooltip("スライド下部の次へ案内テキスト（未割り当て時は自動生成）。")]
    [SerializeField] private TextMeshProUGUI tutorialSlidePromptText;
    [SerializeField] private Vector2 slideAnchorMin = new Vector2(0.06f, 0.12f);
    [SerializeField] private Vector2 slideAnchorMax = new Vector2(0.94f, 0.90f);
    [SerializeField] private Vector2 slideAnchoredPosition = Vector2.zero;
    [SerializeField] private Vector2 slideSizeDelta = Vector2.zero;
    [SerializeField] private Vector2 promptAnchorMin = new Vector2(0.1f, -0.11f);
    [SerializeField] private Vector2 promptAnchorMax = new Vector2(0.9f, -0.01f);
    [SerializeField] private float promptFontSize = 28f;

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

        using (s_tutorialStartupMarker.Auto())
        {
            StartCoroutine(TutorialRoutine());
        }
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

        // 子機Hostを解決する（指定UDPルート → Running → 初期化済みHost）。
        ResolveHost();
        if (_host != null)
        {
            _host.KeepTutorialInterruptScreen = false;
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

        // 【独立チュートリアルモード仕様】
        // EASY / NORMAL / HARD のときはチュートリアルを行わず、カウントダウンから即本編を開始する。
        if (!GameDifficulty.IncludeTutorial)
        {
            skipRequested = true;
            HideSkipHint();
            HideTutorialSlide();

            yield return RunCountdown();

            using (s_mainPlayStartPrepMarker.Auto())
            {
                ClearActiveItems();

                playerMove.SetInputEnabled(true);
                playerMove.SetAutoDrive(false);
                playerMove.SetAutoTargetX(null);

                itemSpawner.SpawnEnabled = true;
                itemSpawner.RestartSchedule();

                if (gameManager != null)
                {
                    gameManager.SetTutorialMode(false);
                    gameManager.ResetScoreAndFever();
                    gameManager.MarkMainPlayStarted();
                }
            }

            HideInteractiveHint();
            tutorialRunning = false;
            yield break;
        }

        // ── TUTORIAL モード専用フロー ──
        skipRequested = false;

        if (skipHintText != null)
        {
            skipHintText.text = Msg(skipHintMessage, skipHintMessageJa);
            skipHintText.gameObject.SetActive(enableHoldToSkip);
        }

        Coroutine skipWatch = enableHoldToSkip ? StartCoroutine(WatchForSkipRoutine()) : null;

        // 1) 良いアイテム説明スライド（ボタン入力で次へ）→ 良いアイテム回収
        yield return ShowTutorialSlideStep("Tutorial/Child/tutorial_gooditems");
        HideTutorialSlide();
        yield return RunAutoCollect();
        ClearActiveItems();

        // 2) 悪いアイテム＆障害物アイテム説明スライド（2枚連続・それぞれボタン入力で次へ）→ 悪い・障害物アイテム回避
        yield return ShowTutorialSlideStep("Tutorial/Child/tutorial_baditems");
        yield return ShowTutorialSlideStep("Tutorial/Child/tutorial_obstacle_lane");
        HideTutorialSlide();
        yield return RunAutoAvoid();
        ClearActiveItems();

        // 3) 鷹（ボス・QTE）説明スライド（2枚連続・それぞれボタン入力で次へ）→ 鷹が出てきて倒す
        yield return ShowTutorialSlideStep("Tutorial/Child/tutorial_QTE1");
        yield return ShowTutorialSlideStep("Tutorial/Child/tutorial_QTE2");
        HideTutorialSlide();
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

        // 4) 鷹撃破後：スコアを0にリセットし、子機を中断画面にして親機チュートリアルへ移行する
        HideSkipHint();
        HideTutorialSlide();
        HideInteractiveHint();
        ClearActiveItems();

        playerMove.SetInputEnabled(false);
        playerMove.SetAutoDrive(false);
        playerMove.SetAutoTargetX(null);
        itemSpawner.SpawnEnabled = false;

        if (gameManager != null)
        {
            gameManager.ResetScoreAndFever();
            gameManager.score = 0;
        }

        ChildUdpRuntimeHost host = ResolveHost();
        if (host != null)
        {
            host.KeepTutorialInterruptScreen = true;
            if (host.Notifications != null)
            {
                host.Notifications.SetSleepLocked(true);
            }
        }

        // 親機へ LOADING_COMPLETE を送信し、親機を ParentLoading → ParentGameScene（親機チュートリアル）へ遷移させる
        NotifyParentStartShown();

        bool isSolo = host == null || host.IsSoloPlayActive || host.Link == null || !host.Link.IsConnected;
        if (isSolo)
        {
            // 子機ソロスタート時は親機が接続されていないため、中断画面を確認後にボタン押下でスコア0のゲームクリア画面へ遷移する
            ShowInteractiveHint(Msg(
                "Child Tutorial Complete! Press A / Space / Enter to finish Tutorial (Score 0)",
                "子機チュートリアル完了！（中断画面） ボタンを押すとゲームクリア画面へ進みます"));
            yield return WaitForSlideAdvanceInput();
            HideInteractiveHint();

            if (host != null)
            {
                host.KeepTutorialInterruptScreen = false;
                host.Notifications?.SetSleepLocked(false);
            }

            if (gameManager != null)
            {
                gameManager.ResetScoreAndFever();
                gameManager.score = 0;
                gameManager.TriggerResult(GameManager.ResultType.TimeUp);
            }
        }

        tutorialRunning = false;
    }

    [ContextMenu("子機チュートリアルUIをシーン上に生成（Unity内で配置・サイズ調整用）")]
    public Image EnsureTutorialSlideOverlay()
    {
        if (tutorialSlideOverlay != null)
        {
            if (tutorialSlidePromptText == null)
                tutorialSlidePromptText = tutorialSlideOverlay.GetComponentInChildren<TextMeshProUGUI>(true);
            return tutorialSlideOverlay;
        }

        Canvas canvas = null;
        if (countdownText != null) canvas = countdownText.GetComponentInParent<Canvas>();
        if (canvas == null && interactiveHintText != null) canvas = interactiveHintText.GetComponentInParent<Canvas>();
        if (canvas == null) canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) return null;

        Transform existing = canvas.transform.Find("ChildTutorialSlideOverlay");
        if (existing != null)
        {
            tutorialSlideOverlay = existing.GetComponent<Image>();
            Transform promptTr = existing.Find("SlidePromptText");
            if (promptTr != null) tutorialSlidePromptText = promptTr.GetComponent<TextMeshProUGUI>();
            return tutorialSlideOverlay;
        }

        GameObject slideObj = new GameObject("ChildTutorialSlideOverlay", typeof(RectTransform), typeof(Image));
        slideObj.transform.SetParent(canvas.transform, false);
        if (skipHintText != null && skipHintText.transform.parent == canvas.transform)
        {
            slideObj.transform.SetSiblingIndex(Mathf.Max(0, skipHintText.transform.GetSiblingIndex()));
        }

        RectTransform rt = slideObj.GetComponent<RectTransform>();
        rt.anchorMin = slideAnchorMin;
        rt.anchorMax = slideAnchorMax;
        rt.anchoredPosition = slideAnchoredPosition;
        rt.sizeDelta = slideSizeDelta;

        tutorialSlideOverlay = slideObj.GetComponent<Image>();
        tutorialSlideOverlay.preserveAspect = true;
        tutorialSlideOverlay.raycastTarget = true;

        GameObject promptObj = new GameObject("SlidePromptText", typeof(RectTransform), typeof(TextMeshProUGUI));
        promptObj.transform.SetParent(slideObj.transform, false);
        RectTransform promptRt = promptObj.GetComponent<RectTransform>();
        promptRt.anchorMin = promptAnchorMin;
        promptRt.anchorMax = promptAnchorMax;
        promptRt.offsetMin = Vector2.zero;
        promptRt.offsetMax = Vector2.zero;

        tutorialSlidePromptText = promptObj.GetComponent<TextMeshProUGUI>();
        if (interactiveHintText != null && interactiveHintText.font != null)
        {
            tutorialSlidePromptText.font = interactiveHintText.font;
        }
        else if (countdownText != null && countdownText.font != null)
        {
            tutorialSlidePromptText.font = countdownText.font;
        }
        tutorialSlidePromptText.fontSize = promptFontSize;
        tutorialSlidePromptText.alignment = TextAlignmentOptions.Center;
        tutorialSlidePromptText.color = new Color(1f, 0.96f, 0.72f, 1f);
        tutorialSlidePromptText.enableWordWrapping = false;
        tutorialSlidePromptText.raycastTarget = false;

        slideObj.SetActive(false);
        return tutorialSlideOverlay;
    }

    private IEnumerator ShowTutorialSlideStep(string resourcePath)
    {
        if (skipRequested) yield break;

        Sprite sp = Resources.Load<Sprite>(resourcePath);
        if (sp == null) yield break;

        Image overlay = EnsureTutorialSlideOverlay();
        if (overlay == null) yield break;

        overlay.sprite = sp;
        if (tutorialSlidePromptText != null)
        {
            tutorialSlidePromptText.text = Msg(
                "Press A / Enter / Space / Click to continue",
                "Aボタン / Enter / Space / クリック で次へ");
        }
        overlay.gameObject.SetActive(true);

        yield return WaitForSlideAdvanceInput();
    }

    private IEnumerator WaitForSlideAdvanceInput()
    {
        float minWait = Mathf.Max(0.15f, slideMinDisplaySeconds);
        float elapsed = 0f;
        while (!skipRequested && elapsed < minWait)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        // 前フレームからの押しっぱなしが解除されるのを一度待つ
        while (!skipRequested && IsSlideAdvancePressedThisFrameOrHeld(checkHeldOnly: true))
        {
            yield return null;
        }

        while (!skipRequested)
        {
            if (IsSlideAdvancePressedThisFrameOrHeld(checkHeldOnly: false))
            {
                yield break;
            }
            yield return null;
        }
    }

    private static bool IsSlideAdvancePressedThisFrameOrHeld(bool checkHeldOnly)
    {
        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (checkHeldOnly)
            {
                if (kb.spaceKey.isPressed || kb.enterKey.isPressed || kb.numpadEnterKey.isPressed || kb.zKey.isPressed)
                    return true;
            }
            else
            {
                if (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame ||
                    kb.numpadEnterKey.wasPressedThisFrame || kb.zKey.wasPressedThisFrame)
                    return true;
            }
        }

        foreach (Gamepad pad in Gamepad.all)
        {
            if (pad == null) continue;
            if (checkHeldOnly)
            {
                if (pad.buttonSouth.isPressed || pad.buttonEast.isPressed || pad.startButton.isPressed)
                    return true;
            }
            else
            {
                if (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame || pad.startButton.wasPressedThisFrame)
                    return true;
            }
        }

        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            if (checkHeldOnly)
            {
                if (mouse.leftButton.isPressed) return true;
            }
            else
            {
                if (mouse.leftButton.wasPressedThisFrame) return true;
            }
        }

        return false;
    }

    private void HideTutorialSlide()
    {
        if (tutorialSlideOverlay != null && tutorialSlideOverlay.gameObject.activeSelf)
        {
            tutorialSlideOverlay.gameObject.SetActive(false);
        }
    }

    private IEnumerator WatchForSkipRoutine()
    {
        while (!skipRequested)
        {
            float held = 0f;
            while (!skipRequested && IsSkipInputHeld())
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

    private bool IsSkipInputHeld()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard[skipKey].isPressed)
        {
            return true;
        }

        if (allowGamepadSkip)
        {
            foreach (Gamepad pad in Gamepad.all)
            {
                if (pad[skipGamepadButton].isPressed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void HandleSkipCleanup()
    {
        if (autoTargetRoutine != null)
        {
            StopCoroutine(autoTargetRoutine);
            autoTargetRoutine = null;
        }

        if (QTEManager.Instance != null && QTEManager.Instance.IsQteActive)
        {
            QTEManager.RegisterHugeQteSuccess();
        }

        ClearActiveItems();
        HideInteractiveHint();
        HideSkipHint();
        HideTutorialSlide();
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

            playerMove.SetAutoTargetX(null);
            playerMove.SetAutoDrive(false);
            playerMove.SetInputEnabled(true);
            ShowInteractiveHint(attempt == 0 ? Msg(collectHintMessage, collectHintMessageJa) : Msg(collectRetryMessage, collectRetryMessageJa));

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

            playerMove.SetInputEnabled(false);
            playerMove.SetAutoDrive(true);

            if (collected && !skipRequested)
            {
                ShowInteractiveHint(Msg(collectSuccessMessage, collectSuccessMessageJa));
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
                    ShowInteractiveHint(Msg(collectRetryMessage, collectRetryMessageJa));
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

    private static string Msg(string english, string japanese)
    {
        if (string.IsNullOrEmpty(english)) return english;
        return GameLanguage.Pick(japanese, english);
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

    private void HideSkipHint()
    {
        if (skipHintText != null && skipHintText.gameObject.activeSelf)
        {
            skipHintText.gameObject.SetActive(false);
        }
    }

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

            while (!skipRequested && playerAnimator != null && playerAnimator.IsDamaging)
            {
                yield return null;
            }

            playerMove.SetAutoTargetX(null);
            playerMove.SetAutoDrive(false);
            playerMove.SetInputEnabled(true);
            ShowInteractiveHint(attempt == 0 ? Msg(avoidHintMessage, avoidHintMessageJa) : Msg(avoidRetryMessage, avoidRetryMessageJa));

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

            playerMove.SetInputEnabled(false);
            playerMove.SetAutoDrive(true);

            if (dodged && !skipRequested)
            {
                ShowInteractiveHint(Msg(avoidSuccessMessage, avoidSuccessMessageJa));
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
                    ShowInteractiveHint(Msg(avoidRetryMessage, avoidRetryMessageJa));
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
                ShowInteractiveHint(Msg(qteApproachMessage, qteApproachMessageJa));
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
            ShowInteractiveHint(Msg(qteSuccessMessage, qteSuccessMessageJa));
            if (qteResultPauseSeconds > 0f)
            {
                yield return new WaitForSecondsRealtime(qteResultPauseSeconds);
            }
        }

        HideInteractiveHint();
    }

    private bool SwapQteHintIfActive(bool alreadySwapped)
    {
        if (alreadySwapped || !explainQte || skipRequested) return alreadySwapped;
        if (QTEManager.Instance == null || !QTEManager.Instance.IsQteActive) return false;

        if (string.IsNullOrEmpty(Msg(qteActiveMessage, qteActiveMessageJa)))
        {
            HideInteractiveHint();
        }
        else
        {
            ShowInteractiveHint(Msg(qteActiveMessage, qteActiveMessageJa));
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

                yield return new WaitForSecondsRealtime(goDisplaySeconds);
                startText.gameObject.SetActive(false);
                yield break;
            }

            NotifyParentStartShown();
            yield return new WaitForSecondsRealtime(goDisplaySeconds);
            yield break;
        }

        NotifyParentStartShown();
        float total = start * Mathf.Max(0f, countdownStepSeconds) + Mathf.Max(0f, goDisplaySeconds);
        if (total > 0f)
        {
            yield return new WaitForSecondsRealtime(total);
        }
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

        ChildUdpRuntimeHost found = FindFirstObjectByType<ChildUdpRuntimeHost>();
        if (found != null && found.IsConfigured) _host = found;
        return _host;
    }

    private static string BuildLoadingCompleteMessage(ChildUdpRuntimeHost host)
    {
        string playId = host != null && host.Session != null ? host.Session.PlaySessionId : "";
        return string.IsNullOrEmpty(playId) ? "LOADING_COMPLETE" : $"LOADING_COMPLETE:{playId}";
    }

    private void NotifyParentStartShown()
    {
        if (startSignalSent) return;
        startSignalSent = true;

        ChildUdpRuntimeHost host = ResolveHost();

        if (host != null)
        {
            string message = BuildLoadingCompleteMessage(host);
            Debug.Log($"[StartTrace][子機] TutorialFlow.NotifyParentStartShown — {message} 送信 (startSignalSent={startSignalSent}, hostId={host.GetInstanceID()}, scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}')", this);
            host.SendState(message);
            Debug.Log($"[TutorialFlow] Start shown — sent {message} to parent.");
            if (loadingCompleteRoutine != null)
            {
                StopCoroutine(loadingCompleteRoutine);
            }
            loadingCompleteRoutine = StartCoroutine(ResendLoadingComplete());
        }
        else
        {
            Debug.LogWarning("[TutorialFlow] Start shown but ChildUdpRuntimeHost not found — LOADING_COMPLETE not sent.");
            Debug.LogWarning($"[StartTrace][子機] TutorialFlow.NotifyParentStartShown 失敗 — 子機Hostが見つからず LOADING_COMPLETE を送れません (startSignalSent={startSignalSent}, scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}')", this);
        }
    }

    private IEnumerator ResendLoadingComplete()
    {
        const int repeatCount = 4;
        const float intervalSeconds = 0.2f;

        for (int i = 0; i < repeatCount; i++)
        {
            yield return new WaitForSecondsRealtime(intervalSeconds);
            ChildUdpRuntimeHost host = ResolveHost();
            if (host != null)
            {
                host.SendState(BuildLoadingCompleteMessage(host));
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
        using var clearScope = s_clearActiveItemsMarker.Auto();

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
