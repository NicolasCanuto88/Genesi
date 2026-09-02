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
    /// esteso Rev AB (Blocco 3.2.d D5 — Compound Collider).
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
    ///     l'orientamento è bloccato.
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
    ///   ShipCollisionRadius. Rev AA lo aveva introdotto come contributo
    ///   sferico della nave alla formula "distanza min tra centri = somma
    ///   dei raggi". Rev AB sostituisce il modello sferico con un compound
    ///   collider (OBB+Sphere multipli, decisioni Q1-Q3 Rev AA workshop):
    ///   la geometria della nave è ora descritta da CompoundColliderAuthoring.
    ///
    ///   AGGIUNTO: cache _compound (CompoundColliderAuthoring) + property
    ///   Compound. Il componente CompoundColliderAuthoring va aggiunto al
    ///   GameObject della Nave (fratello di questo script) e configurato in
    ///   Inspector con i volumi che rappresentano fusoliera + ali + eventuali
    ///   motori. Se assente, il compound è vuoto e degrada al fallback
    ///   "nave = punto" (Rev AD, F-C: il fallback è ora un GUARD per setup
    ///   incompleti, non una modalità di gameplay — vedi
    ///   CompoundColliderMath aIsPoint per la singolarità geometrica associata).
    ///
    ///   Consumer di Compound: PoiCollisionResolver.ResolveCollision e
    ///   DockingController.RunDockingTick (Rev AD: entrambi ora passano
    ///   ShipVolumes non-null a CompoundColliderMath).
    ///
    /// DESIGN — controllo pilotaggio:
    ///   - Assi rotazione: yaw + pitch, no roll
    ///   - Convenzione mouse: FPS standard (mouse su = muso su)
    ///   - Pitch clamp: ±80°
    ///
    /// DIPENDE DA: PropulsionSystem, CompoundColliderAuthoring (Rev AB).
    /// USATO DA:   ExternalWorldFollower, PilotStation, DockingController,
    ///             PoiCollisionResolver.
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

        [Tooltip("Clamp assoluto del pitch in gradi (±). Default 80°.")]
        [SerializeField] private float pitchClampDegrees = 80f;

        [Tooltip("Velocità lineare minima (m/s) sotto la quale la rotazione è " +
                 "BLOCCATA. Default 3 m/s.")]
        [SerializeField] private float minSpeedToSteer = 3f;

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
        private Vector2 _manualLookInput;
        private float _currentYawRate;
        private float _currentPitchRate;

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
        [SerializeField] private bool debugVerbose = false;

        private CompoundColliderAuthoring _compound;
        private bool _hasWarnedMissingCompound;

        /// <summary>
        /// Rev AB — frame counter per throttle del log diagnostico da
        /// UpdatePosition. Emesso solo se debugVerbose == true.
        /// </summary>
        private int _debugUpdatePosCounter;

        // ── Proprietà pubbliche ───────────────────────────────────────────────
        public Quaternion LogicalRotation => _logicalRotation.Value;
        public Vector3 LogicalPosition => _logicalPosition.Value;
        public Vector3 LogicalForward => _logicalRotation.Value * Vector3.forward;

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
        /// Server-only. Se in MANUAL e velocità ≥ minSpeedToSteer, insegue i
        /// rate target con accelerazione angolare, poi applica i rate al
        /// quaternion. Quando la nave rallenta sotto minSpeedToSteer, i rate
        /// decadono a zero.
        /// </summary>
        private void UpdateOrientation()
        {
            var propulsion = PropulsionSystem.Instance;
            bool canSteer = CurrentNavState == NavigationState.Manual
                         && CurrentSpeed >= minSpeedToSteer;

            float dt = Time.fixedDeltaTime;

            float targetYawRate = canSteer ? _manualLookInput.x * manualYawSpeedDegPerSec : 0f;
            float targetPitchRate = canSteer ? -_manualLookInput.y * manualPitchSpeedDegPerSec : 0f;

            float yawAccel, pitchAccel;
            if (propulsion != null && propulsion.YawAcceleration > 0f)
            {
                yawAccel = propulsion.YawAcceleration;
                pitchAccel = propulsion.PitchAcceleration;
            }
            else
            {
                yawAccel = 60f;
                pitchAccel = 45f;
            }

            _currentYawRate = MoveToward(_currentYawRate, targetYawRate, yawAccel * dt);
            _currentPitchRate = MoveToward(_currentPitchRate, targetPitchRate, pitchAccel * dt);

            if (Mathf.Abs(_currentYawRate) < 0.01f && Mathf.Abs(_currentPitchRate) < 0.01f)
                return;

            Vector3 euler = _logicalRotation.Value.eulerAngles;
            float yaw = NormalizeAngle(euler.y);
            float pitch = NormalizeAngle(euler.x);

            yaw += _currentYawRate * dt;
            pitch += _currentPitchRate * dt;

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
        /// stato Manual/Coasting/Autopilot). Se una coppia (volumeNave, volumePOI)
        /// compenetra, il resolver clampa+slida e ritorna la nuova velocità
        /// scalare, che propago a PropulsionSystem via SetCurrentSpeedFromCollision.
        /// </summary>
        private void UpdatePosition()
        {
            float speed = CurrentSpeed;

            // ── DEBUG HEARTBEAT (guardato da debugVerbose) ───────────────
            _debugUpdatePosCounter++;
            if (debugVerbose && (_debugUpdatePosCounter % 50 == 0))
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
        /// Chiamato da PilotStation, una volta per frame, mentre il Pilota è
        /// seduto e NavigationState == Manual. lookDelta atteso in [-1, 1].
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