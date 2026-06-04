using UnityEngine;
using Unity.Netcode;

#if UNITY_EDITOR
using ParrelSync;
#endif

/// <summary>
/// Avvia automaticamente il NetworkManager.
/// - Clone ParrelSync → Start Client (si connette all'Host originale)
/// - Istanza originale o build → Start Host
/// </summary>
public class AutoStartHost : MonoBehaviour
{
    private void Start()
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsListening) return;

#if UNITY_EDITOR
        if (ClonesManager.IsClone())
        {
            NetworkManager.Singleton.StartClient();
            Debug.Log("[AutoStartHost] Clone rilevato → Start Client");
            return;
        }
#endif
        NetworkManager.Singleton.StartHost();
        Debug.Log("[AutoStartHost] → Start Host");
    }
}