using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// CrewCreditEntry — Milestone 2.
/// Riga equipaggio nel tab "Nave" del Tablet. Pattern analogo a CrewHPEntry.
///
/// Il pulsante "Invia crediti" E il relativo importo sono visibili/attivi
/// SOLO quando localIsHost è true — su ogni riga, inclusa quella del client
/// locale (l'host versa anche su sé stesso, vedi nota in EconomyManager).
/// Per un client che non è host, la riga mostra SOLO il nome: l'importo non
/// avrebbe senso da vedere se non si può comunque cliccare nulla.
///
/// ⚠️ Nome reale non disponibile — dipende da: futuro sistema di sync identità
/// giocatore (es. PlayerIdentity, M3). Per ora mostra "Player [clientId]".
/// </summary>
public class CrewCreditEntry : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI nameLabel;
    [SerializeField] private Button sendCreditsButton;
    [SerializeField] private TextMeshProUGUI sendAmountLabel;

    private ulong boundClientId;
    private int amountToSend;

    /// <summary>
    /// Configura la riga. Chiamato da ShipTabUI.RefreshCrewList() per ogni
    /// client connesso, ogni REFRESH_INTERVAL secondi (object-pool style,
    /// nessuna allocazione extra se il numero di giocatori non cambia).
    /// </summary>
    public void Bind(ulong clientId, bool isLocalPlayer, bool localIsHost, int amount)
    {
        boundClientId = clientId;
        amountToSend = amount;

        if (nameLabel != null)
            nameLabel.text = isLocalPlayer ? "Tu" : $"Player {clientId}";

        // L'host può versare crediti su QUALSIASI riga, inclusa la propria.
        // Per chi NON è host, niente importo e niente pulsante: la riga è
        // puramente informativa (solo il nome).
        bool showButton = localIsHost;

        if (sendAmountLabel != null)
        {
            sendAmountLabel.gameObject.SetActive(showButton);
            if (showButton)
                sendAmountLabel.text = $"+{amount} cr";
        }

        if (sendCreditsButton != null)
        {
            sendCreditsButton.gameObject.SetActive(showButton);
            sendCreditsButton.onClick.RemoveAllListeners();
            if (showButton)
                sendCreditsButton.onClick.AddListener(OnSendClicked);
        }
    }

    private void OnSendClicked()
    {
        if (SpaceSurvivor.Ship.EconomyManager.Instance == null)
        {
            Debug.LogWarning("[CrewCreditEntry] EconomyManager.Instance è null — impossibile inviare crediti.");
            return;
        }

        SpaceSurvivor.Ship.EconomyManager.Instance.RequestTransferToPlayerRpc(boundClientId, amountToSend);
    }
}