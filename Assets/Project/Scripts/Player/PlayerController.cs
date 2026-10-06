using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// First-person controller for Space Survivor
/// Uses Unity's New Input System with InputSystem_Actions
/// FIXED: Proper gravity + No legacy Input calls
///
/// MODIFICATORI (Rev BR · Q39-b): la velocità di movimento (camminata, scatto, accovacciato)
/// è moltiplicata per il moltiplicatore MoveSpeed di PlayerStatusEffects (Combat Stim +20%).
/// Il movimento gira sul client proprietario (NetworkTransform owner-authoritative): il
/// moltiplicatore viene dalla maschera replicata degli stati, quindi è già noto qui senza
/// RPC. Le scale non sono influenzate (Ladder gestisce la salita).
///
/// STAMINA (Rev BR): RefillStamina riporta la stamina al massimo. La chiama l'Adrenaline
/// (PlayerMedKit, Rev BR-b) sul client proprietario, dove vive la stamina. MaxStamina serve al
/// kit per non consumare un'Adrenaline con HP e stamina già pieni.
///
/// VALORI DA SO (Rev BT-a · Q72-a): velocità, accelerazione, stamina e gravità vengono da
/// PlayerMovementConfig (campo movementConfig). Senza asset il componente ne crea uno con i
/// default e lo segnala a log: il giocatore si muove comunque. Sensibilità, morbidezza e angolo
/// della visuale restano qui (comfort dell'utente, non bilanciamento).
///
/// SPRINT (Rev BT-a · Q71-a): l'azione "Sprint" ha l'interazione Press "Press And Release"
/// (Press(behavior=2), stessa soluzione di ThrowGrenade in Rev BS-a). PlayerInput chiama
/// OnSprint alla pressione E al rilascio, quindi sprintPressed segue il tasto su tastiera e
/// gamepad. Rete di sicurezza (VerifySprintRelease): se il flag è acceso ma l'azione non risulta
/// più premuta (InputAction.IsPressed), si spegne — copre un rilascio perso, per esempio mentre
/// il componente era disattivato a una postazione. Prima di Rev BT-a l'azione era un Button
/// senza interazione, OnSprint arrivava solo alla pressione e il rilascio era ricavato da
/// Keyboard.current (Shift): lo sprint del gamepad si spegneva subito con una tastiera
/// collegata. Ora nessun accesso diretto ai dispositivi (invariante di input).
///
/// DEBUG (Rev BT-a · Q74-a): l'azione "Debug" non ha più binding (lo spazio è stato tolto).
/// OnDebug resta come guardia: solo Editor/Development Build, e nessuna eccezione se
/// DeguAndTest o il suo pannello mancano.
///
/// VISUALE (Rev BT-b · Q78-a, Q79-a): l'azione "Look" porta due grandezze diverse.
/// - Mouse: spostamento in pixel nel frame → gradi = pixel × lookSensitivity. Invariato.
/// - Stick (e ogni dispositivo non mouse): deflessione −1…1 → velocità angolare. Gradi nel
///   frame = curva(|stick|) × stickLookSpeed × deltaTime, con curva = |stick|^stickLookExponent
///   applicata al modulo (la direzione resta quella dello stick).
/// Prima di Rev BT-b anche lo stick era moltiplicato per lookSensitivity a ogni frame: al massimo
/// 0.15° per frame, cioè ~9°/s a 60 fps, e velocità diversa a seconda del frame rate.
/// Il dispositivo si riconosce da lookAction.activeControl.device (stesso schema di
/// SutureInteraction, Rev BO-c, e PilotStation, Rev T): nessun accesso a Mouse.current.
/// La conversione in gradi (ComputeLookDegrees) avviene qui una volta per frame e serve anche la
/// scala: Ladder.HandleClimbing riceve gradi, quindi la sensibilità è la stessa a piedi e in
/// salita. I tre valori sono comfort dell'utente (Q72-a): restano sul componente, destinati al
/// menu opzioni.
///
/// CROUCH (Rev BT-b · Q81-a): OnCrouch è ignorato a componente disattivato (vedi OnCrouch).
/// Prima, uscendo da una postazione con B (Cancel, stesso tasto di Crouch sul gamepad) il
/// giocatore si ritrovava accovacciato.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInput))]
public class PlayerController : MonoBehaviour
{
    private const string SprintActionName = "Sprint";
    private const string LookActionName = "Look";

    [Header("Movement (Rev BT-a — valori da SO)")]
    [Tooltip("Velocità, accelerazione, stamina e gravità. Asset: Assets/Project/Scripts/Player/" +
             "PlayerMovementConfig.asset. Se manca, si usano i default dello SO e un errore a log.")]
    [SerializeField] private PlayerMovementConfig movementConfig;

    [Header("Look")]
    [Tooltip("Mouse: gradi di rotazione per pixel di spostamento. Vale anche sulla scala (Rev BT-b).")]
    [SerializeField] private float lookSensitivity = 0.15f;
    [SerializeField] private float lookSmoothness = 10f;
    [SerializeField] private float maxLookAngle = 85f;

    [Header("Look — Stick (Rev BT-b)")]
    [Tooltip("Stick: velocità di rotazione a fondo corsa, in gradi al secondo. Indipendente dal frame rate.")]
    [Min(0f)]
    [SerializeField] private float stickLookSpeed = 180f;

    [Tooltip("Stick: curva di risposta. 1 = lineare; valori più alti danno più precisione vicino al " +
             "centro (1.5 → a metà corsa circa il 35% della velocità massima).")]
    [Range(1f, 3f)]
    [SerializeField] private float stickLookExponent = 1.5f;

    [Header("References")]
    [SerializeField] private Transform cameraTransform;

    // Ladder reference (set by Ladder when player enters/exits)
    private Ladder currentLadder;

    // Components
    private CharacterController characterController;
    private PlayerInput playerInput;
    private PlayerStatusEffects statusEffects;   // Rev BR: moltiplicatore di velocità (può mancare fuori dal Player prefab)
    private InputAction sprintAction;            // Rev BT-a: rete di sicurezza sul rilascio dello sprint
    private InputAction lookAction;              // Rev BT-b: dispositivo che guida la visuale (mouse o stick)

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
    private bool lookFromMouse;     // Rev BT-b: solo per l'overlay di debug (dispositivo dell'ultimo frame)

    // Properties
    public float CurrentStamina => currentStamina;
    public float MaxStamina => Config.MaxStamina;   // Rev BR-b: blocco preventivo dell'Adrenaline (Q47-a)
    public bool IsSprinting => sprintPressed && currentStamina > 0 && moveInput.magnitude > 0.1f;
    public bool IsCrouching => crouchToggled;
    public Transform CameraTransform => cameraTransform;

    /// <summary>
    /// Rev BT-a — configurazione attiva. Mai null dopo Awake: se il campo è vuoto, Awake crea
    /// un'istanza con i default e lo segnala una volta.
    /// </summary>
    private PlayerMovementConfig Config => movementConfig;

    /// <summary>
    /// Azzera la velocità interna (orizzontale + verticale/gravità).
    /// NECESSARIO dopo qualunque periodo in cui questo componente resta
    /// disabilitato (es. seduto a una postazione): currentVelocity è un
    /// campo persistente usato per smussare accelerazione/decelerazione —
    /// se non viene disabilitato anche l'Update() che lo aggiorna (è
    /// esattamente il caso quando PlayerController.enabled = false), resta
    /// congelato a qualunque velocità avesse il player nell'istante esatto
    /// dell'interazione. Riattivando il componente senza azzerarlo, il
    /// primo HandleMovement() riparte da quella velocità "vecchia" — il
    /// player riprende per qualche frame a muoversi nella direzione in cui
    /// camminava prima di sedersi, invece di ripartire fermo.
    ///
    /// Da chiamare SEMPRE da qualunque postazione (PilotStation,
    /// MedicalStation, EngineeringStation) subito dopo aver riattivato
    /// questo componente.
    /// </summary>
    public void ResetVelocity()
    {
        currentVelocity = Vector3.zero;
        verticalVelocity = -2f; // valore di riposo "a terra", stesso usato in HandleMovement()
    }

    /// <summary>
    /// Rev BR — riporta la stamina al massimo. Operazione distinta dal recupero naturale
    /// (HandleStamina): la usa l'Adrenaline (BR-b), sul client proprietario.
    /// </summary>
    public void RefillStamina()
    {
        currentStamina = Config.MaxStamina;
    }

    /// <summary>Rev BR — moltiplicatore di velocità dagli stati attivi (1 se il componente manca).</summary>
    private float MoveSpeedMultiplier =>
        statusEffects != null ? statusEffects.GetStatMultiplier(StatKind.MoveSpeed) : 1f;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
        playerInput = GetComponent<PlayerInput>();
        statusEffects = GetComponent<PlayerStatusEffects>();

        // Rev BT-a: senza asset il giocatore deve comunque muoversi — default dello SO + errore.
        if (movementConfig == null)
        {
            Debug.LogError("[PlayerController] PlayerMovementConfig non assegnato: uso i valori di default. " +
                           "Assegna Assets/Project/Scripts/Player/PlayerMovementConfig.asset nel Player prefab.");
            movementConfig = ScriptableObject.CreateInstance<PlayerMovementConfig>();
        }

        currentStamina = Config.MaxStamina;

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
        // Rev BT-a: rete di sicurezza sul rilascio dello sprint, anche sulla scala.
        VerifySprintRelease();

        // Rev BT-b: input della visuale convertito in gradi una volta per frame (mouse o stick).
        Vector2 lookDegrees = ComputeLookDegrees();

        // Check if on ladder
        if (currentLadder != null && currentLadder.IsPlayerOnLadder)
        {
            // On ladder - send input to ladder (Rev BT-b: gradi, stessa sensibilità che a piedi)
            currentLadder.HandleClimbing(moveInput.y, lookDegrees);
        }
        else
        {
            // Normal movement
            HandleMovement();
            HandleLook(lookDegrees);
            HandleStamina();
        }
    }

    // ===== LADDER PUBLIC METHODS =====

    public void SetCurrentLadder(Ladder ladder)
    {
        currentLadder = ladder;
    }

    /// <summary>
    /// Rev BT-a (Q71-a) — rete di sicurezza: se lo sprint risulta acceso ma l'azione "Sprint" non
    /// è più premuta, lo spegne. Sostituisce VerifySprintState, che leggeva Keyboard.current
    /// (Shift) e spegneva lo sprint del gamepad. Il rilascio normale arriva da OnSprint.
    /// </summary>
    private void VerifySprintRelease()
    {
        if (!sprintPressed) return;

        if (sprintAction == null && playerInput != null && playerInput.actions != null)
            sprintAction = playerInput.actions.FindAction(SprintActionName, throwIfNotFound: false);

        if (sprintAction != null && !sprintAction.IsPressed())
            sprintPressed = false;
    }

    private void HandleMovement()
    {
        // Skip movement if CharacterController is disabled (e.g., on ladder)
        if (!characterController.enabled)
        {
            return;
        }

        PlayerMovementConfig config = Config;

        // Determine target speed
        float targetSpeed = config.WalkSpeed;

        if (IsSprinting)
        {
            targetSpeed = config.SprintSpeed;
        }
        else if (IsCrouching)
        {
            targetSpeed = config.CrouchSpeed;
        }

        // Rev BR: modificatori di velocità (Combat Stim)
        targetSpeed *= MoveSpeedMultiplier;

        // Calculate movement direction (horizontal only)
        Vector3 inputDirection = transform.right * moveInput.x + transform.forward * moveInput.y;
        Vector3 targetVelocity = inputDirection.normalized * targetSpeed;

        // Smooth acceleration/deceleration (horizontal only)
        float speedDelta = (targetVelocity.magnitude > currentVelocity.magnitude)
            ? config.Acceleration
            : config.Deceleration;
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
            verticalVelocity -= config.Gravity * Time.deltaTime;
        }

        // Clamp fall speed to terminal velocity
        verticalVelocity = Mathf.Max(verticalVelocity, config.TerminalVelocity);

        // Combine horizontal movement with vertical velocity
        Vector3 finalVelocity = currentVelocity;
        finalVelocity.y = verticalVelocity;

        // Move
        characterController.Move(finalVelocity * Time.deltaTime);
    }

    /// <summary>
    /// Rev BT-b (Q78-a) — converte l'input della visuale in gradi di rotazione per questo frame.
    /// Mouse: pixel × lookSensitivity (come prima). Stick: velocità angolare con curva di risposta
    /// sul modulo, × deltaTime. Il dispositivo viene da lookAction.activeControl: senza azione o
    /// senza controllo attivo si usa il ramo stick, come SutureInteraction (il valore resta
    /// comunque limitato a stickLookSpeed).
    /// </summary>
    private Vector2 ComputeLookDegrees()
    {
        if (lookAction == null && playerInput != null && playerInput.actions != null)
            lookAction = playerInput.actions.FindAction(LookActionName, throwIfNotFound: false);

        lookFromMouse = lookAction != null && lookAction.activeControl?.device is Mouse;

        if (lookFromMouse)
            return lookInput * lookSensitivity;

        float deflection = lookInput.magnitude;
        if (deflection <= 0f)
            return Vector2.zero;

        float curved = Mathf.Pow(Mathf.Min(deflection, 1f), stickLookExponent);
        return (lookInput / deflection) * (curved * stickLookSpeed * Time.deltaTime);
    }

    private void HandleLook(Vector2 lookDegrees)
    {
        // Only look when cursor is locked
        if (Cursor.lockState != CursorLockMode.Locked)
            return;

        // Horizontal rotation (player body)
        transform.Rotate(Vector3.up * lookDegrees.x);

        // Vertical rotation (camera only)
        verticalRotation -= lookDegrees.y;
        verticalRotation = Mathf.Clamp(verticalRotation, -maxLookAngle, maxLookAngle);

        if (cameraTransform != null)
        {
            cameraTransform.localRotation = Quaternion.Euler(verticalRotation, 0f, 0f);
        }
    }

    private void HandleStamina()
    {
        PlayerMovementConfig config = Config;

        if (IsSprinting)
        {
            currentStamina -= config.SprintStaminaDrain * Time.deltaTime;
            currentStamina = Mathf.Max(0f, currentStamina);
        }
        else
        {
            currentStamina += config.StaminaRecovery * Time.deltaTime;
            currentStamina = Mathf.Min(config.MaxStamina, currentStamina);
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

    /// <summary>
    /// Azione "Sprint" (Shift / L3), Press And Release (Rev BT-a · Q71-a): premuto → scatto,
    /// rilasciato → camminata. Tenere premuto su tastiera e gamepad.
    /// </summary>
    public void OnSprint(InputValue value)
    {
        sprintPressed = value.isPressed;
    }

    /// <summary>
    /// Azione "Crouch" (C / B), interruttore.
    /// Rev BT-b (Q81-a): ignorata quando questo componente è disattivato. PlayerInput usa
    /// SendMessages, che raggiunge anche i componenti disattivati: seduti a una postazione, al
    /// tablet, sul letto o in un pannello di riparazione (tutti contesti che disattivano
    /// PlayerController), il B che chiude il contesto (Cancel, stesso tasto) arrivava qui e
    /// accovacciava il giocatore, e lo stesso faceva C. L'evento arriva prima dell'Update che
    /// riattiva il controller, quindi la guardia lo scarta.
    /// </summary>
    public void OnCrouch(InputValue value)
    {
        if (!isActiveAndEnabled) return;

        if (value.isPressed)
        {
            crouchToggled = !crouchToggled;
        }
    }

    /// <summary>
    /// Rev BT-a (Q74-a) — guardia: l'azione "Debug" non ha più binding (prima era lo spazio, che
    /// lanciava una NullReferenceException perché il pannello di DeguAndTest non è assegnato in
    /// scena). Se un giorno le si ridà un tasto, funziona solo in Editor e Development Build e
    /// non lancia eccezioni se DeguAndTest manca.
    /// </summary>
    public void OnDebug(InputValue value)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (!value.isPressed) return;

        DeguAndTest deguAndTest = FindAnyObjectByType<DeguAndTest>();
        if (deguAndTest == null) return;

        deguAndTest.panel();
#endif
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

    [Header("Debug")]
    [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development Build). Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugUI = false;

    // Debug
    private void OnGUI()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (!showDebugUI) return;

        GUI.Label(new Rect(10, 10, 300, 20), $"Stamina: {currentStamina:F1}/{MaxStamina}");
        GUI.Label(new Rect(10, 30, 300, 20), $"Speed: {currentVelocity.magnitude:F2} m/s (×{MoveSpeedMultiplier:F2})");
        GUI.Label(new Rect(10, 50, 300, 20), $"Sprinting: {IsSprinting}");
        GUI.Label(new Rect(10, 70, 300, 20), $"Crouching: {IsCrouching}");
        GUI.Label(new Rect(10, 90, 300, 20), $"Look: {(lookFromMouse ? "mouse" : "stick")}");   // Rev BT-b
#endif
    }
}