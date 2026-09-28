using UnityEngine;

/// <summary>
/// MedicalSupplyLocker — armadietto medico di bordo (Rev BQ · Q30-a).
///
/// Il giocatore lo guarda e preme Interact: il suo kit medico personale (PlayerMedKit)
/// si riempie fino alla capienza prelevando dalla stiva della nave (InventorySystem).
/// Trasferimento esplicito: quello che entra nel kit esce dalla stiva, e il monitor
/// medico (scorte) si aggiorna da solo.
///
/// PATTERN: MonoBehaviour di scena + IInteractable one-shot, come Door e Ladder. Nessun
/// NetworkObject: l'armadietto non ha stato proprio. La richiesta passa dall'RPC del kit
/// del giocatore (owner → server), che valida il mittente e fa il prelievo sul server.
///
/// ⚠️ SETUP EDITOR: collider NON trigger sul layer "Interactable" (6), l'unico visto da
/// InteractionSystem. Vedi guida Editor di Rev BQ.
/// </summary>
public class MedicalSupplyLocker : MonoBehaviour, IInteractable
{
    [Header("Prompt (testi di gioco in inglese)")]
    [Tooltip("Prompt quando il kit ha spazio. {interact} è sostituito dal tasto da InputDeviceManager.")]
    [SerializeField] private string restockPrompt = "[{interact}] Restock medical kit";

    [Tooltip("Prompt quando il kit è già pieno. Interact resta possibile e risponde \"already full\".")]
    [SerializeField] private string kitFullPrompt = "Medical kit full";

    public bool CanInteract()
    {
        PlayerMedKit kit = PlayerMedKit.LocalInstance;
        if (kit == null || !kit.IsSpawned) return false;

        PlayerHealthSystem localHealth = PlayerHealthSystem.LocalInstance;
        return localHealth != null && localHealth.IsAlive;
    }

    public string GetInteractionPrompt()
    {
        PlayerMedKit kit = PlayerMedKit.LocalInstance;
        return kit != null && kit.IsFull ? kitFullPrompt : restockPrompt;
    }

    public bool IsContinuousInteraction() => false;

    public void OnLookEnter() { }

    public void OnLookExit() { }

    public void Interact(GameObject interactor)
    {
        if (interactor == null) return;

        PlayerMedKit kit = interactor.GetComponent<PlayerMedKit>();
        if (kit == null)
        {
            Debug.LogError("[MedicalSupplyLocker] PlayerMedKit assente sul giocatore che interagisce: " +
                           "aggiungerlo al root del Player prefab (guida Editor di Rev BQ).");
            return;
        }

        kit.RequestRestock();
    }
}
