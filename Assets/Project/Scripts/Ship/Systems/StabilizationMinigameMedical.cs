using System;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// StabilizationMinigameMedical — minigame di trattamento della Recovery Bay
    /// (Corpsman · Fase 3a). Derivato di ProgressiveMinigame (D24 · Rev BB).
    ///
    /// ⚠️ NON CONFONDERE con StabilizationMinigameEngineering (Path H, stabilizza un
    /// subsystem della nave). Questo cura un GIOCATORE sdraiato sul letto.
    ///
    /// STORIA:
    ///   - Rev BB: placeholder inerte, non in scena.
    ///   - Rev BO-a: ATTIVO. Coordinatore = RecoveryBed (NetworkBehaviour). Usa ancora
    ///     l'interazione di default Mash+Slider della base: la sutura (archetipo 4,
    ///     tracking continuo) arriva in BO-b tramite CreateInteraction() (seam QB).
    ///
    /// WIN-MODE: "repair" (default dei seam base, nessun override): la barra sale da
    /// 0, soglie 50/75/100, al 100% la sessione si chiude con successo.
    ///
    /// EFFETTO DI SOGLIA (Q3-a — soglia come bersaglio): ApplyThresholdEffect(pct)
    /// → RecoveryBed.ApplyTreatmentThresholdRpc(pct). Il server porta gli HP del
    /// paziente ad ALMENO pct% di maxHP. La regola è idempotente: un RPC duplicato non
    /// cura due volte. A T1 si cura solo HP (niente Stati: Veleno/Radiazioni → BP,
    /// Ferite Composte → T3+).
    ///
    /// MALUS REV U (Q6-a): operatore Corpsman = linea di base (1.0 / 1.0). Chiunque
    /// altro (ruolo None compreso) → moltiplicatori di MedbayConfig su decay e punti
    /// positivi. Il ruolo si legge lato client dal proprio PlayerCrewRole (co-op,
    /// nessun anti-cheat — coerente con BM Q2) e si fissa all'apertura della sessione.
    /// Il tetto resta 100% per tutti.
    /// </summary>
    public class StabilizationMinigameMedical : ProgressiveMinigame
    {
        // ── Contesto di sessione (impostato da Open, prima di BeginSession) ──
        private RecoveryBed _bed;
        private MedbayConfig _config;
        private string _patientLabel = "PAZIENTE";
        private bool _operatorIsCorpsman;

        // Testi coerenti col dominio medico (override dei default engineering della base).
        protected override string PromptText => "PREMI E PER CURARE";
        protected override string CompleteText => "PAZIENTE STABILIZZATO!";

        // ── API pubblica ─────────────────────────────────────────────────────────

        /// <summary>
        /// Apre la sessione di trattamento sul client dell'operatore. Chiamato da
        /// RecoveryBed quando il server ha confermato questo client come operatore.
        /// Il ruolo dell'operatore viene fissato qui per tutta la sessione.
        /// </summary>
        public void Open(RecoveryBed bed, MedbayConfig config, string patientLabel,
                         Action onComplete, Action onInterrupted)
        {
            _bed = bed;
            _config = config;
            _patientLabel = string.IsNullOrEmpty(patientLabel) ? "PAZIENTE" : patientLabel;

            var localRole = PlayerCrewRole.LocalInstance;
            _operatorIsCorpsman = localRole != null && localRole.Role == CrewRole.Corpsman;

            if (_bed == null)
                Debug.LogWarning("[StabilizationMinigameMedical] RecoveryBed non assegnato: " +
                                 "le soglie non avranno effetto sul paziente.");
            if (_config == null)
                Debug.LogWarning("[StabilizationMinigameMedical] MedbayConfig non assegnato: " +
                                 "decay 0 e limite di tempo minimo.");

            BeginSession(onComplete, onInterrupted);
        }

        // ── Hook dominio ─────────────────────────────────────────────────────────

        protected override string GetTargetDisplayName() => _patientLabel;

        protected override float GetInitialDecayRate() =>
            _config != null ? _config.TreatmentDecayRate : 0f;

        protected override float GetTimeLimitSeconds() =>
            _config != null ? _config.TreatmentTimeLimitSeconds : 1f;

        protected override float GetRoleDecayMultiplier() =>
            _operatorIsCorpsman || _config == null ? 1f : _config.NonCorpsmanDecayMultiplier;

        protected override float GetRolePointsMultiplier() =>
            _operatorIsCorpsman || _config == null ? 1f : _config.NonCorpsmanPointsMultiplier;

        protected override void ApplyThresholdEffect(float pct)
        {
            if (_bed == null)
            {
                LogVWarn($"[StabilizationMinigameMedical] soglia {pct:F0}% senza RecoveryBed — nessun effetto.");
                return;
            }

            // Server authority: la cura la applica il server (Q3-a, idempotente).
            _bed.ApplyTreatmentThresholdRpc(pct);
            LogV($"[StabilizationMinigameMedical] soglia {pct:F0}% → ApplyTreatmentThresholdRpc");
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        protected override void OnDebugGUIExtra()
        {
            // Hook dichiarato dalla base solo in Editor/Development: stesso guard qui.
            GUILayout.Label(_operatorIsCorpsman ? "Operatore: Corpsman" : "Operatore: non-Corpsman (Rev U)");
            GUILayout.Label($"Ruolo: decay ×{GetRoleDecayMultiplier():F2} · punti ×{GetRolePointsMultiplier():F2}");
        }
#endif
    }
}