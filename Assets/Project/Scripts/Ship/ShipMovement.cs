using System;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Collision;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ShipMovement — Milestone 3, Blocco 2 + Blocco 3 fase 2 (Rev T),
    /// esteso Fase 3 Blocco 3.1 (Sotto-step 3.1.3) con setter server-only
    /// per DockingController (strafe RCS + auto-align rotazionale),
    /// esteso Rev AB (Blocco 3.2.d D5 — Compound Collider),
    /// esteso Rev AH (Blocco AH.1 — 6DoF QD-γ + look-to-steer coupling).
    ///
    /// DECISIONE ARCHITETTURALE (invariata da Rev Q): "Nave" NON si muove mai
    /// fisicamente nel mondo. Resta esattamente dov'è piazzata in Editor, per
    /// sempre. Questo script traccia SOLO lo stato LOGICO del movimento —
    /// nessuna Transform di "Nave" viene mai spostata o ruotata da qui.
    ///
    /// ESTENSIONE Rev T (verso Blocco 3 fase 2):
    ///   - Orientamento logico esteso da SCALARE (yaw) a QUATERNION completo
    ///     (yaw + pitch, no roll).
    ///   - Aggiunta LogicalPosition (Vector3 NetworkVariable) accumulata
    ///     server-side.
    ///
    /// ESTENSIONE Rev T post-playtest — sensazione "nave pesante":
    ///   - INERZIA ROTAZIONALE: yaw e pitch hanno un rate corrente che insegue
    ///     il target con accelerazione angolare finita.
    ///   - NO STEERING A VELOCITÀ ZERO: sotto minSpeedToSteer (default 3 m/s)
    ///     l'orientamento è bloccato. → RIMOSSO IN REV AH (MS-2), vedi sotto.
    ///
    /// ESTENSIONE Blocco 3.2.c — hook di collisione POI in UpdatePosition:
    ///   In Manual/Coasting/Autopilot la nave attraversava i POI come fantasmi
    ///   (invariante Rev X: clamp posizionale hard solo dentro il Docking).
    ///   Ora UpdatePosition calcola una candidatePos, invoca
    ///   PoiCollisionResolver.Instance.ResolveCollision(...) che (se in stato
    ///   Manual/Coasting/Autopilot) applica clamp+slide contro il POI più
    ///   critico, e ritorna posizione+scalare velocità post-clamp.
    ///
    /// ── MODIFICHE REV AB (Blocco 3.2.d D5) ──────────────────────────────────
    ///
    ///   RIMOSSO: shipCollisionRadius (SerializeField) + property
    ///   ShipCollisionRadius. Sostituito da compound collider (OBB+Sphere) via
    ///   CompoundColliderAuthoring. Consumer di Compound: PoiCollisionResolver
    ///   e DockingController.
    ///
    /// ── MODIFICHE REV AH (Blocco AH.1 — QD-γ) ───────────────────────────────
    ///
    ///   QD-γ (6DoF completo con roll): estensione della rotazione logica da
    ///   yaw+pitch (Euler-based) a full 6DoF via Quaternion incrementale locale.
    ///
    ///   CAMBIAMENTI:
    ///
    ///   1. FIRMA SetManualLookInput: Vector2 → Vector3.
    ///        .x = look horizontal (yaw input), da -1 a +1
    ///        .y = look vertical   (pitch input), da -1 a +1
    ///        .z = roll input, da -1 (A) a +1 (D)
    ///      La componente z è NUOVA (Rev AH). Chiamanti: solo PilotStation
    ///      (aggiornato in AH.1).
    ///
    ///   2. STATO server-only esteso: aggiunto _currentRollRate accanto ai
    ///      due esistenti (yaw, pitch).
    ///
    ///   3. ROTATION MATH: da
    ///        _logicalRotation.Value = Quaternion.Euler(pitch, yaw, 0)
    ///      a
    ///        Quaternion delta = Quaternion.Euler(dPitch, dYaw, dRoll)
    ///        _logicalRotation.Value = _logicalRotation.Value * delta  // local
    ///
    ///      Il moltiplicare a destra applica delta in LOCAL SPACE della nave:
    ///      yaw = rotazione attorno all'up locale, pitch = attorno al right
    ///      locale, roll = attorno al forward locale. Feel spaziale corretto
    ///      (Elite Dangerous style) — "ruota la nave come se fossi tu dentro".
    ///
    ///   4. GIMBAL LOCK: eliminato. Il clamp Euler pitch ±80° è RIMOSSO
    ///      (variabile pitchClampDegrees eliminata). Nessun singularità Euler
    ///      perché non usiamo più Euler come rappresentazione di stato — solo
    ///      come delta locale per il moltiplicatore, e piccoli delta (dt ×
    ///      rate) non hanno singolarità.
    ///
    ///   5. MS-2 (rimozione minSpeedToSteer): la restrizione "no steering a
    ///      velocità < 3 m/s" (Rev T post-playtest) è RIMOSSA. Motivazione
    ///      Rev AH: nello spazio senza atmosfera la nave dispone di RCS/
    ///      thruster laterali che permettono riorientamento anche da ferma.
    ///      Coerente con QD-γ 6DoF (Elite Dangerous, Freelancer, ecc.).
    ///      canSteer ora dipende solo dallo stato Manual, non dalla velocità.
    ///
    ///      NOTA MIGRAZIONE: PilotFlightHUD ha un campo mirror
    ///      minSpeedToSteerMirror usato per il warning "avvia motori per
    ///      sterzare". Con MS-2 il warning non ha più senso ma il campo
    ///      continuerà a triggerarsi sotto 3 m/s. Non dannoso — PilotFlightHUD
    ///      sarà ELIMINATO in AH.4 (remigrazione UI a Screen Space Overlay
    ///      unificato). Fino ad AH.4 il warning è cosmeticamente presente
    ///      ma disallineato dalla realtà runtime.
    ///
    ///   6. ROLL SEMANTICA (Roll-γ + Roll-hold + Roll-inertia-2):
    ///        - Roll-γ: direct steering nave, la camera del player NON rolla
    ///          (motion sickness prevention).
    ///        - Roll-hold: l'assetto roll accumulato NON torna a zero al
    ///          rilascio (nessuna gravità simulata che raddrizzi la nave).
    ///          Coerente con yaw/pitch che sono già hold-behavior.
    ///        - Roll-inertia-2: il rate di roll decelera con inerzia (nuovo
    ///          rollAcceleration in PropulsionUpgradeData, default 45°/s²),
    ///          simmetrico a yaw/pitch.
    ///
    ///   7. RECENTER CAMERA (RC1-b + RC2-c + RC3-c): NON gestito qui.
    ///      Delegato a LookToSteerController (nuovo, AH.3) che intercetta
    ///      l'input mouse, applica cono ±20°/±15° alla camera del player,
    ///      auto-recentra sotto deadzone, e passa l'ECCEDENZA fuori cono a
    ///      SetManualLookInput. Rev AH.1 lavora sulla layer "rotazione logica"
    ///      trasparente al look-to-steer.
    ///
    /// DESIGN — controllo pilotaggio Rev AH:
    ///   - Assi rotazione: yaw + pitch + ROLL (6DoF completo QD-γ)
    ///   - Convenzione mouse: FPS standard (mouse su = muso su)
    ///   - Convenzione roll: A/D con D positivo (nave rolla verso destra
    ///     dal PoV pilota, orizzonte sale a sinistra) — Elite Dangerous style.
    ///     Se in playtest il feel risulta invertito, cambiare segno in PilotStation
    ///     oppure invertire il binding A/D nel InputActions asset.
    ///   - Nessun clamp pitch (6DoF completo, ogni assetto raggiungibile).
    ///
    /// DIPENDE DA: PropulsionSystem (YawAcceleration, PitchAcceleration,
    ///   RollAcceleration [Rev AH], CurrentSpeed, CurrentNavState),
    ///   CompoundColliderAuthoring (Rev AB).
    /// USATO DA:   ExternalWorldFollower, PilotStation, DockingController,
    ///             PoiCollisionResolver, LookToSteerController (Rev AH.3).
    /// </summary>
    public class ShipMovement : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static ShipMovement Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── Steering manuale (logico) ────────────────────────────────────────
        [Header("Steering Manuale (logico — non muove 'Nave')")]
        [Tooltip("Rate MASSIMO di yaw in gradi/secondo, a input X massimo (±1). " +
                 "Il rate corrente insegue questo target con inerzia " +
                 "(data.yawAcceleration).")]
        [SerializeField] private float manualYawSpeedDegPerSec = 90f;

        [Tooltip("Rate MASSIMO di pitch in gradi/secondo, a input Y massimo (±1). " +
                 "Convenzione FPS: mouse su = muso su.")]
        [SerializeField] private float manualPitchSpeedDegPerSec = 60f;

        [Tooltip("Rev AH (QD-γ) — Rate MASSIMO di roll in gradi/secondo, a input Z " +
                 "massimo (±1). Default 90°/s (roll è più vivace di pitch — standard " +
                 "genre sim spaziali). Il rate corrente insegue questo target con " +
                 "inerzia (data.rollAcceleration, Rev AH).")]
        [SerializeField] private float manualRollSpeedDegPerSec = 90f;

        // ── Stato di rete ─────────────────────────────────────────────────────
        private readonly NetworkVariable<Quaternion> _logicalRotation = new NetworkVariable<Quaternion>(
            Quaternion.identity,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<Vector3> _logicalPosition = new NetworkVariable<Vector3>(
            Vector3.zero,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        // Stato server-only (non replicato).
        // Rev AH: _manualLookInput esteso a Vector3 (.z = roll input), aggiunto
        // _currentRollRate simmetrico a _currentYawRate / _currentPitchRate.
        private Vector3 _manualLookInput;
        private float _currentYawRate;
        private float _currentPitchRate;
        private float _currentRollRate;

        // ── Cache compound collider (Rev AB) ─────────────────────────────────
        [Header("Collisione compound (Rev AB — Blocco 3.2.d D5)")]
        [Tooltip("Riferimento al CompoundColliderAuthoring che descrive la " +
                 "geometria di collisione della Nave. Trascinare qui il " +
                 "GameObject Nave (quello con la mesh e il componente " +
                 "CompoundColliderAuthoring). ShipMovement è tipicamente su " +
                 "un GameObject sistemistico fratello di Nave, quindi " +
                 "GetComponent non funziona — serve riferimento esplicito.\n\n" +
                 "Se lasciato vuoto, Awake tenta un fallback via " +
                 "FindAnyObjectByType&lt;CompoundColliderAuthoring&gt;() — " +
                 "funziona se c'è una sola Nave in scena, ma genera LogError " +
                 "se non trova niente. Il drag&amp;drop esplicito è " +
                 "preferibile perché deterministico e più veloce.")]
        [SerializeField] private CompoundColliderAuthoring shipCompound;

        [Header("Debug")]
        [Tooltip("Log diagnostico VERBOSO — heartbeat throttled (1/sec) di " +
                 "UpdatePosition. Attivare solo per indagare mancate " +
                 "invocazioni del resolver o valori inattesi di CurrentSpeed. " +
                 "Off in gameplay normale — introduce rumore in console.")]
        [SerializeField] private bool logVerbose = false;
        [Tooltip("Overlay OnGUI di diagnostica 6DoF (solo Editor/Development Build). Standard Rev BA — default off.")]
        [SerializeField] private bool showDebugUI = false;

        private CompoundColliderAuthoring _compound;
        private bool _hasWarnedMissingCompound;

        /// <summary>
        /// Rev AB — frame counter per throttle del log diagnostico da
        /// UpdatePosition. Emesso solo se logVerbose == true.
        /// </summary>
        private int _debugUpdatePosCounter;

        // ── Proprietà pubbliche ───────────────────────────────────────────────
        public Quaternion LogicalRotation => _logicalRotation.Value;
        public Vector3 LogicalPosition => _logicalPosition.Value;
        public Vector3 LogicalForward => _logicalRotation.Value * Vector3.forward;

        /// <summary>
        /// Rev AI — Asse Y locale della nave espresso in world logic space.
        /// Usato da AnchorSystem per il check di allineamento Y-con-Y con
        /// l'asse Y del POI durante la valutazione docking (portellone di
        /// attracco montato su asse Y della nave). Analogo a LogicalForward
        /// ma per l'asse up.
        ///
        /// Nota architetturale (Rev Q): questa property NON legge
        /// transform.up della Nave (che è fissa in world space per invariante
        /// Rev Q), ma deriva da LogicalRotation. La Nave non si muove mai
        /// fisicamente, il suo "up logico" cambia solo tramite rotation
        /// applicata via SetManualLookInput (yaw/pitch/roll).
        /// </summary>
        public Vector3 LogicalUp => _logicalRotation.Value * Vector3.up;

        public float CurrentSpeed =>
            PropulsionSystem.Instance != null ? PropulsionSystem.Instance.CurrentSpeed : 0f;

        public NavigationState CurrentNavState =>
            PropulsionSystem.Instance != null ? PropulsionSystem.Instance.CurrentNavState : NavigationState.Anchored;

        /// <summary>
        /// Rev AB — Compound collider della nave. Cachato in Awake. Può essere
        /// null se il GameObject non ha CompoundColliderAuthoring: in quel caso
        /// il warning è emesso una sola volta e i consumer degradano al
        /// fallback "nave = punto". Post-Rev AD (F-C, D12 chiuso) questa
        /// modalità NON è più un caso di gameplay ma solo un guard per setup
        /// incompleti: la geometria point-vs-OBB ha una singolarità nota
        /// (vedi CompoundColliderMath aIsPoint). Configurare sempre il compound
        /// aggiungendo CompoundColliderAuthoring al GameObject Nave.
        /// </summary>
        public CompoundColliderAuthoring Compound => _compound;

        // =========================================================================
        // LIFECYCLE
        // =========================================================================

        private void Awake()
        {
            // Rev AB — cache del compound. ShipMovement è su un GameObject
            // sistemistico separato dal GameObject Nave (dove sta il
            // CompoundColliderAuthoring), quindi GetComponent non funziona.
            //
            // Priorità:
            //   1. Riferimento esplicito serializzato (drag&drop in Inspector).
            //   2. Fallback via FindAnyObjectByType — funziona se c'è UN solo
            //      compound in scena. Se ne trova più di uno, prende il primo
            //      (imprevedibile — evitare configurando esplicitamente).
            //
            // Se anche il fallback ritorna null, OnNetworkSpawn stamperà
            // LogError persistente (impossibile che scorra via nei log).
            _compound = shipCompound;
            if (_compound == null)
            {
                _compound = FindAnyObjectByType<CompoundColliderAuthoring>();
            }
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            OnInstanceReady?.Invoke();

            if (_compound == null && !_hasWarnedMissingCompound)
            {
                _hasWarnedMissingCompound = true;
                Debug.LogError("[ShipMovement] CompoundColliderAuthoring NON TROVATO. " +
                               "La nave sarà trattata come PUNTO nelle collisioni contro " +
                               "i POI — nella pratica il resolver non fermerà mai la nave " +
                               "(un punto contro OBB non scatta finché non è esattamente " +
                               "dentro il volume). Fix: assegnare il campo 'Ship Compound' " +
                               "nell'Inspector di ShipMovement, trascinandoci il GameObject " +
                               "Nave che ha il componente CompoundColliderAuthoring, " +
                               "OPPURE verificare che esista in scena UN solo " +
                               "CompoundColliderAuthoring (il fallback " +
                               "FindAnyObjectByType lo prenderà automaticamente).");
            }
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // =========================================================================
        // AGGIORNAMENTO STATO LOGICO (solo server)
        // =========================================================================

        private void FixedUpdate()
        {
            if (!IsServer) return;

            UpdateOrientation();
            UpdatePosition();
        }

        /// <summary>
        /// Server-only. Rev AH (QD-γ): rotazione 6DoF via Quaternion incrementale
        /// locale. Se in MANUAL, insegue i rate target (yaw+pitch+roll) con
        /// accelerazione angolare, poi compone il delta locale sul quaternion
        /// corrente.
        ///
        /// MS-2 (Rev AH): rimossa la restrizione minSpeedToSteer. Rotation
        /// disponibile in Manual indipendentemente dalla velocità (coerente con
        /// 6DoF spaziale: la nave ha RCS per riorientarsi da ferma).
        ///
        /// Local space multiply: LogRot * delta applica delta come rotazione
        /// egocentrica (attorno agli assi locali della nave). Feel corretto per
        /// sim spaziale — "il pilota muove la nave dal suo punto di vista".
        /// </summary>
        private void UpdateOrientation()
        {
            var propulsion = PropulsionSystem.Instance;

            // MS-2 (Rev AH): canSteer non dipende più dalla velocità.
            // Vecchio: canSteer = Manual && CurrentSpeed >= minSpeedToSteer
            // Nuovo:   canSteer = Manual
            bool canSteer = CurrentNavState == NavigationState.Manual;

            float dt = Time.fixedDeltaTime;

            float targetYawRate = canSteer ? _manualLookInput.x * manualYawSpeedDegPerSec : 0f;
            float targetPitchRate = canSteer ? -_manualLookInput.y * manualPitchSpeedDegPerSec : 0f;
            float targetRollRate = canSteer ? _manualLookInput.z * manualRollSpeedDegPerSec : 0f;

            float yawAccel, pitchAccel, rollAccel;
            if (propulsion != null && propulsion.YawAcceleration > 0f)
            {
                yawAccel = propulsion.YawAcceleration;
                pitchAccel = propulsion.PitchAcceleration;
                rollAccel = propulsion.RollAcceleration;
            }
            else
            {
                // Fallback quando PropulsionData non è configurata (edge case
                // di boot / test senza SO). Default coerenti con PropulsionUpgradeData.
                yawAccel = 60f;
                pitchAccel = 45f;
                rollAccel = 45f;
            }

            _currentYawRate = MoveToward(_currentYawRate, targetYawRate, yawAccel * dt);
            _currentPitchRate = MoveToward(_currentPitchRate, targetPitchRate, pitchAccel * dt);
            _currentRollRate = MoveToward(_currentRollRate, targetRollRate, rollAccel * dt);

            // Early exit: nessun rate significativo su nessun asse.
            if (Mathf.Abs(_currentYawRate) < 0.01f
             && Mathf.Abs(_currentPitchRate) < 0.01f
             && Mathf.Abs(_currentRollRate) < 0.01f)
                return;

            // Rev AH (QD-γ) — Quaternion incrementale locale:
            //   delta rappresenta rotazione (pitchDelta, yawDelta, rollDelta) in
            //   local space della nave. Applicato via post-multiply (LogRot * delta):
            //     - pitch attorno al RIGHT locale (X)
            //     - yaw   attorno all'UP locale   (Y)
            //     - roll  attorno al FORWARD locale (Z)
            //   NO gimbal lock (dt piccolo → nessuna singolarità Euler nel delta).
            //   NO clamp pitch (6DoF completo, ogni assetto raggiungibile).
            Quaternion delta = Quaternion.Euler(
                _currentPitchRate * dt,
                _currentYawRate * dt,
                _currentRollRate * dt);

            _logicalRotation.Value = _logicalRotation.Value * delta;

            // Rev AI (fix rotation collision v3 — definitivo).
            //
            // Sostituisce il fix v1 (freeze reattivo, che incastrava la nave)
            // e il fix v2 (freeze preventivo, mai testato). Approccio Nicolas:
            // il POI risponde all'urto come per una collision traslazionale —
            // riceve impulse e scivola via per inerzia. Il player ottiene
            // spazio per manovrare senza freeze rotation.
            //
            // ResolveRotationPenetration:
            //   - Rileva compenetrazione post-rotation.
            //   - Applica SEMPRE impulse push-out al POI (Q1-B — uniforme
            //     con collision traslazionale).
            //   - Emette OnHardCollision SE impactVelocity ≥ soglia → chain
            //     effettistica completa (damage hull, shake, audio, banner
            //     MOTORI OFFLINE via ShipImpactHandler).
            //
            // Fix strutturale completo (rotation swept CCD) resta debito D18 M4+.
            var resolver = PoiCollisionResolver.Instance;
            if (resolver != null)
            {
                resolver.ResolveRotationPenetration(_logicalPosition.Value, _logicalRotation.Value, dt);
            }
        }

        /// <summary>
        /// Server-only. Accumula LogicalPosition da LogicalForward × CurrentSpeed
        /// in QUALUNQUE nav state con velocità > 0.
        ///
        /// In Docking/Docked (Fase 3): PropulsionSystem forza CurrentSpeed=0 →
        /// early return. Il DockingController scrive _logicalPosition
        /// direttamente via SetLogicalPosition (strafe RCS), senza conflitto.
        ///
        /// Blocco 3.2.c — Hook di collisione POI:
        /// prima di scrivere _logicalPosition, la candidatePos passa attraverso
        /// PoiCollisionResolver.Instance.ResolveCollision (se presente e in
        /// stato Manual/Coasting/Autopilot). Se una coppia (volumeNave, volumePOI)
        /// compenetra, il resolver clampa+slida e ritorna la nuova velocità
        /// scalare, che propago a PropulsionSystem via SetCurrentSpeedFromCollision.
        /// </summary>
        private void UpdatePosition()
        {
            float speed = CurrentSpeed;

            // ── DEBUG HEARTBEAT (guardato da logVerbose) ───────────────
            _debugUpdatePosCounter++;
            if (logVerbose && (_debugUpdatePosCounter % 50 == 0))
            {
                var resDbg = PoiCollisionResolver.Instance;
                Debug.Log($"[ShipMov.UpdatePos] speed={speed:F2}u/s  " +
                          $"nav={CurrentNavState}  " +
                          $"resolverExists={resDbg != null}  " +
                          $"willInvoke={(Mathf.Abs(speed) > 0.01f && resDbg != null)}");
            }

            if (Mathf.Abs(speed) <= 0.01f) return;

            Vector3 currentPos = _logicalPosition.Value;
            Vector3 forward = LogicalForward;
            Vector3 candidatePos = currentPos + forward * speed * Time.fixedDeltaTime;

            var resolver = PoiCollisionResolver.Instance;
            if (resolver != null)
            {
                var res = resolver.ResolveCollision(currentPos, candidatePos, forward, speed);
                _logicalPosition.Value = res.ClampedPosition;

                if (res.VelocityWasClamped && PropulsionSystem.Instance != null)
                {
                    PropulsionSystem.Instance.SetCurrentSpeedFromCollision(res.ClampedSpeedScalar);
                }
            }
            else
            {
                _logicalPosition.Value = candidatePos;
            }
        }

        // =========================================================================
        // API PUBBLICA
        // =========================================================================

        /// <summary>
        /// Chiamato da PilotStation (via LookToSteerController in AH.3), una volta
        /// per frame, mentre il Pilota è seduto e NavigationState == Manual.
        ///
        /// Rev AH (QD-γ) — firma estesa da Vector2 a Vector3:
        ///   .x = look horizontal (yaw input), in [-1, +1]
        ///   .y = look vertical   (pitch input), in [-1, +1] (mouse su = +y → muso su)
        ///   .z = roll input, in [-1, +1] (A = -1, D = +1 by convention)
        ///
        /// Il chiamante (LookToSteerController, AH.3) passa qui l'ECCEDENZA fuori
        /// dal cono per look, e l'input roll diretto A/D. In AH.1 (prima di AH.3),
        /// il chiamante temporaneo è PilotStation che passa direttamente il valore
        /// dell'action "Look" — il coupling completo emerge in AH.3.
        /// </summary>
        public void SetManualLookInput(Vector3 lookAndRollInput)
        {
            Vector3 clamped = new Vector3(
                Mathf.Clamp(lookAndRollInput.x, -1f, 1f),
                Mathf.Clamp(lookAndRollInput.y, -1f, 1f),
                Mathf.Clamp(lookAndRollInput.z, -1f, 1f));

            if (IsServer) _manualLookInput = clamped;
            else SetManualLookInputRpc(clamped);
        }

        [Rpc(SendTo.Server)]
        private void SetManualLookInputRpc(Vector3 lookAndRollInput) => _manualLookInput = lookAndRollInput;

        /// <summary>
        /// Fase 3 3.1.3 — server-only setter di LogicalPosition, chiamato dal
        /// DockingController per applicare lo strafe RCS durante Docking.
        /// </summary>
        public void SetLogicalPosition(Vector3 newPos)
        {
            if (!IsServer)
            {
                Debug.LogError("[ShipMovement] SetLogicalPosition called on client — ignored.");
                return;
            }
            _logicalPosition.Value = newPos;
        }

        /// <summary>
        /// Fase 3 3.1.3 — server-only setter di LogicalRotation, chiamato dal
        /// DockingController per l'auto-align rotazionale.
        ///
        /// NOTA STORICA (Rev AH audit): l'auto-align rotazionale è stato RIMOSSO
        /// in Fase 3.1.5 (Opzione 3). Grep procedurale conferma zero callers
        /// esterni di questa API. Mantenuta come API per eventuali riabilitazioni
        /// future dell'auto-align o override server-driven della rotation nave.
        /// Non rimuoverla senza controllare che il debito D nessuno l'abbia
        /// nel frattempo riesumata.
        /// </summary>
        public void SetLogicalRotation(Quaternion newRot)
        {
            if (!IsServer)
            {
                Debug.LogError("[ShipMovement] SetLogicalRotation called on client — ignored.");
                return;
            }
            _logicalRotation.Value = newRot;
        }

        // =========================================================================
        // HELPER
        // =========================================================================

        private static float MoveToward(float current, float target, float maxDelta)
        {
            float diff = target - current;
            if (Mathf.Abs(diff) <= maxDelta) return target;
            return current + Mathf.Sign(diff) * maxDelta;
        }

        // =========================================================================
        // DEBUG GUI
        // =========================================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showDebugUI) return;

            // Rev AH — display esteso a 6DoF. Rimossa riga "CanSteer" (dipendeva
            // da minSpeedToSteer, ora sempre true in Manual per MS-2).
            // Aggiunta riga rate roll accanto a yaw/pitch.
            Vector3 euler = _logicalRotation.Value.eulerAngles;

            GUILayout.BeginArea(new Rect(10, Screen.height - 140, 380, 130));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[ShipMovement] {(IsServer ? "SRV" : "CLT")} (stato logico — 'Nave' non si muove)");
            GUILayout.Label($"NavState: {CurrentNavState} · Speed: {CurrentSpeed:F1} m/s");
            GUILayout.Label($"Euler(readout): yaw {NormalizeAngleDisplay(euler.y):F0}° · pitch {NormalizeAngleDisplay(euler.x):F0}° · roll {NormalizeAngleDisplay(euler.z):F0}°");
            GUILayout.Label($"Rate: yaw {_currentYawRate:F1}°/s · pitch {_currentPitchRate:F1}°/s · roll {_currentRollRate:F1}°/s");
            GUILayout.Label($"LogicalPos: ({_logicalPosition.Value.x:F0}, {_logicalPosition.Value.y:F0}, {_logicalPosition.Value.z:F0})");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        /// <summary>
        /// Solo per il debug GUI: converte un angolo Euler 0..360 in -180..+180
        /// per leggibilità. NON usato dalla logica runtime (che opera direttamente
        /// su Quaternion senza mai convertire in Euler come stato).
        /// </summary>
        private static float NormalizeAngleDisplay(float angleDeg)
        {
            angleDeg %= 360f;
            if (angleDeg > 180f) angleDeg -= 360f;
            else if (angleDeg < -180f) angleDeg += 360f;
            return angleDeg;
        }
#endif
    }
}