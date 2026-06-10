using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    // ─── FTLState ─────────────────────────────────────────────────────────────

    public enum FTLState
    {
        Ready,     // disponibile, 0W
        Charging,  // carica in corso, 700W, propulsione soppressa
        Jumping,   // salto in corso (transizione visiva, ~2.5s), 0W
        Cooldown,  // in ricarica dopo salto riuscito, 0W
        Lockout    // blocco breve dopo salto annullato da blackout, 0W
    }

    // ─── FTLDrive ─────────────────────────────────────────────────────────────

    /// <summary>
    /// FTLDrive — Milestone 2
    /// NetworkBehaviour + IPowerConsumer + IRepairable.
    ///
    /// STATI:
    ///   Ready     → FTL disponibile, 0W consumo
    ///   Charging  → carica attiva, 700W, PropulsionSystem soppresso
    ///   Jumping   → salto in corso (2.5s transizione), 0W
    ///   Cooldown  → timer post-salto (15 min T1), 0W
    ///   Lockout   → 30s dopo blackout durante carica, 0W
    ///
    /// AVVIO SALTO:
    ///   TryInitiateJump() — chiamabile SOLO da PilotStation (M2: anche da debug GUI).
    ///   Sistema OFFLINE → salto negato.
    ///
    /// BLACKOUT DURANTE CARICA:
    ///   SetPowerState(false) → salto annullato → Lockout 30s → PropulsionSystem ripristinato.
    ///
    /// EVENTI:
    ///   OnJumpComplete → M3: NavigationSystem cambia zona.
    ///   OnStateChanged → PilotHUD aggiorna display FTL.
    ///
    /// DIPENDE DA:
    ///   PowerManager (IPowerConsumer) · PropulsionSystem (SetFTLOverride)
    /// </summary>
    public class FTLDrive : NetworkBehaviour, IPowerConsumer, IRepairable
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static FTLDrive Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── Upgrade Data ──────────────────────────────────────────────────────
        [Header("Upgrade Data")]
        [SerializeField] private FTLUpgradeData[] allTiers;
        [SerializeField] private int              startingTierIndex = 0;

        [Header("Stato Iniziale")]
        [SerializeField] private bool startPowered = true;

        // ── Network Variables ─────────────────────────────────────────────────
        private readonly NetworkVariable<float> _netHealth =
            new(100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> _netState =
            new((int)FTLState.Ready,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<float> _netChargeProgress =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Cooldown/Lockout rimanente — aggiornato ogni 0.5s (basta per il display MM:SS)
        private readonly NetworkVariable<float> _netTimeRemaining =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ── Runtime (server) ──────────────────────────────────────────────────
        private FTLUpgradeData _data;
        private PowerManager   _powerManager;
        private bool           _isPowered;
        private float          _countdownTimer;
        private float          _uiSyncTimer;
        private Coroutine      _chargeRoutine;
        private const float    UiSyncInterval = 0.5f;

        // ── Proprietà pubbliche ───────────────────────────────────────────────
        public FTLState CurrentState       => (FTLState)_netState.Value;
        public float    ChargeProgress     => _netChargeProgress.Value;    // 0–1
        public float    TimeRemaining      => _netTimeRemaining.Value;     // sec
        public float    CurrentHealth      => _netHealth.Value;
        public float    CurrentHealthPercent => _data != null && _data.maxHealth > 0f
                                               ? _netHealth.Value / _data.maxHealth : 1f;

        // ── Evento salto completato ───────────────────────────────────────────
        /// <summary>Fired su tutti i client al completamento del salto. M3: NavigationSystem ascolta.</summary>
        public static event Action OnJumpComplete;

        // ── Lifecycle NGO ─────────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            Instance = this;

            if (allTiers != null && startingTierIndex < allTiers.Length)
                _data = allTiers[startingTierIndex];

            if (_data != null)
                _netHealth.Value = _data.maxHealth;

            _netState.OnValueChanged += OnStateChanged;

            if (PowerManager.Instance != null)
                InitWithPowerManager();
            else
                PowerManager.OnInstanceReady += InitWithPowerManager;

            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            PowerManager.OnInstanceReady -= InitWithPowerManager;
            _netState.OnValueChanged     -= OnStateChanged;

            if (_chargeRoutine != null) StopCoroutine(_chargeRoutine);

            if (IsServer && _isPowered && _powerManager != null)
                _powerManager.UnregisterPowerConsumer(this);

            if (Instance == this) Instance = null;
        }

        private void InitWithPowerManager()
        {
            PowerManager.OnInstanceReady -= InitWithPowerManager;
            _powerManager = PowerManager.Instance;
            if (!IsServer) return;

            _powerManager.RegisterPowerConsumer(this);
            _isPowered = startPowered;
        }

        // ── Update (server — countdown timers) ───────────────────────────────
        private void Update()
        {
            if (!IsServer) return;

            var state = CurrentState;
            if (state != FTLState.Cooldown && state != FTLState.Lockout) return;

            _countdownTimer = Mathf.Max(0f, _countdownTimer - Time.deltaTime);

            // Sync UI ogni 0.5s
            _uiSyncTimer += Time.deltaTime;
            if (_uiSyncTimer >= UiSyncInterval)
            {
                _netTimeRemaining.Value = _countdownTimer;
                _uiSyncTimer = 0f;
            }

            // Scadenza
            if (_countdownTimer <= 0f)
            {
                _netTimeRemaining.Value = 0f;
                _netState.Value = (int)FTLState.Ready;
            }
        }

        // ── IPowerConsumer ────────────────────────────────────────────────────
        public float GetPowerDemand()
        {
            if (!_isPowered || _data == null) return 0f;
            return CurrentState == FTLState.Charging ? _data.chargeWatts : 0f;
        }

        public int    GetPriority()   => _data?.powerPriority ?? 10;
        public bool   IsActive()      => _isPowered;
        public bool   CanBeDisabled() => true;
        public string GetSystemName() => _data?.displayName ?? "FTL Drive";

        public void SetPowerState(bool isOn)
        {
            if (!IsServer || _isPowered == isOn) return;
            _isPowered = isOn;

            // Blackout durante la carica → salto annullato
            if (!isOn && CurrentState == FTLState.Charging)
            {
                CancelCharge();
                Debug.LogWarning("[FTLDrive] Blackout durante la carica — salto annullato, lockout 30s");
            }
        }

        // ── IRepairable ───────────────────────────────────────────────────────
        string          IRepairable.GetSystemName()    => GetSystemName();
        ShipSystemState IRepairable.GetCurrentState()  => HealthToState(CurrentHealthPercent);
        float           IRepairable.GetHealthPercent() => CurrentHealthPercent;
        bool            IRepairable.IsRepairable()     => CurrentHealthPercent < 0.75f;

        RepairThreshold[] IRepairable.GetRepairThresholds()
            => _data?.repairThresholds ?? Array.Empty<RepairThreshold>();

        void IRepairable.ApplyRepair(float progressPercent)
        {
            if (!IsServer || _data == null) return;
            float targetHP = _data.maxHealth * (progressPercent / 100f);
            _netHealth.Value = Mathf.Max(_netHealth.Value, targetHP);
            Debug.Log($"[FTLDrive] Repair {progressPercent}% → HP {_netHealth.Value:F0}/{_data.maxHealth}");
        }

        // ── API pubblica ──────────────────────────────────────────────────────

        /// <summary>
        /// Avvia la sequenza di salto FTL.
        /// Chiamabile SOLO da PilotStation (o debug GUI).
        /// Negato se: non Ready · sistema OFFLINE · FTLDrive non alimentato.
        /// </summary>
        public void TryInitiateJump()
        {
            if (IsServer) TryInitiateJumpInternal();
            else          TryInitiateJumpRpc();
        }

        [Rpc(SendTo.Server)]
        private void TryInitiateJumpRpc() => TryInitiateJumpInternal();

        private void TryInitiateJumpInternal()
        {
            if (CurrentState != FTLState.Ready)
            {
                Debug.LogWarning($"[FTLDrive] Salto negato — stato: {CurrentState}");
                return;
            }

            if (!_isPowered)
            {
                Debug.LogWarning("[FTLDrive] Salto negato — sistema non alimentato");
                return;
            }

            if (HealthToState(CurrentHealthPercent) == ShipSystemState.Offline)
            {
                Debug.LogWarning("[FTLDrive] Salto negato — sistema OFFLINE, riparare prima");
                return;
            }

            // Sopprimi propulsione durante la carica
            PropulsionSystem.Instance?.SetFTLOverride(true);

            _chargeRoutine = StartCoroutine(ChargeRoutine());
        }

        /// <summary>Applica danno al drive FTL (da EncounterSystem M3 o debug).</summary>
        public void ApplyDamage(float amount)
        {
            if (IsServer) ApplyDamageInternal(amount);
            else          ApplyDamageRpc(amount);
        }

        [Rpc(SendTo.Server)]
        private void ApplyDamageRpc(float amount) => ApplyDamageInternal(amount);

        private void ApplyDamageInternal(float amount)
        {
            if (_data == null) return;
            _netHealth.Value = Mathf.Clamp(_netHealth.Value - amount, 0f, _data.maxHealth);

            // Se OFFLINE mentre in carica → annulla
            if (HealthToState(CurrentHealthPercent) == ShipSystemState.Offline
                && CurrentState == FTLState.Charging)
            {
                CancelCharge();
                Debug.LogWarning("[FTLDrive] Sistema portato OFFLINE durante la carica — salto annullato");
            }
        }

        // ── Coroutine carica ──────────────────────────────────────────────────
        private IEnumerator ChargeRoutine()
        {
            _netState.Value         = (int)FTLState.Charging;
            _netChargeProgress.Value = 0f;

            float elapsed = 0f;
            float duration = _data?.chargeDuration ?? 15f;

            Debug.Log($"[FTLDrive] Carica FTL avviata ({duration}s, {GetPowerDemand():F0}W)...");

            while (elapsed < duration)
            {
                // Interrotto da SetPowerState(false) o ApplyDamageInternal?
                if (CurrentState != FTLState.Charging) yield break;

                elapsed += Time.deltaTime;
                _netChargeProgress.Value = Mathf.Clamp01(elapsed / duration);
                yield return null;
            }

            // Carica completata
            _netChargeProgress.Value = 1f;
            StartCoroutine(JumpRoutine());
        }

        private IEnumerator JumpRoutine()
        {
            _netState.Value = (int)FTLState.Jumping;
            Debug.Log("[FTLDrive] SALTO FTL in corso...");

            float jumpDuration = _data?.jumpTransitionDuration ?? 2.5f;
            yield return new WaitForSeconds(jumpDuration);

            // Salto completato
            Debug.Log("[FTLDrive] Salto completato. Cooldown avviato.");

            OnJumpComplete?.Invoke();

            // Ripristina propulsione
            PropulsionSystem.Instance?.SetFTLOverride(false);

            // Avvia cooldown
            _countdownTimer         = _data?.cooldownDuration ?? 900f;
            _netTimeRemaining.Value = _countdownTimer;
            _netState.Value         = (int)FTLState.Cooldown;

            _chargeRoutine = null;
        }

        private void CancelCharge()
        {
            if (_chargeRoutine != null)
            {
                StopCoroutine(_chargeRoutine);
                _chargeRoutine = null;
            }

            _netChargeProgress.Value = 0f;

            // Ripristina propulsione
            PropulsionSystem.Instance?.SetFTLOverride(false);

            // Entra in lockout
            _countdownTimer         = _data?.failureLockoutDuration ?? 30f;
            _netTimeRemaining.Value = _countdownTimer;
            _netState.Value         = (int)FTLState.Lockout;
        }

        // ── Callback NetworkVariables ─────────────────────────────────────────
        private void OnStateChanged(int prev, int curr)
        {
            Debug.Log($"[FTLDrive] Stato → {(FTLState)curr}");
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static ShipSystemState HealthToState(float percent)
        {
            if (percent >= 0.75f) return ShipSystemState.Online;
            if (percent >= 0.50f) return ShipSystemState.DegradedLight;
            if (percent >= 0.25f) return ShipSystemState.DegradedHeavy;
            return ShipSystemState.Offline;
        }

        // ── Upgrade ───────────────────────────────────────────────────────────
        public void ApplyUpgrade(int tierIndex)
        {
            if (IsServer) ApplyUpgradeInternal(tierIndex);
            else          ApplyUpgradeRpc(tierIndex);
        }

        [Rpc(SendTo.Server)]
        private void ApplyUpgradeRpc(int i) => ApplyUpgradeInternal(i);

        private void ApplyUpgradeInternal(int tierIndex)
        {
            if (allTiers == null || tierIndex < 0 || tierIndex >= allTiers.Length) return;
            var newData = allTiers[tierIndex];
            if (newData == null || newData.tier <= (_data?.tier ?? 0)) return;

            float hpRatio = _netHealth.Value / (_data?.maxHealth ?? 100f);
            _data            = newData;
            _netHealth.Value = _data.maxHealth * hpRatio;

            Debug.Log($"[FTLDrive] Upgraded to {_data.displayName}");
        }

        // ── Debug GUI ─────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(Screen.width - 240, 310, 230, 260));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[FTLDrive] {(IsServer ? "SRV" : "CLT")}");
            GUILayout.Label($"State:    {CurrentState}");
            GUILayout.Label($"HP:       {CurrentHealth:F0}/{(_data?.maxHealth ?? 0):F0} ({CurrentHealthPercent * 100:F0}%)");
            GUILayout.Label($"Status:   {HealthToState(CurrentHealthPercent)}");

            if (CurrentState == FTLState.Charging)
                GUILayout.Label($"Carica:   {ChargeProgress * 100:F0}%");
            else if (CurrentState == FTLState.Cooldown)
            {
                int m = Mathf.FloorToInt(TimeRemaining / 60f);
                int s = Mathf.FloorToInt(TimeRemaining % 60f);
                GUILayout.Label($"Cooldown: {m:D2}:{s:D2}");
            }
            else if (CurrentState == FTLState.Lockout)
                GUILayout.Label($"Lockout:  {TimeRemaining:F0}s");

            if (IsServer)
            {
                GUILayout.Space(4);
                if (GUILayout.Button("TryInitiateJump")) TryInitiateJumpInternal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("-20 HP")) ApplyDamageInternal(20f);
                if (GUILayout.Button("Repair")) ((IRepairable)this).ApplyRepair(100f);
                GUILayout.EndHorizontal();
                if (CurrentState == FTLState.Cooldown || CurrentState == FTLState.Lockout)
                    if (GUILayout.Button("Skip timer")) { _countdownTimer = 0f; }
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}
