using UnityEngine;
using Unity.Netcode;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// LifeSupportConsumer — Milestone 2
    /// Sostituisce LifeSupportSystem.cs (ora deprecato).
    ///
    /// RESPONSABILITÀ:
    ///   - Implementa IPowerConsumer → si registra con PowerManager
    ///   - Quando alimentato: chiama OxygenSystem.AddGenerationSource()
    ///   - Quando perde alimentazione: chiama OxygenSystem.RemoveGenerationSource()
    ///   - Legge tutti i valori numerici da LifeSupportUpgradeData (ScriptableObject)
    ///   - Gestisce upgrade di tier a runtime
    ///
    /// PATTERN OnInstanceReady (doppia catena):
    ///   1. PowerManager.OnInstanceReady → InitWithPowerManager()
    ///   2. OxygenSystem.OnInstanceReady → InitWithOxygenSystem()
    ///
    /// ⚠️  ATTENZIONE: ShipSystem è ancora MonoBehaviour puro in M2.
    ///     LifeSupportConsumer NON estende ShipSystem ma è NetworkBehaviour diretto,
    ///     così rispetta la regola "ogni nuovo sistema M2 è NetworkBehaviour".
    ///     La migrazione di ShipSystem a NetworkBehaviour è debito tecnico M3.
    /// </summary>
    public class LifeSupportConsumer : NetworkBehaviour, IPowerConsumer
    {
        [Header("Upgrade Data (ScriptableObject)")]
        [Tooltip("Assegna l'asset LifeSupportT1 (index 0 = T1, index 1 = T2, ecc.)")]
        [SerializeField] private LifeSupportUpgradeData[] allTiers;

        [Tooltip("Tier attivo all'avvio (0-based index di allTiers)")]
        [SerializeField] private int startingTierIndex = 0;

        [Header("Initial State")]
        [SerializeField] private bool startPowered = true;

        // Tier correntemente attivo (risolto da allTiers)
        private LifeSupportUpgradeData upgradeData;

        // ===== Stato runtime (server-only) =====

        private bool isPowered = false;
        private bool isRegisteredWithPowerManager = false;
        private PowerManager powerManager;
        private OxygenSystem oxygenSystem;

        // Cache del rate corrente per AddGenerationSource / RemoveGenerationSource simmetrico
        private float cachedGenerationRate = 0f;

        // ===== Properties =====

        public bool IsPowered => isPowered;
        public LifeSupportUpgradeData CurrentUpgradeData => upgradeData;
        public int CurrentTierIndex => upgradeData != null ? upgradeData.Tier - 1 : 0;

        // ===== NGO Lifecycle =====

        public override void OnNetworkSpawn()
        {
            // Risolvi il tier iniziale dall'array
            if (allTiers != null && allTiers.Length > 0 && startingTierIndex < allTiers.Length)
                upgradeData = allTiers[startingTierIndex];

            // Pattern OnInstanceReady — doppia catena
            if (PowerManager.Instance != null)
                InitWithPowerManager();
            else
                PowerManager.OnInstanceReady += InitWithPowerManager;

            if (OxygenSystem.Instance != null)
                InitWithOxygenSystem();
            else
                OxygenSystem.OnInstanceReady += InitWithOxygenSystem;
        }

        public override void OnNetworkDespawn()
        {
            // Unsubscribe sempre
            PowerManager.OnInstanceReady -= InitWithPowerManager;
            OxygenSystem.OnInstanceReady -= InitWithOxygenSystem;

            // Cleanup server-only
            if (IsServer)
            {
                if (isPowered && oxygenSystem != null)
                    oxygenSystem.RemoveGenerationSource(cachedGenerationRate);

                if (isRegisteredWithPowerManager && powerManager != null)
                    powerManager.UnregisterPowerConsumer(this);
            }
        }

        // ===== Init chain =====

        private void InitWithPowerManager()
        {
            PowerManager.OnInstanceReady -= InitWithPowerManager;
            powerManager = PowerManager.Instance;

            if (!IsServer) return;

            powerManager.RegisterPowerConsumer(this);
            isRegisteredWithPowerManager = true;

            // Stato iniziale
            isPowered = startPowered;

            // Se parte alimentato e OxygenSystem è già pronto, attiva subito
            if (isPowered && oxygenSystem != null)
                ActivateGeneration();

            Debug.Log($"[LifeSupportConsumer] Registered with PowerManager (Tier {upgradeData?.Tier ?? 0})");
        }

        private void InitWithOxygenSystem()
        {
            OxygenSystem.OnInstanceReady -= InitWithOxygenSystem;
            oxygenSystem = OxygenSystem.Instance;

            if (!IsServer) return;

            // Configura le soglie di allarme sull'OxygenSystem
            if (upgradeData != null)
            {
                oxygenSystem.SetAlarmThresholds(
                    upgradeData.AlarmThreshold,
                    upgradeData.AlarmClearThreshold,
                    upgradeData.DeathCountdownSeconds);
            }

            // Se era già alimentato (PowerManager pronto prima), attiva la generazione
            if (isPowered)
                ActivateGeneration();

            Debug.Log("[LifeSupportConsumer] Connected to OxygenSystem");
        }

        // ===== IPowerConsumer =====

        public float GetPowerDemand()
        {
            if (upgradeData == null) return 0f;
            return upgradeData.PowerDemandWatts;
        }

        public int GetPriority()
        {
            if (upgradeData == null) return 9;
            return upgradeData.PowerPriority;
        }

        public bool IsActive() => isPowered;

        public bool CanBeDisabled()
        {
            // Priority 9 = non shed-dable automaticamente da PowerManager
            // (solo priority < 10 vengono auto-disabilitati, vedi PowerManager.ShedLoad)
            if (upgradeData == null) return false;
            return upgradeData.PowerPriority < 10;
        }

        public void SetPowerState(bool isOn)
        {
            if (!IsServer) return;
            if (isPowered == isOn) return;

            isPowered = isOn;

            if (isOn)
            {
                OnPowerGained();
            }
            else
            {
                OnPowerLost();
            }
        }

        public string GetSystemName() => "Life Support";

        // ===== Logica alimentazione =====

        private void OnPowerGained()
        {
            if (oxygenSystem == null) return;
            ActivateGeneration();
            Debug.Log("[LifeSupportConsumer] Power ON — O2 generation started");
        }

        private void OnPowerLost()
        {
            if (oxygenSystem == null) return;
            DeactivateGeneration();
            Debug.LogWarning("[LifeSupportConsumer] Power OFF — O2 generation stopped!");
        }

        private void ActivateGeneration()
        {
            if (upgradeData == null || oxygenSystem == null) return;
            cachedGenerationRate = upgradeData.OxygenGenerationPerSecond;
            oxygenSystem.AddGenerationSource(cachedGenerationRate);
        }

        private void DeactivateGeneration()
        {
            if (oxygenSystem == null) return;
            oxygenSystem.RemoveGenerationSource(cachedGenerationRate);
            cachedGenerationRate = 0f;
        }

        // ===== Upgrade a runtime =====

        /// <summary>
        /// Applica un nuovo tier di upgrade a runtime.
        /// Passa l'indice del tier (0-based) — mai il ScriptableObject direttamente via RPC.
        /// Il server risolve l'asset dall'array allTiers locale.
        /// </summary>
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
            if (allTiers == null || tierIndex < 0 || tierIndex >= allTiers.Length)
            {
                Debug.LogWarning($"[LifeSupportConsumer] Tier index {tierIndex} non valido");
                return;
            }

            LifeSupportUpgradeData newData = allTiers[tierIndex];
            if (newData == null) return;

            if (newData.Tier <= (upgradeData?.Tier ?? 0))
            {
                Debug.LogWarning($"[LifeSupportConsumer] Cannot downgrade to Tier {newData.Tier}");
                return;
            }

            // Se attivo, aggiorna generazione live (rimuovi vecchia, aggiungi nuova)
            if (isPowered && oxygenSystem != null)
                DeactivateGeneration();

            upgradeData = newData;

            // Aggiorna soglie allarme
            if (oxygenSystem != null)
            {
                oxygenSystem.SetAlarmThresholds(
                    upgradeData.AlarmThreshold,
                    upgradeData.AlarmClearThreshold,
                    upgradeData.DeathCountdownSeconds);
            }

            if (isPowered && oxygenSystem != null)
                ActivateGeneration();

            Debug.Log($"[LifeSupportConsumer] Upgraded to Tier {upgradeData.Tier} — {upgradeData.OxygenGenerationPerMinute:F1}/min");
        }
    }
}