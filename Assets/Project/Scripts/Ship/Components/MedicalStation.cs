using System.Collections;
using SpaceSurvivor.Ship;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// MedicalStation — postazione del Corpsman nella Medibay (M2 · console di trattamento Rev BO-b,
/// sutura Rev BO-c).
///
/// Pattern di base identico a EngineeringStation:
///   - IInteractable → rilevata da InteractionSystem via raycast
///   - Snap player con lerp verso playerSnapPoint
///   - Camera ruota verso il monitor al termine dello snap (cameraLookAtPoint)
///   - Nessun VirtualCursor: niente click sul monitor
///   - Uscita via Cancel (Esc / B gamepad) con cooldown 0.5s
///
/// DUE VISTE SULLO STESSO MONITOR (Rev BO-b · Q10-a):
///   - Dashboard (display-only, M2): HP crew, O₂, Life Support, scorte mediche, più la
///     riga di stato del letto (treatmentStatusText).
///   - Trattamento: il minigame medico (StabilizationMinigameMedical, canvas complanare)
///     sostituisce il dashboard per la durata della sessione. Il letto è il "robot
///     chirurgico", la console lo teleopera.
///
/// SUTURA (Rev BO-c · Q6-a): il reticolo si muove con l'azione Look del PlayerInput del
/// giocatore seduto. La console la trova in EnterStation (come Cancel e Interact) e la
/// passa a Open; il minigame fa solo ReadValue (nessun handler). PlayerController è spento
/// da seduti: la visuale non si muove.
///
/// AVVIO (Q9-a): da seduti, con un paziente pronto la riga di stato mostra
/// "PATIENT READY — PRESS {interact} …". La pressione di Interact chiede il trattamento
/// al letto (RPC); il server decide l'operatore e la console apre il minigame quando
/// RecoveryBed.OperatorChanged conferma QUESTO client. Nessuna occupazione di rete
/// della postazione: l'unico stato conteso (chi opera) è già server-authority sul letto.
/// Due giocatori seduti insieme: uno solo diventa operatore, l'altro legge "IN PROGRESS".
///
/// FASI (Rev BP-b · Q20-a / Q21-a): ogni sessione cura una condizione. La console
/// sceglie la prossima fase con RecoveryBed.GetNextPhase (prima gli stati, poi gli HP),
/// la mostra nella riga di stato ("PRESS {interact} TO TREAT POISON") e la chiede al
/// server; il server la valida. Accettata, la console apre il minigame con la fase
/// RICHIESTA (il server accetta esattamente quella o rifiuta). Finita una fase si torna
/// al dashboard e la riga propone la successiva. Se restano solo stati che questo tier
/// non cura, la riga lo dice (NotTreatableAtTier).
///
/// CANCEL A DUE TEMPI (Q11-a): con il trattamento aperto Cancel lo interrompe e torna al
/// dashboard; senza trattamento Cancel alza il giocatore. Un solo consumatore per
/// pressione (protocollo Rev AF/AG): il minigame non ascolta Cancel da sé.
///
/// DEBITI LEGACY RISOLTI QUI (Q12-a — solo questa postazione, le altre restano a debito):
///   - tablet bloccato in apertura per tutta la permanenza (TabletStation.SetOpenBlocked);
///   - giocatore a terra da seduto → uscita forzata: trattamento chiuso, dashboard
///     spento, camera ripristinata, CharacterController riacceso (il suo collider serve
///     al raycast del defibrillatore, Rev BD), PlayerController NON riattivato (il
///     freeze Downed resta l'autorità). Il corpo resta alla console, nessun lerp;
///   - uscita normale: PlayerController riattivato solo se Alive (prima:
///     wasPlayerControllerEnabled, che poteva rimettere in moto un giocatore a terra).
///
/// Monitor unico: MedicalDashboardUI + minigame medico.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class MedicalStation : MonoBehaviour, IInteractable
{
    [Header("Dashboard")]
    [SerializeField] private MedicalDashboardUI dashboardUI;
    [SerializeField] private Canvas dashboardCanvas;

    [Header("Trattamento (Rev BO-b)")]
    [Tooltip("Il RecoveryBed che questa console comanda.")]
    [SerializeField] private RecoveryBed bed;

    [Tooltip("Il minigame medico sul monitor di questa postazione (MedicalMinigameCanvas, " +
             "complanare al dashboard).")]
    [SerializeField] private StabilizationMinigameMedical treatmentMinigame;

    [Tooltip("Riga di stato del letto sul dashboard (TextMeshPro, figlia del canvas del " +
             "dashboard: si nasconde con lui durante il trattamento). Facoltativa.")]
    [SerializeField] private TextMeshProUGUI treatmentStatusText;

    [SerializeField] private Color colorStatusReady = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorStatusBusy = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorStatusIdle = new Color(0.8f, 0.8f, 0.8f);

    [Header("Player Positioning")]
    [SerializeField] private Transform playerSnapPoint;
    [SerializeField] private Transform cameraLookAtPoint;
    [SerializeField] private float snapTransitionSpeed = 5f;
    [SerializeField] private float cameraTransitionSpeed = 8f;

    [Header("Input")]
    [SerializeField] private PlayerInput playerInputReference;

    [Header("Debug")]
    [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development). Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugUI = false;

    // ===== TESTI (Rev BO-b — nuovi testi in inglese; localizzazione IT a parte) =====

    private const string StatusNoPatient = "NO PATIENT ON THE BED";
    private const string StatusInProgress = "TREATMENT IN PROGRESS";
    private const string StatusStable = "PATIENT STABLE";
    private const string StatusNotTreatable = "CONDITION NOT TREATABLE AT THIS TIER";
    private const string StatusReadyTemplate = "PATIENT READY — PRESS {interact} TO TREAT {phase}";

    // ===== STATO INTERNO =====

    private bool isUsingStation = false;
    private bool isTransitioning = false;
    private bool treatmentOpen = false;

    private PlayerController playerController;
    private CharacterController characterController;
    private Camera playerCamera;
    private PlayerHealthSystem playerHealth;
    private TabletStation playerTablet;
    private InputAction cancelAction;
    private InputAction interactAction;
    private InputAction lookAction;      // Rev BO-c — reticolo della sutura

    private Vector3 originalPlayerPosition;
    private Quaternion originalPlayerRotation;
    private Quaternion originalCameraRotation;
    private Coroutine transitionRoutine;

    private float interactionCooldown = 0f;
    private const float COOLDOWN_DURATION = 0.5f;

    // Pausa tra due richieste di trattamento (e dopo la chiusura di una sessione:
    // la pressione che chiude non deve riaprire).
    private float requestCooldown = 0f;
    private const float REQUEST_COOLDOWN = 0.5f;

    // Rev BP-b — fase chiesta al server con l'ultima richiesta: se il server accetta,
    // la sessione cura esattamente questa.
    private RecoveryBed.TreatmentPhase requestedPhase = RecoveryBed.TreatmentPhase.None;

    private string lastStatusText;

    // Rig del giocatore locale per CanInteract (cache per istanza di LocalInstance).
    private PlayerHealthSystem rigHealth;
    private PlayerController rigController;
    private TabletStation rigTablet;

    // ===== AWAKE / START / ENABLE =====

    private void Awake()
    {
        GetComponent<BoxCollider>().isTrigger = true;
    }

    private void Start()
    {
        // FIX (Rev Q, post-playtest con due giocatori reali): NON impostare
        // più dashboardCanvas.worldCamera = Camera.main qui — vedi nota
        // dettagliata in EnterStation() per il perché. Lasciato vuoto
        // apposta: l'assegnazione corretta avviene solo lì.

        if (bed == null)
            Debug.LogWarning($"[MedicalStation] {name}: RecoveryBed non assegnato — trattamento non disponibile.");
        if (treatmentMinigame == null)
            Debug.LogWarning($"[MedicalStation] {name}: StabilizationMinigameMedical non assegnato — trattamento non disponibile.");
    }

    private void OnEnable()
    {
        if (bed != null) bed.OperatorChanged += HandleOperatorChanged;
    }

    private void OnDisable()
    {
        if (bed != null) bed.OperatorChanged -= HandleOperatorChanged;
    }

    // ===== UPDATE =====

    private void Update()
    {
        if (interactionCooldown > 0f) interactionCooldown -= Time.deltaTime;
        if (requestCooldown > 0f) requestCooldown -= Time.deltaTime;

        if (!isUsingStation) return;

        // Q12-a — a terra da seduto: uscita forzata, prima di tutto il resto.
        if (playerHealth != null && !playerHealth.IsAlive)
        {
            ForceExitDowned();
            return;
        }

        if (isTransitioning) return;

        // Q11-a — Cancel a due tempi, un solo consumatore per pressione.
        if (cancelAction != null && cancelAction.WasPressedThisFrame())
        {
            if (treatmentOpen) CloseTreatmentByPlayer();
            else ExitStation();
            return;
        }

        if (treatmentOpen) return;

        UpdateTreatmentStatus();

        // Q9-a — avvio dal dashboard.
        if (interactAction != null && interactAction.WasPressedThisFrame())
            TryRequestTreatment();
    }

    // ===== IINTERACTABLE =====

    public void Interact(GameObject interactor)
    {
        if (interactionCooldown > 0f) return;
        if (isUsingStation || isTransitioning) return;   // l'uscita passa solo da Cancel
        if (!CanInteract()) return;

        EnterStation(interactor);
    }

    /// <summary>
    /// Disponibile solo a un giocatore libero: vivo, con il movimento attivo (non
    /// sdraiato, non su un'altra postazione, non a terra) e con il tablet chiuso.
    /// </summary>
    public bool CanInteract()
    {
        if (isUsingStation || isTransitioning || interactionCooldown > 0f) return false;
        if (!EnsureRig()) return false;
        if (!rigHealth.IsAlive) return false;
        if (rigController != null && !rigController.enabled) return false;
        if (rigTablet != null && rigTablet.IsBusy) return false;
        return true;
    }

    public string GetInteractionPrompt() => "[{interact}] Use Medical Station";   // Rev BT-c (Q75-a)
    public bool IsContinuousInteraction() => false;
    public void OnLookEnter() { }
    public void OnLookExit() { }

    private bool EnsureRig()
    {
        PlayerHealthSystem me = PlayerHealthSystem.LocalInstance;
        if (me == null)
        {
            rigHealth = null;
            return false;
        }

        if (me != rigHealth)
        {
            rigHealth = me;
            rigController = me.GetComponent<PlayerController>();
            rigTablet = me.GetComponent<TabletStation>();
        }
        return true;
    }

    // ===== ENTER =====

    private void EnterStation(GameObject interactor)
    {
        playerController = interactor.GetComponent<PlayerController>();
        characterController = interactor.GetComponent<CharacterController>();
        playerCamera = interactor.GetComponentInChildren<Camera>();
        playerHealth = interactor.GetComponent<PlayerHealthSystem>();
        playerTablet = interactor.GetComponent<TabletStation>();

        if (playerController == null || playerCamera == null) return;

        // FIX (Rev Q) — causa reale del pannello "bloccato" in playtest con
        // un secondo giocatore reale: dashboardCanvas.worldCamera veniva
        // impostato una sola volta in Start() su Camera.main (letto al
        // caricamento scena, prima ancora che i player spawnassero) — con
        // due giocatori reali può risolversi alla camera sbagliata e restare
        // tale per tutta la sessione, facendo sì che GraphicRaycaster non
        // rilevi mai i click sul Canvas World Space. Vedi EngineeringStation.cs
        // per la spiegazione completa — stesso identico bug, stesso fix:
        // assegna esplicitamente la camera del giocatore che sta
        // EFFETTIVAMENTE entrando ora, non dipende da Camera.main/tag.
        if (dashboardCanvas != null)
            dashboardCanvas.worldCamera = playerCamera;

        // Cancel, Interact e Look dal PlayerInput del player (mai hardcodati).
        PlayerInput pi = playerInputReference != null
            ? playerInputReference
            : interactor.GetComponent<PlayerInput>();

        cancelAction = pi != null && pi.actions != null
            ? pi.actions.FindAction("Cancel", throwIfNotFound: false)
            : null;
        interactAction = pi != null && pi.actions != null
            ? pi.actions.FindAction("Interact", throwIfNotFound: false)
            : null;
        lookAction = pi != null && pi.actions != null
            ? pi.actions.FindAction("Look", throwIfNotFound: false)
            : null;

        // Salva stato originale
        originalPlayerPosition = interactor.transform.position;
        originalPlayerRotation = interactor.transform.rotation;
        originalCameraRotation = playerCamera.transform.localRotation;

        playerController.enabled = false;
        if (characterController != null)
            characterController.enabled = false;

        // Q12-a — tablet e postazione salvano/ripristinano entrambi PlayerController e
        // camera: il tablet non si apre finché si è seduti.
        if (playerTablet != null) playerTablet.SetOpenBlocked(true);

        isUsingStation = true;
        isTransitioning = true;
        treatmentOpen = false;
        lastStatusText = null;

        // Attiva UI (i dati si aggiornano da Open, a fine transizione)
        if (dashboardUI != null)
            dashboardUI.gameObject.SetActive(true);

        transitionRoutine = StartCoroutine(TransitionToStation(interactor.transform));
    }

    /// <summary>
    /// Snap del player e rotazione della camera verso il monitor, in sequenza. La
    /// postazione resta "in transizione" fino alla fine della rotazione: niente Cancel,
    /// niente avvio del trattamento finché la camera non è sul monitor.
    /// </summary>
    private IEnumerator TransitionToStation(Transform t)
    {
        Vector3 targetPos = playerSnapPoint != null ? playerSnapPoint.position : transform.position;
        Quaternion targetRot = playerSnapPoint != null ? playerSnapPoint.rotation : transform.rotation;

        float progress = 0f;
        while (progress < 1f)
        {
            progress += Time.deltaTime * snapTransitionSpeed;
            t.position = Vector3.Lerp(originalPlayerPosition, targetPos, progress);
            t.rotation = Quaternion.Lerp(originalPlayerRotation, targetRot, progress);
            yield return null;
        }

        t.position = targetPos;
        t.rotation = targetRot;

        // Camera verso il monitor (stesso pattern di EngineeringStation)
        if (cameraLookAtPoint != null)
        {
            Transform cam = playerCamera.transform;
            Vector3 direction = (cameraLookAtPoint.position - cam.position).normalized;
            Quaternion worldTarget = Quaternion.LookRotation(direction);
            Quaternion targetCameraLocalRotation = Quaternion.Inverse(cam.parent.rotation) * worldTarget;

            progress = 0f;
            while (progress < 1f)
            {
                progress += Time.deltaTime * cameraTransitionSpeed;
                cam.localRotation = Quaternion.Lerp(cam.localRotation, targetCameraLocalRotation, progress);
                yield return null;
            }

            cam.localRotation = targetCameraLocalRotation;
        }

        transitionRoutine = null;
        isTransitioning = false;

        if (dashboardUI != null)
            dashboardUI.Open();
    }

    // ===== TRATTAMENTO (Rev BO-b) =====

    private void TryRequestTreatment()
    {
        if (requestCooldown > 0f || bed == null || treatmentMinigame == null) return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (bed.GetTreatmentAvailability(nm.LocalClientId) != RecoveryBed.TreatmentAvailability.Ready) return;

        RecoveryBed.TreatmentPhase phase = bed.GetNextPhase(bed.PatientClientId);
        if (phase == RecoveryBed.TreatmentPhase.None) return;

        requestCooldown = REQUEST_COOLDOWN;
        requestedPhase = phase;
        bed.RequestTreatment(phase);
    }

    /// <summary>
    /// Cambio dell'operatore deciso dal server (tutti i client ricevono l'evento).
    /// Diventato operatore da seduto → apre il minigame. Diventato operatore quando
    /// non è più in posizione (alzato nel frattempo, a terra) → rinuncia subito.
    /// Operatore rimosso dal server → chiude la sessione senza rimandare nulla.
    /// </summary>
    private void HandleOperatorChanged(ulong previous, ulong current)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null) return;
        ulong me = nm.LocalClientId;

        if (current == me)
        {
            bool inPosition = isUsingStation && !isTransitioning
                              && (playerHealth == null || playerHealth.IsAlive);

            if (inPosition && !treatmentOpen) OpenTreatment();
            else if (!treatmentOpen && bed != null) bed.EndTreatment();
        }
        else if (previous == me && treatmentOpen)
        {
            // Fine decisa dal server (paziente alzato, a terra o espulso; letto despawnato).
            if (treatmentMinigame != null) treatmentMinigame.Interrupt();
            CloseTreatmentLocal(notifyServer: false);   // idempotente
        }
    }

    private void OpenTreatment()
    {
        if (treatmentMinigame == null || bed == null)
        {
            if (bed != null) bed.EndTreatment();
            return;
        }

        // Rev BP-b: la fase accettata è quella richiesta. Ripiego (nessuna richiesta
        // registrata): la prossima fase secondo lo stato replicato.
        RecoveryBed.TreatmentPhase phase = requestedPhase != RecoveryBed.TreatmentPhase.None
            ? requestedPhase
            : bed.GetNextPhase(bed.PatientClientId);

        treatmentOpen = true;
        SetDashboardVisible(false);

        treatmentMinigame.Open(bed, bed.Config, bed.PatientLabel, phase, lookAction,
                               OnTreatmentComplete, OnTreatmentInterrupted);
    }

    private void OnTreatmentComplete() => CloseTreatmentLocal(notifyServer: true);

    private void OnTreatmentInterrupted() => CloseTreatmentLocal(notifyServer: true);

    /// <summary>Primo tempo del Cancel: interrompe il trattamento, si resta seduti.</summary>
    private void CloseTreatmentByPlayer()
    {
        if (treatmentMinigame != null) treatmentMinigame.Interrupt();   // → OnTreatmentInterrupted
        CloseTreatmentLocal(notifyServer: true);                          // idempotente
    }

    /// <summary>
    /// Chiusura locale della sessione. Idempotente. Da seduti torna il dashboard.
    /// Il letto invia EndTreatment solo se il server considera ancora questo client
    /// l'operatore (dopo una fine decisa dal server non parte nulla).
    /// </summary>
    private void CloseTreatmentLocal(bool notifyServer)
    {
        if (!treatmentOpen) return;

        treatmentOpen = false;
        requestCooldown = REQUEST_COOLDOWN;
        requestedPhase = RecoveryBed.TreatmentPhase.None;
        lastStatusText = null;

        if (isUsingStation) SetDashboardVisible(true);

        if (notifyServer && bed != null) bed.EndTreatment();
    }

    private void SetDashboardVisible(bool visible)
    {
        if (dashboardUI == null) return;

        if (visible)
        {
            dashboardUI.gameObject.SetActive(true);
            dashboardUI.Open();
        }
        else
        {
            dashboardUI.Close();
            dashboardUI.gameObject.SetActive(false);
        }
    }

    private void UpdateTreatmentStatus()
    {
        if (treatmentStatusText == null) return;

        string text = string.Empty;
        Color color = colorStatusIdle;

        NetworkManager nm = NetworkManager.Singleton;
        if (bed != null && treatmentMinigame != null && nm != null)
        {
            switch (bed.GetTreatmentAvailability(nm.LocalClientId))
            {
                case RecoveryBed.TreatmentAvailability.NoPatient:
                    text = StatusNoPatient;
                    break;
                case RecoveryBed.TreatmentAvailability.InProgress:
                    text = StatusInProgress;
                    color = colorStatusBusy;
                    break;
                case RecoveryBed.TreatmentAvailability.PatientStable:
                    text = StatusStable;
                    break;
                case RecoveryBed.TreatmentAvailability.NotTreatableAtTier:
                    text = StatusNotTreatable;
                    color = colorStatusBusy;
                    break;
                case RecoveryBed.TreatmentAvailability.Ready:
                    string phaseLabel = RecoveryBed.PhaseLabel(bed.GetNextPhase(bed.PatientClientId));
                    text = FormatPrompt(StatusReadyTemplate.Replace("{phase}", phaseLabel));
                    color = colorStatusReady;
                    break;
            }
        }

        if (text != lastStatusText)
        {
            treatmentStatusText.text = text;
            lastStatusText = text;
        }
        treatmentStatusText.color = color;
    }

    private static string FormatPrompt(string template)
    {
        return InputDeviceManager.Instance != null
            ? InputDeviceManager.Instance.FormatPrompt(template)
            : template.Replace("{interact}", "E");
    }

    // ===== EXIT =====

    private void ExitStation()
    {
        if (!isUsingStation) return;

        // Difensivo: con Cancel a due tempi qui il trattamento è già chiuso.
        if (treatmentOpen) CloseTreatmentByPlayer();

        interactionCooldown = COOLDOWN_DURATION;
        isUsingStation = false;
        isTransitioning = true;

        SetDashboardVisible(false);

        if (transitionRoutine != null) StopCoroutine(transitionRoutine);
        transitionRoutine = StartCoroutine(TransitionFromStation());
    }

    private IEnumerator TransitionFromStation()
    {
        GameObject interactor = playerController != null ? playerController.gameObject : null;
        if (interactor == null)
        {
            transitionRoutine = null;
            isTransitioning = false;
            yield break;
        }

        Transform t = interactor.transform;

        float progress = 0f;
        while (progress < 1f)
        {
            progress += Time.deltaTime * snapTransitionSpeed;
            t.position = Vector3.Lerp(t.position, originalPlayerPosition, progress);
            t.rotation = Quaternion.Lerp(t.rotation, originalPlayerRotation, progress);
            playerCamera.transform.localRotation = Quaternion.Lerp(
                playerCamera.transform.localRotation,
                originalCameraRotation,
                progress);
            yield return null;
        }

        t.position = originalPlayerPosition;
        t.rotation = originalPlayerRotation;
        playerCamera.transform.localRotation = originalCameraRotation;

        // FIX — currentVelocity è un campo persistente in PlayerController
        // per smussare accelerazione/decelerazione: disabilitare il
        // componente non lo azzera. Stesso fix di PilotStation/EngineeringStation.
        playerController.ResetVelocity();

        // Q12-a — mai rimettere in moto un giocatore a terra (prima:
        // wasPlayerControllerEnabled). Il freeze Downed resta l'autorità.
        playerController.enabled = playerHealth == null || playerHealth.IsAlive;

        if (characterController != null)
            characterController.enabled = true;

        if (playerTablet != null) playerTablet.SetOpenBlocked(false);

        transitionRoutine = null;
        isTransitioning = false;
    }

    /// <summary>
    /// Q12-a — il giocatore è andato a terra da seduto. Il corpo resta alla console
    /// (niente lerp di un giocatore a terra); si ripristina ciò che la postazione aveva
    /// preso: camera, CharacterController (collider per il defib, Rev BD), tablet.
    /// PlayerController resta com'è: il freeze Downed lo ha già spento e lo riaccenderà
    /// alla rianimazione.
    /// </summary>
    private void ForceExitDowned()
    {
        if (!isUsingStation) return;

        isUsingStation = false;
        isTransitioning = false;
        interactionCooldown = COOLDOWN_DURATION;

        if (transitionRoutine != null)
        {
            StopCoroutine(transitionRoutine);
            transitionRoutine = null;
        }

        if (treatmentOpen) CloseTreatmentByPlayer();   // isUsingStation già false → il dashboard non riappare
        SetDashboardVisible(false);

        if (playerCamera != null)
            playerCamera.transform.localRotation = originalCameraRotation;

        if (playerController != null)
            playerController.ResetVelocity();

        if (characterController != null)
            characterController.enabled = true;

        if (playerTablet != null) playerTablet.SetOpenBlocked(false);
    }

    // ===== DEBUG (standard Rev BA) =====
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!showDebugUI || !isUsingStation) return;

        NetworkManager nm = NetworkManager.Singleton;
        string availability = bed != null && nm != null
            ? bed.GetTreatmentAvailability(nm.LocalClientId).ToString()
            : "—";

        string nextPhase = bed != null ? bed.GetNextPhase(bed.PatientClientId).ToString() : "—";

        GUILayout.BeginArea(new Rect(10, Screen.height - 130, 300, 120));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[MedicalStation] transizione={isTransitioning}");
        GUILayout.Label($"Trattamento aperto: {treatmentOpen} · fase richiesta {requestedPhase}");
        GUILayout.Label($"Letto: {availability} · prossima fase {nextPhase}");
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}