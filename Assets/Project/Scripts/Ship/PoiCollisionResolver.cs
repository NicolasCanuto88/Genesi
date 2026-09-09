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

        [Tooltip("Rev AI (fix v3.1) — Fattore di attenuazione dell'impulse " +
                 "rotation-caused rispetto al calcolo raw depth/dt. Default 0.3 " +
                 "(30% del calcolo raw). Motivazione: rotation collision applica " +
                 "impulse ogni frame in cui c'è penetrazione (auto-regolante ma " +
                 "cumulativo). Il calcolo raw depth/dt può essere alto (es. " +
                 "25 u/s per penetrazione 0.5m), sovraccaricando il POI. " +
                 "Attenuazione moltiplicativa: impulse effettivo = " +
                 "(depth/dt × rotationImpulseFactor) × shipMass / poiMass. " +
                 "\n\nTuning: 0.1 = molto morbido (POI scivola lentamente), " +
                 "0.3 = default (buon compromesso), 1.0 = raw depth/dt (POI " +
                 "vola via). Nota: NON impatta la soglia effettistica " +
                 "(ConfirmMaxVelocity vs impactVelocity raw), solo la forza " +
                 "fisica applicata al POI.")]
        [Range(0.01f, 2f)]
        [SerializeField] private float rotationImpulseFactor = 0.3f;

        [Tooltip("Rev AI (fix v3.3) — Cap sull'impactVelocity raw calcolato come " +
                 "depth/dt per rotation-caused collisions. Default 8 u/s.\n\n" +
                 "Motivazione: la formula depth/dt è geometricamente sbagliata come " +
                 "proxy di velocità di impatto per rotation. Se un compound OBB " +
                 "allungato entra 'di piatto' (es. dorso/pancia della nave contro " +
                 "POI), la depth iniziale al primo frame di contatto è governata " +
                 "dalla LARGHEZZA DELLA FACCIA che compenetra, non dal movimento " +
                 "angolare vero. Risultato: depth/dt sovrastima drasticamente " +
                 "l'intensità dell'urto rispetto a un contatto 'di punta' (muso/" +
                 "coda). Effetti a cascata: severity classification salta a Heavy " +
                 "(audio più forte con riverbero), damage quadratico esplode, " +
                 "impulse POI eccessivo.\n\n" +
                 "Fix minimo: cap l'impactVelocityRaw. Qualunque sia la geometria " +
                 "di contatto, l'urto rotation-caused non può classificarsi come " +
                 "più violento di questo valore. Non è fisicamente corretto (la " +
                 "fisica pura richiederebbe ω × r, velocità tangenziale del " +
                 "punto di contatto), ma è PERCETTIVAMENTE COERENTE con il " +
                 "design 'rotation = spinta cinematica moderata, non schianto " +
                 "violento'.\n\n" +
                 "Tuning: 5 = molto contenuto (rotation quasi mai Heavy), " +
                 "8 = default (bilancia coerenza percettiva e feedback fisico), " +
                 "20 = permissivo (differenza muso/dorso ancora percepibile). " +
                 "Solo il caso rotation è cappato — le collision traslazionali " +
                 "usano impactVelocity radialInward non cappata (fisicamente " +
                 "corretta per il caso traslazionale).")]
        [Min(0.1f)]
        [SerializeField] private float rotationImpactVelocityCap = 8f;

        [Header("Debug")]
        [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development Build). Standard Rev BA — default off.")]
        [SerializeField] private bool showDebugUI = false;
        [Tooltip("Log diagnostici verbosi: collisioni risolte + heartbeat throttled (1/sec) con posizioni logiche/visuali. Standard Rev BA — default off (consolida i vecchi logVerbose + logVerbose). I problemi reali (istanza duplicata) restano sempre a log.")]
        [SerializeField] private bool logVerbose = false;

        // ── Costanti fisiche (Rev AI — refactor rotation collision) ──────────
        //
        // Spostate da ShipImpactHandler in Rev AI. Motivazione: il trasferimento
        // di momento al POI (ApplyMomentumTransferToPoi) è ora responsabilità
        // di questo resolver (chiamato sia da ResolveCollision traslazionale
        // sia da ResolveRotationPenetration). Nessun altro sistema usa queste
        // costanti — sono dedicate al calcolo dell'impulse fisico.
        //
        // Q3 confermata Rev Z: EffectiveShipMass è costante = 1.0. PoiData.Mass
        // agisce come "manopola del rapporto di massa". Se cambia in futuro,
        // modificare solo qui — nessun altro punto del sistema lo referenzia.

        /// <summary>Massa effettiva della nave nel calcolo di momento. Q3=1.0 (Rev Z).</summary>
        private const float EffectiveShipMass = 1.0f;

        /// <summary>Soglia sotto cui la direzione radiale ship→POI è degenere (ship ≈ POI).</summary>
        private const float DegenerateRadialDistanceEpsilon = 1e-4f;

        // ── Stato server-only ─────────────────────────────────────────────────

        /// <summary>
        /// Insieme di POI attualmente in stato di latch. Chiave:
        /// PoiInstance.NetworkObjectId. Popolato al fire di OnHardCollision,
        /// svuotato per POI quando la nave si allontana oltre soglia isteresi
        /// o quando il POI despawna.
        /// </summary>
        private readonly HashSet<ulong> _latchedPoiIds = new HashSet<ulong>();

        /// <summary>
        /// Rev AI (fix v3.2) — Sottoinsieme di _latchedPoiIds che traccia i POI
        /// il cui latch è stato causato da collisione TRASLAZIONALE (ResolveCollision).
        ///
        /// Motivazione: nel caso ibrido "traslazione + rotazione simultanee"
        /// (nave che arriva a POI con velocità E rotea), l'impulse traslazionale
        /// è già forte e sufficiente a spingere il POI. Se anche
        /// ResolveRotationPenetration applicasse il suo impulse (attenuato ma
        /// continuo), i due si sommerebbero e il POI verrebbe spinto più del
        /// dovuto (bug segnalato da Nicolas post-v3.1).
        ///
        /// Uso di questo set: ResolveRotationPenetration skippa l'impulse
        /// rotation quando il POI è in _translationLatchedPoiIds
        /// (traslazione ha già applicato impulse forte). Se il POI è latched
        /// solo per rotazione (_latchedPoiIds ma NON _translationLatchedPoiIds),
        /// l'impulse rotation continua ad essere applicato per garantire
        /// il push-out effettivo.
        ///
        /// Coerenza: quando UpdateLatchHysteresis rimuove un POI da
        /// _latchedPoiIds, deve rimuoverlo anche da questo set. OnNetworkSpawn
        /// e OnPoiDespawn devono clearare/rimuovere in entrambi i set.
        /// </summary>
        private readonly HashSet<ulong> _translationLatchedPoiIds = new HashSet<ulong>();

        /// <summary>
        /// Rev AB — frame counter per throttle del log diagnostico "Heartbeat"
        /// ogni ~1 sec (50 fixed frames). Emesso solo se logVerbose == true.
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
                _translationLatchedPoiIds.Clear();
            }

            if (Instance == this) Instance = null;
        }

        private void HandlePoiDespawned(PoiInstance poi)
        {
            if (poi == null || poi.NetworkObject == null) return;
            ulong id = poi.NetworkObject.NetworkObjectId;
            _latchedPoiIds.Remove(id);
            _translationLatchedPoiIds.Remove(id);
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
        /// Rev AI (fix D18 anticipato, opzione D1-a) — Query-only: verifica se
        /// il compound della nave, alla posizione+rotation date, compenetra un
        /// qualunque POI attualmente in scena.
        ///
        /// MOTIVAZIONE:
        ///   Con QD-γ (rotation libera 6DoF Rev AH) la nave può ruotare in place
        ///   e "spazzare" muso/coda attraverso un POI vicino. ResolveCollision
        ///   gestisce SOLO clamp traslazionale (candidatePos vs currentPos);
        ///   la rotation non produce un delta di posizione da clampare, quindi
        ///   la penetrazione via rotation non viene mai rilevata dal path
        ///   posizionale.
        ///
        ///   Questo metodo esiste come **query pura** (nessun clamp, nessun
        ///   side effect, nessuna emissione OnHardCollision) da chiamare da
        ///   ShipMovement.UpdateOrientation PRIMA di applicare la rotation
        ///   incrementale, per decidere se congelare la rotation nel frame
        ///   corrente (freeze pattern "sei incastrato, non puoi ruotare").
        ///
        ///   Il fix strutturale completo (rotation swept CCD) resta debito
        ///   D18 per M4+. Questo metodo è la copertura minimale del gap
        ///   emerso post-AH.
        ///
        /// PARAMETRI:
        ///   shipPos      — posizione da testare (tipicamente
        ///                  shipMovement.LogicalPosition corrente).
        ///   shipRotation — rotation da testare (tipicamente
        ///                  shipMovement.LogicalRotation corrente).
        ///
        /// RITORNA:
        ///   true se ALMENO UN POI ha compenetrazione con il compound ship.
        ///   Early exit al primo hit (nessun sort, nessuna selezione winner).
        ///
        /// COSTO:
        ///   O(POIs × shipVolumes × poiVolumes) worst case. In pratica basso
        ///   perché ComputeMaxPenetration ha fast rejection interna (bounding
        ///   sphere distance) su coppie di volumi non-vicini.
        ///   Se in playtest emerge lag con molti POI in scena, aggiungere
        ///   early rejection tramite Data.ApproximateRadius prima della
        ///   chiamata a ComputeMaxPenetration.
        ///
        /// GUARD:
        ///   Se non IsServer, ship compound non configurato, o zero POI in
        ///   scena → ritorna false (degradazione elegante).
        /// </summary>
        public bool IsShipPenetratingAnyPoi(Vector3 shipPos, Quaternion shipRotation)
        {
            if (!IsServer) return false;

            var shipMovement = ShipMovement.Instance;
            IReadOnlyList<CompoundVolume> shipVolumes =
                (shipMovement != null && shipMovement.Compound != null)
                    ? shipMovement.Compound.Volumes
                    : null;
            if (shipVolumes == null || shipVolumes.Count == 0) return false;

            foreach (var poi in PoiRegistry.All)
            {
                if (poi == null || poi.Data == null) continue;
                var poiVolumes = poi.CollisionVolumes;
                if (poiVolumes == null || poiVolumes.Count == 0) continue;

                CompoundColliderMath.PairContact pair =
                    CompoundColliderMath.ComputeMaxPenetration(
                        shipPos, shipRotation, shipVolumes,
                        poi.LogicalPosition, poi.LogicalRotation, poiVolumes,
                        fallbackNormal: Vector3.up);

                if (pair.Depth > 0f) return true;  // early exit — un hit basta
            }

            return false;
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

            // ── DEBUG HEARTBEAT (guardato da logVerbose) ───────────────────
            //    Log ogni ~1 sec dello stato che il resolver sta vedendo.
            //    Utile per diagnosticare divergenze visual↔logica o mancata
            //    rilevazione. Off in gameplay normale.
            _debugFrameCounter++;
            if (logVerbose && (_debugFrameCounter % 50 == 0))
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
            //
            // Rev AI (refactor): l'impulse fisico al POI è ora applicato QUI
            // dal resolver (spostato da ShipImpactHandler.ApplyMomentumTransferToPoi).
            // Q1-B: applicato SEMPRE indipendente dalla soglia velocità
            // (nessun early return per impactVelocity sotto ConfirmMaxVelocity).
            // Il latch anti-spam si applica anche all'impulse: un solo impulso
            // per sessione di contatto (evita impulse ripetuti mentre POI e
            // ship sono in contatto continuo). Il POI decade poi per inerzia
            // (PoiInstance._logicalVelocity decay).
            //
            // Il caso rotation-caused (ResolveRotationPenetration) segue una
            // semantica diversa: impulse ogni frame senza latch, auto-regolato
            // dalla profondità di penetrazione. Coerente concettualmente:
            //   - traslazione: "colpo secco" (bam, un impulso forte, POI decade)
            //   - rotation: "spinta continua" (spingi via il POI mentre ci
            //                 ruoti dentro, auto-regolato)
            if (radialInward > 0f)
            {
                ulong poiId = winner.NetworkObject != null
                    ? winner.NetworkObject.NetworkObjectId
                    : 0ul;

                if (poiId != 0ul && !_latchedPoiIds.Contains(poiId))
                {
                    _latchedPoiIds.Add(poiId);

                    // Rev AI (fix v3.2): marca il POI come "latched per traslazione".
                    // Usato da ResolveRotationPenetration per skippare l'impulse
                    // rotation-attenuato (che si sommerebbe alla spinta traslazionale
                    // già applicata). Vedi doc di _translationLatchedPoiIds.
                    _translationLatchedPoiIds.Add(poiId);

                    // Rev AI (refactor): impulse fisico al POI (spostato da
                    // ShipImpactHandler). Applicato SEMPRE (Q1-B), la soglia
                    // agisce solo sull'effettistica ship-side downstream.
                    ApplyMomentumTransferToPoi(radialInward, winner);

                    OnHardCollision?.Invoke(radialInward, winner);

                    if (logVerbose)
                    {
                        if (logVerbose)
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
                else if (logVerbose && poiId == 0ul)
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
                    _translationLatchedPoiIds.Remove(id);
                    continue;
                }

                float releaseThreshold = poi.Data.ApproximateRadius * collisionReleaseHysteresis;
                Vector3 delta = shipPos - poi.LogicalPosition;
                if (delta.sqrMagnitude > releaseThreshold * releaseThreshold)
                {
                    _latchedPoiIds.Remove(id);
                    _translationLatchedPoiIds.Remove(id);
                    if (logVerbose)
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
            if (!showDebugUI) return;

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

        // =========================================================================
        // PHYSICS RESPONSE (Rev AI — refactor rotation collision)
        // =========================================================================

        /// <summary>
        /// Trasferimento di momento al POI colpito. Applica un impulso radiale
        /// lungo la direzione ship→POI, con magnitudo proporzionale a
        /// impactVelocity e scalata dal rapporto di massa (Q3 confermata Rev Z:
        /// EffectiveShipMass = 1.0, PoiData.Mass è la manopola del rapporto).
        ///
        /// Rev AI (refactor): spostato da ShipImpactHandler.ApplyMomentumTransferToPoi.
        /// Ora è responsabilità del resolver, chiamato SIA da ResolveCollision
        /// (traslazionale) SIA da ResolveRotationPenetration (rotation-caused).
        ///
        /// Q1-B (Rev AI): applicato SEMPRE, senza soglia velocità. Coerenza tra
        /// rotation-caused e traslation-caused. La soglia si applica SOLO
        /// all'effettistica ship-side (danno/shake/audio/banner) via
        /// OnHardCollision → ShipImpactHandler.HandleHardCollision.
        ///
        /// GUARD:
        ///   - dist &lt; DegenerateRadialDistanceEpsilon → skip (direzione radiale
        ///     mal definita, ship ≈ POI).
        ///   - poi.Data.Mass ≤ 0 → skip con warning (config invalida).
        /// </summary>
        private void ApplyMomentumTransferToPoi(float impactVelocity, PoiInstance poi)
        {
            if (poi == null || poi.Data == null) return;

            var shipMovement = ShipMovement.Instance;
            if (shipMovement == null) return;

            Vector3 shipToPoi = poi.LogicalPosition - shipMovement.LogicalPosition;
            float dist = shipToPoi.magnitude;

            if (dist < DegenerateRadialDistanceEpsilon)
            {
                if (logVerbose)
                {
                    Debug.LogWarning($"[PoiCollisionResolver] Direzione radiale degenere " +
                                     $"(dist={dist:E2} u) — impulse skippato (POI={poi.Data.DisplayName}).");
                }
                return;
            }

            Vector3 radialDir = shipToPoi / dist;

            float poiMass = poi.Data.Mass;
            if (poiMass <= 0f)
            {
                Debug.LogWarning($"[PoiCollisionResolver] PoiData.Mass non positiva ({poiMass}) " +
                                 $"su {poi.Data.DisplayName} — impulse skippato.");
                return;
            }

            float deltaVMagnitude = impactVelocity * EffectiveShipMass / poiMass;
            Vector3 impulse = radialDir * deltaVMagnitude;

            poi.AddImpulse(impulse);

            if (logVerbose)
            {
                Debug.Log($"[PoiCollisionResolver] IMPULSO → POI={poi.Data.DisplayName}, " +
                          $"deltaV={deltaVMagnitude:F3} u/s, " +
                          $"dir=({radialDir.x:F2},{radialDir.y:F2},{radialDir.z:F2}), " +
                          $"poiMass={poiMass:F1}, v={impactVelocity:F2} u/s");
            }
        }

        /// <summary>
        /// Rev AI (fix rotation collision v3 definitivo) — Rileva compenetrazione
        /// causata da rotation pura e applica physics response completa.
        ///
        /// MOTIVAZIONE:
        ///   ResolveCollision gestisce SOLO clamp traslazionale (candidatePos vs
        ///   currentPos). Con QD-γ (rotation libera 6DoF Rev AH), la nave ferma
        ///   sopra/dentro un POI che ruota fa "spazzare" muso/coda attraverso
        ///   il volume POI senza rilevazione dal path posizionale.
        ///
        ///   Questo metodo è la copertura del gap: chiamato da
        ///   ShipMovement.UpdateOrientation DOPO aver applicato la rotation,
        ///   rileva la penetrazione risultante e:
        ///     1. Applica SEMPRE impulse push-out al POI (auto-libera la nave
        ///        via inerzia del POI stesso — invariante Nicolas: "il POI
        ///        scivola via, il pilota ha spazio per manovrare").
        ///     2. Emette OnHardCollision solo SE impactVelocity ≥ soglia →
        ///        chain effettistica completa (danno hull, shake, audio,
        ///        banner MOTORI OFFLINE).
        ///
        ///   Coerente con la chain esistente per collision traslazionale.
        ///   Il fix strutturale completo (rotation swept CCD) resta debito
        ///   D18 per M4+.
        ///
        /// PARAMETRI:
        ///   shipPos      — posizione ship corrente (post-integration).
        ///   shipRotation — rotation ship corrente (post-integration).
        ///   dt           — Time.fixedDeltaTime, usato per convertire depth →
        ///                  impactVelocity (depth/dt = "velocità di penetrazione").
        ///
        /// FORMULA impactVelocity (Q2 confermata Nicolas):
        ///   impactVelocity = depth / dt
        ///   Se penetri 0.1 m in un frame di 0.02s, sei "andato dentro" a
        ///   5 m/s. Rappresenta l'intensità della compenetrazione. Confrontabile
        ///   con ConfirmMaxVelocity per la soglia effettistica.
        ///
        /// Q1-B (Rev AI): impulse SEMPRE applicato (indipendente da soglia).
        /// Soglia SOLO per emissione OnHardCollision → effettistica.
        ///
        /// GUARD:
        ///   - Non IsServer / ship compound non configurato / zero POI in scena →
        ///     no-op silenzioso.
        ///   - dt ≤ 0 → no-op (frame degenere).
        /// </summary>
        public void ResolveRotationPenetration(Vector3 shipPos, Quaternion shipRotation, float dt)
        {
            if (!IsServer) return;
            if (dt <= 0f) return;

            var shipMovement = ShipMovement.Instance;
            IReadOnlyList<CompoundVolume> shipVolumes =
                (shipMovement != null && shipMovement.Compound != null)
                    ? shipMovement.Compound.Volumes
                    : null;
            if (shipVolumes == null || shipVolumes.Count == 0)
            {
                // Anche senza ship compound, chiama UpdateLatchHysteresis per
                // rilasciare latch stale (edge case: boot senza ship compound).
                UpdateLatchHysteresis(shipPos);
                return;
            }

            // Selezione POI vincitore: quello con depth di compenetrazione
            // massima (analogo a ResolveCollision, riuso del pattern).
            PoiInstance winner = null;
            float winnerDepth = 0f;

            foreach (var poi in PoiRegistry.All)
            {
                if (poi == null || poi.Data == null) continue;
                var poiVolumes = poi.CollisionVolumes;
                if (poiVolumes == null || poiVolumes.Count == 0) continue;

                CompoundColliderMath.PairContact pair =
                    CompoundColliderMath.ComputeMaxPenetration(
                        shipPos, shipRotation, shipVolumes,
                        poi.LogicalPosition, poi.LogicalRotation, poiVolumes,
                        fallbackNormal: Vector3.up);

                if (pair.Depth > winnerDepth)
                {
                    winnerDepth = pair.Depth;
                    winner = poi;
                }
            }

            // Rev AI (fix v3.1): rilascia latch stale ANCHE se non c'è
            // penetrazione corrente. Il POI potrebbe essersi allontanato
            // per inerzia dall'impulse ricevuto in frame precedenti.
            // Chiamata prima del early return "no winner" per gestire il
            // caso "player smette di ruotare, POI decade, latch da liberare".
            UpdateLatchHysteresis(shipPos);

            if (winner == null) return;  // nessuna penetrazione

            // Q2: velocità di penetrazione raw = depth/dt.
            // Usata come metrica per la soglia effettistica (confrontabile
            // con ConfirmMaxVelocity).
            //
            // Rev AI (fix v3.3): CAP applicato per correggere lo sballamento
            // geometrico. La formula depth/dt sovrastima l'intensità quando
            // il compound ship entra "di piatto" (es. dorso/pancia OBB
            // allungato contro POI): la depth iniziale è governata dalla
            // larghezza della faccia che compenetra, non dal movimento
            // angolare. Cap regolarizza la metrica a un valore percettivamente
            // coerente con "rotation = spinta cinematica moderata, non
            // schianto violento". Vedi rotationImpactVelocityCap doc per
            // motivazione geometrica dettagliata.
            //
            // Effetto: qualunque geometria di contatto (muso vs dorso), lo
            // stesso rate rotazionale produce lo stesso boom + impulse.
            // La differenza tra "collisione di punta" e "collisione di piatto"
            // sparisce dal feedback player — coerente con il design semplificato
            // dell'impulse rotation-caused (applicato al centro POI, senza
            // generare rotazione POI).
            float impactVelocityRaw = Mathf.Min(winnerDepth / dt, rotationImpactVelocityCap);

            // Rev AI (fix v3.1): impulse ATTENUATO per rotation-caused.
            // Motivazione: impulse è applicato ogni frame di contatto
            // (auto-regolante via depth). Con calcolo raw depth/dt, la
            // spinta cumulativa era eccessiva (POI volava via troppo forte).
            // Attenuazione moltiplicativa via rotationImpulseFactor
            // (default 0.3, tunabile in Inspector).
            //
            // NOTA: la soglia effettistica confronta impactVelocityRaw
            // (metrica intensità urto), NON impactVelocityAttenuated
            // (usato solo per l'impulse fisico). Un urto rotazionale
            // "forte" (raw > soglia) triggera boom + banner motori,
            // indipendentemente dall'attenuazione applicata al POI.
            float impactVelocityAttenuated = impactVelocityRaw * rotationImpulseFactor;

            // Rev AI (fix v3.2): gate impulse rotation dal secondo latch.
            //
            // Se il POI è in _translationLatchedPoiIds, significa che
            // ResolveCollision (traslazionale) ha già applicato un impulse
            // FORTE al POI in questo o in un frame precedente della sessione
            // di contatto corrente. In quel caso, sommare anche l'impulse
            // rotation-attenuato sarebbe eccessivo (bug segnalato da Nicolas
            // post-v3.1: "collido con velocità E rotazione, le due cose si
            // sommano").
            //
            // Se il POI è latched SOLO per rotation (in _latchedPoiIds ma NON
            // in _translationLatchedPoiIds), l'impulse rotation continua ad
            // essere applicato per garantire il push-out continuo (necessario
            // in caso puro rotation: nave ferma che rotea contro POI — l'impulse
            // rotation-only iniziale sarebbe troppo debole per far uscire il
            // POI oltre soglia isteresi, servono più frame di push-out cumulato).
            //
            // Se il POI non è latched affatto → impulse applicato normalmente.
            //
            // Q1-B: impulse SEMPRE (indipendente da soglia effettistica) — la
            // logica di gate qui è ortogonale, riguarda la coesistenza con
            // traslazione.
            ulong winnerId = winner.NetworkObject != null
                ? winner.NetworkObject.NetworkObjectId
                : 0ul;

            bool translationAlreadyPushed = winnerId != 0ul
                && _translationLatchedPoiIds.Contains(winnerId);

            if (!translationAlreadyPushed)
            {
                // Fix v3.1: applicato con magnitudo attenuata.
                ApplyMomentumTransferToPoi(impactVelocityAttenuated, winner);
            }
            else if (logVerbose)
            {
                Debug.Log($"[PoiCollisionResolver] Impulse rotation SKIPPATO " +
                          $"su POI={winner.Data.DisplayName} — POI già spinto " +
                          $"da collisione traslazionale (evita cumulo).");
            }

            // Effettistica: solo se sopra soglia (chain OnHardCollision →
            // ShipImpactHandler.HandleHardCollision → damage/shake/audio/banner).
            // Recupero soglia da DockingController (source of truth invariante
            // Rev X + Rev Z: un solo tuning globale per la soglia di "urto
            // significativo"). Se DockingController mancante (edge case boot),
            // fallback conservativo 1.0 u/s coerente col fallback di
            // ShipImpactHandler.HandleHardCollision.
            float threshold = DockingController.Instance != null
                ? DockingController.Instance.ConfirmMaxVelocity
                : 1.0f;

            // Rev AI (fix v3.1): LATCH ANTI-SPAM per emissione OnHardCollision.
            // Il latch è lo STESSO usato da ResolveCollision (traslazionale) —
            // _latchedPoiIds. Semantica unificata: un solo "boom" per sessione
            // di contatto con lo stesso POI, sia rotazionale sia traslazionale.
            //
            // L'impulse fisico (sopra) resta continuo per garantire push-out
            // effettivo del POI. Solo la CHAIN EFFETTISTICA (damage hull +
            // shake camera + audio one-shot + banner MOTORI OFFLINE) è
            // gated dal latch.
            //
            // Il rilascio del latch avviene via UpdateLatchHysteresis
            // (chiamato ad ogni frame sopra) quando il POI si allontana oltre
            // ApproximateRadius × collisionReleaseHysteresis (default 1.2).
            // Il player che continua a ruotare contro POI vede: 1 boom → POI
            // scivola via silenziosamente per inerzia → se torna a contatto,
            // nuovo boom (latch rilasciato dall'isteresi distanza).
            if (impactVelocityRaw >= threshold)
            {
                ulong poiId = winner.NetworkObject != null
                    ? winner.NetworkObject.NetworkObjectId
                    : 0ul;

                if (poiId != 0ul && !_latchedPoiIds.Contains(poiId))
                {
                    _latchedPoiIds.Add(poiId);

                    if (logVerbose)
                    {
                        Debug.LogWarning($"[PoiCollisionResolver] ROTATION COLLISION → " +
                                         $"POI={winner.Data.DisplayName}, depth={winnerDepth:F3} u, " +
                                         $"vRaw={impactVelocityRaw:F2} u/s (soglia={threshold:F2}), " +
                                         $"vAttenuated={impactVelocityAttenuated:F2} u/s → " +
                                         $"emetto OnHardCollision (effettistica attiva) + latch.");
                    }
                    OnHardCollision?.Invoke(impactVelocityRaw, winner);
                }
                else if (logVerbose)
                {
                    Debug.Log($"[PoiCollisionResolver] ROTATION COLLISION continua " +
                              $"su POI={winner.Data.DisplayName} già latched — solo " +
                              $"impulse push-out attenuato, nessuna emissione ripetuta.");
                }
            }
            else if (logVerbose)
            {
                Debug.Log($"[PoiCollisionResolver] ROTATION COLLISION (sotto soglia) → " +
                          $"POI={winner.Data.DisplayName}, depth={winnerDepth:F3} u, " +
                          $"vRaw={impactVelocityRaw:F2} u/s < {threshold:F2} u/s → " +
                          $"solo impulse push-out attenuato, no effettistica.");
            }
        }
    }
}