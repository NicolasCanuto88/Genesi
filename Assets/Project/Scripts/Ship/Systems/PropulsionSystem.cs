using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Propulsion System - handles ship movement and thrust
    /// Supports 4 tiers: Industrial (T1), Plasma (T2), Military Ion (T3), Void Drive (T4)
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PropulsionSystem : ShipSystem
    {
        [Header("Propulsion Configuration")]
        [SerializeField] private PropulsionUpgradeData tier1Data; // Default tier
        [SerializeField] private PropulsionUpgradeData tier2Data;
        [SerializeField] private PropulsionUpgradeData tier3Data;
        [SerializeField] private PropulsionUpgradeData tier4Data;

        [Header("Current Stats")]
        [SerializeField] private float maxSpeed = 100f;
        [SerializeField] private float acceleration = 10f;
        [SerializeField] private float fuelEfficiency = 1.0f;
        [SerializeField] private float maneuverability = 1.0f;

        [Header("Fuel Consumption")]
        [SerializeField] private float baseFuelConsumption = 10f; // fuel/min at 100% thrust
        [SerializeField] private float currentFuelConsumption = 0f;

        [Header("Special Abilities")]
        [SerializeField] private bool hasBoostOvercharge = false;
        [SerializeField] private bool canBoost = true;
        [SerializeField] private float boostCooldownRemaining = 0f;
        
        [SerializeField] private bool hasVoidJump = false;
        [SerializeField] private bool canVoidJump = true;
        [SerializeField] private float voidJumpCooldownRemaining = 0f;

        // Components
        private Rigidbody rb;

        // State
        private Vector3 thrustInput = Vector3.zero;
        private bool isBoosting = false;
        private float boostTimeRemaining = 0f;

        // Properties
        public float CurrentSpeed { get; private set; }
        public float MaxSpeed => isBoosting ? GetCurrentUpgradeData().BoostMaxSpeed : maxSpeed;
        public float CurrentThrust => thrustInput.magnitude;
        public bool IsBoosting => isBoosting;
        public bool CanBoost => hasBoostOvercharge && canBoost && isPowered;
        public bool CanVoidJump => hasVoidJump && canVoidJump && isPowered;

        protected override void Start()
        {
            base.Start();

            rb = GetComponent<Rigidbody>();
            rb.useGravity = false; // Space! No gravity
            rb.linearDamping = 0.5f; // Some drag for feel

            // Apply Tier 1 stats by default
            if (tier1Data != null)
            {
                ApplyUpgradeStats(tier1Data);
            }
            else
            {
                Debug.LogError("[PropulsionSystem] No Tier 1 data assigned! Assign PropulsionUpgradeData in Inspector.");
            }
        }

        protected override void Update()
        {
            base.Update();

            // Update cooldowns
            if (boostCooldownRemaining > 0f)
            {
                boostCooldownRemaining -= Time.deltaTime;
                if (boostCooldownRemaining <= 0f)
                {
                    canBoost = true;
                }
            }

            if (voidJumpCooldownRemaining > 0f)
            {
                voidJumpCooldownRemaining -= Time.deltaTime;
                if (voidJumpCooldownRemaining <= 0f)
                {
                    canVoidJump = true;
                }
            }

            // Boost duration
            if (isBoosting)
            {
                boostTimeRemaining -= Time.deltaTime;
                if (boostTimeRemaining <= 0f)
                {
                    EndBoost();
                }
            }
        }

        protected override void OnSystemUpdate()
        {
            // Calculate current speed
            CurrentSpeed = rb.linearVelocity.magnitude;

            // Calculate fuel consumption
            CalculateFuelConsumption();
        }

        private void FixedUpdate()
        {
            if (!isOperational || !isPowered) return;

            ApplyThrust();
        }

        #region Thrust Control

        /// <summary>
        /// Set thrust input direction (-1 to 1 per axis)
        /// Called by ship pilot input
        /// </summary>
        public void SetThrustInput(Vector3 input)
        {
            thrustInput = Vector3.ClampMagnitude(input, 1f);
        }

        private void ApplyThrust()
        {
            if (thrustInput.magnitude < 0.01f) return;

            // Calculate thrust force
            float currentMaxSpeed = MaxSpeed;
            float currentAccel = acceleration;

            // Apply thrust multiplier if boosting
            if (isBoosting)
            {
                currentAccel *= 2f; // Boost acceleration
            }

            // Calculate target velocity
            Vector3 targetVelocity = thrustInput.normalized * currentMaxSpeed;

            // Accelerate towards target
            Vector3 velocityDelta = targetVelocity - rb.linearVelocity;
            Vector3 thrustForce = velocityDelta.normalized * currentAccel * rb.mass;

            // Clamp to max speed
            if (rb.linearVelocity.magnitude < currentMaxSpeed)
            {
                rb.AddForce(thrustForce, ForceMode.Force);
            }
        }

        #endregion

        #region Fuel Consumption

        private void CalculateFuelConsumption()
        {
            // Base consumption when thrusting
            float thrustPercent = thrustInput.magnitude;
            currentFuelConsumption = baseFuelConsumption * fuelEfficiency * thrustPercent;

            // Boost increases consumption
            if (isBoosting)
            {
                PropulsionUpgradeData currentData = GetCurrentUpgradeData();
                currentFuelConsumption *= currentData.BoostFuelMultiplier;
            }

            // TODO: Integrate with FuelManager to actually consume fuel
            // FuelManager.Instance?.ConsumeFuel(currentFuelConsumption * Time.deltaTime / 60f);
        }

        public float GetFuelConsumptionRate()
        {
            return currentFuelConsumption; // fuel per minute
        }

        #endregion

        #region Special Abilities

        /// <summary>
        /// Activate boost overcharge (Tier 3+)
        /// </summary>
        public bool ActivateBoost()
        {
            if (!CanBoost) return false;

            PropulsionUpgradeData currentData = GetCurrentUpgradeData();
            if (!currentData.HasBoostOvercharge) return false;

            isBoosting = true;
            boostTimeRemaining = currentData.BoostDuration;
            canBoost = false;
            boostCooldownRemaining = currentData.BoostCooldown;

            Debug.Log($"[PropulsionSystem] BOOST ACTIVATED! Max speed: {currentData.BoostMaxSpeed} m/s for {currentData.BoostDuration}s");
            return true;
        }

        private void EndBoost()
        {
            isBoosting = false;
            boostTimeRemaining = 0f;
            Debug.Log("[PropulsionSystem] Boost ended");
        }

        /// <summary>
        /// Perform void jump (Tier 4)
        /// </summary>
        public bool PerformVoidJump(Vector3 targetPosition)
        {
            if (!CanVoidJump) return false;

            PropulsionUpgradeData currentData = GetCurrentUpgradeData();
            if (!currentData.HasVoidJump) return false;

            // Check range
            float distance = Vector3.Distance(transform.position, targetPosition);
            if (distance > currentData.VoidJumpRange)
            {
                Debug.LogWarning($"[PropulsionSystem] Target too far for Void Jump: {distance:F0}m (max: {currentData.VoidJumpRange:F0}m)");
                return false;
            }

            // TODO: Check fuel
            // if (!FuelManager.Instance.CanConsume(currentData.VoidJumpFuelCost)) return false;

            // Perform jump
            transform.position = targetPosition;
            rb.linearVelocity = Vector3.zero; // Reset velocity

            // Consume fuel
            // FuelManager.Instance.ConsumeFuel(currentData.VoidJumpFuelCost);

            // Set cooldown
            canVoidJump = false;
            voidJumpCooldownRemaining = currentData.VoidJumpCooldown;

            Debug.Log($"[PropulsionSystem] VOID JUMP executed! Jumped {distance:F0}m");
            return true;
        }

        #endregion

        #region Upgrade System

        protected override void OnUpgradeApplied(int oldTier, int newTier)
        {
            PropulsionUpgradeData newData = GetUpgradeDataForTier(newTier);
            if (newData == null)
            {
                Debug.LogError($"[PropulsionSystem] No upgrade data found for Tier {newTier}");
                return;
            }

            ApplyUpgradeStats(newData);
        }

        private void ApplyUpgradeStats(PropulsionUpgradeData data)
        {
            maxSpeed = data.MaxSpeed;
            acceleration = data.Acceleration;
            fuelEfficiency = data.FuelEfficiency;
            maneuverability = data.Maneuverability;

            // Special abilities
            hasBoostOvercharge = data.HasBoostOvercharge;
            hasVoidJump = data.HasVoidJump;

            // Update power demand based on tier
            powerDemand = 50f + (currentTier * 25f); // 50W base, +25W per tier

            Debug.Log($"[PropulsionSystem] Stats updated: Speed={maxSpeed}, Accel={acceleration}, FuelEff={fuelEfficiency}x");
        }

        private PropulsionUpgradeData GetCurrentUpgradeData()
        {
            return GetUpgradeDataForTier(currentTier);
        }

        private PropulsionUpgradeData GetUpgradeDataForTier(int tier)
        {
            switch (tier)
            {
                case 1: return tier1Data;
                case 2: return tier2Data;
                case 3: return tier3Data;
                case 4: return tier4Data;
                default:
                    Debug.LogWarning($"[PropulsionSystem] Invalid tier: {tier}, returning Tier 1");
                    return tier1Data;
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Get upgrade data for a specific tier (for UI preview)
        /// </summary>
        public PropulsionUpgradeData GetUpgradeData(int tier)
        {
            return GetUpgradeDataForTier(tier);
        }

        #endregion

        #region Debug

        private void OnGUI()
        {
            if (!Debug.isDebugBuild) return;

            int y = 350;
            GUI.Label(new Rect(10, y, 400, 20), $"=== PROPULSION (Tier {currentTier}) ===");
            y += 20;
            GUI.Label(new Rect(10, y, 400, 20), $"Speed: {CurrentSpeed:F1}/{MaxSpeed:F0} m/s");
            y += 20;
            GUI.Label(new Rect(10, y, 400, 20), $"Thrust: {thrustInput.magnitude:F2} | Fuel: {currentFuelConsumption:F1}/min");
            y += 20;
            GUI.Label(new Rect(10, y, 400, 20), $"Efficiency: {fuelEfficiency:F2}x | Powered: {isPowered}");
            
            if (hasBoostOvercharge)
            {
                y += 20;
                if (isBoosting)
                {
                    GUI.color = Color.cyan;
                    GUI.Label(new Rect(10, y, 400, 20), $"BOOST ACTIVE: {boostTimeRemaining:F1}s remaining");
                }
                else
                {
                    GUI.color = canBoost ? Color.green : Color.yellow;
                    GUI.Label(new Rect(10, y, 400, 20), $"Boost Ready: {(canBoost ? "YES" : $"CD: {boostCooldownRemaining:F0}s")}");
                }
                GUI.color = Color.white;
            }

            if (hasVoidJump)
            {
                y += 20;
                GUI.color = canVoidJump ? Color.magenta : Color.yellow;
                GUI.Label(new Rect(10, y, 400, 20), $"Void Jump: {(canVoidJump ? "READY" : $"CD: {voidJumpCooldownRemaining:F0}s")}");
                GUI.color = Color.white;
            }
        }

        #endregion
    }
}
