using System;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    // ─── Enum NavigationState ─────────────────────────────────────────────────
    // Definito qui — usato da PilotStation, PilotHUD, FTLDrive.

    public enum NavigationState
    {
        Anchored,   // nave ferma, 0W, 0 fuel
        Coasting,   // inerzia (Pilota fuori postazione o OFFLINE), 0W, 0 fuel
        Autopilot,  // rotta automatica verso POI, 50W, 0.5 fuel/min
        Manual      // controllo diretto Pilota, 80W, 1.0 fuel/min
    }

    // ─── PropulsionSystem ─────────────────────────────────────────────────────

    /// <summary>
    /// PropulsionSystem — Milestone 2
    /// NetworkBehaviour + IPowerConsumer + IRepairable.
    ///
    /// RESPONSABILITÀ:
    ///   - Gestisce NavigationState (ANCHORED/COASTING/AUTOPILOT/MANUAL)
    ///   - Consuma watt da PowerManager in base allo stato
    ///   - Consuma FuelCell da InventorySystem ogni secondo (server)
    ///   - Implementa IRepairable → pannello fisico in sala motori
    ///   - Riceve SetAutopilotAvailable() da ZoneManager (AsteroidField)
    ///   - Riceve SetFTLOverride() da FTLDrive durante la carica
    ///
    /// MOVIMENTO (M3):
    ///   ShipMovement.cs leggerà GetCurrentSpeed() e GetNavigationState().
    ///   In M2 il sistema è puramente logico — nessun Rigidbody.
    ///
    /// REPAIR PANEL:
    ///   Assegna questa istanza come repairableTarget al RepairPanel
    ///   posizionato fisicamente in sala motori.
    ///
    /// DIPENDE DA:
    ///   PowerManager (IPowerConsumer) · InventorySystem (FuelCell)
    /// </summary>
    public class PropulsionSystem : NetworkBehaviour, IPowerConsumer, IRepairable
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static PropulsionSystem Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── Upgrade Data ──────────────────────────────────────────────────────
        [Header("Upgrade Data")]
        [Tooltip("Array di tier. Index 0 = T1, 1 = T2, ecc.")]
        [SerializeField] private PropulsionUpgradeData[] allTiers;

        [SerializeField] private int startingTierIndex = 0;

        // ── Stato iniziale ────────────────────────────────────────────────────
        [Header("Stato Iniziale")]
        [SerializeField] private NavigationState startingState = NavigationState.Anchored;
        [SerializeField] private bool            startPowered  = true;

        // ── Network Variables ─────────────────────────────────────────────────
        private readonly NetworkVariable<float> _netHealth =
            new(100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> _netNavState =
            new((int)NavigationState.Anchored,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<bool> _netAutopilotAvailable =
            new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ── Runtime (server) ──────────────────────────────────────────────────
        private PropulsionUpgradeData _data;
        private PowerManager          _powerManager;
        private bool                  _isPowered;
        private bool                  _ftlOverride;        // FTLDrive sopprime i motori
        private float                 _fuelAccumulator;    // fuel frazionario accumulato
        private float                 _fuelTickTimer;
        private const float           FuelTickInterval = 1f;

        // ── Proprietà pubbliche ───────────────────────────────────────────────
        public NavigationState CurrentNavState => (NavigationState)_netNavState.Value;
        public bool   AutopilotAvailable       => _netAutopilotAvailable.Value;
        public float  CurrentHealth            => _netHealth.Value;
        public float  CurrentHealthPercent     => _data != null && _data.maxHealth > 0f
                                                  ? _netHealth.Value / _data.maxHealth : 1f;
        public float  CurrentSpeed             => _data != null
                                                  ? _data.maxSpeed * GetDegradationMults().speed : 0f;

        // ── Lifecycle NGO ─────────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            Instance = this;

            if (allTiers != null && startingTierIndex < allTiers.Length)
                _data = allTiers[startingTierIndex];

            if (_data != null)
                _netHealth.Value = _data.maxHealth;

            _netNavState.Value          = (int)startingState;
            _netAutopilotAvailable.Value = true;

            _netHealth.OnValueChanged    += OnHealthChanged;
            _netNavState.OnValueChanged  += OnNavStateChanged;

            if (PowerManager.Instance != null)
                InitWithPowerManager();
            else
                PowerManager.OnInstanceReady += InitWithPowerManager;

            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            PowerManager.OnInstanceReady -= InitWithPowerManager;

            _netHealth.OnValueChanged   -= OnHealthChanged;
            _netNavState.OnValueChanged -= OnNavStateChanged;

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

        // ── Update (server — fuel tick) ───────────────────────────────────────
        private void Update()
        {
            if (!IsServer) return;

            _fuelTickTimer += Time.deltaTime;
            if (_fuelTickTimer < FuelTickInterval) return;
            _fuelTickTimer = 0f;

            ConsumeFuelTick();
        }

        // ── IPowerConsumer ────────────────────────────────────────────────────
        public float GetPowerDemand()
        {
            if (_data == null || !_isPowered || _ftlOverride) return 0f;

            return CurrentNavState switch
            {
                NavigationState.Autopilot => _data.wattsAutopilot * GetDegradationMults().watts,
                NavigationState.Manual    => _data.wattsManual    * GetDegradationMults().watts,
                _                         => 0f
            };
        }

        public int    GetPriority()       => _data?.powerPriority ?? 6;
        public bool   IsActive()          => _isPowered;
        public bool   CanBeDisabled()     => true;
        public string GetSystemName()     => _data?.displayName ?? "Propulsion System";

        public void SetPowerState(bool isOn)
        {
            if (!IsServer || _isPowered == isOn) return;
            _isPowered = isOn;

            if (!isOn)
            {
                // Perdita energia → coasting forzato
                SetNavStateInternal(NavigationState.Coasting);
                Debug.LogWarning("[PropulsionSystem] Power OFF — COASTING forzato");
            }
            else
            {
                Debug.Log("[PropulsionSystem] Power ON");
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

            Debug.Log($"[PropulsionSystem] Repair {progressPercent}% → HP {_netHealth.Value:F0}/{_data.maxHealth}");
        }

        // ── API Pubblica ──────────────────────────────────────────────────────

        /// <summary>
        /// Richiede cambio di stato di navigazione.
        /// Validazione: non AUTOPILOT se AutopilotAvailable=false,
        /// non MANUAL/AUTOPILOT se sistema OFFLINE o FTL attivo.
        /// Chiamabile da qualsiasi client (PilotStation).
        /// </summary>
        public void RequestNavigationState(NavigationState newState)
        {
            if (IsServer) RequestNavStateInternal(newState);
            else          RequestNavStateRpc(newState);
        }

        [Rpc(SendTo.Server)]
        private void RequestNavStateRpc(NavigationState s) => RequestNavStateInternal(s);

        private void RequestNavStateInternal(NavigationState newState)
        {
            // Validazione
            if (newState == NavigationState.Autopilot && !_netAutopilotAvailable.Value)
            {
                Debug.LogWarning("[PropulsionSystem] Autopilota non disponibile (AsteroidField attivo).");
                return;
            }

            if (_ftlOverride && newState != NavigationState.Anchored)
            {
                Debug.LogWarning("[PropulsionSystem] FTL in corso — solo ANCHORED permesso.");
                return;
            }

            if (HealthToState(CurrentHealthPercent) == ShipSystemState.Offline
                && newState != NavigationState.Coasting
                && newState != NavigationState.Anchored)
            {
                Debug.LogWarning("[PropulsionSystem] Sistema OFFLINE — solo COASTING/ANCHORED.");
                return;
            }

            SetNavStateInternal(newState);
        }

        /// <summary>
        /// Chiamato da ZoneManager quando ZoneEvent.AsteroidField è attivo/inattivo.
        /// Aggiorna il parametro passivo di disponibilità autopilota.
        /// NON cambia lo stato di navigazione corrente — è il Pilota a decidere.
        /// </summary>
        public void SetAutopilotAvailable(bool available)
        {
            if (IsServer) SetAutopilotAvailableInternal(available);
            else          SetAutopilotAvailableRpc(available);
        }

        [Rpc(SendTo.Server)]
        private void SetAutopilotAvailableRpc(bool v) => SetAutopilotAvailableInternal(v);

        private void SetAutopilotAvailableInternal(bool available)
        {
            _netAutopilotAvailable.Value = available;

            // Se autopilota è attivo e viene disabilitato: forza COASTING
            if (!available && CurrentNavState == NavigationState.Autopilot)
            {
                SetNavStateInternal(NavigationState.Coasting);
                Debug.LogWarning("[PropulsionSystem] Autopilota disabilitato (AsteroidField) → COASTING");
            }
        }

        /// <summary>
        /// Chiamato da FTLDrive durante la carica.
        /// Sopprime i motori (0W, 0 fuel) e forza ANCHORED.
        /// NON è un comando di protezione — è un vincolo fisico del salto FTL.
        /// </summary>
        public void SetFTLOverride(bool ftlActive)
        {
            if (!IsServer) return;
            _ftlOverride = ftlActive;

            if (ftlActive)
            {
                SetNavStateInternal(NavigationState.Anchored);
                _fuelAccumulator = 0f;
                Debug.Log("[PropulsionSystem] FTL override ON — motori spenti");
            }
            else
            {
                Debug.Log("[PropulsionSystem] FTL override OFF — motori disponibili");
            }
        }

        /// <summary>Applica danno al sistema (da ShipMovement M3 o debug).</summary>
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
        }

        // ── Fuel Consumption ──────────────────────────────────────────────────
        private void ConsumeFuelTick()
        {
            if (!_isPowered || _data == null || InventorySystem.Instance == null) return;

            var state = CurrentNavState;
            if (state != NavigationState.Autopilot && state != NavigationState.Manual) return;

            float fuelPerMin = state == NavigationState.Autopilot
                ? _data.fuelPerMinAutopilot
                : _data.fuelPerMinManual;

            fuelPerMin       *= GetDegradationMults().fuel;
            _fuelAccumulator += (fuelPerMin / 60f) * FuelTickInterval;

            int toConsume = Mathf.FloorToInt(_fuelAccumulator);
            if (toConsume <= 0) return;

            bool consumed = InventorySystem.Instance.TryConsume(ItemType.FuelCell, toConsume);
            if (consumed)
            {
                _fuelAccumulator -= toConsume;
            }
            else
            {
                Debug.LogWarning("[PropulsionSystem] Carburante esaurito → COASTING");
                SetNavStateInternal(NavigationState.Coasting);
                _fuelAccumulator = 0f;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void SetNavStateInternal(NavigationState s)
        {
            _netNavState.Value = (int)s;
        }

        private static ShipSystemState HealthToState(float percent)
        {
            if (percent >= 0.75f) return ShipSystemState.Online;
            if (percent >= 0.50f) return ShipSystemState.DegradedLight;
            if (percent >= 0.25f) return ShipSystemState.DegradedHeavy;
            return ShipSystemState.Offline;
        }

        private struct DegradationMults { public float speed, watts, fuel; }

        private DegradationMults GetDegradationMults()
        {
            if (_data == null) return new DegradationMults { speed = 1, watts = 1, fuel = 1 };

            int idx = CurrentHealthPercent >= 0.75f ? 0
                    : CurrentHealthPercent >= 0.50f ? 1
                    : 2;

            float SafeGet(float[] arr, int i) =>
                arr != null && i < arr.Length ? arr[i] : 1f;

            return new DegradationMults
            {
                speed = SafeGet(_data.speedMultipliers, idx),
                watts = SafeGet(_data.wattsMultipliers, idx),
                fuel  = SafeGet(_data.fuelMultipliers,  idx)
            };
        }

        // ── Callback NetworkVariables ─────────────────────────────────────────
        private void OnHealthChanged(float prev, float curr)
        {
            // OFFLINE: forza COASTING se non già in stato statico
            if (IsServer && HealthToState(CurrentHealthPercent) == ShipSystemState.Offline)
            {
                var state = CurrentNavState;
                if (state == NavigationState.Autopilot || state == NavigationState.Manual)
                    SetNavStateInternal(NavigationState.Coasting);
            }

            // Notifica PowerManager per aggiornare il demand
            // (GetPowerDemand() viene ricalcolato automaticamente al prossimo frame)
        }

        private void OnNavStateChanged(int prev, int curr)
        {
            // Reset fuel accumulator quando si cambia stato
            if (IsServer) _fuelAccumulator = 0f;

            Debug.Log($"[PropulsionSystem] NavState → {(NavigationState)curr}" +
                      $" | Demand: {GetPowerDemand():F1}W");
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

            float prevMaxHP = _data?.maxHealth ?? 100f;
            float hpRatio   = _netHealth.Value / prevMaxHP;

            _data            = newData;
            _netHealth.Value = _data.maxHealth * hpRatio;

            Debug.Log($"[PropulsionSystem] Upgraded to {_data.displayName}");
        }

        // ── Debug GUI ─────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            var mults = GetDegradationMults();
            GUILayout.BeginArea(new Rect(Screen.width - 240, 10, 230, 290));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[Propulsion] {(IsServer ? "SRV" : "CLT")}");
            GUILayout.Label($"State:  {CurrentNavState}");
            GUILayout.Label($"HP:     {CurrentHealth:F0}/{(_data?.maxHealth ?? 0):F0} ({CurrentHealthPercent * 100:F0}%)");
            GUILayout.Label($"Status: {HealthToState(CurrentHealthPercent)}");
            GUILayout.Label($"Demand: {GetPowerDemand():F1}W");
            GUILayout.Label($"Speed:  {CurrentSpeed:F0} m/s ×{mults.speed:F2}");
            GUILayout.Label($"Autopilot: {(_netAutopilotAvailable.Value ? "OK" : "DISABLED")}");
            GUILayout.Label($"FTL Override: {_ftlOverride}");

            if (IsServer)
            {
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Anchored"))  RequestNavStateInternal(NavigationState.Anchored);
                if (GUILayout.Button("Autopilot")) RequestNavStateInternal(NavigationState.Autopilot);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Manual"))    RequestNavStateInternal(NavigationState.Manual);
                if (GUILayout.Button("Coasting"))  RequestNavStateInternal(NavigationState.Coasting);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("-20 HP"))   ApplyDamageInternal(20f);
                if (GUILayout.Button("-50 HP"))   ApplyDamageInternal(50f);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Repair 100%")) ((IRepairable)this).ApplyRepair(100f);
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}
