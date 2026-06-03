using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Avvia automaticamente il NetworkManager come Host se nessuno lo ha già avviato.
/// Garantisce che PowerManager.OnNetworkSpawn() venga chiamato anche in single player.
/// </summary>
public class AutoStartHost : MonoBehaviour
{
    private void Start()
    {
        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.StartHost();
            Debug.Log("[AutoStartHost] Avviato automaticamente come Host (single player)");
        }
    }
}