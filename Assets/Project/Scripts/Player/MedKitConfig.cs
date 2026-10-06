using SpaceSurvivor.Ship;
using UnityEngine;

/// <summary>
/// MedKitConfig — parametri di tuning del kit medico personale (Rev BQ).
/// ScriptableObject secondo la convenzione del progetto ("tutti i valori numerici
/// di tuning vengono da SO, mai hardcodati").
///
/// NAMESPACE GLOBALE: coerenza di dominio con DefibConfig e con i sibling player
/// (PlayerHealthSystem, PlayerStatusEffects, PlayerMedKit).
///
/// CONTENUTO (workshop BQ, tutte le opzioni "a"):
///   - Q30-a — CAPIENZA DEL KIT: quanti pezzi per tipo porta ogni giocatore. Il kit si
///     riempie all'armadietto medico (MedicalSupplyLocker) prelevando dalla stiva.
///   - Q33-a — MEDIKIT: HP nominali del base (+25) e dell'avanzato (+50).
///   - PROFILI DI RUOLO (Rev U): Corpsman 1,5 s di canale ed effetto pieno; chiunque
///     altro 3 s e 60% dell'effetto. Per l'antidoto conta solo la durata del canale:
///     la cura degli stati è tutto-o-niente.
///   - Q31-a / Q32-a — portata del bersaglio (2,5 m) e spostamento massimo durante il
///     canale (1 m).
///   - Q36-a — durata della riga di feedback dopo un esito (2 s).
///
/// DROGHE (Rev BR-b · workshop StatModifier, tutte "a"):
///   - capienza per droga (Adrenaline 2, Hazmat 1, Combat Stim 2);
///   - Adrenaline: HP nominali (+15), uguali per tutti i ruoli (Q43-a);
///   - tier minimo della Medbay per prelevare la Combat Stim all'armadietto (T2, Q48-a);
///   - per la Combat Stim di un non-Corpsman la DURATA è moltiplicata per l'effetto del
///     profilo di default (0.6 → 18 s su 30, Q43-a). Durata ed effetti delle droghe stanno
///     negli asset SED_Hazmat / SED_CombatStim, non qui.
///
/// BOMBA CURATIVA (Rev BS-b · Q65-a): solo la capienza nel kit (1), nessun tier minimo. Volo,
/// raggio, durata e cura del campo stanno nell'asset TD_HealingGrenade (ThrowableData); il
/// moltiplicatore di un non-Corpsman è quello del profilo di default (Q57-a, applicato alla
/// cura del campo).
///
/// NON QUI (decisioni di design, non tuning):
///   - quali stati cura l'antidoto (Veleno e Radiazioni, mai Ferite Composte — Q34-a):
///     costante documentata in PlayerMedKit;
///   - la scelta automatica base/avanzato (Q35-a): usa AdvancedHealAmount come soglia
///     degli HP mancanti;
///   - quale stato applica ogni droga e chi può esserne il bersaglio (Rev BR-b): costanti
///     documentate in PlayerMedKit.
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/MedKit Config", fileName = "MedKitConfig")]
public class MedKitConfig : ScriptableObject
{
    [Header("Capienza del kit personale — Q30-a")]
    [Tooltip("Medikit base che un giocatore può portare. Il kit si riempie all'armadietto medico, " +
             "prelevando dalla stiva della nave (InventorySystem).")]
    [Min(0)]
    [SerializeField] private int capMedkitBase = 2;

    [Tooltip("Medikit avanzati che un giocatore può portare.")]
    [Min(0)]
    [SerializeField] private int capMedkitAdvanced = 1;

    [Tooltip("Antidoti che un giocatore può portare.")]
    [Min(0)]
    [SerializeField] private int capAntidote = 1;

    [Header("Capienza del kit personale — droghe (Rev BR-b)")]
    [Tooltip("Dosi di Adrenaline che un giocatore può portare.")]
    [Min(0)]
    [SerializeField] private int capAdrenaline = 2;

    [Tooltip("Dosi di Hazmat che un giocatore può portare.")]
    [Min(0)]
    [SerializeField] private int capHazmatInjection = 1;

    [Tooltip("Combat Stim che un giocatore può portare.")]
    [Min(0)]
    [SerializeField] private int capCombatStim = 2;

    [Header("Capienza del kit personale — bomba curativa (Rev BS-b)")]
    [Tooltip("Bombe curative che un giocatore può portare. Si lanciano con PlayerThrower (G / RB).")]
    [Min(0)]
    [SerializeField] private int capHealingGrenade = 1;

    [Header("Medikit — HP nominali (Q33-a)")]
    [Tooltip("HP ripristinati dal medikit base con effetto pieno (Corpsman). Gli altri ruoli " +
             "applicano il moltiplicatore del proprio profilo.")]
    [Min(0f)]
    [SerializeField] private float baseHealAmount = 25f;

    [Tooltip("HP ripristinati dal medikit avanzato con effetto pieno. È anche la soglia della " +
             "scelta automatica (Q35-a): l'avanzato si usa se al bersaglio mancano almeno questi HP.")]
    [Min(0f)]
    [SerializeField] private float advancedHealAmount = 50f;

    [Header("Droghe (Rev BR-b)")]
    [Tooltip("HP ripristinati dall'Adrenaline, uguali per tutti i ruoli (Q43-a). La stamina torna piena.")]
    [Min(0f)]
    [SerializeField] private float adrenalineHealAmount = 15f;

    [Tooltip("Tier minimo della Medbay per prelevare Combat Stim all'armadietto (Q48-a). Sotto questo tier " +
             "la sua capienza di rifornimento vale 0; le stim già nel kit restano usabili.")]
    [Range(1, 4)]
    [SerializeField] private int combatStimMinMedbayTier = 2;

    [Header("Profilo di DEFAULT (non-Corpsman, malus Rev U)")]
    [Tooltip("Durata del canale di medikit e antidoto per chi non è Corpsman, in secondi.")]
    [SerializeField] private float defaultChannelSeconds = 3f;

    [Tooltip("Frazione dell'effetto del medikit per chi non è Corpsman (0.6 = 60% degli HP nominali). " +
             "Per la Combat Stim è la frazione della DURATA (Rev BR-b · Q43-a).")]
    [Min(0f)]
    [SerializeField] private float defaultEffectMultiplier = 0.6f;

    [Header("Profilo CORPSMAN — risolto via PlayerCrewRole")]
    [Tooltip("Durata del canale di medikit e antidoto per il Corpsman, in secondi.")]
    [SerializeField] private float corpsmanChannelSeconds = 1.5f;

    [Tooltip("Frazione dell'effetto del medikit per il Corpsman (1 = HP nominali pieni). " +
             "Per la Combat Stim è la frazione della durata.")]
    [Min(0f)]
    [SerializeField] private float corpsmanEffectMultiplier = 1f;

    [Header("Bersaglio e canale — Q31-a · Q32-a")]
    [Tooltip("Distanza massima, in metri, a cui si cura un compagno guardandolo. Oltre, o senza " +
             "compagno sotto il mirino, il kit si usa su se stessi.")]
    [SerializeField] private float targetRange = 2.5f;

    [Tooltip("Spostamento massimo, in metri, dal punto d'inizio del canale. Oltre, il canale si " +
             "interrompe e l'item non viene consumato.")]
    [SerializeField] private float maxMoveDistance = 1f;

    [Header("Feedback a schermo — Q36-a")]
    [Tooltip("Secondi per cui resta visibile la riga di esito (\"+25 HP\", \"Nothing to treat\"...).")]
    [Min(0f)]
    [SerializeField] private float feedbackHoldSeconds = 2f;

    public float BaseHealAmount => Mathf.Max(0f, baseHealAmount);
    public float AdvancedHealAmount => Mathf.Max(0f, advancedHealAmount);
    public float AdrenalineHealAmount => Mathf.Max(0f, adrenalineHealAmount);
    public float TargetRange => Mathf.Max(0.1f, targetRange);
    public float MaxMoveDistance => Mathf.Max(0.05f, maxMoveDistance);
    public float FeedbackHoldSeconds => Mathf.Max(0f, feedbackHoldSeconds);

    /// <summary>Capienza del kit per il tipo indicato. Tipi fuori dal kit → 0. Limite superiore 255 (byte replicato).</summary>
    public int CapFor(ItemType type)
    {
        switch (type)
        {
            case ItemType.MedkitBase: return Mathf.Clamp(capMedkitBase, 0, 255);
            case ItemType.MedkitAdvanced: return Mathf.Clamp(capMedkitAdvanced, 0, 255);
            case ItemType.Antidote: return Mathf.Clamp(capAntidote, 0, 255);
            case ItemType.Adrenaline: return Mathf.Clamp(capAdrenaline, 0, 255);
            case ItemType.HazmatInjection: return Mathf.Clamp(capHazmatInjection, 0, 255);
            case ItemType.CombatStim: return Mathf.Clamp(capCombatStim, 0, 255);
            case ItemType.HealingGrenade: return Mathf.Clamp(capHealingGrenade, 0, 255);
            default: return 0;
        }
    }

    /// <summary>
    /// Rev BR-b (Q48-a) — tier minimo della Medbay per prelevare il tipo all'armadietto.
    /// Solo la Combat Stim ha un vincolo (T2); tutto il resto è disponibile da T1 (compresa la
    /// bomba curativa, loadout T1 — Rev BS-b · Q65-a).
    /// </summary>
    public int MinMedbayTierFor(ItemType type)
    {
        switch (type)
        {
            case ItemType.CombatStim: return Mathf.Clamp(combatStimMinMedbayTier, 1, 4);
            default: return 1;
        }
    }

    /// <summary>HP nominali del medikit indicato (effetto pieno). Tipi che non curano HP → 0.</summary>
    public float HealAmountFor(ItemType type)
    {
        switch (type)
        {
            case ItemType.MedkitBase: return BaseHealAmount;
            case ItemType.MedkitAdvanced: return AdvancedHealAmount;
            default: return 0f;
        }
    }

    /// <summary>
    /// Profilo di ruolo dell'utilizzatore: Corpsman oppure default (malus Rev U). Rev BS-b: il
    /// moltiplicatore vale anche per la cura del campo della bomba curativa (Q57-a).
    /// </summary>
    public MedKitProfile ProfileFor(bool isCorpsman)
    {
        return isCorpsman
            ? new MedKitProfile(corpsmanChannelSeconds, corpsmanEffectMultiplier)
            : new MedKitProfile(defaultChannelSeconds, defaultEffectMultiplier);
    }
}

/// <summary>Profilo risolto per chi usa il kit (Rev BQ): durata del canale e moltiplicatore dell'effetto.</summary>
public readonly struct MedKitProfile
{
    public readonly float ChannelSeconds;
    public readonly float EffectMultiplier;

    public MedKitProfile(float channelSeconds, float effectMultiplier)
    {
        ChannelSeconds = Mathf.Max(0.01f, channelSeconds);
        EffectMultiplier = Mathf.Max(0f, effectMultiplier);
    }
}