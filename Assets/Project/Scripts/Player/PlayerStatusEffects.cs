using System;
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
/// TIPI attivi (NetworkVariable&lt;ushort&gt; da Rev BR, un bit per StatusEffectType): basta ai
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
/// MODIFICATORI DI STATISTICA (Rev BR · Q38-a … Q44-a): gli stati StatModifier (droghe:
/// Hazmat, Combat Stim) cambiano statistiche del giocatore finché sono attivi.
/// - CATEGORIE: Condition (da curare, la vede la Recovery Bay tramite ConditionMask) e
///   Buff (effetto voluto: invisibile alla Bay, non si cura, scade da solo).
/// - CALCOLO SU OGNI CLIENT (Q44-a): GetStatMultiplier legge la maschera replicata e il
///   catalogo serializzato sul prefab (lo stesso ovunque). Nessuna NetworkVariable in più;
///   per questo gli stati StatModifier sono sempre RefreshDuration (un bit = un effetto).
/// - COMBINAZIONE (Q40-a): per statistica le percentuali si sommano; moltiplicatore
///   1 + somma/100, limitato dallo SO StatModifierLimits. Risultato in cache per maschera.
/// - DOVE SI APPLICANO: velocità → PlayerController (client proprietario); danno subito →
///   PlayerHealthSystem.ApplyDamage (server); danno da Radiazioni → tick qui sotto (server);
///   HP max → PlayerHealthSystem.MaxHP (tutti). Quando la maschera cambia il server taglia
///   gli HP correnti al nuovo massimo (ServerClampHPToMax): se il massimo risale, gli HP
///   tagliati non tornano (Q40-a).
/// - DURATA PER RUOLO (Q43-a): ApplyEffect accetta un fattore di durata (non-Corpsman 60%
///   nella Combat Stim, BR-b). Riapplicare rinnova senza accorciare.
/// - DURATA IN SECONDI (Rev BV-b): ApplyEffectForSeconds applica uno stato per i secondi indicati
///   (scudi del Quartermaster, durata dal tier).
///
/// STATISTICHE DI RUOLO (Rev BV-a · Q93-a): alle percentuali degli stati si sommano quelle del
/// RUOLO del giocatore, lette dallo SO RoleStatConfig (Quartermaster: HP max +50%, velocità −15%),
/// prima dei limiti di StatModifierLimits. Il ruolo arriva dal PlayerCrewRole replicato
/// (evento OnRoleChanged), quindi server e client calcolano lo stesso moltiplicatore.
/// - Al cambio di ruolo la cache si ricalcola e scatta OnStatModifiersChanged (UI dell'owner).
/// - Sul server gli HP correnti seguono il nuovo massimo mantenendo la frazione
///   (PlayerHealthSystem.ServerRescaleHPToNewMax): il Quartermaster nasce a 100/100 con ruolo
///   None e passa a 150/150 quando la sua dichiarazione arriva.
/// - Ordine dei componenti: PlayerCrewRole sta dopo questo componente sul Player prefab, quindi la
///   sottoscrizione all'evento esiste già quando il ruolo viene dichiarato; allo spawn il ruolo già
///   noto viene comunque letto (late join, ordine diverso).
///
/// ⚠️ VERIFICA EDITOR: aggiungere questo componente sullo STESSO GameObject radice
/// del Player prefab dove sta PlayerHealthSystem; assegnare TUTTI gli asset
/// StatusEffectData nel campo "Status Catalog" (da Rev BR: 3 condizioni + SED_Hazmat e
/// SED_CombatStim — obbligatorio per i buff, i client ne leggono categoria e modificatori),
/// l'asset StatModifierLimits nel campo "Limits" e (Rev BV-a) l'asset RoleStatConfig nel campo
/// "Role Stats".
/// </summary>
public class PlayerStatusEffects : NetworkBehaviour
{
    [Header("Catalogo stati (assegnare TUTTI gli asset StatusEffectData)")]
    [Tooltip("Asset SO degli stati noti. Usato per applicare per tipo (ApplyEffect(StatusEffectType)) " +
             "e dal debug overlay. Le future sorgenti possono anche passare direttamente il proprio SO.")]
    [SerializeField] private List<StatusEffectData> statusCatalog = new List<StatusEffectData>();

    [Header("Modificatori di statistica (Rev BR)")]
    [Tooltip("Asset StatModifierLimits: intervallo ammesso per ogni moltiplicatore. " +
             "Senza asset si limitano solo i valori impossibili (errore a spawn).")]
    [SerializeField] private StatModifierLimits limits;

    [Tooltip("Rev BV-a (Q93-a) — asset RoleStatConfig: modificatori di statistica per ruolo (Quartermaster HP max " +
             "+50%, velocità −15%). Senza asset nessun ruolo modifica le statistiche (errore a spawn sul server).")]
    [SerializeField] private RoleStatConfig roleStats;

    /// <summary>Numero massimo di StatusEffectType rappresentabili nella maschera (ushort).</summary>
    private const int MaxMaskTypes = 16;

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
    // Rev BR: ushort (16 tipi) al posto di byte (8): condizioni + buff.
    private readonly NetworkVariable<ushort> netActiveMask = new NetworkVariable<ushort>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>
    /// Rev BP-b — maschera dei tipi attivi (bit = 1 &lt;&lt; (int)StatusEffectType), condizioni
    /// E buff. Replicata: leggibile su server e client.
    /// </summary>
    public ushort ActiveMask => netActiveMask.Value;

    /// <summary>
    /// Rev BR — maschera delle sole CONDIZIONI attive (buff esclusi). È quello che conta per
    /// la Recovery Bay ("c'è qualcosa da curare?"). Un tipo assente dal catalogo conta come
    /// condizione (comportamento prudente, identico a prima di BR).
    /// </summary>
    public ushort ConditionMask => (ushort)(netActiveMask.Value & ~BuffBits);

    /// <summary>
    /// Rev BR — fired su server e client quando la maschera replicata cambia (stato applicato,
    /// scaduto, curato). Lo usa PlayerHealthSystem sull'owner per aggiornare HP max nella UI.
    /// </summary>
    public event Action OnActiveMaskChanged;

    /// <summary>
    /// Rev BV-a — fired su server e client quando cambiano i modificatori di statistica: la
    /// maschera replicata (stati) oppure il ruolo del giocatore (Q93-a). Lo usa PlayerHealthSystem
    /// sull'owner per aggiornare l'HP max nella UI.
    /// </summary>
    public event Action OnStatModifiersChanged;

    /// <summary>Rev BV-a — ruolo usato per i modificatori di statistica (dal PlayerCrewRole replicato).</summary>
    public CrewRole StatRole => _statRole;

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

        netActiveMask.OnValueChanged += HandleMaskChanged;
        PlayerCrewRole.OnRoleChanged += HandleRoleChanged;   // Rev BV-a
        _statRole = CrewRole.None;
        _statCacheValid = false;

        if (IsServer)
        {
            netActiveMask.Value = 0;

            if (limits == null)
                Debug.LogError("[PlayerStatusEffects] StatModifierLimits non assegnato sul Player prefab (campo \"Limits\"). " +
                               "I moltiplicatori verranno limitati solo ai valori impossibili. Assegnare l'asset StatModifierLimits.");

            if (roleStats == null)
                Debug.LogError("[PlayerStatusEffects] RoleStatConfig non assegnato sul Player prefab " +
                               "(campo \"Role Stats\"). Nessun ruolo modificherà le statistiche. " +
                               "Assegnare l'asset RoleStatConfig.");

            ValidateEnumFitsMask();
            _health = GetComponent<PlayerHealthSystem>();
            if (_health == null)
                Debug.LogError("[PlayerStatusEffects] PlayerHealthSystem mancante sullo stesso GameObject " +
                               "del Player prefab. Gli stati Damage non potranno infliggere danno. " +
                               "Aggiungere PlayerHealthSystem sul root del Player prefab.");
        }

        // Rev BV-a: ruolo già noto allo spawn (late join, o PlayerCrewRole spawnato prima).
        ApplyStatRole(PlayerCrewRole.GetRole(OwnerClientId));
    }

    public override void OnNetworkDespawn()
    {
        netActiveMask.OnValueChanged -= HandleMaskChanged;
        PlayerCrewRole.OnRoleChanged -= HandleRoleChanged;   // Rev BV-a

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
                                (e.data.EffectivePolicy == StackingPolicy.StackIntensity ? e.stacks : 1);
                    dmg *= TypedDamageMultiplier(e.data.type);   // Rev BR: Hazmat sulle Radiazioni
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

    /// <summary>
    /// Applica per tipo, risolvendo lo SO dal catalogo. SERVER ONLY.
    /// durationScale (Rev BR · Q43-a): moltiplica la durata degli stati non persistenti
    /// (Combat Stim iniettata da un non-Corpsman: 0.6). Ignorato per gli stati persistenti.
    /// </summary>
    public void ApplyEffect(StatusEffectType type, float durationScale = 1f)
    {
        StatusEffectData data = FindInCatalog(type);
        if (data == null)
        {
            Debug.LogWarning($"[PlayerStatusEffects] Nessun StatusEffectData per {type} nel Status Catalog. " +
                             "Assegnare l'asset nell'Inspector del Player prefab.");
            return;
        }
        ApplyEffect(data, durationScale);
    }

    /// <summary>
    /// Applica uno stato secondo la sua stacking policy effettiva. SERVER ONLY.
    /// Le future sorgenti (hazard) chiamano questa via registro per-clientId.
    /// Rev BR: durationScale come sopra; riapplicare uno stato RefreshDuration rinnova la
    /// durata SENZA accorciarla (vale il residuo più lungo).
    /// </summary>
    public void ApplyEffect(StatusEffectData data, float durationScale = 1f)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerStatusEffects] ApplyEffect chiamato lato client — server only. " +
                             "Le sorgenti reali (hazard, D27) devono passare dal server.");
            return;
        }
        if (data == null) return;

        // Rev BR: buff e stati con modificatori devono stare nel catalogo, altrimenti i client
        // non ne conoscono categoria e modificatori (la maschera porta solo il tipo).
        if ((data.category == StatusCategory.Buff || data.HasStatModifiers) && FindInCatalog(data.type) != data)
            Debug.LogError($"[PlayerStatusEffects] {data.name} ({data.type}) non è nel Status Catalog del Player prefab: " +
                           "i client non ne vedranno categoria e modificatori. Aggiungere l'asset al catalogo.");

        float duration = data.IsPersistent ? data.duration : data.duration * Mathf.Max(0.01f, durationScale);

        switch (data.EffectivePolicy)
        {
            case StackingPolicy.RefreshDuration:
                {
                    ActiveEffect existing = FindActive(data.type);
                    if (existing != null)
                    {
                        existing.remaining = Mathf.Max(existing.remaining, duration);   // Rev BR: mai accorciare
                        LogV($"Refresh durata: {data.type} → {existing.remaining:F1}s");
                    }
                    else
                    {
                        _active.Add(NewInstance(data, 1, duration));
                        LogV($"Applicato (refresh policy): {data.type} · {duration:F1}s");
                    }
                    break;
                }

            case StackingPolicy.StackIntensity:
                {
                    ActiveEffect existing = FindActive(data.type);
                    if (existing != null)
                    {
                        existing.stacks = Mathf.Min(existing.stacks + 1, Mathf.Max(1, data.maxStacks));
                        existing.remaining = duration; // rinnova la durata
                        LogV($"Stack intensita': {data.type} → {existing.stacks}");
                    }
                    else
                    {
                        _active.Add(NewInstance(data, 1, duration));
                        LogV($"Applicato (intensity policy): {data.type} → 1");
                    }
                    break;
                }

            case StackingPolicy.StackIndependent:
                {
                    int count = CountActive(data.type);
                    if (count < Mathf.Max(1, data.maxStacks))
                    {
                        _active.Add(NewInstance(data, 1, duration));
                        LogV($"Applicato (independent policy): {data.type} → istanze {count + 1}");
                    }
                    else
                    {
                        // Al cap: rinnova l'istanza con meno tempo residuo (comportamento definito, non silenzioso).
                        ActiveEffect soonest = FindSoonestExpiring(data.type);
                        if (soonest != null && !soonest.data.IsPersistent)
                        {
                            soonest.remaining = duration;
                            LogV($"Independent al cap ({data.maxStacks}): rinnovata l'istanza piu' vicina a scadere.");
                        }
                    }
                    break;
                }
        }

        SyncMask();
    }

    /// <summary>
    /// Rev BV-b (Q101-a) — applica uno stato a tempo per i secondi indicati: gli scudi del
    /// Quartermaster hanno la durata del tier, non quella dello SO. SERVER ONLY. I secondi diventano
    /// il fattore di durata di ApplyEffect (secondi / durata dello SO), quindi valgono le stesse
    /// regole: riapplicare rinnova senza accorciare. Per uno stato persistente i secondi sono ignorati.
    /// </summary>
    public void ApplyEffectForSeconds(StatusEffectType type, float seconds)
    {
        StatusEffectData data = FindInCatalog(type);
        if (data == null)
        {
            Debug.LogWarning($"[PlayerStatusEffects] Nessun StatusEffectData per {type} nel Status Catalog. " +
                             "Assegnare l'asset nell'Inspector del Player prefab.");
            return;
        }

        float scale = data.IsPersistent ? 1f : Mathf.Max(0.01f, seconds) / data.duration;
        ApplyEffect(data, scale);
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

    /// <summary>
    /// Regola unica di curabilità per tier (Rev BC · BP-b). Rev BR: solo le Condition si
    /// curano; i Buff scadono da soli.
    /// </summary>
    private static bool CureAllowed(StatusEffectData data, int medbayTier)
        => data != null
           && data.category == StatusCategory.Condition
           && (!data.curableOnlyAtMedbayT3Plus || medbayTier >= 3);

    // ── API modificatori (Rev BR) — server e client ────────────────────────────

    /// <summary>
    /// Rev BR — moltiplicatore corrente della statistica (1 = nessuna modifica). Vale su server
    /// E client: somma le percentuali dei tipi attivi nella maschera replicata, leggendo i
    /// modificatori dal catalogo (Q40-a / Q44-a), poi applica i limiti. In cache per maschera:
    /// si ricalcola solo quando la maschera cambia.
    /// </summary>
    public float GetStatMultiplier(StatKind stat)
    {
        int idx = (int)stat;
        if (idx < 0 || idx >= _statCache.Length) return 1f;

        ushort mask = netActiveMask.Value;
        if (!_statCacheValid || mask != _statCacheMask)
            RebuildStatCache(mask);

        return _statCache[idx];
    }

    // ── Helper interni ────────────────────────────────────────────────────────

    private static ushort MaskBit(StatusEffectType type) => (ushort)(1 << (int)type);

    /// <summary>Ricalcola la maschera replicata dai tipi attivi. SERVER ONLY. Scrive solo se cambia.</summary>
    private void SyncMask()
    {
        if (!IsServer) return;
        ushort mask = 0;
        for (int i = 0; i < _active.Count; i++)
            mask |= MaskBit(_active[i].data.type);
        if (netActiveMask.Value == mask) return;

        netActiveMask.Value = mask;

        // Rev BR: se la maschera cambia può cambiare l'HP max (Combat Stim −30%): il server
        // taglia gli HP correnti al nuovo massimo. Se il massimo risale non restituisce nulla.
        if (_health != null)
            _health.ServerClampHPToMax();
    }

    private ActiveEffect NewInstance(StatusEffectData data, int stacks, float duration) => new ActiveEffect
    {
        data = data,
        remaining = duration,        // ignorato se persistente
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

    // ── Helper modificatori (Rev BR) ───────────────────────────────────────────

    // Cache dei moltiplicatori per maschera: indice = (int)StatKind.
    private readonly float[] _statCache = new float[(int)StatKind.COUNT];
    private ushort _statCacheMask;
    private bool _statCacheValid;

    // Rev BV-a — ruolo del giocatore per i modificatori. Cambia solo da ApplyStatRole, che
    // invalida la cache: la cache resta quindi indicizzata per maschera.
    private CrewRole _statRole = CrewRole.None;

    // Bit dei tipi Buff secondo il catalogo (statico a runtime: calcolato una volta).
    private ushort _buffBits;
    private bool _buffBitsValid;

    private ushort BuffBits
    {
        get
        {
            if (!_buffBitsValid)
            {
                _buffBits = 0;
                for (int i = 0; i < statusCatalog.Count; i++)
                {
                    StatusEffectData d = statusCatalog[i];
                    if (d != null && d.category == StatusCategory.Buff && (int)d.type >= 0 && (int)d.type < MaxMaskTypes)
                        _buffBits |= MaskBit(d.type);
                }
                _buffBitsValid = true;
            }
            return _buffBits;
        }
    }

    private void RebuildStatCache(ushort mask)
    {
        // Somme percentuali per statistica (Q40-a).
        for (int s = 0; s < _statCache.Length; s++)
            _statCache[s] = 0f;

        for (int bit = 0; bit < MaxMaskTypes; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;

            StatusEffectData data = FindInCatalog((StatusEffectType)bit);
            if (data == null || !data.HasStatModifiers) continue;

            for (int m = 0; m < data.modifiers.Length; m++)
            {
                int stat = (int)data.modifiers[m].stat;
                if (stat < 0 || stat >= _statCache.Length) continue;
                _statCache[stat] += data.modifiers[m].percent;
            }
        }

        // Rev BV-a (Q93-a) — modificatori del ruolo, sommati a quelli degli stati prima dei limiti.
        if (roleStats != null)
            roleStats.AddRolePercents(_statRole, _statCache);

        // Somma → moltiplicatore limitato.
        for (int s = 0; s < _statCache.Length; s++)
        {
            float multiplier = 1f + _statCache[s] / 100f;
            _statCache[s] = limits != null
                ? limits.Clamp((StatKind)s, multiplier)
                : StatModifierLimits.ClampFallback((StatKind)s, multiplier);
        }

        _statCacheMask = mask;
        _statCacheValid = true;
    }

    /// <summary>
    /// Rev BR — moltiplicatore del danno di un DoT in base al tipo di stato. Solo le Radiazioni
    /// hanno una statistica dedicata (Hazmat, GDD §9.6). Il moltiplicatore generale del danno
    /// subito si applica poi in PlayerHealthSystem.ApplyDamage, come per ogni altro danno.
    /// </summary>
    private float TypedDamageMultiplier(StatusEffectType type)
    {
        switch (type)
        {
            case StatusEffectType.Radiation: return GetStatMultiplier(StatKind.RadiationDamageTaken);
            default: return 1f;
        }
    }

    private void HandleMaskChanged(ushort previous, ushort current)
    {
        _statCacheValid = false;
        OnActiveMaskChanged?.Invoke();
        OnStatModifiersChanged?.Invoke();   // Rev BV-a
    }

    /// <summary>Rev BV-a — evento statico di PlayerCrewRole: interessa solo il proprio giocatore.</summary>
    private void HandleRoleChanged(ulong clientId, CrewRole role)
    {
        if (clientId != OwnerClientId) return;
        ApplyStatRole(role);
    }

    /// <summary>
    /// Rev BV-a (Q93-a) — adotta il ruolo per i modificatori di statistica. Sul server gli HP
    /// correnti seguono il nuovo massimo mantenendo la frazione (prima si legge il massimo con il
    /// ruolo vecchio). Su tutti i client la cache si ricalcola e la UI dell'owner si aggiorna.
    /// </summary>
    private void ApplyStatRole(CrewRole role)
    {
        if (role == _statRole) return;

        float previousMax = IsServer && _health != null ? _health.MaxHP : 0f;

        LogV($"Ruolo per le statistiche: {_statRole} → {role}");
        _statRole = role;
        _statCacheValid = false;

        if (IsServer && _health != null)
            _health.ServerRescaleHPToNewMax(previousMax);

        OnStatModifiersChanged?.Invoke();
    }

    /// <summary>
    /// Rev BR — ogni StatusEffectType deve avere un bit nella maschera ushort (valori 0–15).
    /// Un tipo fuori range non verrebbe mai replicato: errore reale, log incondizionato.
    /// </summary>
    private static void ValidateEnumFitsMask()
    {
        foreach (StatusEffectType t in Enum.GetValues(typeof(StatusEffectType)))
            if ((int)t < 0 || (int)t >= MaxMaskTypes)
                Debug.LogError($"[PlayerStatusEffects] StatusEffectType.{t} = {(int)t} fuori dalla maschera replicata " +
                               $"(ammessi 0–{MaxMaskTypes - 1}). Allargare netActiveMask prima di aggiungere tipi.");
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
        // Rev BR: pannello più alto (buff e moltiplicatori) → passo verticale 250.
        float y = 310 + (OwnerClientId * 250f);

        GUILayout.BeginArea(new Rect(570, y, 360, 245));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[PlayerStatusEffects] Client {OwnerClientId} — attivi: {_active.Count} · mask {netActiveMask.Value} · condizioni {ConditionMask}");

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

        // Rev BR — buff. "Stim ×0.6" prova il fattore di durata del non-Corpsman (valore di sola
        // prova: quello reale arriva dal profilo del kit in BR-b).
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Hazmat")) ApplyEffect(StatusEffectType.Hazmat);
        if (GUILayout.Button("Stim")) ApplyEffect(StatusEffectType.CombatStim);
        if (GUILayout.Button("Stim ×0.6")) ApplyEffect(StatusEffectType.CombatStim, 0.6f);
        if (GUILayout.Button("- Buff"))
        {
            RemoveEffect(StatusEffectType.Hazmat);
            RemoveEffect(StatusEffectType.CombatStim);
            RemoveEffect(StatusEffectType.Shielded);   // Rev BV-b
        }
        GUILayout.EndHorizontal();

        // Rev BV-b — scudo del Quartermaster (stato Shielded) senza passare dal gadget.
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Scudo 7 s")) ApplyEffectForSeconds(StatusEffectType.Shielded, 7f);
        if (GUILayout.Button("- Scudo")) RemoveEffect(StatusEffectType.Shielded);
        GUILayout.EndHorizontal();

        GUILayout.Label($"Ruolo {_statRole} · Vel ×{GetStatMultiplier(StatKind.MoveSpeed):F2} · " +
                        $"Danno ×{GetStatMultiplier(StatKind.DamageTaken):F2} · " +
                        $"Rad ×{GetStatMultiplier(StatKind.RadiationDamageTaken):F2} · HPmax ×{GetStatMultiplier(StatKind.MaxHP):F2}");

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