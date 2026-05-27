using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Propulsion System upgrade data (ScriptableObject)
    /// Contains stats for each propulsion tier (1-4)
    /// Values from GDD v0.7 - Propulsion System section
    /// </summary>
    [CreateAssetMenu(fileName = "Propulsion_Upgrade", menuName = "Space Survivor/Upgrades/Propulsion")]
    public class PropulsionUpgradeData : UpgradeData
    {
        [Header("Propulsion Stats")]
        [SerializeField] private float maxSpeed = 100f; // m/s
        [SerializeField] private float acceleration = 10f; // m/s²
        [SerializeField] private float fuelEfficiencyMultiplier = 1.0f; // 1.0 = baseline, 0.75 = -25% fuel
        [SerializeField] private float maneuverabilityMultiplier = 1.0f; // Strafe speed modifier

        [Header("Special Abilities (Tier 4)")]
        [SerializeField] private bool hasVoidJump = false;
        [SerializeField] private float voidJumpRange = 10000f; // 10 km instant teleport
        [SerializeField] private float voidJumpCooldown = 900f; // 15 minutes
        [SerializeField] private float voidJumpFuelCost = 50f;

        [Header("Tier-Specific Modifiers")]
        [SerializeField] private bool hasBoostOvercharge = false; // Tier 3 Military Ion
        [SerializeField] private float boostMaxSpeed = 250f; // 30 sec burst
        [SerializeField] private float boostDuration = 30f;
        [SerializeField] private float boostCooldown = 300f; // 5 min
        [SerializeField] private float boostFuelMultiplier = 2.0f; // +100% fuel drain during boost

        // Properties for easy access
        public float MaxSpeed => maxSpeed;
        public float Acceleration => acceleration;
        public float FuelEfficiency => fuelEfficiencyMultiplier;
        public float Maneuverability => maneuverabilityMultiplier;
        
        // Special abilities
        public bool HasVoidJump => hasVoidJump;
        public float VoidJumpRange => voidJumpRange;
        public float VoidJumpCooldown => voidJumpCooldown;
        public float VoidJumpFuelCost => voidJumpFuelCost;
        
        public bool HasBoostOvercharge => hasBoostOvercharge;
        public float BoostMaxSpeed => boostMaxSpeed;
        public float BoostDuration => boostDuration;
        public float BoostCooldown => boostCooldown;
        public float BoostFuelMultiplier => boostFuelMultiplier;

        /// <summary>
        /// Get formatted stats for UI comparison
        /// </summary>
        public string GetStatsText()
        {
            string stats = $"<b>Max Speed:</b> {maxSpeed:F0} m/s\n";
            stats += $"<b>Acceleration:</b> {acceleration:F1} m/s²\n";
            stats += $"<b>Fuel Efficiency:</b> {fuelEfficiencyMultiplier:F2}x";
            
            if (fuelEfficiencyMultiplier < 1.0f)
            {
                stats += $" <color=green>({(1.0f - fuelEfficiencyMultiplier) * 100f:F0}% reduction)</color>";
            }
            else if (fuelEfficiencyMultiplier > 1.0f)
            {
                stats += $" <color=orange>(+{(fuelEfficiencyMultiplier - 1.0f) * 100f:F0}% consumption)</color>";
            }

            stats += $"\n<b>Maneuverability:</b> {maneuverabilityMultiplier:F1}x";

            // Special abilities
            if (hasBoostOvercharge)
            {
                stats += $"\n\n<color=cyan><b>Boost Overcharge:</b></color>";
                stats += $"\n• {boostMaxSpeed:F0} m/s for {boostDuration}s";
                stats += $"\n• Cooldown: {boostCooldown / 60f:F0} min";
            }

            if (hasVoidJump)
            {
                stats += $"\n\n<color=magenta><b>Void Jump:</b></color>";
                stats += $"\n• Range: {voidJumpRange / 1000f:F0} km instant";
                stats += $"\n• Cost: {voidJumpFuelCost:F0} fuel";
                stats += $"\n• Cooldown: {voidJumpCooldown / 60f:F0} min";
            }

            return stats;
        }

        /// <summary>
        /// Get comparison text vs another tier
        /// </summary>
        public string GetComparisonText(PropulsionUpgradeData otherTier)
        {
            if (otherTier == null) return GetStatsText();

            string comparison = "<b>UPGRADE CHANGES:</b>\n\n";

            // Speed
            float speedDiff = maxSpeed - otherTier.maxSpeed;
            comparison += FormatStatChange("Max Speed", speedDiff, "F0", " m/s");

            // Acceleration
            float accelDiff = acceleration - otherTier.acceleration;
            comparison += FormatStatChange("Acceleration", accelDiff, "F1", " m/s²");

            // Fuel efficiency (inverse: lower is better)
            float fuelDiff = otherTier.fuelEfficiencyMultiplier - fuelEfficiencyMultiplier;
            if (Mathf.Abs(fuelDiff) > 0.01f)
            {
                comparison += $"<b>Fuel Efficiency:</b> ";
                if (fuelDiff > 0)
                {
                    comparison += $"<color=green>{fuelDiff * 100f:F0}% better</color>\n";
                }
                else
                {
                    comparison += $"<color=orange>{-fuelDiff * 100f:F0}% worse</color>\n";
                }
            }

            // New abilities
            if (hasBoostOvercharge && !otherTier.hasBoostOvercharge)
            {
                comparison += $"\n<color=cyan><b>NEW: Boost Overcharge</b></color>";
            }

            if (hasVoidJump && !otherTier.hasVoidJump)
            {
                comparison += $"\n<color=magenta><b>NEW: Void Jump</b></color>";
            }

            return comparison;
        }

        private string FormatStatChange(string statName, float diff, string format, string unit)
        {
            if (Mathf.Abs(diff) < 0.01f) return "";

            string line = $"<b>{statName}:</b> ";
            if (diff > 0)
            {
                line += $"<color=green>+{diff.ToString(format)}{unit}</color>\n";
            }
            else
            {
                line += $"<color=orange>{diff.ToString(format)}{unit}</color>\n";
            }

            return line;
        }
    }
}
