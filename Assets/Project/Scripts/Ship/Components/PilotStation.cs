using System.Collections;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PilotStation — Milestone 2, esteso in Milestone 3 Blocco 2 (Rev Q/R),
/// Blocco 3 fase 2 (Rev T — modello di volo 3D con pitch e throttle),
/// e Fase 3 Blocco 3.1 Sotto-step 3.1.4 (input per docking minigioco con
/// context switching a tre action map).
///
/// PATTERN:
///   - IInteractable → rilevata da InteractionSystem via raycast
///   - Snap player con lerp verso playerSnapPoint
///   - Camera ruota verso la plancia al termine della transizione
///   - Uscita via Cancel (Esc / B gamepad) con cooldown 0.5s
///   - Nessun VirtualCursor (PilotHUD è display-only, nessun click UI)
///
/// ARCHITETTURA A TRE ACTION MAP (Fase 3.1.4):
///
///   Le action del pilota sono divise in 3 InputActionMap dedicati, gestiti
///   dinamicamente in base a NavigationState per evitare conflitti fisici
///   sui tasti tra volo normale (Pilot map) e minigioco docking:
///
///   ┌───────────────┬────────────────────────────────────────┬───────────────────────────────┐
///   │ Map           │ Contenuto                              │ Attivo                        │
///   ├───────────────┼────────────────────────────────────────┼───────────────────────────────┤
///   │ Pilot         │ PilotAutopilot (A), PilotManual (Q),   │ Seduti E                      │
///   │  (esistente)  │ PilotShieldToggle (F), PilotFTLJump(G) │ navState != Docking/Docked    │
///   ├───────────────┼────────────────────────────────────────┼───────────────────────────────┤
///   │ PilotAnchor   │ ToggleAnchor (T KB / X buttonWest GP)  │ Sempre mentre seduti          │
///   │  (nuovo)      │                                        │                               │
///   ├───────────────┼────────────────────────────────────────┼───────────────────────────────┤
///   │ PilotDocking  │ DockingStrafeXY (WASD + LStick),       │ Seduti E                      │
///   │  (nuovo)      │ DockingStrafeZ (Q/E + Triggers),       │ navState == Docking/Docked    │
///   │               │ ConfirmAnchor (Space + buttonSouth),   │                               │
///   │               │ CancelDocking (Esc + buttonEast)       │                               │
///   └───────────────┴────────────────────────────────────────┴───────────────────────────────┘
///
///   Perché ToggleAnchor sta nel suo map dedicato: deve essere premibile SIA
///   in Manual/Coasting (per iniziare docking) SIA in Docking/Docked (per fare
///   undock). Se stesse in Pilot map (spento in Docking) non permetterebbe
///   undock. Se stesse in PilotDocking (spento in Manual) non permetterebbe
///   start. → Terzo map "sempre-on-mentre-seduti".
///
///   Rebind di ToggleAnchor rispetto alla proposta originale (F/Y):
///   → T (KB) / X buttonWest (GP), per non collidere con Shield(F) / FTL(Y)
///   del map Pilot.
///
/// AZIONI PILOTA (InputActionReference — assegna in Inspector):
///   toggleAutopilotAction  → AUTOPILOT ↔ COASTING       [A / LStick Click]
///   toggleManualAction     → MANUAL ↔ COASTING           [Q / RStick Click]
///   shieldToggleAction     → ShieldSystem.TryActivate()  [F / LB]
///   ftlJumpAction          → FTLDrive.TryInitiateJump()  [G / Y buttonNorth]
///   throttleAction (Rev T) → asse W/S continuo [-1, +1]  [W/S KB / RT-LT GP]
///   toggleAnchorAction  (3.1.4) → context-sensitive: Manual/Coasting +
///          Anchorable → RequestStartDocking; Docking/Docked → RequestUndock
///                                                          [T KB / X GP]
///   dockingStrafeXY (3.1.4) → strafe piano perp POI       [WASD KB / LStick GP]
///   dockingStrafeZ  (3.1.4) → strafe assiale approach     [Q/E KB / LT-RT GP]
///   confirmAnchorAction (3.1.4) → Docking only:
///          conferma attracco se IsInAnchorTolerance      [Space KB / A GP]
///   cancelDockingAction (3.1.4) → Docking/Docked only:
///          undock (torna a Manual), resta seduto         [Esc KB / B GP]
///
/// SEMANTICA UNDOCK / CANCEL (3.1.4):
///   Undock via qualsiasi via (ToggleAnchor in Docking/Docked, CancelDocking,
///   o Esc del map UI) → RequestUndock → Manual (fallback Coasting).
///   AnchorSystem.RequestUndock gestisce internamente la scelta del target.
///
///   IMPORTANTE: durante Docking/Docked, il tasto Cancel del map UI (Esc)
///   NON alza il pilota dalla postazione. Fa solo undock, il pilota resta
///   seduto. Uscita dalla postazione richiede un secondo Esc dopo che si è
///   tornati a Manual/Coasting. Semantica confermata da design 3.1.4.
///
/// LOGICA USCITA (TryExitStation):
///   DOCKING/DOCKED    → RequestUndock (torna a Manual), NON alza il pilota
///                       (return dopo l'undock). Semantica confermata:
///                       "cancel durante docking = torna alla guida manuale,
///                       resta seduto".
///   MANUAL attivo     → RequestNavigationState(Coasting) [nessuno al timone]
///   AUTOPILOT attivo  → lasciato invariato               [nave continua da sola]
///   ANCHORED/COASTING → lasciato invariato
///   FTL_CHARGING/JUMPING → uscita bloccata
///
/// REGOLA INVARIANTE:
///   PilotStation è l'unico punto da cui chiamare
///   FTLDrive.TryInitiateJump(), PropulsionSystem.RequestNavigationState(),
///   PropulsionSystem.SetManualThrottleInput(), AnchorSystem.RequestStartDocking(),
///   AnchorSystem.RequestUndock() e DockingController.SetStrafeInput() /
///   RequestConfirmAnchor().
///
/// DECISIONE ARCHITETTURALE (Blocco 2): "Nave" NON si muove MAI fisicamente.
/// Vedi ShipMovement.cs. Il pilotaggio è puramente LOGICO — steering aggiorna
/// ShipMovement.LogicalRotation, throttle aggiorna PropulsionSystem.TargetSpeed,
/// strafe (Docking) aggiorna ShipMovement.LogicalPosition direttamente.
///
/// DIPENDE DA: PropulsionSystem ✅ · FTLDrive ✅ · ShieldSystem ✅
///   ShipMovement (Blocco 2) · shipChaseCamPoint da creare in Editor come
///   figlio fisso di "Nave" · action "Throttle" nell'asset InputActions ·
///   AnchorSystem (3.1.2) · DockingController (3.1.3) · nuovi map
///   `PilotAnchor` (1 action) e `PilotDocking` (4 action) nell'asset
///   InputActions (3.1.4 — vedi istruzioni Editor).
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class PilotStation : MonoBehaviour, IInteractable
{
    // ── HUD ───────────────────────────────────────────────────────────────
    [Header("HUD — Cockpit (World Space, sul monitor)")]
    [SerializeField] private PilotHUD pilotHUD;
    [SerializeField] private Canvas hudCanvas;

    [Header("HUD — Volo esterno MANUAL (Screen Space Overlay)")]
    [Tooltip("PilotFlightHUD (Rev T): HUD sovrapposto durante il volo in vista " +
             "terza persona. Toggle mutualmente esclusivo con PilotHUD — visibile " +
             "solo quando EnterThirdPersonChaseCam è attivo.")]
    [SerializeField] private PilotFlightHUD flightHUD;

    [Header("HUD — Minigioco Docking (Fase 3.1.5)")]
    [Tooltip("GameObject del Canvas World Space del minigioco di docking " +
             "(DockingMinigame_Canvas). Fratello del PilotHUD_Canvas sotto lo " +
             "stesso GameObject 'Monitor'. Attivato quando NavigationState " +
             "diventa Docking (contemporaneamente PilotHUD viene chiuso), " +
             "disattivato al ritorno a Manual/Coasting/Anchored/Autopilot/Docked " +
             "(PilotHUD riaperto). Se lasciato vuoto, nessuna UI del minigioco " +
             "viene mostrata — degradazione elegante, il minigioco resta " +
             "giocabile alla cieca leggendo il pannello Debug di DockingController.")]
    [SerializeField] private GameObject dockingMinigameCanvas;

    [Header("HUD — Minigioco Docking (Fase 3.1.5)")]
    [Tooltip("Canvas World Space del minigioco di attracco — fratello di " +
             "PilotHUD_Canvas sotto lo stesso GameObject 'Monitor'. Attivato " +
             "quando NavigationState == Docking, disattivato altrimenti. " +
             "Il PilotHUD_Canvas si spegne quando questo si accende (i due " +
             "monitor si alternano). In Docked si torna al PilotHUD (che " +
             "mostrerà 'DOCKED TO [POI]' in 3.1.6). Se null, il minigioco " +
             "gira senza feedback visivo — degradazione elegante.")]

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
             "lasciato vuoto, il pilotaggio MANUAL resta in prima persona.")]
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

    [Tooltip("Throttle W/S (Rev T). Value type / Axis 1D. Consigliato: " +
             "1D Axis Composite → Negative: <Keyboard>/s + <Gamepad>/leftTrigger; " +
             "Positive: <Keyboard>/w + <Gamepad>/rightTrigger. Se lasciato vuoto, " +
             "il throttle in MANUAL è sempre 0.")]
    [SerializeField] private InputActionReference throttleAction;

    [Header("Input — Docking (Fase 3.1.4)")]
    [Tooltip("Toggle context-sensitive dell'ancoraggio (map PilotAnchor). " +
             "Manual/Coasting + POI Anchorable → RequestStartDocking; " +
             "Docking/Docked → RequestUndock. Button type. Consigliato: T (KB) / " +
             "X buttonWest (GP). Il map PilotAnchor è sempre attivo mentre " +
             "seduti — necessario perché ToggleAnchor deve triggerarsi sia " +
             "in avvicinamento che a manovra in corso.")]
    [SerializeField] private InputActionReference toggleAnchorAction;

    [Tooltip("Strafe piano perpendicolare all'asse di approccio del POI durante " +
             "Docking (map PilotDocking). Value / Vector 2. Consigliato: 2D " +
             "Vector Composite WASD + <Gamepad>/leftStick. Attivo solo in " +
             "Docking/Docked (context switching automatico).")]
    [SerializeField] private InputActionReference dockingStrafeXYAction;

    [Tooltip("Strafe assiale (avvicina/allontana) lungo l'asse di approccio del POI " +
             "durante Docking (map PilotDocking). Value / Axis 1D. Consigliato: " +
             "1D Axis Composite → Negative: <Keyboard>/q + <Gamepad>/leftTrigger; " +
             "Positive: <Keyboard>/e + <Gamepad>/rightTrigger. Positive = " +
             "avvicinati al POI.")]
    [SerializeField] private InputActionReference dockingStrafeZAction;

    [Tooltip("Conferma attracco durante Docking (map PilotDocking): se " +
             "IsInAnchorTolerance, transiziona a Docked. Button type. " +
             "Consigliato: Space (KB) / A buttonSouth (GP).")]
    [SerializeField] private InputActionReference confirmAnchorAction;

    [Tooltip("Cancel del docking (map PilotDocking) — undock, resta seduto. " +
             "Semantica: torna a Manual (fallback Coasting) senza alzare il " +
             "pilota. Doppione funzionale del Cancel UI map (Esc) durante " +
             "Docking/Docked: entrambi fanno undock senza uscire dalla postazione. " +
             "Button type. Consigliato: Esc (KB) / B buttonEast (GP).")]
    [SerializeField] private InputActionReference cancelDockingAction;

    [Header("Sensibilità input (Rev T)")]
    [Tooltip("Moltiplicatore applicato al delta del mouse per l'action Look prima " +
             "di passarlo a ShipMovement. Il gamepad non è scalato — stick già in " +
             "[-1, +1]. Default 0.15 — 'nave grossa e pesante'.")]
    [SerializeField, Range(0.01f, 1f)] private float mouseSensitivity = 0.15f;

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

    // Fase 3.1.4 — cache dei tre action map per abilitazione/disabilitazione
    // dinamica. Risolti in EnterStation dalle action reference assegnate in
    // Inspector: una qualsiasi action di un dato map basta per risalire al map
    // via .action.actionMap.
    private InputActionMap pilotMap;
    private InputActionMap pilotAnchorMap;
    private InputActionMap pilotDockingMap;

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
        // FIX (Rev Q): NON impostare più hudCanvas.worldCamera = Camera.main
        // qui — assegnazione ora in EnterStation() per la camera del player
        // che entra effettivamente. Vedi commenti nei file MedicalStation /
        // EngineeringStation per la spiegazione completa.
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

        // FIX (Rev Q) — assegna la camera del giocatore che entra ora.
        if (hudCanvas != null)
            hudCanvas.worldCamera = playerCamera;

        // Recupera Cancel/Look action da PlayerInput
        PlayerInput pi = playerInputReference != null
            ? playerInputReference
            : interactor.GetComponent<PlayerInput>();

        if (pi != null)
        {
            cancelAction = pi.actions.FindAction("Cancel");
            lookAction = pi.actions.FindAction("Look");
        }

        // Salva stato originale del player
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

        // Registra callback azioni pilota + cache riferimenti ai tre map
        BindPilotActions();
        CachePilotMaps();

        // Fase 3.1.4 — imposta lo stato iniziale dei tre map in base al
        // navState corrente al momento della seduta:
        //  - Pilot map ON se non stiamo entrando durante Docking/Docked
        //  - PilotAnchor sempre ON
        //  - PilotDocking ON solo se stiamo entrando in Docking/Docked
        //    (raro edge case: un altro pilota ha lasciato la nave in Docked
        //    e ora prendo il timone)
        bool isInDockingState = lastPolledNavState == NavigationState.Docking
                             || lastPolledNavState == NavigationState.Docked;
        ApplyMapActivation(isInDockingState);

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

        // FASE 3.1.4 — SEMANTICA CANCEL DURANTE DOCKING/DOCKED:
        // Cancel (Esc / B) durante Docking o Docked NON alza il pilota dalla
        // postazione. Esegue solo undock (torna a Manual, fallback Coasting)
        // e il pilota resta seduto. Uscita dalla postazione richiede un
        // secondo Cancel dopo la transizione a Manual/Coasting.
        // Confermato da design: "Cancel_docking = torna alla guida manuale,
        // Exit_from_docking = stessa cosa".
        var ps = PropulsionSystem.Instance;
        if (ps != null
            && (ps.CurrentNavState == NavigationState.Docking
                || ps.CurrentNavState == NavigationState.Docked))
        {
            Debug.Log("[PilotStation] Cancel durante Docking/Docked — undock, resta seduto.");
            AnchorSystem.Instance?.RequestUndock();
            return; // Non alziamo il pilota.
        }

        // Riporta la camera sotto il player prima dell'uscita
        ExitThirdPersonChaseCam(restoreLookAtCockpit: false);

        // Azzera input pilota logici
        ShipMovement.Instance?.SetManualLookInput(Vector2.zero);
        PropulsionSystem.Instance?.SetManualThrottleInput(0f);
        DockingController.Instance?.SetStrafeInput(Vector3.zero);

        interactionCooldown = COOLDOWN_DURATION;
        isUsingStation = false;

        // MANUAL attivo → COASTING (nessuno al timone).
        // NB: se veniamo dall'undock durante docking (branch sopra), navState
        // ora è Manual → questo check trasformerà a Coasting correttamente
        // solo se il pilota preme Cancel una SECONDA volta dopo l'undock;
        // ma il branch sopra ritorna prima, quindi qui arriviamo solo da
        // Manual/Coasting/Autopilot/Anchored.
        if (PropulsionSystem.Instance != null
            && PropulsionSystem.Instance.CurrentNavState == NavigationState.Manual)
        {
            PropulsionSystem.Instance.RequestNavigationState(NavigationState.Coasting);
            Debug.Log("[PilotStation] Pilota lascia la postazione (MANUAL → COASTING).");
        }

        UnbindPilotActions();
        DisableAllPilotMaps();

        if (pilotHUD != null)
        {
            pilotHUD.Close();
            pilotHUD.gameObject.SetActive(false);
        }

        // Rev T — sicurezza: chiudi FlightHUD se ancora aperto
        if (flightHUD != null && flightHUD.gameObject.activeSelf)
            flightHUD.Close();

        // Fase 3.1.5 — sicurezza: nascondi il canvas del minigioco se ancora
        // attivo. Caso raro perché il branch iniziale di TryExitStation gestisce
        // Cancel-durante-Docking senza alzare il pilota, ma comunque idempotente.
        if (dockingMinigameCanvas != null && dockingMinigameCanvas.activeSelf)
            dockingMinigameCanvas.SetActive(false);

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

        // FIX — currentVelocity persistente in PlayerController: azzeriamo
        // per evitare che il player riprenda per qualche frame la velocità
        // di prima della seduta.
        playerController.ResetVelocity();

        if (characterController != null)
            characterController.enabled = true;
    }

    // =========================================================================
    // PILOTAGGIO — steering logico + throttle + strafe docking + camera terza persona
    // =========================================================================

    private void PollManualFlightState()
    {
        var ps = PropulsionSystem.Instance;
        NavigationState navState = ps != null ? ps.CurrentNavState : NavigationState.Anchored;

        // Camera swap + context switching dei map su transizioni di stato
        if (navState != lastPolledNavState)
        {
            // Camera: Manual ↔ non-Manual (chase cam)
            if (navState == NavigationState.Manual)
                EnterThirdPersonChaseCam();
            else if (lastPolledNavState == NavigationState.Manual)
                ExitThirdPersonChaseCam();

            // Fase 3.1.4 — context switching Pilot ↔ PilotDocking sulla base
            // di ingresso/uscita da Docking/Docked. PilotAnchor resta sempre
            // ON mentre seduti (non serve toccarlo qui).
            bool wasInDockingState = (lastPolledNavState == NavigationState.Docking
                                   || lastPolledNavState == NavigationState.Docked);
            bool isInDockingState = (navState == NavigationState.Docking
                                  || navState == NavigationState.Docked);

            if (wasInDockingState != isInDockingState)
                ApplyMapActivation(isInDockingState);

            // Fase 3.1.5 — HUD swap PilotHUD ↔ DockingMinigameCanvas.
            // Il DockingMinigameCanvas è attivo SOLO in Docking (fase attiva del
            // minigioco). In Docked il PilotHUD torna attivo per mostrare
            // "DOCKED TO [POI]" (3.1.6). In Manual la chase cam è già gestita
            // sopra (PilotHUD.Close + FlightHUD.Open) — se veniamo da Docking
            // e passiamo direttamente a Manual (edge case: cancel + toggle
            // manual rapido), il PilotHUD verrà comunque chiuso da
            // EnterThirdPersonChaseCam. Coerente.
            //
            // IMPORTANTE: uso gameObject.SetActive() sul PilotHUD, non solo
            // Close(): Close() disattiva la logica interna dell'HUD ma NON
            // nasconde il Canvas (che resta visualmente presente). Per far
            // "spegnere" il monitor durante Docking (i due canvas fratelli
            // sono sovrapposti), serve disabilitare il GameObject.
            bool wasInDockingActive = (lastPolledNavState == NavigationState.Docking);
            bool isInDockingActive = (navState == NavigationState.Docking);
            if (wasInDockingActive != isInDockingActive)
            {
                if (isInDockingActive)
                {
                    // Entrata in Docking: mostra minigame canvas, spegni PilotHUD
                    if (pilotHUD != null)
                    {
                        pilotHUD.Close();
                        pilotHUD.gameObject.SetActive(false);
                    }
                    if (dockingMinigameCanvas != null) dockingMinigameCanvas.SetActive(true);
                }
                else
                {
                    // Uscita da Docking: nascondi minigame canvas, riaccendi PilotHUD
                    // (a meno che stiamo transitando verso Manual — in quel caso
                    // la chase cam gestisce la vista esterna e PilotHUD deve
                    // restare chiuso; EnterThirdPersonChaseCam sopra ha già
                    // richiamato pilotHUD.Close ma il GameObject dobbiamo
                    // decidere qui se riaccenderlo).
                    if (dockingMinigameCanvas != null) dockingMinigameCanvas.SetActive(false);
                    if (pilotHUD != null && navState != NavigationState.Manual)
                    {
                        pilotHUD.gameObject.SetActive(true);
                        pilotHUD.Open();
                    }
                }
            }

            lastPolledNavState = navState;
        }

        if (navState == NavigationState.Manual)
        {
            Vector2 lookDelta = lookAction != null
                ? lookAction.ReadValue<Vector2>()
                : Vector2.zero;

            if (IsLookFromMouse())
                lookDelta *= mouseSensitivity;

            ShipMovement.Instance?.SetManualLookInput(lookDelta);

            float throttle = throttleAction != null && throttleAction.action != null
                ? throttleAction.action.ReadValue<float>()
                : 0f;
            ps?.SetManualThrottleInput(throttle);

            // Azzera strafe di docking (per pulizia server-side)
            DockingController.Instance?.SetStrafeInput(Vector3.zero);
        }
        else if (navState == NavigationState.Docking)
        {
            // Fase 3.1.4 — polling strafe RCS 3D per il minigioco di docking.
            Vector2 strafeXY = dockingStrafeXYAction != null && dockingStrafeXYAction.action != null
                ? dockingStrafeXYAction.action.ReadValue<Vector2>()
                : Vector2.zero;
            float strafeZ = dockingStrafeZAction != null && dockingStrafeZAction.action != null
                ? dockingStrafeZAction.action.ReadValue<float>()
                : 0f;
            DockingController.Instance?.SetStrafeInput(new Vector3(strafeXY.x, strafeXY.y, strafeZ));

            // Azzera Look + Throttle
            ShipMovement.Instance?.SetManualLookInput(Vector2.zero);
            ps?.SetManualThrottleInput(0f);
        }
        else
        {
            // Anchored, Coasting, Autopilot, Docked: nessun input attivo.
            ShipMovement.Instance?.SetManualLookInput(Vector2.zero);
            ps?.SetManualThrottleInput(0f);
            DockingController.Instance?.SetStrafeInput(Vector3.zero);
        }
    }

    private void EnterThirdPersonChaseCam()
    {
        if (isChaseCamActive || shipChaseCamPoint == null || playerCamera == null) return;

        playerCamera.transform.SetParent(shipChaseCamPoint, worldPositionStays: false);
        playerCamera.transform.localPosition = Vector3.zero;
        playerCamera.transform.localRotation = Quaternion.identity;
        isChaseCamActive = true;

        if (pilotHUD != null) pilotHUD.Close();
        if (flightHUD != null) flightHUD.Open();
    }

    private void ExitThirdPersonChaseCam(bool restoreLookAtCockpit = true)
    {
        if (!isChaseCamActive || playerCamera == null) return;

        playerCamera.transform.SetParent(originalCameraParent, worldPositionStays: false);
        playerCamera.transform.localPosition = originalCameraLocalPosition;
        isChaseCamActive = false;

        if (flightHUD != null) flightHUD.Close();
        if (pilotHUD != null) pilotHUD.Open();

        if (restoreLookAtCockpit && cameraLookAtPoint != null)
            StartCoroutine(LookAtCockpitRoutine());
    }

    // =========================================================================
    // GESTIONE ACTION MAP (Fase 3.1.4)
    // =========================================================================

    /// <summary>
    /// Cache dei riferimenti ai tre action map (Pilot, PilotAnchor,
    /// PilotDocking) partendo da una action di ciascuno. Chiamato una volta
    /// in EnterStation dopo BindPilotActions. Se una action reference è
    /// null in Inspector, il map corrispondente resta null e le operazioni
    /// di enable/disable sono no-op (degradazione elegante).
    /// </summary>
    private void CachePilotMaps()
    {
        pilotMap = toggleAutopilotAction?.action?.actionMap;
        pilotAnchorMap = toggleAnchorAction?.action?.actionMap;
        pilotDockingMap = dockingStrafeXYAction?.action?.actionMap;
    }

    /// <summary>
    /// Attiva/disattiva i tre map in base allo stato di docking.
    /// - PilotAnchor: sempre ON quando questa funzione è chiamata (siamo
    ///   seduti — solo lo Cancel del map UI è "globalmente" attivo, il
    ///   ToggleAnchor deve essere premibile in tutti gli stati seduti).
    /// - Pilot: ON quando NON in Docking/Docked
    /// - PilotDocking: ON quando in Docking/Docked
    /// </summary>
    private void ApplyMapActivation(bool inDockingState)
    {
        pilotAnchorMap?.Enable(); // Sempre ON mentre seduti

        if (inDockingState)
        {
            pilotMap?.Disable();
            pilotDockingMap?.Enable();
        }
        else
        {
            pilotMap?.Enable();
            pilotDockingMap?.Disable();
        }
    }

    /// <summary>
    /// Disabilita tutti e tre i map — chiamato in TryExitStation quando il
    /// pilota si alza dalla postazione. I map tornano a essere gestiti dalle
    /// impostazioni default dell'InputActionAsset (tipicamente disabled).
    /// </summary>
    private void DisableAllPilotMaps()
    {
        pilotMap?.Disable();
        pilotAnchorMap?.Disable();
        pilotDockingMap?.Disable();
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

        // Throttle action: NON serve enable esplicito qui — il map di
        // appartenenza (Pilot) sarà abilitato da ApplyMapActivation.
        // Tenuto per compatibilità retroattiva se qualcuno usa Throttle in
        // un map diverso dal Pilot (edge case).
        if (throttleAction?.action != null && !throttleAction.action.enabled)
            throttleAction.action.Enable();

        // Fase 3.1.4 — callback docking:
        //   button actions con callback performed (toggle/confirm/cancel)
        //   value actions lette in polling (strafe XY/Z)
        // Le action si abiliteranno tramite ApplyMapActivation (Enable del map
        // di appartenenza).
        if (toggleAnchorAction?.action != null)
            toggleAnchorAction.action.performed += OnToggleAnchor;

        if (confirmAnchorAction?.action != null)
            confirmAnchorAction.action.performed += OnConfirmAnchor;

        if (cancelDockingAction?.action != null)
            cancelDockingAction.action.performed += OnCancelDocking;
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

        // Fase 3.1.4 — unsubscribe docking
        if (toggleAnchorAction?.action != null)
            toggleAnchorAction.action.performed -= OnToggleAnchor;

        if (confirmAnchorAction?.action != null)
            confirmAnchorAction.action.performed -= OnConfirmAnchor;

        if (cancelDockingAction?.action != null)
            cancelDockingAction.action.performed -= OnCancelDocking;
    }

    // =========================================================================
    // CALLBACK AZIONI PILOTA
    // =========================================================================

    private void OnToggleAutopilot(InputAction.CallbackContext _)
    {
        var ps = PropulsionSystem.Instance;
        if (ps == null) return;

        if (ps.CurrentNavState == NavigationState.Autopilot)
        {
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

    private void OnToggleManual(InputAction.CallbackContext _)
    {
        var ps = PropulsionSystem.Instance;
        if (ps == null) return;

        if (ps.CurrentNavState == NavigationState.Manual)
            ps.RequestNavigationState(NavigationState.Coasting);
        else
            ps.RequestNavigationState(NavigationState.Manual);
    }

    private void OnShieldToggle(InputAction.CallbackContext _)
    {
        ShieldSystem.Instance?.TryActivate();
    }

    private void OnFTLJump(InputAction.CallbackContext _)
    {
        FTLDrive.Instance?.TryInitiateJump();
    }

    /// <summary>
    /// Fase 3.1.4 — Toggle context-sensitive dell'ancoraggio (map PilotAnchor).
    ///
    /// Manual/Coasting + POI Anchorable → RequestStartDocking
    /// Docking/Docked → RequestUndock (torna a Manual, fallback Coasting)
    /// Altri stati (Anchored / Autopilot) → warning log + no-op
    /// </summary>
    private void OnToggleAnchor(InputAction.CallbackContext _)
    {
        var ps = PropulsionSystem.Instance;
        var an = AnchorSystem.Instance;
        if (ps == null || an == null)
        {
            Debug.LogWarning("[PilotStation] ToggleAnchor: sistemi ship non pronti.");
            return;
        }

        var navState = ps.CurrentNavState;

        switch (navState)
        {
            case NavigationState.Manual:
            case NavigationState.Coasting:
                if (an.CurrentAnchorabilityState != AnchorabilityState.Anchorable)
                {
                    Debug.LogWarning($"[PilotStation] ToggleAnchor: nessun POI ancorabile " +
                                     $"(stato: {an.CurrentAnchorabilityState}).");
                    return;
                }
                an.RequestStartDocking();
                break;

            case NavigationState.Docking:
            case NavigationState.Docked:
                an.RequestUndock();
                break;

            default:
                Debug.LogWarning($"[PilotStation] ToggleAnchor: azione non valida in stato {navState}.");
                break;
        }
    }

    /// <summary>
    /// Fase 3.1.4 — Conferma attracco durante Docking. Attivo solo in
    /// NavigationState.Docking (per costruzione: il map PilotDocking è
    /// disabilitato negli altri stati). Il controllo interno resta per
    /// robustezza in caso di race condition (transizione di stato tra
    /// input firing e callback execution).
    /// </summary>
    private void OnConfirmAnchor(InputAction.CallbackContext _)
    {
        var ps = PropulsionSystem.Instance;
        var dc = DockingController.Instance;
        if (ps == null || dc == null)
        {
            Debug.LogWarning("[PilotStation] ConfirmAnchor: sistemi ship non pronti.");
            return;
        }

        if (ps.CurrentNavState != NavigationState.Docking)
        {
            Debug.LogWarning($"[PilotStation] ConfirmAnchor: attivo solo in Docking " +
                             $"(stato attuale: {ps.CurrentNavState}).");
            return;
        }

        dc.RequestConfirmAnchor();
    }

    /// <summary>
    /// Fase 3.1.4 — Cancel dedicato del minigioco docking (map PilotDocking).
    /// Fa esattamente ciò che fa il ToggleAnchor durante Docking/Docked:
    /// undock (torna a Manual, fallback Coasting), il pilota resta seduto.
    ///
    /// Doppione funzionale con il Cancel del map UI (Esc): quando il pilota
    /// preme Esc durante Docking/Docked, il TryExitStation gestisce la stessa
    /// semantica (vedi branch nell'inizio di TryExitStation). L'esistenza di
    /// entrambi non causa problemi — le due callback fanno lo stesso lavoro
    /// e RequestUndock è idempotente rispetto a chiamate doppie (il secondo
    /// tentativo trova già navState non in Docking/Docked e ritorna).
    /// </summary>
    private void OnCancelDocking(InputAction.CallbackContext _)
    {
        var ps = PropulsionSystem.Instance;
        var an = AnchorSystem.Instance;
        if (ps == null || an == null) return;

        if (ps.CurrentNavState != NavigationState.Docking
            && ps.CurrentNavState != NavigationState.Docked)
        {
            Debug.LogWarning($"[PilotStation] CancelDocking: non in Docking/Docked " +
                             $"(stato: {ps.CurrentNavState}).");
            return;
        }

        an.RequestUndock();
    }

    /// <summary>
    /// Rev T — determina se l'action Look è stata triggerata l'ultima volta
    /// da un mouse. Usato per applicare mouseSensitivity solo in quel caso.
    /// </summary>
    private bool IsLookFromMouse()
    {
        if (lookAction == null) return false;
        var device = lookAction.activeControl?.device;
        return device is UnityEngine.InputSystem.Mouse;
    }
}