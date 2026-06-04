using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ScriptableObject con tutti i valori numerici del Life Support per ogni tier.
    /// Nessun valore hardcodato nei sistemi — tutto passa da qui.
    ///
    /// Crea asset via: Assets > Create > SpaceSurvivor > Life Support Upgrade Data
    /// </summary>
    [CreateAssetMenu(fileName = "LifeSupportT1", menuName = "SpaceSurvivor/Life Support Upgrade Data")]
    public class LifeSupportUpgradeData : UpgradeData
    {
        [Header("Oxygen Generation")]
        [Tooltip("O2 generato al minuto quando il sistema è alimentato (T1: 3.0)")]
        [SerializeField] private float oxygenGenerationPerMinute = 3f;

        [Tooltip("Numero massimo di crew che questo tier può supportare in condizioni normali")]
        [SerializeField] private int maxCrewSupported = 3;

        [Header("Power Settings")]
        [Tooltip("Watt consumati quando operativo (T1: 50W)")]
        [SerializeField] private float powerDemandWatts = 50f;

        [Tooltip("Priority IPowerConsumer — 9 = quasi non shed-dable, mai sotto Life Support")]
        [SerializeField] private int powerPriority = 9;

        [Header("Emergency Thresholds")]
        [Tooltip("Soglia % O2 sotto la quale scatta allarme Emergency (default 20%)")]
        [SerializeField, Range(0.05f, 0.40f)] private float alarmThreshold = 0.20f;

        [Tooltip("Isteresi: la soglia di reset allarme è alarmThreshold + hysteresis (default 5%)")]
        [SerializeField, Range(0.01f, 0.15f)] private float alarmHysteresis = 0.05f;

        [Header("Death Timer")]
        [Tooltip("Secondi di grazia dopo O2 = 0% prima che la crew inizi a morire")]
        [SerializeField] private float deathCountdownSeconds = 60f;

        // ===== Properties =====

        /// <summary>O2 generato al secondo (convertito da perMinute per il tick).</summary>
        public float OxygenGenerationPerSecond => oxygenGenerationPerMinute / 60f;

        public float OxygenGenerationPerMinute => oxygenGenerationPerMinute;
        public int MaxCrewSupported => maxCrewSupported;
        public float PowerDemandWatts => powerDemandWatts;
        public int PowerPriority => powerPriority;
        public float AlarmThreshold => alarmThreshold;
        public float AlarmClearThreshold => alarmThreshold + alarmHysteresis;
        public float DeathCountdownSeconds => deathCountdownSeconds;

        // Valori di riferimento GDD — usati nei commenti e nel tooltip
        // T1: +3.0/min, 50W, priority 9, maxCrew 3
        // T2: +5.0/min, 80W, priority 9, maxCrew 5  (da configurare nell'asset T2)
        // T3: +8.0/min, 120W, priority 9, maxCrew 8
        // T4: +15.0/min, 180W, priority 9, maxCrew 12

        public override string GetFormattedDescription()
        {
            string text = base.GetFormattedDescription();
            text += $"\n\n<b>O2 Generation:</b> {oxygenGenerationPerMinute:F1}/min";
            text += $"\n<b>Power Draw:</b> {powerDemandWatts:F0}W";
            text += $"\n<b>Max Crew:</b> {maxCrewSupported}";
            return text;
        }
    }
}
