using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// DefibChargePool — pool di cariche del defibrillatore (Morte &amp; Rianimazione, D27).
///
/// PLACEHOLDER (Q6-a): nel design le cariche defib sono un pool di livello MEDBAY
/// (T1=0 / T2=3 / T3=4 / T4=5). La medbay non esiste ancora (suo milestone) →
/// questo singleton fa da segnaposto server-authoritative + replicato, così:
///   - il client può nascondere il prompt quando non ci sono cariche (Q6-a: a T1
///     il defib è indisponibile);
///   - il server è l'unico a consumare cariche (autorità).
/// Quando la medbay esisterà, il suo livello alimenterà questo pool (o lo
/// sostituirà) — stesso spirito del seam TryCure(tier) di Rev BC.
///
/// PATTERN SINGLETON: Instance + OnInstanceReady, identico a OxygenSystem /
/// PoiCollisionResolver ecc. Sistema-nave (una sola istanza in scena).
///
/// NAMESPACE GLOBALE: coerenza-di-feature con i file della rianimazione
/// (PlayerReviveTarget / PlayerHealthSystem, globali). Deviazione documentata.
///
/// ⚠️ SETUP EDITOR: piazzare UN GameObject con questo componente + NetworkObject
/// in scena di gioco (come gli altri sistemi-nave). Deve essere spawnato dal server.
/// </summary>
public class DefibChargePool : NetworkBehaviour
{
    public static DefibChargePool Instance { get; private set; }

    /// <summary>Fired dopo OnNetworkSpawn — i dipendenti si sottoscrivono se Instance è null al loro Start.</summary>
    public static event Action OnInstanceReady;

    [Header("Config")]
    [Tooltip("SO di tuning defib. Fornisce le cariche iniziali (placeholder). Se null si usa 'fallbackStartingCharges'.")]
    [SerializeField] private DefibConfig config;

    [Tooltip("Cariche iniziali usate se 'config' non è assegnato. Placeholder.")]
    [SerializeField] private int fallbackStartingCharges = 3;

    private readonly NetworkVariable<int> netCharges = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public int Charges => netCharges.Value;
    public bool HasCharge => netCharges.Value > 0;

    public override void OnNetworkSpawn()
    {
        Instance = this;

        if (IsServer)
        {
            int start = config != null ? config.PlaceholderStartingCharges : Mathf.Max(0, fallbackStartingCharges);
            netCharges.Value = start;
        }

        OnInstanceReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Consuma una carica. SERVER ONLY. Ritorna false se non ce ne sono.</summary>
    public bool TryConsumeCharge()
    {
        if (!IsServer) return false;
        if (netCharges.Value <= 0) return false;
        netCharges.Value -= 1;
        return true;
    }

    /// <summary>Imposta le cariche (test / futuro ricarico medbay). SERVER ONLY.</summary>
    public void ServerSetCharges(int value)
    {
        if (!IsServer) return;
        netCharges.Value = Mathf.Max(0, value);
    }

    // ── Debug GUI (Editor/Development, solo server) — standard Rev BA ──
    [Header("Debug")]
    [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development, solo server). Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugUI = false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!showDebugUI) return;
        if (!IsServer) return;

        GUILayout.BeginArea(new Rect(10, 10, 220, 90));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[DefibChargePool] Cariche: {netCharges.Value}");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("+1")) ServerSetCharges(netCharges.Value + 1);
        if (GUILayout.Button("-1")) ServerSetCharges(netCharges.Value - 1);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}
