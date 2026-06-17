using System.Collections.Generic;
using SpaceSurvivor.Ship;
using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// ShipTabUI — Milestone 2. Tab "Nave" del Tablet.
///
/// DATI REALI: Fleet Account (EconomyManager.FleetCredits).
/// LISTA EQUIPAGGIO: usa NetworkManager.ConnectedClientsIds — i NOMI reali dei
/// giocatori non sono disponibili finché non esisterà un sistema di sync
/// dell'identità giocatore (dipende da: PlayerIdentity o simile, M3). Per ora
/// ogni riga mostra "Player [clientId]" (o "Tu" per il client locale).
///
/// "Invia crediti": il pulsante è visibile/attivo SOLO se
/// NetworkManager.Singleton.IsHost è true sul client locale — questo è
/// puramente un gate UX. La validazione che conta è server-side, dentro
/// EconomyManager.RequestTransferToPlayerRpc (un client che modifica il
/// proprio client per bypassare il gate UI verrebbe comunque respinto dal server).
///
/// STATO SISTEMI: in M2 mostra solo un riepilogo testuale minimo (i dettagli
/// completi restano su Monitor 2 in Sala Macchine) per non introdurre qui
/// dipendenze dirette a PropulsionSystem/FTLDrive/ShieldSystem/HullSystem non
/// richieste in questa sessione — estendibile in M4-M5 polish.
/// </summary>
public class ShipTabUI : MonoBehaviour, IDashboardPanel
{
    [Header("Fleet Account")]
    [SerializeField] private TextMeshProUGUI fleetCreditsLabel;

    [Header("Stato sistemi (riepilogo, sola lettura)")]
    [SerializeField] private TextMeshProUGUI systemsStatusLabel;

    [Header("Equipaggio")]
    [SerializeField] private Transform crewListContainer;
    [SerializeField] private CrewCreditEntry crewEntryPrefab;
    [Tooltip("M2: importo fisso per click. Slider/importo personalizzato in una sessione futura.")]
    [SerializeField] private int transferAmountPerClick = 100;

    private const float REFRESH_INTERVAL = 0.5f;
    private readonly List<CrewCreditEntry> spawnedEntries = new List<CrewCreditEntry>();

    public void Open()
    {
        RefreshFleetCredits(EconomyManager.Instance != null ? EconomyManager.Instance.FleetCredits : 0);
        EconomyManager.OnFleetCreditsChanged += RefreshFleetCredits;

        InvokeRepeating(nameof(RefreshCrewList), 0f, REFRESH_INTERVAL);
        InvokeRepeating(nameof(RefreshSystemsStatus), 0f, REFRESH_INTERVAL);
    }

    public void Close()
    {
        EconomyManager.OnFleetCreditsChanged -= RefreshFleetCredits;
        CancelInvoke(nameof(RefreshCrewList));
        CancelInvoke(nameof(RefreshSystemsStatus));
    }

    private void RefreshFleetCredits(int amount)
    {
        if (fleetCreditsLabel != null)
            fleetCreditsLabel.text = $"{amount} cr";
    }

    private void RefreshSystemsStatus()
    {
        if (systemsStatusLabel == null) return;
        systemsStatusLabel.text = "Stato sistemi — dettaglio su Monitor 2, Sala Macchine";
    }

    private void RefreshCrewList()
    {
        if (crewListContainer == null || crewEntryPrefab == null) return;
        if (NetworkManager.Singleton == null) return;

        bool localIsHost = NetworkManager.Singleton.IsHost;
        var connectedIds = NetworkManager.Singleton.ConnectedClientsIds;

        while (spawnedEntries.Count < connectedIds.Count)
            spawnedEntries.Add(Instantiate(crewEntryPrefab, crewListContainer));

        while (spawnedEntries.Count > connectedIds.Count)
        {
            int last = spawnedEntries.Count - 1;
            Destroy(spawnedEntries[last].gameObject);
            spawnedEntries.RemoveAt(last);
        }

        for (int i = 0; i < connectedIds.Count; i++)
        {
            ulong clientId = connectedIds[i];
            bool isLocalPlayer = clientId == NetworkManager.Singleton.LocalClientId;
            spawnedEntries[i].Bind(clientId, isLocalPlayer, localIsHost, transferAmountPerClick);
        }
    }
}
