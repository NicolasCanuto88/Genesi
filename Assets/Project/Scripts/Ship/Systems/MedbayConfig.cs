using System;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// MedbayConfig — parametri di tuning della Recovery Bay (Rev BO-a · Fase 3a Corpsman).
    /// ScriptableObject secondo la convenzione del progetto: nessun valore numerico
    /// di tuning o di upgrade hardcodato nel codice.
    ///
    /// CONTENUTO:
    ///   - Tabella per tier (Q4): cosa fa il letto da solo a ciascun livello del
    ///     modulo Medbay. Il tier corrente viene da MedbaySystem (NetworkVariable).
    ///     A oggi solo T1 è di design (auto-cura fino al 50% degli HP massimi,
    ///     GDD v0.9.39 Q4-a); T2–T4 sono SEGNAPOSTO uguali a T1, da definire in BP.
    ///   - Auto-cura (Q5-a): cadenza dei tick server.
    ///   - Trattamento (minigame medico): decay e limite di tempo della sessione.
    ///   - Malus Rev U (Q6-a): moltiplicatori per un operatore NON-Corpsman. Il
    ///     Corpsman è la linea di base (1.0 / 1.0), come nel profilo defib.
    ///
    /// NON QUI (di proposito):
    ///   - Le soglie di cura: con Q3-a la soglia N% porta gli HP almeno a N% di maxHP.
    ///     È la definizione della regola, non un valore di tuning.
    ///   - Il costo di upgrade T2: si definisce in BP contro l'income missioni
    ///     (800–2.000 cr lordi) e dipende dal Blocco 5 (upgrade nave).
    ///   - I parametri della sutura (archetipo 4): arrivano in BO-b.
    /// </summary>
    [CreateAssetMenu(menuName = "SpaceSurvivor/Medbay Config", fileName = "MedbayConfig")]
    public class MedbayConfig : ScriptableObject
    {
        /// <summary>Valori del letto per un singolo tier del modulo Medbay.</summary>
        [Serializable]
        public struct TierData
        {
            [Tooltip("Tetto dell'auto-cura come frazione degli HP massimi (0..1). " +
                     "GDD v0.9.39 Q4-a: senza Corpsman il letto porta gli HP solo fino al 50% (0.5).")]
            [Range(0f, 1f)]
            [SerializeField] private float autoHealCapFraction;

            [Tooltip("Velocità dell'auto-cura in HP al secondo (sotto il tetto). " +
                     "1.0 con maxHP 100: da 20% a 50% in ~30 s.")]
            [Min(0f)]
            [SerializeField] private float autoHealPerSecond;

            public TierData(float capFraction, float perSecond)
            {
                autoHealCapFraction = capFraction;
                autoHealPerSecond = perSecond;
            }

            public float AutoHealCapFraction => Mathf.Clamp01(autoHealCapFraction);
            public float AutoHealPerSecond => Mathf.Max(0f, autoHealPerSecond);
        }

        [Header("Tabella per tier — indice 0 = T1 (Q4)")]
        [Tooltip("Una riga per tier del modulo Medbay (T1..T4). T2–T4 sono SEGNAPOSTO uguali a T1 " +
                 "finché BP non li definisce. Un tier oltre la tabella usa l'ultima riga.")]
        [SerializeField] private TierData[] tiers =
        {
            new TierData(0.5f, 1f),   // T1 — di design (Q4-a)
            new TierData(0.5f, 1f),   // T2 — segnaposto (BP)
            new TierData(0.5f, 1f),   // T3 — segnaposto
            new TierData(0.5f, 1f)    // T4 — segnaposto
        };

        [Header("Auto-cura — Q5-a")]
        [Tooltip("Cadenza dei tick di auto-cura sul server, in secondi. Ogni tick applica " +
                 "autoHealPerSecond × intervallo. Un tick ogni 0.5 s evita di scrivere la " +
                 "NetworkVariable degli HP a ogni frame.")]
        [SerializeField] private float autoHealTickInterval = 0.5f;

        [Header("Trattamento — minigame medico")]
        [Tooltip("Decay della barra del trattamento in punti al secondo (linea di base Corpsman).")]
        [SerializeField] private float treatmentDecayRate = 2f;

        [Tooltip("Durata massima della sessione di trattamento, in secondi. Allo scadere la " +
                 "sessione si chiude e le soglie già raggiunte restano (come la riparazione).")]
        [SerializeField] private float treatmentTimeLimitSeconds = 60f;

        [Header("Malus Rev U — operatore NON-Corpsman (Q6-a)")]
        [Tooltip("Moltiplicatore del decay per un operatore non-Corpsman (>1 = più difficile). " +
                 "Il ruolo non dichiarato (None) conta come non-Corpsman.")]
        [SerializeField] private float nonCorpsmanDecayMultiplier = 1.3f;

        [Tooltip("Moltiplicatore dei punti guadagnati per un operatore non-Corpsman (<1 = più lento). " +
                 "Si applica ai soli punti positivi (IMinigameHost.ApplyPoints).")]
        [SerializeField] private float nonCorpsmanPointsMultiplier = 0.75f;

        // ── Accesso ────────────────────────────────────────────────────────────

        /// <summary>
        /// Valori del tier indicato (1-based). Un tier sotto 1 usa T1, uno oltre la
        /// tabella usa l'ultima riga. Tabella vuota → letto inerte (0 / 0).
        /// </summary>
        public TierData GetTier(int tier)
        {
            if (tiers == null || tiers.Length == 0) return new TierData(0f, 0f);
            int index = Mathf.Clamp(tier - 1, 0, tiers.Length - 1);
            return tiers[index];
        }

        public int TierCount => tiers != null ? tiers.Length : 0;

        public float AutoHealTickInterval => Mathf.Max(0.05f, autoHealTickInterval);
        public float TreatmentDecayRate => Mathf.Max(0f, treatmentDecayRate);
        public float TreatmentTimeLimitSeconds => Mathf.Max(1f, treatmentTimeLimitSeconds);
        public float NonCorpsmanDecayMultiplier => Mathf.Max(0f, nonCorpsmanDecayMultiplier);
        public float NonCorpsmanPointsMultiplier => Mathf.Max(0f, nonCorpsmanPointsMultiplier);
    }
}
