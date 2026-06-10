using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// FTLUpgradeData — ScriptableObject per un tier del FTL Drive.
    /// Crea un asset per ogni tier (es. "FTLUpgrade_T1").
    /// Tutti i valori numerici vengono da qui — mai hardcodati in FTLDrive.
    /// </summary>
    [CreateAssetMenu(
        fileName = "FTLUpgrade_T1",
        menuName  = "SpaceSurvivor/Upgrades/FTL")]
    public class FTLUpgradeData : ScriptableObject
    {
        [Header("Identificazione")]
        public int    tier;
        public string displayName  = "FTL Drive T1";
        public int    purchaseCost = 0;

        [Header("Salute Sistema")]
        [Tooltip("HP massimi del drive FTL (T1 = 100).")]
        public float maxHealth = 100f;

        [Header("Parametri Salto")]
        [Tooltip("Watt assorbiti durante la carica (T1 = 700W).")]
        public float chargeWatts = 700f;

        [Tooltip("Durata della carica in secondi (T1 = 15s).")]
        public float chargeDuration = 15f;

        [Tooltip("Cooldown dopo un salto riuscito in secondi (T1 = 900s = 15 min).")]
        public float cooldownDuration = 900f;

        [Tooltip("Lockout dopo un salto annullato da blackout (T1 = 30s).")]
        public float failureLockoutDuration = 30f;

        [Tooltip("Durata animazione salto in secondi (puramente visiva, M3).")]
        public float jumpTransitionDuration = 2.5f;

        [Tooltip("Range del salto in AU (usato da NavigationSystem M3).")]
        public float rangeAU = 50f;

        [Header("PowerManager")]
        [Tooltip("Priority 10 — non interrompibile automaticamente da PowerManager.\n" +
                 "Se si verifica un blackout durante la carica, FTLDrive lo gestisce via SetPowerState().")]
        public int powerPriority = 10;

        [Header("Soglie Riparazione")]
        [Tooltip(
            "Materiali consumati al completamento di ogni soglia del RepairMinigame.\n" +
            "Tipico T1: soglia 0.5 → 1× Electronic · 0.75 → 1× Coolant · 1.0 → 1×E + 1×C")]
        public RepairThreshold[] repairThresholds;
    }
}
