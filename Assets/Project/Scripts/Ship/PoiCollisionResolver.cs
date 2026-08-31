using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// PoiCollisionResolver — Milestone 3 Fase 3 Blocco 3.2.c.
    /// NetworkBehaviour singleton — GameObject dedicato figlio di Nave
    /// (fratello di DockingController, ShipMovement, PropulsionSystem,
    /// AnchorSystem, ShipImpactHandler).
    ///
    /// RESPONSABILITÀ:
    ///   Gestisce la collisione fisica hard tra nave e POI FUORI dal contesto
    ///   Docking, cioè quando NavigationState ∈ {Manual, Coasting, Autopilot}.
    ///
    ///   In Docking / Docked il resolver dorme (early-return): il
    ///   DockingController ha il proprio clamp+slide con semantica diversa
    ///   (frame POI, base perpendicolare, minigame). Mutuamente esclusivo per
    ///   evitare doppio clamp sulla stessa LogicalPosition nello stesso tick.
    ///
    /// FLUSSO DI VITA:
    ///   Il resolver è passivo: NON gira in FixedUpdate proprio. Espone il
    ///   metodo pubblico ResolveCollision(currentPos, candidatePos, velocity)
    ///   che ShipMovement.UpdatePosition invoca subito prima di scrivere
    ///   _logicalPosition (hook Rev AA). Vantaggio: zero rischio di ordine
    ///   di esecuzione tra ShipMovement.FixedUpdate e un ipotetico
    ///   resolver.FixedUpdate — il resolver lavora dentro lo stesso tick di
    ///   integrazione, sulla stessa candidatePos appena calcolata.
    ///
    /// SELEZIONE DEL POI (PA2 confermato Rev AA — single-pass sul più vicino):
    ///   Itera PoiRegistry.All server-side, calcola per ognuno
    ///   dist_to_poi = |candidatePos - poi.LogicalPosition|. Se dist_to_poi
    ///   &lt; hardRadius del POI, il POI è candidato di collisione. Sceglie
    ///   quello con dist_to_poi minore. Se più POI sono sotto soglia
    ///   simultaneamente (raro con spacing PoiSpawner corrente), il più
    ///   vicino vince — l'altro sarà gestito al prossimo tick (single-pass
    ///   è sufficiente per lo scenario tipico; multi-pass promuovibile in
    ///   Rev AB se emergono bug di overlap).
    ///
    /// EMISSIONE OnHardCollision:
    ///   Signature identica a DockingController.OnHardCollision:
    ///     Action&lt;float, PoiInstance&gt; — (radialImpactVelocity, poiColpito).
    ///   Consumer: ShipImpactHandler (che si sottoscrive con lo stesso
    ///   pattern del DockingController). L'handler è agnostico rispetto alla
    ///   sorgente, quindi zero modifiche al body di HandleHardCollision.
    ///
    /// LATCH PER-POI (isteresi anti-spam):
    ///   Analogo a DockingController._hasFiredCollisionThisSession ma
    ///   generalizzato a HashSet&lt;ulong&gt; (chiave = poi.NetworkObjectId)
    ///   perché possiamo interagire con più POI nel tempo. Rilascio:
    ///   dopo che il POI è emesso, viene aggiunto all'HashSet; ogni tick,
    ///   se distance(ship, poi) &gt; hardR × collisionReleaseHysteresis,
    ///   viene rimosso. Rimozione automatica anche quando il POI despawna
    ///   (via PoiInstance.OnAnyPoiDespawned).
    ///
    /// INTERAZIONE CON PROPULSIONSYSTEM (PA1 confermato Rev AA — accetto martellamento):
    ///   Il modello di volo Rev T lavora con CurrentSpeed scalare e
    ///   LogicalForward. La velocità istantanea è velocity = LogicalForward
    ///   × CurrentSpeed (vettore 3D). Post-clamp la velocità può avere
    ///   tangenziale ≠ 0 su assi diversi da LogicalForward: la nuova
    ///   CurrentSpeed scalare è la proiezione della velocità tangenziale
    ///   su LogicalForward — dot(tangentialVelocity, LogicalForward). Se
    ///   negativa (il pilota sta scivolando all'indietro), il segno è
    ///   preservato.
    ///
    ///   PropulsionSystem.SetCurrentSpeedFromCollision(newScalar) è
    ///   invocato dal resolver via ShipMovement quando il clamp ha ridotto
    ///   la velocità. TargetSpeed NON viene toccato: se il pilota tiene W
    ///   premuto contro un POI, il tick successivo riaccelera CurrentSpeed
    ///   → il clamp reintarba → "martellamento" contro la mesh (feel
    ///   fisicamente coerente + consumo fuel visibile come feedback).
    ///
    /// AUTOPILOT (PA4 confermato Rev AA — collide e ferma):
    ///   Il resolver si applica identicamente in Autopilot: se la rotta
    ///   automatica porta la nave contro un POI, la nave si ferma sulla
    ///   superficie del raggio hard. Il pilota deve riprendere Manual per
    ///   aggirare. Pathfinding avoidance non è implementato (fuori scope,
    ///   dipende da: sistema di navigazione avanzato). Un allarme di
    ///   collisione imminente basato su WarningRadius è registrato come
    ///   idea futura per Blocco 3.2.d (feedback teatrale + audio + UI).
    ///
    /// DIPENDE DA:
    ///   - PoiCollisionMath (helper statico, Rev AA)
    ///   - PoiRegistry (server-only iteratore POI attivi)
    ///   - PoiInstance (LogicalPosition, Data.HardCollisionRadius,
    ///     OnAnyPoiDespawned)
    ///   - ShipMovement (Instance — non chiama direttamente, ma verifica
    ///     stato)
    ///   - PropulsionSystem (Instance, CurrentNavState)
    ///
    /// EDITOR SETUP:
    ///   Componente NetworkBehaviour su un GameObject figlio di Nave, con
    ///   NetworkObject. Fratello di ShipMovement / PropulsionSystem /
    ///   DockingController / ShipImpactHandler. Nessuna serialized reference
    ///   richiesta — tutto risolto via singleton pattern.
    ///
    /// dipende da setup Editor: aggiungere il componente al prefab/scena
    ///   di Nave e verificare che il NetworkObject sia registrato nella
    ///   NetworkManager.
    /// </summary>
    public class PoiCollisionResolver : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static PoiCollisionResolver Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── Tuning (SerializeField) ──────────────────────────────────────────
        [Header("Collisione (Blocco 3.2.c)")]
        [Tooltip("Se true, applica clamp posizionale hard: la nave NON può " +
                 "attraversare la mesh di alcun POI durante Manual/Coasting/" +
                 "Autopilot. Se false, la collisione emette solo OnHardCollision " +
                 "senza vincolare posizione — utile per debug/test edge. In " +
                 "gameplay normale DEVE restare true.")]
        [SerializeField] private bool useHardPositionClamp = true;

        [Tooltip("Fattore di isteresi sul rilascio del latch di collisione per-POI. " +
                 "Un POI colpito viene rimosso dal set di latch quando la distanza " +
                 "supera HardCollisionRadius × questo fattore. Default 1.2 " +
                 "(20% oltre il raggio hard). Coerente con il valore usato dal " +
                 "DockingController (Rev W) — previene spam di eventi se la nave " +
                 "resta al bordo.")]
        [Min(1.01f)]
        [SerializeField] private float collisionReleaseHysteresis = 1.2f;

        [Header("Debug")]
        [Tooltip("Log dettagliato di ogni collisione risolta (POI colpito, " +
                 "velocità di impatto, direzione). Utile in playtest 3.2.c per " +
                 "tuning; disattivare in build finale.")]
        [SerializeField] private bool logCollisions = true;

        // ── Stato server-only ─────────────────────────────────────────────────

        /// <summary>
        /// Insieme di POI attualmente in stato di latch (già fired
        /// OnHardCollision, in attesa di uscire dal raggio × hysteresis).
        /// Chiave: PoiInstance.NetworkObjectId. Popolato al fire, svuotato
        /// per POI quando la nave si allontana o quando il POI despawna.
        /// </summary>
        private readonly HashSet<ulong> _latchedPoiIds = new HashSet<ulong>();

        // ── Evento pubblico ───────────────────────────────────────────────────

        /// <summary>
        /// Fire server-side quando avviene un HardCollision fuori dal Docking.
        /// Signature identica a DockingController.OnHardCollision (invariante
        /// Rev Y): (radialImpactVelocity in u/s, PoiInstance colpito).
        ///
        /// impactVelocity è la componente RADIALE della velocità server-side
        /// al contatto (magnitudine, sempre &gt;= 0), calcolata da
        /// PoiCollisionMath — semantica fisica: la parte di velocità che
        /// stava puntando dentro la mesh.
        ///
        /// Consumer: ShipImpactHandler.HandleHardCollision (agnostico rispetto
        /// alla sorgente, si sottoscrive a entrambi i publisher —
        /// DockingController e questo resolver).
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
                // Cleanup del latch su despawn di POI (edge case: colpisco un
                // POI, viene despawnato dal server prima che la nave si sia
                // allontanata → rimango con id fantasma nel set).
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

        /// <summary>
        /// Risultato di ResolveCollision. Struct value-type (nessuna
        /// allocazione heap per chiamate ad alta frequenza — FixedUpdate).
        /// </summary>
        public struct ResolveResult
        {
            /// <summary>Posizione applicata alla nave (clampata al bordo del POI se collisione, altrimenti = candidatePos).</summary>
            public Vector3 ClampedPosition;

            /// <summary>Nuova velocità scalare da assegnare a PropulsionSystem.CurrentSpeed (proiezione della velocità post-clamp su LogicalForward).</summary>
            public float ClampedSpeedScalar;

            /// <summary>true se il clamp ha ridotto la velocità (radial &lt; 0). Il chiamante deve chiamare SetCurrentSpeedFromCollision solo se true.</summary>
            public bool VelocityWasClamped;
        }

        /// <summary>
        /// Server-only. Chiamato da ShipMovement.UpdatePosition prima di
        /// scrivere _logicalPosition. Calcola se candidatePos sfora il raggio
        /// hard di qualche POI, in tal caso clampa e emette OnHardCollision.
        ///
        /// PARAMETRI:
        ///   currentPos       — ship.LogicalPosition prima dell'integrazione.
        ///   candidatePos     — currentPos + logicalForward * currentSpeed * dt.
        ///   logicalForward   — direzione di avanzamento della nave in world
        ///                      (usata per proiettare la velocità post-clamp
        ///                      in scalare CurrentSpeed).
        ///   currentSpeed     — velocità scalare della nave (u/s).
        ///
        /// RITORNO:
        ///   ResolveResult con posizione da applicare + scalare velocità da
        ///   assegnare a PropulsionSystem (solo se VelocityWasClamped == true).
        ///
        /// PRECONDIZIONI:
        ///   Il chiamante deve verificare che siamo in stato Manual/Coasting/
        ///   Autopilot PRIMA di invocare. Il resolver comunque protegge con
        ///   early return se lo stato è Docking/Docked/Anchored (mutex con
        ///   DockingController). Anchored ha currentSpeed = 0 ma il resolver
        ///   non ha nessun lavoro utile da fare in quel caso.
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

            // Guardia di stato: in Docking/Docked il DockingController ha il
            // proprio clamp. Anchored ha velocità zero, niente da fare.
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

            // Rev AA hotfix: raggio effettivo di collisione = poiR + shipR
            // (formula fisica somma dei raggi). Cache locale una volta per tick.
            // Fallback 0 se ShipMovement non ancora pronto (edge case boot):
            // comportamento pre-hotfix (probabilmente compenetrazione visibile,
            // ma nessun crash).
            var shipMovement = ShipMovement.Instance;
            float shipR = shipMovement != null ? shipMovement.ShipCollisionRadius : 0f;

            // ── Selezione POI più vicino tra quelli che sforano (PA2.a) ──
            //    "Sforano" ora significa: distanza < (poiR + shipR).
            PoiInstance closestBreacher = null;
            float closestDistSqr = float.MaxValue;
            foreach (var poi in PoiRegistry.All)
            {
                if (poi == null || poi.Data == null) continue;

                float hardR = poi.Data.HardCollisionRadius;
                if (hardR <= 0f) continue;

                float effectiveR = hardR + shipR;
                Vector3 delta = candidatePos - poi.LogicalPosition;
                float distSqr = delta.sqrMagnitude;

                // Sforo del raggio effettivo?
                if (distSqr < effectiveR * effectiveR && distSqr < closestDistSqr)
                {
                    closestDistSqr = distSqr;
                    closestBreacher = poi;
                }
            }

            // Aggiornamento isteresi di rilascio del latch: per ogni POI in
            // latch, se la nave si è allontanata oltre (poiR+shipR) × hysteresis,
            // rimuoviamo. Iteriamo su una copia perché possiamo modificare il
            // set durante l'iterazione (rimozione).
            //
            // NB: iteriamo il registry per trovare i POI corrispondenti agli
            // ID nel latch. Se un id non è più nel registry (POI despawnato),
            // viene comunque rimosso dal callback OnAnyPoiDespawned — qui non
            // serve gestirlo esplicitamente.
            if (_latchedPoiIds.Count > 0)
            {
                UpdateLatchHysteresis(candidatePos, shipR);
            }

            if (closestBreacher == null)
            {
                // Nessuno sforo: passa attraverso.
                return result;
            }

            // ── Clamp+slide contro il POI selezionato (helper puro) ─────
            float hardRadius = closestBreacher.Data.HardCollisionRadius;
            Vector3 poiPos = closestBreacher.LogicalPosition;

            // Fallback radial: usa la direzione corrente ship→outward, o world-up
            // come ultimo ripiego (non dovrebbe mai servire con ClampAgainstPoi
            // che ha il proprio fallback interno).
            PoiCollisionMath.ClampResult clamp = PoiCollisionMath.ClampAgainstPoi(
                currentPos,
                candidatePos,
                velocity,
                poiPos,
                hardRadius,
                shipR,
                useHardPositionClamp,
                fallbackRadial: Vector3.up);

            result.ClampedPosition = clamp.ClampedPosition;

            if (clamp.HadCollision)
            {
                // Proietta la velocità post-clamp sull'asse LogicalForward per
                // ottenere il nuovo scalare CurrentSpeed. Segno preservato:
                // se il pilota sta scivolando all'indietro, resta negativo.
                //
                // NB: se il clamp ha azzerato tutta la componente lungo forward
                // (nave dritta contro POI), Dot(clampedVel, forward) ≈ 0 →
                // la nave si ferma. Se il pilota puntava di traverso, resta
                // la componente tangenziale proiettata su forward.
                float newScalar = Vector3.Dot(clamp.ClampedVelocity, logicalForward);
                result.ClampedSpeedScalar = newScalar;
                result.VelocityWasClamped = true;

                // Fire OnHardCollision (con latch anti-spam per-POI).
                ulong poiId = closestBreacher.NetworkObject != null
                    ? closestBreacher.NetworkObject.NetworkObjectId
                    : 0ul;

                if (poiId != 0ul && !_latchedPoiIds.Contains(poiId))
                {
                    _latchedPoiIds.Add(poiId);
                    OnHardCollision?.Invoke(clamp.RadialImpactSpeed, closestBreacher);

                    if (logCollisions)
                    {
                        Debug.LogWarning($"[PoiCollisionResolver] HARD COLLISION fuori Docking → " +
                                         $"POI={closestBreacher.Data.DisplayName}, " +
                                         $"v_radial={clamp.RadialImpactSpeed:F2} u/s, " +
                                         $"newSpeed={newScalar:F2} u/s (era {currentSpeed:F2}), " +
                                         $"navState={state}");
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
        /// oltre (poiR + shipR) × collisionReleaseHysteresis. Iteriamo tramite
        /// copia temporanea per poter rimuovere durante l'iterazione.
        /// </summary>
        private void UpdateLatchHysteresis(Vector3 shipPos, float shipR)
        {
            // Copia degli id correnti (allocation minima — HashSet.Count è
            // tipicamente 0-2 in gameplay normale, latch anti-spam).
            var idsSnapshot = new List<ulong>(_latchedPoiIds);

            foreach (ulong id in idsSnapshot)
            {
                // Risolve il POI dal registry / SpawnManager.
                PoiInstance poi = ResolvePoi(id);
                if (poi == null || poi.Data == null)
                {
                    // POI sparito senza passare per OnAnyPoiDespawned (edge case).
                    _latchedPoiIds.Remove(id);
                    continue;
                }

                float effectiveR = poi.Data.HardCollisionRadius + shipR;
                float releaseThreshold = effectiveR * collisionReleaseHysteresis;
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
        /// Risolve un PoiInstance dal NetworkObjectId via SpawnManager. Copia
        /// dell'utility privata di DockingController.ResolvePoi (Rev W): non
        /// vale la pena estrarre in un helper condiviso finché ci sono solo
        /// due chiamanti.
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