using System;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ShipMovement — Milestone 3, Blocco 2 + Blocco 3 fase 2 (Rev T),
    /// esteso Fase 3 Blocco 3.1 (Sotto-step 3.1.3) con setter server-only
    /// per DockingController (strafe RCS + auto-align rotazionale).
    ///
    /// DECISIONE ARCHITETTURALE (invariata da Rev Q): "Nave" NON si muove mai
    /// fisicamente nel mondo. Resta esattamente dov'è piazzata in Editor, per
    /// sempre. Questo script traccia SOLO lo stato LOGICO del movimento —
    /// nessuna Transform di "Nave" viene mai spostata o ruotata da qui.
    ///
    /// ESTENSIONE Rev T (verso Blocco 3 fase 2):
    ///   - Orientamento logico esteso da SCALARE (yaw) a QUATERNION completo
    ///     (yaw + pitch, no roll). Serve per pilotaggio 3D e per ruotare
    ///     correttamente il mondo esterno inverso su più assi.
    ///   - Aggiunta LogicalPosition (Vector3 NetworkVariable) accumulata
    ///     server-side per il futuro sistema POI (Fase 2).
    ///
    /// AGGIORNAMENTO Rev T post-playtest — sensazione "nave pesante":
    ///   - INERZIA ROTAZIONALE: yaw e pitch hanno ora un rate corrente
    ///     (deg/sec) che insegue il target (input × maxRate) con
    ///     accelerazione angolare finita (data.yawAcceleration /
    ///     data.pitchAcceleration). La nave "prende" e "perde" la rotazione
    ///     invece di rispondere istantaneamente al mouse. Rende visibile la
    ///     pesantezza della nave e permette manovre più teatrali.
    ///   - NO STEERING A VELOCITÀ ZERO: sotto minSpeedToSteer (default 3 m/s)
    ///     l'orientamento è bloccato. Semanticamente: senza velocità
    ///     lineare la nave non ha come ruotare (niente RCS thrusters per ora).
    ///     Meccanicamente: forza il Pilota ad "avviare" prima di sterzare.
    ///
    /// ESTENSIONE Blocco 3.2.c — hook di collisione POI in UpdatePosition:
    ///   In Manual/Coasting/Autopilot la nave attraversava i POI come fantasmi
    ///   (invariante rev X: clamp posizionale hard esisteva solo dentro il
    ///   Docking). Ora UpdatePosition calcola una candidatePos, invoca
    ///   PoiCollisionResolver.Instance.ResolveCollision(...) che (se in stato
    ///   Manual/Coasting/Autopilot) applica clamp+slide contro il POI più
    ///   vicino tra quelli che sforano HardCollisionRadius, e ritorna
    ///   posizione+scalare velocità post-clamp. Se il resolver ha ridotto la
    ///   velocità, invoco PropulsionSystem.SetCurrentSpeedFromCollision per
    ///   propagare la nuova CurrentSpeed. Coerenza: il resolver in Docking/
    ///   Docked/Anchored dorme (early-return) — il DockingController ha il
    ///   proprio clamp, mutuamente esclusivo. Se il resolver non è ancora
    ///   spawnato (edge case boot), UpdatePosition scrive candidatePos diretta.
    ///
    /// ESTENSIONE Rev AA hotfix — ShipCollisionRadius:
    ///   La nave ha un ingombro fisico non-zero (mesh visiva della cabina +
    ///   motori + ali). La collisione contro POI usa la formula fisica
    ///   "distanza min tra centri = somma dei raggi": raggio effettivo di
    ///   clamp = poi.HardCollisionRadius + ship.ShipCollisionRadius. Senza
    ///   questo contributo il clamp scattava solo quando il PUNTO
    ///   LogicalPosition entrava nella mesh POI — cioè quando l'intera metà
    ///   avanti della nave era già visibilmente compenetrata. ShipCollisionRadius
    ///   è letto sia dal PoiCollisionResolver (Manual/Coasting/Autopilot) sia
    ///   dal DockingController (Docking minigame) per coerenza.
    ///
    /// ESTENSIONE Fase 3 3.1.3 — API per DockingController:
    ///   - SetLogicalPosition(Vector3): server-only setter, usato dal
    ///     DockingController per applicare lo strafe RCS durante Docking.
    ///     In Docking CurrentSpeed=0 → UpdatePosition() qui è inerte (early
    ///     return), quindi la scrittura del DockingController non entra in
    ///     conflitto con l'integrazione throttle Rev T.
    ///   - SetLogicalRotation(Quaternion): server-only setter, usato dal
    ///     DockingController per l'auto-align rotazionale (interpolazione
    ///     shortest-arc verso l'allineamento pancia-approachAxis del POI).
    ///     In Docking CurrentNavState != Manual → UpdateOrientation() qui
    ///     lascia decadere i rate a zero e non scrive la NetVar (early
    ///     return se rate insignificanti), quindi zero conflitto.
    ///
    /// DESIGN — controllo pilotaggio:
    ///   - Assi rotazione: yaw + pitch, no roll
    ///   - Convenzione mouse: FPS standard (mouse su = muso su)
    ///   - Pitch clamp: ±80°
    ///   - Roll sempre zero — garantito dalla ricomposizione via
    ///     Quaternion.Euler(pitch, yaw, 0)
    ///
    /// DIPENDE DA: PropulsionSystem (CurrentNavState, CurrentSpeed,
    ///             MaxSpeedAtDegradation, data.yawAcceleration/pitchAcceleration)
    /// USATO DA: ExternalWorldFollower, PilotStation, DockingController,
    ///           futuro sistema POI
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
                 "(data.yawAcceleration). Default 90 = giro completo in ~4s a " +
                 "input pieno, dopo aver 'preso' la rotazione.")]
        [SerializeField] private float manualYawSpeedDegPerSec = 90f;

        [Tooltip("Rate MASSIMO di pitch in gradi/secondo, a input Y massimo (±1). " +
                 "Convenzione FPS: mouse su = muso su (pitch euler negativo).")]
        [SerializeField] private float manualPitchSpeedDegPerSec = 60f;

        [Tooltip("Clamp assoluto del pitch in gradi (±). Default 80°.")]
        [SerializeField] private float pitchClampDegrees = 80f;

        [Tooltip("Velocità lineare minima (m/s) sotto la quale la rotazione è " +
                 "BLOCCATA. Semantica: senza velocità la nave non ha come " +
                 "ruotare (niente RCS). Meccanicamente: il Pilota deve avviare " +
                 "prima di sterzare. Default 3 m/s. Se metti 0, la rotazione è " +
                 "sempre concessa (comportamento pre-Rev T post-playtest).")]
        [SerializeField] private float minSpeedToSteer = 3f;

        // ── Collisione fisica (Blocco 3.2.c hotfix Rev AA) ───────────────────
        [Header("Collisione fisica (Rev AA)")]
        [Tooltip("Raggio di collisione della nave (u logiche). Contributo della " +
                 "geometria della nave alla formula di collisione contro POI: " +
                 "raggio effettivo di clamp = poi.HardCollisionRadius + " +
                 "ship.ShipCollisionRadius (distanza min tra centri = somma dei " +
                 "raggi). Senza questo contributo il clamp scattava solo quando " +
                 "il PUNTO LogicalPosition entrava nella mesh POI — cioè quando " +
                 "l'intera metà avanti della nave era già visibilmente " +
                 "compenetrata (bug rilevato in playtest 3.2.c.4 pre-hotfix).\n\n" +
                 "Default 15 u: punto di partenza plausibile per la mesh " +
                 "CreepyCat Scifi Kit Vol.4. Tuning empirico: aumentare finché " +
                 "cabina e ali non entrano più nella mesh POI durante impatto " +
                 "Manual. Applicato sia in Docking (DockingController) sia in " +
                 "Manual/Coasting/Autopilot (PoiCollisionResolver) per coerenza.")]
        [Min(0f)]
        [SerializeField] private float shipCollisionRadius = 15f;

        // ── Stato di rete ─────────────────────────────────────────────────────
        private readonly NetworkVariable<Quaternion> _logicalRotation = new NetworkVariable<Quaternion>(
            Quaternion.identity,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<Vector3> _logicalPosition = new NetworkVariable<Vector3>(
            Vector3.zero,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        // Stato server-only (non replicato — sono cinematica interna, ricostruita
        // da input + rotation replicati). Vector2 = (X: yaw input, Y: pitch input).
        private Vector2 _manualLookInput;
        private float _currentYawRate;    // deg/sec — insegue input.x × maxYaw con inerzia
        private float _currentPitchRate;  // deg/sec — insegue input.y × maxPitch con inerzia

        // ── Proprietà pubbliche ───────────────────────────────────────────────
        public Quaternion LogicalRotation => _logicalRotation.Value;
        public Vector3 LogicalPosition => _logicalPosition.Value;
        public Vector3 LogicalForward => _logicalRotation.Value * Vector3.forward;

        public float CurrentSpeed =>
            PropulsionSystem.Instance != null ? PropulsionSystem.Instance.CurrentSpeed : 0f;

        public NavigationState CurrentNavState =>
            PropulsionSystem.Instance != null ? PropulsionSystem.Instance.CurrentNavState : NavigationState.Anchored;

        /// <summary>
        /// Blocco 3.2.c hotfix Rev AA — raggio di collisione della nave (u logiche).
        /// Contributo geometrico della nave alla formula di clamp contro POI.
        /// Letto da DockingController (via PoiCollisionMath.ClampAgainstPoi) e da
        /// PoiCollisionResolver (idem). Vedi tooltip inspector per motivazione
        /// completa.
        /// </summary>
        public float ShipCollisionRadius => shipCollisionRadius;

        // =========================================================================
        // LIFECYCLE NGO
        // =========================================================================

        public override void OnNetworkSpawn()
        {
            Instance = this;
            OnInstanceReady?.Invoke();
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
        /// Server-only. Se in MANUAL e velocità ≥ minSpeedToSteer, insegue i
        /// rate target (input × maxRate) con accelerazione angolare data dal
        /// PropulsionUpgradeData, poi applica i rate correnti al quaternion.
        ///
        /// Quando la nave rallenta sotto minSpeedToSteer, i rate correnti
        /// vengono lasciati decadere a zero (con la stessa accelerazione) —
        /// così quando la nave si ferma non c'è uno "stop rotazionale" duro
        /// che sembra un bug.
        ///
        /// In Docking/Docked (Fase 3): CurrentNavState != Manual → canSteer=false
        /// → rate decadono a zero → early return se insignificanti. Il
        /// DockingController scrive direttamente _logicalRotation via
        /// SetLogicalRotation, senza conflitto.
        ///
        /// Roll garantito a zero dalla composizione via Quaternion.Euler.
        /// </summary>
        private void UpdateOrientation()
        {
            var propulsion = PropulsionSystem.Instance;
            bool canSteer = CurrentNavState == NavigationState.Manual
                         && CurrentSpeed >= minSpeedToSteer;

            float dt = Time.fixedDeltaTime;

            // Rate target: se posso sterzare, insegue l'input; altrimenti decadi a zero.
            // Convenzione FPS per il pitch: mouse su → muso su → euler.x negativo → segno meno.
            float targetYawRate = canSteer ? _manualLookInput.x * manualYawSpeedDegPerSec : 0f;
            float targetPitchRate = canSteer ? -_manualLookInput.y * manualPitchSpeedDegPerSec : 0f;

            // Accelerazione angolare dal data, scalata dal degrado (se disponibile).
            float yawAccel, pitchAccel;
            if (propulsion != null && propulsion.YawAcceleration > 0f)
            {
                yawAccel = propulsion.YawAcceleration;
                pitchAccel = propulsion.PitchAcceleration;
            }
            else
            {
                // Fallback (non dovrebbe mai succedere se PropulsionSystem è spawnato).
                yawAccel = 60f;
                pitchAccel = 45f;
            }

            _currentYawRate = MoveToward(_currentYawRate, targetYawRate, yawAccel * dt);
            _currentPitchRate = MoveToward(_currentPitchRate, targetPitchRate, pitchAccel * dt);

            // Se nessuno dei due rate è significativo, non toccare la NetworkVariable
            // (evita scritture inutili e micro-jitter numerico su tempi lunghi).
            if (Mathf.Abs(_currentYawRate) < 0.01f && Mathf.Abs(_currentPitchRate) < 0.01f)
                return;

            // Estrai yaw/pitch dalla rotazione corrente, normalizzati in [-180, +180].
            Vector3 euler = _logicalRotation.Value.eulerAngles;
            float yaw = NormalizeAngle(euler.y);
            float pitch = NormalizeAngle(euler.x);

            // Integra i rate correnti nel dt.
            yaw += _currentYawRate * dt;
            pitch += _currentPitchRate * dt;

            // Clamp pitch. Roll forzato a 0 dalla ricomposizione euler.
            pitch = Mathf.Clamp(pitch, -pitchClampDegrees, +pitchClampDegrees);

            _logicalRotation.Value = Quaternion.Euler(pitch, yaw, 0f);
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
        /// stato Manual/Coasting/Autopilot). Se sfora HardCollisionRadius del
        /// POI più vicino, il resolver clampa+slida e ritorna la nuova velocità
        /// scalare, che propago a PropulsionSystem via SetCurrentSpeedFromCollision.
        /// In Docking/Docked il resolver dorme (mutex col DockingController) →
        /// passa la candidatePos inalterata.
        /// </summary>
        private void UpdatePosition()
        {
            float speed = CurrentSpeed;
            if (Mathf.Abs(speed) <= 0.01f) return;

            Vector3 currentPos = _logicalPosition.Value;
            Vector3 forward = LogicalForward;
            Vector3 candidatePos = currentPos + forward * speed * Time.fixedDeltaTime;

            // Se il resolver è pronto, delego a lui la decisione finale su
            // posizione + velocità. Altrimenti (edge case boot: resolver non
            // ancora spawnato) scrivo la candidate diretta — comportamento
            // pre-3.2.c preservato.
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
        /// Chiamato da PilotStation, una volta per frame, mentre il Pilota è
        /// seduto e NavigationState == Manual. lookDelta atteso in [-1, 1] su
        /// entrambi gli assi (X = yaw, Y = pitch — dell'azione Look, mouse/stick).
        /// La sensibilità mouse è applicata a monte in PilotStation, questo
        /// input arriva già scalato correttamente per device.
        /// </summary>
        public void SetManualLookInput(Vector2 lookDelta)
        {
            Vector2 clamped = new Vector2(
                Mathf.Clamp(lookDelta.x, -1f, 1f),
                Mathf.Clamp(lookDelta.y, -1f, 1f));

            if (IsServer) _manualLookInput = clamped;
            else SetManualLookInputRpc(clamped);
        }

        [Rpc(SendTo.Server)]
        private void SetManualLookInputRpc(Vector2 lookDelta) => _manualLookInput = lookDelta;

        /// <summary>
        /// Fase 3 3.1.3 — server-only setter di LogicalPosition, chiamato dal
        /// DockingController per applicare lo strafe RCS durante Docking.
        /// Fuori da Docking (in Manual/Autopilot) il PropulsionSystem integra
        /// LogicalForward × CurrentSpeed tramite UpdatePosition() sopra — non
        /// dovrebbe essere chiamato in quegli stati (la validazione della
        /// coerenza è responsabilità del chiamante, tipicamente
        /// DockingController che gira solo se stato == Docking).
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
        /// DockingController per l'auto-align rotazionale (shortest-arc slerp
        /// verso l'allineamento pancia-approachAxis del POI, pesato sulla
        /// progressione di avvicinamento).
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

        /// <summary>
        /// Sposta 'current' verso 'target' di al massimo 'maxDelta' unità.
        /// Come Mathf.MoveTowards, esplicitato qui per chiarezza.
        /// </summary>
        private static float MoveToward(float current, float target, float maxDelta)
        {
            float diff = target - current;
            if (Mathf.Abs(diff) <= maxDelta) return target;
            return current + Mathf.Sign(diff) * maxDelta;
        }

        private static float NormalizeAngle(float angleDeg)
        {
            angleDeg %= 360f;
            if (angleDeg > 180f) angleDeg -= 360f;
            else if (angleDeg < -180f) angleDeg += 360f;
            return angleDeg;
        }

        // =========================================================================
        // DEBUG GUI
        // =========================================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            Vector3 euler = _logicalRotation.Value.eulerAngles;
            float yaw = NormalizeAngle(euler.y);
            float pitch = NormalizeAngle(euler.x);

            GUILayout.BeginArea(new Rect(10, Screen.height - 140, 340, 130));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[ShipMovement] {(IsServer ? "SRV" : "CLT")} (stato logico — 'Nave' non si muove)");
            GUILayout.Label($"NavState: {CurrentNavState} · Speed: {CurrentSpeed:F1} m/s");
            GUILayout.Label($"Rotation: yaw {yaw:F1}° · pitch {pitch:F1}°");
            GUILayout.Label($"Rate: yaw {_currentYawRate:F1}°/s · pitch {_currentPitchRate:F1}°/s");
            GUILayout.Label($"CanSteer: {(CurrentNavState == NavigationState.Manual && CurrentSpeed >= minSpeedToSteer)}");
            GUILayout.Label($"LogicalPos: ({_logicalPosition.Value.x:F0}, {_logicalPosition.Value.y:F0}, {_logicalPosition.Value.z:F0})");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}