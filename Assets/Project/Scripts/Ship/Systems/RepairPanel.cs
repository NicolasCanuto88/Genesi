using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// RepairPanel — Milestone 2
    /// Pannello fisico interagibile nella nave che apre il RepairMinigame.
    ///
    /// RESPONSABILITÀ:
    ///   - IInteractable: rilevato da InteractionSystem via raycast
    ///   - CanInteract() → true solo se:
    ///       1. il sistema target è DEGRADED/OFFLINE (IsRepairable())
    ///       2. il minigame non è già in corso
    ///       3. i materiali per TUTTE le soglie (50+75+100, sommati) sono
    ///          disponibili — HasMaterialsForFullRepair() (gate cumulativo)
    ///   - Interact() → disabilita PlayerController + apre RepairMinigame
    ///   - Cancel → chiude minigame e ripristina il player
    ///   - ApplyRepairThresholdRpc() → RPC server-side che consuma materiali
    ///     e applica la riparazione con SOGLIE RELATIVE ALLA SESSIONE.
    ///
    /// MODELLO SOGLIE RELATIVE ALLA SESSIONE (Rev P):
    ///   Ogni soglia dà un guadagno HP proporzionale al deficit ALL'INIZIO
    ///   della sessione corrente, non un target assoluto sul maxHealth.
    ///
    ///   Esempio: HP = 60/100 (deficit 40)
    ///     soglia 50% → +20 → HP 80
    ///     soglia 75% → +30 → HP 90
    ///     soglia 100% → +40 → HP 100
    ///
    ///   Implementazione: questo pannello cattura _sessionStartPercent
    ///   (HP normalizzato 0-1) alla PRIMA soglia della sessione (quella con
    ///   progress più basso — tipicamente 50%), poi per ogni soglia calcola:
    ///
    ///     adjustedProgress = (_sessionStartPercent
    ///                          + (1 - _sessionStartPercent) * (progressPct/100))
    ///                        * 100
    ///
    ///   e lo passa a IRepairable.ApplyRepair(adjustedProgress). Poiché
    ///   ApplyRepair fa già targetHP = maxHealth * (adjustedProgress/100),
    ///   ZERO modifiche a PropulsionSystem.cs / FTLDrive.cs — l'aggiustamento
    ///   è interamente qui.
    ///
    /// GATE CUMULATIVO (Rev P):
    ///   Con soglie relative, OGNI soglia dà sempre un guadagno reale →
    ///   una sessione completata al 100% attraversa SEMPRE tutte e 3 le
    ///   soglie e consuma SEMPRE tutti i loro materiali. Il gate quindi
    ///   richiede la SOMMA dei materiali di tutte le soglie (vedi
    ///   IRepairableExtensions.HasMaterialsForFullRepair). Se mancano,
    ///   CanInteract() == false — InteractionSystem non mostra alcun prompt
    ///   (pattern esistente, nessuna modifica a InteractionSystem.cs).
    ///
    /// SETUP IN SCENA:
    ///   1. Crea un GameObject sul pannello fisico (parete della nave)
    ///   2. Aggiungi NetworkObject component (⚠ obbligatorio — RepairPanel è NetworkBehaviour)
    ///   3. Aggiungi Collider (per raycast InteractionSystem)
    ///   4. Assegna il sistema IRepairable target (es. PropulsionSystem)
    ///   5. Assegna il RepairMinigame (figlio di questo GameObject)
    ///   6. Assegna PlayerInput reference (stessa dell'EngineeringStation)
    ///   7. Registra il prefab / GameObject nella lista NetworkPrefabs del NetworkManager
    /// </summary>
    public class RepairPanel : NetworkBehaviour, IInteractable
    {
        [Header("Target System")]
        [Tooltip("Il sistema nave che questo pannello ripara. Deve implementare IRepairable.")]
        [SerializeField] private MonoBehaviour repairableTarget;

        [Header("Minigame")]
        [Tooltip("Il RepairMinigame su questo pannello (di solito figlio di questo GameObject).")]
        [SerializeField] private RepairMinigame repairMinigame;

        [Header("Input")]
        [Tooltip("Stessa referenza PlayerInput usata nelle altre stazioni.")]
        [SerializeField] private PlayerInput playerInputReference;

        [Header("Prompt")]
        [SerializeField] private string interactionPrompt = "Ripara sistema";

        // ── Stato runtime (client/UI) ────────────────────────────────────────
        private PlayerController _playerController;
        private CharacterController _characterController;
        private float _cooldown;
        private InputAction _cancelAction;
        private bool _isActive;

        // ── Stato runtime (server — sessione di riparazione) ─────────────────
        // HP normalizzato (0-1) all'inizio della sessione corrente.
        // Catturato alla prima soglia (progress più basso) e riusato per le
        // soglie successive della stessa sessione. -1 = nessuna sessione attiva.
        private float _sessionStartPercent = -1f;

        // IRepairable cachata
        private IRepairable _repairable;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _repairable = repairableTarget as IRepairable;

            if (_repairable == null)
                Debug.LogWarning($"[RepairPanel] {name}: repairableTarget non implementa IRepairable.");

            if (repairMinigame == null)
                Debug.LogWarning($"[RepairPanel] {name}: RepairMinigame non assegnato.");
        }

        private void Update()
        {
            if (_cooldown > 0f) _cooldown -= Time.deltaTime;

            if (_isActive && _cancelAction != null && _cancelAction.WasPressedThisFrame())
                ExitRepair();
        }

        // ── IInteractable ─────────────────────────────────────────────────────

        /// <summary>
        /// Gate a tre livelli:
        ///   1. Sistema DEGRADED/OFFLINE (IsRepairable)
        ///   2. Minigame non già in corso / cooldown scaduto
        ///   3. Materiali per TUTTE le soglie, sommati (gate cumulativo)
        ///
        /// Se (3) è false, CanInteract() ritorna false e InteractionSystem
        /// non mostra alcun prompt — il giocatore deve consultare Monitor 2
        /// Sezione B per scoprire cosa manca.
        /// </summary>
        public bool CanInteract()
            => !_isActive && _cooldown <= 0f
            && _repairable != null
            && _repairable.IsRepairable()
            && _repairable.HasMaterialsForFullRepair();

        public string GetInteractionPrompt()
        {
            if (_repairable == null) return interactionPrompt;

            if (!_repairable.IsRepairable())
                return $"{_repairable.GetSystemName()} — Operativo";

            if (!_repairable.HasMaterialsForFullRepair())
                return $"{_repairable.GetSystemName()} — Materiali insufficienti (vedi Monitor 2)";

            return $"Ripara {_repairable.GetSystemName()} [{_repairable.GetCurrentState()}]";
        }

        public void Interact(GameObject interactor)
        {
            if (!CanInteract()) return;
            EnterRepair(interactor);
        }

        public bool IsContinuousInteraction() => false;
        public void OnLookEnter() { }
        public void OnLookExit() { }

        // ── Enter / Exit ──────────────────────────────────────────────────────

        private void EnterRepair(GameObject interactor)
        {
            _playerController = interactor.GetComponent<PlayerController>();
            _characterController = interactor.GetComponent<CharacterController>();

            // Recupera Cancel action da PlayerInput (mai hardcodato)
            PlayerInput pi = playerInputReference != null
                ? playerInputReference
                : interactor.GetComponent<PlayerInput>();

            if (pi != null)
                _cancelAction = pi.actions["Cancel"];

            // Disabilita movimento player
            if (_playerController != null) _playerController.enabled = false;
            if (_characterController != null) _characterController.enabled = false;

            _isActive = true;

            // Apri minigame — passa riferimento a questo RepairPanel per l'RPC
            repairMinigame?.Open(_repairable, this, OnMinigameComplete, OnMinigameInterrupted);
        }

        private void ExitRepair()
        {
            _isActive = false;
            _cooldown = 0.5f;

            // Ripristina player
            if (_playerController != null) _playerController.enabled = true;
            if (_characterController != null) _characterController.enabled = true;

            repairMinigame?.Interrupt();
        }

        private void OnMinigameComplete()
        {
            _isActive = false;
            _cooldown = 1.0f;

            if (_playerController != null) _playerController.enabled = true;
            if (_characterController != null) _characterController.enabled = true;
        }

        private void OnMinigameInterrupted()
        {
            // Il minigame è stato interrotto internamente (es. sistema tornato ONLINE)
            ExitRepair();
        }

        // ── RPC Server-Side — soglie relative alla sessione ───────────────────

        /// <summary>
        /// Chiamato da RepairMinigame quando il giocatore supera una soglia
        /// (progressPct = 50, 75 o 100 — valore RAW del minigame).
        /// Eseguito SEMPRE sul server, indipendentemente da quale client ha giocato.
        ///
        /// STEP 1 — Sessione: se progressPct corrisponde alla soglia più bassa
        ///          (GetFirstThreshold), cattura _sessionStartPercent = HP
        ///          normalizzato ATTUALE. Questo è l'inizio di una nuova sessione.
        ///
        /// STEP 2 — Consumo materiali per QUESTA soglia (TryConsume, server-side).
        ///          Con il gate cumulativo all'ingresso, questo dovrebbe sempre
        ///          riuscire — un fallimento qui indica una race condition reale
        ///          (altro pannello ha consumato lo stesso materiale nel frattempo).
        ///
        /// STEP 3 — Calcola adjustedProgress relativo a _sessionStartPercent e
        ///          chiama IRepairable.ApplyRepair(adjustedProgress).
        ///          ApplyRepair non cambia: targetHP = maxHealth * (adjusted/100).
        /// </summary>
        [Rpc(SendTo.Server)]
        public void ApplyRepairThresholdRpc(float progressPct)
        {
            if (_repairable == null)
            {
                Debug.LogWarning("[RepairPanel] RPC: _repairable null sul server.");
                return;
            }

            var thresholds = _repairable.GetRepairThresholds();
            var firstThreshold = _repairable.GetFirstThreshold();

            // ── STEP 1 — Inizio sessione: cattura HP di partenza ──────────────
            bool isFirstThresholdOfSession =
                firstThreshold.HasValue
                && Mathf.Approximately(firstThreshold.Value.progress * 100f, progressPct);

            if (isFirstThresholdOfSession || _sessionStartPercent < 0f)
            {
                _sessionStartPercent = _repairable.GetHealthPercent();
                Debug.Log($"[RepairPanel] Nuova sessione riparazione — "
                        + $"{_repairable.GetSystemName()} parte da {_sessionStartPercent * 100f:F0}% HP");
            }

            // ── STEP 2 — Consumo materiali per questa soglia ─────────────────
            if (thresholds != null)
            {
                foreach (var threshold in thresholds)
                {
                    if (!Mathf.Approximately(threshold.progress * 100f, progressPct))
                        continue;

                    if (threshold.materials != null && InventorySystem.Instance != null)
                    {
                        foreach (var req in threshold.materials)
                        {
                            bool ok = InventorySystem.Instance.TryConsume(req.itemType, req.amount);
                            if (!ok)
                                Debug.LogWarning(
                                    $"[RepairPanel] RPC: Materiali insufficienti — "
                                  + $"{req.amount}× {req.itemType} a soglia {progressPct}% "
                                  + $"(race condition? il gate all'ingresso avrebbe dovuto garantirli)");
                        }
                    }
                    break;
                }
            }

            // ── STEP 3 — Applica riparazione relativa alla sessione ──────────
            float adjustedProgress =
                (_sessionStartPercent + (1f - _sessionStartPercent) * (progressPct / 100f)) * 100f;

            _repairable.ApplyRepair(adjustedProgress);

            Debug.Log($"[RepairPanel] RPC: soglia minigame {progressPct}% → "
                    + $"target effettivo {adjustedProgress:F1}% su {_repairable.GetSystemName()} "
                    + $"(sessione iniziata a {_sessionStartPercent * 100f:F0}%)");

            // Soglia 100% → fine sessione, pronta per la prossima
            if (Mathf.Approximately(progressPct, 100f))
                _sessionStartPercent = -1f;
        }

        // ── API pubblica ──────────────────────────────────────────────────────

        /// <summary>Aggiorna la referenza al sistema riparabile (utile per sistemi dinamici).</summary>
        public void SetRepairTarget(MonoBehaviour target)
        {
            repairableTarget = target;
            _repairable = target as IRepairable;
        }
    }
}