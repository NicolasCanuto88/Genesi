using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// WreckOperationsTuning — parametri dei sistemi Relitto (Rev BG - Stage B):
    /// ombelicale (consumo potenza) e pump O2. Nessun valore hardcodato: tutto qui,
    /// come da paletto. Crea un asset via Assets > Create > Space Survivor.
    /// </summary>
    [CreateAssetMenu(fileName = "WreckOperationsTuning",
                     menuName = "SpaceSurvivor/Wreck Operations Tuning")]
    public class WreckOperationsTuning : ScriptableObject
    {
        [Header("Ombelicale — consumo potenza (IPowerConsumer)")]
        [Tooltip("Watt di base per alimentare un relitto, indipendenti dalla massa.")]
        [Min(0f)] public float umbilicalBaseWatt = 50f;

        [Tooltip("Watt aggiuntivi per unita di massa del POI (PoiData.Mass).")]
        [Min(0f)] public float umbilicalWattPerMass = 1.5f;

        [Tooltip("Tetto massimo di consumo dell'ombelicale (W): evita di sfondare il " +
                 "reserve su relitti molto massicci. Con base 50 e 1.5/massa, il tetto " +
                 "250 si raggiunge attorno a massa ~133.")]
        [Min(0f)] public float umbilicalMaxWatt = 250f;

        [Tooltip("Priorita IPowerConsumer. Convenzione PowerManager: valore BASSO = " +
                 "sheddato per primo in deficit; sotto 10 = spento in blackout. " +
                 "L'ombelicale e un lusso: tienila bassa (sotto luci/vitali).")]
        public int umbilicalPriority = 2;

        [Header("Pump O2")]
        [Tooltip("Velocita di travaso in Harvest (residuo relitto -> tank nave), in " +
                 "punti/secondo sulla scala 0-100. Default 5 -> ~8 s per 40 di residuo.")]
        [Min(0f)] public float harvestRatePerSecond = 5f;
    }
}