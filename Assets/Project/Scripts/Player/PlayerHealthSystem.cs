using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerHealthSystem — Milestone 2 (stub). Ultimo item rimasto di M2, vedi
/// SESSION_HANDOFF Rev N §TODO.
///
/// DIFFERENZA ARCHITETTURALE rispetto a tutti i sistemi NetworkBehaviour
/// esistenti (EconomyManager, OxygenSystem, PowerManager, HullSystem, ecc.):
/// quelli sono SINGLETON — una sola istanza in scena, condivisa da tutti i
/// client (pattern Instance + OnInstanceReady). Questo invece vive sul Player
/// prefab: esiste UNA ISTANZA PER OGNI CLIENT CONNESSO, ciascuna con il
/// proprio OwnerClientId e la propria NetworkVariable HP. È il primo sistema
/// "per-player" del progetto — non esiste (e non avrebbe senso) un singleton
/// Instance qui: "quale" istanza sarebbe "la" istanza?
///
/// HP — NetworkVariable&lt;float&gt;, server-authority: stesso pattern di
/// EconomyManager.netFleetCredits (server scrive, tutti leggono), ma con
/// scope per-istanza invece che per-sessione/lobby.
///
/// REGISTRO STATICO (activeByClientId) — permette a qualsiasi UI di trovare
/// l'istanza HP di un membro specifico dell'equipaggio dato il suo clientId,
/// lo stesso identificativo già usato da NetworkManager.ConnectedClientsIds
/// in ShipTabUI/CrewCreditEntry. Usato da MedicalDashboardUI (Sezione A) per
/// mostrare l'HP di OGNI membro equipaggio, non solo il proprio.
///
/// LocalInstance / evento statico OnLocalHealthChanged — scorciatoia per il
/// client locale (IsOwner), così chi mostra solo il PROPRIO HP (ProfileTabUI,
/// Tab Profilo del Tablet) non deve cercare nel registro il proprio clientId.
/// Stesso identico pattern già in uso per
/// LocalCharacterProfile.OnPersonalCreditsChanged.
///
/// ApplyDamage(float) — SERVER ONLY. Nessuna sorgente di danno reale esiste
/// ancora: dipende da Combattimento FPS (GDD §5) e Sistema Abbordaggio
/// (GDD §6), entrambi "da completare" (M3). In M2 è richiamabile solo dai
/// pulsanti di debug (OnGUI) qui sotto, per testare il collegamento a
/// CrewHPEntry/ProfileTabUI senza dover aspettare il combattimento reale —
/// stesso identico pattern già adottato da EconomyManager per il Fleet
/// Account prima che Missioni/Vendite esistessero.
///
/// NESSUNA morte/respawn qui: a HP 0 il valore resta clampato a 0 senza altre
/// conseguenze. GDD §7 (Morte &amp; Respawn) è esplicitamente Milestone 3 —
/// quando quella logica esisterà, andrà rivista anche la reinizializzazione
/// HP in OnNetworkSpawn qui sotto (oggi assume "ogni spawn = HP piena", che
/// in M2 è corretto perché non esiste ancora un ciclo morte→respawn sullo
/// stesso NetworkObject).
///
/// ⚠️ Dipende da:
///   - PlayerIdentity (M3) per nomi reali nelle UI — qui si espone solo
///     OwnerClientId, stessa identica limitazione già accettata in
///     CrewCreditEntry/ShipTabUI ("Player [clientId]").
///   - Combattimento FPS / Sistema Abbordaggio (M3) come fonte reale di danno.
///
/// ⚠️ VERIFICA EDITOR RICHIESTA (non eseguibile da codice) — IMPORTANTE:
///   Questo è il PRIMO NetworkBehaviour mai aggiunto al Player prefab in
///   questo progetto. Finora ogni NetworkBehaviour (EconomyManager,
///   OxygenSystem, PowerManager, ecc.) era un singleton piazzato a mano in
///   scena, indipendente dal Player. Il Player invece va spawnato
///   automaticamente da NGO per ogni client connesso, tramite il campo
///   "Player Prefab" di NetworkManager (meccanismo standard NGO — non
///   compare in nessun file .cs di questo progetto, è puro setup Editor).
///   Prima di testare, verificare in Editor:
///     1. Il Player prefab ha già un componente NetworkObject? Se non ce
///        l'ha (probabile: PlayerController.cs non lo richiede via
///        [RequireComponent] ed è un MonoBehaviour puro), va aggiunto ora
///        sullo stesso GameObject radice.
///     2. NetworkManager → Player Prefab punta a QUESTO prefab (quello con
///        PlayerController + PlayerInput + TabletStation).
///     3. Aggiungere questo componente (PlayerHealthSystem) sullo stesso
///        GameObject radice del Player prefab.
///   Se il Player non era già configurato come Player Prefab di NGO prima di
///   questa sessione, questo è il primo task che lo richiede esplicitamente —
///   finora il progetto funzionava anche senza, perché tutti i sistemi
///   precedenti erano scene-singleton indipendenti dal Player.
/// </summary>
public class PlayerHealthSystem : NetworkBehaviour
{
    [Header("Configurazione (stub — il GDD non specifica ancora un valore ufficiale)")]
    [Tooltip("HP massimi del giocatore. Il GDD (§7, §9.4) definisce solo soglie percentuali " +
             "(40% = FERITO, 20% = CRITICO) ma nessun valore assoluto: 100 è coerente con i " +
             "valori stub già usati da MedicalDashboardUI (100f/100f) e CrewHPEntry. Da " +
             "confermare in design quando esisterà danno reale (Combattimento FPS, M3).")]
    [SerializeField] private float maxHP = 100f;

    // ── NetworkVariable (server scrive, tutti leggono) — pattern identico a EconomyManager ──
    private readonly NetworkVariable<float> netCurrentHP = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public float CurrentHP => netCurrentHP.Value;
    public float MaxHP => maxHP;
    public float HealthPercent => maxHP > 0f ? netCurrentHP.Value / maxHP : 0f;

    /// <summary>Nessuna logica di morte collegata: solo un comodo getter per UI/future use — M3.</summary>
    public bool IsAlive => netCurrentHP.Value > 0f;

    // ── Registro statico per-clientId — equivalente "per-player" del pattern Instance ──
    private static readonly Dictionary<ulong, PlayerHealthSystem> activeByClientId = new();

    /// <summary>Istanza del client locale (IsOwner) — scorciatoia per ProfileTabUI (Tab Profilo).</summary>
    public static PlayerHealthSystem LocalInstance { get; private set; }

    /// <summary>Fired SOLO per l'istanza locale quando il proprio HP cambia: (current, max).</summary>
    public static event Action<float, float> OnLocalHealthChanged;

    // ── Lifecycle NGO ──────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // Nessun ciclo morte/respawn esiste ancora (M3) — ogni spawn parte a HP piena.
            // Quando esisterà la logica di respawn (GDD §7) andrà rivisto: bisognerà
            // distinguere "primo spawn" da "respawn dopo morte" per non sovrascrivere
            // eventuale stato persistito.
            netCurrentHP.Value = maxHP;
        }

        activeByClientId[OwnerClientId] = this;
        netCurrentHP.OnValueChanged += HandleHPChanged;

        if (IsOwner)
        {
            LocalInstance = this;
            OnLocalHealthChanged?.Invoke(netCurrentHP.Value, maxHP);
        }
    }

    public override void OnNetworkDespawn()
    {
        netCurrentHP.OnValueChanged -= HandleHPChanged;

        if (activeByClientId.TryGetValue(OwnerClientId, out var registered) && registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;
    }

    private void HandleHPChanged(float previous, float current)
    {
        if (IsOwner)
            OnLocalHealthChanged?.Invoke(current, maxHP);
    }

    // ── Lookup per UI — usato da MedicalDashboardUI per ogni membro equipaggio ──

    /// <summary>
    /// Cerca l'istanza HP del client con il clientId indicato. Usato da
    /// MedicalDashboardUI (Sezione A) per ogni riga della lista equipaggio —
    /// stesso clientId già enumerato da NetworkManager.ConnectedClientsIds.
    /// </summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerHealthSystem instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    // ── API pubblica — danno ──────────────────────────────────────────────

    /// <summary>
    /// Applica danno a questo giocatore. SERVER ONLY.
    /// Nessuna sorgente di danno reale esiste ancora — dipende da:
    /// Combattimento FPS (GDD §5) e Sistema Abbordaggio (GDD §6), entrambi
    /// "da completare" (M3). In M2 richiamabile solo dai pulsanti di debug
    /// (OnGUI sotto) per testare il collegamento a CrewHPEntry/ProfileTabUI
    /// senza aspettare il combattimento reale.
    ///
    /// Nessuna morte: HP clampato a 0, nessun evento di morte/respawn (M3).
    /// </summary>
    public void ApplyDamage(float amount)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerHealthSystem] ApplyDamage chiamato lato client — " +
                              "deve essere invocato solo lato server. Quando esisterà una fonte " +
                              "di danno reale (M3), quel sistema dovrà inviare una Rpc al server " +
                              "che poi chiama questo metodo — mai direttamente dal client.");
            return;
        }

        if (amount <= 0f) return;

        netCurrentHP.Value = Mathf.Max(0f, netCurrentHP.Value - amount);
    }

    // ── Debug GUI (solo per testare HP prima che esista una fonte di danno reale) ──
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!IsServer) return;

        // Offset verticale per OwnerClientId, per non sovrapporre i pannelli
        // di più client testati in locale (es. ParrelSync).
        float y = 310 + (OwnerClientId * 90f);

        GUILayout.BeginArea(new Rect(280, y, 280, 100));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[PlayerHealthSystem] Client {OwnerClientId}: {netCurrentHP.Value:F0}/{maxHP:F0} HP");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-10 (test danno)")) ApplyDamage(10f);
        if (GUILayout.Button("+10 (test cura)")) netCurrentHP.Value = Mathf.Min(maxHP, netCurrentHP.Value + 10f);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}
