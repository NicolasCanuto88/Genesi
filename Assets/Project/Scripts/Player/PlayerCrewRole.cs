using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerCrewRole — identità di ruolo NETWORKED per-player (Rev BM · apertura Fase 3).
///
/// PROBLEMA CHE CHIUDE: fino a Rev BL il server non sapeva CHI fosse il Corpsman
/// (o l'Ingegnere, lo Scanner…): il ruolo era solo una stringa nel profilo locale.
/// Tutti i seam di ruolo restavano a identità. Questo componente rende il ruolo
/// uno stato replicato, leggibile da server e client per qualsiasi clientId.
///
/// PATTERN per-player (consolidato Rev BC/BD, identico a PlayerHealthSystem /
/// PlayerStatusEffects): vive sul ROOT del Player prefab, UNA istanza per client
/// connesso; registro statico activeByClientId + LocalInstance + TryGetByClientId.
/// NON è un singleton-nave (niente Instance/OnInstanceReady).
///
/// DICHIARAZIONE (Q2-a Rev BM): allo spawn l'OWNER legge il ruolo del personaggio
/// attivo da LocalCharacterProfile, lo converte con CrewRoles.Parse e lo dichiara
/// al server via DeclareRoleRpc. Il server:
///   - accetta la dichiarazione SOLO dal proprietario di questo oggetto
///     (SenderClientId == OwnerClientId) — nessuno può dichiarare il ruolo altrui;
///   - si FIDA del valore (gioco co-op, nessun anti-cheat — decisione di workshop);
///   - valida che sia un membro dell'enum (byte arbitrario → None).
/// Duplicati AMMESSI (crew mista 3–5 player, 5 ruoli): due Corpsman sono legittimi.
///
/// CONSUMATORI:
///   - Rev BM: PlayerReviveTarget.ResolveProfile (Corpsman → 2s / 60% HP).
///   - Futuri: clausola malus trasversale Rev U (ProgressiveMinigame.GetRole*,
///     ScannerSystem.GetRole*) — i moltiplicatori restano a identità finché non
///     vengono progettati (debito "Bonus/malus di ruolo").
///
/// LATE-JOINER: il valore è NetworkVariable → sincronizzato allo spawn. Chi entra
/// tardi vede i ruoli già assegnati; OnRoleChanged viene notificato anche per il
/// valore iniziale non-None (vedi OnNetworkSpawn).
///
/// FINESTRA DI SPAWN: tra lo spawn e l'arrivo della RPC il ruolo vale None →
/// ogni consumatore degrada al profilo di default (identità). Nessun NullRef.
///
/// ⚠️ SETUP EDITOR: aggiungere sul ROOT del Player prefab (stesso GameObject di
/// PlayerHealthSystem / PlayerStatusEffects / PlayerReviveTarget). Vedi guida Rev BM.
/// </summary>
public class PlayerCrewRole : NetworkBehaviour
{
    // ── Stato replicato (server scrive, tutti leggono) ─────────────────────────
    private readonly NetworkVariable<CrewRole> netRole = new NetworkVariable<CrewRole>(
        CrewRole.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Ruolo corrente di QUESTO player (replicato). None finché non dichiarato.</summary>
    public CrewRole Role => netRole.Value;

    // ── Registro statico per-clientId — stesso pattern di PlayerHealthSystem ──
    private static readonly Dictionary<ulong, PlayerCrewRole> activeByClientId = new();

    /// <summary>Istanza del client locale (IsOwner) — scorciatoia per UI del proprio ruolo.</summary>
    public static PlayerCrewRole LocalInstance { get; private set; }

    /// <summary>
    /// Fired su OGNI client quando il ruolo di un membro cambia: (clientId, nuovoRuolo).
    /// Notificato anche allo spawn se il valore iniziale non è None (late-joiner).
    /// </summary>
    public static event Action<ulong, CrewRole> OnRoleChanged;

    /// <summary>Trova l'istanza del client indicato.</summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerCrewRole instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    /// <summary>
    /// Ruolo del client indicato; None se il suo player non è (ancora) spawnato o
    /// se il ruolo non è stato dichiarato. Leggibile su server E client.
    /// </summary>
    public static CrewRole GetRole(ulong clientId)
    {
        if (activeByClientId.TryGetValue(clientId, out var instance) && instance != null)
            return instance.Role;
        return CrewRole.None;
    }

    /// <summary>true se il client indicato ha esattamente quel ruolo. HasRole(x, None) è sempre false.</summary>
    public static bool HasRole(ulong clientId, CrewRole role)
        => role != CrewRole.None && GetRole(clientId) == role;

    // ── Debug (standard Rev BA) ────────────────────────────────────────────────
    [Header("Debug")]
    [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development Build, solo server): mostra il ruolo " +
             "di ogni player e permette di ciclarlo. Utile in ParrelSync, dove le istanze condividono il " +
             "file profilo. Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugUI = false;

    [Tooltip("Log verbosi di dichiarazione/cambio ruolo. Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    // ── Lifecycle NGO ──────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        activeByClientId[OwnerClientId] = this;
        netRole.OnValueChanged += HandleRoleChanged;

        if (IsOwner)
        {
            LocalInstance = this;
            DeclareLocalRole();
        }

        // Late-joiner: il valore è già sincronizzato ma OnValueChanged non scatta
        // per il valore iniziale → notifica esplicita se c'è già un ruolo.
        if (netRole.Value != CrewRole.None)
            OnRoleChanged?.Invoke(OwnerClientId, netRole.Value);
    }

    public override void OnNetworkDespawn()
    {
        netRole.OnValueChanged -= HandleRoleChanged;

        if (activeByClientId.TryGetValue(OwnerClientId, out var registered) && registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;
    }

    private void HandleRoleChanged(CrewRole previous, CrewRole current)
    {
        LogV($"Ruolo {previous} → {current}");
        OnRoleChanged?.Invoke(OwnerClientId, current);
    }

    // ── Dichiarazione (owner → server) ─────────────────────────────────────────

    /// <summary>
    /// OWNER: legge il ruolo del personaggio attivo e lo dichiara al server.
    /// Se il profilo manca (scena Game avviata senza passare dal MainMenu) dichiara
    /// None e lo segnala: il ruolo si può assegnare dall'overlay di debug.
    /// </summary>
    private void DeclareLocalRole()
    {
        LocalCharacterProfile profile = LocalCharacterProfile.Instance;
        string raw = profile != null ? profile.Role : null;
        CrewRole declared = CrewRoles.Parse(raw);

        if (profile == null)
        {
            Debug.LogWarning("[PlayerCrewRole] LocalCharacterProfile assente (scena Game avviata senza " +
                             "MainMenu?). Ruolo dichiarato: None. Assegnalo dall'overlay di debug (showDebugUI).");
        }
        else if (declared == CrewRole.None)
        {
            LogV($"Ruolo del profilo non riconosciuto (\"{raw}\") → None.");
        }

        DeclareRoleRpc(declared);
    }

    [Rpc(SendTo.Server)]
    private void DeclareRoleRpc(CrewRole role, RpcParams rpcParams = default)
    {
        // Solo il proprietario di questo player può dichiararne il ruolo.
        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != OwnerClientId)
        {
            Debug.LogWarning($"[PlayerCrewRole] Dichiarazione rifiutata: client {sender} ha tentato di " +
                             $"impostare il ruolo del player di client {OwnerClientId}.");
            return;
        }

        ServerSetRole(role);
    }

    // ── API server ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Imposta il ruolo. SERVER ONLY. Usato dalla dichiarazione dell'owner e dal
    /// debug; seam pronto per un'eventuale assegnazione host in lobby (Q2-b, non ora).
    /// Un valore fuori enum diventa None.
    /// </summary>
    public void ServerSetRole(CrewRole role)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerCrewRole] ServerSetRole chiamato lato client — server only.");
            return;
        }

        if (!CrewRoles.IsValid(role)) role = CrewRole.None;
        if (netRole.Value == role) return;

        netRole.Value = role;
    }

    // ── Log verboso standard Rev BA ────────────────────────────────────────────
    private void LogV(string msg)
    {
        if (logVerbose) Debug.Log($"[PlayerCrewRole/{OwnerClientId}] {msg}");
    }

    // ── Debug overlay (solo Editor/Development, solo server) ────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!showDebugUI) return;
        if (!IsServer || !IsSpawned) return;

        // Affiancato ai pannelli PlayerHealthSystem (x=280) e PlayerStatusEffects
        // (x=570, w=340 → termina a 910); banda verticale per OwnerClientId.
        float y = 310 + (OwnerClientId * 120f);

        GUILayout.BeginArea(new Rect(920, y, 240, 64));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[Ruolo] Client {OwnerClientId}: {CrewRoles.ToDisplayName(netRole.Value)}");
        if (GUILayout.Button("Ciclo ruolo ▶"))
        {
            // None → Pilot → … → Quartermaster → None
            byte next = (byte)(((byte)netRole.Value + 1) % ((byte)CrewRole.Quartermaster + 1));
            ServerSetRole((CrewRole)next);
        }
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}
