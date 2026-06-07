using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Dati di upgrade per HullSystem.
    /// Crea asset: Assets > Create > SpaceSurvivor > Hull Upgrade Data
    ///
    /// Tier di riferimento GDD:
    ///   T1: 500 HP  · cost 0 cr    (nave base)
    ///   T2: 800 HP  · cost 1200 cr
    ///   T3: 1200 HP · cost 2800 cr
    ///   T4: 2000 HP · cost 5500 cr
    /// </summary>
    [CreateAssetMenu(fileName = "HullUpgradeData_T1",
                     menuName = "SpaceSurvivor/Hull Upgrade Data")]
    public class HullUpgradeData : ScriptableObject
    {
        [Header("Tier Info")]
        [Tooltip("Numero del tier (1–4).")]
        public int tier = 1;

        [Header("Hull Stats")]
        [Tooltip("HP massimi dello scafo.")]
        public float maxHP = 500f;

        [Tooltip("HP riparati al secondo durante riparazione in stazione (non in volo).")]
        public float repairRatePerSecond = 50f;

        [Header("Economy")]
        [Tooltip("Costo acquisto upgrade in crediti.")]
        public float purchaseCost = 0f;

        [Header("Alarm Thresholds")]
        [Tooltip("Percentuale sotto cui scatta HullCritical (0–1).")]
        [Range(0f, 1f)] public float criticalThreshold = 0.20f;

        [Tooltip("Percentuale sopra cui HullCritical viene clearato — isteresi (0–1).")]
        [Range(0f, 1f)] public float criticalHysteresis = 0.25f;
    }
}
