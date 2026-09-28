using System;
using SpaceSurvivor.Ship;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// DefibChargePool — pool di cariche del defibrillatore (Morte &amp; Rianimazione, D27).
///
/// Rev BP-a (Q26-a): le cariche sono un pool di livello MEDBAY. Il server riempie il
/// pool dal tier del modulo (MedbaySystem) con la tabella di DefibConfig
/// (T1=0 / T2=3 / T3=4 / T4=5):
///   - allo spawn (tier corrente, oppure T1 se MedbaySystem non è ancora spawnato);
///   - su MedbaySystem.OnInstanceReady (MedbaySystem spawnato dopo questo pool);
///   - su MedbaySystem.OnTierChanged (upgrade o overlay di debug): il pool torna PIENO
///     al valore del nuovo tier.
/// Nel design le cariche sono PER MISSIONE e si ricaricano in medbay tra le missioni.
/// Il ciclo missione non esiste ancora → la ricarica avviene solo nei tre casi sopra
/// (debito tracciato nel GDD).
///
/// REGOLA DI DISPONIBILITÀ (una sola, BP-a): IsDefibAvailable = ci sono cariche E c'è
/// almeno un Corpsman in crew (connesso, in qualsiasi stato vitale: un Corpsman a
/// terra può essere rianimato da un compagno). Senza Corpsman nessuno ha il
/// defibrillatore: i caduti vanno a timer → clone (GDD ruoli M3 close). La usano sia
/// il client (prompt, PlayerReviveTarget.CanInteract) sia il server (ReviveServerRpc).
///
/// AUTORITÀ: solo il server scrive le cariche (NetworkVariable letta da tutti, così
/// il client può nascondere il prompt). Il server è l'unico a consumarle.
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
    [Tooltip("SO di tuning defib. Fornisce la tabella cariche per tier Medbay (Rev BP-a). Se null si usa " +
             "'fallbackStartingCharges', senza legame col tier.")]
    [SerializeField] private DefibConfig config;

    [Tooltip("Cariche usate SOLO se 'config' non è assegnato (modalità degradata, errore a log).")]
    [SerializeField] private int fallbackStartingCharges = 3;

    private readonly NetworkVariable<int> netCharges = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public int Charges => netCharges.Value;
    public bool HasCharge => netCharges.Value > 0;

    /// <summary>
    /// Rev BP-a — true se c'è almeno un Corpsman connesso in crew, in qualsiasi stato
    /// vitale. Leggibile su server e client (ruoli replicati).
    /// </summary>
    public static bool CorpsmanInCrew => PlayerCrewRole.AnyWithRole(CrewRole.Corpsman);

    /// <summary>
    /// Rev BP-a — regola unica di disponibilità del defib: cariche &gt; 0 E Corpsman in
    /// crew. Il client la usa per il prompt, il server la ricontrolla nella RPC.
    /// </summary>
    public bool IsDefibAvailable => HasCharge && CorpsmanInCrew;

    private bool _loggedMissingConfig;

    public override void OnNetworkSpawn()
    {
        Instance = this;

        if (IsServer)
        {
            MedbaySystem.OnTierChanged += HandleMedbayTierChanged;
            MedbaySystem.OnInstanceReady += HandleMedbayReady;
            ServerRefillFromTier("spawn");
        }

        OnInstanceReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        MedbaySystem.OnTierChanged -= HandleMedbayTierChanged;
        MedbaySystem.OnInstanceReady -= HandleMedbayReady;

        if (Instance == this) Instance = null;
    }

    // ── Ricarica dal tier (SERVER) ───────────────────────────────────────────

    private void HandleMedbayReady() => ServerRefillFromTier("MedbaySystem pronto");

    private void HandleMedbayTierChanged(int tier) => ServerRefillFromTier($"tier → T{tier}");

    /// <summary>
    /// Riporta il pool al valore del tier Medbay corrente. SERVER ONLY. Idempotente:
    /// più chiamate con lo stesso tier danno lo stesso valore.
    /// </summary>
    private void ServerRefillFromTier(string reason)
    {
        if (!IsServer || !IsSpawned) return;

        int tier = MedbaySystem.CurrentTierOrDefault;
        int charges;
        if (config != null)
        {
            charges = config.ChargesForMedbayTier(tier);
        }
        else
        {
            if (!_loggedMissingConfig)
            {
                Debug.LogError("[DefibChargePool] DefibConfig non assegnato: cariche di ripiego " +
                               $"({fallbackStartingCharges}) senza legame col tier Medbay.");
                _loggedMissingConfig = true;
            }
            charges = Mathf.Max(0, fallbackStartingCharges);
        }

        netCharges.Value = charges;
        LogV($"[DefibChargePool] Ricarica ({reason}): T{tier} → {charges} cariche.");
    }

    /// <summary>Consuma una carica. SERVER ONLY. Ritorna false se non ce ne sono.</summary>
    public bool TryConsumeCharge()
    {
        if (!IsServer) return false;
        if (netCharges.Value <= 0) return false;
        netCharges.Value -= 1;
        return true;
    }

    /// <summary>Imposta le cariche (test). SERVER ONLY.</summary>
    public void ServerSetCharges(int value)
    {
        if (!IsServer) return;
        netCharges.Value = Mathf.Max(0, value);
    }

    // ── Debug (Editor/Development, solo server) — standard Rev BA ──
    [Header("Debug")]
    [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development, solo server). Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugUI = false;

    [Tooltip("Log verbosi delle ricariche dal tier. Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    private void LogV(string msg) { if (logVerbose) Debug.Log(msg); }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!showDebugUI) return;
        if (!IsServer) return;

        GUILayout.BeginArea(new Rect(10, 10, 260, 110));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[DefibChargePool] Cariche: {netCharges.Value} · Medbay T{MedbaySystem.CurrentTierOrDefault}");
        GUILayout.Label($"Corpsman in crew: {(CorpsmanInCrew ? "sì" : "NO → defib spento")}");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("+1")) ServerSetCharges(netCharges.Value + 1);
        if (GUILayout.Button("-1")) ServerSetCharges(netCharges.Value - 1);
        if (GUILayout.Button("Ricarica")) ServerRefillFromTier("overlay");
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}