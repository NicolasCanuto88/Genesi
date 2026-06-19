using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

/// <summary>
/// RelayManager — Rev O (Blocco 1 M3, rewrite).
///
/// PRIMA: componente ibrido backend+UI — aveva button listener, TMP_InputField
/// e TextMeshProUGUI hardcodati nel proprio Inspector, avviava host/client
/// dall'interno di Start() tramite onClick. Incompatibile con un menu separato.
///
/// ORA: puro backend. Gestisce esclusivamente:
///   - Inizializzazione Unity Gaming Services (UGS) una sola volta
///   - Creazione allocation Relay per l'host → restituisce il join code
///   - Join allocation Relay per il client dato un join code
///   - Esposizione di IsServiceReady / LastJoinCode per MainMenuManager
///
/// Nessuna dipendenza da UI — la UI sta tutta in MainMenuManager.
/// Questo componente resta sul suo GameObject in scena (RelayManager), invariato.
///
/// ⚠️ Non sostituisce il transport per test puramente locali (stesso host,
/// stessa LAN): in quel caso NetworkManager.StartHost() funziona senza Relay.
/// MainMenuManager gestisce il fallback locale quando IsServiceReady è false.
///
/// ⚠️ In produzione questo sarà sostituito da SteamworksTransport (M3+, GDD §2B).
/// L'interfaccia pubblica (StartHostAsync / StartClientAsync) può restare la stessa
/// — basta reimplementare l'interno con Steamworks al momento del passaggio.
/// </summary>
public class RelayManager : MonoBehaviour
{
    [Header("Impostazioni")]
    [Tooltip("Numero massimo di giocatori per allocation Relay (il codice host + connessi).")]
    [SerializeField] private int maxPlayers = 5;

    /// <summary>Join code dell'ultima partita creata come host. Null fino alla prima chiamata riuscita.</summary>
    public string LastJoinCode { get; private set; }

    /// <summary>True quando UGS è inizializzato e il giocatore è autenticato — ok per chiamare Host/Join.</summary>
    public bool IsServiceReady { get; private set; }

    /// <summary>Fired sul thread principale quando UGS è pronto. MainMenuManager lo usa per abilitare i pulsanti.</summary>
    public static event Action OnServiceReady;

    // ── LIFECYCLE ─────────────────────────────────────────────────────────────

    private async void Start()
    {
        try
        {
            await InitializeServicesAsync();
            IsServiceReady = true;
            OnServiceReady?.Invoke();
            Debug.Log("[RelayManager] UGS pronto, giocatore autenticato.");
        }
        catch (Exception e)
        {
            // Non blocca il gioco: MainMenuManager usa il fallback locale se !IsServiceReady.
            Debug.LogWarning($"[RelayManager] Inizializzazione UGS fallita: {e.Message}. " +
                             "Il menu funzionerà in modalità locale (senza Relay).");
        }
    }

    // ── INIT ──────────────────────────────────────────────────────────────────

    private async Task InitializeServicesAsync()
    {
        if (IsServiceReady) return;

        await UnityServices.InitializeAsync();

        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    // ── API PUBBLICA ──────────────────────────────────────────────────────────

    /// <summary>
    /// Crea una nuova partita Relay come host. Chiama NetworkManager.StartHost() internamente.
    /// </summary>
    /// <returns>Il join code che gli altri giocatori devono inserire per connettersi.</returns>
    /// <exception cref="Exception">Se Relay o UGS non sono disponibili.</exception>
    public async Task<string> StartHostAsync()
    {
        // Se l'init asincrono in Start() non è ancora finito, completalo ora.
        if (!IsServiceReady)
        {
            await InitializeServicesAsync();
            IsServiceReady = true;
        }

        Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers);
        LastJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetRelayServerData(new RelayServerData(allocation, "dtls"));

        NetworkManager.Singleton.StartHost();

        Debug.Log($"[RelayManager] Host avviato. Join code: {LastJoinCode}");
        return LastJoinCode;
    }

    /// <summary>
    /// Unisce una partita Relay esistente come client.
    /// Chiama NetworkManager.StartClient() internamente.
    /// </summary>
    /// <param name="joinCode">Codice a 6 caratteri generato dall'host.</param>
    /// <exception cref="Exception">Se il codice è errato o la partita non esiste più.</exception>
    public async Task StartClientAsync(string joinCode)
    {
        if (!IsServiceReady)
        {
            await InitializeServicesAsync();
            IsServiceReady = true;
        }

        JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetRelayServerData(new RelayServerData(joinAllocation, "dtls"));

        NetworkManager.Singleton.StartClient();

        Debug.Log($"[RelayManager] Client avviato con codice: {joinCode}");
    }
}