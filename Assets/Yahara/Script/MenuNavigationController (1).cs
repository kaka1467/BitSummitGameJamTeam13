using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class MenuNavigationController : MonoBehaviour
{
    [Header("Navigation Settings")]
    [SerializeField] private List<Button> selectableButtons = new List<Button>();
    [SerializeField] private GameObject selectionArrow;
    [SerializeField] private float arrowOffsetX = -50f; // 矢印のX座標オフセット
    [SerializeField] private float arrowOffsetY = 0f;   // 矢印のY座標オフセット
    [SerializeField] private List<Button> cancelButtons = new List<Button>();
    [SerializeField] private float cancelArrowOffsetX = -50f;
    [SerializeField] private float cancelArrowOffsetY = 0f;

    [Header("Modal Panel Settings")]
    [SerializeField] private List<GameObject> exclusivePanels = new List<GameObject>();
    
    [Header("Input Settings")]
    [SerializeField] private KeyCode confirmKey = KeyCode.Return;
    [SerializeField] private KeyCode upKey = KeyCode.UpArrow;
    [SerializeField] private KeyCode downKey = KeyCode.DownArrow;
    [SerializeField] private string verticalAxisName = "Vertical"; // ゲームパッド対応
    [SerializeField] private string submitButtonName = "Submit";   // ゲームパッド決定ボタン
    
    [Header("Retroid Pocket Flip 2 Settings")]
    // Retroid Pocket Flip 2のキー設定
    [SerializeField] private KeyCode retroidConfirmKey = KeyCode.Space; // Retroidの決定ボタン
    [SerializeField] private KeyCode[] additionalConfirmKeys = new KeyCode[] 
    { 
        KeyCode.JoystickButton0,  // Aボタン
        KeyCode.JoystickButton1,  // Bボタン
        KeyCode.Z,                // 追加の確認キー
        KeyCode.X                 // 追加の確認キー
    };
    
    private int currentIndex = 0;
    private float lastVerticalInput = 0f;
    private float inputDelay = 0.2f; // 入力間隔
    private float lastInputTime = 0f;

    void Start()
    {
        if (selectionArrow == null)
        {
            Debug.LogError("Selection Arrow is not assigned!");
            return;
        }

        // ボタンリストが空の場合、自動的に子オブジェクトからボタンを取得
        if (selectableButtons.Count == 0)
        {
            Button[] buttons = GetComponentsInChildren<Button>();
            selectableButtons.AddRange(buttons);
        }

        // 最初のボタンを選択
        if (selectableButtons.Count > 0)
        {
            UpdateArrowPosition();
        }
        else
        {
            Debug.LogWarning("No selectable buttons found!");
        }
    }

    void Update()
    {
        if (selectableButtons.Count == 0 || selectionArrow == null)
            return;

        EnsureValidSelection();
        HandleNavigation();
        HandleConfirm();
    }

    private void HandleNavigation()
    {
        float verticalInput = Input.GetAxisRaw(verticalAxisName);
        bool upPressed = Input.GetKeyDown(upKey);
        bool downPressed = Input.GetKeyDown(downKey);

        // キーボードでの十字キー入力
        if (upPressed || downPressed)
        {
            if (upPressed)
            {
                NavigateUp();
            }
            else if (downPressed)
            {
                NavigateDown();
            }
            lastInputTime = Time.time;
        }
        // ゲームパッド/アナログスティックでの入力
        else if (Mathf.Abs(verticalInput) > 0.5f && Time.time - lastInputTime > inputDelay)
        {
            if (verticalInput > 0.5f)
            {
                NavigateUp();
            }
            else if (verticalInput < -0.5f)
            {
                NavigateDown();
            }
            lastInputTime = Time.time;
        }

        lastVerticalInput = verticalInput;
    }

    private void HandleConfirm()
    {
        bool confirmPressed = Input.GetKeyDown(confirmKey) || 
                            Input.GetKeyDown(retroidConfirmKey) ||
                            Input.GetButtonDown(submitButtonName);

        // 追加の確認キーをチェック
        foreach (KeyCode key in additionalConfirmKeys)
        {
            if (Input.GetKeyDown(key))
            {
                confirmPressed = true;
                break;
            }
        }

        if (confirmPressed)
        {
            ExecuteCurrentButton();
        }
    }

    private void NavigateUp()
    {
        if (selectableButtons.Count == 0) return;

        // 有効なボタンを上方向に探す
        int startIndex = currentIndex;
        do
        {
            currentIndex--;
            if (currentIndex < 0)
                currentIndex = selectableButtons.Count - 1;

            // 無限ループ防止
            if (currentIndex == startIndex)
                break;

        } while (!IsButtonInteractable(currentIndex));

        UpdateArrowPosition();
    }

    private void NavigateDown()
    {
        if (selectableButtons.Count == 0) return;

        // 有効なボタンを下方向に探す
        int startIndex = currentIndex;
        do
        {
            currentIndex++;
            if (currentIndex >= selectableButtons.Count)
                currentIndex = 0;

            // 無限ループ防止
            if (currentIndex == startIndex)
                break;

        } while (!IsButtonInteractable(currentIndex));

        UpdateArrowPosition();
    }

    private bool IsButtonInteractable(int index)
    {
        if (index < 0 || index >= selectableButtons.Count)
            return false;

        Button button = selectableButtons[index];
        return button != null && button.gameObject.activeInHierarchy && button.interactable && IsAllowedByExclusivePanels(button);
    }

    private bool IsAllowedByExclusivePanels(Button button)
    {
        GameObject activePanel = GetActiveExclusivePanel();
        if (activePanel == null)
        {
            return true;
        }

        return button.transform.IsChildOf(activePanel.transform);
    }

    private GameObject GetActiveExclusivePanel()
    {
        if (exclusivePanels == null || exclusivePanels.Count == 0)
        {
            return null;
        }

        for (int i = 0; i < exclusivePanels.Count; i++)
        {
            GameObject panel = exclusivePanels[i];
            if (panel != null && panel.activeInHierarchy)
            {
                return panel;
            }
        }

        return null;
    }

    private void EnsureValidSelection()
    {
        if (IsButtonInteractable(currentIndex))
        {
            if (!selectionArrow.activeSelf)
            {
                selectionArrow.SetActive(true);
            }
            return;
        }

        int validIndex = FindFirstValidIndex();
        if (validIndex >= 0)
        {
            currentIndex = validIndex;
            if (!selectionArrow.activeSelf)
            {
                selectionArrow.SetActive(true);
            }
            UpdateArrowPosition();
        }
        else
        {
            if (selectionArrow.activeSelf)
            {
                selectionArrow.SetActive(false);
            }
        }
    }

    private int FindFirstValidIndex()
    {
        for (int i = 0; i < selectableButtons.Count; i++)
        {
            if (IsButtonInteractable(i))
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdateArrowPosition()
    {
        if (currentIndex < 0 || currentIndex >= selectableButtons.Count)
            return;

        Button selectedButton = selectableButtons[currentIndex];
        if (selectedButton == null || selectionArrow == null)
            return;

        // 矢印の位置を選択中のボタンの横に配置
        RectTransform buttonRect = selectedButton.GetComponent<RectTransform>();
        RectTransform arrowRect = selectionArrow.GetComponent<RectTransform>();

        if (buttonRect != null && arrowRect != null)
        {
            bool isCancelButton = cancelButtons != null && cancelButtons.Contains(selectedButton);
            float offsetX = isCancelButton ? cancelArrowOffsetX : arrowOffsetX;
            float offsetY = isCancelButton ? cancelArrowOffsetY : arrowOffsetY;

            // ボタンの位置を取得して矢印を配置
            Vector3 buttonPos = buttonRect.position;
            arrowRect.position = new Vector3(
                buttonPos.x + offsetX,
                buttonPos.y + offsetY,
                buttonPos.z
            );
        }

        // 視覚的なフィードバック（オプション）
        // selectedButton.Select();
    }

    private void ExecuteCurrentButton()
    {
        if (currentIndex < 0 || currentIndex >= selectableButtons.Count)
            return;

        Button selectedButton = selectableButtons[currentIndex];
        
        if (selectedButton != null && selectedButton.interactable && selectedButton.gameObject.activeInHierarchy)
        {
            // ボタンのクリックイベントを実行
            selectedButton.onClick.Invoke();
            Debug.Log($"Button clicked: {selectedButton.name}");
        }
    }

    // 外部から現在の選択を変更するメソッド
    public void SetSelectedIndex(int index)
    {
        if (index >= 0 && index < selectableButtons.Count)
        {
            currentIndex = index;
            UpdateArrowPosition();
        }
    }

    // 外部からボタンリストを更新するメソッド
    public void UpdateSelectableButtons(List<Button> buttons)
    {
        selectableButtons = buttons;
        currentIndex = 0;
        UpdateArrowPosition();
    }

    // 現在選択されているボタンを取得
    public Button GetCurrentButton()
    {
        if (currentIndex >= 0 && currentIndex < selectableButtons.Count)
        {
            return selectableButtons[currentIndex];
        }
        return null;
    }
}