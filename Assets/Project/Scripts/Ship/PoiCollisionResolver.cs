using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Collision;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// PoiCollisionResolver — Milestone 3 Fase 3 Blocco 3.2.c (Rev AA) esteso
    /// Rev AB (Blocco 3.2.d D5 — Compound Collider).
    /// NetworkBehaviour singleton — GameObject dedicato figlio di Nave
    /// (fratello di DockingController, ShipMovement, PropulsionSystem,
    /// AnchorSystem, ShipImpactHandler).
    ///
    /// RESPONSABILITÀ:
    ///   Gestisce la collisione fisica hard tra nave e POI FUORI dal contesto
    ///   Docking, cioè quando NavigationState ∈ {Manual, Coasting, Autopilot}.
    ///
    ///   In Docking / Docked il resolver dorme (early-return): il
    ///   DockingController ha il proprio clamp+slide con semantica diversa.
    ///   Mutuamente esclusivo per evitare doppio clamp sulla stessa
    ///   LogicalPosition nello stesso tick.
    ///
    /// FLUSSO DI VITA:
    ///   Passivo: NON gira in FixedUpdate proprio. Espone ResolveCollision
    ///   che ShipMovement.UpdatePosition invoca subito prima di scrivere
    ///   _logicalPosition (hook Rev AA).
    ///
    /// ── MODIFICHE REV AB (Q4 = C — closest-point unificato) ─────────────────
    ///
    /// SELEZIONE DEL POI VINCITORE:
    ///   Rev AA: distanza centro-centro &lt; (poiR + shipR), sceglie il più
    ///   vicino.
    ///   Rev AB: per ogni POI in PoiRegistry.All calcolo
    ///   CompoundColliderMath.ComputeMaxPenetration(shipVolumes, poiVolumes).
    ///   Sceglie il POI con depth MASSIMA (coppia di volumi più compenetrata).
    ///   Se più POI compenetrano nel tick, il vincitore è quello con la
    ///   depth più alta — l'altro sarà gestito al prossimo tick (single-pass
    ///   sufficiente per lo scenario tipico, spacing PoiSpawner tende a
    ///   evitare overlap simultanei).
    ///
    /// CLAMP+SLIDE → FULL STOP (Rev AB stabilizzazione, post-playtest):
    ///   Rev AA usava clamp posizionale + slide tangenziale
    ///   (azzera radiale, preserva tangenziale). Pattern pensato per sfera-vs-
    ///   sfera dove la normale coincide con l'asse di ingresso. Con OBB×OBB
    ///   il min-axis di SAT NON è correlato alla direzione di moto della
    ///   nave: per ingressi obliqui il push-out è quasi perpendicolare a
    ///   forward, la componente radiale della velocity è piccola,
    ///   azzerarla non ferma la nave. Osservato in playtest: v_radial ≈ 8/100,
    ///   la nave attraversa il POI mentre oscilla lateralmente (tremito).
    ///
    ///   Rev AB usa full stop: rollback posizionale a currentPos + azzeramento
    ///   completo di CurrentSpeed. La nave si ferma al bordo del volume POI;
    ///   il pilota deve girare esplicitamente per uscire. Feel FPS "muro
    ///   invisibile". Il DockingController mantiene invece il clamp+slide
    ///   originale (in docking lo strafe RCS è lento e obliquo/tangenziale
    ///   è il caso normale — il feel di scivolamento è desiderabile).
    ///
    /// LATCH ANTI-SPAM PER-POI:
    ///   Invariato per il set di ID (HashSet&lt;ulong&gt; keyed su
    ///   NetworkObjectId).
    ///   Isteresi di rilascio: Rev AA usava effectiveR = poiR + shipR come
    ///   soglia. Rev AB non ha più un raggio unico per POI (il compound è
    ///   multi-volume); come surrogato uso poi.Data.ApproximateRadius (Q5=B,
    ///   raggio approssimato per usi non-collisionali strict). Il release
    ///   threshold è ApproximateRadius × collisionReleaseHysteresis: quando
    ///   la distanza dal CENTRO logico del POI supera questa soglia, il
    ///   latch è rilasciato. Non è geometricamente esatto (il compound può
    ///   avere volumi che sporgono oltre ApproximateRadius), ma per anti-spam
    ///   è sufficiente e stabile.
    ///
    /// EMISSIONE OnHardCollision:
    ///   Signature identica a DockingController.OnHardCollision:
    ///     Action&lt;float, PoiInstance&gt; — (radialImpactVelocity, poiColpito).
    ///   Consumer: ShipImpactHandler. La semantica di radialImpactVelocity
    ///   è la componente della velocità nave lungo l'asse di push-out
    ///   della coppia vincitore (CompoundColliderMath.PairContact.NormalOutwardFromA
    ///   invertito): coerente col Rev AA (sempre &gt;= 0).
    ///
    /// INTERAZIONE CON PROPULSIONSYSTEM (invariata):
    ///   La velocità post-clamp proiettata su LogicalForward → nuova
    ///   CurrentSpeed scalare. Segno preservato. TargetSpeed NON toccato
    ///   (martellamento contro POI come feature intenzionale).
    ///
    /// AUTOPILOT (invariato):
    ///   Il resolver si applica identicamente in Autopilot: se la rotta
    ///   automatica porta la nave contro un POI, la nave si ferma alla
    ///   superficie del volume più esterno. Il pilota deve riprendere Manual.
    ///
    /// DIPENDE DA:
    ///   - CompoundColliderMath (helper statico, Rev AB)
    ///   - PoiRegistry (server-only iteratore POI attivi)
    ///   - PoiInstance (LogicalPosition/Rotation, CollisionVolumes, Data.ApproximateRadius,
    ///     OnAnyPoiDespawned)
    ///   - ShipMovement (Instance, LogicalRotation, Compound)
    ///   - PropulsionSystem (Instance, CurrentNavState)
    /// </summary>
    public class PoiCollisionResolver : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static PoiCollisionResolver Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── Tuning (SerializeField) ──────────────────────────────────────────
        [Header("Collisione (Blocco 3.2.c + Rev AB compound)")]
        [Tooltip("Se true, applica clamp posizionale hard: la nave NON può " +
                 "attraversare la mesh di alcun POI durante Manual/Coasting/" +
                 "Autopilot. Se false, la collisione emette solo OnHardCollision " +
                 "senza vincolare posizione — utile per debug/test edge. In " +
                 "gameplay normale DEVE restare true.")]
        [SerializeField] private bool useHardPositionClamp = true;

        [Tooltip("Fattore di isteresi sul rilascio del latch di collisione per-POI. " +
                 "Un POI colpito viene rimosso dal set di latch quando la distanza " +
                 "dal centro logico del POI supera Data.ApproximateRadius × questo " +
                 "fattore. Default 1.2 (20% oltre il raggio approssimato). Con " +
                 "compound multi-volume la soglia non è geometricamente esatta, " +
                 "ma per anti-spam è sufficiente.")]
        [Min(1.01f)]
        [SerializeField] private float collisionReleaseHysteresis = 1.2f;

        [Header("Debug")]
        [Tooltip("Log dettagliato di ogni collisione risolta. Utile in playtest; " +
                 "disattivare in build finale.")]
        [SerializeField] private bool logCollisions = true;

        [Tooltip("Log diagnostico VERBOSO — heartbeat throttled (1/sec) dello " +
                 "stato del resolver + posizioni logiche/visuali nel log HARD " +
                 "COLLISION. Attivare solo quando si sta indagando su " +
                 "divergenze visual↔logica o mancata rilevazione. Off in " +
                 "gameplay normale — introduce rumore in console.")]
        [SerializeField] private bool debugVerbose = false;

        // ── Stato server-only ─────────────────────────────────────────────────

        /// <summary>
        /// Insieme di POI attualmente in stato di latch. Chiave:
        /// PoiInstance.NetworkObjectId. Popolato al fire di OnHardCollision,
        /// svuotato per POI quando la nave si allontana oltre soglia isteresi
        /// o quando il POI despawna.
        /// </summary>
        private readonly HashSet<ulong> _latchedPoiIds = new HashSet<ulong>();

        /// <summary>
        /// Rev AB — frame counter per throttle del log diagnostico "Heartbeat"
        /// ogni ~1 sec (50 fixed frames). Emesso solo se debugVerbose == true.
        /// </summary>
        private int _debugFrameCounter;

        // ── Evento pubblico ───────────────────────────────────────────────────

        /// <summary>
        /// Fire server-side quando avviene un HardCollision fuori dal Docking.
        /// Signature identica a DockingController.OnHardCollision:
        /// (radialImpactVelocity in u/s, PoiInstance colpito).
        /// </summary>
        public event Action<float, PoiInstance> OnHardCollision;

        // ── Lifecycle NGO ─────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[PoiCollisionResolver] Istanza duplicata rilevata — distruggo.");
                Destroy(gameObject);
                return;
            }
            Instance = this;

            if (IsServer)
            {
                PoiInstance.OnAnyPoiDespawned += HandlePoiDespawned;
            }

            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                PoiInstance.OnAnyPoiDespawned -= HandlePoiDespawned;
                _latchedPoiIds.Clear();
            }

            if (Instance == this) Instance = null;
        }

        private void HandlePoiDespawned(PoiInstance poi)
        {
            if (poi == null || poi.NetworkObject == null) return;
            _latchedPoiIds.Remove(poi.NetworkObject.NetworkObjectId);
        }

        // =========================================================================
        // API PUBBLICA — chiamata da ShipMovement.UpdatePosition
        // =========================================================================

        public struct ResolveResult
        {
            /// <summary>Posizione applicata alla nave (clampata al bordo del compound POI se collisione, altrimenti = candidatePos).</summary>
            public Vector3 ClampedPosition;

            /// <summary>Nuova velocità scalare da assegnare a PropulsionSystem.CurrentSpeed.</summary>
            public float ClampedSpeedScalar;

            /// <summary>true se il clamp ha ridotto la velocità (radial inward > 0). Il chiamante deve chiamare SetCurrentSpeedFromCollision solo se true.</summary>
            public bool VelocityWasClamped;
        }

        /// <summary>
        /// Server-only. Chiamato da ShipMovement.UpdatePosition prima di
        /// scrivere _logicalPosition. Calcola se qualche coppia (volumeNave,
        /// volumePOI) compenetra; in tal caso clampa+slida e emette
        /// OnHardCollision.
        ///
        /// PARAMETRI:
        ///   currentPos       — ship.LogicalPosition prima dell'integrazione.
        ///   candidatePos     — currentPos + logicalForward * currentSpeed * dt.
        ///   logicalForward   — direzione di avanzamento della nave.
        ///   currentSpeed     — velocità scalare della nave (u/s).
        /// </summary>
        public ResolveResult ResolveCollision(
            Vector3 currentPos,
            Vector3 candidatePos,
            Vector3 logicalForward,
            float currentSpeed)
        {
            ResolveResult result = new ResolveResult
            {
                ClampedPosition = candidatePos,
                ClampedSpeedScalar = currentSpeed,
                VelocityWasClamped = false,
            };

            if (!IsServer) return result;

            // ── DEBUG HEARTBEAT (guardato da debugVerbose) ───────────────────
            //    Log ogni ~1 sec dello stato che il resolver sta vedendo.
            //    Utile per diagnosticare divergenze visual↔logica o mancata
            //    rilevazione. Off in gameplay normale.
            _debugFrameCounter++;
            if (debugVerbose && (_debugFrameCounter % 50 == 0))
            {
                int poiTotal = 0;
                int poiWithVolumes = 0;
                float nearestDist = float.PositiveInfinity;
                string nearestName = "(none)";
                foreach (var p in PoiRegistry.All)
                {
                    if (p == null) continue;
                    poiTotal++;
                    if (p.Data != null && p.CollisionVolumes != null && p.CollisionVolumes.Count > 0)
                        poiWithVolumes++;
                    float d = Vector3.Distance(candidatePos, p.LogicalPosition);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearestName = p.Data != null ? p.Data.DisplayName : p.name;
                    }
                }
                var shipMovDbg = ShipMovement.Instance;
                bool hasShipCompound = shipMovDbg != null && shipMovDbg.Compound != null;
                int shipVolCount = hasShipCompound ? shipMovDbg.Compound.Count : 0;
                Debug.Log($"[Resolver HEARTBEAT] speed={currentSpeed:F1}u/s  " +
                          $"shipPos=({currentPos.x:F0},{currentPos.y:F0},{currentPos.z:F0})  " +
                          $"POI totali={poiTotal} (conVolumi={poiWithVolumes})  " +
                          $"più vicino='{nearestName}' dist={nearestDist:F1}u  " +
                          $"shipCompound={hasShipCompound} (vol={shipVolCount})");
            }

            var propulsion = PropulsionSystem.Instance;
            if (propulsion == null) return result;

            NavigationState state = propulsion.CurrentNavState;
            if (state == NavigationState.Docking
                || state == NavigationState.Docked
                || state == NavigationState.Anchored)
            {
                return result;
            }

            // Vettore velocità 3D (Rev T: velocity = logicalForward * currentSpeed).
            Vector3 velocity = logicalForward * currentSpeed;

            // Rev AB — compound della nave (può essere null → nave = punto).
            var shipMovement = ShipMovement.Instance;
            IReadOnlyList<CompoundVolume> shipVolumes =
                (shipMovement != null && shipMovement.Compound != null)
                    ? shipMovement.Compound.Volumes
                    : null;
            Quaternion shipRotation = shipMovement != null
                ? shipMovement.LogicalRotation
                : Quaternion.identity;

            // ── Selezione POI vincitore: quello con depth di compenetrazione
            //    massima quando testato con ComputeMaxPenetration (Rev AB, Q4=C).
            PoiInstance winner = null;
            float winnerDepth = 0f;
            CompoundColliderMath.PairContact winnerPair = default;

            foreach (var poi in PoiRegistry.All)
            {
                if (poi == null || poi.Data == null) continue;

                var poiVolumes = poi.CollisionVolumes;
                if (poiVolumes == null || poiVolumes.Count == 0) continue;

                CompoundColliderMath.PairContact pair =
                    CompoundColliderMath.ComputeMaxPenetration(
                        candidatePos, shipRotation, shipVolumes,
                        poi.LogicalPosition, poi.LogicalRotation, poiVolumes,
                        fallbackNormal: Vector3.up);

                if (pair.Depth > winnerDepth)
                {
                    winnerDepth = pair.Depth;
                    winner = poi;
                    winnerPair = pair;
                }
            }

            // Rilascio latch con isteresi (Rev AB: soglia = ApproximateRadius × factor).
            if (_latchedPoiIds.Count > 0)
            {
                UpdateLatchHysteresis(candidatePos);
            }

            if (winner == null)
            {
                // Nessuna compenetrazione: passa attraverso.
                return result;
            }

            // ── Clamp+slide contro il POI vincitore ──────────────────────────
            //    Riuso il winnerPair (già calcolato sopra) per evitare
            //    doppio lavoro: ClampAgainstCompound rifarebbe internamente
            //    ComputeMaxPenetration, ma applichiamo il risultato manualmente.
            //
            //    ─── REV AB — STABILIZZAZIONE (post-playtest) ────────────────
            //    Cambio semantico rispetto Rev AA "clamp+slide":
            //
            //    Il pattern originale Rev AA (azzera radiale, preserva
            //    tangenziale) era pensato per sfera-vs-sfera, dove la normale
            //    di collisione punta sempre RADIALMENTE dal centro POI e
            //    coincide con la direzione di ingresso della nave. In quel
            //    caso "azzera radiale" = "ferma il movimento verso il POI".
            //
            //    Con OBB×OBB (Rev AB compound) la normale del min-axis di SAT
            //    NON è correlata alla direzione di moto della nave: per
            //    ingressi obliqui SAT restituisce tipicamente il min-axis
            //    LATERALE (o un cross-axis diagonale), non l'asse frontale
            //    di ingresso. La componente radiale della velocity su quel
            //    min-axis è piccola (es. v_radial=8/100 osservato in playtest),
            //    azzerarla non ferma la nave: forward resta ~99, la nave
            //    continua ad avanzare e ATTRAVERSA il POI oscillando
            //    lateralmente (tremito visibile).
            //
            //    SOLUZIONE — full stop:
            //      1. ROLLBACK POSITION: torno a currentPos (posizione del
            //         frame precedente, pre-integrazione). Nessuna
            //         compenetrazione = nessun push-out visibile.
            //      2. ZERO SPEED: azzero completamente CurrentSpeed. La nave
            //         si ferma al bordo del volume POI. Il pilota deve
            //         girare esplicitamente per uscire.
            //
            //    Feel di gioco: "muro invisibile" classico FPS. Il pilota
            //    capisce immediatamente che c'è un ostacolo. Zero attraver-
            //    samento, zero tremito.
            //
            //    Il DockingController usa ancora clamp+slide (comportamento
            //    Rev AA invariato) perché in docking lo strafe RCS è lento
            //    e obliquo/tangenziale è il caso normale — il feel di
            //    scivolamento è desiderabile lì.
            Vector3 normalOut = winnerPair.NormalOutwardFromA;
            if (normalOut.sqrMagnitude < CompoundColliderMath.DegenerateEpsilon
                                          * CompoundColliderMath.DegenerateEpsilon)
            {
                normalOut = Vector3.up;
            }

            // Calcolo v_radial solo per il log e per l'evento OnHardCollision
            // (semantica invariata: velocità della nave lungo l'asse di
            // push-out al momento del contatto — utile per Blocco 3.2
            // danno hull futuro).
            float radialInward = -Vector3.Dot(velocity, normalOut);

            if (useHardPositionClamp)
            {
                // ROLLBACK: posizione = currentPos (fuori dal volume POI,
                // per costruzione — al frame precedente non c'era collisione).
                result.ClampedPosition = currentPos;
                // FULL STOP: azzera la velocità completamente.
                result.ClampedSpeedScalar = 0f;
                result.VelocityWasClamped = true;
            }
            else
            {
                // Modalità debug (useHardPositionClamp=false): posizione
                // libera, nessun clamp. Utile per test edge.
                result.ClampedPosition = candidatePos;
            }

            // Emissione evento OnHardCollision (con latch anti-spam per-POI).
            // Semantica invariata: fire una sola volta per sessione di
            // contatto, condizione = velocity aveva componente inward.
            if (radialInward > 0f)
            {
                ulong poiId = winner.NetworkObject != null
                    ? winner.NetworkObject.NetworkObjectId
                    : 0ul;

                if (poiId != 0ul && !_latchedPoiIds.Contains(poiId))
                {
                    _latchedPoiIds.Add(poiId);
                    OnHardCollision?.Invoke(radialInward, winner);

                    if (logCollisions)
                    {
                        if (debugVerbose)
                        {
                            // Forma estesa: posizioni logiche + visuali per
                            // diagnosticare divergenze gizmo↔calcolo.
                            var shipMovDbg = ShipMovement.Instance;
                            Vector3 shipLogPos = shipMovDbg != null ? shipMovDbg.LogicalPosition : Vector3.zero;
                            Quaternion shipLogRot = shipMovDbg != null ? shipMovDbg.LogicalRotation : Quaternion.identity;
                            Vector3 shipTransformPos = shipMovDbg != null ? shipMovDbg.transform.position : Vector3.zero;
                            Quaternion shipTransformRot = shipMovDbg != null ? shipMovDbg.transform.rotation : Quaternion.identity;
                            Vector3 poiLogPos = winner.LogicalPosition;
                            Vector3 poiVisualPos = winner.transform.position;
                            Vector3 deltaLogical = poiLogPos - shipLogPos;

                            Debug.LogWarning(
                                $"[PoiCollisionResolver] HARD COLLISION (full stop) → " +
                                $"POI={winner.Data.DisplayName}, " +
                                $"depth={winnerPair.Depth:F2}u, " +
                                $"v_radial={radialInward:F2} u/s, " +
                                $"speed azzerata (era {currentSpeed:F2}), " +
                                $"navState={state}\n" +
                                $"    shipLogical pos={shipLogPos:F2} rot={shipLogRot.eulerAngles:F1}\n" +
                                $"    shipTransform pos={shipTransformPos:F2} rot={shipTransformRot.eulerAngles:F1}\n" +
                                $"    poiLogical pos={poiLogPos:F2}   poiTransform pos={poiVisualPos:F2}\n" +
                                $"    delta LOGICO poi−ship = {deltaLogical:F2}  |Δ|={deltaLogical.magnitude:F2}u");
                        }
                        else
                        {
                            // Forma sintetica (default in gameplay).
                            Debug.LogWarning($"[PoiCollisionResolver] HARD COLLISION (full stop) → " +
                                             $"POI={winner.Data.DisplayName}, " +
                                             $"depth={winnerPair.Depth:F2}u, " +
                                             $"v_radial={radialInward:F2} u/s, " +
                                             $"speed azzerata (era {currentSpeed:F2}), " +
                                             $"navState={state}");
                        }
                    }
                }
                else if (logCollisions && poiId == 0ul)
                {
                    Debug.LogWarning("[PoiCollisionResolver] POI senza NetworkObject valido — " +
                                     "OnHardCollision skippato (latch impossibile).");
                }
            }

            return result;
        }

        /// <summary>
        /// Rilascia dal set di latch i POI da cui la nave si è allontanata
        /// oltre Data.ApproximateRadius × collisionReleaseHysteresis. Iteriamo
        /// tramite copia temporanea per poter rimuovere durante l'iterazione.
        ///
        /// Rev AB: soglia basata su ApproximateRadius (sfera approssimata,
        /// Q5=B) invece che sul raggio effettivo somma. Non è geometricamente
        /// esatto ma sufficiente per anti-spam.
        /// </summary>
        private void UpdateLatchHysteresis(Vector3 shipPos)
        {
            var idsSnapshot = new List<ulong>(_latchedPoiIds);

            foreach (ulong id in idsSnapshot)
            {
                PoiInstance poi = ResolvePoi(id);
                if (poi == null || poi.Data == null)
                {
                    _latchedPoiIds.Remove(id);
                    continue;
                }

                float releaseThreshold = poi.Data.ApproximateRadius * collisionReleaseHysteresis;
                Vector3 delta = shipPos - poi.LogicalPosition;
                if (delta.sqrMagnitude > releaseThreshold * releaseThreshold)
                {
                    _latchedPoiIds.Remove(id);
                    if (logCollisions)
                    {
                        Debug.Log($"[PoiCollisionResolver] Latch rilasciato per POI={poi.Data.DisplayName} " +
                                  $"(distance {delta.magnitude:F1}u > release {releaseThreshold:F1}u).");
                    }
                }
            }
        }

        /// <summary>
        /// Risolve un PoiInstance dal NetworkObjectId via SpawnManager.
        /// </summary>
        private static PoiInstance ResolvePoi(ulong networkObjectId)
        {
            if (networkObjectId == 0ul) return null;
            if (NetworkManager.Singleton == null) return null;
            if (NetworkManager.Singleton.SpawnManager == null) return null;

            if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects
                    .TryGetValue(networkObjectId, out var no))
                return null;
            if (no == null) return null;
            return no.GetComponent<PoiInstance>();
        }

        // =========================================================================
        // DEBUG GUI (solo lettura — cursore-safe)
        // =========================================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!IsServer) return;

            var propulsion = PropulsionSystem.Instance;
            NavigationState state = propulsion != null
                ? propulsion.CurrentNavState
                : NavigationState.Anchored;

            GUILayout.BeginArea(new Rect(Screen.width - 260, Screen.height - 260, 250, 55));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[PoiCollResolver] SRV — state {state}");
            GUILayout.Label($"Latch POI count: {_latchedPoiIds.Count}");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}