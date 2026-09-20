using System;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// StabilizationMinigameEngineering — Rev BI (Path H · "hold-against-decay")
    ///
    /// Derivato INGEGNERIA di ProgressiveMinigame per la STABILIZZAZIONE di un
    /// subsystem nave che sta cedendo. Meccanicamente DISTINTO dal repair:
    ///   - start NON-zero (il subsystem parte già instabile, non a 0)
    ///   - decay aggressivo che tira la barra verso il basso
    ///   - FLOOR: se la barra tocca il floor, il subsystem è perso → fallimento
    ///   - WIN-MODE invertito: sopravvivere fino allo scadere del timer = successo
    ///     (il 100% NON conclude in anticipo — è solo buffer massimo)
    ///
    /// ⚠️ PALETTO (lockato in Rev BF): questa è una classe SEPARATA da
    /// StabilizationMinigameMedical (placeholder Corpsman, dominio Fase 3, modello
    /// "sali a 100%"). Non condividono nulla oltre la base ProgressiveMinigame.
    ///
    /// La logica di crisi vive qui; l'AUTORITÀ dell'esito (effetto sul subsystem)
    /// resta nel coordinatore server StabilizationPanel, come RepairMinigame delega
    /// a RepairPanel. Il minigame non tocca mai direttamente il subsystem.
    ///
    /// SCOPE Rev BI (Q3-c): la meccanica + la UI sono complete e giocabili; il
    /// TRIGGER dal mondo (quale stato "acuto" arma la stabilizzazione) è armato da
    /// un flag di debug su StabilizationPanel, in attesa dei fallimenti a cascata
    /// del Combat (M4.7) — stesso pattern di debugStartTier sullo Scanner.
    ///
    /// SETUP IN SCENA:
    ///   Canvas World Space (scala 0.001) figlio dello StabilizationPanel, con la
    ///   STESSA struttura del Canvas di RepairMinigameEngineering (barra, slider,
    ///   timer, marker) + un marker "floor" statico posizionato al floor %.
    ///   Assegna mashAction / repairSliderKeys (campi ereditati) e stabilizationPanel.
    /// </summary>
    public class StabilizationMinigameEngineering : ProgressiveMinigame
    {
        // ── Network — coordinatore server per l'esito ─────────────────────────

        [Header("Engineering — Network")]
        [Tooltip("StabilizationPanel su questo stesso GameObject (o padre). " +
                 "Riceve l'esito (successo/perso) con autorità server.")]
        [SerializeField] private StabilizationPanel stabilizationPanel;

        // ── Parametri hold-against-decay (dominio Ingegneria) ─────────────────

        [Header("Engineering — Stabilizzazione (hold-against-decay)")]
        [Tooltip("Progresso iniziale della barra (0–100). Il subsystem parte già " +
                 "instabile, non a zero.")]
        [SerializeField, Range(0f, 100f)] private float startProgress = 45f;

        [Tooltip("Floor (0–100): se la barra lo tocca, il subsystem è perso. " +
                 "Deve stare sotto startProgress.")]
        [SerializeField, Range(0f, 100f)] private float floorProgress = 15f;

        [Tooltip("Durata (s) da tenere contro il decay. Allo scadere = successo.")]
        [SerializeField] private float holdDurationSeconds = 25f;

        [Tooltip("Velocità di decay durante il hold. Più alta = più difficile tenere.")]
        [SerializeField] private float stabilizationDecayRate = 4f;

        // ── Stato runtime ─────────────────────────────────────────────────────

        private IRepairable _target;

        // ── Testi (override dei default engineering-repair della base) ────────

        protected override string PromptText => "PREMI E PER STABILIZZARE";
        protected override string CompleteText => "SUBSYSTEM STABILIZZATO!";

        // ── Win-mode Path H (override dei seam base, tutti default = repair) ───

        protected override float GetStartProgress() => startProgress;
        protected override bool CompletesAtHundred => false;   // vince solo il timer
        protected override bool FailsAtFloor => true;          // floor = subsystem perso
        protected override float GetFloorProgress() => floorProgress;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();

            if (stabilizationPanel == null)
                stabilizationPanel = GetComponentInParent<StabilizationPanel>();

            if (stabilizationPanel == null)
                Debug.LogWarning("[StabilizationMinigameEngineering] StabilizationPanel non trovato — " +
                                 "l'esito non sarà instradato con autorità server.");

            if (floorProgress >= startProgress)
                Debug.LogWarning("[StabilizationMinigameEngineering] floorProgress >= startProgress: " +
                                 "la sessione fallirebbe all'istante. Correggi in Inspector.");
        }

        // ── API pubblica ──────────────────────────────────────────────────────

        /// <summary>
        /// Apre il minigame di stabilizzazione per il subsystem target.
        /// panel: lo StabilizationPanel che coordina l'esito server-side.
        /// </summary>
        public void Open(IRepairable target, StabilizationPanel panel,
                         Action onComplete, Action onInterrupted)
        {
            if (target == null) return;

            _target = target;

            if (panel != null) stabilizationPanel = panel;

            BeginSession(onComplete, onInterrupted);
        }

        // ── Hook dominio ──────────────────────────────────────────────────────

        protected override string GetTargetDisplayName()
            => _target != null ? _target.GetSystemName() : "SUBSYSTEM";

        protected override float GetInitialDecayRate() => stabilizationDecayRate;

        protected override float GetTimeLimitSeconds() => holdDurationSeconds;

        protected override void ApplyThresholdEffect(float pct)
        {
            // Hold-mode: NESSUN consumo/effetto a soglia. La stabilizzazione riuscita
            // è l'atto di sopravvivere al timer (OnTimerExpired). Le soglie 50/75/100
            // restano solo come feedback visivo (marker + status della base).
            //
            // Distinzione dal placeholder medico: lì ApplyThresholdEffect è inerte
            // perché NON ancora implementato; qui è inerte PER DESIGN (il modello
            // hold non consuma a soglia). Se in futuro servisse un effetto parziale,
            // instradarlo a stabilizationPanel via RPC — non toccare il subsystem qui.
        }

        // ── Terminali Path H ──────────────────────────────────────────────────

        /// <summary>
        /// Path H: allo scadere del timer il subsystem ha retto → SUCCESSO.
        /// Inverte il terminale del repair (dove timer scaduto = fallimento).
        /// </summary>
        protected override void OnTimerExpired()
        {
            SetStatus(CompleteText, colorGood);
            if (stabilizationPanel != null)
                stabilizationPanel.NotifyStabilizationOutcomeRpc(true);
            CloseInternal();
            _onComplete?.Invoke();
        }

        /// <summary>
        /// Floor sfondato: il subsystem è andato oltre il punto di tenuta → perso.
        /// Aggiunge il feedback e delega la chiusura-interruzione alla base.
        /// </summary>
        protected override void OnFloorBreached()
        {
            SetStatus("SUBSYSTEM PERSO", colorCritical);
            if (stabilizationPanel != null)
                stabilizationPanel.NotifyStabilizationOutcomeRpc(false);
            base.OnFloorBreached(); // CloseInternal + _onInterrupted
        }

        // ── Debug GUI (riga extra dominio) ──────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        protected override void OnDebugGUIExtra()
        {
            GUILayout.Label($"Floor: {floorProgress:F0} · Start: {startProgress:F0}");
            GUILayout.Label($"Panel: {(stabilizationPanel != null ? "OK" : "MANCANTE ⚠")}");
        }
#endif
    }
}
