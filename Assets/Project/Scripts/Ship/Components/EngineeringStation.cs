using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using System.Collections;

/// <summary>
/// Engineering Station - Physical workstation with player snap positioning
/// Player presses E to sit at workstation and view dashboard on monitor
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class EngineeringStation : MonoBehaviour, IInteractable
{
    [Header("Dashboard")]
    [SerializeField] private EngineeringDashboardUI dashboardUI;
    [SerializeField] private Canvas dashboardCanvas; // World Space canvas
    // firstSelectedElement rimosso: la selezione iniziale è ora gestita
    // dinamicamente da EngineeringDashboardUI.SetInitialSelection() in base
    // allo stato blackout, che è più corretto di un elemento fisso.

    [Header("Player Positioning")]
    [SerializeField] private Transform playerSnapPoint;
    [Tooltip("Un LookAt point per monitor, nell'ordine Monitor 1/2/3.")]
    [SerializeField] private Transform[] cameraLookAtPoints;
    [SerializeField] private float snapTransitionSpeed = 5f;
    [SerializeField] private float cameraTransitionSpeed = 8f;

    [Header("Camera Control")]
    [Tooltip("Manual camera pitch offset (X rotation). Positive = look UP, Negative = look DOWN")]
    [SerializeField] private float cameraPitchOffset = 0f;
    [Tooltip("Manual camera yaw offset (Y rotation). Positive = look RIGHT, Negative = look LEFT")]
    [SerializeField] private float cameraYawOffset = 0f;
    [Tooltip("Use LookAt point or manual rotation offsets?")]
    [SerializeField] private bool useLookAtPoint = true;

    [Header("Input")]
    [SerializeField] private bool allowMovementWhileUsing = false;

    private bool isUsingStation = false;
    private bool isExiting = false;
    private PlayerController playerController;
    private CharacterController characterController;
    private PlayerInput playerInputComponent;
    private Camera playerCamera;
    private InputAction cancelAction;

    private float interactionCooldown = 0f;
    private const float COOLDOWN_DURATION = 0.5f;

    private Vector3 originalPlayerPosition;
    private Quaternion originalPlayerRotation;
    private Quaternion originalCameraRotation;
    private bool wasPlayerControllerEnabled;

    private bool isTransitioning = false;
    private float transitionProgress = 0f;

    // FIX: rotazione target calcolata una volta sola all'entrata, non ogni frame
    private Quaternion targetCameraLocalRotationCached;

    private void Awake()
    {
        if (dashboardUI != null)
            dashboardUI.gameObject.SetActive(false);

        BoxCollider trigger = GetComponent<BoxCollider>();
        trigger.isTrigger = true;
    }

    private void Start()
    {
        // FIX (Rev Q, post-playtest con due giocatori reali): NON impostare
        // più dashboardCanvas.worldCamera = Camera.main qui — vedi nota
        // dettagliata in EnterStation() per il perché. Lasciato vuoto
        // apposta: l'assegnazione corretta avviene solo lì.
    }

    // ===== IInteractable =====

    public void Interact(GameObject interactor)
    {
        if (isUsingStation) ExitStation();
        else EnterStation(interactor);
    }

    public string GetInteractionPrompt()
        => isUsingStation ? "[{cancel}] Exit Engineering Station" : "[{interact}] Use Engineering Station";

    public bool CanInteract()
        => dashboardUI != null && playerSnapPoint != null && interactionCooldown <= 0f;

    public bool IsContinuousInteraction() => false;
    public void OnLookEnter() { }
    public void OnLookExit() { }

    // ===== Station Control =====

    private void EnterStation(GameObject player)
    {
        if (playerSnapPoint == null || cameraLookAtPoints == null || cameraLookAtPoints.Length == 0 || cameraLookAtPoints[0] == null)
        {
            Debug.LogError("[EngineeringStation] Missing snap points!");
            return;
        }

        // FIX (Rev Q, post-playtest con due giocatori reali) — riferimenti
        // SEMPRE riassegnati ad ogni ingresso, non più "solo la prima
        // volta" (era `if (playerController == null)`, stesso identico
        // pattern già corretto in MedicalStation/PilotStation). Con un solo
        // Engineer per sessione il bug non si manifestava mai, ma lasciato
        // così sarebbe esploso non appena un giocatore DIVERSO da quello
        // che ha usato per primo la postazione ci si fosse seduto: avrebbe
        // ereditato camera/PlayerController del primo, non i propri.
        playerController = player.GetComponent<PlayerController>();
        characterController = player.GetComponent<CharacterController>();
        playerCamera = player.GetComponentInChildren<Camera>();
        playerInputComponent = player.GetComponent<PlayerInput>();

        if (playerInputComponent != null)
            cancelAction = playerInputComponent.actions.FindAction("Cancel");

        // FIX PRINCIPALE (Rev Q) — causa reale del pannello "bloccato" in
        // playtest con un secondo giocatore reale: dashboardCanvas.worldCamera
        // veniva impostato UNA SOLA VOLTA in Start() su Camera.main, letto al
        // caricamento della scena. Con un solo giocatore locale (i vecchi test
        // ParrelSync auto-connect) capitava quasi sempre di risolversi per
        // caso alla camera giusta; con due giocatori reali che spawnano in
        // momenti diversi, Camera.main può risolversi alla camera sbagliata
        // (o restare quella di un altro player) — e da quel momento resta
        // sbagliata per tutta la sessione, perché impostata una volta sola.
        // GraphicRaycaster su un Canvas World Space usa esattamente questo
        // riferimento per capire dove "atterra" un click: con la camera
        // sbagliata i bottoni semplicemente non ricevono mai l'evento di
        // click — da cui la sensazione di "tutto bloccato" pur senza alcun
        // errore in console.
        //
        // Fix: assegna esplicitamente la camera del giocatore che sta
        // EFFETTIVAMENTE entrando nella postazione in questo momento — non
        // dipende da tag, da Camera.main, né da quale altro player abbia
        // attivato/disattivato la propria camera nel frattempo.
        if (dashboardCanvas != null && playerCamera != null)
            dashboardCanvas.worldCamera = playerCamera;

        if (playerController == null || characterController == null || playerCamera == null)
        {
            Debug.LogError("[EngineeringStation] Player missing required components!");
            return;
        }

        // Salva stato originale
        originalPlayerPosition = player.transform.position;
        originalPlayerRotation = player.transform.rotation;
        originalCameraRotation = playerCamera.transform.localRotation;
        wasPlayerControllerEnabled = playerController.enabled;

        // Disabilita controllo player durante transizione
        playerController.enabled = false;

        isUsingStation = true;
        isExiting = false;
        isTransitioning = true;
        transitionProgress = 0f;

        if (dashboardUI != null)
        {
            dashboardUI.gameObject.SetActive(true);
            dashboardUI.Open();
            // La selezione EventSystem iniziale è gestita da dashboardUI.Open()
            // → SetInitialSelection(): Restore se blackout, altrimenti prima
            // luce. Vedi EngineeringDashboardUI.cs. Non serve toccarla qui.
        }

        // VirtualCursor rimosso: navigazione via tasti direzionali/gamepad
        // gestita dall'EventSystem in Game.unity con InputSystemUIInputModule.

        Debug.Log("[EngineeringStation] Entering station - Transitioning to workstation");
    }

    private void ExitStation()
    {
        if (!isUsingStation) return;

        isExiting = true;
        isTransitioning = true;
        transitionProgress = 0f;

        // VirtualCursor rimosso — vedi EnterStation. Non c'è nulla da
        // "disattivare" in questo pannello: la navigazione tornerà
        // automaticamente al gameplay quando l'EventSystem non avrà più
        // Selected e il PlayerController tornerà attivo.

        if (dashboardUI != null)
        {
            dashboardUI.Close();
            dashboardUI.gameObject.SetActive(false);
        }

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);

        Debug.Log("[EngineeringStation] Exiting station - Returning to normal movement");
    }

    public void LookAtMonitor(int index)
    {
        if (cameraLookAtPoints == null || cameraLookAtPoints.Length == 0) return;
        if (!isUsingStation || isTransitioning) return;

        index = Mathf.Clamp(index, 0, cameraLookAtPoints.Length - 1);
        Transform target = cameraLookAtPoints[index];
        if (target == null) return;

        if (monitorLookCoroutine != null) StopCoroutine(monitorLookCoroutine);
        monitorLookCoroutine = StartCoroutine(LookAtMonitorRoutine(target));
    }

    private Coroutine monitorLookCoroutine;

    private IEnumerator LookAtMonitorRoutine(Transform target)
    {
        Quaternion startRot = playerCamera.transform.localRotation;
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime * cameraTransitionSpeed;

            Vector3 dir = (target.position - playerCamera.transform.position).normalized;
            Quaternion worldRot = Quaternion.LookRotation(dir);
            Quaternion localRot = Quaternion.Inverse(playerController.transform.rotation) * worldRot;

            playerCamera.transform.localRotation = Quaternion.Slerp(startRot, localRot, Mathf.Clamp01(t));
            yield return null;
        }

        // Salva la rotazione finale per il lerp di uscita
        targetCameraLocalRotationCached = playerCamera.transform.localRotation;
        monitorLookCoroutine = null;
    }

    private void Update()
    {
        if (interactionCooldown > 0f)
            interactionCooldown -= Time.deltaTime;

        if (isUsingStation && !isTransitioning)
        {
            if (cancelAction != null && cancelAction.WasPressedThisFrame())
                ExitStation();
        }

        if (isTransitioning)
            UpdateTransition();
    }

    private void UpdateTransition()
    {
        if (playerController == null || characterController == null || playerCamera == null)
            return;

        transitionProgress += Time.deltaTime * snapTransitionSpeed;
        float t = Mathf.Clamp01(transitionProgress);

        if (!isExiting)
        {
            // ENTRATA � sposta verso snap point
            characterController.enabled = false;

            playerController.transform.position = Vector3.Lerp(originalPlayerPosition, playerSnapPoint.position, t);
            playerController.transform.rotation = Quaternion.Slerp(originalPlayerRotation, playerSnapPoint.rotation, t);

            characterController.enabled = true;

            if (t >= 1f)
            {
                isTransitioning = false;
                playerController.enabled = allowMovementWhileUsing;

                // Camera: ora che il player � fermo allo snap point, punta verso Monitor 1.
                // Usiamo LookAtMonitorRoutine � stesso metodo dei tasti 1/2 � cos� il risultato
                // � identico. Il player � gi� nella rotazione finale: nessuna ambiguit� sul parent.
                LookAtMonitor(0);

                Debug.Log("[EngineeringStation] Transition complete - At workstation");
            }
        }
        else
        {
            // USCITA � torna alla posizione originale
            characterController.enabled = false;

            playerController.transform.position = Vector3.Lerp(playerSnapPoint.position, originalPlayerPosition, t);
            playerController.transform.rotation = Quaternion.Slerp(playerSnapPoint.rotation, originalPlayerRotation, t);

            playerCamera.transform.localRotation = Quaternion.Slerp(targetCameraLocalRotationCached, originalCameraRotation, t);

            characterController.enabled = true;

            if (t >= 1f)
            {
                isTransitioning = false;
                isExiting = false;
                isUsingStation = false;
                interactionCooldown = COOLDOWN_DURATION;

                characterController.enabled = false;
                playerController.transform.position = originalPlayerPosition;
                playerController.transform.rotation = originalPlayerRotation;
                playerCamera.transform.localRotation = originalCameraRotation;
                characterController.enabled = true;

                playerController.enabled = wasPlayerControllerEnabled;

                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;

                Debug.Log("[EngineeringStation] Exit complete - Normal movement restored");
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (playerSnapPoint == null) return;

        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(playerSnapPoint.position, 0.3f);
        Gizmos.DrawRay(playerSnapPoint.position, playerSnapPoint.forward * 0.5f);

        Vector3 eyesPosition = playerSnapPoint.position + Vector3.up * 1.6f;
        Gizmos.color = Color.blue;
        Gizmos.DrawWireSphere(eyesPosition, 0.1f);

        if (useLookAtPoint && cameraLookAtPoints != null && cameraLookAtPoints.Length > 0 && cameraLookAtPoints[0] != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(eyesPosition, cameraLookAtPoints[0].position);
            Vector3 direction = (cameraLookAtPoints[0].position - eyesPosition).normalized;
            Gizmos.color = Color.red;
            Gizmos.DrawRay(eyesPosition, direction * 1.5f);
        }
        else
        {
            Quaternion manualRotation = playerSnapPoint.rotation * Quaternion.Euler(cameraPitchOffset, cameraYawOffset, 0f);
            Vector3 lookDirection = manualRotation * Vector3.forward;
            Gizmos.color = Color.magenta;
            Gizmos.DrawRay(eyesPosition, lookDirection * 2f);
            Gizmos.DrawWireSphere(eyesPosition + lookDirection * 2f, 0.15f);
        }

#if UNITY_EDITOR
        string modeText = useLookAtPoint ? "MODE: LookAt Point" : $"MODE: Manual (Pitch: {cameraPitchOffset:F1}, Yaw: {cameraYawOffset:F1})";

        if (useLookAtPoint && cameraLookAtPoints != null && cameraLookAtPoints.Length > 0 && cameraLookAtPoints[0] != null)
        {
            Vector3 direction = (cameraLookAtPoints[0].position - eyesPosition).normalized;
            float verticalAngle = Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude) * Mathf.Rad2Deg;
            modeText += $"\nAngle: {verticalAngle:F1}� (+ = UP)";
        }

        UnityEditor.Handles.Label(eyesPosition + Vector3.up * 0.5f, modeText);
#endif
    }
}