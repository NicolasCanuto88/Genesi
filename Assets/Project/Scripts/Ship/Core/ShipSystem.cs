using UnityEngine;
using System;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Abstract base class for all ship systems (Propulsion, FTL, Shields, etc.)
    /// Integrates with PowerManager and provides upgrade framework
    /// </summary>
    public abstract class ShipSystem : MonoBehaviour, IPowerConsumer
    {
        [Header("System Info")]
        [SerializeField] protected string systemName = "Ship System";
        [SerializeField] protected int currentTier = 1;
        [SerializeField] protected bool isOperational = true;

        [Header("Power Settings")]
        [SerializeField] protected float powerDemand = 0f;
        [SerializeField] protected int priority = 5; // 0-10, 10 = critical
        [SerializeField] protected bool requiresPower = true;

        // References
        protected ShipController ship;
        protected bool isPowered = true;

        // Events
        public event Action<int> OnTierUpgraded; // newTier
        public event Action<float> OnSystemDamaged; // damagePercent (0-1)
        public event Action OnSystemRepaired;
        public event Action OnSystemEnabled;
        public event Action OnSystemDisabled;

        // Properties
        public string SystemName => systemName;
        public int CurrentTier => currentTier;
        public bool IsOperational => isOperational;
        public bool IsPowered => isPowered;
        public float PowerDemand => powerDemand;

        #region Lifecycle

        protected virtual void Start()
        {
            // Auto-register with PowerManager if requires power
            if (requiresPower && PowerManager.Instance != null)
            {
                PowerManager.Instance.RegisterPowerConsumer(this);
            }
        }

        protected virtual void OnDestroy()
        {
            // Unregister from PowerManager
            if (requiresPower && PowerManager.Instance != null)
            {
                PowerManager.Instance.UnregisterPowerConsumer(this);
            }
        }

        protected virtual void Update()
        {
            if (isOperational && isPowered)
            {
                OnSystemUpdate();
            }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize system with reference to ship controller
        /// Called by ShipController.Start()
        /// </summary>
        public virtual void Initialize(ShipController shipController)
        {
            ship = shipController;
            Debug.Log($"[{systemName}] Initialized at Tier {currentTier}");
        }

        #endregion

        #region IPowerConsumer Implementation

        public virtual float GetPowerDemand()
        {
            return (isOperational && requiresPower) ? powerDemand : 0f;
        }

        public virtual int GetPriority()
        {
            return priority;
        }

        public virtual bool IsActive()
        {
            return isOperational && isPowered;
        }

        public virtual bool CanBeDisabled()
        {
            // Critical systems (priority 10) cannot be auto-disabled
            return priority < 10;
        }

        public virtual void SetPowerState(bool isOn)
        {
            bool wasOn = isPowered;
            isPowered = isOn;

            if (wasOn && !isOn)
            {
                OnPowerLost();
                OnSystemDisabled?.Invoke();
            }
            else if (!wasOn && isOn)
            {
                OnPowerRestored();
                OnSystemEnabled?.Invoke();
            }
        }

        public virtual string GetSystemName()
        {
            return systemName;
        }

        #endregion

        #region System Lifecycle (to be overridden)

        /// <summary>
        /// Called every frame when system is operational and powered
        /// Override to implement system-specific behavior
        /// </summary>
        protected virtual void OnSystemUpdate()
        {
            // Override in derived classes
        }

        /// <summary>
        /// Called when power is lost (PowerManager load shedding or damage)
        /// </summary>
        protected virtual void OnPowerLost()
        {
            Debug.LogWarning($"[{systemName}] POWER LOST");
        }

        /// <summary>
        /// Called when power is restored after being lost
        /// </summary>
        protected virtual void OnPowerRestored()
        {
            Debug.Log($"[{systemName}] Power restored");
        }

        #endregion

        #region Upgrade System

        /// <summary>
        /// Apply upgrade data to system
        /// Called by UpgradeManager after purchase validation
        /// </summary>
        public virtual void ApplyUpgrade(int newTier)
        {
            if (newTier <= currentTier)
            {
                Debug.LogWarning($"[{systemName}] Cannot downgrade from Tier {currentTier} to {newTier}");
                return;
            }

            int oldTier = currentTier;
            currentTier = newTier;

            OnUpgradeApplied(oldTier, newTier);
            OnTierUpgraded?.Invoke(newTier);

            Debug.Log($"[{systemName}] Upgraded from Tier {oldTier} to Tier {newTier}");
        }

        /// <summary>
        /// Override to handle tier-specific upgrade logic
        /// </summary>
        protected virtual void OnUpgradeApplied(int oldTier, int newTier)
        {
            // Override in derived classes to apply new stats
        }

        #endregion

        #region Damage & Repair

        /// <summary>
        /// Damage the system (reduces efficiency)
        /// </summary>
        public virtual void TakeDamage(float damagePercent)
        {
            if (!isOperational) return;

            damagePercent = Mathf.Clamp01(damagePercent);

            if (damagePercent >= 1.0f)
            {
                // System destroyed
                isOperational = false;
                Debug.LogError($"[{systemName}] SYSTEM DESTROYED");
            }
            else
            {
                Debug.LogWarning($"[{systemName}] Took {damagePercent * 100f:F0}% damage");
            }

            OnSystemDamaged?.Invoke(damagePercent);
        }

        /// <summary>
        /// Repair the system (restore functionality)
        /// </summary>
        public virtual void Repair(float repairPercent)
        {
            repairPercent = Mathf.Clamp01(repairPercent);

            if (!isOperational && repairPercent >= 1.0f)
            {
                isOperational = true;
                Debug.Log($"[{systemName}] SYSTEM REPAIRED");
                OnSystemRepaired?.Invoke();
            }
        }

        #endregion

        #region Manual Enable/Disable

        /// <summary>
        /// Manually enable/disable system (player control)
        /// </summary>
        public virtual void SetOperationalState(bool enabled)
        {
            if (isOperational == enabled) return;

            isOperational = enabled;

            if (enabled)
            {
                OnSystemEnabled?.Invoke();
                Debug.Log($"[{systemName}] Enabled");
            }
            else
            {
                OnSystemDisabled?.Invoke();
                Debug.Log($"[{systemName}] Disabled");
            }
        }

        #endregion
    }
}
