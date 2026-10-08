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
        return IsGamepad ? "LS" : "Shift";   // Rev BT-c: lo sprint del gamepad è il click dello stick sinistro (L3)
    }

    public string GetCrouchPrompt()
    {
        return IsGamepad ? "B" : "C";   // Rev BT-c: il crouch del gamepad è B (buttonEast)
    }

    // ===== KIT MEDICO (Rev BU-d · Q91-c) =====
    // Tasti delle azioni del kit nella mappa Player, per la riga del kit sul tablet (ProfileTabUI).
    // Scritte fisse come gli altri prompt: chi cambia uno di questi binding (UseMedkit, UseAntidote,
    // UseDrug, CycleDrug, ThrowGrenade, DeployDrone) aggiorna anche queste righe (audit Rev AF/AG).
    // Niente frecce: ↑↓←→ non sono nell'atlante principale di LiberationSans.

    public string GetMedkitPrompt()
    {
        return IsGamepad ? "D-pad Down" : "H";
    }

    public string GetAntidotePrompt()
    {
        return IsGamepad ? "D-pad Up" : "J";
    }

    public string GetDrugPrompt()
    {
        return IsGamepad ? "D-pad Right" : "K";
    }

    public string GetCycleDrugPrompt()
    {
        return IsGamepad ? "D-pad Left" : "L";
    }

    public string GetGrenadePrompt()
    {
        return IsGamepad ? "RB" : "G";
    }

    public string GetDronePrompt()
    {
        return IsGamepad ? "LB" : "V";
    }

    public string FormatPrompt(string template)
    {
        return template
            .Replace("{interact}", GetInteractPrompt())
            .Replace("{cancel}", GetCancelPrompt())
            .Replace("{confirm}", GetConfirmPrompt())
            .Replace("{sprint}", GetSprintPrompt())
            .Replace("{crouch}", GetCrouchPrompt())
            .Replace("{medkit}", GetMedkitPrompt())        // Rev BU-d
            .Replace("{antidote}", GetAntidotePrompt())
            .Replace("{drug}", GetDrugPrompt())
            .Replace("{cycledrug}", GetCycleDrugPrompt())
            .Replace("{grenade}", GetGrenadePrompt())
            .Replace("{drone}", GetDronePrompt());
    }
}