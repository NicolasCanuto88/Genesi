using UnityEngine;

/// <summary>
/// Tipo canonico di stato di alterazione. Id stabile su cui il codice fa switch
/// type-safe (Q5-a Rev BC) — coerente con ZoneEvent/EMIntensity gia' in uso.
/// Gli asset StatusEffectData portano i parametri; l'enum porta l'identita'.
/// </summary>
public enum StatusEffectType
{
    Radiation,       // da zone/hazard (DeepVoid, tempeste) — SORGENTE FUORI SCOPE Rev BC
    Poison,          // danno nel tempo
    CompoundWounds   // "Ferite Composte" — applicato alla rianimazione (D27), curabile solo medbay T3+
}

/// <summary>
/// Come viene applicato l'effetto di un tick. Tipizzato per estensione futura
/// (Heal / StatModifier verranno aggiunti quando esisteranno gli stat player /
/// una API di cura) senza toccare il framework. Oggi bastano due casi REALI:
/// gli stati DoT (Radiazioni/Veleno → Damage) e il marker (Ferite Composte → None).
/// </summary>
public enum EffectKind
{
    None,     // marker puro: presenza + eventuale flag di curabilita', nessun tick meccanico
    Damage    // ogni tick instrada danno a PlayerHealthSystem.ApplyDamage (server-only)
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
/// Ferite Composte usa questo: resta finche' non curato a medbay T3+ (hook placeholder).
/// Radiazioni/Veleno usano duration &gt; 0 (scadono).
///
/// SORGENTI FUORI SCOPE: gli hazard che applicano Radiazioni (ZoneManager/tempeste)
/// e la medbay che cura Ferite Composte NON esistono ancora. Qui si costruisce solo
/// lo stato + i seam. I valori numerici degli asset in Rev BC sono VALORI DI TEST
/// (vedi guida Editor); i rate canonici GDD §9.6 (radiazioni -1/-5/-15 HP/min) si
/// calibrano quando la sorgente verra' cablata.
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

    [Header("Durata")]
    [Tooltip("Durata in secondi. <= 0 ⇒ PERSISTENTE (non scade da solo; rimosso solo via cura/RemoveEffect).")]
    public float duration = 8f;

    [Header("Effetto periodico")]
    [Tooltip("Cosa fa un tick. None = marker puro (nessun tick). Damage = instrada a PlayerHealthSystem.ApplyDamage.")]
    public EffectKind effectKind = EffectKind.Damage;

    [Tooltip("Secondi tra un tick e l'altro. <= 0 ⇒ nessun tick periodico (coerente con EffectKind.None).")]
    public float tickInterval = 1f;

    [Tooltip("Entita' dell'effetto per singolo tick. Con EffectKind.Damage = HP di danno per tick. " +
             "Con StackIntensity il danno effettivo e' effectPerTick x stack correnti.")]
    public float effectPerTick = 1f;

    [Header("Stacking")]
    public StackingPolicy stackingPolicy = StackingPolicy.RefreshDuration;

    [Tooltip("Cap. StackIntensity: massimo moltiplicatore di intensita'. StackIndependent: massimo numero di istanze " +
             "contemporanee dello stesso tipo. RefreshDuration: ignorato (sempre 1).")]
    [Min(1)] public int maxStacks = 1;

    [Header("Curabilita' (hook placeholder Rev BC)")]
    [Tooltip("Se true, lo stato e' rimovibile SOLO da una medbay di tier >= 3 (Ferite Composte). " +
             "La medbay non esiste ancora: TryCure ritorna false finche' non arrivera' con la sua milestone. " +
             "Se false, lo stato e' curabile/rimovibile senza vincoli di tier.")]
    public bool curableOnlyAtMedbayT3Plus = false;

    /// <summary>true se lo stato non scade da solo (duration &lt;= 0). Vedi Q7 Rev BC.</summary>
    public bool IsPersistent => duration <= 0f;

    /// <summary>true se lo stato produce tick periodici (Damage con intervallo valido).</summary>
    public bool HasPeriodicTick => effectKind != EffectKind.None && tickInterval > 0f;
}
