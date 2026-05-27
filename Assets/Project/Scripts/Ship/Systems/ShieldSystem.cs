using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Shield System - provides damage absorption
    /// TO BE IMPLEMENTED in Step 4
    /// </summary>
    public class ShieldSystem : ShipSystem
    {
        [Header("Shield Stats (Placeholder)")]
        [SerializeField] private float maxShieldHP = 50f;
        [SerializeField] private float currentShieldHP = 50f;
        [SerializeField] private float absorptionPercent = 0.4f; // 40% absorption

        public float ShieldHP => currentShieldHP;
        public float ShieldPercent => currentShieldHP / maxShieldHP;

        protected override void Start()
        {
            base.Start();
            systemName = "Shield System";
            powerDemand = 25f;
            priority = 6;

            // Tier 0 (No Shields) by default
            if (currentTier == 0)
            {
                isOperational = false;
                maxShieldHP = 0f;
                currentShieldHP = 0f;
            }
        }

        /// <summary>
        /// Absorb damage, return remaining damage that goes to hull
        /// </summary>
        public float AbsorbDamage(float incomingDamage)
        {
            if (!isOperational || !isPowered || currentShieldHP <= 0f)
            {
                return incomingDamage; // No absorption, all damage to hull
            }

            // Calculate absorbed amount
            float absorbed = incomingDamage * absorptionPercent;
            absorbed = Mathf.Min(absorbed, currentShieldHP); // Can't absorb more than current HP

            currentShieldHP -= absorbed;
            currentShieldHP = Mathf.Max(0f, currentShieldHP);

            float remainingDamage = incomingDamage - absorbed;

            Debug.Log($"[ShieldSystem] Absorbed {absorbed:F1} damage, {remainingDamage:F1} passed through");

            return remainingDamage;
        }

        // TODO: Implement full shield mechanics in Step 4
        // - Regeneration delay
        // - Regen rate
        // - Tier 2-4 abilities (Adaptive Resistance, Phase Mode, etc.)
    }
}