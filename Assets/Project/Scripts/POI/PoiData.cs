using UnityEngine;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiData — Milestone 3, Blocco 3, Sottofase 2b.
    ///
    /// Descrittore statico di una CATEGORIA di POI (Point of Interest).
    /// Un'istanza di questo ScriptableObject rappresenta un archetipo (es.
    /// "Relitto abbandonato di piccole dimensioni"), NON un POI concreto
    /// nello spazio. Le istanze runtime del POI sono i PoiInstance
    /// (NetworkBehaviour), che referenziano un PoiData per leggere i loro
    /// parametri statici.
    ///
    /// PATTERN DI USO:
    ///   1. Creare asset via menu Assets → Create → Space Survivor → POI Data
    ///   2. Configurare i campi (tipo, prefab visuale, spawn range, ecc.)
    ///   3. Referenziare l'asset dalla lista pesata di PoiSpawner
    ///   4. PoiSpawner istanzia il prefab associato e lo inizializza con
    ///      questi valori
    ///
    /// SCELTA MINIMALISTA (Sottofase 2b):
    ///   NIENTE lista di loot / composizione / difficoltà scan / signature
    ///   radar / ecc. Lo Scanner in 2b mostra solo "tipo + distanza", niente
    ///   di più. Il potenziamento "Deep Scan" (Scanner T2+) che mostra il
    ///   loot potenziale è registrato come idea futura ma NON implementato
    ///   qui — quando esisterà un LootTable, allora si aggiungerà il campo.
    ///   Aggiungerlo ora in bianco sarebbe cargo culting.
    ///
    /// CAMPI DORMIENTI PER IL FUTURO (Blocco 3 Fase 3+ / Blocco 4):
    ///   I campi della sezione "Collisione (dormiente)" sono presenti da
    ///   ora perché il futuro PoiCollisionSystem li leggerà da PoiData
    ///   senza dover riaprire il prefab. NON usati in 2b: il PoiVisual non
    ///   ha collider e nessun sistema legge questi valori. Servono solo a
    ///   stabilire i default sensati quando il sistema sarà scritto.
    ///
    /// DIPENDE DA: ExternalWorldFollower (Rev T.2) sul prefab visuale — deve
    ///             essere già presente come componente del prefab
    ///             visualPrefab (attivato via SetLogicalOverride dal
    ///             PoiInstance in OnNetworkSpawn).
    /// </summary>
    [CreateAssetMenu(
        fileName = "PoiData_Wreck",
        menuName = "Space Survivor/POI Data",
        order = 100)]
    public class PoiData : ScriptableObject
    {
        // ── Identità ─────────────────────────────────────────────────────────
        [Header("Identità")]
        [Tooltip("Categoria del POI. In Sottofase 2b solo WreckAbandoned è " +
                 "supportato — altri valori sono placeholder per la Fase 3.")]
        [SerializeField] private PoiType type = PoiType.WreckAbandoned;

        [Tooltip("Nome visualizzato nella ScannerUI (es. \"Relitto abbandonato\"). " +
                 "Localizzabile in futuro — per ora stringa libera.")]
        [SerializeField] private string displayName = "Relitto abbandonato";

        // ── Rappresentazione visuale ─────────────────────────────────────────
        [Header("Rappresentazione visuale")]
        [Tooltip("Prefab del PoiVisual — GameObject con mesh + " +
                 "ExternalWorldFollower già attaccato. Il PoiInstance chiamerà " +
                 "SetLogicalOverride sul suo ExternalWorldFollower in " +
                 "OnNetworkSpawn. Nessun collider in Sottofase 2b.")]
        [SerializeField] private GameObject visualPrefab;

        // ── Parametri di spawn ───────────────────────────────────────────────
        [Header("Parametri di spawn (usati da PoiSpawner)")]
        [Tooltip("Distanza minima dalla nave (nello spazio logico) al momento " +
                 "dello spawn. Il POI apparirà oltre questa distanza. Unità: " +
                 "metri logici.")]
        [Min(0f)]
        [SerializeField] private float spawnDistanceMin = 2000f;

        [Tooltip("Distanza massima dalla nave (nello spazio logico) al momento " +
                 "dello spawn. Deve essere >= spawnDistanceMin. Unità: metri.")]
        [Min(0f)]
        [SerializeField] private float spawnDistanceMax = 6000f;

        [Tooltip("Range di pitch (elevazione) rispetto all'orizzontale della " +
                 "nave al momento dello spawn, in gradi. 0 = solo piano " +
                 "orizzontale, 90 = anche direttamente sopra/sotto. Un valore " +
                 "moderato (es. 30°) mantiene i POI in una fascia larga ma " +
                 "prevalentemente frontale/laterale.")]
        [Range(0f, 90f)]
        [SerializeField] private float spawnPitchRangeDeg = 30f;

        // ── Collisione (DORMIENTE — non usata in 2b) ─────────────────────────
        [Header("Collisione (dormiente — usata in Fase 3+)")]
        [Tooltip("[Fase 3+] Distanza sotto la quale un impatto è considerato " +
                 "pieno: massimo danno, freeze forzato. Non usato in 2b.")]
        [Min(0f)]
        [SerializeField] private float hardCollisionRadius = 30f;

        [Tooltip("[Fase 3+] Distanza sotto la quale l'urto è \"leggero\": danno " +
                 "proporzionale a velocità, la nave può ancora manovrare. Non " +
                 "usato in 2b.")]
        [Min(0f)]
        [SerializeField] private float softCollisionRadius = 50f;

        [Tooltip("[Fase 3+] Distanza a cui la UI mostra warning \"TROPPO VICINO, " +
                 "RIDURRE VELOCITÀ\". Non usato in 2b.")]
        [Min(0f)]
        [SerializeField] private float warningRadius = 100f;

        [Tooltip("[Fase 3+] Distanza entro cui il pilota può iniziare la " +
                 "manovra di ancoraggio (attracco). Non usato in 2b.")]
        [Min(0f)]
        [SerializeField] private float dockingRadius = 200f;

        [Tooltip("[Fase 3+] Massa logica del POI, usata per il calcolo del " +
                 "trasferimento di momento durante l'impatto. Unità arbitrarie " +
                 "— il bilanciamento sarà relativo alla massa della nave. Non " +
                 "usato in 2b.")]
        [Min(0.1f)]
        [SerializeField] private float mass = 100f;

        // ── Accessors pubblici ───────────────────────────────────────────────
        public PoiType Type => type;
        public string DisplayName => displayName;
        public GameObject VisualPrefab => visualPrefab;

        public float SpawnDistanceMin => spawnDistanceMin;
        public float SpawnDistanceMax => spawnDistanceMax;
        public float SpawnPitchRangeDeg => spawnPitchRangeDeg;

        // Campi dormienti — esposti già come property per non dover cambiare
        // firma quando il PoiCollisionSystem li userà davvero.
        public float HardCollisionRadius => hardCollisionRadius;
        public float SoftCollisionRadius => softCollisionRadius;
        public float WarningRadius => warningRadius;
        public float DockingRadius => dockingRadius;
        public float Mass => mass;

        // ── Validation ───────────────────────────────────────────────────────
        private void OnValidate()
        {
            if (spawnDistanceMax < spawnDistanceMin)
            {
                spawnDistanceMax = spawnDistanceMin;
            }

            // Coerenza dei raggi dormienti — utile anche se non usati adesso,
            // così quando li useremo non troveremo config sballate.
            if (softCollisionRadius < hardCollisionRadius)
            {
                softCollisionRadius = hardCollisionRadius;
            }
            if (warningRadius < softCollisionRadius)
            {
                warningRadius = softCollisionRadius;
            }
            if (dockingRadius < warningRadius)
            {
                dockingRadius = warningRadius;
            }
        }
    }
}
