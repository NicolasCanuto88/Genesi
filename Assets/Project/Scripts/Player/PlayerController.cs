using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// First-person controller for Space Survivor
/// Uses Unity's New Input System with InputSystem_Actions
/// FIXED: Proper gravity + No legacy Input calls
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInput))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float walkSpeed = 3f;
    [SerializeField] private float sprintSpeed = 5.5f;
    [SerializeField] private float crouchSpeed = 1.5f;
    [SerializeField] private float acceleration = 10f;
    [SerializeField] private float deceleration = 10f;

    [Header("Look")]
    [SerializeField] private float lookSensitivity = 2f;
    [SerializeField] private float lookSmoothness = 10f;
    [SerializeField] private float maxLookAngle = 85f;

    [Header("Stamina")]
    [SerializeField] private float maxStamina = 100f;
    [SerializeField] private float sprintStaminaDrain = 20f;
    [SerializeField] private float staminaRecovery = 15f;

    [Header("Gravity")]
    [SerializeField] private float gravity = 20f; // Adjustable gravity strength
    [SerializeField] private float terminalVelocity = -50f; // Max fall speed

    [Header("References")]
    [SerializeField] private Transform cameraTransform;

    // Ladder reference (set by Ladder when player enters/exits)
    private Ladder currentLadder;

    // Components
    private CharacterController characterController;
    private PlayerInput playerInput;

    // Input values
    private Vector2 moveInput;
    private Vector2 lookInput;
    private bool sprintPressed;
    private bool crouchToggled;

    // State
    private Vector3 currentVelocity;
    private float currentStamina;
    private float verticalRotation;
    private float verticalVelocity; // Separate vertical velocity for gravity

    // Properties
    public float CurrentStamina => currentStamina;
    public bool IsSprinting => sprintPressed && currentStamina > 0 && moveInput.magnitude > 0.1f;
    public bool IsCrouching => crouchToggled;
    public Transform CameraTransform => cameraTransform;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
        playerInput = GetComponent<PlayerInput>();
        currentStamina = maxStamina;

        // Auto-assign camera if not set
        if (cameraTransform == null)
        {
            cameraTransform = GetComponentInChildren<Camera>()?.transform;
            if (cameraTransform == null)
            {
                Debug.LogError("[PlayerController] No camera found! Add a Camera as child object.");
            }
        }

        // Lock cursor
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Start()
    {
        // Force cursor lock on start (Unity editor sometimes resets it)
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        // Check if on ladder
        if (currentLadder != null && currentLadder.IsPlayerOnLadder)
        {
            // On ladder - send input to ladder
            currentLadder.HandleClimbing(moveInput.y, lookInput);
        }
        else
        {
            // Normal movement
            HandleMovement();
            HandleLook();
            HandleStamina();

            // WORKAROUND: Verify sprint state every frame
            VerifySprintState();
        }
    }

    // ===== LADDER PUBLIC METHODS =====

    public void SetCurrentLadder(Ladder ladder)
    {
        currentLadder = ladder;
    }

    private void VerifySprintState()
    {
        // Check if Shift is actually pressed
        if (Keyboard.current != null)
        {
            bool shiftActuallyPressed = Keyboard.current.leftShiftKey.isPressed ||
                                       Keyboard.current.rightShiftKey.isPressed;

            // If callback says sprint is ON but Shift is not pressed, force it OFF
            if (sprintPressed && !shiftActuallyPressed)
            {
                sprintPressed = false;
                // Debug log removed - workaround is working silently
            }
        }
    }

    private void HandleMovement()
    {
        // Skip movement if CharacterController is disabled (e.g., on ladder)
        if (!characterController.enabled)
        {
            return;
        }

        // Determine target speed
        float targetSpeed = walkSpeed;

        if (IsSprinting)
        {
            targetSpeed = sprintSpeed;
        }
        else if (IsCrouching)
        {
            targetSpeed = crouchSpeed;
        }

        // Calculate movement direction (horizontal only)
        Vector3 inputDirection = transform.right * moveInput.x + transform.forward * moveInput.y;
        Vector3 targetVelocity = inputDirection.normalized * targetSpeed;

        // Smooth acceleration/deceleration (horizontal only)
        float speedDelta = (targetVelocity.magnitude > currentVelocity.magnitude) ? acceleration : deceleration;
        currentVelocity = Vector3.Lerp(currentVelocity, targetVelocity, speedDelta * Time.deltaTime);

        // Handle gravity separately
        if (characterController.isGrounded)
        {
            // Reset vertical velocity when grounded
            verticalVelocity = -2f; // Small downward force to keep grounded
        }
        else
        {
            // Apply gravity acceleration
            verticalVelocity -= gravity * Time.deltaTime;
        }

        // Clamp fall speed to terminal velocity
        verticalVelocity = Mathf.Max(verticalVelocity, terminalVelocity);

        // Combine horizontal movement with vertical velocity
        Vector3 finalVelocity = currentVelocity;
        finalVelocity.y = verticalVelocity;

        // Move
        characterController.Move(finalVelocity * Time.deltaTime);
    }

    private void HandleLook()
    {
        // Only look when cursor is locked
        if (Cursor.lockState != CursorLockMode.Locked)
            return;

        // Horizontal rotation (player body)
        transform.Rotate(Vector3.up * lookInput.x * lookSensitivity);

        // Vertical rotation (camera only)
        verticalRotation -= lookInput.y * lookSensitivity;
        verticalRotation = Mathf.Clamp(verticalRotation, -maxLookAngle, maxLookAngle);

        if (cameraTransform != null)
        {
            cameraTransform.localRotation = Quaternion.Euler(verticalRotation, 0f, 0f);
        }
    }

    private void HandleStamina()
    {
        if (IsSprinting)
        {
            currentStamina -= sprintStaminaDrain * Time.deltaTime;
            currentStamina = Mathf.Max(0f, currentStamina);
        }
        else
        {
            currentStamina += staminaRecovery * Time.deltaTime;
            currentStamina = Mathf.Min(maxStamina, currentStamina);
        }
    }

    // ===== INPUT SYSTEM CALLBACKS =====
    // These are called automatically by PlayerInput component

    public void OnMove(InputValue value)
    {
        moveInput = value.Get<Vector2>();
    }

    public void OnLook(InputValue value)
    {
        lookInput = value.Get<Vector2>();
    }

    public void OnSprint(InputValue value)
    {
        // Button callback only fires on press, not release (Unity 6.3 behavior)
        float rawValue = value.Get<float>();
        sprintPressed = rawValue > 0.5f;
    }

    public void OnCrouch(InputValue value)
    {
        if (value.isPressed)
        {
            crouchToggled = !crouchToggled;
        }
    }
    public void OnDebug(InputValue value)
    {
        if (value.isPressed)
        {
            DeguAndTest deguAndTest = FindObjectOfType<DeguAndTest>();
            deguAndTest.panel();
        }
    }

    // OnCancel is handled by EngineeringStation and other systems
    // Do NOT toggle cursor lock here - it conflicts with station exit
    public void OnCancel(InputValue value)
    {
        // Intentionally empty - cursor management is handled by:
        // - EngineeringStation (dashboard open/close)
        // - VirtualCursor (gamepad/keyboard switch)
        // - PlayerController.Start() (initial lock)
    }

    // Debug
    private void OnGUI()
    {
        if (!Debug.isDebugBuild) return;

        GUI.Label(new Rect(10, 10, 300, 20), $"Stamina: {currentStamina:F1}/{maxStamina}");
        GUI.Label(new Rect(10, 30, 300, 20), $"Speed: {currentVelocity.magnitude:F2} m/s");
        GUI.Label(new Rect(10, 50, 300, 20), $"Sprinting: {IsSprinting}");
        GUI.Label(new Rect(10, 70, 300, 20), $"Crouching: {IsCrouching}");
    }
}