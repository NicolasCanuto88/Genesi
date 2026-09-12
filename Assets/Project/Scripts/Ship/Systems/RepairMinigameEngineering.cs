using System;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// RepairMinigameEngineering — Framework Minigame Progressivo (D24 · Rev BB)
    ///
    /// Derivato Ingegneria di ProgressiveMinigame. Rifattorizzazione di
    /// RepairMinigame (M2): la logica condivisa (barra, soglie, mash+slider,
    /// lifecycle) vive ora nella base. Qui resta SOLO lo specifico Ingegneria:
    ///   - target IRepairable + coordinatore server RepairPanel
    ///   - mappatura decay/tempo dallo ShipSystemState
    ///   - effetto di soglia → RepairPanel.ApplyRepairThresholdRpc (server authority)
    ///
    /// COMPORTAMENTO INVARIATO rispetto a RepairMinigame (gate di regressione D24):
    /// stessi punti mash/slider, stesse soglie 50/75/100, stessa invariante materiali
    /// (solo al superamento soglia, mai all'avvio né su interruzione), stesso RPC,
    /// stessi limiti di tempo per stato, stesso grace period.
    ///
    /// MULTIPLAYER (Rev M): MonoBehaviour locale. L'autorità resta in RepairPanel
    /// (NetworkBehaviour) via ApplyRepairThresholdRpc — il minigame non tocca mai
    /// direttamente InventorySystem / IRepairable.
    ///
    /// SETUP IN SCENA:
    ///   Canvas World Space (scala 0.001) figlio del RepairPanel.
    ///   Assegna mashAction e repairSliderKeys dall'InputActions asset (campi ereditati).
    ///   Assegna repairPanel (stesso GameObject o padre — sempre presente).
    /// </summary>
    public class RepairMinigameEngineering : ProgressiveMinigame
    {
        // ── Network — riferimento al RepairPanel per RPC ──────────────────────

        [Header("Engineering — Network")]
        [Tooltip("RepairPanel su questo stesso GameObject (o padre). " +
                 "Usato per ApplyRepairThresholdRpc — obbligatorio per multiplayer.")]
        [SerializeField] private RepairPanel repairPanel;

        // ── Timer per stato (dominio Ingegneria) ──────────────────────────────

        [Header("Engineering — Timer per stato")]
        [SerializeField] private float timeLimitDegradedLight = 120f;
        [SerializeField] private float timeLimitDegradedHeavy = 90f;
        [SerializeField] private float timeLimitOffline = 60f;

        // ── Stato runtime ─────────────────────────────────────────────────────

        private IRepairable _target;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();

            // Auto-cerca RepairPanel se non assegnato in Inspector
            if (repairPanel == null)
                repairPanel = GetComponentInParent<RepairPanel>();

            if (repairPanel == null)
                Debug.LogWarning("[RepairMinigameEngineering] RepairPanel non trovato — " +
                                 "le riparazioni non saranno sincronizzate in multiplayer.");
        }

        // ── API pubblica ──────────────────────────────────────────────────────

        /// <summary>
        /// Apre il minigame per il sistema specificato.
        /// panel: il RepairPanel che coordina l'RPC server-side.
        /// </summary>
        public void Open(IRepairable target, RepairPanel panel,
                         Action onComplete, Action onInterrupted)
        {
            if (target == null || !target.IsRepairable()) return;

            _target = target;

            // Usa il panel passato esplicitamente (priorità su quello serializzato)
            if (panel != null) repairPanel = panel;

            BeginSession(onComplete, onInterrupted);
        }

        // ── Hook dominio ──────────────────────────────────────────────────────

        protected override string GetTargetDisplayName() => _target.GetSystemName();

        protected override float GetInitialDecayRate()
            => _target.GetCurrentState().GetBarDecayRate();

        protected override float GetTimeLimitSeconds()
            => GetTimeLimit(_target.GetCurrentState());

        protected override void ApplyThresholdEffect(float pct)
        {
            // Delega consumo materiali + riparazione al server via RPC.
            if (repairPanel != null)
            {
                repairPanel.ApplyRepairThresholdRpc(pct);
            }
            else
            {
                Debug.LogError("[RepairMinigameEngineering] repairPanel null — RPC non inviato. " +
                               "Assegna RepairPanel in Inspector o sulla stessa gerarchia.");
            }
        }

        // ── Helper engineering ──────────────────────────────────────────────────

        private float GetTimeLimit(ShipSystemState state) => state switch
        {
            ShipSystemState.DegradedLight => timeLimitDegradedLight,
            ShipSystemState.DegradedHeavy => timeLimitDegradedHeavy,
            ShipSystemState.Offline => timeLimitOffline,
            _ => 120f
        };

        // ── Debug GUI (riga extra dominio) ──────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        protected override void OnDebugGUIExtra()
        {
            GUILayout.Label($"Panel RPC: {(repairPanel != null ? "OK" : "MANCANTE ⚠")}");
        }
#endif
    }
}
