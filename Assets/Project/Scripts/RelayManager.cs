using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using System.Threading.Tasks;

/// <summary>
/// Gestisce la connessione via Unity Relay per test su reti diverse.
/// In produzione verrà sostituito da SteamworksTransport (M3).
/// </summary>
public class RelayManager : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_InputField joinCodeInput;
    [SerializeField] private TextMeshProUGUI joinCodeDisplay;
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private Button hostButton;
    [SerializeField] private Button clientButton;

    [Header("Settings")]
    [SerializeField] private int maxPlayers = 4;

    private async void Start()
    {
        // Inizializza UGS e autenticazione anonima
        await UnityServices.InitializeAsync();

        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();

        SetStatus("Pronto. Crea o unisciti a una partita.");

        if (hostButton != null) hostButton.onClick.AddListener(StartHost);
        if (clientButton != null) clientButton.onClick.AddListener(StartClient);
    }

    public async void StartHost()
    {
        SetStatus("Creazione partita...");

        try
        {
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers);
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            // Configura il transport con i dati Relay
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetRelayServerData(new RelayServerData(allocation, "dtls"));

            NetworkManager.Singleton.StartHost();

            // Mostra il codice all'host
            if (joinCodeDisplay != null)
                joinCodeDisplay.text = $"Join Code: {joinCode}";

            SetStatus($"Partita creata! Codice: {joinCode}");
            Debug.Log($"[RelayManager] Host avviato. Join code: {joinCode}");
        }
        catch (System.Exception e)
        {
            SetStatus($"Errore: {e.Message}");
            Debug.LogError($"[RelayManager] Host error: {e}");
        }
    }

    public async void StartClient()
    {
        if (joinCodeInput == null || string.IsNullOrEmpty(joinCodeInput.text))
        {
            SetStatus("Inserisci il join code.");
            return;
        }

        SetStatus("Connessione in corso...");

        try
        {
            string joinCode = joinCodeInput.text.Trim().ToUpper();
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetRelayServerData(new RelayServerData(joinAllocation, "dtls"));

            NetworkManager.Singleton.StartClient();
            SetStatus("Connesso!");
            Debug.Log($"[RelayManager] Client connesso con codice: {joinCode}");
        }
        catch (System.Exception e)
        {
            SetStatus($"Errore: {e.Message}");
            Debug.LogError($"[RelayManager] Client error: {e}");
        }
    }

    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
        Debug.Log($"[RelayManager] {message}");
    }
}