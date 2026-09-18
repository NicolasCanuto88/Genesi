using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using System.Collections;
using SpaceSurvivor.UI;

/// <summary>
/// ScannerStation — Rev BH (Fase 2b, D29). Postazione fisica "Sensors" (GDD §9.2).
///
/// Replica il pattern di EngineeringStation (seduto/snap + camera verso il monitor,
/// E per usare / Cancel per uscire, disabilita il PlayerController mentre in uso),
/// con UN SOLO monitor: nessun MonitorSwitcher, nessun hub. Mostra la ScannerUI
/// (IDashboardPanel) chiamando Open()/Close() all'ingresso/uscita.
///
/// CAMERA — nessun transform "look at" dedicato è obbligatorio (feedback Nicolas):
///   - useLookAtPoint = true (default): la camera guarda il Canvas del monitor
///     (dashboardCanvas). Nessun empty da creare. Se vuoi un mirino diverso puoi
///     assegnare cameraLookAtPoint (opzionale) e sovrascrive il canvas.
///   - useLookAtPoint = false: aim manuale = rotazione dello snap point + offset
///     pitch/yaw (come la modalità manuale di EngineeringStation), zero transform.
///
/// NB — nessun ruolo ha esclusiva: qualunque player può sedersi allo Scanner
/// (i bonus/malus di ruolo vivono nello ScannerSystem, non qui).
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class ScannerStation : MonoBehaviour, IInteractable
{
    [Header("Debug")]
    [Tooltip("Log diagnostici verbosi (ingresso/uscita/transizioni). Default off. " +
             "I problemi reali (snap/componenti mancanti) restano sempre a log.")]
    [SerializeField] private bool logVerbose = false;
    private void LogV(string msg) { if (logVerbose) Debug.Log(msg); }

    [Header("Dashboard")]
    [SerializeField] private ScannerUI scannerUI;
    [SerializeField] private Canvas dashboardCanvas; // World Space canvas — anche bersaglio di default dello sguardo

    [Header("Player Positioning")]
    [SerializeField] private Transform playerSnapPoint;
    [SerializeField] private float snapTransitionSpeed = 5f;
    [SerializeField] private float cameraTransitionSpeed = 8f;

    [Header("Camera Control")]
    [Tooltip("Se true la camera guarda il monitor: usa 'Camera Look At Point' se " +
             "assegnato, altrimenti il Canvas del monitor (dashboardCanvas). " +
             "Se false usa gli offset manuali qui sotto (nessun transform).")]
    [SerializeField] private bool useLookAtPoint = true;
    [Tooltip("Opzionale. Punto specifico verso cui guardare. Se null (default) si " +
             "usa il Canvas del monitor — nessun empty da creare.")]
    [SerializeField] private Transform cameraLookAtPoint;
    [Tooltip("Aim manuale (solo se Use Look At Point = false). Pitch: + = su, - = giù.")]
    [SerializeField] private float cameraPitchOffset = 0f;
    [Tooltip("Aim manuale (solo se Use Look At Point = false). Yaw: + = destra, - = sinistra.")]
    [SerializeField] private float cameraYawOffset = 0f;

    [Header("Input")]
    [SerializeField] private bool allowMovementWhileUsing = false;

    private bool isUsingStation = false;
    public bool IsUsingStation => isUsingStation;

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
    private Quaternion targetCameraLocalRotationCached;
    private Coroutine cameraAimCoroutine;

    private void Awake()
    {
        if (scannerUI != null)
            scannerUI.gameObject.SetActive(false);

        BoxCollider trigger = GetComponent<BoxCollider>();
        trigger.isTrigger = true;
    }

    // ===== IInteractable =====

    public void Interact(GameObject interactor)
    {
        if (isUsingStation) ExitStation();
        else EnterStation(interactor);
    }

    public string GetInteractionPrompt()
        => isUsingStation ? "[{cancel}] Exit Scanner Station" : "[{interact}] Use Scanner Station";

    public bool CanInteract()
        => scannerUI != null && playerSnapPoint != null && interactionCooldown <= 0f;

    public bool IsContinuousInteraction() => false;
    public void OnLookEnter() { }
    public void OnLookExit() { }

    // ===== Station Control =====

    private void EnterStation(GameObject player)
    {
        if (playerSnapPoint == null)
        {
            Debug.LogError("[ScannerStation] Missing playerSnapPoint!");
            return;
        }

        // Riferimenti SEMPRE riassegnati ad ogni ingresso (fix multi-player, come
        // EngineeringStation Rev Q): con player diversi, non ereditare camera/
        // controller del primo che si è seduto.
        playerController = player.GetComponent<PlayerController>();
        characterController = player.GetComponent<CharacterController>();
        playerCamera = player.GetComponentInChildren<Camera>();
        playerInputComponent = player.GetComponent<PlayerInput>();

        if (playerInputComponent != null)
            cancelAction = playerInputComponent.actions.FindAction("Cancel");

        // Fix Canvas World Space (EngineeringStation Rev Q): assegna la worldCamera
        // del player che sta EFFETTIVAMENTE entrando, altrimenti il GraphicRaycaster
        // usa la camera sbagliata e i click non arrivano ("tutto bloccato").
        if (dashboardCanvas != null && playerCamera != null)
            dashboardCanvas.worldCamera = playerCamera;

        if (playerController == null || characterController == null || playerCamera == null)
        {
            Debug.LogError("[ScannerStation] Player missing required components!");
            return;
        }

        originalPlayerPosition = player.transform.position;
        originalPlayerRotation = player.transform.rotation;
        originalCameraRotation = playerCamera.transform.localRotation;
        wasPlayerControllerEnabled = playerController.enabled;

        playerController.enabled = false;

        isUsingStation = true;
        isExiting = false;
        isTransitioning = true;
        transitionProgress = 0f;

        if (scannerUI != null)
        {
            scannerUI.gameObject.SetActive(true);
            // Monitor singolo, nessun MonitorSwitcher: apriamo noi il pannello.
            scannerUI.Open();
        }

        LogV("[ScannerStation] Entering station.");
    }

    private void ExitStation()
    {
        if (!isUsingStation) return;

        isExiting = true;
        isTransitioning = true;
        transitionProgress = 0f;

        if (scannerUI != null)
        {
            scannerUI.Close();
            scannerUI.gameObject.SetActive(false);
        }

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);

        LogV("[ScannerStation] Exiting station.");
    }

    // ===== Camera aim (nessun transform obbligatorio) =====

    /// <summary>
    /// Rotazione LOCALE (rispetto al corpo del player) che la camera deve avere
    /// da seduto. In modalità lookAt guarda cameraLookAtPoint se assegnato,
    /// altrimenti il Canvas del monitor; in modalità manuale usa gli offset dallo
    /// snap point. Ritorna null se non c'è un bersaglio valido (mantiene la
    /// rotazione corrente).
    /// </summary>
    private Quaternion? ComputeTargetCameraLocalRotation()
    {
        if (playerController == null || playerCamera == null) return null;

        Quaternion worldRot;

        if (useLookAtPoint)
        {
            Transform target = cameraLookAtPoint != null
                ? cameraLookAtPoint
                : (dashboardCanvas != null ? dashboardCanvas.transform : null);

            if (target != null)
            {
                Vector3 dir = target.position - playerCamera.transform.position;
                if (dir.sqrMagnitude < 0.0001f) return null;
                worldRot = Quaternion.LookRotation(dir.normalized);
            }
            else
            {
                // Nessun bersaglio: ripiego sull'aim manuale dallo snap point.
                worldRot = playerSnapPoint.rotation * Quaternion.Euler(cameraPitchOffset, cameraYawOffset, 0f);
            }
        }
        else
        {
            worldRot = playerSnapPoint.rotation * Quaternion.Euler(cameraPitchOffset, cameraYawOffset, 0f);
        }

        return Quaternion.Inverse(playerController.transform.rotation) * worldRot;
    }

    private void AimCamera()
    {
        if (!isUsingStation) return;
        Quaternion? target = ComputeTargetCameraLocalRotation();
        if (target == null) return;

        if (cameraAimCoroutine != null) StopCoroutine(cameraAimCoroutine);
        cameraAimCoroutine = StartCoroutine(AimCameraRoutine(target.Value));
    }

    private IEnumerator AimCameraRoutine(Quaternion targetLocalRot)
    {
        Quaternion startRot = playerCamera.transform.localRotation;
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime * cameraTransitionSpeed;
            playerCamera.transform.localRotation = Quaternion.Slerp(startRot, targetLocalRot, Mathf.Clamp01(t));
            yield return null;
        }

        targetCameraLocalRotationCached = targetLocalRot;
        cameraAimCoroutine = null;
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
            // ENTRATA — sposta verso lo snap point.
            characterController.enabled = false;

            playerController.transform.position = Vector3.Lerp(originalPlayerPosition, playerSnapPoint.position, t);
            playerController.transform.rotation = Quaternion.Slerp(originalPlayerRotation, playerSnapPoint.rotation, t);

            characterController.enabled = true;

            if (t >= 1f)
            {
                isTransitioning = false;
                playerController.enabled = allowMovementWhileUsing;
                AimCamera();
                LogV("[ScannerStation] Transition complete - At workstation.");
            }
        }
        else
        {
            // USCITA — torna alla posizione originale.
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
                playerController.ResetVelocity();

                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;

                LogV("[ScannerStation] Exit complete - Normal movement restored.");
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

        // Bersaglio dello sguardo: lookAt point > canvas > (manuale).
        Transform target = cameraLookAtPoint != null
            ? cameraLookAtPoint
            : (dashboardCanvas != null ? dashboardCanvas.transform : null);

        if (useLookAtPoint && target != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(eyesPosition, target.position);
            Vector3 direction = (target.position - eyesPosition).normalized;
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
    }
}