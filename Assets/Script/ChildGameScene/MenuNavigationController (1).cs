using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using System.Collections.Generic;

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
    [SerializeField] private Button connectingCancelButton;
    [SerializeField] private float connectingCancelArrowOffsetX = -50f;
    [SerializeField] private float connectingCancelArrowOffsetY = 0f;

    [Header("Modal Panel Settings")]
    [SerializeField] private List<GameObject> exclusivePanels = new List<GameObject>();
    
    [Header("Input Settings")]
    [SerializeField] private float inputDelay = 0.2f; // 入力間隔
    
    private int currentIndex = 0;
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
        bool upPressed = false;
        bool downPressed = false;
        float gamepadVertical = 0f;

        // Keyboard input - Up Arrow
        if (Keyboard.current != null && Keyboard.current[Key.UpArrow].wasPressedThisFrame)
        {
            upPressed = true;
        }

        // Keyboard input - Down Arrow
        if (Keyboard.current != null && Keyboard.current[Key.DownArrow].wasPressedThisFrame)
        {
            downPressed = true;
        }

        // Gamepad input - D-Pad
        if (Gamepad.current != null)
        {
            if (Gamepad.current.dpad.up.wasPressedThisFrame)
            {
                upPressed = true;
            }
            if (Gamepad.current.dpad.down.wasPressedThisFrame)
            {
                downPressed = true;
            }

            // Gamepad Left Stick (vertical axis)
            gamepadVertical = Gamepad.current.leftStick.y.ReadValue();
        }

        // Handle keyboard/gamepad d-pad input
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
        // Handle analog stick input with cooldown
        else if (Mathf.Abs(gamepadVertical) > 0.5f && Time.time - lastInputTime > inputDelay)
        {
            if (gamepadVertical > 0.5f)
            {
                NavigateUp();
            }
            else if (gamepadVertical < -0.5f)
            {
                NavigateDown();
            }
            lastInputTime = Time.time;
        }
    }

    private void HandleConfirm()
    {
        bool confirmPressed = false;

        // Keyboard input - Enter
        if (Keyboard.current != null && Keyboard.current.enterKey.wasPressedThisFrame)
        {
            confirmPressed = true;
        }

        // Keyboard input - Space
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            confirmPressed = true;
        }

        // Gamepad input - Button South (A button on Xbox, Cross on PlayStation)
        if (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame)
        {
            confirmPressed = true;
        }

        // Gamepad input - Button East (B button on Xbox, Circle on PlayStation) as alternative
        if (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame)
        {
            confirmPressed = true;
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
            bool isConnectingCancel = connectingCancelButton != null && selectedButton == connectingCancelButton;
            bool isCancelButton = cancelButtons != null && cancelButtons.Contains(selectedButton);
            float offsetX = isConnectingCancel ? connectingCancelArrowOffsetX : (isCancelButton ? cancelArrowOffsetX : arrowOffsetX);
            float offsetY = isConnectingCancel ? connectingCancelArrowOffsetY : (isCancelButton ? cancelArrowOffsetY : arrowOffsetY);

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