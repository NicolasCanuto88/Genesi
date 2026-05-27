using UnityEngine;
using UnityEngine.InputSystem;
using System;

/// <summary>
/// Detects which input device the player is currently using
/// Auto-switches between Keyboard/Mouse and Gamepad based on last input
/// Provides correct button prompt strings for UI
/// </summary>
public class InputDeviceManager : MonoBehaviour
{
    public enum ActiveDevice
    {
        KeyboardMouse,
        Gamepad
    }

    // Singleton
    public static InputDeviceManager Instance { get; private set; }

    // Current device
    private ActiveDevice currentDevice = ActiveDevice.KeyboardMouse;
    public ActiveDevice CurrentDevice => currentDevice;
    public bool IsGamepad => currentDevice == ActiveDevice.Gamepad;
    public bool IsKeyboard => currentDevice == ActiveDevice.KeyboardMouse;

    // Event fired when device changes
    public event Action<ActiveDevice> OnDeviceChanged;

    // Polling interval (avoid checking every frame for performance)
    private float pollTimer = 0f;
    private const float POLL_INTERVAL = 0.1f;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
            return;
        }
    }

    private void Update()
    {
        pollTimer += Time.unscaledDeltaTime;
        if (pollTimer < POLL_INTERVAL) return;
        pollTimer = 0f;

        DetectActiveDevice();
    }

    private void DetectActiveDevice()
    {
        ActiveDevice newDevice = currentDevice;

        // Check Gamepad
        if (Gamepad.current != null)
        {
            var gp = Gamepad.current;

            bool hasGamepadInput = false;

            // Sticks with deadzone
            if (gp.leftStick.ReadValue().magnitude > 0.2f ||
                gp.rightStick.ReadValue().magnitude > 0.2f)
            {
                hasGamepadInput = true;
            }

            // Any button
            if (gp.buttonSouth.isPressed || gp.buttonNorth.isPressed ||
                gp.buttonEast.isPressed || gp.buttonWest.isPressed ||
                gp.dpad.ReadValue().magnitude > 0.5f ||
                gp.leftShoulder.isPressed || gp.rightShoulder.isPressed ||
                gp.leftTrigger.ReadValue() > 0.5f || gp.rightTrigger.ReadValue() > 0.5f ||
                gp.startButton.isPressed || gp.selectButton.isPressed)
            {
                hasGamepadInput = true;
            }

            if (hasGamepadInput)
            {
                newDevice = ActiveDevice.Gamepad;
            }
        }

        // Check Keyboard
        if (Keyboard.current != null && Keyboard.current.anyKey.isPressed)
        {
            newDevice = ActiveDevice.KeyboardMouse;
        }

        // Check Mouse (movement or click)
        if (Mouse.current != null)
        {
            if (Mouse.current.delta.ReadValue().magnitude > 1f ||
                Mouse.current.leftButton.isPressed ||
                Mouse.current.rightButton.isPressed ||
                Mathf.Abs(Mouse.current.scroll.ReadValue().y) > 0.1f)
            {
                newDevice = ActiveDevice.KeyboardMouse;
            }
        }

        // Fire event on change
        if (newDevice != currentDevice)
        {
            currentDevice = newDevice;
            OnDeviceChanged?.Invoke(currentDevice);
            Debug.Log($"[InputDeviceManager] Switched to: {currentDevice}");
        }
    }

    // ===== BUTTON PROMPT HELPERS =====

    public string GetInteractPrompt()
    {
        return IsGamepad ? "X" : "E";
    }

    public string GetCancelPrompt()
    {
        return IsGamepad ? "B" : "ESC";
    }

    public string GetConfirmPrompt()
    {
        return IsGamepad ? "A" : "Enter";
    }

    public string GetSprintPrompt()
    {
        return IsGamepad ? "LT" : "Shift";
    }

    public string GetCrouchPrompt()
    {
        return IsGamepad ? "RS" : "C";
    }

    public string FormatPrompt(string template)
    {
        return template
            .Replace("{interact}", GetInteractPrompt())
            .Replace("{cancel}", GetCancelPrompt())
            .Replace("{confirm}", GetConfirmPrompt())
            .Replace("{sprint}", GetSprintPrompt())
            .Replace("{crouch}", GetCrouchPrompt());
    }
}