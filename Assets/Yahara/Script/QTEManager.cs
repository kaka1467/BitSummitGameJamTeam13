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

    [SerializeField] private TextMeshProUGUI qteText;
    [SerializeField] private int sequenceLength = 7;
    [SerializeField] private float timeLimitSeconds = 5f;
    [SerializeField] private Color enteredColor = Color.green;
    [SerializeField] private Color remainingColor = Color.white;

    private bool isQteActive;
    private string currentSequence = string.Empty;
    private int currentIndex;
    private float remainingTime;
    private Action<bool> onFinished;

    public bool IsQteActive => isQteActive;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        EnsureQteText();
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
                RegenerateSequence();
                return;
            }
        }

        HandleQteInput();
        UpdateQteText();
    }

    public bool StartHugeObstacleQte(Action<bool> finishedCallback)
    {
        if (isQteActive) return false;

        EnsureQteText();
        RegenerateSequence();
        onFinished = finishedCallback;
        isQteActive = true;

        Time.timeScale = 0f;
        SetQteVisible(true);
        UpdateQteText();
        return true;
    }

    private void HandleQteInput()
    {
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

        RegenerateSequence();
    }

    private void RegenerateSequence()
    {
        currentSequence = GenerateSequence(sequenceLength);
        currentIndex = 0;
        remainingTime = timeLimitSeconds;
        UpdateQteText();
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

    private void FinishQte(bool success)
    {
        isQteActive = false;
        Time.timeScale = 1f;
        SetQteVisible(false);

        Action<bool> callback = onFinished;
        onFinished = null;
        callback?.Invoke(success);
        HugeQteFinished?.Invoke(success);
    }

    private void UpdateQteText()
    {
        if (qteText == null) return;

        string entered = currentIndex > 0 ? currentSequence.Substring(0, currentIndex) : string.Empty;
        string remain = currentSequence.Substring(currentIndex);
        string enteredHex = ColorUtility.ToHtmlStringRGB(enteredColor);
        string remainHex = ColorUtility.ToHtmlStringRGB(remainingColor);
        string displaySequence = $"<color=#{enteredHex}>{entered}</color><color=#{remainHex}>{remain}</color>";

        if (timeLimitSeconds > 0f)
        {
            qteText.text = $"QTE {remainingTime:0.0}s\n{displaySequence}";
        }
        else
        {
            qteText.text = $"QTE\n{displaySequence}";
        }
    }

    private void SetQteVisible(bool visible)
    {
        if (qteText == null) return;
        qteText.gameObject.SetActive(visible);
    }

    private void EnsureQteText()
    {
        if (qteText != null) return;

        Canvas canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null)
        {
            GameObject canvasObject = new GameObject("QTECanvas");
            canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;
            canvasObject.AddComponent<CanvasScaler>();
            canvasObject.AddComponent<GraphicRaycaster>();
        }

        GameObject textObject = new GameObject("QTEText");
        textObject.transform.SetParent(canvas.transform, false);

        RectTransform rectTransform = textObject.AddComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.pivot = new Vector2(0.5f, 0.5f);
        rectTransform.anchoredPosition = Vector2.zero;
        rectTransform.sizeDelta = new Vector2(1200f, 300f);

        qteText = textObject.AddComponent<TextMeshProUGUI>();
        qteText.fontSize = 72f;
        qteText.alignment = TextAlignmentOptions.Center;
    }
}