using UnityEngine;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiData — Milestone 3, Blocco 3, Sottofase 2b (esteso Blocco 3.1
    /// Fase 3 per direzione di approccio all'ancoraggio).
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
    ///
    /// CAMPI ATTIVI IN 3.1 (Fase 3 Blocco 1 — Ancoraggio):
    ///   - dockingApproachDirection: direzione di attracco in local space
    ///     del PoiVisual. Il DockingController proietta la posizione della
    ///     nave su questo asse per calcolare LateralError e AxialDistance.
    ///   - dockingRadius: attivato da AnchorSystem come raggio dentro cui
    ///     l'ancoraggio è disponibile (candidato ancorabile).
    ///
    /// CAMPI ATTIVI IN 3.2 (Fase 3 Blocco 2 — Impatto):
    ///   - hardCollisionRadius: soglia di impatto pieno (danno + inerzia POI)
    ///   - softCollisionRadius: soglia di urto leggero
    ///   - warningRadius: soglia warning UI
    ///   - mass: usata dal calcolo di trasferimento momento POI ↔ nave
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

        // ── Ancoraggio (Fase 3 Blocco 3.1 — attivo) ──────────────────────────
        [Header("Ancoraggio (Fase 3 Blocco 3.1)")]
        [Tooltip("Direzione di approccio all'ancoraggio, in local space del " +
                 "PoiVisual (verrà normalizzata a runtime). Convenzione: la " +
                 "nave deve avvicinarsi al POI lungo questo asse per completare " +
                 "l'attracco. Esempi: (0,1,0) = attracco dall'alto; " +
                 "(1,0,0) = attracco dal lato +X; per stazioni con hangar, " +
                 "puntare verso il portello. Default (0,1,0) = dall'alto, " +
                 "sensato per relitti piatti.")]
        [SerializeField] private Vector3 dockingApproachDirection = Vector3.up;

        // ── Collisione (Fase 3 Blocco 3.2 — dormiente in 3.1) ────────────────
        [Header("Collisione (Fase 3+)")]
        [Tooltip("Distanza sotto la quale un impatto è considerato pieno: " +
                 "massimo danno, freeze forzato. In 3.1 rileva l'evento " +
                 "OnHardCollision (nessun consumer ancora); in 3.2 applica " +
                 "danno e trasferisce inerzia al POI.")]
        [Min(0f)]
        [SerializeField] private float hardCollisionRadius = 30f;

        [Tooltip("[Fase 3.2] Distanza sotto la quale l'urto è \"leggero\": " +
                 "danno proporzionale a velocità, la nave può ancora manovrare. " +
                 "Non usato in 3.1.")]
        [Min(0f)]
        [SerializeField] private float softCollisionRadius = 50f;

        [Tooltip("[Fase 3.2] Distanza a cui la UI mostra warning \"TROPPO " +
                 "VICINO, RIDURRE VELOCITÀ\". Non usato in 3.1.")]
        [Min(0f)]
        [SerializeField] private float warningRadius = 100f;

        [Tooltip("[Fase 3.1] Distanza entro cui l'ancoraggio è disponibile — " +
                 "AnchorSystem tratta questo POI come candidato ancorabile " +
                 "quando la nave si trova entro questo raggio (in aggiunta a " +
                 "ScanState >= Detected).")]
        [Min(0f)]
        [SerializeField] private float dockingRadius = 200f;

        [Tooltip("[Fase 3.1] Apertura totale del cono di approccio, in gradi. " +
                 "Il pilota può iniziare docking solo se si trova entro questo " +
                 "cono (metà angolo per lato) attorno all'asse di approccio del " +
                 "POI (dockingApproachDirection). Default 60° = 30° per lato. " +
                 "Coerente col cono visuale (mesh trasparente) sul prefab: " +
                 "quello che il pilota vede DEVE matchare questo valore, " +
                 "altrimenti la percezione del pilota è dissociata dalla " +
                 "meccanica reale.")]
        [Range(10f, 180f)]
        [SerializeField] private float dockingConeAngleDeg = 60f;

        [Tooltip("[Fase 3.1 — D5, Rev W] Distanza entro cui il MeshRenderer del " +
                 "cono di attracco (GameObject 'Cylinder' del prefab POI) è " +
                 "abilitato. Sopra questo raggio il cono è invisibile a tutti, " +
                 "sotto è visibile SOLO alla camera del pilota locale seduto " +
                 "(vedi PilotStation D9 + layer DockingConeVisual).\n\n" +
                 "Design: il cono serve a comunicare al pilota da che lato " +
                 "attraccare — informazione utile SOLO a distanza operativa. " +
                 "Mostrarlo da lontano rovinerebbe la lettura dello spazio " +
                 "(troppi coni visibili contemporaneamente su POI multipli).\n\n" +
                 "Default 200m: allineato a dockingRadius per far coincidere " +
                 "\"entri nella zona di attracco\" con \"vedi il cono\". " +
                 "Modificabile in inspector per tuning indipendente in playtest " +
                 "senza toccare la gameplay-critical dockingRadius.")]
        [Min(0f)]
        [SerializeField] private float coneVisibilityRadius = 200f;

        [Tooltip("[Fase 3.2] Massa logica del POI, usata per il calcolo del " +
                 "trasferimento di momento durante l'impatto. Unità arbitrarie " +
                 "— il bilanciamento sarà relativo alla massa della nave. Non " +
                 "usato in 3.1.")]
        [Min(0.1f)]
        [SerializeField] private float mass = 100f;

        [Tooltip("[Fase 3.2.a — Blocco 3.2] Moltiplicatore per-POI applicato al " +
                 "danno da impatto sulla nave. Formula (ShipImpactHandler):\n" +
                 "  damage = v² × hullDamagePerImpactSquared × impactDamageMultiplier\n" +
                 "Default 1.0 = comportamento neutro. Usare < 1 per POI " +
                 "\"morbidi\" (asteroide vetroso, detrito sospeso: 0.3–0.6). " +
                 "Usare > 1 per POI \"blindati\" (relitto militare, roccia dura: " +
                 "1.5–2.5). NON influisce sul trasferimento di momento al POI " +
                 "(quello dipende da Mass): un frammento morbido può fare poco " +
                 "danno alla nave ed essere comunque sbalzato via.")]
        [Min(0f)]
        [SerializeField] private float impactDamageMultiplier = 1f;

        // ── Accessors pubblici ───────────────────────────────────────────────
        public PoiType Type => type;
        public string DisplayName => displayName;
        public GameObject VisualPrefab => visualPrefab;

        public float SpawnDistanceMin => spawnDistanceMin;
        public float SpawnDistanceMax => spawnDistanceMax;
        public float SpawnPitchRangeDeg => spawnPitchRangeDeg;

        /// <summary>
        /// Direzione di approccio in LOCAL space del PoiVisual, normalizzata.
        /// Se l'inspector contiene un vettore zero (edge case) restituisce
        /// Vector3.up come fallback sensato.
        /// </summary>
        public Vector3 DockingApproachDirectionLocal
        {
            get
            {
                var d = dockingApproachDirection;
                if (d.sqrMagnitude < 1e-6f) return Vector3.up;
                return d.normalized;
            }
        }

        // Campi dormienti/attivi Fase 3 — property pubbliche.
        public float HardCollisionRadius => hardCollisionRadius;
        public float SoftCollisionRadius => softCollisionRadius;
        public float WarningRadius => warningRadius;
        public float DockingRadius => dockingRadius;
        public float DockingConeAngleDeg => dockingConeAngleDeg;
        public float ConeVisibilityRadius => coneVisibilityRadius;

        /// <summary>
        /// Coseno della metà-apertura del cono di approccio. Calcolato al volo
        /// da dockingConeAngleDeg. Il check server-side in AnchorSystem è:
        ///   Dot(fromPoiToShip.normalized, approachAxisWorld) >= DockingConeMinDot
        /// Con angolo 60° totale (30° per lato): cos(30°) ≈ 0.866.
        /// Con angolo 180° totale (90° per lato = semisfera): cos(90°) = 0.
        /// </summary>
        public float DockingConeMinDot =>
            Mathf.Cos(dockingConeAngleDeg * 0.5f * Mathf.Deg2Rad);

        public float Mass => mass;
        public float ImpactDamageMultiplier => impactDamageMultiplier;

        // ── Validation ───────────────────────────────────────────────────────
        private void OnValidate()
        {
            if (spawnDistanceMax < spawnDistanceMin)
            {
                spawnDistanceMax = spawnDistanceMin;
            }

            // Coerenza dei raggi — utile anche per i campi non ancora usati,
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