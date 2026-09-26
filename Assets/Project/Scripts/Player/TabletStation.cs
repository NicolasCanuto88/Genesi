using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// TabletStation — Milestone 2.
/// Il tablet personale che ogni giocatore porta sempre con sé.
///
/// Differenza rispetto a EngineeringStation/MedicalStation: non è un oggetto
/// fisico piazzato nella nave con un proprio Transform/Collider — vive sul
/// Player prefab ed è richiamabile ovunque, in qualunque momento.
///
/// MODELLO 3D + UI (aggiornato in questa sessione):
///   Il tablet è un modello 3D tenuto in mano, con il Canvas UI ancorato
///   esattamente sulla sua faccia-schermo. Modello e Canvas vivono SOTTO uno
///   stesso GameObject padre (`tabletRoot`), così un solo SetActive() mostra
///   o nasconde entrambi insieme — non due toggle separati.
///
///   Gerarchia consigliata (figlia della Camera del player):
///     TabletHoldPoint (punto di aggancio "mano")
///       └── TabletRoot (= il campo `tabletRoot` qui sotto)
///             ├── TabletModel (mesh 3D del tablet)
///             │     └── ScreenAnchor (ancorato/orientato sulla faccia-schermo)
///             │           └── TabletCanvas (World Space, scale 0.001)
///
/// PATTERN:
///   - Apertura/chiusura: azione input dedicata "ToggleTablet", letta tramite
///     il meccanismo "SendMessages" di PlayerInput (Unity chiama automaticamente
///     OnToggleTablet su QUALSIASI componente sullo stesso GameObject — esattamente
///     come OnDebug già fa su PlayerController). Nessuna modifica a
///     PlayerController.cs necessaria.
///   - Il personaggio NON si teletrasporta (a differenza delle altre postazioni):
///     resta dov'è. Il movimento viene bloccato (PlayerController.enabled = false)
///     e la camera ruota leggermente verso il basso, come per guardare il tablet
///     tenuto in mano.
///   - VirtualCursor: usa l'overload Activate(canvas, cursorImage, scrollRect)
///     aggiunto in questa sessione — punta al Canvas del tablet senza che
///     EngineeringStation/MedicalStation debbano essere toccate.
///   - Uscita: Cancel (Esc / B gamepad), stesso pattern delle altre postazioni,
///     con piccolo cooldown per evitare riaperture accidentali.
///
/// ⚠️ Dipende da:
///   - EconomyManager (Fleet Account) — tab "Nave"
///   - LocalCharacterProfile (Personal Account) — tab "Profilo"
///   - PlayerHealthSystem (M3, non ancora implementato) — HP nel tab "Profilo" è stub
///   - Sistema di inventario personale (non ancora progettato) — stub nel tab "Profilo"
///
/// ⚠️ Setup Input Actions richiesto: aggiungere l'azione "ToggleTablet" all'asset
///    InputActions (es. Tab da tastiera, un tasto libero su gamepad — Select/View
///    o D-Pad Up, per non sovrapporsi a [South]=Interact/RepairMash o [North]=RepairKey_1).
///
/// ⚠️ Edge case non gestito in questa sessione: apertura del Tablet mentre il
///    giocatore è già seduto a un'altra postazione (Engineering/Medical/Pilot) —
///    da testare e, se necessario, bloccare in una sessione futura.
///
/// REV BO-a — BLOCCO ESTERNO (SetOpenBlocked / IsBusy), additivo:
///   Il letto della Recovery Bay (RecoveryBed) blocca l'APERTURA del tablet mentre
///   il giocatore è sdraiato o sta curando. Motivo (audit Rev AF/AG): tablet e
///   postazioni salvano/ripristinano entrambi PlayerController.enabled e la
///   rotazione camera. Se l'uscita dalla postazione avviene a tablet aperto (Cancel
///   condiviso nello stesso frame, o fine forzata dal server), il ripristino del
///   tablet arriva DOPO e rimette PlayerController a false → giocatore bloccato.
///   - SetOpenBlocked(true) impedisce solo l'APERTURA; la chiusura resta sempre
///     possibile.
///   - IsBusy = aperto o in transizione: chi apre una postazione lo controlla PRIMA
///     di entrare.
///   Le altre postazioni (Engineering/Medical/Pilot/pannelli) NON lo usano ancora:
///   l'edge case sopra resta aperto per loro (debito tracciato nel GDD delta).
/// </summary>
[RequireComponent(typeof(PlayerInput))]
public class TabletStation : MonoBehaviour
{
    [Header("Modello 3D + UI (un unico blocco attivato/disattivato insieme)")]
    [Tooltip("GameObject che contiene SIA il modello 3D del tablet SIA il Canvas ancorato al suo schermo. Questo è l'UNICO oggetto su cui viene chiamato SetActive() — vedi gerarchia consigliata nel commento di classe.")]
    [SerializeField] private GameObject tabletRoot;

    [Header("Riferimenti UI (figli di tabletRoot)")]
    [Tooltip("Canvas World Space ancorato sullo schermo del modello — usato qui solo per assegnare worldCamera in Start().")]
    [SerializeField] private Canvas tabletCanvas;
    [Tooltip("RectTransform dello stesso Canvas — passato a VirtualCursor.Activate().")]
    [SerializeField] private RectTransform tabletCanvasRect;
    [SerializeField] private RectTransform tabletCursorImage;
    [Tooltip("Opzionale — lascia vuoto se il tablet non ha contenuti scrollabili.")]
    [SerializeField] private UnityEngine.UI.ScrollRect tabletScrollRect;
    [SerializeField] private TabletDashboardUI dashboardUI;

    [Header("Camera — \"guarda il tablet\"")]
    [SerializeField] private float lookDownPitch = 35f;
    [SerializeField] private float cameraTransitionSpeed = 8f;

    [Header("Cooldown")]
    [SerializeField] private float exitCooldown = 0.3f;

    private bool isOpen = false;
    private bool isTransitioning = false;
    private float cooldownTimer = 0f;

    private PlayerController playerController;
    private Camera playerCamera;
    private PlayerInput playerInput;
    private InputAction cancelAction;

    private Quaternion originalCameraLocalRotation;
    private bool wasPlayerControllerEnabled;

    // Rev BO-a — blocco esterno dell'apertura (vedi commento di classe).
    private bool openBlocked = false;

    /// <summary>Rev BO-a — true se il tablet è aperto o in transizione (apertura/chiusura).</summary>
    public bool IsBusy => isOpen || isTransitioning;

    /// <summary>
    /// Rev BO-a — blocca/sblocca l'APERTURA del tablet (la chiusura resta possibile).
    /// Usato da RecoveryBed mentre il giocatore è sdraiato o sta curando.
    /// </summary>
    public void SetOpenBlocked(bool blocked) => openBlocked = blocked;

    private void Awake()
    {
        playerController = GetComponent<PlayerController>();
        playerInput = GetComponent<PlayerInput>();
        playerCamera = GetComponentInChildren<Camera>();

        if (playerInput != null)
            cancelAction = playerInput.actions.FindAction("Cancel", throwIfNotFound: false);

        if (tabletRoot != null)
            tabletRoot.SetActive(false);
    }

    private void Start()
    {
        // Stesso pattern già in uso su EngineeringStation/MedicalStation.
        if (tabletCanvas != null && Camera.main != null)
            tabletCanvas.worldCamera = Camera.main;
    }

    private void Update()
    {
        if (cooldownTimer > 0f)
            cooldownTimer -= Time.deltaTime;

        if (isOpen && !isTransitioning && cancelAction != null && cancelAction.WasPressedThisFrame())
            CloseTablet();
    }

    // ===== Input callback — chiamato automaticamente da PlayerInput (SendMessages) =====
    // Richiede l'azione "ToggleTablet" nell'Input Actions asset (vedi note sopra).
    public void OnToggleTablet(InputValue value)
    {
        if (!value.isPressed) return;
        if (cooldownTimer > 0f || isTransitioning) return;

        if (isOpen) CloseTablet();
        else if (!openBlocked) OpenTablet();   // Rev BO-a: apertura bloccabile dall'esterno
    }

    private void OpenTablet()
    {
        if (tabletRoot == null || playerController == null || playerCamera == null)
        {
            Debug.LogError("[TabletStation] Riferimenti mancanti — controlla l'Inspector.");
            return;
        }

        wasPlayerControllerEnabled = playerController.enabled;
        originalCameraLocalRotation = playerCamera.transform.localRotation;

        playerController.enabled = false;
        isOpen = true;

        // Un solo SetActive: mostra modello 3D + Canvas insieme (sono nello stesso sottoalbero).
        tabletRoot.SetActive(true);
        dashboardUI?.Open();

        VirtualCursor.Instance?.Activate(tabletCanvasRect, tabletCursorImage, tabletScrollRect);

        StartCoroutine(RotateCameraRoutine(Quaternion.Euler(lookDownPitch, 0f, 0f), onComplete: null));
    }

    private void CloseTablet()
    {
        if (!isOpen) return;

        isOpen = false;
        cooldownTimer = exitCooldown;

        dashboardUI?.Close();
        VirtualCursor.Instance?.Deactivate();

        StartCoroutine(RotateCameraRoutine(originalCameraLocalRotation, onComplete: () =>
        {
            if (tabletRoot != null)
                tabletRoot.SetActive(false);

            if (playerController != null)
                playerController.enabled = wasPlayerControllerEnabled;
        }));
    }

    private IEnumerator RotateCameraRoutine(Quaternion target, System.Action onComplete)
    {
        isTransitioning = true;
        Quaternion start = playerCamera.transform.localRotation;
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime * cameraTransitionSpeed;
            playerCamera.transform.localRotation = Quaternion.Slerp(start, target, Mathf.Clamp01(t));
            yield return null;
        }

        playerCamera.transform.localRotation = target;
        isTransitioning = false;
        onComplete?.Invoke();
    }
}