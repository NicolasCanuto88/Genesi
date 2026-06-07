using UnityEngine;
using Unity.Netcode;
using System;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// HullSystem — Milestone 2
    /// Gestisce l'integrità dello scafo (HP) come NetworkBehaviour con server authority.
    ///
    /// RESPONSABILITÀ:
    ///   - Traccia CurrentHP / MaxHP come NetworkVariable
    ///   - Riceve danno via TakeDamage() (ServerRpc se chiamato da client)
    ///   - Notifica ElectricalDegradationManager.SetHullPercent() ad ogni cambio HP
    ///   - Trigger AlarmSystem.HullCritical con isteresi (default 20% / 25%)
    ///   - Espone HullPercent e OnHullChanged per ShipSystemsDashboardUI
    ///
    /// NON implementa IPowerConsumer — lo scafo non consuma energia.
    /// NON intercetta danno dagli scudi — ShieldSystem filtra il danno
    ///   prima che arrivi qui, chiamando HullSystem.NotifyDamagePassthrough().
    ///
    /// ⚠️  DIPENDE DA: GameOverSystem (M2) per consumare OnShipDestroyed.
    /// ⚠️  DIPENDE DA: EncounterSystem (M2) per danno reale in ingresso.
    /// </summary>
    public class HullSystem : NetworkBehaviour
    {
        // ===== SINGLETON + INSTANCE READY =====

        public static HullSystem Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ===== EVENTI PUBBLICI =====

        /// <summary>
        /// Fired su tutti i client quando HP cambia.
        /// Parametri: (currentHP, maxHP, percent 0–1)
        /// </summary>
        public event Action<float, float, float> OnHullChanged;

        /// <summary>
        /// Fired sul server quando HP raggiunge 0.
        /// ⚠️ Dipende da: GameOverSystem (M2).
        /// </summary>
        public static event Action OnShipDestroyed;

        /// <summary>
        /// Punto di ingresso statico per danno passthrough da ShieldSystem.
        /// ShieldSystem chiama HullSystem.NotifyDamagePassthrough(remaining).
        /// HullSystem esegue il danno internamente — nessun GetComponent tra sistemi.
        /// </summary>
        public static void NotifyDamagePassthrough(float damage)
        {
            if (Instance != null)
                Instance.ApplyDamageInternal(damage);
        }

        // ===== NETWORKVARABLES =====

        private NetworkVariable<float> netCurrentHP = new NetworkVariable<float>(
            500f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NetworkVariable<float> netMaxHP = new NetworkVariable<float>(
            500f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        // ===== INSPECTOR =====

        [Header("Upgrade Data")]
        [Tooltip("Tier attivo. Popola l'array con T1…T4. Index 0 = T1.")]
        [SerializeField] private HullUpgradeData[] allTiers;

        [Tooltip("Tier iniziale (0 = T1).")]
        [SerializeField] private int startingTierIndex = 0;

        [Header("Debug")]
        [SerializeField] private bool showDebugGUI = true;

        // ===== STATO PRIVATO (server-only) =====

        private HullUpgradeData currentUpgrade;
        private bool alarmActive = false;

        // ===== PUBLIC READ API =====

        public float CurrentHP => netCurrentHP.Value;
        public float MaxHP => netMaxHP.Value;
        public float HullPercent => netMaxHP.Value > 0f
            ? Mathf.Clamp01(netCurrentHP.Value / netMaxHP.Value)
            : 0f;

        // ===== NGO LIFECYCLE =====

        public override void OnNetworkSpawn()
        {
            if (Instance == null)
                Instance = this;
            else
            {
                Destroy(gameObject);
                return;
            }

            netCurrentHP.OnValueChanged += HandleHPChanged;
            netMaxHP.OnValueChanged += HandleHPChanged;

            if (IsServer)
                InitUpgrade();

            OnInstanceReady?.Invoke();
            Debug.Log($"[HullSystem] Online — {netCurrentHP.Value:F0}/{netMaxHP.Value:F0} HP");
        }

        public override void OnNetworkDespawn()
        {
            netCurrentHP.OnValueChanged -= HandleHPChanged;
            netMaxHP.OnValueChanged -= HandleHPChanged;

            if (Instance == this) Instance = null;
        }

        // ===== INIZIALIZZAZIONE UPGRADE =====

        private void InitUpgrade()
        {
            if (allTiers == null || allTiers.Length == 0)
            {
                Debug.LogError("[HullSystem] Nessun HullUpgradeData assegnato!");
                return;
            }

            int idx = Mathf.Clamp(startingTierIndex, 0, allTiers.Length - 1);
            currentUpgrade = allTiers[idx];

            netMaxHP.Value = currentUpgrade.maxHP;
            netCurrentHP.Value = currentUpgrade.maxHP;
        }

        // ===== PUBLIC API — DANNO =====

        /// <summary>
        /// Infligge danno diretto allo scafo (collisioni, hazard ambientali).
        /// Se ShieldSystem è attivo, il danno filtrato arriva via NotifyDamagePassthrough.
        /// Chiamabile da qualsiasi client.
        /// </summary>
        public void TakeDamage(float damage)
        {
            if (damage <= 0f) return;

            if (IsServer)
                ApplyDamageInternal(damage);
            else
                TakeDamageRpc(damage);
        }

        [Rpc(SendTo.Server)]
        private void TakeDamageRpc(float damage) => ApplyDamageInternal(damage);

        internal void ApplyDamageInternal(float damage)
        {
            if (!IsServer) return;

            float newHP = Mathf.Max(0f, netCurrentHP.Value - damage);
            netCurrentHP.Value = newHP;

            NotifyDependentSystems();

            if (newHP <= 0f)
            {
                Debug.LogError("[HullSystem] ⚠ SCAFO DISTRUTTO");
                OnShipDestroyed?.Invoke();
            }
        }

        // ===== PUBLIC API — RIPARAZIONE =====

        public void RepairFull()
        {
            if (!IsServer) return;
            netCurrentHP.Value = netMaxHP.Value;
            NotifyDependentSystems();
            Debug.Log("[HullSystem] Scafo riparato al 100%.");
        }

        public void RepairAmount(float amount)
        {
            if (amount <= 0f) return;

            if (IsServer)
                ApplyRepairInternal(amount);
            else
                RepairAmountRpc(amount);
        }

        [Rpc(SendTo.Server)]
        private void RepairAmountRpc(float amount) => ApplyRepairInternal(amount);

        private void ApplyRepairInternal(float amount)
        {
            if (!IsServer) return;
            netCurrentHP.Value = Mathf.Min(netMaxHP.Value, netCurrentHP.Value + amount);
            NotifyDependentSystems();
        }

        // ===== PUBLIC API — UPGRADE =====

        public void ApplyUpgrade(int tierIndex)
        {
            if (IsServer)
                ApplyUpgradeInternal(tierIndex);
            else
                ApplyUpgradeRpc(tierIndex);
        }

        [Rpc(SendTo.Server)]
        private void ApplyUpgradeRpc(int tierIndex) => ApplyUpgradeInternal(tierIndex);

        private void ApplyUpgradeInternal(int tierIndex)
        {
            if (!IsServer) return;
            if (allTiers == null || tierIndex < 0 || tierIndex >= allTiers.Length)
            {
                Debug.LogError($"[HullSystem] TierIndex {tierIndex} non valido.");
                return;
            }

            float oldPercent = HullPercent;
            currentUpgrade = allTiers[tierIndex];
            netMaxHP.Value = currentUpgrade.maxHP;
            netCurrentHP.Value = currentUpgrade.maxHP * oldPercent;
            NotifyDependentSystems();

            Debug.Log($"[HullSystem] Upgrade a T{currentUpgrade.tier} — MaxHP: {currentUpgrade.maxHP}");
        }

        // ===== NOTIFICA SISTEMI DIPENDENTI =====

        private void NotifyDependentSystems()
        {
            float percent = HullPercent * 100f;

            if (ElectricalDegradationManager.Instance != null)
                ElectricalDegradationManager.Instance.SetHullPercent(percent);
            else
                ElectricalDegradationManager.OnInstanceReady += NotifyDegradationOnReady;

            UpdateHullAlarm();
        }

        private void NotifyDegradationOnReady()
        {
            ElectricalDegradationManager.OnInstanceReady -= NotifyDegradationOnReady;
            if (ElectricalDegradationManager.Instance != null)
                ElectricalDegradationManager.Instance.SetHullPercent(HullPercent * 100f);
        }

        private void UpdateHullAlarm()
        {
            if (currentUpgrade == null) return;

            float percent = HullPercent;

            if (!alarmActive && percent < currentUpgrade.criticalThreshold)
            {
                alarmActive = true;
                AlarmSystem.Instance?.RaiseAlarm(
                    AlarmSystem.AlarmSource.HullCritical,
                    AlarmSystem.AlarmSeverity.Critical);
                Debug.LogWarning($"[HullSystem] ⚠ SCAFO CRITICO: {percent * 100f:F1}%");
            }
            else if (alarmActive && percent >= currentUpgrade.criticalHysteresis)
            {
                alarmActive = false;
                AlarmSystem.Instance?.ClearAlarm(AlarmSystem.AlarmSource.HullCritical);
                Debug.Log($"[HullSystem] Scafo rientrato nella soglia: {percent * 100f:F1}%");
            }
        }

        // ===== NETWORKVAR CALLBACK (tutti i client) =====

        private void HandleHPChanged(float previousValue, float newValue)
        {
            OnHullChanged?.Invoke(netCurrentHP.Value, netMaxHP.Value, HullPercent);
        }

        // ===== DEBUG GUI =====

        private void OnGUI()
        {
            if (!showDebugGUI) return;

            int y = 340;
            GUI.Label(new Rect(10, y, 350, 20),
                $"=== HULL: {netCurrentHP.Value:F0}/{netMaxHP.Value:F0} HP ({HullPercent * 100f:F1}%) ===");
            y += 20;

            if (IsServer)
            {
                if (GUI.Button(new Rect(10, y, 130, 22), "Damage -50 HP"))
                    ApplyDamageInternal(50f);
                if (GUI.Button(new Rect(150, y, 130, 22), "Repair +100 HP"))
                    ApplyRepairInternal(100f);
                y += 28;
                if (GUI.Button(new Rect(10, y, 200, 22), "Simulate Critical (-80%)"))
                    ApplyDamageInternal(netMaxHP.Value * 0.8f);
                if (GUI.Button(new Rect(220, y, 130, 22), "Repair Full"))
                    RepairFull();
            }
        }
    }
}