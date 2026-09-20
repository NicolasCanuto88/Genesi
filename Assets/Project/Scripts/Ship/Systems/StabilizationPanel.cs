using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// StabilizationPanel — Rev BI (Path H)
    /// Pannello fisico interagibile che apre lo StabilizationMinigameEngineering
    /// (hold-against-decay) su un subsystem che sta cedendo.
    ///
    /// FRATELLO DISTINTO di RepairPanel:
    ///   - stessa struttura enter/exit/cancel/cooldown + autorità server;
    ///   - MA nessun gate materiali (stabilizzare è un atto di tenuta, non di
    ///     riparazione: non consuma nulla);
    ///   - MA win-mode "hold": l'esito è tenere fino al timer, non salire a 100%.
    ///
    /// TRIGGER (SCOPE Rev BI · Q3-c):
    ///   L'arming reale — quale stato "acuto" (fallimento a cascata) mette un
    ///   subsystem in condizione "da stabilizzare" — appartiene al Combat (M4.7),
    ///   dove i cedimenti a cascata esistono. Qui l'arming è un FLAG DI DEBUG
    ///   (debugArmed), esattamente come debugStartTier sullo Scanner: la meccanica
    ///   e la UI sono complete e giocabili subito; solo il filo dal mondo è rimandato.
    ///
    /// ESITO SERVER-AUTHORITY (SEAM):
    ///   NotifyStabilizationOutcomeRpc(success) gira sul server. Oggi logga soltanto
    ///   (il modello danni non ha ancora uno stato di cascata da fermare/forzare).
    ///   È il punto d'innesto dove, al Combat, il successo fermerà la cascata e il
    ///   fallimento forzerà la perdita del subsystem.
    ///
    /// SETUP IN SCENA:
    ///   1. GameObject sul pannello fisico (parete nave)
    ///   2. NetworkObject component (⚠ obbligatorio — StabilizationPanel è NetworkBehaviour)
    ///   3. Collider (per raycast InteractionSystem)
    ///   4. Assegna il subsystem IRepairable target (es. PropulsionSystem)
    ///   5. Assegna lo StabilizationMinigameEngineering (figlio di questo GameObject)
    ///   6. Assegna PlayerInput reference (stessa delle altre stazioni)
    ///   7. debugArmed = true per testare; registra il GameObject nei NetworkPrefabs
    /// </summary>
    public class StabilizationPanel : NetworkBehaviour, IInteractable
    {
        [Header("Target System")]
        [Tooltip("Il subsystem che questo pannello stabilizza. Deve implementare IRepairable.")]
        [SerializeField] private MonoBehaviour stabilizableTarget;

        [Header("Minigame")]
        [Tooltip("Lo StabilizationMinigameEngineering su questo pannello (di solito figlio).")]
        [SerializeField] private StabilizationMinigameEngineering stabilizationMinigame;

        [Header("Input")]
        [Tooltip("Stessa referenza PlayerInput usata nelle altre stazioni.")]
        [SerializeField] private PlayerInput playerInputReference;

        [Header("Prompt")]
        [SerializeField] private string interactionPrompt = "Stabilizza sistema";

        [Header("Debug — arming (Q3-c, in attesa Combat M4.7)")]
        [Tooltip("Arma la stabilizzazione per il test. Sostituito dallo stato acuto " +
                 "networked di cascata al Combat. Vero = pannello interagibile.")]
        [SerializeField] private bool debugArmed = true;

        // ── Stato runtime (client/UI) ────────────────────────────────────────
        private PlayerController _playerController;
        private CharacterController _characterController;
        private float _cooldown;
        private InputAction _cancelAction;
        private bool _isActive;

        // IRepairable cachata (usata per il nome sistema; nessun consumo materiali)
        private IRepairable _repairable;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _repairable = stabilizableTarget as IRepairable;

            if (_repairable == null)
                Debug.LogWarning($"[StabilizationPanel] {name}: stabilizableTarget non implementa IRepairable.");

            if (stabilizationMinigame == null)
                Debug.LogWarning($"[StabilizationPanel] {name}: StabilizationMinigameEngineering non assegnato.");
        }

        private void Update()
        {
            if (_cooldown > 0f) _cooldown -= Time.deltaTime;

            if (_isActive && _cancelAction != null && _cancelAction.WasPressedThisFrame())
                ExitStabilization();
        }

        // ── IInteractable ─────────────────────────────────────────────────────

        /// <summary>
        /// Gate: pannello armato (debugArmed, sostituito dallo stato acuto al Combat),
        /// minigame non già attivo, cooldown scaduto, target valido.
        /// NB: nessun gate materiali — la stabilizzazione non consuma.
        /// </summary>
        public bool CanInteract()
            => !_isActive && _cooldown <= 0f
            && _repairable != null
            && debugArmed;

        public string GetInteractionPrompt()
        {
            if (_repairable == null) return interactionPrompt;

            if (!debugArmed)
                return $"{_repairable.GetSystemName()} — Stabile";

            return $"Stabilizza {_repairable.GetSystemName()}";
        }

        public void Interact(GameObject interactor)
        {
            if (!CanInteract()) return;
            EnterStabilization(interactor);
        }

        public bool IsContinuousInteraction() => false;
        public void OnLookEnter() { }
        public void OnLookExit() { }

        // ── Enter / Exit ──────────────────────────────────────────────────────

        private void EnterStabilization(GameObject interactor)
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

            // Apri minigame — passa riferimento a questo panel per l'esito server-side
            stabilizationMinigame?.Open(_repairable, this, OnMinigameComplete, OnMinigameInterrupted);
        }

        private void ExitStabilization()
        {
            _isActive = false;
            _cooldown = 0.5f;

            // Ripristina player
            if (_playerController != null) _playerController.enabled = true;
            if (_characterController != null) _characterController.enabled = true;

            stabilizationMinigame?.Interrupt();
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
            // Interruzione interna (Cancel o floor sfondato): ripristina come l'uscita.
            ExitStabilization();
        }

        // ── Esito server-authority (SEAM Combat M4.7) ─────────────────────────

        /// <summary>
        /// Chiamato dal minigame all'esito (true = tenuto fino al timer, false =
        /// floor sfondato). Eseguito SEMPRE sul server.
        ///
        /// OGGI (Rev BI · Q3-c): logga soltanto — il modello danni non ha ancora
        /// uno stato di cascata. È il punto dove, al Combat (M4.7):
        ///   success → si ferma la cascata / il subsystem resta al livello tenuto;
        ///   !success → si forza la perdita (es. Offline) del subsystem.
        /// </summary>
        [Rpc(SendTo.Server)]
        public void NotifyStabilizationOutcomeRpc(bool success)
        {
            string sys = _repairable != null ? _repairable.GetSystemName() : name;

            if (success)
                Debug.Log($"[StabilizationPanel] {sys}: stabilizzato (tenuto fino al timer). " +
                          "Effetto reale anti-cascata da cablare al Combat (M4.7).");
            else
                Debug.Log($"[StabilizationPanel] {sys}: PERSO (floor sfondato). " +
                          "Perdita reale del subsystem da cablare al Combat (M4.7).");
        }

        // ── API pubblica ──────────────────────────────────────────────────────

        /// <summary>Aggiorna il target (utile per sistemi dinamici).</summary>
        public void SetStabilizationTarget(MonoBehaviour target)
        {
            stabilizableTarget = target;
            _repairable = target as IRepairable;
        }

        /// <summary>Arma/disarma la stabilizzazione (placeholder del trigger di cascata).</summary>
        public void SetArmed(bool armed) => debugArmed = armed;

#if UNITY_EDITOR
        [ContextMenu("DEBUG — Ri-arma stabilizzazione")]
        private void DebugRearm() => debugArmed = true;
#endif
    }
}
