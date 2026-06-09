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
    ///   - CanInteract() → true solo se il sistema target è DEGRADED/OFFLINE
    ///     e il minigame non è già in corso
    ///   - Interact() → disabilita PlayerController + apre RepairMinigame
    ///   - Cancel → chiude minigame e ripristina il player
    ///
    /// SETUP IN SCENA:
    ///   1. Crea un GameObject sul pannello fisico (parete della nave)
    ///   2. Aggiungi Collider (per raycast InteractionSystem)
    ///   3. Assegna il sistema IRepairable target (es. PropulsionSystem)
    ///   4. Assegna il RepairMinigame (figlio di questo GameObject)
    ///   5. Assegna PlayerInput reference (stessa dell'EngineeringStation)
    ///
    /// Pattern identico a MedicalStation: PlayerController disabled durante uso,
    /// Cancel via PlayerInput reference (mai hardcodato).
    /// </summary>
    public class RepairPanel : MonoBehaviour, IInteractable
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

        // ── Stato runtime ─────────────────────────────────────────────────────
        private PlayerController    _playerController;
        private CharacterController _characterController;
        private float               _cooldown;
        private InputAction         _cancelAction;
        private bool                _isActive;

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

        public bool CanInteract()
            => !_isActive && _cooldown <= 0f
            && _repairable != null && _repairable.IsRepairable();

        public string GetInteractionPrompt()
        {
            if (_repairable == null) return interactionPrompt;
            return $"Ripara {_repairable.GetSystemName()} [{_repairable.GetCurrentState()}]";
        }

        public void Interact(GameObject interactor)
        {
            if (!CanInteract()) return;
            EnterRepair(interactor);
        }

        public bool IsContinuousInteraction() => false;
        public void OnLookEnter() { }
        public void OnLookExit()  { }

        // ── Enter / Exit ──────────────────────────────────────────────────────

        private void EnterRepair(GameObject interactor)
        {
            _playerController    = interactor.GetComponent<PlayerController>();
            _characterController = interactor.GetComponent<CharacterController>();

            // Recupera Cancel action da PlayerInput (mai hardcodato)
            PlayerInput pi = playerInputReference != null
                ? playerInputReference
                : interactor.GetComponent<PlayerInput>();

            if (pi != null)
                _cancelAction = pi.actions["Cancel"];

            // Disabilita movimento player
            if (_playerController    != null) _playerController.enabled    = false;
            if (_characterController != null) _characterController.enabled = false;

            _isActive = true;

            // Apri minigame
            repairMinigame?.Open(_repairable, OnMinigameComplete, OnMinigameInterrupted);
        }

        private void ExitRepair()
        {
            _isActive = false;
            _cooldown = 0.5f;

            // Ripristina player
            if (_playerController    != null) _playerController.enabled    = true;
            if (_characterController != null) _characterController.enabled = true;

            repairMinigame?.Interrupt();
        }

        private void OnMinigameComplete()
        {
            _isActive = false;
            _cooldown = 1.0f;

            if (_playerController    != null) _playerController.enabled    = true;
            if (_characterController != null) _characterController.enabled = true;
        }

        private void OnMinigameInterrupted()
        {
            // Il minigame è stato interrotto internamente (es. sistema tornato ONLINE)
            ExitRepair();
        }

        // ── API pubblica ──────────────────────────────────────────────────────

        /// <summary>Aggiorna la referenza al sistema riparabile (utile per sistemi dinamici).</summary>
        public void SetRepairTarget(MonoBehaviour target)
        {
            repairableTarget = target;
            _repairable      = target as IRepairable;
        }
    }
}
