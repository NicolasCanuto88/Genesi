using UnityEngine;

/// <summary>
/// DefibConfig — parametri di tuning per Morte &amp; Rianimazione (D27).
/// ScriptableObject secondo la convenzione del progetto ("tutti i valori numerici
/// di tuning vengono da SO, mai hardcodati").
///
/// NAMESPACE GLOBALE: coerenza-di-dominio con i sibling player (PlayerHealthSystem /
/// PlayerStatusEffects / StatusEffectData sono globali — deviazione già documentata
/// in Rev BC). La rianimazione è dominio player.
///
/// SEAM MODIFICATORE DI RUOLO (Q5-a Rev BD → CABLATO in Rev BM): l'asset porta DUE
/// profili — DEFAULT (non-Corpsman: 4s / 30%) e CORPSMAN (2s / 60%, GDD ruoli M3
/// close). PlayerReviveTarget.ResolveProfile sceglie il profilo in base al ruolo
/// networked del rianimatore (PlayerCrewRole, Rev BM). Clausola malus Rev U: il
/// non-Corpsman può defibrillare, ma con canale doppio e metà HP.
///
/// NON ANCORA QUI (tracciato per Rev BP — Corpsman T2):
///   - 3s di immunità al danno dopo la rianimazione del Corpsman (tocca il choke
///     point ApplyDamage → revisione a parte);
///   - cariche dal tier della Medbay (T1=0 / T2=3 / T3=4 / T4=5) e regola
///     "nessun Corpsman in crew → nessun defib".
///
/// SEAM CARICHE MEDBAY (Q6-a): le cariche del defibrillatore sono, nel design, un
/// pool di livello MEDBAY (T1=0 / T2=3 / T3=4 / T4=5). La medbay non esiste ancora
/// (suo milestone) → qui c'è solo un valore PLACEHOLDER usato da DefibChargePool.
/// La tabella per-tier è documentata nel tooltip; verrà cablata quando la medbay
/// avrà il suo livello — stesso spirito del seam TryCure(tier) di Rev BC che nega
/// finché la medbay non esiste.
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/Defib Config", fileName = "DefibConfig")]
public class DefibConfig : ScriptableObject
{
    [Header("Profilo di DEFAULT (identità non-Corpsman) — Q5-a")]
    [Tooltip("Durata del canale di defibrillazione per un rianimatore NON-Corpsman, in secondi. " +
             "GDD ruoli: non-Corpsman = 4s (malus Rev U: il doppio del Corpsman).")]
    [SerializeField] private float defaultChannelSeconds = 4f;

    [Tooltip("Frazione di HP massimi ripristinata da un rianimatore NON-Corpsman (0..1). " +
             "GDD ruoli: non-Corpsman = 30% (0.30).")]
    [Range(0f, 1f)]
    [SerializeField] private float defaultHpRestoreFraction = 0.30f;

    [Header("Profilo CORPSMAN (Rev BM) — risolto via PlayerCrewRole")]
    [Tooltip("Durata del canale di defibrillazione per un rianimatore CORPSMAN, in secondi. " +
             "GDD ruoli: Corpsman = 2s.")]
    [SerializeField] private float corpsmanChannelSeconds = 2f;

    [Tooltip("Frazione di HP massimi ripristinata da un rianimatore CORPSMAN (0..1). " +
             "GDD ruoli: Corpsman = 60% (0.60).")]
    [Range(0f, 1f)]
    [SerializeField] private float corpsmanHpRestoreFraction = 0.60f;

    [Header("Cariche defib — PLACEHOLDER (dominio Medbay, non ancora esistente) — Q6-a")]
    [Tooltip("Cariche iniziali del pool defib per la sessione. PLACEHOLDER: nel design le " +
             "cariche scalano col livello medbay (T1=0 / T2=3 / T3=4 / T4=5). La medbay non " +
             "esiste ancora → questo valore fa da segnaposto. Per il GATE netcode (contesa) " +
             "impostare > 0 (es. 3 = 'come T2'). A 0 il defib è indisponibile e il downed va a " +
             "timer→respawn (comportamento T1).")]
    [SerializeField] private int placeholderStartingCharges = 3;

    public float DefaultChannelSeconds => Mathf.Max(0.01f, defaultChannelSeconds);
    public float DefaultHpRestoreFraction => Mathf.Clamp01(defaultHpRestoreFraction);

    public float CorpsmanChannelSeconds => Mathf.Max(0.01f, corpsmanChannelSeconds);
    public float CorpsmanHpRestoreFraction => Mathf.Clamp01(corpsmanHpRestoreFraction);

    public int PlaceholderStartingCharges => Mathf.Max(0, placeholderStartingCharges);
}