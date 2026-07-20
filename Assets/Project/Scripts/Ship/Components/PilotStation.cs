using System.Collections;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PilotStation — Milestone 2, esteso in Milestone 3 Blocco 2 (Rev Q/R) e
/// Blocco 3 fase 2 (Rev T — modello di volo 3D con pitch e throttle).
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
///   throttleAction (Rev T) → asse W/S continuo [-1, +1]  [W/S KB / RT-LT GP]
///
/// LOGICA USCITA:
///   MANUAL attivo     → RequestNavigationState(Coasting)   [nessuno al timone]
///   AUTOPILOT attivo  → lasciato invariato                 [nave continua da sola]
///   ANCHORED/COASTING → lasciato invariato
///   FTL_CHARGING/JUMPING → uscita bloccata
///
/// REGOLA INVARIANTE:
///   PilotStation è l'unico punto da cui chiamare
///   FTLDrive.TryInitiateJump(), PropulsionSystem.RequestNavigationState()
///   e PropulsionSystem.SetManualThrottleInput().
///
/// DECISIONE ARCHITETTURALE (Blocco 2): "Nave" NON si muove MAI fisicamente.
/// Vedi ShipMovement.cs per il dettaglio completo. Il pilotaggio è puramente
/// LOGICO — steering aggiorna ShipMovement.LogicalRotation (Quaternion), il
/// throttle aggiorna PropulsionSystem.TargetSpeed. Il mondo esterno
/// (ExternalWorldFollower) scorre in senso inverso rispetto a questi valori.
///
/// BLOCCO 3 fase 2 (Rev T) — modello di volo 3D:
///   - Look action (X,Y) → ShipMovement.SetManualLookInput(Vector2)
///     Convenzione FPS: mouse su = muso su. Pitch clampato a ±80° internamente.
///   - Throttle action (asse) → PropulsionSystem.SetManualThrottleInput(float)
///     +1 = accelera in avanti, -1 = decelera, 0 = mantieni velocità (inerzia).
///   - Camera terza persona ancorata a shipChaseCamPoint (figlio fisso di
///     "Nave", statico). Quando NavState diventa Manual la camera swappa
///     dalla vista cockpit alla vista esterna.
///
/// DIPENDE DA: PropulsionSystem ✅ · FTLDrive ✅ · ShieldSystem ✅
///   ShipMovement (Blocco 2) · shipChaseCamPoint da creare in Editor come
///   figlio fisso di "Nave" · nuova action "Throttle" nell'asset InputActions.
/// Multiplayer (M3+): aggiungere role-check (solo il Pilota può usare questa postazione).
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
             "solo quando EnterThirdPersonChaseCam è attivo. Se lasciato vuoto, " +
             "il volo MANUAL non ha HUD visibile (degradazione elegante).")]
    [SerializeField] private PilotFlightHUD flightHUD;

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

    [Tooltip("Throttle W/S (Rev T). Value type / Axis 1D. Consigliato: " +
             "1D Axis Composite → Negative: <Keyboard>/s + <Gamepad>/leftTrigger; " +
             "Positive: <Keyboard>/w + <Gamepad>/rightTrigger. Se lasciato vuoto, " +
             "il throttle in MANUAL è sempre 0 — la nave mantiene sempre la velocità " +
             "corrente (utile per test isolati di sterzata).")]
    [SerializeField] private InputActionReference throttleAction;

    [Header("Sensibilità input (Rev T)")]
    [Tooltip("Moltiplicatore applicato al delta del mouse per l'action Look prima " +
             "di passarlo a ShipMovement. Il New Input System restituisce i movimenti " +
             "del mouse in unità arbitrarie che possono spesso saturare il clamp " +
             "[-1, +1] interno, rendendo la sterzata a mouse iper-reattiva rispetto " +
             "allo stick. Il gamepad NON è scalato — il suo stick è già in [-1, +1] " +
             "e la sensibilità naturale è corretta. Default 0.15 — da tarare in " +
             "playtest, ma parte come 'nave grossa e pesante'.")]
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
        // qui — stesso bug "camera sbagliata risolta una volta sola al
        // caricamento scena" già fixato in EngineeringStation.cs/
        // MedicalStation.cs (vedi quei file per la spiegazione completa).
        // PilotHUD è display-only (nessun click UI, vedi nota in testa al
        // file) quindi qui non causava pannelli "bloccati" — ma la stessa
        // assegnazione corretta avviene comunque in EnterStation() per
        // coerenza ed eventuali usi futuri non puramente display-only.
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

        // FIX (Rev Q) — stesso pattern di EngineeringStation.cs/MedicalStation.cs:
        // assegna la camera del giocatore che sta EFFETTIVAMENTE entrando ora,
        // non più Camera.main risolto una volta sola in Start().
        if (hudCanvas != null)
            hudCanvas.worldCamera = playerCamera;

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
        // false = non riorientare al monitor (TransitionFromStation ci pensa già).
        ExitThirdPersonChaseCam(restoreLookAtCockpit: false);

        // Azzera input pilota logici — evita che valori "congelati" restino
        // attivi sul server dopo che il Pilota si è alzato.
        ShipMovement.Instance?.SetManualLookInput(Vector2.zero);
        PropulsionSystem.Instance?.SetManualThrottleInput(0f);

        interactionCooldown = COOLDOWN_DURATION;
        isUsingStation = false;

        // MANUAL attivo → COASTING: nessuno al timone, la nave mantiene l'inerzia
        // (PropulsionSystem in COASTING congela TargetSpeed = CurrentSpeed, la
        // nave continua a viaggiare alla velocità corrente indefinitamente —
        // Rev T, coerente con "spazio vuoto, nessun attrito").
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

        // Rev T — sicurezza: se per qualche motivo il FlightHUD è ancora
        // aperto (es. uscita brutale mentre in MANUAL), chiudilo. In caso
        // normale ExitThirdPersonChaseCam sopra ha già chiamato Close().
        if (flightHUD != null && flightHUD.gameObject.activeSelf)
            flightHUD.Close();

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
    // PILOTAGGIO MANUALE — steering logico + throttle + camera terza persona
    // =========================================================================

    /// <summary>
    /// Eseguito ogni frame mentre seduti (non in transizione). Rileva i
    /// cambi di NavigationState per scambiare la camera cockpit/terza
    /// persona, e mentre MANUAL è attivo inoltra:
    ///   - Look (Vector2) → ShipMovement.SetManualLookInput (yaw + pitch logici)
    ///   - Throttle (float) → PropulsionSystem.SetManualThrottleInput
    ///
    /// Nulla di tutto questo muove "Nave" fisicamente — vedi nota
    /// architetturale in testa al file.
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
            // Look — yaw (X) e pitch (Y), convenzione FPS gestita in ShipMovement.
            // Sensibilità mouse applicata a monte: il New Input System restituisce
            // il delta mouse in unità che spesso saturano il clamp [-1, +1] di
            // ShipMovement, mentre lo stick gamepad è già naturalmente in [-1, +1].
            // Scaliamo solo se l'ultimo device che ha triggerato l'action è una
            // tastiera/mouse; altrimenti passiamo il vettore invariato.
            Vector2 lookDelta = lookAction != null
                ? lookAction.ReadValue<Vector2>()
                : Vector2.zero;

            if (IsLookFromMouse())
                lookDelta *= mouseSensitivity;

            ShipMovement.Instance?.SetManualLookInput(lookDelta);

            // Throttle — asse continuo W/S. Se non assegnato in Inspector,
            // resta 0 (nave mantiene velocità corrente) — degradazione elegante.
            float throttle = throttleAction != null && throttleAction.action != null
                ? throttleAction.action.ReadValue<float>()
                : 0f;
            ps?.SetManualThrottleInput(throttle);
        }
        else
        {
            ShipMovement.Instance?.SetManualLookInput(Vector2.zero);
            ps?.SetManualThrottleInput(0f);
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

        // Rev T — swap HUD cockpit → HUD di volo. Il PilotHUD della plancia
        // è dietro alla vista terza persona (non guardato dal Pilota), il
        // FlightHUD Screen Space Overlay è sempre visibile davanti.
        if (pilotHUD != null) pilotHUD.Close();
        if (flightHUD != null) flightHUD.Open();
    }

    /// <summary>
    /// Riporta la camera sotto il player (parent/posizione originali salvati
    /// in EnterStation). Se <paramref name="restoreLookAtCockpit"/> è true
    /// (default), riorienta anche la camera verso il monitor tramite
    /// LookAtCockpitRoutine — usato quando torniamo da MANUAL a COASTING
    /// restando seduti alla postazione. Se false, la camera resta come
    /// pare — usato in fase di uscita dalla postazione, dove il lerp
    /// finale di TransitionFromStation ci pensa già.
    ///
    /// Idempotente: sicuro da chiamare anche se la chase cam non era
    /// attiva (esce subito senza toccare nulla).
    /// </summary>
    private void ExitThirdPersonChaseCam(bool restoreLookAtCockpit = true)
    {
        if (!isChaseCamActive || playerCamera == null) return;

        playerCamera.transform.SetParent(originalCameraParent, worldPositionStays: false);
        playerCamera.transform.localPosition = originalCameraLocalPosition;
        isChaseCamActive = false;

        // Rev T — swap HUD di volo → HUD cockpit. FlightHUD scompare (siamo
        // di nuovo in vista cockpit), PilotHUD torna a mostrare la piena
        // strumentazione sul monitor.
        if (flightHUD != null) flightHUD.Close();
        if (pilotHUD != null) pilotHUD.Open();

        // Se richiesto (caso "resto seduto"), riorienta al monitor come
        // faceva TransitionToStation dopo lo snap: stesso LookAtCockpitRoutine
        // → stessa vista cockpit di quando ci si è appena seduti.
        if (restoreLookAtCockpit && cameraLookAtPoint != null)
            StartCoroutine(LookAtCockpitRoutine());
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

        // Throttle action: enable esplicito (le action Value non si abilitano
        // automaticamente solo dal reading — dipende dal PlayerInput setup).
        // Rev T: se non assegnato o già enabled, no-op.
        if (throttleAction?.action != null && !throttleAction.action.enabled)
            throttleAction.action.Enable();
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

        // Throttle action: non la disabilitiamo (potrebbe essere condivisa con
        // la Player map — non è nostra da spegnere in modo aggressivo). Il
        // polling in PollManualFlightState smetterà comunque di leggerla
        // quando isUsingStation == false.
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

    /// <summary>
    /// Rev T — determina se l'action Look è stata triggerata l'ultima volta
    /// da un mouse. Usato per applicare mouseSensitivity solo in quel caso.
    /// Il gamepad non ha bisogno di scaling (stick già in [-1, +1]).
    ///
    /// Nota: activeControl può essere null se nessun device ha appena
    /// scritto sull'action — trattiamo quel caso come "non mouse", scelta
    /// sicura (peggio non scaliamo il mouse per un frame che scaliamo lo
    /// stick per errore).
    /// </summary>
    private bool IsLookFromMouse()
    {
        if (lookAction == null) return false;
        var device = lookAction.activeControl?.device;
        return device is UnityEngine.InputSystem.Mouse;
    }
}