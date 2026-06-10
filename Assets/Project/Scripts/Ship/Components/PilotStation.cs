using System.Collections;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PilotStation — Milestone 2
/// Postazione fisica del Pilota nel cockpit.
///
/// PATTERN:
///   Identico a MedicalStation / EngineeringStation:
///   - IInteractable → rilevata da InteractionSystem via raycast
///   - Snap player con lerp verso playerSnapPoint
///   - Camera ruota verso la plancia al termine della transizione
///   - Uscita via Cancel (Esc / B gamepad) con cooldown 0.5s
///   - Nessun VirtualCursor (PilotHUD è display-only, nessun click UI)
///
/// AZIONI PILOTA (InputActionReference — assegna in Inspector):
///   toggleAutopilotAction  → AUTOPILOT ↔ COASTING       [consigliato: A / LStick Click]
///   toggleManualAction     → MANUAL ↔ COASTING           [consigliato: Q / RStick Click]
///   shieldToggleAction     → ShieldSystem.TryActivate()  [F / LB]
///   ftlJumpAction          → FTLDrive.TryInitiateJump()  [G / Y]
///
/// LOGICA USCITA:
///   MANUAL attivo     → RequestNavigationState(Coasting)   [nessuno al timone]
///   AUTOPILOT attivo  → lasciato invariato                 [nave continua da sola]
///   ANCHORED/COASTING → lasciato invariato
///   FTL_CHARGING/JUMPING → uscita bloccata
///
/// REGOLA INVARIANTE:
///   PilotStation è l'unico punto da cui chiamare
///   FTLDrive.TryInitiateJump() e PropulsionSystem.RequestNavigationState().
///
/// DIPENDE DA: PropulsionSystem ✅ · FTLDrive ✅ · ShieldSystem ✅
/// Multiplayer (M3+): aggiungere role-check (solo il Pilota può usare questa postazione).
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class PilotStation : MonoBehaviour, IInteractable
{
    // ── HUD ───────────────────────────────────────────────────────────────
    [Header("HUD")]
    [SerializeField] private PilotHUD pilotHUD;
    [SerializeField] private Canvas   hudCanvas;

    // ── Player Positioning ────────────────────────────────────────────────
    [Header("Player Positioning")]
    [SerializeField] private Transform playerSnapPoint;
    [SerializeField] private Transform cameraLookAtPoint;
    [SerializeField] private float     snapTransitionSpeed   = 5f;
    [SerializeField] private float     cameraTransitionSpeed = 8f;

    // ── Input ─────────────────────────────────────────────────────────────
    [Header("Input — Cancel (PlayerInput reference)")]
    [SerializeField] private PlayerInput playerInputReference;

    [Header("Input — Pilot Actions (assign InputActionReference in Inspector)")]
    [Tooltip("Toggle Autopilot ↔ Coasting. Es: A (KB) / LStick Click (GP)")]
    [SerializeField] private InputActionReference toggleAutopilotAction;
    [Tooltip("Toggle Manual ↔ Coasting. Es: Q (KB) / RStick Click (GP)")]
    [SerializeField] private InputActionReference toggleManualAction;
    [Tooltip("Attiva/disattiva scudi — esclusiva del Pilota. F (KB) / LB (GP)")]
    [SerializeField] private InputActionReference shieldToggleAction;
    [Tooltip("Avvia salto FTL — mai automatico. G (KB) / Y (GP)")]
    [SerializeField] private InputActionReference ftlJumpAction;

    // ── Stato interno ─────────────────────────────────────────────────────
    private bool isUsingStation;
    private bool isTransitioning;

    private PlayerController    playerController;
    private CharacterController characterController;
    private Camera              playerCamera;
    private InputAction         cancelAction;

    private Vector3    originalPlayerPosition;
    private Quaternion originalPlayerRotation;
    private Quaternion originalCameraRotation;
    private Quaternion targetCameraLocalRotation;
    private bool       wasPlayerControllerEnabled;

    private float       interactionCooldown;
    private const float COOLDOWN_DURATION = 0.5f;

    // =========================================================================
    // LIFECYCLE
    // =========================================================================

    private void Awake()
    {
        GetComponent<BoxCollider>().isTrigger = true;
    }

    private void Start()
    {
        if (hudCanvas != null && Camera.main != null)
            hudCanvas.worldCamera = Camera.main;
    }

    private void Update()
    {
        if (interactionCooldown > 0f)
            interactionCooldown -= Time.deltaTime;

        if (isUsingStation && cancelAction != null && cancelAction.WasPressedThisFrame())
            TryExitStation();
    }

    // =========================================================================
    // IInteractable
    // =========================================================================

    public void Interact(GameObject interactor)
    {
        if (interactionCooldown > 0f) return;

        if (isUsingStation) TryExitStation();
        else                EnterStation(interactor);
    }

    public bool   CanInteract()             => !isUsingStation && interactionCooldown <= 0f;
    public string GetInteractionPrompt()    => "Console Pilota";
    public bool   IsContinuousInteraction() => false;
    public void   OnLookEnter()             { }
    public void   OnLookExit()              { }

    // =========================================================================
    // ENTRATA
    // =========================================================================

    private void EnterStation(GameObject interactor)
    {
        playerController    = interactor.GetComponent<PlayerController>();
        characterController = interactor.GetComponent<CharacterController>();
        playerCamera        = interactor.GetComponentInChildren<Camera>();

        if (playerController == null || playerCamera == null)
        {
            Debug.LogWarning("[PilotStation] Interactor privo di PlayerController o Camera.");
            return;
        }

        // Cancel action dal PlayerInput reference (stesso pattern MedicalStation)
        if (playerInputReference != null)
            cancelAction = playerInputReference.actions.FindAction("Cancel");

        // Salva stato originale del player
        originalPlayerPosition     = interactor.transform.position;
        originalPlayerRotation     = interactor.transform.rotation;
        originalCameraRotation     = playerCamera.transform.localRotation;
        wasPlayerControllerEnabled = playerController.enabled;

        playerController.enabled = false;
        if (characterController != null)
            characterController.enabled = false;

        isUsingStation = true;

        // Registra callback azioni pilota
        BindPilotActions();

        // Attiva HUD
        if (pilotHUD != null)
            pilotHUD.gameObject.SetActive(true);

        StartCoroutine(TransitionToStation(interactor));
    }

    private IEnumerator TransitionToStation(GameObject interactor)
    {
        isTransitioning = true;

        Transform  t         = interactor.transform;
        Vector3    targetPos = playerSnapPoint != null ? playerSnapPoint.position : transform.position;
        Quaternion targetRot = playerSnapPoint != null ? playerSnapPoint.rotation : transform.rotation;

        for (float p = 0f; p < 1f; p += Time.deltaTime * snapTransitionSpeed)
        {
            t.position = Vector3.Lerp(originalPlayerPosition, targetPos, p);
            t.rotation = Quaternion.Lerp(originalPlayerRotation, targetRot, p);
            yield return null;
        }

        t.position      = targetPos;
        t.rotation      = targetRot;
        isTransitioning = false;

        if (cameraLookAtPoint != null)
            StartCoroutine(LookAtCockpitRoutine());
        else if (pilotHUD != null)
            pilotHUD.Open();
    }

    private IEnumerator LookAtCockpitRoutine()
    {
        Vector3    dir      = (cameraLookAtPoint.position - playerCamera.transform.position).normalized;
        Quaternion worldTgt = Quaternion.LookRotation(dir);
        targetCameraLocalRotation =
            Quaternion.Inverse(playerCamera.transform.parent.rotation) * worldTgt;

        for (float p = 0f; p < 1f; p += Time.deltaTime * cameraTransitionSpeed)
        {
            playerCamera.transform.localRotation = Quaternion.Lerp(
                playerCamera.transform.localRotation,
                targetCameraLocalRotation,
                p);
            yield return null;
        }

        playerCamera.transform.localRotation = targetCameraLocalRotation;

        if (pilotHUD != null)
            pilotHUD.Open();
    }

    // =========================================================================
    // USCITA
    // =========================================================================

    private void TryExitStation()
    {
        if (!isUsingStation || isTransitioning) return;

        // Blocco uscita durante FTL Charging / Jumping — il Pilota deve restare
        if (FTLDrive.Instance != null)
        {
            var ftlState = FTLDrive.Instance.CurrentState;
            if (ftlState == FTLState.Charging || ftlState == FTLState.Jumping)
            {
                Debug.LogWarning("[PilotStation] Uscita bloccata — FTL in corso. Attendere la fine della sequenza.");
                return;
            }
        }

        interactionCooldown = COOLDOWN_DURATION;
        isUsingStation      = false;

        // MANUAL attivo → COASTING: nessuno al timone, la nave mantiene l'inerzia
        if (PropulsionSystem.Instance != null
            && PropulsionSystem.Instance.CurrentNavState == NavigationState.Manual)
        {
            PropulsionSystem.Instance.RequestNavigationState(NavigationState.Coasting);
            Debug.Log("[PilotStation] Pilota lascia la postazione (MANUAL → COASTING).");
        }

        UnbindPilotActions();

        if (pilotHUD != null)
        {
            pilotHUD.Close();
            pilotHUD.gameObject.SetActive(false);
        }

        StartCoroutine(TransitionFromStation());
    }

    private IEnumerator TransitionFromStation()
    {
        if (playerController == null) yield break;

        Transform t = playerController.transform;

        for (float p = 0f; p < 1f; p += Time.deltaTime * snapTransitionSpeed)
        {
            t.position = Vector3.Lerp(t.position, originalPlayerPosition, p);
            t.rotation = Quaternion.Lerp(t.rotation, originalPlayerRotation, p);
            playerCamera.transform.localRotation = Quaternion.Lerp(
                playerCamera.transform.localRotation,
                originalCameraRotation,
                p);
            yield return null;
        }

        t.position                           = originalPlayerPosition;
        t.rotation                           = originalPlayerRotation;
        playerCamera.transform.localRotation = originalCameraRotation;

        playerController.enabled = wasPlayerControllerEnabled;
        if (characterController != null)
            characterController.enabled = true;
    }

    // =========================================================================
    // AZIONI PILOTA — bind / unbind
    // =========================================================================

    private void BindPilotActions()
    {
        if (toggleAutopilotAction?.action != null)
            toggleAutopilotAction.action.performed += OnToggleAutopilot;

        if (toggleManualAction?.action != null)
            toggleManualAction.action.performed += OnToggleManual;

        if (shieldToggleAction?.action != null)
            shieldToggleAction.action.performed += OnShieldToggle;

        if (ftlJumpAction?.action != null)
            ftlJumpAction.action.performed += OnFTLJump;
    }

    private void UnbindPilotActions()
    {
        if (toggleAutopilotAction?.action != null)
            toggleAutopilotAction.action.performed -= OnToggleAutopilot;

        if (toggleManualAction?.action != null)
            toggleManualAction.action.performed -= OnToggleManual;

        if (shieldToggleAction?.action != null)
            shieldToggleAction.action.performed -= OnShieldToggle;

        if (ftlJumpAction?.action != null)
            ftlJumpAction.action.performed -= OnFTLJump;
    }

    // =========================================================================
    // CALLBACK AZIONI PILOTA
    // =========================================================================

    /// <summary>
    /// Toggle AUTOPILOT ↔ COASTING.
    /// Regola invariante: mai automatico, solo dalla postazione.
    /// PropulsionSystem.RequestNavigationState() valida internamente
    /// se l'autopilota è disponibile (AsteroidField lo blocca).
    /// </summary>
    private void OnToggleAutopilot(InputAction.CallbackContext _)
    {
        var ps = PropulsionSystem.Instance;
        if (ps == null) return;

        if (ps.CurrentNavState == NavigationState.Autopilot)
        {
            // Spegni autopilota → inerzia
            ps.RequestNavigationState(NavigationState.Coasting);
        }
        else
        {
            if (!ps.AutopilotAvailable)
            {
                Debug.LogWarning("[PilotStation] Autopilota non disponibile — AsteroidField attivo. " +
                                 "Usare il volo MANUALE.");
                return;
            }
            ps.RequestNavigationState(NavigationState.Autopilot);
        }
    }

    /// <summary>
    /// Toggle MANUAL ↔ COASTING.
    /// Obbligatorio in ZoneEvent.AsteroidField (autopilota non disponibile).
    /// </summary>
    private void OnToggleManual(InputAction.CallbackContext _)
    {
        var ps = PropulsionSystem.Instance;
        if (ps == null) return;

        if (ps.CurrentNavState == NavigationState.Manual)
            ps.RequestNavigationState(NavigationState.Coasting);
        else
            ps.RequestNavigationState(NavigationState.Manual);
    }

    /// <summary>
    /// Toggle scudi ON/OFF — esclusiva del Pilota (tasto F / LB gamepad).
    /// TryActivate() gestisce internamente spin-up, off e stati non validi.
    /// </summary>
    private void OnShieldToggle(InputAction.CallbackContext _)
    {
        ShieldSystem.Instance?.TryActivate();
    }

    /// <summary>
    /// Avvia salto FTL. Mai automatico — solo dalla PilotStation.
    /// TryInitiateJump() nega se: non Ready, OFFLINE, non alimentato.
    /// </summary>
    private void OnFTLJump(InputAction.CallbackContext _)
    {
        FTLDrive.Instance?.TryInitiateJump();
    }
}
