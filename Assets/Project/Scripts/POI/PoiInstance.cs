using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Collision;
using SpaceSurvivor.Ship;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiInstance — Milestone 3, Blocco 3, Sottofase 2b (moto proprio 3.2.b)
    /// esteso Rev AB (Blocco 3.2.d D5 — Compound Collider).
    ///
    /// NetworkBehaviour server-authoritative che rappresenta un'istanza
    /// concreta di POI (Point of Interest) nello spazio logico della sessione.
    ///
    /// RESPONSABILITÀ:
    ///   1. NetworkVariable server-authoritative: LogicalPosition,
    ///      LogicalRotation, LogicalVelocity, ScanState
    ///   2. Referenziare un PoiData (parametri statici)
    ///   3. Sincronizzare il proprio PoiVisual via SetLogicalOverride
    ///   4. Auto-registrarsi nel PoiRegistry server-side
    ///   5. Esporre eventi:
    ///        - OnScanStateChanged (per-instance, feedback visuale/UI)
    ///        - OnAnyPoiSpawned / OnAnyPoiDespawned (statici, per
    ///          subscriber client-side)
    ///   6. [Blocco 3.2.b] Integrazione server-side del moto proprio.
    ///   7. [Rev AB — Blocco 3.2.d D5] Esporre in world logico:
    ///        - CollisionVolumes (proxy a Data.CollisionVolumes)
    ///        - DockingAnchorPositionWorld
    ///        - DockingAnchorForwardWorld
    ///
    /// ── DOCKING ANCHOR (Rev AB, Q6 = B) ──────────────────────────────────────
    ///
    ///   Fino a Rev AA la direzione di approccio era derivata da
    ///   Data.DockingApproachDirectionLocal (Vector3 su ScriptableObject),
    ///   ruotata dalla rotation del POI. Rev AB introduce il componente
    ///   DockingAnchor come marker su un GameObject figlio del PoiInstance:
    ///   la sua position + forward definiscono ancoraggio e direzione di
    ///   approccio.
    ///
    ///   IMPORTANTE — dove piazzare il DockingAnchor:
    ///     Il DockingAnchor va messo come FIGLIO DIRETTO DEL ROOT PoiInstance,
    ///     non sotto il Visual. Il Visual è mosso a runtime da
    ///     ExternalWorldFollower per rappresentare LogicalPosition; l'anchor
    ///     sotto Visual verrebbe trascinato via. Il root, invece, è statico
    ///     in worldspace: l'anchor come figlio del root ha localPosition
    ///     stabile e semanticamente identifica "punto di attracco in local
    ///     space del centro logico del POI".
    ///
    ///     Il modeler apre il prefab, vede la mesh (sotto Visual, tipicamente
    ///     centrata all'origine locale), e crea il DockingAnchor come figlio
    ///     del root posizionato relativamente al mesh. Nessuna deriva runtime.
    ///
    ///   Cache: risolto UNA VOLTA in OnNetworkSpawn via GetComponentInChildren
    ///   con includeInactive=false. Le property DockingAnchorPositionWorld /
    ///   DockingAnchorForwardWorld trasformano al volo:
    ///     posWorld = LogicalPosition + LogicalRotation * anchorLocalPos
    ///     fwdWorld = LogicalRotation * (anchorLocalRot * Vector3.forward)
    ///
    ///   Fallback (anchor null): warning UNA VOLTA + fallback su convention
    ///   pre-Rev AB (Vector3.up = default vecchio dockingApproachDirection).
    ///
    /// STRUTTURA PREFAB ATTESA (Rev AB):
    ///   PoiInstance_Wreck (root)
    ///   ├─ NetworkObject
    ///   ├─ PoiInstance (questo script)
    ///   ├─ DockingAnchor (GameObject figlio con componente DockingAnchor)
    ///   └─ Visual (child)
    ///      ├─ ExternalWorldFollower
    ///      ├─ Mesh + Renderer
    ///      └─ PoiVisualIndicator
    ///
    /// DIPENDE DA:
    ///   - ExternalWorldFollower (Rev T.2, con SetLogicalOverride)
    ///   - ShipMovement (Instance, letto dal Follower)
    ///   - PoiRegistry (auto-registrazione server-side)
    ///   - DockingAnchor (marker Rev AB, opzionale con fallback)
    ///   - CompoundVolume (proxy a Data.CollisionVolumes)
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class PoiInstance : NetworkBehaviour
    {
        [Header("Riferimenti")]
        [Tooltip("Il PoiData (ScriptableObject) che descrive la categoria di " +
                 "questo POI.")]
        [SerializeField] private PoiData data;

        [Tooltip("Riferimento all'ExternalWorldFollower del GameObject figlio " +
                 "\"Visual\". Assegnare a mano nell'inspector del prefab.")]
        [SerializeField] private ExternalWorldFollower visualFollower;

        // ── Moto proprio (Blocco 3.2.b) ──────────────────────────────────────
        [Header("Moto proprio (Blocco 3.2.b)")]
        [Tooltip("Costante di tempo del damping esponenziale applicato alla " +
                 "velocità logica del POI, in secondi. Ogni FixedUpdate:\n" +
                 "  velocity *= exp(-dt / dampingTau)\n\n" +
                 "Default 30 s: sui primi secondi dopo l'urto il decadimento è " +
                 "impercettibile (feel newtoniano \"spazio\"), su tempi lunghi " +
                 "il POI decelera evitando che si allontani infinitamente dalla " +
                 "playzone.\n\n" +
                 "Impostare a 0 per Newton puro (nessun damping). Impostare a " +
                 "valori bassi (es. 3-5 s) per feel \"attrito nel vuoto\".")]
        [Min(0f)]
        [SerializeField] private float dampingTau = 30f;

        [Tooltip("Soglia di velocità sotto cui il POI viene considerato fermo: " +
                 "la velocità viene azzerata e l'integrazione viene saltata " +
                 "finché non arriva un nuovo AddImpulse.\n\n" +
                 "Default 0.05 u/s (5 cm/s): al di sotto il moto del POI è " +
                 "sotto la soglia percettiva.")]
        [Min(0f)]
        [SerializeField] private float sleepSpeedThreshold = 0.05f;

        // ── NetworkVariable server-authoritative ─────────────────────────────
        private readonly NetworkVariable<Vector3> _logicalPosition =
            new NetworkVariable<Vector3>(
                Vector3.zero,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<Quaternion> _logicalRotation =
            new NetworkVariable<Quaternion>(
                Quaternion.identity,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        /// <summary>
        /// Velocità logica del POI, in unità/secondo nello spazio logico.
        /// Scritta esclusivamente dal server. Replicata a tutti i client.
        /// </summary>
        private readonly NetworkVariable<Vector3> _logicalVelocity =
            new NetworkVariable<Vector3>(
                Vector3.zero,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<PoiScanState> _scanState =
            new NetworkVariable<PoiScanState>(
                PoiScanState.Unknown,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // ── Cache DockingAnchor (Rev AB) ─────────────────────────────────────

        /// <summary>
        /// Anchor Transform risolto UNA VOLTA in OnNetworkSpawn. Null se il
        /// prefab non ha un GameObject figlio con componente DockingAnchor:
        /// in quel caso il warning è emesso una sola volta e le property di
        /// world position/forward usano il fallback pre-Rev AB.
        /// </summary>
        private Transform _dockingAnchorTransform;

        /// <summary>
        /// LocalPosition dell'anchor rispetto al root PoiInstance, cachata
        /// a OnNetworkSpawn per evitare accessi ripetuti a transform (che
        /// implica calcolo di matrici world). Stabile per tutta la vita del
        /// PoiInstance (l'anchor è statico rispetto al root, non è mosso da
        /// nessun componente runtime).
        /// </summary>
        private Vector3 _dockingAnchorLocalPos;

        /// <summary>
        /// LocalRotation dell'anchor rispetto al root PoiInstance.
        /// </summary>
        private Quaternion _dockingAnchorLocalRot;

        /// <summary>
        /// Warning emesso una sola volta (evita spam). Rivalutato ogni OnNetworkSpawn.
        /// </summary>
        private bool _hasWarnedMissingAnchor;

        // ── Accessors pubblici ───────────────────────────────────────────────
        public PoiData Data => data;
        public Vector3 LogicalPosition => _logicalPosition.Value;
        public Quaternion LogicalRotation => _logicalRotation.Value;
        public Vector3 LogicalVelocity => _logicalVelocity.Value;
        public PoiScanState ScanState => _scanState.Value;

        /// <summary>
        /// Rev AB (Blocco 3.2.d D5) — Proxy alla lista di volumi compound
        /// definita in PoiData. Restituisce sempre non-null (lista vuota se
        /// data == null o data.CollisionVolumes == null).
        /// Consumer: PoiCollisionResolver, DockingController via
        /// CompoundColliderMath.
        /// </summary>
        public IReadOnlyList<CompoundVolume> CollisionVolumes
        {
            get
            {
                if (data == null || data.CollisionVolumes == null) return EmptyVolumes;
                return data.CollisionVolumes;
            }
        }

        private static readonly IReadOnlyList<CompoundVolume> EmptyVolumes = new List<CompoundVolume>();

        /// <summary>
        /// Rev AB (Q6 = B) — Posizione WORLD LOGICO del punto di attracco.
        /// Calcolata da LogicalPosition + LogicalRotation * anchorLocalPos.
        /// Se il DockingAnchor non è configurato sul prefab (fallback), ritorna
        /// LogicalPosition (l'attracco converge al centro del POI, comportamento
        /// pre-Rev AB).
        /// </summary>
        public Vector3 DockingAnchorPositionWorld
        {
            get
            {
                if (_dockingAnchorTransform == null) return _logicalPosition.Value;
                return _logicalPosition.Value + _logicalRotation.Value * _dockingAnchorLocalPos;
            }
        }

        /// <summary>
        /// Rev AB (Q6 = B) — Direzione WORLD LOGICO di approccio (forward
        /// dell'anchor, ruotato dalla LogicalRotation). Il pilota arriva
        /// AL POI lungo -DockingAnchorForwardWorld (opposto del forward
        /// dell'anchor, coerente col fatto che il forward "esce" dal POI).
        /// Se il DockingAnchor non è configurato, fallback su
        /// LogicalRotation * Vector3.up (default pre-Rev AB del vecchio
        /// dockingApproachDirection).
        /// </summary>
        public Vector3 DockingAnchorForwardWorld
        {
            get
            {
                if (_dockingAnchorTransform == null)
                {
                    return _logicalRotation.Value * Vector3.up;
                }
                Quaternion worldRot = _logicalRotation.Value * _dockingAnchorLocalRot;
                return worldRot * Vector3.forward;
            }
        }

        // ── Eventi pubblici (per-instance) ───────────────────────────────────
        public event Action<PoiScanState, PoiScanState> OnScanStateChanged;

        // ── Eventi statici (lifecycle globale) ───────────────────────────────
        public static event Action<PoiInstance> OnAnyPoiSpawned;
        public static event Action<PoiInstance> OnAnyPoiDespawned;

        // ── Lifecycle NGO ────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                PoiRegistry.Register(this);
            }

            _logicalPosition.OnValueChanged += HandleLogicalPositionChanged;
            _logicalRotation.OnValueChanged += HandleLogicalRotationChanged;
            _scanState.OnValueChanged += HandleScanStateChanged;

            // Cache DockingAnchor (Rev AB). GetComponentInChildren scandisce
            // tutto il sottoalbero del root — trova l'anchor sia se figlio
            // diretto sia se annidato (pattern raccomandato: figlio diretto
            // del root, vedi doc di classe).
            ResolveDockingAnchor();

            ApplyLogicalToVisual();

            // Notifica subscriber globali (client-side liste, ScannerUI, ecc.)
            OnAnyPoiSpawned?.Invoke(this);
        }

        public override void OnNetworkDespawn()
        {
            OnAnyPoiDespawned?.Invoke(this);

            _logicalPosition.OnValueChanged -= HandleLogicalPositionChanged;
            _logicalRotation.OnValueChanged -= HandleLogicalRotationChanged;
            _scanState.OnValueChanged -= HandleScanStateChanged;

            if (IsServer)
            {
                PoiRegistry.Unregister(this);
            }
        }

        /// <summary>
        /// Rev AB — Cache DockingAnchor + local pose. Chiamato in OnNetworkSpawn.
        /// Se non trovato, emette warning una volta (poi silenzioso) e le
        /// property di world position/forward useranno il fallback pre-Rev AB.
        /// </summary>
        private void ResolveDockingAnchor()
        {
            var anchor = GetComponentInChildren<DockingAnchor>(includeInactive: false);
            if (anchor == null)
            {
                _dockingAnchorTransform = null;
                if (!_hasWarnedMissingAnchor)
                {
                    _hasWarnedMissingAnchor = true;
                    Debug.LogWarning($"[PoiInstance] {name}: nessun DockingAnchor " +
                                     "trovato nel sottoalbero. Uso fallback pre-Rev AB " +
                                     "(anchor = LogicalPosition, forward = LogicalRotation × " +
                                     "Vector3.up). Aggiungere un GameObject figlio del " +
                                     "root con componente DockingAnchor per definire il " +
                                     "punto di attracco.");
                }
                return;
            }

            _dockingAnchorTransform = anchor.transform;

            // Local pose rispetto al root (this.transform). Cache costante:
            // l'anchor è statico rispetto al root, non verrà mai mosso a runtime.
            //
            // Uso InverseTransformPoint / InverseTransformDirection per essere
            // robusti anche se l'anchor è annidato più profondamente (non solo
            // figlio diretto). Per figlio diretto = anchor.localPosition/localRotation.
            _dockingAnchorLocalPos = transform.InverseTransformPoint(anchor.transform.position);
            _dockingAnchorLocalRot = Quaternion.Inverse(transform.rotation) * anchor.transform.rotation;
        }

        // ── Integrazione moto proprio (Blocco 3.2.b, server-only) ────────────

        private void FixedUpdate()
        {
            if (!IsSpawned) return;
            if (!IsServer) return;

            Vector3 v = _logicalVelocity.Value;

            // Fast path: POI fermo → nessuna scrittura, nessun evento.
            if (v.sqrMagnitude < sleepSpeedThreshold * sleepSpeedThreshold)
            {
                if (v != Vector3.zero)
                {
                    _logicalVelocity.Value = Vector3.zero;
                }
                return;
            }

            float dt = Time.fixedDeltaTime;

            // Integrazione posizione (Euler esplicito).
            _logicalPosition.Value += v * dt;

            // Damping esponenziale: v(t) = v(0) * exp(-t / tau).
            if (dampingTau > 0f)
            {
                float decay = Mathf.Exp(-dt / dampingTau);
                _logicalVelocity.Value = v * decay;
            }
        }

        // ── API server-only ──────────────────────────────────────────────────

        public void InitializeLogicalPose(Vector3 logicalPosition, Quaternion logicalRotation)
        {
            if (!IsServer)
            {
                Debug.LogError("[PoiInstance] InitializeLogicalPose called on client — ignored.");
                return;
            }

            _logicalPosition.Value = logicalPosition;
            _logicalRotation.Value = logicalRotation;
            _logicalVelocity.Value = Vector3.zero;

            ApplyLogicalToVisual();
        }

        public void SetScanState(PoiScanState newState)
        {
            if (!IsServer)
            {
                Debug.LogError("[PoiInstance] SetScanState called on client — ignored.");
                return;
            }

            _scanState.Value = newState;
        }

        /// <summary>
        /// Aggiunge un impulso alla velocità logica del POI. Server-only.
        /// Consumer: ShipImpactHandler (Blocco 3.2.b.2).
        /// </summary>
        public void AddImpulse(Vector3 deltaV)
        {
            if (!IsServer)
            {
                Debug.LogError("[PoiInstance] AddImpulse called on client — ignored.");
                return;
            }

            _logicalVelocity.Value += deltaV;
        }

        /// <summary>
        /// Azzera istantaneamente la velocità logica del POI. Server-only.
        /// Rev AF (Blocco 3.2.d.e — chiusura Milestone 3): consumato da
        /// DockingController.RequestConfirmAnchorInternal per fermare il
        /// relitto al momento della conferma dell'ancoraggio, evitando che
        /// un POI precedentemente colpito (con velocity residua da momentum
        /// transfer Rev Z) continui a driftare dopo l'attracco — situazione
        /// scorretta perché il POI è semanticamente vincolato alla nave.
        ///
        /// Setter esplicito e semanticamente distinto da AddImpulse(deltaV):
        /// evita il pattern hackish AddImpulse(-_logicalVelocity.Value) e
        /// rende leggibile l'intent del caller.
        ///
        /// Note che questo NON impedisce ulteriori AddImpulse dopo la
        /// chiamata: se un consumer post-Docked applicasse un nuovo impulso
        /// (scenario ipotetico M4 con nemici che colpiscono nave-ancorata),
        /// il POI si rimetterebbe in moto. Il caso non è coperto in Rev AF
        /// (Q3-A confermata): se emerge in playtest futuro, aprire debito
        /// per progettazione dedicata (POI ancorato = massa infinita?).
        /// </summary>
        public void ResetVelocity()
        {
            if (!IsServer)
            {
                Debug.LogError("[PoiInstance] ResetVelocity called on client — ignored.");
                return;
            }

            _logicalVelocity.Value = Vector3.zero;
        }

        // ── Callback NetVar ──────────────────────────────────────────────────

        private void HandleLogicalPositionChanged(Vector3 _, Vector3 __)
        {
            ApplyLogicalToVisual();
        }

        private void HandleLogicalRotationChanged(Quaternion _, Quaternion __)
        {
            ApplyLogicalToVisual();
        }

        private void HandleScanStateChanged(PoiScanState previous, PoiScanState next)
        {
            OnScanStateChanged?.Invoke(previous, next);
        }

        // ── Applicazione al visual ───────────────────────────────────────────

        private void ApplyLogicalToVisual()
        {
            if (visualFollower == null)
            {
                Debug.LogError($"[PoiInstance] {name}: visualFollower non assegnato nell'inspector del prefab.");
                return;
            }

            visualFollower.SetLogicalOverride(_logicalPosition.Value, _logicalRotation.Value);
        }

        // =========================================================================
        // GIZMOS RUNTIME (Rev AB — debug compound POI)
        // =========================================================================
        //
        // Il PoiInstance NON ha un CompoundColliderAuthoring: la lista di
        // volumi vive su PoiData (SO, per-categoria). Senza un gizmo dedicato
        // il POI sarebbe invisibile all'ispezione geometrica in scena — è
        // impossibile capire se il volume è configurato dove ci si aspetta.
        //
        // Questi gizmi disegnano i volumi in wireframe sulla POSIZIONE VISUALE
        // del POI (visualFollower.transform), che rappresenta LogicalPosition
        // ruotata dalla nave. La geometria mostrata è quella USATA per la
        // collisione, calcolata identicamente a CompoundColliderMath (trasfor-
        // mazione local→world usando LogicalRotation).
        //
        // Il calcolo di collisione avviene in space LOGICO — il gizmo mostra
        // la rappresentazione VISUALE di quello space, quindi coerente con
        // ciò che il pilota vede.
        //
        // Visibili sempre (non solo selezionato) per navigazione scena.
        // OBB azzurro, Sphere verde (coerente con CompoundColliderAuthoring).
#if UNITY_EDITOR
        [Header("Debug gizmos (Editor only)")]
        [Tooltip("Se true, disegna i volumi del compound POI in wireframe " +
                 "attorno al visual del POI. Cruciale in debug — senza gizmo " +
                 "non c'è modo di verificare visivamente la geometria di " +
                 "collisione. Default true.")]
        [SerializeField] private bool drawCompoundGizmos = true;

        private void OnDrawGizmos()
        {
            if (!drawCompoundGizmos) return;
            if (data == null || data.CollisionVolumes == null) return;
            if (data.CollisionVolumes.Count == 0) return;

            // Uso la posizione VISUALE del POI (visualFollower.transform), non
            // LogicalPosition: durante il play il visual rappresenta la posizione
            // logica ruotata dal Follower. Fuori dal play, visualFollower.transform
            // è la posizione statica del prefab in scena.
            Transform pivot = visualFollower != null
                ? visualFollower.transform
                : transform;

            Vector3 basePos = pivot.position;
            Quaternion baseRot = pivot.rotation;

            Color obbColor = new Color(0.4f, 0.7f, 1f, 1f);   // azzurro
            Color sphColor = new Color(0.4f, 1f, 0.5f, 1f);   // verde

            for (int i = 0; i < data.CollisionVolumes.Count; i++)
            {
                var v = data.CollisionVolumes[i];
                Vector3 worldCenter = basePos + baseRot * v.localPosition;
                Quaternion worldRot = baseRot * Quaternion.Euler(v.localEulerAngles);

                if (v.type == SpaceSurvivor.Collision.CompoundVolumeType.OBB)
                {
                    var prev = Gizmos.matrix;
                    Gizmos.matrix = Matrix4x4.TRS(worldCenter, worldRot, Vector3.one);
                    Gizmos.color = obbColor;
                    Gizmos.DrawWireCube(Vector3.zero, v.scale);
                    Gizmos.matrix = prev;
                }
                else // Sphere
                {
                    Gizmos.color = sphColor;
                    Gizmos.DrawWireSphere(worldCenter, v.Radius);
                }
            }
        }
#endif
    }
}