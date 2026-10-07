using System;
using UnityEngine;

/// <summary>
/// Tipo canonico di stato di alterazione. Id stabile su cui il codice fa switch
/// type-safe (Q5-a Rev BC) — coerente con ZoneEvent/EMIntensity gia' in uso.
/// Gli asset StatusEffectData portano i parametri; l'enum porta l'identita'.
///
/// STABILE, NON RIORDINARE (Rev BR): il valore è serializzato negli asset SED_* e
/// fa da bit nella maschera replicata di PlayerStatusEffects (ushort → massimo 16
/// tipi, valori 0–15). I tipi nuovi si aggiungono in coda con valore esplicito.
/// </summary>
public enum StatusEffectType
{
    Radiation = 0,       // da zone/hazard (DeepVoid, tempeste) — SORGENTE FUORI SCOPE Rev BC
    Poison = 1,          // danno nel tempo
    CompoundWounds = 2,  // "Ferite Composte" — applicato alla rianimazione (D27), curabile solo medbay T3+;
                         // Rev BU-a: StatModifier (HP max e velocità giù, valori nell'asset)

    // ── Buff (Rev BR · Q41-a): droghe del Corpsman ──
    Hazmat = 3,          // Hazmat Injection — riduce il danno da Radiazioni (GDD §9.6, conciliazione v0.9.46)
    CombatStim = 4       // Combat Stim — velocità su, HP max giù (workshop ruoli M3)
}

/// <summary>
/// Come viene applicato l'effetto di uno stato. Tipizzato per estensione futura
/// (Heal verrà aggiunto se servirà) senza toccare il framework.
///   None         — marker puro, nessun effetto (Ferite Composte fino a Rev BU-a)
///   Damage       — DoT: ogni tick instrada danno a PlayerHealthSystem.ApplyDamage (server-only)
///   StatModifier — Rev BR: nessun tick; finché lo stato è attivo i suoi `modifiers`
///                  cambiano le statistiche del giocatore (PlayerStatusEffects.GetStatMultiplier)
/// STABILE, NON RIORDINARE: valore serializzato negli asset.
/// </summary>
public enum EffectKind
{
    None = 0,
    Damage = 1,
    StatModifier = 2
}

/// <summary>
/// Politica di stacking (Q2-a Rev BC). Scelta per-stato sullo SO:
///   RefreshDuration  — riapplicare rinnova la durata, nessun accumulo (Radiazioni, Ferite Composte)
///   StackIndependent — ogni applicazione e' un'istanza a se' con la propria scadenza (cap = maxStacks)
///   StackIntensity   — gli stack aumentano l'entita' per tick (danno x stack), durata rinnovata, cap = maxStacks (Veleno)
/// </summary>
public enum StackingPolicy
{
    RefreshDuration,
    StackIndependent,
    StackIntensity
}

/// <summary>
/// Rev BR (Q38-a) — natura dello stato.
///   Condition — alterazione da curare (Veleno, Radiazioni, Ferite Composte). È quello che
///               vede la Recovery Bay (PlayerStatusEffects.ConditionMask).
///   Buff      — effetto voluto (droghe). Invisibile alla Bay, non si cura, scade da solo.
/// Default Condition (0): gli asset creati prima di BR restano Condition senza toccarli.
/// </summary>
public enum StatusCategory
{
    Condition = 0,
    Buff = 1
}

/// <summary>
/// Rev BR (Q39-b) — statistiche modificabili dagli stati StatModifier, e dove si applicano.
/// STABILE, NON RIORDINARE: valore serializzato negli asset. COUNT è la sentinella.
/// </summary>
public enum StatKind
{
    MoveSpeed = 0,             // velocità di movimento — client proprietario (PlayerController)
    DamageTaken = 1,           // tutto il danno in ingresso — server (PlayerHealthSystem.ApplyDamage)
    RadiationDamageTaken = 2,  // solo il DoT delle Radiazioni — server (tick di PlayerStatusEffects)
    MaxHP = 3,                 // HP massimi — tutti i client (PlayerHealthSystem.MaxHP)

    COUNT = 4                  // sentinella — SEMPRE ULTIMA
}

/// <summary>
/// Rev BR — una voce di modifica: statistica + variazione percentuale.
/// Regola di combinazione (Q40-a): per ogni statistica le percentuali degli stati attivi si
/// SOMMANO (Combat Stim +20% e un altro +10% = +30%); il moltiplicatore finale è
/// 1 + somma/100, limitato dallo SO StatModifierLimits.
/// </summary>
[Serializable]
public struct StatModifierEntry
{
    [Tooltip("Statistica modificata.")]
    public StatKind stat;

    [Tooltip("Variazione percentuale: +20 = +20%, -70 = -70%. Le percentuali di stati diversi si sommano.")]
    public float percent;
}

/// <summary>
/// StatusEffectData — parametri di uno stato di alterazione (Rev BC / D25).
/// Convenzione SO del progetto (asset-driven, mai valori hardcodati nel codice).
/// Crea asset: Assets &gt; Create &gt; SpaceSurvivor &gt; Status Effect Data
///
/// NAMESPACE: globale, come i sibling sul Player prefab (PlayerHealthSystem,
/// PlayerController, PlayerNetworkOwnership). Deviazione documentata rispetto agli
/// altri SO (SpaceSurvivor.Ship): questo e' un SO di DOMINIO PLAYER, e gli script
/// player del progetto vivono nel namespace globale — coerenza-di-dominio prevale
/// su coerenza-con-gli-altri-SO. Il menuName conserva il raggruppamento
/// "SpaceSurvivor/" nel menu Create.
///
/// PERSISTENZA (Q7 Rev BC): duration &lt;= 0 ⇒ stato PERSISTENTE (non scade da solo).
/// Rev BP-b (Q23-a): TUTTI e tre gli stati sono persistenti. Veleno e Radiazioni
/// restano finché non curati in Recovery Bay (T2+) o finché il giocatore non torna
/// come clone (respawn = corpo nuovo, stati azzerati); Ferite Composte richiede T3+.
///
/// VALORI (Rev BP-b): DoT a ritmi di design — Radiazioni 1 HP ogni 12 s (≈ 5 HP/min,
/// la "tempesta" di GDD §9.6), Veleno 1 HP ogni 4 s per stack (≈ 15 HP/min, max 3
/// stack). Le sorgenti reali (hazard, tempeste, nemici) non esistono ancora: si
/// ricalibrano quando verranno cablate. Oggi si applicano dall'overlay di debug.
///
/// MODIFICATORI (Rev BR · Q38-a / Q44-a): uno stato StatModifier porta una lista di
/// `modifiers`. Nessun tempo né stack viene replicato: ogni client ricava i
/// moltiplicatori dalla maschera replicata dei tipi attivi più il catalogo del Player
/// prefab (lo stesso asset su tutti). Perciò:
///   - uno stato StatModifier si comporta SEMPRE come RefreshDuration (EffectivePolicy),
///     così la maschera basta a descriverlo;
///   - deve stare nel "Status Catalog" del Player prefab, altrimenti i client non
///     possono leggerne i modificatori.
/// </summary>
[CreateAssetMenu(fileName = "StatusEffectData_New",
                 menuName = "SpaceSurvivor/Status Effect Data")]
public class StatusEffectData : ScriptableObject
{
    [Header("Identita'")]
    [Tooltip("Tipo canonico. Un solo stato attivo per tipo, salvo StackIndependent.")]
    public StatusEffectType type = StatusEffectType.Poison;

    [Tooltip("Nome leggibile per debug/UI futura.")]
    public string displayName = "Nuovo Stato";

    [TextArea] public string description = "";

    [Header("Categoria (Rev BR)")]
    [Tooltip("Condition = alterazione da curare (la vede la Recovery Bay). " +
             "Buff = effetto voluto (droghe): invisibile alla Bay, non si cura.")]
    public StatusCategory category = StatusCategory.Condition;

    [Header("Durata")]
    [Tooltip("Durata in secondi. <= 0 ⇒ PERSISTENTE (non scade da solo; rimosso solo via cura/RemoveEffect).")]
    public float duration = 8f;

    [Header("Effetto")]
    [Tooltip("None = marker puro (nessun tick). Damage = DoT verso PlayerHealthSystem.ApplyDamage. " +
             "StatModifier = nessun tick, applica i Modifiers finché attivo (Rev BR).")]
    public EffectKind effectKind = EffectKind.Damage;

    [Tooltip("Solo Damage. Secondi tra un tick e l'altro. <= 0 ⇒ nessun tick periodico.")]
    public float tickInterval = 1f;

    [Tooltip("Solo Damage. HP di danno per tick. Con StackIntensity il danno effettivo e' effectPerTick x stack correnti.")]
    public float effectPerTick = 1f;

    [Header("Modificatori (Rev BR · solo StatModifier)")]
    [Tooltip("Statistiche modificate finché lo stato è attivo. Percentuali: si sommano con quelle di altri stati.")]
    public StatModifierEntry[] modifiers = new StatModifierEntry[0];

    [Header("Stacking")]
    [Tooltip("Ignorata per gli stati StatModifier, che si comportano sempre come RefreshDuration (Rev BR).")]
    public StackingPolicy stackingPolicy = StackingPolicy.RefreshDuration;

    [Tooltip("Cap. StackIntensity: massimo moltiplicatore di intensita'. StackIndependent: massimo numero di istanze " +
             "contemporanee dello stesso tipo. RefreshDuration: ignorato (sempre 1).")]
    [Min(1)] public int maxStacks = 1;

    [Header("Curabilita' (Rev BC · BP-b)")]
    [Tooltip("Se true, lo stato e' rimovibile SOLO da una medbay di tier >= 3 (Ferite Composte). " +
             "Se false, lo stato e' curabile senza vincoli di tier. Ignorato per i Buff, che non si curano.")]
    public bool curableOnlyAtMedbayT3Plus = false;

    /// <summary>true se lo stato non scade da solo (duration &lt;= 0). Vedi Q7 Rev BC.</summary>
    public bool IsPersistent => duration <= 0f;

    /// <summary>true se lo stato produce tick di danno periodici (Damage con intervallo valido).</summary>
    public bool HasPeriodicTick => effectKind == EffectKind.Damage && tickInterval > 0f;

    /// <summary>Rev BR — true se lo stato modifica statistiche (StatModifier con almeno una voce).</summary>
    public bool HasStatModifiers => effectKind == EffectKind.StatModifier && modifiers != null && modifiers.Length > 0;

    /// <summary>
    /// Rev BR — politica di stacking effettivamente usata. Gli stati StatModifier sono sempre
    /// RefreshDuration: la maschera replicata (un bit per tipo) deve bastare ai client per
    /// calcolare i modificatori (Q38-a).
    /// </summary>
    public StackingPolicy EffectivePolicy =>
        effectKind == EffectKind.StatModifier ? StackingPolicy.RefreshDuration : stackingPolicy;

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (effectKind == EffectKind.StatModifier && stackingPolicy != StackingPolicy.RefreshDuration)
            Debug.LogWarning($"[StatusEffectData] {name}: gli stati StatModifier usano sempre RefreshDuration " +
                             $"(Rev BR). La policy {stackingPolicy} verrà ignorata.", this);

        if (effectKind != EffectKind.StatModifier && modifiers != null && modifiers.Length > 0)
            Debug.LogWarning($"[StatusEffectData] {name}: i Modifiers hanno effetto solo con EffectKind.StatModifier.", this);

        if (modifiers != null)
            for (int i = 0; i < modifiers.Length; i++)
                if (modifiers[i].stat < 0 || modifiers[i].stat >= StatKind.COUNT)
                    Debug.LogWarning($"[StatusEffectData] {name}: voce {i} con statistica non valida ({modifiers[i].stat}).", this);
    }
#endif
}