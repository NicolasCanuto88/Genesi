using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Serialization;
using SpaceSurvivor.Collision;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// DockingController — Milestone 3 Fase 3 Blocco 3.1 (Sotto-step 3.1.3).
    /// NetworkBehaviour singleton — GameObject dedicato figlio di Nave.
    ///
    /// RESPONSABILITÀ:
    ///   Gestisce il minigioco di attracco attivo durante
    ///   NavigationState.Docking. Server-authoritative su geometria, input,
    ///   auto-align rotazionale; espone via NetworkVariable i valori che la
    ///   UI del minigioco (3.1.5) leggerà per rendere cerchio dinamico +
    ///   cornice fissa + prompt di conferma.
    ///
    /// FLUSSO DI VITA:
    ///   1. Ingresso Docking (via AnchorSystem in 3.1.2 → PropulsionSystem):
    ///      HandleEnterDocking() cattura _currentPoi, calcola geometria
    ///      congelata (approachAxis, base perpendicolare, initialAxial,
    ///      initialShipRotation, targetShipRotation).
    ///   2. Update server-only (solo se stato == Docking):
    ///      - Timer freeze thrusters post-collisione (LATCH)
    ///      - Applica strafe RCS a ship.LogicalPosition
    ///      - Ricalcola geometria (axial, lateral, distance)
    ///      - Auto-align rotazionale: slerp shortest-arc, pesato su
    ///        progressione (reversibile)
    ///      - Check hard collision → fire OnHardCollision + latch + freeze
    ///      - Check out-of-range → force undock a Coasting
    ///      - Aggiorna NetVar per la UI
    ///   3. RequestConfirmAnchor (chiamato in 3.1.4 dal PilotStation):
    ///      se IsInAnchorTolerance → transiziona Docking → Docked,
    ///      setta PoiInstance.ScanState = Anchored.
    ///   4. Uscita da Docking (verso Docked, o forzata a Coasting):
    ///      HandleExitDocking() ripulisce cache e flag.
    ///
    /// GEOMETRIA DEL MINIGIOCO:
    ///   L'asse di approccio è congelato all'ingresso Docking:
    ///     approachAxisWorld = _currentPoi.DockingAnchorForwardWorld
    ///   (Rev AB, Q6=B: derivato dal DockingAnchor Transform sul prefab POI —
    ///   ex poi.LogicalRotation × poi.DockingApproachDirectionLocal, rimosso).
    ///   Rappresenta la direzione da cui la nave deve venire (es. "sopra"
    ///   il POI). Vettore utile per il calcolo:
    ///     fromPoiToShip = ship.LogicalPosition - poi.LogicalPosition
    ///   Componenti:
    ///     axial   = Dot(fromPoiToShip, approachAxis)  → distanza lungo asse.
    ///               Positivo = nave sul lato di approccio, deve ridurre
    ///               axial per avvicinarsi. Negativo = ha superato il POI.
    ///     lateral = |fromPoiToShip - approachAxis * axial|
    ///               → scostamento perpendicolare all'asse.
    ///
    /// AUTO-ALIGN ROTAZIONALE (shortest-arc, reversibile, basato su progressione):
    ///   All'ingresso Docking:
    ///     initialShipRotation = ship.LogicalRotation
    ///     currentShipDownGlobal = initialShipRotation * Vector3.down
    ///     correction = Quaternion.FromToRotation(currentShipDownGlobal,
    ///                                            approachAxisWorld)
    ///     targetShipRotation = correction * initialShipRotation
    ///   Ogni tick:
    ///     t = 1 - Clamp01((axial - finalDockingDistance) /
    ///                     (initialAxial - finalDockingDistance))
    ///     ship.LogicalRotation = Slerp(initialShipRotation,
    ///                                  targetShipRotation, t)
    ///   Reversibile: se il pilota indietreggia, axial cresce, t scende, la
    ///   rotazione torna verso initialShipRotation. Motivata narrativamente
    ///   da "computer di allineamento + RCS di precisione" (consumo 60W in
    ///   PropulsionSystem).
    ///   Convenzione universale docking cilindro/cilindro: nessuna rotazione
    ///   azimutale attorno all'asse di approccio — la nave allinea SOLO la
    ///   pancia, l'angolo di roll rispetto all'asse resta quello d'ingresso.
    ///
    /// STRAFE INPUT — MODELLO INERZIALE (Rev W — D7/D8):
    ///   Modello di volo con integrazione a due fasi: input → accelerazione →
    ///   velocità → posizione. Con stabilizzazione RCS per-asse (2C β).
    ///     acceleration_world =
    ///       (_perpBasisX * input.x + _perpBasisY * input.y
    ///        + (-_approachAxisWorld) * input.z) * rcsThrustPower
    ///     _strafeVelocity += acceleration_world * dt
    ///     STABILIZZAZIONE PER-ASSE (2C β, Rev W hotfix):
    ///       per ogni asse (X/Y/Z del frame POI), se |input[asse]| <
    ///       inputDeadZone, la componente della velocity su quell'asse è
    ///       ridotta di stabilizingThrustPower * dt (senza overshoot, si ferma
    ///       esattamente a zero). Modelli i "thrusters di stabilizzazione RCS"
    ///       che tengono ferma la nave sugli assi non-comandati.
    ///     _strafeVelocity clamped a magnitude <= maxRcsVelocity
    ///     newPos = ship.LogicalPosition + _strafeVelocity * dt
    ///
    ///   Convenzione input invariata rispetto al modello legacy:
    ///     input.z > 0 → avvicinati al POI (axial diminuisce)
    ///     input.z < 0 → allontanati (axial aumenta)
    ///     input.x     → strafe destra/sinistra nel piano perp
    ///     input.y     → strafe su/giù nel piano perp
    ///
    ///   Coerenza multiplayer (4B): _strafeVelocity è server-only, NON
    ///   replicato via NetworkVariable. Gli altri client vedono il movimento
    ///   attraverso la replica di ship.LogicalPosition tramite
    ///   ExternalWorldFollower. Nessuna latenza percepita — nessun HUD
    ///   consuma la velocità direttamente.
    ///
    ///   Rilascio input: la stabilizzazione per-asse riduce la velocità sugli
    ///   assi rilasciati a rate lineare stabilizingThrustPower. Il pilota può
    ///   comunque applicare controspinta esplicita per fermarsi più
    ///   rapidamente (rcsThrustPower > stabilizingThrustPower). Impostare
    ///   stabilizingThrustPower=0 per tornare a Newton puro (2B).
    ///
    /// COLLISION — CLAMP POSIZIONALE HARD + SLIDE TANGENZIALE (Rev W — D8;
    /// Rev AB — Blocco 3.2.d D5, migrato a compound collider OBB+Sphere):
    ///   Prima di applicare newPos, invoco CompoundColliderMath.ClampAgainstCompound
    ///   passando shipVolumes=NULL (invariante attracco guidato — nave = punto
    ///   contro volumi POI, equivalente semantico al pre-Rev AB "shipRadius=0f").
    ///   Il math helper trova la coppia (punto nave, volume POI) con depth di
    ///   compenetrazione massima; se depth > 0:
    ///     newPos = candidatePos + normalOutward * depth   (clamp lungo asse push-out)
    ///     if (Dot(velocity, normalOutward) < 0):
    ///       fire OnHardCollision(|radialInward|), setta latch
    ///       _strafeVelocity += normalOutward * radialInward
    ///       // → componente verso POI azzerata, tangenziale preservata (slide)
    ///
    ///   INVARIANTE: la posizione della nave non compenetra alcun volume del
    ///   compound POI. Il compound (OBB / Sphere multi-volume) è fisicamente
    ///   inattraversabile, indipendentemente da quanto veloce si arrivi.
    ///
    ///   OnHardCollision(impactVelocity) emette la componente della velocità
    ///   nave lungo l'asse di push-out della coppia vincitore (Rev W).
    ///   Semantica per Blocco 3.2 (danno hull proporzionale all'impatto).
    ///
    ///   Il latch (_hasFiredCollisionThisSession) previene spam di eventi
    ///   se il pilota resta al bordo. Rilasciato quando distance dal centro
    ///   logico POI > Data.ApproximateRadius * collisionReleaseHysteresis
    ///   (default 1.2×; Rev AB Q5=B: ex-HardCollisionRadius).
    ///
    ///   NB: postCollisionThrusterFreezeSeconds RIMOSSO in Rev W. Con clamp
    ///   posizionale la "sospensione" thrusters non serve più — la fisica
    ///   gestisce naturalmente il contatto.
    ///
    /// OUT-OF-RANGE (uscita forzata):
    ///   Se axial > maxDockingAxialRange, axial < -maxDockingAxialRange, o
    ///   lateral > maxDockingLateralRange → transizione a Coasting (via
    ///   AnchorSystem.RequestUndock che azzera anche AnchoredPoiId e riporta
    ///   ScanState del POI, mantenendo coerenza).
    ///
    /// DIPENDE DA:
    ///   PropulsionSystem (Instance, CurrentNavState, AnchoredPoiId,
    ///     RequestNavigationState) ·
    ///   ShipMovement (Instance, LogicalPosition/Rotation + setter server) ·
    ///   AnchorSystem (Instance, RequestUndock) ·
    ///   PoiInstance / PoiRegistry (risoluzione POI da NetworkObjectId) ·
    ///   NetworkManager.SpawnManager (lookup NetworkObject)
    ///
    /// dipende da setup Editor: GameObject dedicato figlio di Nave, con
    ///   NetworkObject + DockingController. Fratello di PropulsionSystem,
    ///   ShipMovement, AnchorSystem.
    /// </summary>
    public class DockingController : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static DockingController Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── Tuning (SerializeField — modificabili da inspector; in futuro
        //    passibili di bonus/malus di ruolo del pilota) ──────────────────
        [Header("Strafe RCS — Modello inerziale (Rev W)")]
        [Tooltip("Accelerazione applicata dai thrusters RCS a input pieno, in u/s². " +
                 "Il vettore accelerazione è composto sui tre assi del frame del " +
                 "POI (perpBasisX, perpBasisY, -approachAxisWorld) scalati dai " +
                 "componenti input × questo valore.\n\n" +
                 "Default 30 u/s²: con maxRcsVelocity=8 u/s la nave raggiunge il " +
                 "cap in ~0.27s — responsività fine ma senza brusche.\n\n" +
                 "Rinominato in Rev W da strafeSpeedRcs (semantica cambiata: " +
                 "prima u/s velocità diretta, ora u/s² accelerazione). Il valore " +
                 "vecchio è preservato via FormerlySerializedAs, ma DEVE essere " +
                 "re-tunato — 10 u/s² sarebbe troppo debole.")]
        [Min(0.1f)]
        [FormerlySerializedAs("strafeSpeedRcs")]
        [SerializeField] private float rcsThrustPower = 30f;

        [Tooltip("Cap hard alla magnitudine della velocità RCS, in u/s. Sopra " +
                 "questo valore la velocità viene clampata (i thrusters non " +
                 "possono spingere oltre). Default 8 u/s: valore basso " +
                 "coerente con Newton puro (asse 2B — nessun damping) — velocità " +
                 "più alte renderebbero la manovra fine impraticabile.")]
        [Min(0.1f)]
        [SerializeField] private float maxRcsVelocity = 8f;

        [Tooltip("[Rev W hotfix — modello 2C per-axis] Accelerazione dei thrusters " +
                 "di stabilizzazione RCS quando il pilota NON sta comandando un " +
                 "asse (input < inputDeadZone). Modello decelerazione lineare: " +
                 "ogni componente della velocità sulla quale l'input è a zero " +
                 "viene ridotta di stabilizingThrustPower × dt fino a raggiungere " +
                 "esattamente zero (senza overshoot).\n\n" +
                 "Con maxRcsVelocity=8 u/s e stabilizingThrustPower=20 u/s²: " +
                 "il tempo di stop da velocità piena è 8/20 = 0.4s.\n\n" +
                 "Comportamento per-asse (Decisione β): la stabilizzazione lavora " +
                 "indipendentemente sui tre assi del frame POI (perpBasisX, " +
                 "perpBasisY, -approachAxisWorld). Se il pilota preme W (input.z=1) " +
                 "ma non A/D (input.x=0), l'asse X viene stabilizzato mentre Z " +
                 "continua sotto guida. Coerente col comportamento SAS reale.\n\n" +
                 "Impostare a 0 per disabilitare la stabilizzazione e tornare a " +
                 "Newton puro (2B).")]
        [Min(0f)]
        [SerializeField] private float stabilizingThrustPower = 20f;

        [Tooltip("[Rev W hotfix] Soglia sotto la quale l'input di uno stick/axis " +
                 "è considerato \"zero\" ai fini della stabilizzazione RCS. " +
                 "Applicata su |input.x|, |input.y|, |input.z| separatamente. " +
                 "Default 0.05: robusto al drift del gamepad analogico, invisibile " +
                 "alla percezione del pilota. Non applicabile alle azioni digitali " +
                 "(WASD/QE) che sono binarie 0/1 e non hanno drift.")]
        [Range(0f, 0.5f)]
        [SerializeField] private float inputDeadZone = 0.05f;

        [Tooltip("Se true, applica clamp posizionale hard: la nave NON può " +
                 "attraversare alcun volume del compound POI (Rev AB — OBB+Sphere " +
                 "multi-volume). Se false, la collisione emette solo " +
                 "OnHardCollision senza vincolare posizione — utile per debug " +
                 "o test di comportamenti edge. In gameplay normale DEVE " +
                 "restare true.")]
        [SerializeField] private bool useHardPositionClamp = true;

        [Header("Attracco — tolleranze e target")]
        [Tooltip("Distanza assiale ideale dalla superficie del POI al momento " +
                 "dello snap a Docked. Default 40u con ApproximateRadius=30 " +
                 "→ buffer di 10m rispetto al centro. La distanza è misurata " +
                 "lungo _approachAxisWorld dal centro logico POI. La UI " +
                 "(Convenzione B) usa questo valore per sapere quando il cerchio " +
                 "combacia con la cornice.")]
        [Min(1f)]
        [SerializeField] private float finalDockingDistance = 40f;

        [Tooltip("Tolleranza sulla distanza assiale finale. IsInAnchorTolerance " +
                 "richiede |axial - finalDockingDistance| <= questo valore. " +
                 "Default ±5m.")]
        [Min(0.1f)]
        [SerializeField] private float axialDockingTolerance = 5f;

        [Tooltip("Tolleranza sullo scostamento laterale (magnitude del vettore " +
                 "lateral). IsInAnchorTolerance richiede lateralError <= questo " +
                 "valore. Default 15m — corrisponde al cerchio 'ben centrato' " +
                 "nella cornice sulla UI del minigioco.")]
        [Min(0.1f)]
        [SerializeField] private float lateralTolerance = 15f;

        [Tooltip("[Rev W — 6C] Velocità RCS massima consentita per la conferma " +
                 "di ancoraggio. IsInAnchorTolerance richiede |_strafeVelocity| " +
                 "<= questo valore, in aggiunta ai check posizionali " +
                 "(lateral+axial). Con Newton puro (2B) senza questo check il " +
                 "pilota potrebbe confermare l'attracco mentre sta scivolando " +
                 "tangenzialmente sulla superficie del POI — non desiderato.\n\n" +
                 "Default 1.0 u/s: praticamente ferma. Alzare per un attracco " +
                 "più indulgente, abbassare per un attracco chirurgico.")]
        [Min(0.01f)]
        [SerializeField] private float confirmMaxVelocity = 1.0f;

        [Header("Uscita forzata (out-of-range)")]
        [Tooltip("Scostamento laterale massimo prima di transizione automatica " +
                 "a Coasting (uscita dal minigioco). Deve essere > lateralTolerance " +
                 "per lasciare margine di manovra. Default 100m.")]
        [Min(1f)]
        [SerializeField] private float maxDockingLateralRange = 100f;

        [Tooltip("Distanza assiale massima (in valore assoluto) prima di uscita " +
                 "forzata. Il pilota può ancora indietreggiare fino a questa " +
                 "distanza, oppure superare il POI di questa distanza. Deve " +
                 "essere > DockingRadius del POI (200) per non triggerare " +
                 "immediatamente all'ingresso. Default 400m.")]
        [Min(1f)]
        [SerializeField] private float maxDockingAxialRange = 400f;

        [Header("Collisione")]
        [Tooltip("Fattore di isteresi sul rilascio del LATCH di collisione. Il " +
                 "flag _hasFiredCollisionThisSession si sblocca quando la " +
                 "distanza al POI supera Data.ApproximateRadius × questo fattore " +
                 "(Rev AB — ex-HardCollisionRadius, rinominato per allineare " +
                 "nome a semantica). Default 1.2 (20% oltre il raggio " +
                 "approssimato). Previene spam di eventi se il pilota " +
                 "'oscilla' al bordo.")]
        [Min(1.01f)]
        [SerializeField] private float collisionReleaseHysteresis = 1.2f;

        // ── Network Variables (readable dalla UI del minigioco) ───────────────

        /// <summary>Scostamento laterale corrente (magnitude del vettore lateral) in u.</summary>
        private readonly NetworkVariable<float> _netLateralError =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>
        /// Fase 3.1.5 — Scostamento laterale come Vector2 nella base perpendicolare
        /// del POI: X = componente lungo _perpBasisX, Y = componente lungo _perpBasisY.
        /// Per costruzione LateralOffset.magnitude == LateralError. Serve alla UI del
        /// minigioco (3.1.5) per posizionare il cerchio dinamico sul canvas: la sola
        /// magnitudine (LateralError) non basta, serve la direzione nel piano perp.
        /// Server-authoritative.
        /// </summary>
        private readonly NetworkVariable<Vector2> _netLateralOffset =
            new(Vector2.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Distanza corrente lungo l'asse di approccio (Dot fromPoiToShip · approachAxis) in u.</summary>
        private readonly NetworkVariable<float> _netAxialDistance =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Distanza assiale al momento dell'ingresso Docking, congelata.
        /// La UI usa questo per calcolare la scala del cerchio (Convenzione B).</summary>
        private readonly NetworkVariable<float> _netInitialAxialDistance =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>true se lateralError e axialDistance sono entrambi entro tolleranza — attivo prompt confirm.</summary>
        private readonly NetworkVariable<bool> _netIsInAnchorTolerance =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>
        /// Fase 3.1.5 — DockingRadius del POI corrente (dal PoiData). Congelato
        /// all'ingresso Docking. Serve alla UI per mappare AxialDistance a
        /// scala del cerchio in modo che il rapporto "vicinanza al target vs
        /// distanza di partenza" sia sempre lo stesso: a axial = DockingRadius
        /// → cerchio minimo; a axial = FinalDockingDistance → cerchio massimo.
        /// Non usiamo InitialAxialDistance perché varia (dipende da dove il
        /// pilota preme T) — la UI diventerebbe imprevedibile.
        /// </summary>
        private readonly NetworkVariable<float> _netDockingRadiusReference =
            new(200f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>
        /// Blocco 3.2.b.3 — Magnitude corrente della velocità RCS della nave
        /// durante il Docking (|_strafeVelocity|), in u/s. Server-write ogni
        /// tick, replicata a tutti per il monitor pilota (DockingMinigameUI).
        ///
        /// Motivazione: durante il Docking PropulsionSystem.CurrentSpeed è
        /// sempre 0 by design (la traslazione è delegata al DockingController
        /// via strafe RCS). Il monitor UI non può leggere lì. Questa NetVar
        /// espone la velocità di volo effettiva durante il minigioco senza
        /// dover replicare l'intero vettore _strafeVelocity (server-only,
        /// invariante Rev W): la sola magnitude è sufficiente per il display
        /// numerico e per i futuri consumer teatrali (feedback audio scalato
        /// con la velocità, ecc.).
        /// </summary>
        private readonly NetworkVariable<float> _netCurrentRcsSpeed =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ── Runtime (server-only) ─────────────────────────────────────────────
        private Vector3 _strafeInput; // ricevuto via RPC, in convenzione (X=right, Y=up, Z=forward)
        private PoiInstance _currentPoi;

        // Frame di riferimento congelato all'ingresso Docking:
        private Vector3 _approachAxisWorld;
        private Vector3 _perpBasisX;
        private Vector3 _perpBasisY;

        // Auto-align rotazionale:
        private Quaternion _initialShipRotation;
        private Quaternion _targetShipRotation;
        private float _initialAxialDistanceCached;

        // Collision LATCH (freeze timer rimosso in Rev W — vedi header):
        private bool _hasFiredCollisionThisSession;

        // Rev W (D7) — velocità RCS integrata server-side. Newton puro:
        // nessun damping. Modificata da RunDockingTick, azzerata a
        // enter/exit Docking. NON replicata via NetworkVariable (4B) —
        // il movimento è visto dagli altri client attraverso la replica di
        // ship.LogicalPosition tramite ExternalWorldFollower.
        private Vector3 _strafeVelocity;

        // Monitoring transizioni di stato (nessun evento OnNavStateChanged su
        // PropulsionSystem — lo osserviamo internamente).
        private NavigationState _previousNavState = NavigationState.Anchored;

        // ── Evento pubblico ───────────────────────────────────────────────────

        /// <summary>
        /// Fire server-side quando si verifica un HardCollision durante Docking.
        /// Parametri: (impactVelocity in u/s, PoiInstance colpito).
        ///
        /// impactVelocity è la componente RADIALE reale della velocità
        /// server-side al momento del contatto (non stima dall'input) —
        /// invariante Rev X.
        ///
        /// La PoiInstance passata consente al consumer (ShipImpactHandler
        /// in Blocco 3.2.a) di accedere al PoiData del POI colpito senza
        /// duplicare la risoluzione da AnchoredPoiId. Semanticamente
        /// l'evento è self-contained: "chi ha colpito chi, con quale
        /// forza".
        /// </summary>
        public event Action<float, PoiInstance> OnHardCollision;

        // ── Accessors pubblici ────────────────────────────────────────────────
        public float LateralError => _netLateralError.Value;
        public Vector2 LateralOffset => _netLateralOffset.Value;
        public float AxialDistance => _netAxialDistance.Value;
        public float InitialAxialDistance => _netInitialAxialDistance.Value;
        public bool IsInAnchorTolerance => _netIsInAnchorTolerance.Value;

        // Tuning esposti per la UI del minigioco (3.1.5): la UI legge da qui
        // invece di duplicare i SerializeField in Inspector, evitando drift.
        public float FinalDockingDistance => finalDockingDistance;
        public float AxialDockingTolerance => axialDockingTolerance;
        public float LateralTolerance => lateralTolerance;
        public float MaxDockingLateralRange => maxDockingLateralRange;
        public float DockingRadiusReference => _netDockingRadiusReference.Value;

        /// <summary>
        /// Blocco 3.2.b.3 — Velocità RCS corrente della nave in Docking (u/s).
        /// Consumer: DockingMinigameUI (display sul monitor pilota) e potenziali
        /// futuri consumer teatrali. Vedi commento su _netCurrentRcsSpeed.
        /// </summary>
        public float CurrentRcsSpeed => _netCurrentRcsSpeed.Value;

        /// <summary>
        /// Soglia di velocità RCS (u/s) sotto la quale l'ancoraggio è
        /// confermabile. Esposta pubblicamente perché è ANCHE la soglia
        /// sotto la quale gli impatti non generano danno (Blocco 3.2.a) —
        /// invariante Rev X: un solo tuning globale gestisce sia "posso
        /// attraccare" che "quanto è troppo forte". Consumer: ShipImpactHandler.
        /// </summary>
        public float ConfirmMaxVelocity => confirmMaxVelocity;

        // ── Lifecycle NGO ─────────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            Instance = this;
            _previousNavState = NavigationState.Anchored;
            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // ── Update (server-only) ──────────────────────────────────────────────
        private void Update()
        {
            if (!IsServer) return;

            var propulsion = PropulsionSystem.Instance;
            if (propulsion == null) return;

            NavigationState currentState = propulsion.CurrentNavState;

            // Rileva transizioni di stato (enter/exit Docking)
            if (currentState != _previousNavState)
            {
                if (currentState == NavigationState.Docking
                    && _previousNavState != NavigationState.Docking)
                {
                    HandleEnterDocking();
                }
                else if (_previousNavState == NavigationState.Docking
                         && currentState != NavigationState.Docking)
                {
                    HandleExitDocking();
                }
                _previousNavState = currentState;
            }

            if (currentState == NavigationState.Docking)
            {
                RunDockingTick();
            }
        }

        // =========================================================================
        // ENTER / EXIT DOCKING
        // =========================================================================

        private void HandleEnterDocking()
        {
            var propulsion = PropulsionSystem.Instance;
            var movement = ShipMovement.Instance;
            if (propulsion == null || movement == null)
            {
                Debug.LogError("[DockingController] EnterDocking: sistemi ship non pronti.");
                return;
            }

            // Risolvi POI da AnchoredPoiId (settato da AnchorSystem prima della
            // transizione di stato).
            ulong poiId = propulsion.AnchoredPoiId;
            _currentPoi = ResolvePoi(poiId);
            if (_currentPoi == null)
            {
                Debug.LogError($"[DockingController] EnterDocking: POI con NetworkObjectId {poiId} non trovato.");
                return;
            }

            // Calcola approachAxis in world space (congelato per tutta la sessione
            // Docking — non riflette rotazioni successive del POI, che comunque
            // non dovrebbero accadere per POI passivi).
            //
            // Rev AB (Q6 = B): l'asse di approccio è ora derivato dal DockingAnchor
            // Transform sul prefab POI (via PoiInstance.DockingAnchorForwardWorld),
            // non più dal vecchio PoiData.DockingApproachDirectionLocal (rimosso in
            // Rev AB). Se il prefab non ha DockingAnchor configurato, PoiInstance
            // emette warning una volta e ritorna il fallback pre-Rev AB
            // (LogicalRotation × Vector3.up). Normalizzazione difensiva.
            Vector3 anchorFwd = _currentPoi.DockingAnchorForwardWorld;
            float anchorFwdMag = anchorFwd.magnitude;
            _approachAxisWorld = anchorFwdMag > 1e-4f
                ? anchorFwd / anchorFwdMag
                : (_currentPoi.LogicalRotation * Vector3.up).normalized;

            // Base perpendicolare all'asse di approccio, derivata dal frame POI.
            // Convenzione canonica: usa world-up come helper; se troppo parallelo,
            // fallback su world-forward. Q_frame_XY confermato: base derivata dal
            // POI, indipendente dalla nave.
            Vector3 helper = Mathf.Abs(Vector3.Dot(_approachAxisWorld, Vector3.up)) > 0.99f
                ? Vector3.forward
                : Vector3.up;
            _perpBasisX = Vector3.Cross(_approachAxisWorld, helper).normalized;
            _perpBasisY = Vector3.Cross(_perpBasisX, _approachAxisWorld).normalized;

            // Fase 3.1.5 — auto-align RIMOSSO (Opzione 3). La rotation della
            // nave resta quella d'entrata Docking per tutta la sessione.
            // _initialShipRotation è conservata per debug/diagnostica ma
            // _targetShipRotation non è più calcolata né usata.
            _initialShipRotation = movement.LogicalRotation;
            _targetShipRotation = _initialShipRotation;

            // Calcola geometria iniziale
            Vector3 fromPoiToShip = movement.LogicalPosition - _currentPoi.LogicalPosition;
            _initialAxialDistanceCached = Vector3.Dot(fromPoiToShip, _approachAxisWorld);

            // Guardia numerica: se initialAxial ≈ finalDockingDistance (pilota
            // entra già praticamente in posizione), evitiamo divisione per zero
            // nel calcolo di t. Un buffer minimo di 1u basta.
            if (Mathf.Abs(_initialAxialDistanceCached - finalDockingDistance) < 1f)
            {
                _initialAxialDistanceCached = finalDockingDistance + 1f;
            }

            _netInitialAxialDistance.Value = _initialAxialDistanceCached;
            _netDockingRadiusReference.Value = _currentPoi.Data.DockingRadius;

            // Reset flags collisione + strafe input + velocity (Rev W)
            _hasFiredCollisionThisSession = false;
            _strafeInput = Vector3.zero;
            _strafeVelocity = Vector3.zero;

            Debug.Log($"[DockingController] EnterDocking → POI {_currentPoi.Data.DisplayName}, " +
                      $"axial iniziale {_initialAxialDistanceCached:F1}u, " +
                      $"approachAxis {_approachAxisWorld}");
        }

        private void HandleExitDocking()
        {
            Debug.Log("[DockingController] ExitDocking — cleanup");
            _currentPoi = null;
            _strafeInput = Vector3.zero;
            _strafeVelocity = Vector3.zero;
            _hasFiredCollisionThisSession = false;
            _netLateralError.Value = 0f;
            _netLateralOffset.Value = Vector2.zero;
            _netAxialDistance.Value = 0f;
            _netInitialAxialDistance.Value = 0f;
            _netIsInAnchorTolerance.Value = false;
            _netCurrentRcsSpeed.Value = 0f;
        }

        // =========================================================================
        // DOCKING TICK — server-only, ogni frame durante Docking
        // =========================================================================

        private void RunDockingTick()
        {
            var movement = ShipMovement.Instance;
            if (movement == null || _currentPoi == null) return;

            float dt = Time.deltaTime;

            // 1. INTEGRAZIONE INERZIALE (Rev W — D7)
            //    input → accelerazione → velocità → posizione candidata.
            //    Con stabilizzazione RCS per-asse (2C β): gli assi non-comandati
            //    vengono decelerati linearmente. Cap magnitude a maxRcsVelocity.
            //    Convenzione input.z > 0 = avvicinati al POI = accelerazione lungo
            //    (-_approachAxisWorld) perché l'asse "esce" dal POI verso il
            //    lato di attracco.
            Vector3 acceleration =
                  _perpBasisX * (_strafeInput.x * rcsThrustPower)
                + _perpBasisY * (_strafeInput.y * rcsThrustPower)
                + (-_approachAxisWorld) * (_strafeInput.z * rcsThrustPower);

            _strafeVelocity += acceleration * dt;

            // Stabilizzazione RCS per-asse (2C β, Rev W hotfix)
            // Per ciascuno dei tre assi del frame POI, se |input| < deadzone
            // la componente della velocità su quell'asse viene ridotta di
            // stabilizingThrustPower * dt (senza overshoot: si ferma esattamente
            // a zero). La base (perpBasisX, perpBasisY, -approachAxisWorld) è
            // ortonormale, quindi la proiezione/ricomposizione preserva
            // esattamente _strafeVelocity.
            if (stabilizingThrustPower > 0f)
            {
                // "Asse Z positivo" del frame POI = direzione di avvicinamento
                // al POI, coerente con la convenzione input.z > 0 = avanti.
                Vector3 axisZ = -_approachAxisWorld;

                float vX = Vector3.Dot(_strafeVelocity, _perpBasisX);
                float vY = Vector3.Dot(_strafeVelocity, _perpBasisY);
                float vZ = Vector3.Dot(_strafeVelocity, axisZ);

                float decelStep = stabilizingThrustPower * dt;

                if (Mathf.Abs(_strafeInput.x) < inputDeadZone)
                    vX = DecelToZero(vX, decelStep);
                if (Mathf.Abs(_strafeInput.y) < inputDeadZone)
                    vY = DecelToZero(vY, decelStep);
                if (Mathf.Abs(_strafeInput.z) < inputDeadZone)
                    vZ = DecelToZero(vZ, decelStep);

                _strafeVelocity = _perpBasisX * vX + _perpBasisY * vY + axisZ * vZ;
            }

            // Cap velocità (3A)
            float vMag = _strafeVelocity.magnitude;
            if (vMag > maxRcsVelocity)
            {
                _strafeVelocity = _strafeVelocity * (maxRcsVelocity / vMag);
            }

            // 2. POSIZIONE CANDIDATA + CLAMP POSIZIONALE HARD (Rev W — D8, 5D+5B;
            //    Rev AB — Blocco 3.2.d D5, migrato a compound collider)
            //    Tentativo di posizione. Se qualche coppia (volumeNave, volumePOI)
            //    compenetra, la posizione è clampata sull'asse di push-out della
            //    coppia con depth massima; la componente radiale della velocità
            //    (verso il POI) è azzerata; la tangenziale è preservata (slide).
            //
            //    Rev AB (Q4=C): PoiCollisionMath.ClampAgainstPoi (sfera-sfera,
            //    Rev AA) è stato sostituito da CompoundColliderMath.ClampAgainstCompound.
            //    Il math helper opera sulle liste di volumi del compound.
            //
            //    INVARIANTE SEMANTICO "attracco guidato" (Rev AA Opzione A):
            //    passiamo shipVolumes = NULL. Semanticamente: la nave è trattata
            //    come PUNTO (LogicalPosition) contro i volumi del POI. Equivalente
            //    esatto al Rev AA "shipRadius=0f" — con compound multi-volume,
            //    passare i volumi della nave farebbe fermare l'attracco lontano
            //    dal bordo POI (ali/motori nave impatterebbero prima del punto
            //    di attracco sull'anchor). Il Docking è guidato lungo un asse
            //    dedicato (approachAxis = DockingAnchor forward); la nave deve
            //    poter raggiungere il DockingAnchor per completare l'ancoraggio.
            //
            //    ShipVolumes VIENE INVECE usato dal PoiCollisionResolver in
            //    Manual/Coasting/Autopilot, dove la nave può impattare da
            //    qualunque direzione non allineata e la geometria della nave
            //    deve essere rispettata.
            //
            //    fallbackNormal = _approachAxisWorld (coerente col contesto
            //    Docking: se il math helper non riesce a determinare una normal
            //    valida, uso l'asse di attracco come guess sensata).
            Vector3 currentPos = movement.LogicalPosition;
            Vector3 candidatePos = currentPos + _strafeVelocity * dt;

            var clamp = CompoundColliderMath.ClampAgainstCompound(
                currentPosA: currentPos,
                candidatePosA: candidatePos,
                rotationA: movement.LogicalRotation,
                volumesA: null, // invariante Docking: nave = punto
                worldPosB: _currentPoi.LogicalPosition,
                worldRotB: _currentPoi.LogicalRotation,
                volumesB: _currentPoi.CollisionVolumes,
                velocity: _strafeVelocity,
                useHardClamp: useHardPositionClamp,
                fallbackNormal: _approachAxisWorld);

            candidatePos = clamp.ClampedPosition;
            _strafeVelocity = clamp.ClampedVelocity;

            if (clamp.HadCollision && !_hasFiredCollisionThisSession)
            {
                _hasFiredCollisionThisSession = true;
                OnHardCollision?.Invoke(clamp.RadialImpactSpeed, _currentPoi);

                Debug.LogWarning($"[DockingController] HARD COLLISION! " +
                                 $"radial impact={clamp.RadialImpactSpeed:F2}u/s. " +
                                 $"Componente radiale velocity azzerata; " +
                                 $"tangenziale preservata (slide).");
            }

            // Applica posizione (clampata o meno)
            movement.SetLogicalPosition(candidatePos);

            // Blocco 3.2.b.3 — Replica magnitude _strafeVelocity per il monitor
            // pilota (DockingMinigameUI). Scrittura in questo punto è
            // intenzionale: dopo il clamp posizionale, che può aver azzerato
            // la componente radiale della velocità su collisione. Il valore
            // riflette quindi la velocità EFFETTIVA usata per aggiornare la
            // posizione (post-clamp), non quella pre-clamp.
            _netCurrentRcsSpeed.Value = _strafeVelocity.magnitude;

            // 3. Ricalcola geometria dopo lo strafe (usa la posizione applicata)
            Vector3 fromPoiToShip = candidatePos - _currentPoi.LogicalPosition;
            float axial = Vector3.Dot(fromPoiToShip, _approachAxisWorld);
            Vector3 axialComp = _approachAxisWorld * axial;
            Vector3 lateralVec = fromPoiToShip - axialComp;
            float lateralErr = lateralVec.magnitude;
            float distanceToPoi = fromPoiToShip.magnitude;

            // 4. Auto-align rotazione — RIMOSSO in Fase 3.1.5 (Opzione 3).
            //    La nave conserva la rotation d'entrata durante tutto il Docking.
            //    Motivazioni:
            //    - Rimuove lo snap iniziale (t alto al primo tick produceva
            //      salto brusco della rotation)
            //    - Rimuove il "salto al ritorno in Manual" (causato da
            //      ShipMovement.UpdateOrientation che forzava Quaternion.Euler
            //      con pitch clamp + roll zero, rifiutando rotation "capovolte"
            //      prodotte dall'auto-align)
            //    - Coerente con "docking cilindro/cilindro" (nessuna convenzione
            //      azimutale) e con la rotation del POI ora deterministica dal
            //      prefab (PoiSpawner Fase 3.1.5): il modeler orienta il POI
            //      così che il lato di attracco sia visivamente evidente
            //    - Il pilota è responsabile di essere in rotation ragionevole
            //      quando preme T (rotation d'entrata = rotation durante tutto
            //      il Docking = rotation al ritorno in Manual)
            //    La base perpendicolare X/Y del strafe resta derivata dal frame
            //    POI (Q_frame_XY), quindi il minigioco funziona correttamente
            //    indipendentemente dall'orientamento della nave.

            // 5. RILASCIO LATCH con isteresi (Rev W — freeze thrusters rimosso,
            //    ma il latch resta per prevenire spam di OnHardCollision).
            //    La detection della collisione è ora fusa nel blocco 2 (clamp
            //    posizionale): al primo tick di contatto viene emesso
            //    OnHardCollision. Qui armiamo il rilascio.
            //
            //    Rev AB (Q5=B): la soglia di isteresi usa ora
            //    Data.ApproximateRadius (raggio approssimato per usi
            //    non-collisionali strict, ex-HardCollisionRadius). Il compound
            //    multi-volume non ha un raggio unico; ApproximateRadius è la
            //    sfera che meglio approssima l'ingombro. Non è geometricamente
            //    esatto (volumi periferici possono sporgere oltre), ma per
            //    anti-spam è sufficiente e stabile. Coerente con la scelta
            //    identica nel PoiCollisionResolver Rev AB.
            float releaseRadius = _currentPoi.Data.ApproximateRadius;
            if (_hasFiredCollisionThisSession
                && distanceToPoi > releaseRadius * collisionReleaseHysteresis)
            {
                _hasFiredCollisionThisSession = false;
            }

            // 6. Detection out-of-range → uscita forzata
            bool outOfRange = lateralErr > maxDockingLateralRange
                           || Mathf.Abs(axial) > maxDockingAxialRange;
            if (outOfRange)
            {
                Debug.LogWarning($"[DockingController] Out-of-range — undock forzato. " +
                                 $"lateral={lateralErr:F1}u (max {maxDockingLateralRange:F1}), " +
                                 $"axial={axial:F1}u (max ±{maxDockingAxialRange:F1}).");
                // AnchorSystem.RequestUndock è l'API canonica che azzera
                // AnchoredPoiId, riporta ScanState del POI a Scanned, e
                // transiziona a Coasting.
                if (AnchorSystem.Instance != null)
                    AnchorSystem.Instance.RequestUndock();
                else
                    PropulsionSystem.Instance?.RequestNavigationState(NavigationState.Coasting);
                return;
            }

            // 7. Update NetVar per la UI
            //    Tolerance ora include check velocità (Rev W — 6C):
            //    la nave è "in tolleranza" per confermare l'attracco solo se
            //    posizionalmente ok E velocità RCS sotto soglia. Con Newton
            //    puro (2B) senza questo check si potrebbe confermare Docked
            //    mentre si sta scivolando tangenzialmente sulla superficie.
            bool posInTol = lateralErr <= lateralTolerance
                         && Mathf.Abs(axial - finalDockingDistance) <= axialDockingTolerance;
            bool velInTol = _strafeVelocity.magnitude <= confirmMaxVelocity;
            bool inTol = posInTol && velInTol;

            // Proietto il vettore laterale sulla base perpendicolare per esporre
            // il Vector2 alla UI (3.1.5). LateralOffset.magnitude == LateralError
            // per costruzione — la UI usa X/Y per posizionare il cerchio.
            Vector2 lateralOffset = new Vector2(
                Vector3.Dot(lateralVec, _perpBasisX),
                Vector3.Dot(lateralVec, _perpBasisY)
            );

            if (!Mathf.Approximately(_netLateralError.Value, lateralErr))
                _netLateralError.Value = lateralErr;
            if ((_netLateralOffset.Value - lateralOffset).sqrMagnitude > 1e-4f)
                _netLateralOffset.Value = lateralOffset;
            if (!Mathf.Approximately(_netAxialDistance.Value, axial))
                _netAxialDistance.Value = axial;
            if (_netIsInAnchorTolerance.Value != inTol)
                _netIsInAnchorTolerance.Value = inTol;
        }

        // =========================================================================
        // API PUBBLICA
        // =========================================================================

        /// <summary>
        /// Chiamato dal PilotStation (3.1.4), una volta per frame durante Docking.
        /// input.x = strafe destra/sinistra nel piano perp del POI
        /// input.y = strafe su/giù nel piano perp del POI
        /// input.z = avanti (verso POI) / indietro (allontanamento)
        /// Ogni componente attesa in [-1, +1].
        /// </summary>
        public void SetStrafeInput(Vector3 input)
        {
            Vector3 clamped = new Vector3(
                Mathf.Clamp(input.x, -1f, 1f),
                Mathf.Clamp(input.y, -1f, 1f),
                Mathf.Clamp(input.z, -1f, 1f));

            if (IsServer) _strafeInput = clamped;
            else SetStrafeInputRpc(clamped);
        }

        [Rpc(SendTo.Server)]
        private void SetStrafeInputRpc(Vector3 input) => _strafeInput = input;

        /// <summary>
        /// Chiamato dal PilotStation (3.1.4) quando il pilota preme "Confirm
        /// Anchor" nel minigioco. Se in tolleranza, transiziona Docking →
        /// Docked e setta PoiInstance.ScanState = Anchored.
        /// </summary>
        public void RequestConfirmAnchor()
        {
            if (IsServer) RequestConfirmAnchorInternal();
            else RequestConfirmAnchorRpc();
        }

        [Rpc(SendTo.Server)]
        private void RequestConfirmAnchorRpc() => RequestConfirmAnchorInternal();

        private void RequestConfirmAnchorInternal()
        {
            var propulsion = PropulsionSystem.Instance;
            if (propulsion == null) return;

            if (propulsion.CurrentNavState != NavigationState.Docking)
            {
                Debug.LogWarning($"[DockingController] ConfirmAnchor rifiutato — " +
                                 $"stato attuale {propulsion.CurrentNavState} (atteso Docking).");
                return;
            }

            if (!_netIsInAnchorTolerance.Value)
            {
                Debug.LogWarning("[DockingController] ConfirmAnchor rifiutato — " +
                                 "non in tolleranza.");
                return;
            }

            if (_currentPoi != null)
            {
                _currentPoi.SetScanState(PoiScanState.Anchored);
            }

            propulsion.RequestNavigationState(NavigationState.Docked);
            Debug.Log($"[DockingController] ANCHOR CONFERMATO → DOCKED su POI " +
                      $"{(_currentPoi != null ? _currentPoi.Data.DisplayName : "?")}");
        }

        // =========================================================================
        // HELPER
        // =========================================================================

        /// <summary>
        /// Decelera un valore scalare verso zero di step unità, senza
        /// overshoot. Se |v| &lt;= step ritorna 0. Usato dalla stabilizzazione
        /// RCS per-asse (2C β) per portare esattamente a zero la componente
        /// di velocità su un asse non-comandato, evitando oscillazioni
        /// numeriche attorno allo zero.
        /// </summary>
        private static float DecelToZero(float v, float step)
        {
            if (v > step) return v - step;
            if (v < -step) return v + step;
            return 0f;
        }

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
            GUILayout.BeginArea(new Rect(Screen.width - 260, Screen.height - 200, 250, 190));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[Docking] {(IsServer ? "SRV" : "CLT")}");
            string poiLabel = _currentPoi != null && _currentPoi.Data != null
                ? _currentPoi.Data.DisplayName : "-";
            GUILayout.Label($"POI:      {poiLabel}");
            GUILayout.Label($"Axial:    {_netAxialDistance.Value:F1}u " +
                            $"(init {_netInitialAxialDistance.Value:F1})");
            GUILayout.Label($"Target:   {finalDockingDistance:F1} ±{axialDockingTolerance:F1}");
            GUILayout.Label($"Lateral:  {_netLateralError.Value:F1}u " +
                            $"(tol {lateralTolerance:F1})");
            GUILayout.Label($"Velocity: {_strafeVelocity.magnitude:F2}u/s " +
                            $"(max {maxRcsVelocity:F1}, confirm ≤{confirmMaxVelocity:F1})");
            GUILayout.Label($"InTol:    {_netIsInAnchorTolerance.Value}");
            GUILayout.Label($"Latch:    {_hasFiredCollisionThisSession}");
            GUILayout.Label($"Strafe:   {_strafeInput}");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}