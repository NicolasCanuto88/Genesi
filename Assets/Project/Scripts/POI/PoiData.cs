using System.Collections.Generic;
using UnityEngine;
using SpaceSurvivor.Collision;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiData — Milestone 3, Blocco 3 (Rev AB — Blocco 3.2.d D5).
    ///
    /// Descrittore statico di una CATEGORIA di POI (Point of Interest).
    /// Un'istanza di questo ScriptableObject rappresenta un archetipo (es.
    /// "Relitto abbandonato di piccole dimensioni"), NON un POI concreto
    /// nello spazio. Le istanze runtime del POI sono i PoiInstance
    /// (NetworkBehaviour), che referenziano un PoiData per leggere i loro
    /// parametri statici.
    ///
    /// ── MODIFICHE REV AB (Blocco 3.2.d — D5, Compound Collider) ─────────────
    ///
    /// Q5 = B (Coesistenza permanente + rename semantico):
    ///   hardCollisionRadius → approximateRadius (property ApproximateRadius).
    ///   Non è più il raggio di collisione hard (sostituito da collisionVolumes),
    ///   ma resta come RAGGIO APPROSSIMATO usato per consumer non-collisionali
    ///   (spacing tra POI nel PoiSpawner, range di scanner, radar UI). Il grep
    ///   esaustivo Rev AB (32 righe totali su 5 file) ha confermato: nessun
    ///   consumer di HardCollisionRadius al di fuori dei sistemi di collisione,
    ///   quindi il rename è puro cambio nomenclatura per allineare il nome
    ///   alla semantica reale.
    ///
    /// Q1-Q3 = OBB+Sphere / compound / Position-Rotation-Scale:
    ///   Aggiunto collisionVolumes (List&lt;CompoundVolume&gt;). Il PoiInstance
    ///   proxya questa lista al PoiCollisionResolver e al DockingController via
    ///   CompoundColliderMath.ClampAgainstCompound.
    ///
    /// Q6 = B (DockingAnchor Transform separato):
    ///   RIMOSSO dockingApproachDirection. La direzione di approccio è ora
    ///   definita da un GameObject figlio "DockingAnchor" sul prefab POI
    ///   (transform.forward → PoiInstance.DockingAnchorForwardWorld). La
    ///   collisione descrive "dove sto"; il docking descrive "come mi avvicino".
    ///   Sono responsabilità semanticamente ortogonali.
    ///
    /// ── CAMPI ATTIVI (post-Rev AB) ──────────────────────────────────────────
    ///
    /// Fase 3 Blocco 3.1 (Ancoraggio):
    ///   - dockingRadius: raggio dentro cui l'ancoraggio è disponibile
    ///     (candidato ancorabile — usato da AnchorSystem).
    ///   - dockingConeAngleDeg, coneVisibilityRadius: geometria del cono
    ///     visuale di attracco.
    ///
    /// Fase 3 Blocco 3.2 (Impatto):
    ///   - collisionVolumes (Rev AB): geometria di collisione compound.
    ///   - approximateRadius (Rev AB, rename): raggio approssimato per
    ///     consumer non-collisionali.
    ///   - softCollisionRadius, warningRadius: soglie per UI/audio (Blocco 3.2.d
    ///     futuro — Blocco Feedback teatrale).
    ///   - mass: trasferimento momento POI ↔ nave.
    ///   - impactDamageMultiplier: modulazione danno per-POI.
    ///
    /// DIPENDE DA: ExternalWorldFollower (Rev T.2) sul prefab visuale.
    ///             CompoundVolume (SpaceSurvivor.Collision, Rev AB).
    /// </summary>
    [CreateAssetMenu(
        fileName = "PoiData_Wreck",
        menuName = "SpaceSurvivor/POI Data",
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
                 "OnNetworkSpawn. In Rev AB il prefab POI deve ANCHE avere un " +
                 "GameObject figlio con componente DockingAnchor (posiziona il " +
                 "punto di attracco + forward = direzione di approccio).")]
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
                 "orizzontale, 90 = anche direttamente sopra/sotto.")]
        [Range(0f, 90f)]
        [SerializeField] private float spawnPitchRangeDeg = 30f;

        // ── Collisione (Rev AB — Compound Collider) ──────────────────────────
        [Header("Collisione compound (Rev AB — Blocco 3.2.d D5)")]
        [Tooltip("Lista di volumi primitivi (OBB / Sphere) che compongono la " +
                 "geometria di collisione del POI. Definiti in LOCAL space " +
                 "rispetto a LogicalPosition + LogicalRotation del PoiInstance.\n\n" +
                 "Esempi:\n" +
                 "- Placeholder cubo (lato 40u): 1 OBB, localPos=(0,0,0), " +
                 "localEuler=(0,0,0), scale=(40,40,40). halfExtents = 20 su " +
                 "ogni asse.\n" +
                 "- Meteorite sferico (raggio 15u): 1 Sphere, localPos=(0,0,0), " +
                 "scale=(30,30,30). Diametro = 30u (scale.x, Y/Z ignorati).\n" +
                 "- Relitto-astronave: fusoliera (OBB lungo Z) + ali (2 OBB " +
                 "laterali sottili) + eventuali motori posteriori.\n\n" +
                 "Se vuota → nessuna collisione (il POI è un fantasma, la nave " +
                 "attraversa). Sconsigliato in gameplay reale — segnala tramite " +
                 "warning nel resolver.")]
        [SerializeField] private List<CompoundVolume> collisionVolumes = new List<CompoundVolume>();

        [Tooltip("[Rev AB — ex hardCollisionRadius, rinominato] Raggio " +
                 "APPROSSIMATO del POI, in unità logiche. Usato da consumer " +
                 "non-collisionali (spacing tra POI in PoiSpawner, range " +
                 "scanner, radar UI, isteresi rilascio latch nel resolver). " +
                 "NON è più usato dalla collisione hard (sostituito da " +
                 "collisionVolumes). Semanticamente: sfera che approssima " +
                 "l'ingombro del compound, sufficiente quando la precisione " +
                 "geometrica non è critica.\n\n" +
                 "Per il placeholder cubo lato 40u: approximateRadius=30 " +
                 "(coerente con Rev AA hardCollisionRadius = 30u). Per un " +
                 "relitto-astronave lungo 80u: approximateRadius ≈ 40 (metà " +
                 "lunghezza massima).")]
        [Min(0f)]
        [SerializeField] private float approximateRadius = 30f;

        // ── Ancoraggio (Fase 3 Blocco 3.1) ──────────────────────────────────
        [Header("Ancoraggio (Fase 3 Blocco 3.1)")]
        [Tooltip("[Fase 3.1] Distanza entro cui l'ancoraggio è disponibile — " +
                 "AnchorSystem tratta questo POI come candidato ancorabile " +
                 "quando la nave si trova entro questo raggio (in aggiunta a " +
                 "ScanState >= Detected).")]
        [Min(0f)]
        [SerializeField] private float dockingRadius = 200f;

        [Tooltip("[Fase 3.1] Apertura totale del cono di approccio, in gradi. " +
                 "Il pilota può iniziare docking solo se si trova entro questo " +
                 "cono (metà angolo per lato) attorno all'asse di approccio del " +
                 "POI (ora derivato dal DockingAnchor forward, Rev AB Q6=B). " +
                 "Default 60° = 30° per lato. Coerente col cono visuale (mesh " +
                 "trasparente) sul prefab: quello che il pilota vede DEVE " +
                 "matchare questo valore.")]
        [Range(10f, 180f)]
        [SerializeField] private float dockingConeAngleDeg = 60f;

        [Tooltip("[Fase 3.1 — D5, Rev W] Distanza entro cui il MeshRenderer del " +
                 "cono di attracco (GameObject 'Cylinder' del prefab POI) è " +
                 "abilitato. Sopra questo raggio il cono è invisibile a tutti, " +
                 "sotto è visibile SOLO alla camera del pilota locale seduto.\n\n" +
                 "Default 200m: allineato a dockingRadius per far coincidere " +
                 "\"entri nella zona di attracco\" con \"vedi il cono\".")]
        [Min(0f)]
        [SerializeField] private float coneVisibilityRadius = 200f;

        // ── Collisione soft / warning (Fase 3.2.d futuro) ────────────────────
        [Header("Feedback (Fase 3.2.d futuro)")]
        [Tooltip("[Fase 3.2.d] Distanza sotto la quale l'urto è \"leggero\": " +
                 "danno proporzionale a velocità, la nave può ancora manovrare. " +
                 "Non usato in Rev AB.")]
        [Min(0f)]
        [SerializeField] private float softCollisionRadius = 50f;

        [Tooltip("[Fase 3.2.d] Distanza a cui la UI mostra warning \"TROPPO " +
                 "VICINO, RIDURRE VELOCITÀ\". Non usato in Rev AB.")]
        [Min(0f)]
        [SerializeField] private float warningRadius = 100f;

        // ── Fisica ───────────────────────────────────────────────────────────
        [Header("Fisica")]
        [Tooltip("[Fase 3.2] Massa logica del POI, usata per il calcolo del " +
                 "trasferimento di momento durante l'impatto. Unità arbitrarie " +
                 "— il bilanciamento sarà relativo alla massa della nave.")]
        [Min(0.1f)]
        [SerializeField] private float mass = 100f;

        [Tooltip("[Fase 3.2.a — Blocco 3.2] Moltiplicatore per-POI applicato al " +
                 "danno da impatto sulla nave. Formula (ShipImpactHandler):\n" +
                 "  damage = v² × hullDamagePerImpactSquared × impactDamageMultiplier\n" +
                 "Default 1.0 = comportamento neutro. Usare < 1 per POI " +
                 "\"morbidi\" (asteroide vetroso, detrito sospeso: 0.3–0.6). " +
                 "Usare > 1 per POI \"blindati\" (relitto militare, roccia dura: " +
                 "1.5–2.5). NON influisce sul trasferimento di momento al POI " +
                 "(quello dipende da Mass).")]
        [Min(0f)]
        [SerializeField] private float impactDamageMultiplier = 1f;

        // ── Relitto — O2 residuo (Rev BG - Stage B) ──────────────────────────
        [Header("Relitto - O2 residuo")]
        [Tooltip("[Rev BG] Riserva di O2 residuo recuperabile dal relitto tramite il " +
                 "pump (Harvest -> tank nave). Stessa scala del tank nave (0-100). Ha " +
                 "senso solo per POI di tipo WreckAbandoned; per gli altri lascia 0. " +
                 "Default 40 = ~40 punti recuperabili nel tank nave.")]
        [Min(0f)]
        [SerializeField] private float wreckO2ReserveInitial = 40f;

        // ── Scanner Info-per-Tier (Rev BH — Fase 2b, D29) ────────────────────
        //
        // Dati statici (per-archetipo) rivelati dallo scan attivo secondo il
        // tier dello scanner. Mappa spec D29:
        //   T1 = tipo/massa/distanza  → già coperti da Type/Mass + geometria scan
        //   T2 = composizione (QUI, reale) + O2 sì/no (da WreckOxygenReserve)
        //   T3 = quantità O2 (da WreckOxygenReserve.Residual, live) + nemici sì/no (STUB)
        //   T4 = blueprint + sistemi relitto + layout (STUB)
        // I campi marcati STUB sono placeholder di design fino al Combat (M4.7):
        // "nemici" diventerà un conteggio LIVE per-istanza (spec D29 Q4: info
        // combat dinamica, non statica); blueprint/sistemi/layout si legheranno
        // a contenuto di relitto reale quando esisterà.
        [Header("Scanner Info-per-Tier (Rev BH - D29)")]
        [Tooltip("[T2 - reale] Composizione materiale del POI mostrata dallo scan " +
                 "attivo T2 (es. \"Lega di titanio, scafo militare\"). Stringa libera, " +
                 "localizzabile in futuro come DisplayName. Vuota = \"Sconosciuta\".")]
        [TextArea(1, 3)]
        [SerializeField] private string composition = "";

        [Tooltip("[T3 - STUB] Presenza nemici rivelata dallo scan attivo T3 " +
                 "(sì/no). PLACEHOLDER di design: al Combat (M4.7) sarà sostituito " +
                 "da un conteggio nemici LIVE per-istanza (spec D29 Q4: info " +
                 "combat dinamica, live-only, decade). In 2b serve solo a validare " +
                 "la riga UI T3.")]
        [SerializeField] private bool stubHasEnemies = false;

        [Tooltip("[T4 - STUB] Blueprint sbloccato dallo scan attivo T4 (Oracle). " +
                 "Assorbe il vecchio Fold Drive Blueprint (D29 Q1-b). PLACEHOLDER " +
                 "di design fino a contenuto relitto reale.")]
        [TextArea(1, 3)]
        [SerializeField] private string stubBlueprintInfo = "";

        [Tooltip("[T4 - STUB] Sistemi attivi del relitto rivelati dallo scan T4. " +
                 "PLACEHOLDER di design fino a contenuto relitto reale.")]
        [TextArea(1, 3)]
        [SerializeField] private string stubShipSystemsInfo = "";

        [Tooltip("[T4 - STUB] Layout interno del relitto rivelato dallo scan T4. " +
                 "PLACEHOLDER di design fino a boarding/EVA reale.")]
        [TextArea(1, 3)]
        [SerializeField] private string stubLayoutInfo = "";

        // ── Accessors pubblici ───────────────────────────────────────────────
        public PoiType Type => type;
        public string DisplayName => displayName;
        public GameObject VisualPrefab => visualPrefab;

        public float SpawnDistanceMin => spawnDistanceMin;
        public float SpawnDistanceMax => spawnDistanceMax;
        public float SpawnPitchRangeDeg => spawnPitchRangeDeg;

        /// <summary>
        /// Rev AB — Lista dei volumi di collisione compound. Definiti in LOCAL
        /// space rispetto a LogicalPosition + LogicalRotation del PoiInstance.
        /// Trasformazione local→world eseguita al volo da CompoundColliderMath.
        /// </summary>
        public IReadOnlyList<CompoundVolume> CollisionVolumes => collisionVolumes;

        /// <summary>
        /// Rev AB (ex HardCollisionRadius, rinominato per allineare nome a
        /// semantica). Raggio APPROSSIMATO del POI in unità logiche. Usato da
        /// consumer non-collisionali. La collisione hard usa collisionVolumes.
        /// </summary>
        public float ApproximateRadius => approximateRadius;

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
        /// </summary>
        public float DockingConeMinDot =>
            Mathf.Cos(dockingConeAngleDeg * 0.5f * Mathf.Deg2Rad);

        public float Mass => mass;
        public float ImpactDamageMultiplier => impactDamageMultiplier;

        /// <summary>[Rev BG] O2 residuo iniziale recuperabile dal relitto (scala 0-100
        /// del tank nave). Consumato dal WreckOxygenPump in modalita Harvest.</summary>
        public float WreckO2ReserveInitial => wreckO2ReserveInitial;

        // ── Accessors Scanner Info-per-Tier (Rev BH — D29) ───────────────────
        /// <summary>[T2 reale] Composizione materiale mostrata dallo scan T2.</summary>
        public string Composition => composition;
        /// <summary>[T3 STUB] Presenza nemici (placeholder → conteggio live a Combat M4.7).</summary>
        public bool StubHasEnemies => stubHasEnemies;
        /// <summary>[T4 STUB] Blueprint (assorbe Fold Drive Blueprint, D29 Q1-b).</summary>
        public string StubBlueprintInfo => stubBlueprintInfo;
        /// <summary>[T4 STUB] Sistemi attivi del relitto.</summary>
        public string StubShipSystemsInfo => stubShipSystemsInfo;
        /// <summary>[T4 STUB] Layout interno del relitto.</summary>
        public string StubLayoutInfo => stubLayoutInfo;

        // ── Validation ───────────────────────────────────────────────────────
        private void OnValidate()
        {
            if (spawnDistanceMax < spawnDistanceMin)
            {
                spawnDistanceMax = spawnDistanceMin;
            }

            // Rev AB — hardCollisionRadius rimosso dalla catena. Ordinamento
            // residuo tra soft/warning/docking (che restano sferici concettuali
            // per Blocchi 3.2.d futuri).
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