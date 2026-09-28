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
/// PROFILI DI RUOLO (Q5-a Rev BD → cablato in Rev BM, immunità in Rev BP-a): l'asset
/// porta DUE profili — DEFAULT (non-Corpsman: 4s / 30% / nessuna immunità) e CORPSMAN
/// (2s / 60% / 3s di immunità, GDD ruoli M3 close). PlayerReviveTarget.ResolveProfile
/// sceglie il profilo in base al ruolo networked del rianimatore (PlayerCrewRole).
/// Clausola malus Rev U: il non-Corpsman può defibrillare, ma con canale doppio, metà
/// HP e senza immunità (Q27-a di BP).
///
/// CARICHE PER TIER MEDBAY (Rev BP-a · Q26-a): le cariche del defibrillatore sono un
/// pool di livello MEDBAY (T1=0 / T2=3 / T3=4 / T4=5). DefibChargePool riempie il pool
/// dalla tabella allo spawn e a ogni cambio di tier (MedbaySystem.OnTierChanged).
/// Nel design le cariche sono PER MISSIONE e si ricaricano in medbay tra una missione
/// e l'altra: il ciclo missione non esiste ancora, quindi la ricarica avviene solo
/// all'inizio della sessione e al cambio di tier (debito tracciato nel GDD).
///
/// REGOLA "NESSUN CORPSMAN IN CREW → NESSUN DEFIB" (BP-a): non è un valore di tuning
/// e non vive qui. La applica DefibChargePool.IsDefibAvailable.
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

    [Tooltip("Secondi di immunità al danno dopo una rianimazione di un NON-Corpsman. " +
             "Rev BP-a · Q27-a: 0 (l'immunità è del solo profilo Corpsman).")]
    [Min(0f)]
    [SerializeField] private float defaultImmunitySeconds = 0f;

    [Header("Profilo CORPSMAN (Rev BM) — risolto via PlayerCrewRole")]
    [Tooltip("Durata del canale di defibrillazione per un rianimatore CORPSMAN, in secondi. " +
             "GDD ruoli: Corpsman = 2s.")]
    [SerializeField] private float corpsmanChannelSeconds = 2f;

    [Tooltip("Frazione di HP massimi ripristinata da un rianimatore CORPSMAN (0..1). " +
             "GDD ruoli: Corpsman = 60% (0.60).")]
    [Range(0f, 1f)]
    [SerializeField] private float corpsmanHpRestoreFraction = 0.60f;

    [Tooltip("Secondi di immunità al danno dopo una rianimazione del CORPSMAN (Rev BP-a). " +
             "GDD ruoli: 3s. Blocca ogni danno, DoT degli stati compresi (choke point ApplyDamage).")]
    [Min(0f)]
    [SerializeField] private float corpsmanImmunitySeconds = 3f;

    [Header("Cariche defib per tier Medbay — Rev BP-a · Q26-a")]
    [Tooltip("Cariche del pool defib per tier del modulo Medbay. Indice 0 = T1. GDD: T1=0 / T2=3 / " +
             "T3=4 / T4=5. A 0 il defib è indisponibile: il downed va a timer → respawn. Un tier " +
             "oltre la tabella usa l'ultima riga. Per i test del defib porta la Medbay a T2 " +
             "(debugStartTier di MedbaySystem, oppure l'overlay 'Tier +1').")]
    [SerializeField] private int[] chargesByMedbayTier = { 0, 3, 4, 5 };

    public float DefaultChannelSeconds => Mathf.Max(0.01f, defaultChannelSeconds);
    public float DefaultHpRestoreFraction => Mathf.Clamp01(defaultHpRestoreFraction);
    public float DefaultImmunitySeconds => Mathf.Max(0f, defaultImmunitySeconds);

    public float CorpsmanChannelSeconds => Mathf.Max(0.01f, corpsmanChannelSeconds);
    public float CorpsmanHpRestoreFraction => Mathf.Clamp01(corpsmanHpRestoreFraction);
    public float CorpsmanImmunitySeconds => Mathf.Max(0f, corpsmanImmunitySeconds);

    /// <summary>
    /// Cariche del pool per il tier Medbay indicato (1-based). Un tier sotto 1 usa T1,
    /// uno oltre la tabella usa l'ultima riga. Tabella vuota → 0 (defib indisponibile).
    /// </summary>
    public int ChargesForMedbayTier(int tier)
    {
        if (chargesByMedbayTier == null || chargesByMedbayTier.Length == 0) return 0;
        int index = Mathf.Clamp(tier - 1, 0, chargesByMedbayTier.Length - 1);
        return Mathf.Max(0, chargesByMedbayTier[index]);
    }
}