using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class QTEManager : MonoBehaviour
{
    public static QTEManager Instance { get; private set; }
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

    private bool isQteActive;
    private string currentSequence = string.Empty;
    private int currentIndex;
    private float remainingTime;
    private Action<bool> onFinished;
    private bool hasStoredPlayerUpdateMode;
    private AnimatorUpdateMode storedPlayerUpdateMode;

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

    public bool StartHugeObstacleQte(Action<bool> finishedCallback)
    {
        if (isQteActive) return false;

        if (slots == null || slots.Length == 0)
        {
            Debug.LogError("QTEManager: slots が未設定です。Inspectorで画像＋テキストのセットを割り当ててください。");
            return false;
        }

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
                FinishQte(true);
            }
            return;
        }

        HandleQteMiss();
        RegenerateSequence();
    }

    private void HandleQteMiss()
    {
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
            Instance.FinishQte(true);
        }
        else
        {
            HugeQteFinished?.Invoke(true);
        }
    }

    private void FinishQte(bool success)
    {
        isQteActive = false;
        Time.timeScale = 1f;
        SetQteVisible(false);

        SetPlayerAnimatorUnscaled(false);

        // QTE終了時にダメージポーズのロックを解除する
        if (playerAnimator != null) playerAnimator.SetDamageLock(false);

        Action<bool> callback = onFinished;
        onFinished = null;
        callback?.Invoke(success);
        HugeQteFinished?.Invoke(success);
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