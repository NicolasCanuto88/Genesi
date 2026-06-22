using System.Collections;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PilotStation — Milestone 2, esteso in Milestone 3 Blocco 2.
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
/// BUG FIX — uscita con Esc non funzionava:
///   EnterStation ora recupera il PlayerInput con fallback su
///   interactor.GetComponent<PlayerInput>() se playerInputReference non è
///   assegnato in Inspector (stesso pattern già in MedicalStation —
///   mancava qui, lasciando cancelAction null silenziosamente).
///
/// DECISIONE ARCHITETTURALE (Blocco 2, dopo ampia sperimentazione — vedi
/// SESSION_HANDOFF per la cronologia completa): "Nave" NON si muove MAI
/// fisicamente. Una nave che trasla/ruota davvero nel mondo, con player
/// agganciati ad essa, si è rivelata un terreno di bug profondi e
/// interconnessi (tremolio, oscillazioni, CharacterController che non
/// segue piattaforme in movimento — limite documentato di Unity stesso,
/// non un nostro bug). La soluzione adottata: la nave resta ferma,
/// "Velocità"/NavigationState restano concetti puramente LOGICI (HUD,
/// consumo carburante, ETA) — quando in Blocco 3 esisteranno asteroidi/
/// relitti/stazioni visivi, saranno LORO a muoversi in senso inverso
/// rispetto a questa velocità logica (la nave resta il centro fisso del
/// mondo) — pattern comune nei giochi spaziali, evita anche problemi di
/// precisione a coordinate molto grandi. Vedi ShipMovement.cs per il
/// dettaglio.
///
/// Conseguenza pratica per QUESTO file: nessuna matematica di posizione
/// relativa alla nave è necessaria — il player non deve mai essere
/// "agganciato" a nulla, perché nulla si muove. Identico, in questo, al
/// comportamento di MedicalStation/EngineeringStation.
///
/// BLOCCO 2 (M3) — pilotaggio MANUALE (solo stato logico):
///   - Mentre seduto e NavigationState == Manual, la X del Look stick/mouse
///     (azione "Look" del Player Action Map — libera, perché PlayerController
///     è disabilitato qui e non la consuma più) pilota lo yaw LOGICO della
///     nave via ShipMovement.Instance.SetManualSteerInput() — usato in
///     futuro per ruotare in senso inverso il mondo esterno (Blocco 3+),
///     non per ruotare "Nave" stessa. Azzerata quando si esce da MANUAL o
///     dalla postazione.
///   - Camera: passa automaticamente da vista cockpit (dashboard, esistente)
///     a vista esterna in terza persona ancorata a shipChaseCamPoint (figlio
///     fisso di "Nave") quando NavState diventa Manual, e torna alla vista
///     cockpit quando NavState cambia o si esce dalla postazione. Nessuna
///     dipendenza da Cinemachine — il progetto non lo ha installato
///     (verificato in Packages/manifest.json), quindi si riusa lo stesso
///     approccio "Camera.transform diretto" già usato da LookAtCockpitRoutine.
///     Nota: finché non esiste contenuto esterno visivo che si muove
///     (Blocco 3+), questa vista mostrerà la nave ferma dall'esterno — non
///     dinamica come sarà una volta aggiunto il mondo inverso, ma corretta
///     e pronta per quando arriverà.
///
/// DIPENDE DA: PropulsionSystem ✅ · FTLDrive ✅ · ShieldSystem ✅
///   ShipMovement (Blocco 2, nuovo) · shipChaseCamPoint da creare in Editor
///   come figlio fisso di "Nave".
/// Multiplayer (M3+): aggiungere role-check (solo il Pilota può usare questa postazione).
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class PilotStation : MonoBehaviour, IInteractable
{
    // ── HUD ───────────────────────────────────────────────────────────────
    [Header("HUD")]
    [SerializeField] private PilotHUD pilotHUD;
    [SerializeField] private Canvas hudCanvas;

    // ── Player Positioning ────────────────────────────────────────────────
    [Header("Player Positioning")]
    [SerializeField] private Transform playerSnapPoint;
    [SerializeField] private Transform cameraLookAtPoint;
    [SerializeField] private float snapTransitionSpeed = 5f;
    [SerializeField] private float cameraTransitionSpeed = 8f;

    // ── Camera terza persona (Blocco 2) ─────────────────────────────────────
    [Header("Camera Terza Persona — MANUAL (Blocco 2)")]
    [Tooltip("⚠️ EDITOR: crea un GameObject vuoto figlio di 'Nave' (statico, " +
             "la nave non si muove mai — vedi nota architetturale in testa al " +
             "file), posizionato dietro/sopra la plancia, orientato lungo la " +
             "prua. Nessun asset necessario — solo un Transform vuoto. Se " +
             "lasciato vuoto, il pilotaggio MANUAL resta in prima persona " +
             "(vista cockpit invariata).")]
    [SerializeField] private Transform shipChaseCamPoint;

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

    private PlayerController playerController;
    private CharacterController characterController;
    private Camera playerCamera;
    private InputAction cancelAction;
    private InputAction lookAction; // Blocco 2 — steering MANUAL (logico)

    private Vector3 originalPlayerPosition;
    private Quaternion originalPlayerRotation;
    private Quaternion originalCameraRotation;
    private Quaternion targetCameraLocalRotation;
    private bool wasPlayerControllerEnabled;

    // Blocco 2 — stato camera terza persona / polling NavState
    private Transform originalCameraParent;
    private Vector3 originalCameraLocalPosition;
    private bool isChaseCamActive;
    private NavigationState lastPolledNavState;

    private float interactionCooldown;
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

        if (isUsingStation && !isTransitioning)
            PollManualFlightState();
    }

    // =========================================================================
    // IInteractable
    // =========================================================================

    public void Interact(GameObject interactor)
    {
        if (interactionCooldown > 0f) return;

        if (isUsingStation) TryExitStation();
        else EnterStation(interactor);
    }

    public bool CanInteract() => !isUsingStation && interactionCooldown <= 0f;
    public string GetInteractionPrompt() => "Console Pilota";
    public bool IsContinuousInteraction() => false;
    public void OnLookEnter() { }
    public void OnLookExit() { }

    // =========================================================================
    // ENTRATA
    // =========================================================================

    private void EnterStation(GameObject interactor)
    {
        playerController = interactor.GetComponent<PlayerController>();
        characterController = interactor.GetComponent<CharacterController>();
        playerCamera = interactor.GetComponentInChildren<Camera>();

        if (playerController == null || playerCamera == null)
        {
            Debug.LogWarning("[PilotStation] Interactor privo di PlayerController o Camera.");
            return;
        }

        // Recupera Cancel/Look action da PlayerInput: usa la reference assegnata
        // in Inspector se presente, altrimenti fallback sul PlayerInput
        // dell'interactor stesso (stesso pattern MedicalStation.EnterStation —
        // bug fix: prima mancava il fallback, lasciando cancelAction null se il
        // campo Inspector non era assegnato, ed Esc non usciva mai).
        PlayerInput pi = playerInputReference != null
            ? playerInputReference
            : interactor.GetComponent<PlayerInput>();

        if (pi != null)
        {
            cancelAction = pi.actions.FindAction("Cancel");
            // Blocco 2: azione "Look" del Player map — libera mentre
            // PlayerController è disabilitato (non la consuma più nessuno),
            // riusata qui per lo steering MANUAL logico. Nessuna modifica
            // all'asset Input Actions necessaria.
            lookAction = pi.actions.FindAction("Look");
        }

        // Salva stato originale del player — posizione/rotazione ASSOLUTE
        // nel mondo, semplici: "Nave" non si muove mai (vedi nota
        // architetturale in testa al file), quindi non serve nessuna
        // matematica relativa alla nave.
        originalPlayerPosition = interactor.transform.position;
        originalPlayerRotation = interactor.transform.rotation;
        originalCameraRotation = playerCamera.transform.localRotation;
        wasPlayerControllerEnabled = playerController.enabled;

        // Blocco 2 — stato camera per il possibile swap a terza persona
        originalCameraParent = playerCamera.transform.parent;
        originalCameraLocalPosition = playerCamera.transform.localPosition;
        isChaseCamActive = false;
        lastPolledNavState = PropulsionSystem.Instance != null
            ? PropulsionSystem.Instance.CurrentNavState
            : NavigationState.Anchored;

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

        Transform t = interactor.transform;
        Vector3 targetPos = playerSnapPoint != null ? playerSnapPoint.position : transform.position;
        Quaternion targetRot = playerSnapPoint != null ? playerSnapPoint.rotation : transform.rotation;

        for (float p = 0f; p < 1f; p += Time.deltaTime * snapTransitionSpeed)
        {
            t.position = Vector3.Lerp(originalPlayerPosition, targetPos, p);
            t.rotation = Quaternion.Lerp(originalPlayerRotation, targetRot, p);
            yield return null;
        }

        t.position = targetPos;
        t.rotation = targetRot;
        isTransitioning = false;

        if (cameraLookAtPoint != null)
            StartCoroutine(LookAtCockpitRoutine());
        else if (pilotHUD != null)
            pilotHUD.Open();
    }

    private IEnumerator LookAtCockpitRoutine()
    {
        Vector3 dir = (cameraLookAtPoint.position - playerCamera.transform.position).normalized;
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

        // Riporta SEMPRE la camera sotto il player prima di qualunque altra
        // logica di uscita: TransitionFromStation lavora su
        // playerCamera.transform.localRotation assumendo che il parent sia
        // di nuovo quello originale, altrimenti il lerp finale sarebbe nello
        // spazio locale sbagliato (quello di shipChaseCamPoint).
        ExitThirdPersonChaseCam();
        ShipMovement.Instance?.SetManualSteerInput(0f);

        interactionCooldown = COOLDOWN_DURATION;
        isUsingStation = false;

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

        t.position = originalPlayerPosition;
        t.rotation = originalPlayerRotation;
        playerCamera.transform.localRotation = originalCameraRotation;

        playerController.enabled = wasPlayerControllerEnabled;

        // FIX — currentVelocity è un campo persistente in PlayerController
        // per smussare accelerazione/decelerazione: disabilitare il
        // componente non lo azzera, resta congelato alla velocità che il
        // player aveva nell'istante di EnterStation. Senza questo,
        // riattivando il componente il player riprenderebbe per qualche
        // frame a muoversi nella direzione di prima di sedersi. Stesso
        // identico bug latente in MedicalStation/EngineeringStation —
        // stesso fix applicabile lì allo stesso modo.
        playerController.ResetVelocity();

        if (characterController != null)
            characterController.enabled = true;
    }

    // =========================================================================
    // PILOTAGGIO MANUALE — steering logico + camera terza persona (Blocco 2)
    // =========================================================================

    /// <summary>
    /// Eseguito ogni frame mentre seduti (non in transizione). Rileva i
    /// cambi di NavigationState per scambiare la camera cockpit/terza
    /// persona, e mentre MANUAL è attivo inoltra la X del Look stick/mouse
    /// a ShipMovement come yaw LOGICO di sterzata (non muove "Nave" — vedi
    /// nota architetturale in testa al file).
    /// </summary>
    private void PollManualFlightState()
    {
        var ps = PropulsionSystem.Instance;
        NavigationState navState = ps != null ? ps.CurrentNavState : NavigationState.Anchored;

        if (navState != lastPolledNavState)
        {
            if (navState == NavigationState.Manual)
                EnterThirdPersonChaseCam();
            else if (lastPolledNavState == NavigationState.Manual)
                ExitThirdPersonChaseCam();

            lastPolledNavState = navState;
        }

        if (navState == NavigationState.Manual)
        {
            float steerX = lookAction != null ? lookAction.ReadValue<Vector2>().x : 0f;
            ShipMovement.Instance?.SetManualSteerInput(steerX);
        }
        else
        {
            ShipMovement.Instance?.SetManualSteerInput(0f);
        }
    }

    /// <summary>
    /// Riparenta la camera del player su shipChaseCamPoint (figlio fisso di
    /// "Nave" — la nave non si muove mai, quindi questo punto è
    /// staticamente corretto) per la vista esterna in terza persona. No-op
    /// se shipChaseCamPoint non è assegnato in Inspector — in quel caso il
    /// pilotaggio MANUAL resta in vista cockpit (degradazione elegante,
    /// nessun errore).
    /// </summary>
    private void EnterThirdPersonChaseCam()
    {
        if (isChaseCamActive || shipChaseCamPoint == null || playerCamera == null) return;

        playerCamera.transform.SetParent(shipChaseCamPoint, worldPositionStays: false);
        playerCamera.transform.localPosition = Vector3.zero;
        playerCamera.transform.localRotation = Quaternion.identity;
        isChaseCamActive = true;
    }

    /// <summary>
    /// Riporta la camera sotto il player (parent/posizione originali salvati
    /// in EnterStation), pronta per essere ri-orientata da LookAtCockpitRoutine
    /// o dal lerp finale di TransitionFromStation. Idempotente — sicuro da
    /// chiamare anche se la chase cam non era attiva.
    /// </summary>
    private void ExitThirdPersonChaseCam()
    {
        if (!isChaseCamActive || playerCamera == null) return;

        playerCamera.transform.SetParent(originalCameraParent, worldPositionStays: false);
        playerCamera.transform.localPosition = originalCameraLocalPosition;
        // La localRotation corretta (vista cockpit) viene ripristinata da
        // LookAtCockpitRoutine/TransitionFromStation — qui basta essere nel
        // parent giusto prima che quei lerp lavorino.
        isChaseCamActive = false;
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