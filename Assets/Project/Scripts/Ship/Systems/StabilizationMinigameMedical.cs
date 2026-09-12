using System;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// StabilizationMinigameMedical — Framework Minigame Progressivo (D24 · Rev BB)
    ///
    /// PLACEHOLDER derivato Medicina (Corpsman) di ProgressiveMinigame.
    /// Struttura in piedi per validare la generalizzazione D24; l'implementazione
    /// piena (target paziente, coordinatore server, effetti di stabilizzazione,
    /// interazione definitiva) arriva nella FASE CORPSMAN (ROADMAP_M4_v2 §3).
    ///
    /// Stato attuale: eredita l'interazione mash+slider di default dalla base e
    /// NON applica alcun effetto server (nessun coordinatore medico esiste ancora).
    /// ApplyThresholdEffect è volutamente inerte (solo log verboso).
    ///
    /// ⚠️ NON assegnare a un GameObject in scena finché la Fase Corpsman non
    /// definisce target/coordinatore: senza effetto, il 100% chiude soltanto la UI.
    /// </summary>
    public class StabilizationMinigameMedical : ProgressiveMinigame
    {
        [Header("Medical — Placeholder (Fase Corpsman)")]
        [Tooltip("Etichetta header provvisoria finché non esiste un target paziente.")]
        [SerializeField] private string placeholderLabel = "STABILIZZAZIONE";

        [Tooltip("Decay iniziale provvisorio. Sostituito dalla logica medica in Fase Corpsman.")]
        [SerializeField] private float placeholderDecayRate = 1.0f;

        [Tooltip("Limite di tempo provvisorio (s). Sostituito in Fase Corpsman.")]
        [SerializeField] private float placeholderTimeLimit = 90f;

        // Testi coerenti col dominio medico (override dei default engineering della base).
        protected override string PromptText => "PREMI E PER STABILIZZARE";
        protected override string CompleteText => "PAZIENTE STABILIZZATO!";

        // ── API pubblica (provvisoria) ──────────────────────────────────────────

        /// <summary>
        /// Avvio provvisorio del minigame medico. Firma minimale finché la Fase
        /// Corpsman non introduce il target paziente e il coordinatore server.
        /// </summary>
        public void Open(Action onComplete, Action onInterrupted)
        {
            BeginSession(onComplete, onInterrupted);
        }

        // ── Hook dominio (placeholder) ──────────────────────────────────────────

        protected override string GetTargetDisplayName() => placeholderLabel;

        protected override float GetInitialDecayRate() => placeholderDecayRate;

        protected override float GetTimeLimitSeconds() => placeholderTimeLimit;

        protected override void ApplyThresholdEffect(float pct)
        {
            // TODO (Fase Corpsman): instradare al coordinatore medico server-authority
            // (analogo di RepairPanel.ApplyRepairThresholdRpc) per applicare la
            // stabilizzazione con server authority. Oggi inerte per design.
            LogV($"[StabilizationMinigameMedical] soglia {pct:F0}% — " +
                 "effetto medico non ancora implementato (Fase Corpsman).");
        }
    }
}
