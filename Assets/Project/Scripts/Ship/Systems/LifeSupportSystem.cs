using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Life Support System - manages oxygen generation
    /// Now extends ShipSystem for consistency
    /// HIGH PRIORITY - cannot be disabled automatically
    /// </summary>
    public class LifeSupportSystem : ShipSystem
    {
        [Header("Oxygen Settings")]
        [SerializeField] private float oxygenGenerationRate = 10f; // Units per second when powered
        [SerializeField] private float currentOxygenLevel = 100f;
        [SerializeField] private float maxOxygenLevel = 100f;
        [SerializeField] private float oxygenConsumptionRate = 1f; // Always consuming

        // Properties
        public float OxygenPercentage => currentOxygenLevel / maxOxygenLevel;

        protected override void Start()
        {
            base.Start();

            // Life Support specific settings
            systemName = "Life Support System";
            powerDemand = 150f;
            priority = 10; // Critical - cannot be auto-disabled
        }

        protected override void OnSystemUpdate()
        {
            // Generate oxygen when powered
            currentOxygenLevel += oxygenGenerationRate * Time.deltaTime;
            currentOxygenLevel = Mathf.Min(maxOxygenLevel, currentOxygenLevel);

            // Always consume oxygen (crew breathing) - even when unpowered
            currentOxygenLevel -= oxygenConsumptionRate * Time.deltaTime;
            currentOxygenLevel = Mathf.Max(0f, currentOxygenLevel);

            // Critical warning
            if (currentOxygenLevel < 20f)
            {
                Debug.LogWarning($"[LifeSupport] OXYGEN CRITICAL: {currentOxygenLevel:F1}%");
            }
        }

        protected override void OnPowerLost()
        {
            base.OnPowerLost();
            Debug.LogWarning("[LifeSupport] POWER LOST - Oxygen generation stopped!");
        }

        // Debug
        private void OnGUI()
        {
            if (!Debug.isDebugBuild) return;

            int y = 180;
            GUI.Label(new Rect(10, y, 300, 20), $"=== LIFE SUPPORT ===");
            y += 20;
            GUI.Label(new Rect(10, y, 300, 20), $"Oxygen: {currentOxygenLevel:F1}% / {maxOxygenLevel}%");
            y += 20;
            GUI.Label(new Rect(10, y, 300, 20), $"Powered: {isPowered} | Operational: {isOperational}");
            y += 20;
            GUI.Label(new Rect(10, y, 300, 20), $"Generation: {(isPowered && isOperational ? oxygenGenerationRate : 0):F1}/s");
        }
    }
}