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
    [SerializeField] private UnityEngine.UI.Selectable firstSelectedElement;

    [Header("Player Positioning")]
    [SerializeField] private Transform playerSnapPoint;
    [Tooltip("Un LookAt point per monitor, nell'ordine Monitor 1/2/3.")]
    [SerializeField] private Transform[] cameraLookAtPoints;
    [SerializeField] private float snapTransitionSpeed = 5f;
    [SerializeField] private float cameraTransitionSpeed = 8f;

    [Header("Camera Control")]
    [Tooltip("Manual camera pitch offset (X rotation). Positive = look UP, Negative = look DOWN")]
    [SerializeField] private float cameraPitchOffset = 0f; // Vertical rotation
    [Tooltip("Manual camera yaw offset (Y rotation). Positive = look RIGHT, Negative = look LEFT")]
    [SerializeField] private float cameraYawOffset = 0f; // Horizontal rotation
    [Tooltip("Use LookAt point or manual rotation offsets?")]
    [SerializeField] private bool useLookAtPoint = true;

    [Header("Input")]
    [SerializeField] private bool allowMovementWhileUsing = false;

    private bool isUsingStation = false;
    private bool isExiting = false; // Differentiates enter vs exit transition
    private PlayerController playerController;
    private CharacterController characterController;
    private PlayerInput playerInputComponent;
    private Camera playerCamera;
    private InputAction cancelAction;

    // Cooldown to prevent re-entering immediately after exit
    private float interactionCooldown = 0f;
    private const float COOLDOWN_DURATION = 0.5f;

    // Stored player state
    private Vector3 originalPlayerPosition;
    private Quaternion originalPlayerRotation;
    private Quaternion originalCameraRotation;
    private bool wasPlayerControllerEnabled;

    // Transition state
    private bool isTransitioning = false;
    private float transitionProgress = 0f;

    private void Awake()
    {
        // Ensure dashboard is closed at start
        if (dashboardUI != null)
        {
            dashboardUI.gameObject.SetActive(false);
        }

        // Setup trigger collider
        BoxCollider trigger = GetComponent<BoxCollider>();
        trigger.isTrigger = true;

        // Assign Event Camera to Canvas
        if (dashboardCanvas != null && dashboardCanvas.renderMode == RenderMode.WorldSpace)
        {
            // Will be assigned in Start when camera is found
        }
    }

    private void Start()
    {
        // Find and assign Main Camera to World Space Canvas
        if (dashboardCanvas != null && Camera.main != null)
        {
            dashboardCanvas.worldCamera = Camera.main;
        }
    }

    // ===== IInteractable Implementation =====

    public void Interact(GameObject interactor)
    {
        if (isUsingStation)
        {
            ExitStation();
        }
        else
        {
            EnterStation(interactor);
        }
    }

    public string GetInteractionPrompt()
    {
        return isUsingStation ? "[{cancel}] Exit Engineering Station" : "[{interact}] Use Engineering Station";
    }

    public bool CanInteract()
    {
        return dashboardUI != null && playerSnapPoint != null && interactionCooldown <= 0f;
    }

    public bool IsContinuousInteraction()
    {
        return false;
    }

    public void OnLookEnter()
    {
        // Optional: Highlight effect
    }

    public void OnLookExit()
    {
        // Optional: Remove highlight
    }

    // ===== Station Control =====

    private void EnterStation(GameObject player)
    {
        if (playerSnapPoint == null || cameraLookAtPoints[0] == null)
        {
            Debug.LogError("[EngineeringStation] Missing snap points!");
            return;
        }

        // Get player components
        if (playerController == null)
        {
            playerController = player.GetComponent<PlayerController>();
            characterController = player.GetComponent<CharacterController>();
            playerCamera = player.GetComponentInChildren<Camera>();
            playerInputComponent = player.GetComponent<PlayerInput>();

            // Get Cancel action from player's input
            if (playerInputComponent != null)
            {
                cancelAction = playerInputComponent.actions.FindAction("Cancel");
            }
        }

        if (playerController == null || characterController == null || playerCamera == null)
        {
            Debug.LogError("[EngineeringStation] Player missing required components!");
            return;
        }

        // Store original state
        originalPlayerPosition = player.transform.position;
        originalPlayerRotation = player.transform.rotation;
        originalCameraRotation = playerCamera.transform.localRotation;
        wasPlayerControllerEnabled = playerController.enabled;

        // Disable player control during transition
        playerController.enabled = false;

        // Start transition
        isUsingStation = true;
        isExiting = false;
        isTransitioning = true;
        transitionProgress = 0f;

        // Open dashboard
        if (dashboardUI != null)
        {
            dashboardUI.gameObject.SetActive(true);
            dashboardUI.Open();
        }

        // Set first selected element for gamepad
        if (firstSelectedElement != null && EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(firstSelectedElement.gameObject);
        }

        // Activate virtual cursor (handles gamepad/keyboard cursor visibility internally)
        if (VirtualCursor.Instance != null)
        {
            VirtualCursor.Instance.Activate();
        }
        else
        {
            // No virtual cursor, fallback to standard mouse
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        Debug.Log("[EngineeringStation] Entering station - Transitioning to workstation");
    }

    private void ExitStation()
    {
        if (!isUsingStation) return;

        // Start exit transition
        isExiting = true;
        isTransitioning = true;
        transitionProgress = 0f;

        // Deactivate virtual cursor
        if (VirtualCursor.Instance != null)
        {
            VirtualCursor.Instance.Deactivate();
        }

        // Close dashboard
        if (dashboardUI != null)
        {
            dashboardUI.Close();
            dashboardUI.gameObject.SetActive(false);
        }

        // Clear EventSystem selection
        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }

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

        monitorLookCoroutine = null;
    }

    private void Update()
    {
        // Tick cooldown
        if (interactionCooldown > 0f)
        {
            interactionCooldown -= Time.deltaTime;
        }

        // Handle exit via Cancel action (ESC keyboard / B gamepad)
        if (isUsingStation && !isTransitioning)
        {
            if (cancelAction != null && cancelAction.WasPressedThisFrame())
            {
                ExitStation();
            }
        }

        // Handle transitions
        if (isTransitioning)
        {
            UpdateTransition();
        }
    }

    private void UpdateTransition()
    {
        if (playerController == null || characterController == null || playerCamera == null)
            return;

        transitionProgress += Time.deltaTime * snapTransitionSpeed;
        float t = Mathf.Clamp01(transitionProgress);

        if (!isExiting)
        {
            // ENTERING station - Move TO snap point

            // Disable CharacterController for direct position control
            characterController.enabled = false;

            // Interpolate player position
            Vector3 targetPosition = playerSnapPoint.position;
            playerController.transform.position = Vector3.Lerp(originalPlayerPosition, targetPosition, t);

            // Interpolate player rotation (body)
            Quaternion targetRotation = playerSnapPoint.rotation;
            playerController.transform.rotation = Quaternion.Slerp(originalPlayerRotation, targetRotation, t);

            // Interpolate camera rotation
            Quaternion targetCameraLocalRotation;

            if (useLookAtPoint && cameraLookAtPoints[0] != null)
            {
                Vector3 cameraPosition = playerCamera.transform.position;
                Vector3 directionToMonitor = (cameraLookAtPoints[0].position - cameraPosition).normalized;
                Quaternion targetCameraWorldRotation = Quaternion.LookRotation(directionToMonitor);

                Quaternion worldToLocal = Quaternion.Inverse(playerController.transform.rotation);
                targetCameraLocalRotation = worldToLocal * targetCameraWorldRotation;
            }
            else
            {
                targetCameraLocalRotation = Quaternion.Euler(cameraPitchOffset, cameraYawOffset, 0f);
            }

            float cameraTProgress = Mathf.Clamp01(transitionProgress * (cameraTransitionSpeed / snapTransitionSpeed));
            playerCamera.transform.localRotation = Quaternion.Slerp(originalCameraRotation, targetCameraLocalRotation, cameraTProgress);

            characterController.enabled = true;

            // Transition complete
            if (t >= 1f)
            {
                isTransitioning = false;

                if (!allowMovementWhileUsing)
                {
                    playerController.enabled = false;
                }
                else
                {
                    playerController.enabled = true;
                }

                Debug.Log("[EngineeringStation] Transition complete - At workstation");
            }
        }
        else
        {
            // EXITING station - Return to original position

            characterController.enabled = false;

            playerController.transform.position = Vector3.Lerp(playerSnapPoint.position, originalPlayerPosition, t);
            playerController.transform.rotation = Quaternion.Slerp(playerSnapPoint.rotation, originalPlayerRotation, t);

            // Camera: lerp from station look direction back to original
            Quaternion stationCameraLocalRot;
            if (useLookAtPoint && cameraLookAtPoints[0] != null)
            {
                Vector3 dirToMonitor = (cameraLookAtPoints[0].position - playerCamera.transform.position).normalized;
                Quaternion stationCameraWorldRot = Quaternion.LookRotation(dirToMonitor);
                stationCameraLocalRot = Quaternion.Inverse(playerController.transform.rotation) * stationCameraWorldRot;
            }
            else
            {
                stationCameraLocalRot = Quaternion.Euler(cameraPitchOffset, cameraYawOffset, 0f);
            }

            playerCamera.transform.localRotation = Quaternion.Slerp(stationCameraLocalRot, originalCameraRotation, t);

            characterController.enabled = true;

            // Transition complete
            if (t >= 1f)
            {
                isTransitioning = false;
                isExiting = false;
                isUsingStation = false;

                // Set cooldown to prevent immediate re-enter
                interactionCooldown = COOLDOWN_DURATION;

                // Force final position/rotation
                characterController.enabled = false;
                playerController.transform.position = originalPlayerPosition;
                playerController.transform.rotation = originalPlayerRotation;
                playerCamera.transform.localRotation = originalCameraRotation;
                characterController.enabled = true;

                // Restore player controller
                playerController.enabled = wasPlayerControllerEnabled;

                // Restore cursor lock
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;

                Debug.Log("[EngineeringStation] Exit complete - Normal movement restored");
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        // Draw snap point
        if (playerSnapPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(playerSnapPoint.position, 0.3f);
            Gizmos.DrawRay(playerSnapPoint.position, playerSnapPoint.forward * 0.5f);

            // Draw player height (eyes position)
            Vector3 eyesPosition = playerSnapPoint.position + Vector3.up * 1.6f;
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(eyesPosition, 0.1f);

            // Draw camera direction preview
            if (useLookAtPoint && cameraLookAtPoints[0] != null)
            {
                // LookAt mode - draw line to target
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(eyesPosition, cameraLookAtPoints[0].position);

                Vector3 direction = (cameraLookAtPoints[0].position - eyesPosition).normalized;
                Gizmos.color = Color.red;
                Gizmos.DrawRay(eyesPosition, direction * 1.5f);
            }
            else
            {
                // Manual mode - draw rotation preview
                Quaternion manualRotation = playerSnapPoint.rotation * Quaternion.Euler(cameraPitchOffset, cameraYawOffset, 0f);
                Vector3 lookDirection = manualRotation * Vector3.forward;

                Gizmos.color = Color.magenta;
                Gizmos.DrawRay(eyesPosition, lookDirection * 2f);

                // Draw sphere at end of look ray
                Gizmos.DrawWireSphere(eyesPosition + lookDirection * 2f, 0.15f);
            }

#if UNITY_EDITOR
            // Label with current mode and values
            string modeText = useLookAtPoint ? "MODE: LookAt Point" : $"MODE: Manual (Pitch: {cameraPitchOffset:F1}°, Yaw: {cameraYawOffset:F1}°)";
            
            if (useLookAtPoint && cameraLookAtPoints[0] != null)
            {
                Vector3 direction = (cameraLookAtPoints[0].position - eyesPosition).normalized;
                float verticalAngle = Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude) * Mathf.Rad2Deg;
                modeText += $"\nAngle: {verticalAngle:F1}° (+ = UP)";
            }
            
            UnityEditor.Handles.Label(eyesPosition + Vector3.up * 0.5f, modeText);
#endif
        }

        // Draw look at point (only in LookAt mode)
        if (useLookAtPoint && cameraLookAtPoints[0] != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(cameraLookAtPoints[0].position, 0.2f);
        }
    }
}