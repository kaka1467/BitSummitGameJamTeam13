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

    public bool IsQteActive => isQteActive;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        ResolvePlayerAnimator();
        ResolveAnimatorComponent();
        SetQteVisible(false);
    }

    private void Update()
    {
        if (!isQteActive) return;

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

        // ミス回数は「新しいQTEを正常に開始できたときだけ」0に戻す（開始を拒否した場合は実行中QTEの値を変えない）。
        // 再出題（RegenerateSequence）では戻さない＝同じQTE内のミスを数え続ける。
        missCount = 0;

        // 報酬の可否は開始時に確定する。GameManager が無い場合は本プレイと判断せず、チュートリアル中も対象外。
        GameManager gameManager = GameManager.instance;
        successRewardPending = gameManager != null && !gameManager.IsTutorialMode;

        RegenerateSequence();
        onFinished = finishedCallback;
        isQteActive = true;

        // QTE中でもダメージ後はRunへ戻す
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
        if (PlayerInputLock.IsLocked)
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
                // 本プレイの入力完了（強制成功・ブースト回避・チュートリアル・中断とは区別する）
                FinishQte(QteOutcome.Success, true);
            }
            return;
        }

        HandleQteMiss();
        RegenerateSequence();
    }

    private void HandleQteMiss()
    {
        // 誤入力とタイムアウトの共通処理。ミス回数はここだけで数える（同じミスを二重に数えない）。
        // ミス時にスコアは直接減らさない（ダメージ演出のみ。減額は成功報酬の計算でのみ行う）。
        missCount++;

        if (playerAnimator != null)
        {
            playerAnimator.SetDamageLock(false);
        }

        TriggerPlayerDamage();
    }

    private void RegenerateSequence()
    {
        // 文字数は配置したスロットの数に合わせる
        currentSequence = GenerateSequence(slots.Length);
        currentIndex = 0;
        remainingTime = timeLimitSeconds;
        UpdateQteText();
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

        // キーボード入力
        if (kb != null)
        {
            if (kb.aKey.wasPressedThisFrame) return 'A';
            if (kb.bKey.wasPressedThisFrame) return 'B';
            if (kb.xKey.wasPressedThisFrame) return 'X';
            if (kb.yKey.wasPressedThisFrame) return 'Y';
        }

        // ゲームパッド入力
        if (gp != null)
        {
            // buttonSouth=A, buttonEast=B, buttonWest=X, buttonNorth=Y (一般的な配置)
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

    // 外部（アイテムなど）から「Huge QTE を成功扱いで終了した」と登録するためのAPI
    public static void RegisterHugeQteSuccess()
    {
        if (Instance != null && Instance.isQteActive)
        {
            // 強制成功（ブースト回避・チュートリアルのスキップ）は入力完了ではないため報酬対象にしない。
            Instance.FinishQte(QteOutcome.Success, false);
        }
        else
        {
            HugeQteFinished?.Invoke(true);
        }
    }

    /// <summary>
    /// QTEを終了する（成功／失敗）。中断は CancelQteForResult を使う（ここからは呼ばない）。
    /// 何度呼んでも1回だけ実行される（2回目以降は何もしない）。
    /// 報酬は「本プレイの入力完了」による成功時だけ、GameManager.AddScore 経由で1回だけ付与する。
    /// </summary>
    private void FinishQte(QteOutcome outcome, bool fromPlayerInput)
    {
        if (!isQteActive) return;

        isQteActive = false;
        Time.timeScale = 1f;
        SetQteVisible(false);

        // 報酬の可否と値は、終了コールバック（Item側のプール返却・ロック解除）が状態を書き換える前に確定する。
        // 報酬可能状態はここで消費する（二重終了ガードと合わせて、QTE1回につき1回だけ付与する）。
        int rewardScore = ConsumeSuccessReward(outcome, fromPlayerInput);

        SetPlayerAnimatorUnscaled(false);

        // QTE終了時にダメージポーズのロックを解除する
        if (playerAnimator != null) playerAnimator.SetDamageLock(false);

        // コールバック参照を先に取り出してから消す（再入・二重終了で重複実行しない）。
        Action<QteOutcome> callback = onFinished;
        onFinished = null;

        // 報酬は GameManager.AddScore の共通処理（フィーバー中の2倍・捕獲後の得点固定・結果確定後のガード）を
        // 通して付与する。QTEManager 側ではフィーバー倍率を掛けない。
        GrantSuccessReward(rewardScore);

        callback?.Invoke(outcome);
        HugeQteFinished?.Invoke(outcome == QteOutcome.Success);
    }

    /// <summary>
    /// 成功報酬の点を確定し、「報酬可能」状態を消費する（1回だけ）。
    ///  ・本プレイの入力完了（fromPlayerInput）以外は0（強制成功・ブースト回避・チュートリアル・Cancelled・Failure）。
    ///  ・一発成功で successBaseScore 点。ミス1回（誤入力またはタイムアウト）ごとに successMissPenalty 点を減らし、
    ///    successMinScore 点を下回らない（5,000 → 4,000 → … → 1,000 が下限）。
    /// </summary>
    private int ConsumeSuccessReward(QteOutcome outcome, bool fromPlayerInput)
    {
        bool eligible = successRewardPending;
        successRewardPending = false;   // 付与より先に消費する（再入・二重付与を防ぐ）

        if (!eligible || !fromPlayerInput || outcome != QteOutcome.Success) return 0;

        return Mathf.Max(successMinScore, successBaseScore - (missCount * successMissPenalty));
    }

    /// <summary>
    /// 報酬を加点する（付与はこの1か所だけ）。0以下なら何もしない（得点変化なし）。
    /// フィーバー中の2倍・捕獲後の得点固定・結果確定後のガードは GameManager.AddScore の共通処理に任せる。
    /// </summary>
    private static void GrantSuccessReward(int amount)
    {
        if (amount <= 0) return;

        GameManager gameManager = GameManager.instance;
        if (gameManager == null) return;

        gameManager.AddScore(amount);
    }

    /// <summary>
    /// 結果確定（捕獲など）のためにQTEを中断する。成功・失敗としては扱わない。
    ///  ・追加ダメージは与えない（Item 側が QteOutcome.Cancelled で抑止する）。
    ///  ・HugeQteFinished は発火しない（次回巨大障害物の予約・チュートリアルの完了判定を起こさない）。
    ///  ・コールバックは「アイテムのプール返却と IsQteLocked 解除」のためだけに1回呼ぶ。
    ///  ・Time.timeScale はここでは変更しない（時間停止の所有権は呼び出し側の結果処理・捕獲待ちにある）。
    ///  ・何度呼んでも安全（1回目だけ true を返し、2回目以降は何もしない）。
    /// </summary>
    public bool CancelQteForResult()
    {
        if (!isQteActive) return false;

        isQteActive = false;

        // 中断（捕獲など）では報酬を出さない。報酬可能状態もここで消費する。
        successRewardPending = false;

        // コールバック参照を先に取り出し、元の参照を消す（再入・二重呼び出しで重複実行しない）。
        Action<QteOutcome> callback = onFinished;
        onFinished = null;

        SetQteVisible(false);
        SetPlayerAnimatorUnscaled(false);

        // QTE由来のダメージポーズのロックを解除する。
        if (playerAnimator != null) playerAnimator.SetDamageLock(false);

        // 中断を通知する（Item 側は Cancelled で効果を適用せず、プール返却と
        // ItemEffect.IsQteLocked の解除だけを行う）。
        callback?.Invoke(QteOutcome.Cancelled);
        return true;
    }

    private void OnDestroy()
    {
        // 破棄時にQTEが残っていれば、QTE由来の時間停止だけを解除する。
        //  ・結果確定による停止中は isQteActive が false のため触れない（結果処理の停止状態を上書きしない）。
        //  ・破棄時はアイテム／プールも同時に破棄されるため、コールバック（プール返却）は呼ばない。
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

    private void UpdateQteText()
    {
        if (!isQteActive) return;
        if (string.IsNullOrEmpty(currentSequence)) return;

        if (timerText != null)
        {
            bool showTimer = timeLimitSeconds > 0f;
            timerText.gameObject.SetActive(showTimer);
            if (showTimer) timerText.text = $"{Mathf.Max(0f, remainingTime):0.00}s";
        }

        for (int i = 0; i < slots.Length && i < currentSequence.Length; i++)
        {
            QteSlot slot = slots[i];
            if (slot == null) continue;

            bool pressed = i < currentIndex;

            if (slot.label != null)
            {
                slot.label.text = currentSequence[i].ToString();
                slot.label.color = pressed ? enteredColor : remainingColor;
            }

            // 押す前/押した後で画像を切り替える（未設定なら差し替えない）
            Sprite sprite = pressed ? slotPressedSprite : slotNormalSprite;
            if (slot.image != null && sprite != null)
            {
                slot.image.sprite = sprite;
            }
        }
    }

    private void SetQteVisible(bool visible)
    {
        // QTE関連のオブジェクトはまとめる親が無いので、1つずつ表示/非表示を切り替える
        if (qteBack != null) qteBack.SetActive(visible);
        if (titleText != null) titleText.gameObject.SetActive(visible);
        if (timerText != null) timerText.gameObject.SetActive(visible);

        if (slots == null) return;
        foreach (QteSlot slot in slots)
        {
            if (slot != null && slot.image != null)
            {
                slot.image.gameObject.SetActive(visible);
            }
        }
    }
}