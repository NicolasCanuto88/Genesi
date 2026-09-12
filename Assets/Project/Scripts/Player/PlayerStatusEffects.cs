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
/// SCOPE DI REPLICA (Q3-a Rev BC): l'insieme degli stati attivi vive SOLO lato
/// server (List non replicata). Il danno degli stati DoT e' comunque visibile
/// cross-client perche' passa da PlayerHealthSystem.ApplyDamage, il cui HP e' gia'
/// un NetworkVariable replicato. La replica dell'insieme stati (icone HUD) NON si
/// costruisce ora: arriva con Corpsman/Downed/HUD stati, per non congelare al buio
/// uno schema INetworkSerializable prima che esista una UI reale.
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
/// - Medbay T3+ (cura Ferite Composte) → solo hook: TryCure(type, medbayTier)
///   ritorna false per gli stati curableOnlyAtMedbayT3Plus finche' nessuno chiama
///   con tier >= 3 (nessuna medbay esiste).
/// - Morte &amp; Rianimazione (D27) → applichera' CompoundWounds via ApplyEffect alla
///   rianimazione; qui lo stato e' gia' pronto.
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
                }
            }
        }
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
    }

    // ── API server: rimozione / cura / query ──────────────────────────────────

    /// <summary>Rimuove tutte le istanze di un tipo, senza vincoli di curabilita'. SERVER ONLY.</summary>
    public void RemoveEffect(StatusEffectType type)
    {
        if (!IsServer) return;
        int removed = _active.RemoveAll(e => e.data.type == type);
        if (removed > 0) LogV($"Rimosso: {type} (x{removed})");
    }

    /// <summary>
    /// Tentativo di cura via medbay (hook placeholder Rev BC). SERVER ONLY.
    /// Se lo stato e' curableOnlyAtMedbayT3Plus e medbayTier &lt; 3 ⇒ false (non curato).
    /// Nessuna medbay esiste ancora: in pratica ritorna false per Ferite Composte
    /// finche' la Fase medbay non chiamera' con tier &gt;= 3.
    /// </summary>
    public bool TryCure(StatusEffectType type, int medbayTier)
    {
        if (!IsServer) return false;

        ActiveEffect any = FindActive(type);
        if (any == null) return false;

        if (any.data.curableOnlyAtMedbayT3Plus && medbayTier < 3)
        {
            LogV($"Cura negata: {type} richiede medbay T3+ (tier fornito {medbayTier}).");
            return false;
        }

        RemoveEffect(type);
        return true;
    }

    /// <summary>true se almeno un'istanza del tipo e' attiva. SERVER ONLY (stato non replicato).</summary>
    public bool HasEffect(StatusEffectType type) => IsServer && FindActive(type) != null;

    // ── Helper interni ────────────────────────────────────────────────────────

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

        GUILayout.BeginArea(new Rect(570, y, 340, 150));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[PlayerStatusEffects] Client {OwnerClientId} — attivi: {_active.Count}");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Rad")) ApplyEffect(StatusEffectType.Radiation);
        if (GUILayout.Button("Veleno")) ApplyEffect(StatusEffectType.Poison);
        if (GUILayout.Button("Ferite")) ApplyEffect(StatusEffectType.CompoundWounds);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("- Rad")) RemoveEffect(StatusEffectType.Radiation);
        if (GUILayout.Button("- Vel")) RemoveEffect(StatusEffectType.Poison);
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
