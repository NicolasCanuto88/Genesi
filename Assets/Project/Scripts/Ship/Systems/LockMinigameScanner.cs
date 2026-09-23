using System;
using UnityEngine;
using SpaceSurvivor.Poi;
using SpaceSurvivor.Ship.Systems;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// LockMinigameScanner — Rev BK (aggancio "live" di un POI dallo Scanner)
    ///
    /// Derivato SCANNER di ProgressiveMinigame. Riusa il framework D24 (barra +
    /// soglie + mash/slider + lifecycle) SENZA duplicare logica: qui resta solo
    /// lo specifico dell'aggancio.
    ///
    /// WIN-MODE = "repair" (default dei seam base, NESSUN override):
    ///   - start a 0 · sali a 100% = AGGANCIO ACQUISITO (CompletesAtHundred=true)
    ///   - timer scaduto prima del 100% = interruzione (nessun lock) via il
    ///     comportamento base di OnTimerExpired → _onInterrupted
    ///   - nessun floor (FailsAtFloor=false)
    /// È deliberatamente BREVE (time-limit corto, decay moderato): un atto di
    /// acquisizione, non un hold sostenuto (quello è il Path H dell'Ingegnere).
    ///
    /// EFFETTO (server-authority): SOLO alla soglia 100% invia
    /// ScannerSystem.RequestLockRpc(targetId). Le soglie 50/75 sono MUTE (feedback
    /// visivo del marker sì, effetto no) — l'aggancio è un evento singolo, non
    /// progressivo. Il minigame è client-local; l'autorità dello stato locked
    /// resta in ScannerSystem (NetworkBehaviour singleton), esattamente come
    /// RepairMinigameEngineering delega a RepairPanel. Qui il "coordinatore" è il
    /// singleton ScannerSystem: non serve un panel per-istanza (la nave ha un
    /// solo Scanner).
    ///
    /// ⚠️ PALETTO: lo SGANCIO NON passa da qui — è istantaneo (RequestUnlockRpc,
    /// nessun minigame), gestito da ScannerUI. Questo minigame porta SOLO in stato
    /// locked.
    ///
    /// INPUT: riusa le stesse InputActionReference del framework (mashAction /
    /// repairSliderKeys ereditate). Alla postazione Scanner non c'è un altro
    /// minigame aperto in contemporanea → nessun nuovo binding, invariante
    /// input-audit rispettata.
    ///
    /// SETUP IN SCENA:
    ///   Canvas World Space (scala 0.001) figlio del monitor Scanner (stessa
    ///   struttura del Canvas di RepairMinigameEngineering: barra, slider, timer,
    ///   marker). Assegna mashAction / repairSliderKeys (campi ereditati). Assegna
    ///   il riferimento a questo componente su ScannerUI.lockMinigame.
    /// </summary>
    public class LockMinigameScanner : ProgressiveMinigame
    {
        // ── Parametri aggancio (dominio Scanner) ──────────────────────────────

        [Header("Scanner — Aggancio (repair-mode breve)")]
        [Tooltip("Velocità di decay iniziale della barra (pt/s). Moderata: l'atto " +
                 "è breve, non un hold. Il modificatore di ruolo (default 1) la " +
                 "scala nella Fase ruoli.")]
        [Min(0f)]
        [SerializeField] private float lockDecayRate = 2.5f;

        [Tooltip("Time-limit dell'acquisizione (s). Corto: se non raggiungi il " +
                 "100% entro il tempo, l'aggancio fallisce (nessun lock).")]
        [Min(1f)]
        [SerializeField] private float lockTimeLimitSeconds = 15f;

        // ── Stato runtime ─────────────────────────────────────────────────────

        private PoiInstance _target;
        private ulong _targetId;

        // ── Testi (override dei default engineering-repair della base) ────────

        protected override string PromptText => "PREMI E PER AGGANCIARE";
        protected override string CompleteText => "AGGANCIO ACQUISITO!";

        // NB: win-mode = repair puro → NESSUN override di GetStartProgress /
        // CompletesAtHundred / FailsAtFloor (restano ai default della base).

        // ── API pubblica ──────────────────────────────────────────────────────

        /// <summary>
        /// Apre il minigame di aggancio per il POI specificato. Il gate (Scanned +
        /// range) è ri-validato server-side dall'RPC; qui accettiamo un target
        /// valido non-null con NetworkObject spawnato.
        /// </summary>
        public void Open(PoiInstance target, Action onComplete, Action onInterrupted)
        {
            if (target == null || target.NetworkObject == null)
            {
                Debug.LogWarning("[LockMinigameScanner] Open: target null o senza " +
                                 "NetworkObject — ignorato.");
                onInterrupted?.Invoke();
                return;
            }

            _target = target;
            _targetId = target.NetworkObject.NetworkObjectId;

            // Hardening: il framework avvia coroutine (StartCoroutine) in
            // BeginSession → il GameObject DEVE essere attivo. Se a riposo è
            // spento (per nasconderlo), lo riattiviamo qui; Awake della base
            // ri-nasconde rootCanvas, e BeginSession lo riaccende. Così il
            // minigame parte a prescindere dallo stato a riposo del GO.
            // NB: rootCanvas NON deve puntare all'intero monitor, ma al root del
            // minigame (vedi SETUP), altrimenti CloseInternal spegnerebbe tutto.
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            BeginSession(onComplete, onInterrupted);
        }

        // ── Hook dominio ──────────────────────────────────────────────────────

        protected override string GetTargetDisplayName()
            => _target != null && _target.Data != null ? _target.Data.DisplayName : "CONTATTO";

        protected override float GetInitialDecayRate() => lockDecayRate;

        protected override float GetTimeLimitSeconds() => lockTimeLimitSeconds;

        protected override void ApplyThresholdEffect(float pct)
        {
            // L'aggancio è un evento SINGOLO: solo la soglia 100% invia l'RPC.
            // 50/75 restano feedback puramente visivo (marker), nessun effetto.
            if (pct < 100f) return;

            var scanner = ScannerSystem.Instance;
            if (scanner != null)
            {
                scanner.RequestLockRpc(_targetId);
            }
            else
            {
                Debug.LogError("[LockMinigameScanner] ScannerSystem.Instance null — " +
                               "aggancio non instradato.");
            }
        }

        // ── Debug GUI (riga extra dominio) ──────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        protected override void OnDebugGUIExtra()
        {
            GUILayout.Label($"Target id: {_targetId}");
            GUILayout.Label($"Scanner: {(ScannerSystem.Instance != null ? "OK" : "MANCANTE ⚠")}");
        }
#endif
    }
}