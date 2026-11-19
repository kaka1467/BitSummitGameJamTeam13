using System.Collections;
using UnityEngine;

/// <summary>
/// ParentGameTimeManager：親機側のゲーム全体タイムリミットと時計表示を管理する。
///
/// 目的（P09 / D-Q01）：
///  ・ゲーム内時刻「夜8:00（20:00）スタート → 深夜0:00（24:00 = 00:00）到達でクリア」の進行を作る。
///  ・難易度別の実時間制限：EASY=4分(240秒) / NORMAL=6分(360秒) / HARD=8分(480秒)。
///  ・すべての難易度で同じ時計スケール（20:00 → 00:00 = 240分）を使用する。
/// </summary>
public class ParentGameTimeManager : MonoBehaviour
{
    // ── 時間設定 ──────────────────────────────────────────────────────────────
    [Header("時間設定")]
    [Tooltip("プレイ実時間の制限（秒）。EASY=240s(4分), NORMAL=360s(6分), HARD=480s(8分) が難易度から自動反映される。")]
    [SerializeField, Min(1f)] private float limitSeconds = 360f;

    [Tooltip("ゲーム内の開始時刻（時）。20 = 夜8時（20:00）。")]
    [SerializeField, Range(0, 23)] private int startHour = 20;

    [Tooltip("ゲーム内で進む総時間（分）。240分 = 4時間（20:00 → 24:00 = 00:00）。")]
    [SerializeField, Min(1f)] private float gameMinutesTotal = 240f;

    // ── 時計表示 ──────────────────────────────────────────────────────────────
    [Header("時計表示")]
    [Tooltip("時刻の表示形式。{0}=時(2桁), {1}=分(2桁)。ClockFacePresenter が GetClockText() の戻り値を表示する。")]
    [SerializeField] private string clockFormat = "{0:00}:{1:00}";

    // ── 終了設定 ──────────────────────────────────────────────────────────────
    [Header("タイムリミット")]
    [Tooltip("制限時間へ到達したら結果画面（TIME_UP）へ遷移する。")]
    [SerializeField] private bool enableTimeUp = true;

    [Tooltip("TIME_UP で遷移するシーン名。既存の親機クリア画面。")]
    [SerializeField] private string timeUpSceneName = "ParentGameClear";

    [Tooltip("遷移前にシーン名を SceneNameResolver で解決する（既存のタイトル復帰と同じ解決を使う）。")]
    [SerializeField] private bool resolveSceneName = false;

    // ── 開始前ディレイ ────────────────────────────────────────────────────────
    [Header("開始前ディレイ")]
    [Tooltip("本編開始前の待機（秒）。この秒数だけ 20:00 のまま止め、経過後にタイマーを開始する。\n" +
             "0 以下なら Start 直後に即時開始する。")]
    [SerializeField, Min(0f)] private float startDelaySeconds = 0f;

    // ── クリア演出（PA07：閉眼・暗転） ────────────────────────────────────────
    [Header("クリア演出（PA07：閉眼・暗転）")]
    [Tooltip("00:00（TIME_UP）到達時に、眠るようにまぶたが閉じて暗転してから結果画面へ遷移する演出を有効にする。")]
    [SerializeField] private bool enableClearSleepSequence = true;

    [Tooltip("クリア時にまぶたがゆっくり閉じる時間（秒）。")]
    [SerializeField, Min(0.1f)] private float clearEyelidCloseSeconds = 1.5f;

    [Tooltip("まぶたが閉じ切って完全暗転したあと、結果画面へ遷移するまでの保持時間（秒）。")]
    [SerializeField, Min(0f)] private float clearBlackoutHoldSeconds = 0.8f;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs;

    // ── 状態 ──────────────────────────────────────────────────────────────────
    private float _elapsed;
    private bool _timeUpFired;
    private bool _timerRunning;
    private bool _timerStopped;
    private MotherSuspicionSystem _suspicionSystem;
    private Coroutine _clearSequenceCoroutine;

    // ── 公開（読み取り専用） ──────────────────────────────────────────────────
    public static ParentGameTimeManager Instance { get; private set; }

    public float ProgressRate => Mathf.Clamp01(_elapsed / Mathf.Max(1f, limitSeconds));
    public float RemainingSeconds => Mathf.Max(0f, LimitSeconds - _elapsed);
    public bool IsTimeUp => _timeUpFired;

    public bool IsTimeUpOrReachedThisFrame =>
        _timeUpFired ||
        (_timerRunning && !_timerStopped && !IsCaptureConfirmed() &&
         (_elapsed >= LimitSeconds || _elapsed + Time.deltaTime >= LimitSeconds));

    public bool IsRunning => _timerRunning;
    public bool IsStopped => _timerStopped;

    public bool IsPlayFinished
    {
        get
        {
            if (_timeUpFired) return true;
            if (_timerStopped) return true;
            if (_timerRunning && _elapsed >= LimitSeconds) return true;
            WarnIfMissingReference();
            return IsResultConfirmed();
        }
    }

    private bool _missingRefWarned;

    private void WarnIfMissingReference()
    {
        if (_missingRefWarned) return;
        if (_suspicionSystem != null) return;

        _missingRefWarned = true;
        bool hasHost = ParentUdpRuntimeHost.Running != null;
        Debug.LogWarning(hasHost
            ? "[ParentGameTimeManager] 必須参照不足：MotherSuspicionSystem が見つかりません（通常シーンの設定不備の可能性）。捕獲状態の判定は Host 経由のみで継続します。"
            : "[ParentGameTimeManager] 必須参照不足：MotherSuspicionSystem も Host もありません（単体デバッグ相当）。捕獲状態は判定できません。", this);
    }

    private static bool _unavailableRejectLogged;

    public static void LogUnavailableRejectOnce()
    {
        if (_unavailableRejectLogged) return;
        _unavailableRejectLogged = true;
        Debug.LogWarning("[ParentGameTimeManager] 自動開始を拒否：ParentGameTimeManager が無い通常ゲームシーンです" +
                         "（判定不能）。開始可否を判定できないため、自動の親イベントは開始しません。" +
                         "シーンに ParentGameTimeManager を配置してください。");
    }

    public enum AutoStartDecision
    {
        Allow = 0,
        RejectPlayFinished = 1,
        RejectUnavailable = 2,
        AllowSoloDebug = 3,
    }

    private static MotherSuspicionSystem _sharedSuspicionSystem;
    private static bool _sharedSuspicionResolveAttempted;

    public static AutoStartDecision EvaluateAutoStart()
    {
        var manager = Instance;
        if (manager != null)
            return manager.IsPlayFinished ? AutoStartDecision.RejectPlayFinished : AutoStartDecision.Allow;

        if (IsFinishedByExistingInfo()) return AutoStartDecision.RejectPlayFinished;
        if (IsGameplaySceneNow()) return AutoStartDecision.RejectUnavailable;
        return AutoStartDecision.AllowSoloDebug;
    }

    private static bool IsFinishedByExistingInfo()
    {
        var host = ParentUdpRuntimeHost.Running;
        if (host != null && host.Session != null)
        {
            if (IsCaptureConfirmedForCurrentPlay(host.Session)) return true;
            if (host.Session.ResultProcessed) return true;
        }

        var s = ResolveSharedSuspicion();
        return s != null && (s.isCaught || s.HasPermanentGameOver);
    }

    private static MotherSuspicionSystem ResolveSharedSuspicion()
    {
        if (_sharedSuspicionSystem != null) return _sharedSuspicionSystem;
        if (_sharedSuspicionResolveAttempted) return null;
        _sharedSuspicionResolveAttempted = true;
        _sharedSuspicionSystem = Object.FindFirstObjectByType<MotherSuspicionSystem>();
        return _sharedSuspicionSystem;
    }

    private static bool IsGameplaySceneNow()
    {
        string active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

        var host = ParentUdpRuntimeHost.Running;
        if (host != null && host.Settings != null && !string.IsNullOrEmpty(host.Settings.GameplaySceneName))
            return ParentSceneBoundaryPolicy.IsGameplayScene(active, host.Settings.GameplaySceneName);

        return active == "ParentGameScene";
    }

    public void StartTimer()
    {
        if (_timerStopped)
        {
            Debug.Log("[ParentGameTimeManager] 結果確定済みのため StartTimer を無視します（時刻を固定したまま）。", this);
            return;
        }

        if (_timerRunning) return;

        _timerRunning = true;

        Debug.Log($"[ParentGameTimeManager] タイマー開始 — 難易度={GameDifficulty.DisplayName} / 制限={LimitSeconds:F0}s / 表示={GetClockText()}", this);
    }

    public void StopTimer()
    {
        if (_timerStopped) return;

        _timerRunning = false;
        _timerStopped = true;

        Debug.Log($"[ParentGameTimeManager] タイマー停止 — 時刻を固定（表示={GetClockText()}）", this);
    }

    public float ElapsedSeconds => _elapsed;
    public float LimitSeconds => Mathf.Max(1f, limitSeconds);

    /// <summary>
    /// 現在のゲーム内時刻を "HH:MM" で返す（24時間で折り返す）。
    ///  ・20:00 スタート、進行率 1.0 で 24:00（= 00:00）へ到達する。
    /// </summary>
    public string GetClockText()
    {
        float totalMinutes = startHour * 60f + ProgressRate * gameMinutesTotal;

        int totalMinutesInt = Mathf.FloorToInt(totalMinutes);
        int hour = ((totalMinutesInt / 60) % 24 + 24) % 24;
        int minute = ((totalMinutesInt % 60) + 60) % 60;

        return string.Format(clockFormat, hour, minute);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[ParentGameTimeManager] 重複するインスタンスを破棄します。", this);
            Destroy(gameObject);
            return;
        }

        Instance = this;
        ApplyDifficultyTimeSettings();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void ApplyDifficultyTimeSettings()
    {
        limitSeconds = GameDifficulty.TimeLimitSeconds;
        startHour = GameDifficulty.ClockStartHour;
        gameMinutesTotal = GameDifficulty.ClockGameMinutesTotal;
    }

    private void Start()
    {
        ApplyDifficultyTimeSettings();

        _elapsed = 0f;
        _timeUpFired = false;
        _timerRunning = false;
        _timerStopped = false;

        _suspicionSystem = Object.FindFirstObjectByType<MotherSuspicionSystem>();
        _missingRefWarned = false;

        _sharedSuspicionSystem = _suspicionSystem;
        _sharedSuspicionResolveAttempted = true;

        MotherApproachController.ResetTutorialHolds();

        // 【独立チュートリアルモード】親機チュートリアルを実行する
        if (GameDifficulty.IncludeTutorial)
        {
            Debug.Log("[ParentGameTimeManager] TUTORIAL モードで開始 — 親機チュートリアルシーケンスを起動します。", this);
            StartCoroutine(ParentTutorialRoutine());
            return;
        }

        if (showDebugLogs)
            Debug.Log($"[ParentGameTimeManager] 開始 | 難易度={GameDifficulty.DisplayName} | 制限={LimitSeconds:F1}s | 開始時刻={startHour:00}:00 | " +
                      $"ゲーム内総時間={gameMinutesTotal:F0}分 | 遷移先='{timeUpSceneName}' | " +
                      $"開始前ディレイ={startDelaySeconds:F2}s", this);

        if (startDelaySeconds > 0f)
            StartCoroutine(StartTimerAfterDelay());
        else
            StartTimer();
    }

    [Header("Parent Tutorial Slide Overlay (Resources/Tutorial/Parent)")]
    [Tooltip("シーン上で位置・サイズを調整したImageがあればここに割り当てます（未割り当て時は自動生成）。")]
    [SerializeField] private UnityEngine.UI.Image parentTutorialSlideImage;
    [Tooltip("スライド下部の案内テキスト（未割り当て時は自動生成）。")]
    [SerializeField] private TMPro.TextMeshProUGUI parentTutorialPromptText;
    [SerializeField] private int tutorialCanvasSortingOrder = 250;
    [SerializeField] private Vector2 tutorialSlideAnchorMin = Vector2.zero;
    [SerializeField] private Vector2 tutorialSlideAnchorMax = Vector2.one;
    [SerializeField] private Vector2 tutorialSlideAnchoredPosition = Vector2.zero;
    [SerializeField] private Vector2 tutorialSlideSizeDelta = Vector2.zero;
    [SerializeField] private Vector2 tutorialPromptAnchorMin = new Vector2(0.1f, 0.03f);
    [SerializeField] private Vector2 tutorialPromptAnchorMax = new Vector2(0.9f, 0.10f);
    [SerializeField] private float tutorialPromptFontSize = 32f;

    [ContextMenu("親機チュートリアルUIをシーン上に生成（Unity内で配置・サイズ調整用）")]
    public UnityEngine.UI.Image EnsureParentTutorialOverlay()
    {
        if (parentTutorialSlideImage != null)
        {
            if (parentTutorialPromptText == null)
                parentTutorialPromptText = parentTutorialSlideImage.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            return parentTutorialSlideImage;
        }

        GameObject existingCanvas = GameObject.Find("ParentTutorialOverlayCanvas");
        if (existingCanvas != null)
        {
            Transform slideTr = existingCanvas.transform.Find("TutorialSlideImage");
            if (slideTr != null)
            {
                parentTutorialSlideImage = slideTr.GetComponent<UnityEngine.UI.Image>();
                parentTutorialPromptText = slideTr.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                return parentTutorialSlideImage;
            }
        }

        GameObject canvasObj = new GameObject("ParentTutorialOverlayCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(UnityEngine.UI.CanvasScaler),
            typeof(UnityEngine.UI.GraphicRaycaster));

        Canvas canvas = canvasObj.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = tutorialCanvasSortingOrder;

        UnityEngine.UI.CanvasScaler scaler = canvasObj.GetComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject imgObj = new GameObject("TutorialSlideImage", typeof(RectTransform), typeof(UnityEngine.UI.Image));
        imgObj.transform.SetParent(canvasObj.transform, false);
        RectTransform imgRt = imgObj.GetComponent<RectTransform>();
        imgRt.anchorMin = tutorialSlideAnchorMin;
        imgRt.anchorMax = tutorialSlideAnchorMax;
        imgRt.anchoredPosition = tutorialSlideAnchoredPosition;
        imgRt.sizeDelta = tutorialSlideSizeDelta;

        parentTutorialSlideImage = imgObj.GetComponent<UnityEngine.UI.Image>();
        parentTutorialSlideImage.preserveAspect = true;
        parentTutorialSlideImage.raycastTarget = true;

        GameObject promptObj = new GameObject("TutorialPromptText", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        promptObj.transform.SetParent(imgObj.transform, false);
        RectTransform promptRt = promptObj.GetComponent<RectTransform>();
        promptRt.anchorMin = tutorialPromptAnchorMin;
        promptRt.anchorMax = tutorialPromptAnchorMax;
        promptRt.offsetMin = Vector2.zero;
        promptRt.offsetMax = Vector2.zero;

        parentTutorialPromptText = promptObj.GetComponent<TMPro.TextMeshProUGUI>();
        TMPro.TMP_Text anyTmp = Object.FindFirstObjectByType<TMPro.TMP_Text>();
        if (anyTmp != null && anyTmp.font != null)
            parentTutorialPromptText.font = anyTmp.font;
        parentTutorialPromptText.fontSize = tutorialPromptFontSize;
        parentTutorialPromptText.alignment = TMPro.TextAlignmentOptions.Center;
        parentTutorialPromptText.color = new Color(1f, 0.96f, 0.72f, 1f);
        parentTutorialPromptText.enableWordWrapping = false;
        parentTutorialPromptText.raycastTarget = false;

        imgObj.SetActive(false);
        return parentTutorialSlideImage;
    }

    private void ShowParentTutorialSlide(string resourcePath, string promptJa, string promptEn)
    {
        Sprite sp = Resources.Load<Sprite>(resourcePath);
        if (sp == null)
        {
            Debug.LogWarning($"[ParentGameTimeManager] Tutorial slide not found at Resources/{resourcePath}");
            return;
        }

        UnityEngine.UI.Image img = EnsureParentTutorialOverlay();
        if (img == null) return;

        img.sprite = sp;
        if (parentTutorialPromptText != null)
        {
            parentTutorialPromptText.text = GameLanguage.Pick(promptJa, promptEn);
        }
        img.gameObject.SetActive(true);
    }

    private void HideParentTutorialSlide()
    {
        if (parentTutorialSlideImage != null && parentTutorialSlideImage.gameObject.activeSelf)
        {
            parentTutorialSlideImage.gameObject.SetActive(false);
        }
    }

    private static IEnumerator WaitForParentTutorialButtonPress()
    {
        yield return new WaitForSecondsRealtime(0.25f);

        while (IsParentTutorialButtonDown(heldOnly: true))
            yield return null;

        while (!IsParentTutorialButtonDown(heldOnly: false))
            yield return null;
    }

    private static bool IsParentTutorialButtonDown(bool heldOnly)
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null)
        {
            if (heldOnly)
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

        foreach (var pad in UnityEngine.InputSystem.Gamepad.all)
        {
            if (pad == null) continue;
            if (heldOnly)
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

        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse != null)
        {
            if (heldOnly)
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

    private static bool IsPillowSensorPressed(SleepingController sleepingController)
    {
        if (sleepingController != null && sleepingController.IsSleeping)
            return true;

        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.spaceKey.isPressed)
            return true;

        foreach (var pad in UnityEngine.InputSystem.Gamepad.all)
        {
            if (pad != null && pad.buttonSouth.isPressed)
                return true;
        }

        return false;
    }

    private IEnumerator ParentTutorialRoutine()
    {
        yield return new WaitForSecondsRealtime(0.4f);

        MotherApproachWarning warningSystem = Object.FindFirstObjectByType<MotherApproachWarning>();
        SleepingController sleepingController = Object.FindFirstObjectByType<SleepingController>();
        if (_suspicionSystem == null)
            _suspicionSystem = Object.FindFirstObjectByType<MotherSuspicionSystem>();

        // 1) 怪しさメーターの説明スライド（ボタン入力で次へ）
        ShowParentTutorialSlide(
            "Tutorial/Parent/Tutorial01",
            "Aボタン / Enter / Space / クリック で次へ",
            "Press A / Enter / Space / Click to continue");
        yield return WaitForParentTutorialButtonPress();

        // 2) お母さんがどこから覗いてくるか（ドア・窓）の説明スライド（ボタン入力で次へ）
        ShowParentTutorialSlide(
            "Tutorial/Parent/Tutorial02",
            "Aボタン / Enter / Space / クリック で次へ",
            "Press A / Enter / Space / Click to continue");
        yield return WaitForParentTutorialButtonPress();
        HideParentTutorialSlide();

        // 3) 1キーのドア演出：お母さんがドアの前まで来て停止し、枕センサーを押すまでドアを開けない
        if (warningSystem != null)
        {
            MotherApproachController.ResetTutorialHolds();
            MotherApproachController.TutorialHoldBeforeDoorOpen = true;

            Debug.Log("[ParentGameTimeManager] 親機チュートリアル：1キードア演出を開始します");
            warningSystem.StartManualDoorWarningSequence();

            float waitDoorTimeout = 20f;
            float waitedDoor = 0f;
            while (!MotherApproachController.TutorialWaitingForPillowAtDoor &&
                   waitedDoor < waitDoorTimeout &&
                   warningSystem.isWarningActive)
            {
                waitedDoor += Time.deltaTime;
                yield return null;
            }

            // ドア前に到達したら枕センサー説明スライド（Tutorial03）を表示し、枕センサーが押されるまで待機
            ShowParentTutorialSlide(
                "Tutorial/Parent/Tutorial03",
                "枕センサー（または Space / Aボタン）を長押しして寝たふりをしよう！",
                "Hold the Pillow Sensor (or Space / A Button) to pretend to sleep!");

            while (!IsPillowSensorPressed(sleepingController))
            {
                yield return null;
            }

            // 枕センサーが押されたらスライドを閉じてドアを開けさせ、覗き〜帰宅まで完了させる
            HideParentTutorialSlide();
            MotherApproachController.TutorialHoldBeforeDoorOpen = false;

            while (warningSystem != null && warningSystem.isWarningActive)
            {
                yield return null;
            }
        }

        yield return new WaitForSeconds(0.8f);

        // 4) 4キーの庭演出：お母さんが庭の窓の前まで来て、枕センサーを押してやり過ごす
        if (warningSystem != null)
        {
            MotherApproachController.ResetTutorialHolds();
            MotherApproachController.TutorialHoldBeforeGardenPeek = true;

            Debug.Log("[ParentGameTimeManager] 親機チュートリアル：4キー庭演出を開始します");
            warningSystem.StartManualGardenPeekWarningSequence();

            float waitGardenTimeout = 20f;
            float waitedGarden = 0f;
            while (!MotherApproachController.TutorialWaitingForPillowAtGarden &&
                   waitedGarden < waitGardenTimeout &&
                   warningSystem.isWarningActive)
            {
                waitedGarden += Time.deltaTime;
                yield return null;
            }

            ShowParentTutorialSlide(
                "Tutorial/Parent/Tutorial03",
                "窓からお母さんが来た！枕センサー（または Space / Aボタン）を長押しして寝たふりをしよう！",
                "Mom is at the window! Hold the Pillow Sensor (or Space / A Button) to pretend to sleep!");

            while (!IsPillowSensorPressed(sleepingController))
            {
                yield return null;
            }

            HideParentTutorialSlide();
            MotherApproachController.TutorialHoldBeforeGardenPeek = false;

            while (warningSystem != null && warningSystem.isWarningActive)
            {
                yield return null;
            }
        }

        MotherApproachController.ResetTutorialHolds();
        HideParentTutorialSlide();

        // 5) 4キーの庭演出が終わったら、スコア0でゲームクリア画面へ遷移する
        Debug.Log("[ParentGameTimeManager] 親機チュートリアル完了 — スコア0でゲームクリア画面へ遷移します");
        GameDifficulty.RecordResultScore(false, 0);

        _timeUpFired = true;
        StopTimer();

        if (enableClearSleepSequence)
        {
            if (_clearSequenceCoroutine != null)
                StopCoroutine(_clearSequenceCoroutine);
            _clearSequenceCoroutine = StartCoroutine(TimeUpClearSequenceCoroutine());
        }
        else
        {
            StopAllEventsForTimeUp();
            TransitionToTimeUp();
        }
    }

    private IEnumerator StartTimerAfterDelay()
    {
        Debug.Log($"[ParentGameTimeManager] 本編開始前：{startDelaySeconds:F2}s 待機します（表示={GetClockText()}）", this);

        yield return new WaitForSeconds(startDelaySeconds);

        StartTimer();
    }

    private void Update()
    {
        if (_timeUpFired) return;

        if (!_timerStopped && IsResultConfirmed())
        {
            StopTimer();
            return;
        }

        if (!_timerRunning)
        {
            return;
        }

        _elapsed += Time.deltaTime;

        if (!enableTimeUp) return;

        if (_elapsed >= LimitSeconds)
        {
            if (IsCaptureConfirmed())
            {
                _timeUpFired = true;
                _elapsed = LimitSeconds;
                StopTimer();
                Debug.Log("[ParentGameTimeManager] 捕獲が確定しているため、TIME_UP では遷移しません（捕獲優先）。", this);
                return;
            }

            _timeUpFired = true;
            _elapsed = LimitSeconds;
            StopTimer();

            Debug.Log($"[ParentGameTimeManager] TIME UP — 制限時間 {LimitSeconds:F1}s 到達（表示={GetClockText()}）", this);

            if (enableClearSleepSequence)
            {
                if (_clearSequenceCoroutine != null)
                    StopCoroutine(_clearSequenceCoroutine);
                _clearSequenceCoroutine = StartCoroutine(TimeUpClearSequenceCoroutine());
            }
            else
            {
                StopAllEventsForTimeUp();
                TransitionToTimeUp();
            }
        }
    }

    private IEnumerator TimeUpClearSequenceCoroutine()
    {
        StopAllEventsForTimeUp();

        SleepVisionEffectController sleepVision = Object.FindFirstObjectByType<SleepVisionEffectController>();
        if (sleepVision != null && sleepVision.isActiveAndEnabled)
        {
            Debug.Log($"[ParentGameTimeManager] PA07 クリア閉眼・暗転演出を開始（閉眼={clearEyelidCloseSeconds:F2}s / 暗転保持={clearBlackoutHoldSeconds:F2}s）", this);
            yield return sleepVision.PlayClearSleepSequence(clearEyelidCloseSeconds, clearBlackoutHoldSeconds);
        }
        else
        {
            Debug.LogWarning("[ParentGameTimeManager] SleepVisionEffectController が見つからないため、待機後にクリア画面へ遷移します。", this);
            float fallbackWait = Mathf.Max(0f, clearEyelidCloseSeconds + clearBlackoutHoldSeconds);
            if (fallbackWait > 0f)
                yield return new WaitForSeconds(fallbackWait);
        }

        _clearSequenceCoroutine = null;
        TransitionToTimeUp();
    }

    private void StopAllEventsForTimeUp()
    {
        ParentUdpRuntimeHost.Running?.NotifyTimeUpFromParent();

        if (_suspicionSystem == null)
            _suspicionSystem = Object.FindFirstObjectByType<MotherSuspicionSystem>();

        if (_suspicionSystem != null)
        {
            _suspicionSystem.NotifyTimeUp();
        }
        else
        {
            MotherApproachWarning warning = Object.FindFirstObjectByType<MotherApproachWarning>();
            if (warning != null)
            {
                warning.NotifyPlayFinished();
                warning.StopWarningSequence();
                warning.StopAllMotherSounds();
            }

            MotherChoreController chore = Object.FindFirstObjectByType<MotherChoreController>();
            if (chore != null && chore.IsChoreActive)
                chore.AbortChore("TIME_UP");
        }

        SuspicionHeartbeat heartbeat = Object.FindFirstObjectByType<SuspicionHeartbeat>();
        if (heartbeat != null)
            heartbeat.enabled = false;

        SleepingController sleeping = Object.FindFirstObjectByType<SleepingController>();
        if (sleeping != null)
            sleeping.enabled = false;
    }

    private bool IsResultConfirmed()
    {
        var host = ParentUdpRuntimeHost.Running;
        if (host != null && host.Session != null)
        {
            if (IsCaptureConfirmedForCurrentPlay(host.Session)) return true;
            if (host.Session.ResultProcessed) return true;
        }

        return _suspicionSystem != null &&
               (_suspicionSystem.isCaught || _suspicionSystem.HasPermanentGameOver);
    }

    private static bool IsCaptureConfirmedForCurrentPlay(ParentPlaySession session)
    {
        if (session == null) return false;
        if (string.IsNullOrEmpty(session.PlaySessionId)) return false;
        return session.PlaySessionId == session.CaptureConfirmedPlayId;
    }

    private bool IsCaptureConfirmed()
    {
        var host = ParentUdpRuntimeHost.Running;
        if (host != null && host.Session != null &&
            IsCaptureConfirmedForCurrentPlay(host.Session))
            return true;

        return _suspicionSystem != null &&
               (_suspicionSystem.isCaught || _suspicionSystem.HasPermanentGameOver);
    }

    private void TransitionToTimeUp()
    {
        if (string.IsNullOrEmpty(timeUpSceneName))
        {
            Debug.LogWarning("[ParentGameTimeManager] timeUpSceneName が空のため遷移できません。", this);
            return;
        }

        Debug.Log($"[ParentGameTimeManager] TIME_UP により '{timeUpSceneName}' へ遷移します。", this);

        if (resolveSceneName)
            ParentSceneTransitionRunner.LoadSceneNowResolved(timeUpSceneName);
        else
            ParentSceneTransitionRunner.LoadSceneNow(timeUpSceneName);
    }
}
