using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// RepairPanel — Milestone 2
    /// Pannello fisico interagibile nella nave che apre il RepairMinigameEngineering.
    ///
    /// RESPONSABILITÀ:
    ///   - IInteractable: rilevato da InteractionSystem via raycast
    ///   - CanInteract() → true solo se:
    ///       1. il sistema target è DEGRADED/OFFLINE (IsRepairable())
    ///       2. il minigame non è già in corso
    ///       3. i materiali per TUTTE le soglie (50+75+100, sommati) sono
    ///          disponibili — HasMaterialsForFullRepair() (gate cumulativo)
    ///   - Interact() → disabilita PlayerController + apre RepairMinigameEngineering
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
    ///   5. Assegna il RepairMinigameEngineering (figlio di questo GameObject)
    ///   6. Assegna PlayerInput reference (stessa dell'EngineeringStation)
    ///   7. Registra il prefab / GameObject nella lista NetworkPrefabs del NetworkManager
    ///   8. (Rev BY) PanelViewFraming sullo stesso GameObject, con snap point e look-at point
    ///
    /// INQUADRATURA (Rev BY · Q135-a): se sullo stesso GameObject c'è un PanelViewFraming, all'apertura
    /// il giocatore scivola davanti al pannello e la camera si gira verso il monitor; all'uscita lo
    /// sguardo resta sul monitor. Senza il componente il pannello si comporta come prima.
    ///
    /// A TERRA DURANTE IL MINIGAME (Rev BY · Q136-a, schema di MedicalStation.ForceExitDowned): il
    /// minigame si chiude, finisce l'inquadratura, si riaccende il CharacterController (il suo collider
    /// serve al raycast del defibrillatore) ma non PlayerController, che il freeze Downed tiene spento.
    /// All'uscita normale PlayerController si riaccende solo se il giocatore è vivo. Prima i due
    /// componenti si riaccendevano sempre, anche a un giocatore a terra.
    ///
    /// CHIUSURA UNICA (Rev BY): tutte le uscite passano da EndSession, che spegne _isActive per primo.
    /// Il callback di interruzione del minigame, che scatta dentro Interrupt(), trova _isActive già
    /// falso e non ripete il ripristino (prima ExitRepair girava due volte per ogni Cancel).
    /// </summary>
    public class RepairPanel : NetworkBehaviour, IInteractable
    {
        [Header("Target System")]
        [Tooltip("Il sistema nave che questo pannello ripara. Deve implementare IRepairable.")]
        [SerializeField] private MonoBehaviour repairableTarget;

        [Header("Minigame")]
        [Tooltip("Il RepairMinigameEngineering su questo pannello (di solito figlio di questo GameObject).")]
        [SerializeField] private RepairMinigameEngineering repairMinigame;

        [Header("Input")]
        [Tooltip("Stessa referenza PlayerInput usata nelle altre stazioni.")]
        [SerializeField] private PlayerInput playerInputReference;

        [Header("Prompt")]
        [SerializeField] private string interactionPrompt = "[{interact}] Repair system";   // Rev BT-c (Q75-a)

        // ── Stato runtime (client/UI) ────────────────────────────────────────
        private PlayerController _playerController;
        private CharacterController _characterController;
        private PlayerHealthSystem _playerHealth;   // Rev BY (Q136-a)
        private PanelViewFraming _framing;           // Rev BY (Q135-a) — facoltativo
        private float _cooldown;
        private InputAction _cancelAction;
        private bool _isActive;

        private const float ExitCooldown = 0.5f;
        private const float CompleteCooldown = 1.0f;

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
            _framing = GetComponent<PanelViewFraming>();   // Rev BY (Q135-a)

            if (_repairable == null)
                Debug.LogWarning($"[RepairPanel] {name}: repairableTarget non implementa IRepairable.");

            if (repairMinigame == null)
                Debug.LogWarning($"[RepairPanel] {name}: RepairMinigameEngineering non assegnato.");
        }

        private void Update()
        {
            if (_cooldown > 0f) _cooldown -= Time.deltaTime;

            if (!_isActive) return;

            // Rev BY (Q136-a) — a terra durante il minigame: uscita forzata, prima di tutto il resto.
            if (_playerHealth != null && !_playerHealth.IsAlive)
            {
                EndSession(ExitCooldown, interruptMinigame: true);
                return;
            }

            if (_cancelAction != null && _cancelAction.WasPressedThisFrame())
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
                return $"{_repairable.GetSystemName()} — Operational";   // Rev BT-c (Q75-a): prompt in inglese

            if (!_repairable.HasMaterialsForFullRepair())
                return $"{_repairable.GetSystemName()} — Not enough materials (see Monitor 2)";

            return $"[{{interact}}] Repair {_repairable.GetSystemName()} [{_repairable.GetCurrentState()}]";
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
            _playerHealth = interactor.GetComponent<PlayerHealthSystem>();   // Rev BY (Q136-a)

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

            // Rev BY (Q135-a) — giocatore davanti al pannello, camera sul monitor.
            if (_framing != null) _framing.Begin(interactor);

            // Apri minigame — passa riferimento a questo RepairPanel per l'RPC
            repairMinigame?.Open(_repairable, this, OnMinigameComplete, OnMinigameInterrupted);
        }

        /// <summary>Uscita chiesta dal giocatore (Cancel).</summary>
        private void ExitRepair()
        {
            EndSession(ExitCooldown, interruptMinigame: true);
        }

        private void OnMinigameComplete()
        {
            if (!_isActive) return;
            EndSession(CompleteCooldown, interruptMinigame: false);   // il minigame si è già chiuso
        }

        private void OnMinigameInterrupted()
        {
            // Interruzione interna (timer scaduto, sistema tornato ONLINE). Se _isActive è già falso
            // la chiusura è in corso (Interrupt chiamato da EndSession): niente da ripetere.
            if (!_isActive) return;
            EndSession(ExitCooldown, interruptMinigame: false);   // il minigame si è già chiuso
        }

        /// <summary>
        /// Rev BY — unica chiusura della sessione (Cancel, fine o interruzione del minigame, giocatore a
        /// terra). Ordine: stato spento, inquadratura chiusa (lo sguardo passa a PlayerController),
        /// giocatore ripristinato, minigame interrotto se ancora aperto. PlayerController si riaccende
        /// solo da vivi (Q136-a); il CharacterController sempre (a terra serve al defibrillatore).
        /// </summary>
        private void EndSession(float cooldown, bool interruptMinigame)
        {
            if (!_isActive) return;

            _isActive = false;
            _cooldown = cooldown;

            if (_framing != null) _framing.End();

            if (_playerController != null)
            {
                // Come le postazioni: la velocità interna è rimasta quella di prima del pannello.
                _playerController.ResetVelocity();
                _playerController.enabled = _playerHealth == null || _playerHealth.IsAlive;
            }

            if (_characterController != null) _characterController.enabled = true;

            if (interruptMinigame) repairMinigame?.Interrupt();   // → OnMinigameInterrupted, che esce subito
        }

        // ── RPC Server-Side — soglie relative alla sessione ───────────────────

        /// <summary>
        /// Chiamato da RepairMinigameEngineering quando il giocatore supera una soglia
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