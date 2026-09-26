using System;
using SpaceSurvivor.UI;
using UnityEngine;
using UnityEngine.InputSystem;

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
    ///   - Rev BO-a: ATTIVO. Coordinatore = RecoveryBed (NetworkBehaviour), interazione di
    ///     default Mash+Slider (impalcatura temporanea).
    ///   - Rev BO-b: si opera dal monitor della MedicalStation (la console chiama Open).
    ///   - Rev BO-c: SUTURA "segui l'ago" (archetipo 4, tracking continuo) tramite
    ///     CreateInteraction() — primo uso reale del seam QB. Niente barra (Q13-b): la
    ///     ferita si cuce fino al progresso raggiunto, con tacche 50/75.
    ///
    /// WIN-MODE: "repair" (default dei seam base, nessun override): il progresso sale da
    /// 0, soglie 50/75/100, al 100% la sessione si chiude con successo.
    ///
    /// CAMPI DELLA BASE SUL CANVAS MEDICO: barra, marker, progressText e tutti i campi
    /// Mash+Slider restano a None (la base li salta; MashSliderInteraction non nasce mai).
    /// Il progresso lo mostra la sutura, letto da _progress nell'override di Update.
    ///
    /// EFFETTO DI SOGLIA (Q3-a — soglia come bersaglio): ApplyThresholdEffect(pct)
    /// → RecoveryBed.ApplyTreatmentThresholdRpc(pct). Il server porta gli HP del
    /// paziente ad ALMENO pct% di maxHP (idempotente). Qui si pilotano anche gli stadi di
    /// velocità dell'ago (0–50 / 50–75 / 75–100) e le tacche della ferita.
    ///
    /// MALUS REV U (Q6-a di BO-a): operatore Corpsman = linea di base (1.0 / 1.0).
    /// Chiunque altro → moltiplicatori di MedbayConfig su decay e punti positivi (la
    /// perdita fuori bersaglio non è attenuata). Ruolo letto dal proprio PlayerCrewRole e
    /// fissato all'apertura della sessione.
    /// </summary>
    public class StabilizationMinigameMedical : ProgressiveMinigame, ISutureSettings
    {
        [Header("Sutura (Rev BO-c)")]
        [Tooltip("Parametri di gioco della sutura (SO). Se manca si usano i default del codice.")]
        [SerializeField] private SutureTuning sutureTuning;

        [Tooltip("Graphic che disegna ferita, ago e reticolo (dentro SuturePanel, sotto la mask).")]
        [SerializeField] private SutureLineGraphic sutureGraphic;

        // ── Contesto di sessione (impostato da Open, prima di BeginSession) ──
        private RecoveryBed _bed;
        private MedbayConfig _config;
        private string _patientLabel = "PATIENT";
        private bool _operatorIsCorpsman;
        private InputAction _lookAction;

        // Riferimento tipizzato all'interazione (creata lazy dalla base alla prima sessione).
        private SutureInteraction _suture;

        // Testi (Rev BO-c — nuovi testi in inglese; localizzazione IT a parte).
        // I testi della base (grace, soglia, tempo scaduto) restano per ora in italiano (Q16-a).
        protected override string PromptText => "FOLLOW THE NEEDLE";
        protected override string CompleteText => "PATIENT STABILIZED";

        // ── API pubblica ─────────────────────────────────────────────────────────

        /// <summary>
        /// Apre la sessione di trattamento sul client dell'operatore. Chiamato dalla
        /// MedicalStation quando il server ha confermato questo client come operatore.
        /// lookAction = azione Look del PlayerInput del giocatore seduto (Q6-a): muove il
        /// reticolo. Il ruolo dell'operatore viene fissato qui per tutta la sessione.
        /// </summary>
        public void Open(RecoveryBed bed, MedbayConfig config, string patientLabel,
                         InputAction lookAction, Action onComplete, Action onInterrupted)
        {
            _bed = bed;
            _config = config;
            _patientLabel = string.IsNullOrEmpty(patientLabel) ? "PATIENT" : patientLabel;
            _lookAction = lookAction;

            var localRole = PlayerCrewRole.LocalInstance;
            _operatorIsCorpsman = localRole != null && localRole.Role == CrewRole.Corpsman;

            if (_bed == null)
                Debug.LogWarning("[StabilizationMinigameMedical] RecoveryBed non assegnato: " +
                                 "le soglie non avranno effetto sul paziente.");
            if (_config == null)
                Debug.LogWarning("[StabilizationMinigameMedical] MedbayConfig non assegnato: " +
                                 "decay 0 e limite di tempo minimo.");
            if (_lookAction == null)
                Debug.LogWarning("[StabilizationMinigameMedical] Azione Look non passata: " +
                                 "il reticolo della sutura resta fermo.");

            BeginSession(onComplete, onInterrupted);
        }

        // ── Seam QB ──────────────────────────────────────────────────────────────

        protected override IMinigameInteraction CreateInteraction()
        {
            _suture = new SutureInteraction(this);
            return _suture;
        }

        // ── Q13-b — la ferita al posto della barra ───────────────────────────────

        protected override void Update()
        {
            base.Update();

            // Se base.Update ha chiuso la sessione (100%, timer) il canvas è già spento.
            if (_isActive && _suture != null)
                _suture.SetProgress(_progress / 100f);
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
            // Vista e ritmo della sutura (chiamato DENTRO ApplyPoints, cioè dentro il Tick
            // della sutura: i due metodi scrivono solo campi → sicuri in rientranza).
            if (_suture != null)
            {
                _suture.MarkThresholdReached(pct);
                _suture.SetSpeedStage(pct >= 75f ? 2 : pct >= 50f ? 1 : 0);
            }

            if (_bed == null)
            {
                LogVWarn($"[StabilizationMinigameMedical] soglia {pct:F0}% senza RecoveryBed — nessun effetto.");
                return;
            }

            // Server authority: la cura la applica il server (Q3-a, idempotente).
            _bed.ApplyTreatmentThresholdRpc(pct);
            LogV($"[StabilizationMinigameMedical] soglia {pct:F0}% → ApplyTreatmentThresholdRpc");
        }

        // ── ISutureSettings (letti al momento dell'uso) ──────────────────────────

        SutureTuning ISutureSettings.Tuning => sutureTuning;
        SutureLineGraphic ISutureSettings.Graphic => sutureGraphic;
        InputAction ISutureSettings.LookAction => _lookAction;
        Color ISutureSettings.NotchDefaultColor => colorMarkerDefault;

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