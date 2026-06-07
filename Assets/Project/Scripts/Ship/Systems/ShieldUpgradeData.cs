using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Dati di upgrade per ShieldSystem.
    /// Crea asset: Assets > Create > SpaceSurvivor > Shield Upgrade Data
    ///
    /// Tier di riferimento GDD §9.6 + §11:
    ///   T1 Basic    — absorption 40%  · HP  50 · regen 2/s · pause 4s · spinUp 3s · 25W  (87W in tempesta rad.)
    ///   T2 Military — absorption 70%  · HP 150 · regen 5/s · pause 3s · spinUp 2s · 35W  (110W)
    ///   T3 Plasma   — absorption 85%  · HP 300 · regen 8/s · pause 2s · spinUp 1.5s· 50W  (140W)
    ///   T4 Phase    — absorption 95%  · HP 500 · regen 15/s· pause 1.5s· spinUp 1s · 70W  (175W)
    ///
    /// Consumo: W istantanei (non W/min). Il GDD usa "W/min" come notazione
    /// ma intende consumo continuo in W, coerente con il budget PowerManager 1000W.
    /// </summary>
    [CreateAssetMenu(fileName = "ShieldUpgradeData_T1",
                     menuName = "SpaceSurvivor/Shield Upgrade Data")]
    public class ShieldUpgradeData : ScriptableObject
    {
        [Header("Tier Info")]
        public int tier = 1;

        [Header("Shield Stats")]
        [Tooltip("HP massimi degli scudi.")]
        public float maxHP = 50f;

        [Tooltip("Percentuale di assorbimento danno (0–1). T1=0.40, T2=0.70, T3=0.85, T4=0.95")]
        [Range(0f, 1f)] public float absorptionPercent = 0.40f;

        [Header("Regeneration")]
        [Tooltip("HP rigenerati al secondo quando non si ricevono colpi.")]
        public float regenRate = 2f;

        [Tooltip("Secondi di pausa dopo un colpo prima che la regen riprenda.")]
        public float regenPause = 4f;

        [Tooltip("Pausa aggiuntiva dopo riattivazione scudi (scoraggia cycling ON/OFF rapido).")]
        public float reactivationPause = 2f;

        [Header("Activation")]
        [Tooltip("Secondi di spin-up prima che gli scudi siano operativi.")]
        public float spinUpTime = 3f;

        [Header("Power Consumption (W istantanei)")]
        [Tooltip("Consumo in W a scudi operativi, zona normale.")]
        public float powerNormal = 25f;

        [Tooltip("Consumo in W durante combattimento attivo (colpi ricevuti nell'ultimo tick).")]
        public float powerCombat = 40f;

        [Tooltip("Consumo in W in tempesta di radiazioni.")]
        public float powerRadiationStorm = 87f;

        [Tooltip("Consumo in W in tempesta di asteroidi.")]
        public float powerAsteroidStorm = 60f;

        [Header("Priority")]
        [Tooltip("Priority per PowerManager load shedding. Scudi: 7 (più critico delle luci, meno del Life Support).")]
        public int powerPriority = 7;

        [Header("Economy")]
        public float purchaseCost = 5300f;
    }
}
