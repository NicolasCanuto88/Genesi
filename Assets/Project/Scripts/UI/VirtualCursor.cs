using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using TMPro;

/// <summary>
/// Virtual cursor for gamepad UI interaction on World Space canvas
/// Uses direct UI raycasting instead of simulating mouse input
/// Left Stick = Move cursor (clamped inside monitor)
/// Right Stick Y = Scroll toggle list
/// A Button = Click UI elements
/// </summary>
public class VirtualCursor : MonoBehaviour
{
    [Header("Cursor Settings")]
    [SerializeField] private RectTransform cursorImage;
    [SerializeField] private RectTransform canvasRect;
    [SerializeField] private float cursorSpeed = 500f;
    [SerializeField] private float cursorAcceleration = 2f;

    [Header("Scroll Settings")]
    [SerializeField] private ScrollRect scrollRect;
    [SerializeField] private float scrollSpeed = 0.5f;

    [Header("Input")]
    [SerializeField] private float deadzone = 0.15f;

    [Header("Visual Feedback")]
    [SerializeField] private Color normalColor = Color.white;
    [SerializeField] private Color hoverColor = Color.cyan;

    // State
    private bool isActive = false;
    private Vector2 cursorLocalPosition;
    private Vector2 canvasSize;
    private Camera worldCamera;
    private Canvas worldCanvas;
    private Image cursorImageComponent;

    // Click state
    private bool wasClickedLastFrame = false;
    private Selectable currentHovered = null;

    // Singleton
    public static VirtualCursor Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        if (cursorImage != null)
        {
            cursorImage.gameObject.SetActive(false);
            cursorImageComponent = cursorImage.GetComponent<Image>();
        }
    }

    private bool isSubscribed = false;

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void OnDisable()
    {
        if (isSubscribed && InputDeviceManager.Instance != null)
        {
            InputDeviceManager.Instance.OnDeviceChanged -= OnDeviceChanged;
            isSubscribed = false;
        }

        Deactivate();
    }

    private void TrySubscribe()
    {
        if (isSubscribed) return;

        if (InputDeviceManager.Instance != null)
        {
            InputDeviceManager.Instance.OnDeviceChanged += OnDeviceChanged;
            isSubscribed = true;
            Debug.Log("[VirtualCursor] Subscribed to InputDeviceManager");
        }
    }

    private void OnDeviceChanged(InputDeviceManager.ActiveDevice device)
    {
        Debug.Log($"[VirtualCursor] Device changed to: {device}, isActive: {isActive}");

        if (!isActive) return;

        if (device == InputDeviceManager.ActiveDevice.KeyboardMouse)
        {
            Debug.Log("[VirtualCursor] Switching to MOUSE mode");
            ShowCursor(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (device == InputDeviceManager.ActiveDevice.Gamepad)
        {
            Debug.Log("[VirtualCursor] Switching to GAMEPAD mode");
            ShowCursor(true);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void Update()
    {
        // Retry subscription if not yet subscribed
        if (!isSubscribed)
        {
            TrySubscribe();
        }

        if (!isActive) return;
        if (Gamepad.current == null) return;

        // Check if we should be showing virtual cursor or real mouse
        if (InputDeviceManager.Instance != null)
        {
            if (InputDeviceManager.Instance.IsGamepad)
            {
                // Gamepad mode: process virtual cursor
                if (!cursorImage.gameObject.activeSelf)
                {
                    ShowCursor(true);
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }

                UpdateCursorMovement();
                UpdateScroll();
                UpdateClick();
                UpdateVisualPosition();
            }
            else
            {
                // Keyboard/Mouse mode: hide virtual cursor, ensure real cursor visible
                if (cursorImage != null && cursorImage.gameObject.activeSelf)
                {
                    ShowCursor(false);
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
        }
    }

    private void UpdateCursorMovement()
    {
        Vector2 stickInput = Gamepad.current.leftStick.ReadValue();

        // Apply deadzone
        if (stickInput.magnitude < deadzone)
        {
            return;
        }

        // Remap past deadzone
        stickInput = stickInput.normalized * ((stickInput.magnitude - deadzone) / (1f - deadzone));

        // Acceleration
        float speedMultiplier = Mathf.Pow(stickInput.magnitude, cursorAcceleration);

        // Move in canvas local space
        cursorLocalPosition += stickInput * speedMultiplier * cursorSpeed * Time.unscaledDeltaTime;

        // Clamp inside canvas
        float halfWidth = canvasSize.x * 0.5f;
        float halfHeight = canvasSize.y * 0.5f;

        cursorLocalPosition.x = Mathf.Clamp(cursorLocalPosition.x, -halfWidth, halfWidth);
        cursorLocalPosition.y = Mathf.Clamp(cursorLocalPosition.y, -halfHeight, halfHeight);
    }

    private void UpdateScroll()
    {
        if (scrollRect == null) return;

        float scrollInput = Gamepad.current.rightStick.ReadValue().y;

        if (Mathf.Abs(scrollInput) < deadzone) return;

        scrollRect.verticalNormalizedPosition += scrollInput * scrollSpeed * Time.unscaledDeltaTime;
        scrollRect.verticalNormalizedPosition = Mathf.Clamp01(scrollRect.verticalNormalizedPosition);
    }

    private void UpdateClick()
    {
        // Find what's under the cursor
        Selectable hitSelectable = RaycastUI();

        // Update hover visual
        if (hitSelectable != currentHovered)
        {
            currentHovered = hitSelectable;

            if (cursorImageComponent != null)
            {
                cursorImageComponent.color = (currentHovered != null) ? hoverColor : normalColor;
            }
        }

        // Handle A button press (single press only, not held)
        bool isClickedThisFrame = Gamepad.current.buttonSouth.isPressed;

        if (isClickedThisFrame && !wasClickedLastFrame)
        {
            // Button just pressed this frame
            if (currentHovered != null)
            {
                PerformClick(currentHovered);
            }
        }

        wasClickedLastFrame = isClickedThisFrame;
    }

    /// <summary>
    /// Raycast from cursor position into World Space canvas UI
    /// </summary>
    private Selectable RaycastUI()
    {
        if (worldCamera == null || canvasRect == null) return null;

        // Convert canvas local position to world position
        Vector3 worldPos = canvasRect.TransformPoint(cursorLocalPosition);

        // Convert world position to screen position for EventSystem raycasting
        Vector3 screenPos = worldCamera.WorldToScreenPoint(worldPos);

        // Check if it's in front of the camera
        if (screenPos.z <= 0) return null;

        // Raycast using EventSystem
        PointerEventData pointerData = new PointerEventData(EventSystem.current)
        {
            position = new Vector2(screenPos.x, screenPos.y)
        };

        List<RaycastResult> results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointerData, results);

        // Find first Selectable in results
        foreach (var result in results)
        {
            // Check the hit object and its parents for Selectable
            Selectable selectable = result.gameObject.GetComponentInParent<Selectable>();
            if (selectable != null && selectable.interactable)
            {
                return selectable;
            }
        }

        return null;
    }

    /// <summary>
    /// Perform a click on a UI Selectable (Toggle or Button)
    /// </summary>
    private void PerformClick(Selectable selectable)
    {
        if (selectable == null) return;

        // Handle Toggle
        Toggle toggle = selectable as Toggle;
        if (toggle != null)
        {
            toggle.isOn = !toggle.isOn;
            Debug.Log($"[VirtualCursor] Toggled: {toggle.gameObject.name} → {toggle.isOn}");
            return;
        }

        // Handle Button
        Button button = selectable as Button;
        if (button != null)
        {
            button.onClick.Invoke();
            Debug.Log($"[VirtualCursor] Clicked button: {button.gameObject.name}");
            return;
        }
    }

    private void UpdateVisualPosition()
    {
        if (cursorImage == null) return;
        cursorImage.anchoredPosition = cursorLocalPosition;
    }

    // ===== PUBLIC CONTROL =====

    public void Activate()
    {
        if (canvasRect == null)
        {
            Debug.LogError("[VirtualCursor] canvasRect not assigned!");
            return;
        }

        // Ensure subscribed
        TrySubscribe();

        isActive = true;
        wasClickedLastFrame = false;
        currentHovered = null;

        // Cache canvas info
        canvasSize = canvasRect.rect.size;
        worldCanvas = canvasRect.GetComponent<Canvas>();

        if (worldCanvas != null)
        {
            worldCamera = worldCanvas.worldCamera;
        }

        if (worldCamera == null)
        {
            worldCamera = Camera.main;
        }

        // Center cursor
        cursorLocalPosition = Vector2.zero;

        // Show based on current device
        if (InputDeviceManager.Instance != null && InputDeviceManager.Instance.IsGamepad)
        {
            ShowCursor(true);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            Debug.Log("[VirtualCursor] Activated in GAMEPAD mode");
        }
        else
        {
            ShowCursor(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Debug.Log("[VirtualCursor] Activated in KEYBOARD/MOUSE mode");
        }
    }

    public void Deactivate()
    {
        isActive = false;
        wasClickedLastFrame = false;
        currentHovered = null;
        ShowCursor(false);

        // Restore cursor to locked state for gameplay
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void ShowCursor(bool show)
    {
        if (cursorImage != null)
        {
            cursorImage.gameObject.SetActive(show);
        }
    }

    public bool IsActive => isActive;
}