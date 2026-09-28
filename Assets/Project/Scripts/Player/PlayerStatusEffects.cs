using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerStatusEffects — framework degli stati di alterazione per-player
/// (Rev BC / D25). SERVER-AUTHORITATIVE.
///
/// PATTERN (coerente con PlayerHealthSystem, primo NetworkBehaviour per-player):
/// - Vive sul Player prefab: UNA istanza per client connesso, con il proprio
///   OwnerClientId. Non e' un singleton.
/// - Registro statico per-clientId (activeByClientId) + LocalInstance, cosi' le
///   future sorgenti server (hazard/tempeste per le Radiazioni) e la futura UI
///   possono trovare l'istanza di un membro specifico dato il suo clientId —
///   stesso identico pattern di PlayerHealthSystem.
///
/// SCOPE DI REPLICA (Q3-a Rev BC → Rev BP-b · Q24-a): l'insieme degli stati attivi
/// (istanze, stack, timer) vive SOLO lato server. Da BP-b si replica una MASCHERA dei
/// TIPI attivi (NetworkVariable&lt;byte&gt;, un bit per StatusEffectType): basta ai
/// consumatori client reali — letto e console della Recovery Bay (quale fase curare,
/// chi può sdraiarsi) e, in futuro, le icone stato dell'HUD. Stack e durate restano
/// server-only finché una UI non li chiede. Il danno DoT è visibile cross-client
/// perché passa da PlayerHealthSystem.ApplyDamage (HP replicati).
///
/// CURA IN MEDBAY (Rev BP-b): la regola "questo stato è curabile a questo tier" è una
/// sola (CureAllowed): la usano TryCure (server, sull'istanza attiva) e
/// IsCurableAtMedbay (server e client, sul catalogo). RecoveryBed aggiunge il vincolo
/// del tier Medbay (MedbayConfig: T1 non cura stati).
///
/// RESPAWN (Rev BP-b): il clone è un corpo nuovo → ServerClearAll, chiamato da
/// PlayerHealthSystem.ServerRespawn. Con Veleno e Radiazioni persistenti (Q23-a)
/// senza pulizia il clone se li porterebbe dietro.
///
/// TICK (Q1-a Rev BC): un solo Update() gated IsServer avanza i timer di tutti gli
/// stati attivi con accumulatore per-stato. Nessuna coroutine.
///
/// INTEGRAZIONE DANNO: gli stati Damage instradano a
/// GetComponent&lt;PlayerHealthSystem&gt;().ApplyDamage(...) — sibling cachato in
/// OnNetworkSpawn (nessun GetComponent a catena runtime). ApplyDamage e' gia'
/// server-only e clampa a 0; nessuna logica di morte qui (D27).
///
/// FUORI SCOPE (non progettare prima della loro milestone):
/// - Sorgenti hazard di Radiazioni → si cablano con ZoneManager/hazard chiamando
///   ApplyEffect(StatusEffectType.Radiation) su questa istanza via registro.
/// - Morte &amp; Rianimazione (D27): applica CompoundWounds alla rianimazione (attivo).
/// - Medbay (Rev BP-b): TryCure(type, medbayTier) è chiamato da RecoveryBed al 100%
///   di una fase stato. Ferite Composte resta curabile solo con tier &gt;= 3.
///
/// ⚠️ VERIFICA EDITOR: aggiungere questo componente sullo STESSO GameObject radice
/// del Player prefab dove sta PlayerHealthSystem; assegnare i 3 asset
/// StatusEffectData nel campo "Status Catalog" (serve al debug overlay e alla
/// convenienza ApplyEffect(type)).
/// </summary>
public class PlayerStatusEffects : NetworkBehaviour
{
    [Header("Catalogo stati (assegnare i 3 asset StatusEffectData)")]
    [Tooltip("Asset SO degli stati noti. Usato per applicare per tipo (ApplyEffect(StatusEffectType)) " +
             "e dal debug overlay. Le future sorgenti possono anche passare direttamente il proprio SO.")]
    [SerializeField] private List<StatusEffectData> statusCatalog = new List<StatusEffectData>();

    // ── Runtime SOLO server (Q3-a): insieme non replicato degli stati attivi ──
    private class ActiveEffect
    {
        public StatusEffectData data;
        public float remaining;        // ignorato se data.IsPersistent
        public float tickAccumulator;
        public int stacks;             // rilevante per StackIntensity; sempre 1 per le altre policy
    }

    private readonly List<ActiveEffect> _active = new List<ActiveEffect>();

    // ── Maschera replicata dei TIPI attivi (Rev BP-b · Q24-a) — server scrive, tutti leggono ──
    private readonly NetworkVariable<byte> netActiveMask = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>
    /// Rev BP-b — maschera dei tipi attivi (bit = 1 &lt;&lt; (int)StatusEffectType).
    /// Replicata: leggibile su server e client.
    /// </summary>
    public byte ActiveMask => netActiveMask.Value;

    /// <summary>
    /// Rev BP-b — true se almeno un'istanza del tipo è attiva. Legge la maschera
    /// replicata: vale su server E client (a differenza di HasEffect, server-only).
    /// </summary>
    public bool IsActive(StatusEffectType type) => (netActiveMask.Value & MaskBit(type)) != 0;

    // ── Sibling HP cachato (server) ──
    private PlayerHealthSystem _health;

    // ── Registro statico per-clientId — stesso pattern di PlayerHealthSystem ──
    private static readonly Dictionary<ulong, PlayerStatusEffects> activeByClientId = new();

    /// <summary>Istanza del client locale (IsOwner) — scorciatoia per future UI del proprio stato.</summary>
    public static PlayerStatusEffects LocalInstance { get; private set; }

    /// <summary>
    /// Trova l'istanza del client indicato. Le future sorgenti server (hazard
    /// Radiazioni) la useranno per applicare stati a un membro specifico.
    /// </summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerStatusEffects instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    // ── Debug standard Rev BA ──
    [Header("Debug")]
    [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development Build, solo server). Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugUI = false;

    [Tooltip("Log verboso di lifecycle stati (apply/expire/tick). Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    // ── Lifecycle NGO ──────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        activeByClientId[OwnerClientId] = this;

        if (IsOwner)
            LocalInstance = this;

        if (IsServer)
        {
            netActiveMask.Value = 0;
            _health = GetComponent<PlayerHealthSystem>();
            if (_health == null)
                Debug.LogError("[PlayerStatusEffects] PlayerHealthSystem mancante sullo stesso GameObject " +
                               "del Player prefab. Gli stati Damage non potranno infliggere danno. " +
                               "Aggiungere PlayerHealthSystem sul root del Player prefab.");
        }
    }

    public override void OnNetworkDespawn()
    {
        if (activeByClientId.TryGetValue(OwnerClientId, out var registered) && registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;

        _active.Clear();
    }

    // ── Tick server (Q1-a) ───────────────────────────────────────────────────

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;
        if (_active.Count == 0) return;

        float dt = Time.deltaTime;
        bool expired = false;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            ActiveEffect e = _active[i];

            // Tick periodico (solo Damage con intervallo valido)
            if (e.data.HasPeriodicTick && e.data.effectKind == EffectKind.Damage)
            {
                e.tickAccumulator += dt;
                while (e.tickAccumulator >= e.data.tickInterval)
                {
                    e.tickAccumulator -= e.data.tickInterval;
                    float dmg = e.data.effectPerTick *
                                (e.data.stackingPolicy == StackingPolicy.StackIntensity ? e.stacks : 1);
                    if (dmg > 0f && _health != null)
                        _health.ApplyDamage(dmg);
                }
            }

            // Scadenza (gli stati persistenti non scadono)
            if (!e.data.IsPersistent)
            {
                e.remaining -= dt;
                if (e.remaining <= 0f)
                {
                    LogV($"Scaduto: {e.data.type} ({e.data.displayName})");
                    _active.RemoveAt(i);
                    expired = true;
                }
            }
        }

        if (expired) SyncMask();
    }

    // ── API server: applicazione ─────────────────────────────────────────────

    /// <summary>Applica per tipo, risolvendo lo SO dal catalogo. SERVER ONLY.</summary>
    public void ApplyEffect(StatusEffectType type)
    {
        StatusEffectData data = FindInCatalog(type);
        if (data == null)
        {
            Debug.LogWarning($"[PlayerStatusEffects] Nessun StatusEffectData per {type} nel Status Catalog. " +
                             "Assegnare l'asset nell'Inspector del Player prefab.");
            return;
        }
        ApplyEffect(data);
    }

    /// <summary>
    /// Applica uno stato secondo la sua stacking policy. SERVER ONLY.
    /// Le future sorgenti (hazard) chiamano questa via registro per-clientId.
    /// </summary>
    public void ApplyEffect(StatusEffectData data)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerStatusEffects] ApplyEffect chiamato lato client — server only. " +
                             "Le sorgenti reali (hazard, D27) devono passare dal server.");
            return;
        }
        if (data == null) return;

        switch (data.stackingPolicy)
        {
            case StackingPolicy.RefreshDuration:
                {
                    ActiveEffect existing = FindActive(data.type);
                    if (existing != null)
                    {
                        existing.remaining = data.duration;
                        LogV($"Refresh durata: {data.type}");
                    }
                    else
                    {
                        _active.Add(NewInstance(data, 1));
                        LogV($"Applicato (refresh policy): {data.type}");
                    }
                    break;
                }

            case StackingPolicy.StackIntensity:
                {
                    ActiveEffect existing = FindActive(data.type);
                    if (existing != null)
                    {
                        existing.stacks = Mathf.Min(existing.stacks + 1, Mathf.Max(1, data.maxStacks));
                        existing.remaining = data.duration; // rinnova la durata
                        LogV($"Stack intensita': {data.type} → {existing.stacks}");
                    }
                    else
                    {
                        _active.Add(NewInstance(data, 1));
                        LogV($"Applicato (intensity policy): {data.type} → 1");
                    }
                    break;
                }

            case StackingPolicy.StackIndependent:
                {
                    int count = CountActive(data.type);
                    if (count < Mathf.Max(1, data.maxStacks))
                    {
                        _active.Add(NewInstance(data, 1));
                        LogV($"Applicato (independent policy): {data.type} → istanze {count + 1}");
                    }
                    else
                    {
                        // Al cap: rinnova l'istanza con meno tempo residuo (comportamento definito, non silenzioso).
                        ActiveEffect soonest = FindSoonestExpiring(data.type);
                        if (soonest != null && !soonest.data.IsPersistent)
                        {
                            soonest.remaining = data.duration;
                            LogV($"Independent al cap ({data.maxStacks}): rinnovata l'istanza piu' vicina a scadere.");
                        }
                    }
                    break;
                }
        }

        SyncMask();
    }

    // ── API server: rimozione / cura / query ──────────────────────────────────

    /// <summary>Rimuove tutte le istanze di un tipo, senza vincoli di curabilita'. SERVER ONLY.</summary>
    public void RemoveEffect(StatusEffectType type)
    {
        if (!IsServer) return;
        int removed = _active.RemoveAll(e => e.data.type == type);
        if (removed > 0) LogV($"Rimosso: {type} (x{removed})");
        SyncMask();
    }

    /// <summary>
    /// Rev BP-b — rimuove TUTTI gli stati, senza vincoli di curabilità. SERVER ONLY.
    /// Chiamato da PlayerHealthSystem.ServerRespawn: il clone è un corpo nuovo.
    /// </summary>
    public void ServerClearAll()
    {
        if (!IsServer) return;
        if (_active.Count > 0) LogV($"Pulizia totale: {_active.Count} istanze rimosse.");
        _active.Clear();
        SyncMask();
    }

    /// <summary>
    /// Cura via medbay. SERVER ONLY. Chiamato da RecoveryBed al 100% di una fase stato
    /// (Rev BP-b). Regola unica CureAllowed: uno stato curableOnlyAtMedbayT3Plus con
    /// medbayTier &lt; 3 ⇒ false (non curato). Il vincolo "T1 non cura stati" è del
    /// letto (MedbayConfig), non di questo componente.
    /// </summary>
    public bool TryCure(StatusEffectType type, int medbayTier)
    {
        if (!IsServer) return false;

        ActiveEffect any = FindActive(type);
        if (any == null) return false;

        if (!CureAllowed(any.data, medbayTier))
        {
            LogV($"Cura negata: {type} richiede medbay T3+ (tier fornito {medbayTier}).");
            return false;
        }

        RemoveEffect(type);
        return true;
    }

    /// <summary>true se almeno un'istanza del tipo e' attiva. SERVER ONLY (vedi IsActive per i client).</summary>
    public bool HasEffect(StatusEffectType type) => IsServer && FindActive(type) != null;

    /// <summary>
    /// Rev BP-b — lo stato di questo tipo sarebbe curabile da una medbay del tier
    /// indicato? Legge il CATALOGO (serializzato sul prefab): vale su server E client.
    /// Stessa regola di TryCure. Tipo assente dal catalogo ⇒ false.
    /// </summary>
    public bool IsCurableAtMedbay(StatusEffectType type, int medbayTier)
        => CureAllowed(FindInCatalog(type), medbayTier);

    /// <summary>Regola unica di curabilità per tier (Rev BC · BP-b).</summary>
    private static bool CureAllowed(StatusEffectData data, int medbayTier)
        => data != null && (!data.curableOnlyAtMedbayT3Plus || medbayTier >= 3);

    // ── Helper interni ────────────────────────────────────────────────────────

    private static byte MaskBit(StatusEffectType type) => (byte)(1 << (int)type);

    /// <summary>Ricalcola la maschera replicata dai tipi attivi. SERVER ONLY. Scrive solo se cambia.</summary>
    private void SyncMask()
    {
        if (!IsServer) return;
        byte mask = 0;
        for (int i = 0; i < _active.Count; i++)
            mask |= MaskBit(_active[i].data.type);
        if (netActiveMask.Value != mask) netActiveMask.Value = mask;
    }

    private ActiveEffect NewInstance(StatusEffectData data, int stacks) => new ActiveEffect
    {
        data = data,
        remaining = data.duration,   // ignorato se persistente
        tickAccumulator = 0f,
        stacks = stacks
    };

    private ActiveEffect FindActive(StatusEffectType type)
    {
        for (int i = 0; i < _active.Count; i++)
            if (_active[i].data.type == type) return _active[i];
        return null;
    }

    private int CountActive(StatusEffectType type)
    {
        int c = 0;
        for (int i = 0; i < _active.Count; i++)
            if (_active[i].data.type == type) c++;
        return c;
    }

    private ActiveEffect FindSoonestExpiring(StatusEffectType type)
    {
        ActiveEffect best = null;
        for (int i = 0; i < _active.Count; i++)
        {
            if (_active[i].data.type != type) continue;
            if (best == null || _active[i].remaining < best.remaining) best = _active[i];
        }
        return best;
    }

    private StatusEffectData FindInCatalog(StatusEffectType type)
    {
        for (int i = 0; i < statusCatalog.Count; i++)
            if (statusCatalog[i] != null && statusCatalog[i].type == type) return statusCatalog[i];
        return null;
    }

    // ── Log verboso standard Rev BA ───────────────────────────────────────────
    private void LogV(string msg)
    {
        if (logVerbose) Debug.Log($"[PlayerStatusEffects/{OwnerClientId}] {msg}");
    }

    // ── Debug overlay (solo Editor/Development, solo server) ───────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!showDebugUI) return;
        if (!IsServer || !IsSpawned) return;

        // Affiancato al pannello di PlayerHealthSystem (x=280, w=280 → termina a 560),
        // stessa banda verticale per OwnerClientId.
        float y = 310 + (OwnerClientId * 90f);

        GUILayout.BeginArea(new Rect(570, y, 340, 175));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[PlayerStatusEffects] Client {OwnerClientId} — attivi: {_active.Count} · mask {netActiveMask.Value}");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Rad")) ApplyEffect(StatusEffectType.Radiation);
        if (GUILayout.Button("Veleno")) ApplyEffect(StatusEffectType.Poison);
        if (GUILayout.Button("Ferite")) ApplyEffect(StatusEffectType.CompoundWounds);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("- Rad")) RemoveEffect(StatusEffectType.Radiation);
        if (GUILayout.Button("- Vel")) RemoveEffect(StatusEffectType.Poison);
        if (GUILayout.Button("- Tutti")) ServerClearAll();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Cura Ferite T2 (deve fallire)")) TryCure(StatusEffectType.CompoundWounds, 2);
        if (GUILayout.Button("Cura Ferite T3 (ok)")) TryCure(StatusEffectType.CompoundWounds, 3);
        GUILayout.EndHorizontal();

        for (int i = 0; i < _active.Count; i++)
        {
            ActiveEffect e = _active[i];
            string dur = e.data.IsPersistent ? "persistente" : $"{e.remaining:F1}s";
            string stk = e.data.stackingPolicy == StackingPolicy.StackIntensity ? $" x{e.stacks}" : "";
            GUILayout.Label($"• {e.data.type}{stk} — {dur}");
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}