using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// PropulsionUpgradeData — ScriptableObject per un tier del sistema di propulsione.
    /// Crea un asset per ogni tier (es. "PropulsionUpgrade_T1").
    /// Tutti i valori numerici vengono da qui — mai hardcodati in PropulsionSystem.
    /// </summary>
    [CreateAssetMenu(
        fileName = "PropulsionUpgrade_T1",
        menuName  = "SpaceSurvivor/Upgrades/Propulsion")]
    public class PropulsionUpgradeData : ScriptableObject
    {
        [Header("Identificazione")]
        public int    tier;
        public string displayName  = "Propulsion System T1";
        public int    purchaseCost = 0;

        [Header("Salute Sistema")]
        [Tooltip("HP massimi del sistema (T1 = 100).")]
        public float maxHealth = 100f;

        [Header("Velocità (ShipMovement — M3)")]
        [Tooltip("Velocità massima in m/s. Moltiplicata per degradazione.")]
        public float maxSpeed = 100f;

        [Header("Consumo Energetico")]
        [Tooltip("Watt consumati in AUTOPILOT. Moltiplicati per degradazione.")]
        public float wattsAutopilot = 50f;

        [Tooltip("Watt consumati in MANUAL. Moltiplicati per degradazione.")]
        public float wattsManual = 80f;

        [Tooltip("Priority PowerManager. 6 = sotto scudi (7), sopra luci (3).")]
        public int   powerPriority = 6;

        [Header("Consumo Carburante (FuelCell/min)")]
        public float fuelPerMinAutopilot = 0.5f;
        public float fuelPerMinManual    = 1.0f;

        [Header("DEGRADED — Moltiplicatori (index 0=Online 1=DegradedLight 2=DegradedHeavy)")]
        [Tooltip("Moltiplicatori velocità per stato di degrado.")]
        public float[] speedMultipliers = { 1.0f, 0.85f, 0.65f };

        [Tooltip("Moltiplicatori watt per stato di degrado.")]
        public float[] wattsMultipliers = { 1.0f, 1.15f, 1.30f };

        [Tooltip("Moltiplicatori fuel/min per stato di degrado.")]
        public float[] fuelMultipliers  = { 1.0f, 1.15f, 1.30f };

        [Header("Soglie Riparazione")]
        [Tooltip(
            "Materiali consumati al completamento di ogni soglia del RepairMinigame.\n" +
            "Tipico T1: soglia 0.5 → 1× MechanicalPart · 0.75 → 1× WireBundle · 1.0 → 1× Mechanical + 1× Wire")]
        public RepairThreshold[] repairThresholds;
    }
}
