using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// QTEの終了状態。成功・失敗・中断を区別する。
///  ・Success   … 入力完了など、正常に成功して終了した。
///  ・Failure   … 失敗として終了した（現状のQTEは失敗終了を生成しない。開始できなかった場合は Item 側が処理する）。
///  ・Cancelled … 結果確定（捕獲など）のために中断した。
///                追加ダメージ・HugeQteFinished による次回予約は行わない。
/// </summary>
public enum QteOutcome
{
    Success,
    Failure,
    Cancelled,
}

public class QTEManager : MonoBehaviour
{
    public static QTEManager Instance { get; private set; }

    /// <summary>
    /// Huge QTE の終了通知（従来どおり bool）。Cancelled では発火しない（中断を成功/失敗として扱わない）。
    /// 成功時のみ true。ItemSpawner は true のときだけ次回巨大障害物を予約する。
    /// </summary>
    public static event Action<bool> HugeQteFinished;

    // 「画像＋テキスト」1セット分
    [Serializable]
    private class QteSlot
    {
        public Image image;             // ボタン画像
        public TextMeshProUGUI label;   // A/B/X/Y の文字
    }

    [Header("QTE UI（シーン上に配置したものを割り当てる）")]
    [SerializeField] private GameObject qteBack;               // 背景画像（QTEBack）
    [Tooltip("QTE中だけ表示する、スコア（獲得ボーナス）の背景。QTEが始まると表示し、終わる・中断すると隠す。")]
    [SerializeField] private GameObject qteScoreBack;
    [SerializeField] private TextMeshProUGUI titleText;        // 「PUSH BUTTON!」
    [SerializeField] private TextMeshProUGUI timerText;        // 残り秒数
    [SerializeField] private QteSlot[] slots = new QteSlot[7]; // 画像＋テキストのセット（要素数＝QTEの文字数）

    [Header("ボタン画像")]
    [SerializeField] private Sprite slotNormalSprite;          // 押す前
    [SerializeField] private Sprite slotPressedSprite;         // 押した後

    [SerializeField] private PlayerAnimator playerAnimator;
    [SerializeField] private Animator playerAnimatorComponent;

    [Header("QTEの時間設定")]
    [SerializeField] private float timeLimitSeconds = 5f;

    [Header("文字色の設定")]
    [SerializeField] private Color enteredColor = Color.green;
    [SerializeField] private Color remainingColor = Color.black;

    [Header("QTE成功報酬（本プレイの入力クリア時のみ）")]
    [Tooltip("ミス0回（一発成功）で本プレイのQTEを入力でクリアしたときの報酬（点）。")]
    [SerializeField] private int successBaseScore = 5000;
    [Tooltip("ミス1回（誤入力または制限時間切れ）ごとに減らす報酬（点）。")]
    [SerializeField] private int successMissPenalty = 1000;
    [Tooltip("報酬の下限（点）。ミスが重なってもクリアすればこの点は必ず得られる。")]
    [SerializeField] private int successMinScore = 1000;

    [Header("クリア時のSE")]
    [Tooltip("QTEを入力でクリアしたときに鳴らすSE。未設定なら鳴らさない。")]
    [SerializeField] private AudioClip clearSe;
    [SerializeField, Range(0f, 1f)] private float clearSeVolume = 1f;

    [Header("報酬表示（獲得予定の報酬と、ミスによる減少）")]
    [Tooltip("今クリアしたら得られる報酬を出すテキスト。未割り当てなら、残り秒数(timerText)と同じフォントで実行時に作る。")]
    [SerializeField] private TextMeshProUGUI rewardText;
    [Tooltip("自動で作る場合の位置。残り秒数(timerText)の位置からのずれ。")]
    [SerializeField] private Vector2 rewardTextOffset = new Vector2(0f, 90f);
    [SerializeField] private string rewardLabel = "BONUS ";
    [SerializeField] private Color rewardColor = new Color(1f, 0.85f, 0.2f, 1f);
    [Tooltip("ミスで報酬が減ったときに出す「-1000」などの色。")]
    [SerializeField] private Color rewardDecreaseColor = new Color(1f, 0.25f, 0.25f, 1f);
    [Tooltip("報酬が減ったとき、報酬テキストが減少色で光る秒数。")]
    [SerializeField, Min(0f)] private float rewardFlashSeconds = 0.5f;
    [Tooltip("「-1000」が下へ流れて消えるまでの秒数。")]
    [SerializeField, Min(0.1f)] private float rewardDecreaseSeconds = 0.9f;

    [Tooltip("「-1000」を出すテキスト。未割り当てなら、残り秒数(timerText)と同じフォントで実行時に作り、" +
             "位置は『報酬テキストからのずれ』（下の Reward Decrease Offset）で決める。" +
             "シーン上に自分で置いたテキストを割り当てた場合は、そのテキストを置いた位置から出る。")]
    [SerializeField] private TextMeshProUGUI rewardDecreaseText;
    [Tooltip("自動で作る場合の、「-1000」の開始位置。報酬テキストの位置からのずれ（Canvas座標。上が正）。")]
    [SerializeField] private Vector2 rewardDecreaseOffset = new Vector2(0f, -50f);
    [Tooltip("「-1000」が、表示されている間に動く量（Canvas座標）。既定は下へ40。0にすると動かない。")]
    [SerializeField] private Vector2 rewardDecreaseMove = new Vector2(0f, -40f);

    private bool rewardDecreaseAssignedInScene;     // 「-1000」のテキストをシーン上で割り当てたか（その場合は置いた位置を使う）
    private Vector2 rewardDecreaseScenePosition;    // 割り当てたテキストの、シーン上の位置
    private float rewardDecreaseAge = -1f;     // 負なら非表示
    private float rewardFlashLeft;
    private Vector2 rewardDecreaseStart;

    private bool isQteActive;
    private string currentSequence = string.Empty;
    private int currentIndex;
    private float remainingTime;
    private Action<QteOutcome> onFinished;
    private bool hasStoredPlayerUpdateMode;
    private AnimatorUpdateMode storedPlayerUpdateMode;

    // 実行中QTEのミス回数（誤入力＋タイムアウト）。新しいQTEを正常に開始したときだけ0に戻す。
    private int missCount;
    // 実行中QTEが報酬対象（本プレイ）かどうか。QTE開始時に確定し、付与または中断時に消費する。
    private bool successRewardPending;

    // 7個配置されたQTEスロットの初期座標キャッシュ（難易度に応じて4個/6個/7個を常に中央揃え配置するため）
    private bool hasCachedSlotLayout;
    private Vector2[] initialSlotImagePositions;
    private Vector2[] initialSlotLabelPositions;
    private bool[] slotLabelIsIndependent;
    private float cachedSlotCenterX;
    private float cachedSlotStepX = 140f;

    public bool IsQteActive => isQteActive;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        CacheInitialSlotPositions();
        ResolvePlayerAnimator();
        ResolveAnimatorComponent();
        SetQteVisible(false);
    }

    private void Update()
    {
        if (!isQteActive) return;

        // 寝たふり中は子機全体が止まっている。QTEの制限時間（実時間）も進めない（M-Q02）。
        if (ChildGamePause.IsPaused) return;

        UpdateRewardAnimation();

        if (timeLimitSeconds > 0f)
        {
            remainingTime -= Time.unscaledDeltaTime;
            if (remainingTime <= 0f)
            {
                HandleQteMiss();
                RegenerateSequence();
                return;
            }
        }

        HandleQteInput();
        if (!isQteActive) return;
        UpdateQteText();
    }

    public bool StartHugeObstacleQte(Action<QteOutcome> finishedCallback)
    {
        if (isQteActive) return false;

        if (slots == null || slots.Length == 0)
        {
            Debug.LogError("QTEManager: slots が未設定です。Inspectorで画像＋テキストのセットを割り当ててください。");
            return false;
        }

        missCount = 0;

        GameManager gameManager = GameManager.instance;
        successRewardPending = gameManager != null && !gameManager.IsTutorialMode;

        RegenerateSequence();
        onFinished = finishedCallback;
        isQteActive = true;

        ResolvePlayerAnimator();
        if (playerAnimator != null) playerAnimator.SetDamageLock(false);

        ResolveAnimatorComponent();
        SetPlayerAnimatorUnscaled(true);

        Time.timeScale = 0f;
        SetQteVisible(true);
        UpdateQteText();
        return true;
    }

    private void HandleQteInput()
    {
        if (PlayerInputLock.IsLocked || ChildGamePause.IsPaused)
        {
            return;
        }

        if (Keyboard.current == null && Gamepad.current == null) return;

        char? input = GetInputChar();
        if (!input.HasValue) return;

        if (input.Value == currentSequence[currentIndex])
        {
            currentIndex++;
            if (currentIndex >= currentSequence.Length)
            {
                FinishQte(QteOutcome.Success, true);
            }
            return;
        }

        HandleQteMiss();
        RegenerateSequence();
    }

    private void HandleQteMiss()
    {
        int rewardBefore = GetCurrentReward();
        missCount++;

        int rewardAfter = GetCurrentReward();
        if (successRewardPending && rewardAfter < rewardBefore)
            ShowRewardDecrease(rewardBefore - rewardAfter);

        if (playerAnimator != null)
        {
            playerAnimator.SetDamageLock(false);
        }

        TriggerPlayerDamage();
    }

    private void RegenerateSequence()
    {
        // 【D-F03 / D-R05】難易度に応じたスロット数（EASY=4, NORMAL=6, HARD=7）と制限時間（EASY=6.0s, NORMAL=5.0s, HARD=4.2s）を適用
        int targetCount = Mathf.Clamp(GameDifficulty.ChildQteSlotCount, 1, slots.Length);
        currentSequence = GenerateSequence(targetCount);
        currentIndex = 0;
        remainingTime = GameDifficulty.ChildQteTimeLimitSeconds > 0f
            ? GameDifficulty.ChildQteTimeLimitSeconds
            : timeLimitSeconds;
        UpdateSlotLayoutForActiveCount(targetCount);
        UpdateQteText();
    }

    /// <summary>
    /// シーン上に配置された7個のスロットの初期座標・中心X・スロット間隔をキャッシュする。
    /// </summary>
    private void CacheInitialSlotPositions()
    {
        if (hasCachedSlotLayout || slots == null || slots.Length == 0) return;

        int n = slots.Length;
        initialSlotImagePositions = new Vector2[n];
        initialSlotLabelPositions = new Vector2[n];
        slotLabelIsIndependent = new bool[n];

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float firstX = 0f;
        float lastX = 0f;
        int validCount = 0;

        for (int i = 0; i < n; i++)
        {
            QteSlot slot = slots[i];
            if (slot == null || slot.image == null) continue;

            RectTransform imgRt = slot.image.rectTransform;
            Vector2 imgPos = imgRt.anchoredPosition;
            initialSlotImagePositions[i] = imgPos;

            if (validCount == 0) firstX = imgPos.x;
            lastX = imgPos.x;
            if (imgPos.x < minX) minX = imgPos.x;
            if (imgPos.x > maxX) maxX = imgPos.x;
            validCount++;

            if (slot.label != null)
            {
                RectTransform lblRt = slot.label.rectTransform;
                initialSlotLabelPositions[i] = lblRt.anchoredPosition;
                slotLabelIsIndependent[i] = !lblRt.IsChildOf(imgRt);
            }
        }

        if (validCount > 0)
        {
            cachedSlotCenterX = (minX + maxX) * 0.5f;
            if (validCount > 1)
            {
                float step = (lastX - firstX) / (validCount - 1);
                if (Mathf.Abs(step) > 1f)
                    cachedSlotStepX = step;
            }
            hasCachedSlotLayout = true;
        }
    }

    /// <summary>
    /// 有効なスロット数（EASY=4, NORMAL=6, HARD=7）に応じて、表示されるスロット群（0..activeCount-1）が
    /// 常に画面中央（元の7スロット全体の中心X）を基準に左右対称・等間隔で並ぶように座標を再配置する。
    /// </summary>
    private void UpdateSlotLayoutForActiveCount(int activeCount)
    {
        if (slots == null || slots.Length == 0) return;
        CacheInitialSlotPositions();
        if (!hasCachedSlotLayout) return;

        int count = Mathf.Clamp(activeCount, 1, slots.Length);
        float halfSpan = (count - 1) * 0.5f;

        for (int i = 0; i < count; i++)
        {
            QteSlot slot = slots[i];
            if (slot == null || slot.image == null) continue;

            float targetX = cachedSlotCenterX + (i - halfSpan) * cachedSlotStepX;
            Vector2 baseImgPos = initialSlotImagePositions[i];
            float deltaX = targetX - baseImgPos.x;

            slot.image.rectTransform.anchoredPosition = new Vector2(targetX, baseImgPos.y);

            if (slot.label != null && slotLabelIsIndependent[i])
            {
                Vector2 baseLblPos = initialSlotLabelPositions[i];
                slot.label.rectTransform.anchoredPosition = new Vector2(baseLblPos.x + deltaX, baseLblPos.y);
            }
        }
    }

    private void TriggerPlayerDamage()
    {
        if (playerAnimator == null)
        {
            ResolvePlayerAnimator();
        }

        if (playerAnimator != null)
        {
            playerAnimator.PlayDamage();
        }
    }

    private void ResolvePlayerAnimator()
    {
        if (playerAnimator != null) return;

        GameObject player = GameObject.FindWithTag("Player");
        if (player == null) return;

        playerAnimator = player.GetComponent<PlayerAnimator>() ?? player.GetComponentInParent<PlayerAnimator>();
    }

    private void ResolveAnimatorComponent()
    {
        if (playerAnimatorComponent != null) return;

        if (playerAnimator != null)
        {
            playerAnimatorComponent = playerAnimator.GetComponent<Animator>();
        }
    }

    private char? GetInputChar()
    {
        var kb = Keyboard.current;
        var gp = Gamepad.current;

        if (kb != null)
        {
            if (kb.aKey.wasPressedThisFrame) return 'A';
            if (kb.bKey.wasPressedThisFrame) return 'B';
            if (kb.xKey.wasPressedThisFrame) return 'X';
            if (kb.yKey.wasPressedThisFrame) return 'Y';
        }

        if (gp != null)
        {
            if (gp.buttonSouth.wasPressedThisFrame) return 'A';
            if (gp.buttonEast.wasPressedThisFrame) return 'B';
            if (gp.buttonWest.wasPressedThisFrame) return 'X';
            if (gp.buttonNorth.wasPressedThisFrame) return 'Y';
        }

        return null;
    }

    private string GenerateSequence(int length)
    {
        const string letters = "ABXY";
        StringBuilder builder = new StringBuilder(length);

        for (int index = 0; index < length; index++)
        {
            builder.Append(letters[UnityEngine.Random.Range(0, letters.Length)]);
        }

        return builder.ToString();
    }

    public static void RegisterHugeQteSuccess()
    {
        if (Instance != null && Instance.isQteActive)
        {
            Instance.FinishQte(QteOutcome.Success, false);
        }
        else
        {
            HugeQteFinished?.Invoke(true);
        }
    }

    private void FinishQte(QteOutcome outcome, bool fromPlayerInput)
    {
        if (!isQteActive) return;

        isQteActive = false;
        Time.timeScale = 1f;
        SetQteVisible(false);

        int rewardScore = ConsumeSuccessReward(outcome, fromPlayerInput);

        if (outcome == QteOutcome.Success && fromPlayerInput) PlayClearSe();

        SetPlayerAnimatorUnscaled(false);

        if (playerAnimator != null) playerAnimator.SetDamageLock(false);

        Action<QteOutcome> callback = onFinished;
        onFinished = null;

        GrantSuccessReward(rewardScore);

        callback?.Invoke(outcome);
        HugeQteFinished?.Invoke(outcome == QteOutcome.Success);
    }

    private int ConsumeSuccessReward(QteOutcome outcome, bool fromPlayerInput)
    {
        bool eligible = successRewardPending;
        successRewardPending = false;

        if (!eligible || !fromPlayerInput || outcome != QteOutcome.Success) return 0;

        return GetCurrentReward();
    }

    private static void GrantSuccessReward(int amount)
    {
        if (amount <= 0) return;

        GameManager gameManager = GameManager.instance;
        if (gameManager == null) return;

        gameManager.AddScore(amount);
    }

    public bool CancelQteForResult()
    {
        if (!isQteActive) return false;

        isQteActive = false;
        successRewardPending = false;

        Action<QteOutcome> callback = onFinished;
        onFinished = null;

        SetQteVisible(false);
        SetPlayerAnimatorUnscaled(false);

        if (playerAnimator != null) playerAnimator.SetDamageLock(false);

        callback?.Invoke(QteOutcome.Cancelled);
        return true;
    }

    private void OnDestroy()
    {
        if (isQteActive)
        {
            isQteActive = false;
            onFinished = null;
            Time.timeScale = 1f;
        }
    }

    private void SetPlayerAnimatorUnscaled(bool enabled)
    {
        if (playerAnimatorComponent == null) return;

        if (enabled)
        {
            if (!hasStoredPlayerUpdateMode)
            {
                storedPlayerUpdateMode = playerAnimatorComponent.updateMode;
                hasStoredPlayerUpdateMode = true;
            }
            playerAnimatorComponent.updateMode = AnimatorUpdateMode.UnscaledTime;
            return;
        }

        if (hasStoredPlayerUpdateMode)
        {
            playerAnimatorComponent.updateMode = storedPlayerUpdateMode;
            hasStoredPlayerUpdateMode = false;
        }
    }

    private void PlayClearSe()
    {
        if (clearSe == null) return;

        if (AudioManager.Instance == null)
            new GameObject("AudioManager").AddComponent<AudioManager>();

        if (AudioManager.Instance != null)
            AudioManager.Instance.PlaySE(clearSe, clearSeVolume);
    }

    private int GetCurrentReward()
    {
        return Mathf.Max(successMinScore, successBaseScore - (missCount * successMissPenalty));
    }

    private void EnsureRewardUi()
    {
        if (rewardText == null && timerText != null)
            rewardText = CreateRewardText("QteRewardText", rewardTextOffset, 0.8f);

        if (rewardDecreaseText != null && !rewardDecreaseAssignedInScene && rewardDecreaseText.transform.parent != null
            && rewardDecreaseText.gameObject.name != "QteRewardDecreaseText")
        {
            rewardDecreaseAssignedInScene = true;
            rewardDecreaseScenePosition = rewardDecreaseText.rectTransform.anchoredPosition;
        }

        if (rewardDecreaseText == null && timerText != null)
        {
            rewardDecreaseText = CreateRewardText("QteRewardDecreaseText", rewardTextOffset, 0.7f);
            if (rewardDecreaseText != null) rewardDecreaseText.gameObject.SetActive(false);
        }
    }

    private TextMeshProUGUI CreateRewardText(string objectName, Vector2 offset, float sizeRatio)
    {
        RectTransform src = timerText.rectTransform;
        RectTransform parent = src.parent as RectTransform;
        if (parent == null) return null;

        var go = new GameObject(objectName, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = src.anchorMin;
        rect.anchorMax = src.anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(Mathf.Max(600f, src.sizeDelta.x * 3f), src.sizeDelta.y);
        rect.localScale = src.localScale;
        rect.anchoredPosition = src.anchoredPosition + offset;

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        text.font = timerText.font;
        text.fontSharedMaterial = timerText.fontSharedMaterial;
        text.fontSize = timerText.fontSize * sizeRatio;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        text.color = rewardColor;
        return text;
    }

    private void ShowRewardDecrease(int amount)
    {
        EnsureRewardUi();
        if (rewardDecreaseText == null || rewardText == null) return;

        rewardFlashLeft = rewardFlashSeconds;
        rewardDecreaseAge = 0f;
        rewardDecreaseStart = rewardDecreaseAssignedInScene
            ? rewardDecreaseScenePosition
            : rewardText.rectTransform.anchoredPosition + rewardDecreaseOffset;
        rewardDecreaseText.text = $"-{amount}";
        rewardDecreaseText.color = rewardDecreaseColor;
        rewardDecreaseText.rectTransform.anchoredPosition = rewardDecreaseStart;
        rewardDecreaseText.gameObject.SetActive(true);
    }

    private void UpdateRewardAnimation()
    {
        if (rewardText != null)
        {
            if (rewardFlashLeft > 0f)
            {
                rewardFlashLeft = Mathf.Max(0f, rewardFlashLeft - Time.unscaledDeltaTime);
                float k = (rewardFlashSeconds > 0f) ? rewardFlashLeft / rewardFlashSeconds : 0f;
                rewardText.color = Color.Lerp(rewardColor, rewardDecreaseColor, k);
            }
            else
            {
                rewardText.color = rewardColor;
            }
        }

        if (rewardDecreaseText != null && rewardDecreaseAge >= 0f)
        {
            rewardDecreaseAge += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(rewardDecreaseAge / rewardDecreaseSeconds);

            rewardDecreaseText.rectTransform.anchoredPosition = rewardDecreaseStart + rewardDecreaseMove * t;
            Color c = rewardDecreaseColor;
            c.a = 1f - t * t;
            rewardDecreaseText.color = c;

            if (t >= 1f)
            {
                rewardDecreaseAge = -1f;
                rewardDecreaseText.gameObject.SetActive(false);
            }
        }
    }

    private void UpdateQteText()
    {
        if (!isQteActive) return;
        if (string.IsNullOrEmpty(currentSequence)) return;

        if (rewardText != null)
        {
            rewardText.gameObject.SetActive(successRewardPending);
            if (successRewardPending) rewardText.text = rewardLabel + GetCurrentReward();
        }

        if (timerText != null)
        {
            bool showTimer = timeLimitSeconds > 0f;
            timerText.gameObject.SetActive(showTimer);
            if (showTimer) timerText.text = $"{Mathf.Max(0f, remainingTime):0.00}s";
        }

        for (int i = 0; i < slots.Length; i++)
        {
            QteSlot slot = slots[i];
            if (slot == null) continue;

            bool withinSequence = i < currentSequence.Length;
            if (slot.image != null)
            {
                slot.image.gameObject.SetActive(withinSequence);
            }
            if (slot.label != null && slotLabelIsIndependent != null && i < slotLabelIsIndependent.Length && slotLabelIsIndependent[i])
            {
                slot.label.gameObject.SetActive(withinSequence);
            }
            if (!withinSequence) continue;

            bool pressed = i < currentIndex;

            if (slot.label != null)
            {
                slot.label.text = currentSequence[i].ToString();
                slot.label.color = pressed ? enteredColor : remainingColor;
            }

            Sprite sprite = pressed ? slotPressedSprite : slotNormalSprite;
            if (slot.image != null && sprite != null)
            {
                slot.image.sprite = sprite;
            }
        }
    }

    private void SetQteVisible(bool visible)
    {
        if (qteBack != null) qteBack.SetActive(visible);
        if (qteScoreBack != null) qteScoreBack.SetActive(visible);
        if (titleText != null) titleText.gameObject.SetActive(visible);
        if (timerText != null) timerText.gameObject.SetActive(visible);

        if (visible)
        {
            EnsureRewardUi();
            rewardFlashLeft = 0f;
            rewardDecreaseAge = -1f;
            if (rewardText != null)
            {
                rewardText.color = rewardColor;
                rewardText.gameObject.SetActive(successRewardPending);
            }
        }
        else
        {
            if (rewardText != null) rewardText.gameObject.SetActive(false);
        }

        if (rewardDecreaseText != null) rewardDecreaseText.gameObject.SetActive(false);

        if (slots == null) return;
        for (int i = 0; i < slots.Length; i++)
        {
            QteSlot slot = slots[i];
            if (slot == null) continue;
            bool showSlot = visible && (string.IsNullOrEmpty(currentSequence) || i < currentSequence.Length);
            if (slot.image != null)
            {
                slot.image.gameObject.SetActive(showSlot);
            }
            if (slot.label != null && slotLabelIsIndependent != null && i < slotLabelIsIndependent.Length && slotLabelIsIndependent[i])
            {
                slot.label.gameObject.SetActive(showSlot);
            }
        }
    }
}
