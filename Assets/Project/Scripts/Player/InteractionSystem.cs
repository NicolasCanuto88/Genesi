using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

/// <summary>
/// Handles player interaction with interactable objects
/// Uses Unity's New Input System
///
/// INTERAZIONI CONTINUE (Rev BT-c · Q73-a): un'interazione continua (oggi solo la
/// rianimazione, PlayerReviveTarget) tiene isInteracting acceso finché l'oggetto non chiama
/// EndInteraction. Finché è acceso, InteractionSystem non mostra prompt e non avvia altre
/// interazioni, e kit e lancio (PlayerMedKit, PlayerThrower) restano bloccati.
/// Rete di sicurezza: l'oggetto che ha avviato l'interazione è ricordato in activeInteractable;
/// se viene distrutto o disattivato prima di chiamare EndInteraction (per esempio il giocatore
/// a terra si disconnette e il suo NetworkObject viene despawnato), l'interazione si chiude qui
/// a inizio Update. Prima di Rev BT-c il giocatore restava "in interazione" per sempre: niente
/// porte, scale, postazioni, letto, armadietto, kit né lancio, nemmeno dopo la clonazione.
/// La stessa verifica protegge OnInteract da un bersaglio distrutto nel frame precedente.
/// File convertito in UTF-8 in Rev BT-c (era Windows-1252).
///
/// BERSAGLIO VINCOLATO (Rev BW-c · Q113-a / Q114-a): un interagibile può vincolare a sé il bersaglio
/// (SetLockedInteractable) finché non lo libera (ClearLockedInteractable). Lo usa la scala: in salita
/// la visuale gira (±75°) e il raggio può non colpirla, ma Interact deve sempre poter scendere o
/// lasciare la presa, e nessun altro oggetto deve diventare bersaglio. Con un vincolo attivo il raggio
/// non viene lanciato; se il vincolato non è più vivo il vincolo cade da solo; se il suo CanInteract
/// è falso non c'è bersaglio (niente prompt).
///
/// TABLET APERTO (Rev BX-a · Q117-a): con il tablet aperto o in transizione (TabletStation.IsBusy)
/// non c'è bersaglio: niente prompt e Interact non avvia nulla. Prima ci si poteva sedere al Pilota
/// (o a Ingegneria, Scanner, pannelli) con il tablet aperto, e all'uscita il giocatore restava
/// fermo. Il vincolo della scala non viene toccato: torna attivo alla chiusura del tablet.
/// L'altro verso (niente tablet da seduti) è in TabletStation.CanOpen.
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

    private TabletStation tablet;   // Rev BX-a: stesso GameObject del Player

    // State
    private IInteractable currentInteractable;
    private IInteractable activeInteractable;   // Rev BT-c: chi ha avviato l'interazione in corso
    private IInteractable lockedInteractable;   // Rev BW-c: bersaglio vincolato (scala)
    private bool isInteracting;

    // Debug
    [Header("Debug")]
    [Tooltip("Disegna il raggio di interazione con Debug.DrawRay (Scene view). Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugRay = false;

    private void Awake()
    {
        tablet = GetComponent<TabletStation>();   // Rev BX-a

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
        // Rev BT-c (Q73-a): rete di sicurezza — l'interazione continua non può sopravvivere
        // all'oggetto che la guida.
        if (isInteracting && !IsAlive(activeInteractable))
        {
            EndInteraction();
        }

        CheckForInteractable();
        UpdateUI();
    }

    /// <summary>
    /// Rev BT-c — vero se l'interagibile esiste ancora e, se è un componente Unity, non è stato
    /// distrutto né disattivato. Un riferimento d'interfaccia a un MonoBehaviour distrutto non è
    /// null per C#: il controllo passa da UnityEngine.Object.
    /// </summary>
    private static bool IsAlive(IInteractable interactable)
    {
        if (interactable == null) return false;
        if (interactable is Behaviour behaviour) return behaviour != null && behaviour.isActiveAndEnabled;
        if (interactable is Object unityObject) return unityObject != null;
        return true;
    }

    /// <summary>Rev BX-a — tablet aperto o in transizione.</summary>
    private bool IsTabletBusy => tablet != null && tablet.IsBusy;

    private void CheckForInteractable()
    {
        // Rev BX-a (Q117-a) — con il tablet aperto nessun bersaglio (il vincolo resta, non si tocca).
        if (IsTabletBusy)
        {
            SetCurrentInteractable(null);
            return;
        }

        // Rev BW-c — bersaglio vincolato: nessun raggio finché resta attivo.
        if (lockedInteractable != null)
        {
            if (!IsAlive(lockedInteractable))
            {
                lockedInteractable = null;   // l'oggetto non c'è più: si torna al raggio
            }
            else
            {
                SetCurrentInteractable(lockedInteractable.CanInteract() ? lockedInteractable : null);
                return;
            }
        }

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
        // Rev BT-c: IsAlive scarta un bersaglio distrutto dopo l'ultimo CheckForInteractable.
        // Rev BX-a: il tablet può essersi aperto in questo stesso frame (Tab ed E insieme), dopo
        // l'ultimo CheckForInteractable: si ricontrolla qui.
        if (value.isPressed && !IsTabletBusy && IsAlive(currentInteractable) && !isInteracting)
        {
            StartInteraction();
        }
    }

    private void StartInteraction()
    {
        // Rev BT-c: bersaglio fissato prima di Interact — Interact può chiamare EndInteraction,
        // che ricalcola currentInteractable.
        IInteractable target = currentInteractable;
        activeInteractable = target;
        isInteracting = true;
        target.Interact(this.gameObject);

        if (!target.IsContinuousInteraction())
        {
            EndInteraction();
        }
    }

    /// <summary>
    /// Rev BW-c — vincola il bersaglio dell'interazione a questo oggetto finché non viene liberato.
    /// Un solo vincolo alla volta: l'ultimo che lo chiede lo ottiene.
    /// </summary>
    public void SetLockedInteractable(IInteractable target)
    {
        lockedInteractable = target;
        CheckForInteractable();
    }

    /// <summary>Rev BW-c — libera il vincolo, solo se è ancora quello di chi lo chiede.</summary>
    public void ClearLockedInteractable(IInteractable target)
    {
        if (lockedInteractable != target) return;
        lockedInteractable = null;
        CheckForInteractable();
    }

    /// <summary>Rev BW-c — cambio di bersaglio con le notifiche di sguardo (stesso schema del raggio).</summary>
    private void SetCurrentInteractable(IInteractable target)
    {
        if (currentInteractable == target) return;

        currentInteractable?.OnLookExit();
        currentInteractable = target;
        currentInteractable?.OnLookEnter();
    }

    public void EndInteraction()
    {
        isInteracting = false;
        activeInteractable = null;   // Rev BT-c
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