using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;

/// <summary>
/// MedicalStation — Milestone 2
/// Postazione fisica del Medico nel Medibay (Livello 2).
///
/// Pattern identico a EngineeringStation:
///   - IInteractable → rilevata da InteractionSystem via raycast
///   - Snap player con lerp verso playerSnapPoint
///   - Camera ruota verso il monitor al termine della transizione
///   - VirtualCursor.Activate() / Deactivate()
///   - Uscita via Cancel (Esc / B gamepad) con cooldown 0.5s
///
/// Un solo monitor — MedicalDashboardUI.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class MedicalStation : MonoBehaviour, IInteractable
{
    [Header("Dashboard")]
    [SerializeField] private MedicalDashboardUI dashboardUI;
    [SerializeField] private Canvas dashboardCanvas;

    [Header("Player Positioning")]
    [SerializeField] private Transform playerSnapPoint;
    [SerializeField] private Transform cameraLookAtPoint;
    [SerializeField] private float snapTransitionSpeed = 5f;
    [SerializeField] private float cameraTransitionSpeed = 8f;

    [Header("Input")]
    [SerializeField] private PlayerInput playerInputReference;

    // ===== STATO INTERNO =====

    private bool isUsingStation  = false;
    private bool isTransitioning = false;

    private PlayerController     playerController;
    private CharacterController  characterController;
    private Camera               playerCamera;
    private InputAction          cancelAction;

    private Vector3    originalPlayerPosition;
    private Quaternion originalPlayerRotation;
    private Quaternion originalCameraRotation;
    private Quaternion targetCameraLocalRotation;
    private bool       wasPlayerControllerEnabled;

    private float interactionCooldown = 0f;
    private const float COOLDOWN_DURATION = 0.5f;

    // ===== AWAKE / START =====

    private void Awake()
    {
        GetComponent<BoxCollider>().isTrigger = true;
    }

    private void Start()
    {
        if (dashboardCanvas != null && Camera.main != null)
            dashboardCanvas.worldCamera = Camera.main;
    }

    private void Update()
    {
        if (interactionCooldown > 0f)
            interactionCooldown -= Time.deltaTime;

        if (isUsingStation && cancelAction != null && cancelAction.WasPressedThisFrame())
            ExitStation();
    }

    // ===== IINTERACTABLE =====

    public void Interact(GameObject interactor)
    {
        if (interactionCooldown > 0f) return;

        if (isUsingStation)
            ExitStation();
        else
            EnterStation(interactor);
    }

    public bool   CanInteract()            => !isUsingStation && interactionCooldown <= 0f;
    public string GetInteractionPrompt()   => "Medical Station";
    public bool   IsContinuousInteraction() => false;
    public void   OnLookEnter()            { }
    public void   OnLookExit()             { }

    // ===== ENTER =====

    private void EnterStation(GameObject interactor)
    {
        playerController    = interactor.GetComponent<PlayerController>();
        characterController = interactor.GetComponent<CharacterController>();
        playerCamera        = interactor.GetComponentInChildren<Camera>();

        if (playerController == null || playerCamera == null) return;

        // Recupera Cancel action da PlayerInput
        PlayerInput pi = playerInputReference != null
            ? playerInputReference
            : interactor.GetComponent<PlayerInput>();

        if (pi != null)
            cancelAction = pi.actions["Cancel"];

        // Salva stato originale
        originalPlayerPosition = interactor.transform.position;
        originalPlayerRotation = interactor.transform.rotation;
        originalCameraRotation = playerCamera.transform.localRotation;

        wasPlayerControllerEnabled = playerController.enabled;
        playerController.enabled   = false;
        if (characterController != null)
            characterController.enabled = false;

        isUsingStation = true;

        // Attiva UI
        if (dashboardUI != null)
            dashboardUI.gameObject.SetActive(true);

        VirtualCursor.Instance?.Activate();

        StartCoroutine(TransitionToStation(interactor));
    }

    private IEnumerator TransitionToStation(GameObject interactor)
    {
        isTransitioning = true;

        Transform t       = interactor.transform;
        Vector3   targetPos = playerSnapPoint != null ? playerSnapPoint.position : transform.position;
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

        isTransitioning = false;

        // Camera verso il monitor (stesso pattern di EngineeringStation)
        if (cameraLookAtPoint != null)
            StartCoroutine(LookAtMonitorRoutine());
        else if (dashboardUI != null)
            dashboardUI.Open();
    }

    private IEnumerator LookAtMonitorRoutine()
    {
        Vector3 direction = (cameraLookAtPoint.position - playerCamera.transform.position).normalized;
        Quaternion worldTarget = Quaternion.LookRotation(direction);
        targetCameraLocalRotation = Quaternion.Inverse(playerCamera.transform.parent.rotation) * worldTarget;

        float progress = 0f;
        while (progress < 1f)
        {
            progress += Time.deltaTime * cameraTransitionSpeed;
            playerCamera.transform.localRotation = Quaternion.Lerp(
                playerCamera.transform.localRotation,
                targetCameraLocalRotation,
                progress);
            yield return null;
        }

        playerCamera.transform.localRotation = targetCameraLocalRotation;

        if (dashboardUI != null)
            dashboardUI.Open();
    }

    // ===== EXIT =====

    private void ExitStation()
    {
        if (!isUsingStation) return;

        interactionCooldown = COOLDOWN_DURATION;
        isUsingStation      = false;

        if (dashboardUI != null)
        {
            dashboardUI.Close();
            dashboardUI.gameObject.SetActive(false);
        }

        VirtualCursor.Instance?.Deactivate();

        StartCoroutine(TransitionFromStation());
    }

    private IEnumerator TransitionFromStation()
    {
        GameObject interactor = playerController?.gameObject;
        if (interactor == null) yield break;

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

        playerController.enabled = wasPlayerControllerEnabled;
        if (characterController != null)
            characterController.enabled = true;
    }
}
