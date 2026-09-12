using UnityEngine;

/// <summary>
/// PersonalOxygenConfig — parametri di tuning del TANK PERSONALE (D26, Rev BE).
/// ScriptableObject secondo la convenzione del progetto ("tutti i valori numerici
/// di tuning vengono da SO, mai hardcodati"). Stesso modello di DefibConfig (Rev BD).
///
/// NAMESPACE GLOBALE: coerenza-di-dominio con i sibling player (PlayerHealthSystem /
/// PlayerStatusEffects / DefibConfig sono globali — deviazione già documentata in
/// Rev BC/BD). Il tank personale è dominio player.
///
/// MODELLO (Q2-b, Fase 1): il tank personale è la riserva O2 della tuta.
/// - A BORDO (atmosfera nave) → si RICARICA passivamente verso il massimo.
/// - FUORI (EVA / relitto senza O2) → si CONSUMA.
/// Lo stato "fuori" NON esiste ancora in Fase 1 (EVA = D31, accesso relitto = D22):
/// il consumo è quindi un SEAM guidato da PlayerOxygen.SetConsuming(bool), che i
/// futuri sistemi EVA/relitto chiameranno. In Fase 1 nessun chiamante reale → il
/// tank resta pieno (refill passivo), salvo il drain di debug per validare il refill.
///
/// I due rate qui sotto sono VALORI DI TUNING PLACEHOLDER: il refill è già attivo
/// in Fase 1; il consumo si tara davvero quando EVA/relitto esisteranno. Nessuna
/// "progettazione di sistema prima del suo milestone": qui c'è solo tuning data.
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/Personal Oxygen Config", fileName = "PersonalOxygenConfig")]
public class PersonalOxygenConfig : ScriptableObject
{
    [Header("Capacità")]
    [Tooltip("Livello massimo del tank personale (unità coerenti con la percentuale 0-100, come l'O2 nave).")]
    [SerializeField] private float maxLevel = 100f;

    [Header("Ricarica (a bordo) — ATTIVA in Fase 1")]
    [Tooltip("Ricarica del tank personale mentre il crew è a bordo, in unità/minuto. " +
             "PLACEHOLDER di tuning: default 200/min → pieno (0→100) in ~30s al rientro.")]
    [SerializeField] private float rechargePerMinute = 200f;

    [Header("Consumo (fuori) — SEAM: attivo solo in EVA/relitto (D31/D22)")]
    [Tooltip("Consumo del tank personale mentre si è fuori dall'atmosfera, in unità/minuto. " +
             "PLACEHOLDER di tuning: default 20/min → ~5 min di autonomia (100→0). " +
             "In Fase 1 questo rate è esercitato SOLO dal drain di debug; la tara reale " +
             "arriva quando EVA/relitto esisteranno e chiameranno SetConsuming(true).")]
    [SerializeField] private float consumptionPerMinute = 20f;

    // ===== Getter pubblici (rate già convertiti in unità/secondo) =====

    public float MaxLevel => maxLevel;
    public float RechargePerSecond => rechargePerMinute / 60f;
    public float ConsumptionPerSecond => consumptionPerMinute / 60f;
}
