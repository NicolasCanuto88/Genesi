using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Rev BF — Fase 2a (Danni Differenziati), Stage B.
    /// ScriptableObject di tuning per l'instradamento del danno POST-scudo.
    ///
    /// MODELLO (confermato in workshop, QB1-a modalità SELEZIONE):
    ///   1. Lo scudo assorbe per primo (a monte, dentro ShipDamageRouter).
    ///   2. Il RESIDUO post-scudo si divide in:
    ///        - hull-floor  = residuo × hullFloorFraction  → SEMPRE allo scafo.
    ///        - subsystem   = residuo − hull-floor          → a UN subsystem
    ///                        selezionato con probabilità pesata (se abilitato).
    ///   3. Se la selezione è disabilitata per quella severità (tipico Light) o
    ///      non esiste un bersaglio valido, l'intero residuo va allo scafo.
    ///
    /// Una riga per ImpactSeverity. I pesi sono relativi (il router li normalizza).
    /// I numeri sono un punto di partenza per il playtest, tutti tunabili in
    /// Inspector — nessun valore hardcodato nel codice (invariante SO della casa).
    ///
    /// SETUP: Create → SpaceSurvivor → Damage Distribution Table. Compilare una
    /// riga per Light/Medium/Hard e assegnare l'asset al campo 'table' di
    /// ShipDamageRouter.
    /// </summary>
    [CreateAssetMenu(
        fileName = "DamageDistributionTable",
        menuName = "SpaceSurvivor/Damage Distribution Table")]
    public class DamageDistributionTable : ScriptableObject
    {
        [System.Serializable]
        public struct SubsystemWeight
        {
            [Tooltip("Subsystem selezionabile. Hull viene ignorato qui (è il floor, non un bersaglio di selezione).")]
            public ShipSubsystem target;

            [Tooltip("Peso relativo di selezione. 0 = mai scelto. I pesi sono normalizzati dal router.")]
            [Min(0f)]
            public float weight;
        }

        [System.Serializable]
        public struct SeverityRow
        {
            [Tooltip("Severità d'impatto a cui si applica questa riga.")]
            public ImpactSeverity severity;

            [Tooltip("Quota del residuo post-scudo garantita allo scafo (0–1). " +
                     "1 = tutto il residuo va allo scafo (nessun subsystem).")]
            [Range(0f, 1f)]
            public float hullFloorFraction;

            [Tooltip("Se false, l'intero residuo va allo scafo (nessuna selezione). " +
                     "Tipico per Light: un urto lieve non stacca un sistema.")]
            public bool enableSelection;

            [Tooltip("Pesi di selezione fra i subsystem (Propulsion/FTL). " +
                     "Ignorato se enableSelection = false o vuoto.")]
            public SubsystemWeight[] weights;
        }

        [Tooltip("Una riga per ciascuna ImpactSeverity (Light/Medium/Hard). " +
                 "Severità mancanti usano un fallback sicuro: tutto il residuo allo scafo.")]
        [SerializeField]
        private SeverityRow[] rows = new SeverityRow[0];

        /// <summary>
        /// Ritorna la riga per la severità richiesta. Se assente, ritorna una riga
        /// di fallback conservativa (tutto il residuo allo scafo, selezione off):
        /// degradazione sicura, mai NullRef.
        /// </summary>
        public SeverityRow GetRow(ImpactSeverity severity)
        {
            if (rows != null)
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    if (rows[i].severity == severity)
                        return rows[i];
                }
            }

            return new SeverityRow
            {
                severity = severity,
                hullFloorFraction = 1f,
                enableSelection = false,
                weights = null,
            };
        }
    }
}
