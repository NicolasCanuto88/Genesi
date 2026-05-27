using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

/// <summary>
/// Handles player interaction with interactable objects
/// Uses Unity's New Input System
/// </summary>
public class InteractionSystem : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float interactionRange = 2.5f;
    [SerializeField] private LayerMask interactionLayer = ~0;

    [Header("UI References")]
    [SerializeField] private GameObject interactionPromptUI;
    [SerializeField] private TextMeshProUGUI interactionText;
    [SerializeField] private string defaultPromptText = "[{interact}] Interact";

    [Header("References")]
    [SerializeField] private Transform cameraTransform;

    // State
    private IInteractable currentInteractable;
    private bool isInteracting;

    // Debug
    [Header("Debug")]
    [SerializeField] private bool showDebugRay = true;

    private void Awake()
    {
        // Auto-find camera if not assigned
        if (cameraTransform == null)
        {
            cameraTransform = GetComponentInChildren<Camera>()?.transform;

            if (cameraTransform == null)
            {
                Camera mainCam = Camera.main;
                if (mainCam != null)
                {
                    cameraTransform = mainCam.transform;
                }
                else
                {
                    Debug.LogError("[InteractionSystem] No camera found!");
                }
            }
        }
    }

    private void Update()
    {
        CheckForInteractable();
        UpdateUI();
    }

    private void CheckForInteractable()
    {
        if (cameraTransform == null)
            return;

        Ray ray = new Ray(cameraTransform.position, cameraTransform.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, interactionRange, interactionLayer))
        {
            IInteractable interactable = hit.collider.GetComponent<IInteractable>();

            if (interactable != null && interactable.CanInteract())
            {
                if (currentInteractable != interactable)
                {
                    currentInteractable?.OnLookExit();
                    currentInteractable = interactable;
                    currentInteractable.OnLookEnter();
                }

                if (showDebugRay)
                {
                    Debug.DrawRay(ray.origin, ray.direction * hit.distance, Color.green);
                }

                return;
            }
        }

        // No interactable found
        if (currentInteractable != null)
        {
            currentInteractable.OnLookExit();
            currentInteractable = null;
        }

        if (showDebugRay)
        {
            Debug.DrawRay(ray.origin, ray.direction * interactionRange, Color.red);
        }
    }

    private void UpdateUI()
    {
        if (currentInteractable != null && !isInteracting)
        {
            if (interactionPromptUI != null)
            {
                interactionPromptUI.SetActive(true);
            }

            if (interactionText != null)
            {
                string promptText = currentInteractable.GetInteractionPrompt();

                if (string.IsNullOrEmpty(promptText))
                {
                    promptText = defaultPromptText;
                }

                // Replace button placeholders with correct device buttons
                if (InputDeviceManager.Instance != null)
                {
                    promptText = InputDeviceManager.Instance.FormatPrompt(promptText);
                }

                interactionText.text = promptText;
            }
        }
        else
        {
            if (interactionPromptUI != null)
            {
                interactionPromptUI.SetActive(false);
            }
        }
    }

    // ===== INPUT SYSTEM CALLBACK =====
    public void OnInteract(InputValue value)
    {
        // Il tuo Input Actions ha "Hold" interaction per Interact
        // Quindi questo viene chiamato quando inizia l'hold
        if (value.isPressed && currentInteractable != null && !isInteracting)
        {
            StartInteraction();
        }
    }

    private void StartInteraction()
    {
        isInteracting = true;
        currentInteractable.Interact(this.gameObject);

        if (!currentInteractable.IsContinuousInteraction())
        {
            EndInteraction();
        }
    }

    public void EndInteraction()
    {
        isInteracting = false;
        CheckForInteractable();
    }

    // Properties
    public bool IsInteracting => isInteracting;
    public IInteractable CurrentInteractable => currentInteractable;
}

/// <summary>
/// Interface for all interactable objects
/// </summary>
public interface IInteractable
{
    void Interact(GameObject interactor);
    bool CanInteract();
    string GetInteractionPrompt();
    bool IsContinuousInteraction();
    void OnLookEnter();
    void OnLookExit();
}