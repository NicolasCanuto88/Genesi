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
/// SEAM MODIFICATORE DI RUOLO (Q5-a): qui vive SOLO il profilo di DEFAULT
/// (identità non-Corpsman). Il Corpsman NON esiste ancora: quando esisterà, il
/// futuro sistema ruoli fornirà il proprio profilo (2s / 60% HP) sovrascrivendo il
/// default via PlayerReviveTarget.ResolveProfile — senza toccare questo asset.
/// Stesso pattern dell'hook a identità di default di Rev BB.
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
             "GDD ruoli: non-Corpsman = 4s. Il Corpsman (2s) arriverà dal futuro sistema ruoli.")]
    [SerializeField] private float defaultChannelSeconds = 4f;

    [Tooltip("Frazione di HP massimi ripristinata da un rianimatore NON-Corpsman (0..1). " +
             "GDD ruoli: non-Corpsman = 30%. Il Corpsman (60%) arriverà dal futuro sistema ruoli.")]
    [Range(0f, 1f)]
    [SerializeField] private float defaultHpRestoreFraction = 0.30f;

    [Header("Cariche defib — PLACEHOLDER (dominio Medbay, non ancora esistente) — Q6-a")]
    [Tooltip("Cariche iniziali del pool defib per la sessione. PLACEHOLDER: nel design le " +
             "cariche scalano col livello medbay (T1=0 / T2=3 / T3=4 / T4=5). La medbay non " +
             "esiste ancora → questo valore fa da segnaposto. Per il GATE netcode (contesa) " +
             "impostare > 0 (es. 3 = 'come T2'). A 0 il defib è indisponibile e il downed va a " +
             "timer→respawn (comportamento T1).")]
    [SerializeField] private int placeholderStartingCharges = 3;

    public float DefaultChannelSeconds => Mathf.Max(0.01f, defaultChannelSeconds);
    public float DefaultHpRestoreFraction => Mathf.Clamp01(defaultHpRestoreFraction);
    public int PlaceholderStartingCharges => Mathf.Max(0, placeholderStartingCharges);
}
