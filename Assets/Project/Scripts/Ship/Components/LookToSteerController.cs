using UnityEngine;

namespace SpaceSurvivor.Ship.Components
{
    /// <summary>
    /// LookToSteerController — Milestone 3, Rev AH.3.
    ///
    /// Implementa il pattern "look-to-steer" per la guida cockpit view interno:
    ///
    ///   1. LIBERTÀ VISTA (QB-2): la camera del pilota può ruotare entro un
    ///      cono ±20° yaw / ±15° pitch (default, tunabile). Dentro il cono,
    ///      l'input mouse muove la camera del player. La rotazione nave resta
    ///      ferma (LogicalRotation non tocca).
    ///
    ///   2. COUPLING OLTRE CONO (QC-2): quando l'input tenta di spingere la
    ///      vista oltre il bordo del cono, l'eccedenza NON è persa — viene
    ///      passata come input alla nave via ShipMovement.SetManualLookInput.
    ///      La curva è quadratica (ease-in): eccedenza piccola = coupling
    ///      minimo, eccedenza grande = coupling pieno. Feedback fluido, no
    ///      scatti bruschi.
    ///
    ///   3. AUTO-RECENTER (RC1-b + RC2-c + RC3-c): quando l'input è sotto
    ///      deadzone 0.05 (mouse fermo, stick rilasciato), la camera ritorna
    ///      al centro del cono con critically-damped spring (SmoothDamp),
    ///      settling ~0.25s. La nave nel frattempo continua a ruotare per
    ///      inerzia (RC3-c) tramite l'accelerazione angolare finita del rate
    ///      esistente in ShipMovement (yawAcceleration/pitchAcceleration).
    ///
    ///   4. ROLL (QD-γ + Roll-γ): il roll è direct steering (A/D + gamepad
    ///      LT/RT) — la camera NON rolla (motion sickness prevention). Il
    ///      valore di rollInput è passato diritto a SetManualLookInput.z, che
    ///      in ShipMovement diventa target rate del roll con inerzia
    ///      (Roll-inertia-2, tramite RollAcceleration di PropulsionSystem).
    ///
    /// INVARIANTE Rev Q PRESERVATA:
    ///   Questo componente scrive SOLO su playerCamera.transform.localRotation
    ///   (la camera del pilota, figlia del Player). La Nave (GameObject fisico)
    ///   NON viene mai toccata. L'eventuale rotazione della nave è puramente
    ///   logica via ShipMovement.LogicalRotation → ExternalWorldFollower ruota
    ///   il mondo inverso.
    ///
    /// PATTERN DELTA CameraShaker (Rev AE):
    ///   Il CameraShaker applica delta rotation in LateUpdate via
    ///   transform.localRotation *= deltaRot. Il pattern DELTA compone opaco
    ///   con qualunque baseline scritta prima. Poiché LookToSteerController
    ///   scrive la baseline via Tick chiamato in PilotStation.Update
    ///   (PollManualFlightState), l'ordine temporale è:
    ///     Update:      LookToSteer scrive localRotation (baseline)
    ///     LateUpdate:  CameraShaker compone DELTA sulla baseline
    ///   Zero conflitto, shake sempre pulito.
    ///
    /// PATTERN Tick DETERMINISTICO (TIME-2):
    ///   Nessun Update indipendente. Il componente espone Tick(look, roll) che
    ///   PilotStation.PollManualFlightState chiama direttamente ogni frame nel
    ///   branch Manual. Ordine deterministico, testabile, zero configurazione
    ///   Editor via Script Execution Order.
    ///
    /// LIFECYCLE:
    ///   Bind(Camera cam):    chiamato da PilotStation.EnterStation con la
    ///                        camera del pilota corrente. Cacha il riferimento
    ///                        e resetta lo stato interno.
    ///   Tick(look, roll):    chiamato ogni frame in Manual da
    ///                        PilotStation.PollManualFlightState. Con input
    ///                        zero, esegue il recenter smooth.
    ///   Unbind():            chiamato da PilotStation.TryExitStation.
    ///                        Rimuove il riferimento e resetta lo stato.
    ///
    /// SETUP EDITOR:
    ///   1. Nella scena, crea un GameObject vuoto figlio della PilotStation:
    ///      nome consigliato "CockpitLookAnchor".
    ///   2. Aggiungi questo componente (LookToSteerController) al GameObject.
    ///   3. Nell'Inspector della PilotStation, drag il GameObject
    ///      CockpitLookAnchor nel campo "Look Controller".
    ///   4. I parametri del cono e del coupling sono tunabili nell'Inspector
    ///      del componente (default coerenti con workshop AH: ±20°/±15°,
    ///      deadzone 0.05, spring 0.25s, easing quadratic).
    ///
    /// DIPENDE DA: ShipMovement.Instance (per SetManualLookInput).
    /// USATO DA:   PilotStation (Bind/Tick/Unbind in EnterStation/
    ///             PollManualFlightState/TryExitStation).
    /// </summary>
    public class LookToSteerController : MonoBehaviour
    {
        // ── Cono di libertà vista (QB-2) ──────────────────────────────────
        [Header("Cono libertà vista (QB-2)")]
        [Tooltip("Ampiezza massima yaw (gradi ±) della camera dentro il cono " +
                 "prima che il coupling con la nave si attivi. Default 20°.")]
        [SerializeField] private float coneYawDegrees = 20f;

        [Tooltip("Ampiezza massima pitch (gradi ±) della camera dentro il cono. " +
                 "Default 15° (tipicamente minore di yaw per emulare fisiologia " +
                 "collo umano).")]
        [SerializeField] private float conePitchDegrees = 15f;

        // ── Auto-recenter (RC1-b + RC2-c) ─────────────────────────────────
        [Header("Auto-recenter (RC1-b + RC2-c)")]
        [Tooltip("Soglia sotto cui l'input look è considerato zero (deadzone). " +
                 "Sotto questa soglia, la camera torna al centro del cono. " +
                 "Default 0.05 (coerente con DockingController.inputDeadZone).")]
        [SerializeField] private float recenterDeadzone = 0.05f;

        [Tooltip("Tempo di settling (secondi) del critically-damped spring che " +
                 "riporta la camera al centro. Default 0.25s = risposta rapida " +
                 "ma percettibilmente fluida, no snap istantaneo.")]
        [SerializeField] private float recenterSettlingTime = 0.25f;

        // ── Coupling (QC-2 easing) ────────────────────────────────────────
        [Header("Coupling look → nave (QC-2 easing)")]
        [Tooltip("Esponente della curva easing applicata all'eccedenza oltre " +
                 "il cono prima di passarla a ShipMovement come input nave. " +
                 "1.0 = lineare (QC-1). 2.0 = quadratic ease-in (QC-2, default). " +
                 "3.0 = cubic (più dolce all'inizio).")]
        [SerializeField] private float couplingEasingExponent = 2f;

        [Tooltip("Eccedenza (gradi/frame) oltre il cono che produce coupling " +
                 "pieno (input = ±1 a ShipMovement) PRIMA dell'easing. Valori " +
                 "più piccoli = coupling più reattivo (piccolo push oltre = piena " +
                 "rotazione nave). Default 3° — con movimento mouse moderatamente " +
                 "veloce oltre il cono, coupling raggiunge full-scale in ~1 frame. " +
                 "Tunabile in playtest per aggiustare la 'pesantezza' del pattern.")]
        [SerializeField] private float couplingReferenceExcessDeg = 3f;

        // ── Debug ─────────────────────────────────────────────────────────
        [Header("Debug")]
        [Tooltip("Log verboso: emette in Debug.Log il valore di offset camera + " +
                 "eccedenza + coupling ogni frame in cui Tick viene chiamato con " +
                 "input non-zero. Attivare solo per indagare comportamenti " +
                 "anomali del coupling. Off in gameplay normale.")]
        [SerializeField] private bool debugVerbose = false;

        // ── Stato runtime ─────────────────────────────────────────────────
        private Camera _boundCamera;
        private float _yawOffset;      // gradi, entro [-coneYawDegrees, +coneYawDegrees]
        private float _pitchOffset;    // gradi, entro [-conePitchDegrees, +conePitchDegrees]
        private float _yawVelocity;    // SmoothDamp state
        private float _pitchVelocity;  // SmoothDamp state

        // =========================================================================
        // API PUBBLICA
        // =========================================================================

        /// <summary>
        /// Assegna la camera che questo controller pilota. Chiamato da
        /// PilotStation.EnterStation con la camera del pilota corrente.
        /// Resetta lo stato interno (offset camera a zero).
        ///
        /// NON scrive immediatamente sulla camera — il primo Tick lo farà.
        /// Se serve un reset visibile immediato della camera al centro, il
        /// chiamante può invocare Tick(Vector2.zero, 0f) subito dopo Bind.
        /// </summary>
        public void Bind(Camera cam)
        {
            _boundCamera = cam;
            ResetInternalState();
        }

        /// <summary>
        /// Rimuove il riferimento alla camera e resetta lo stato interno.
        /// Chiamato da PilotStation.TryExitStation. Non tenta di riportare
        /// la camera al centro — PilotStation gestisce autonomamente il
        /// ripristino della camera all'uscita (originalCameraRotation).
        /// </summary>
        public void Unbind()
        {
            _boundCamera = null;
            ResetInternalState();
        }

        /// <summary>
        /// Chiamato ogni frame da PilotStation.PollManualFlightState nel branch
        /// Manual. Implementa look-to-steer + auto-recenter + coupling.
        ///
        /// lookInput: delta mouse post-sensitivity, gradi (float). Convenzione:
        ///   x positivo = guarda a destra (yaw destro)
        ///   y positivo = mouse su = muso su (input FPS standard)
        ///
        /// rollInput: input roll [-1, +1]. A/D o gamepad LT/RT.
        ///   Passato diretto a ShipMovement.SetManualLookInput.z (Roll-γ:
        ///   direct steering nave, camera non rolla).
        ///
        /// Se non bound o camera null, no-op (guard difensiva — non dovrebbe
        /// mai accadere se PilotStation gestisce correttamente Bind/Unbind).
        /// </summary>
        public void Tick(Vector2 lookInput, float rollInput)
        {
            if (_boundCamera == null) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;  // guard tempi patologici (pause, ecc.)

            // ── Deadzone (RC1-b): input molto piccolo = recenter smooth ──
            bool inDeadzone = lookInput.magnitude < recenterDeadzone;

            float couplingYawInput = 0f;
            float couplingPitchInput = 0f;

            if (inDeadzone)
            {
                // RC2-c: critically-damped spring toward zero.
                // SmoothDamp con target=0 è il classico modo Unity per un
                // critically-damped spring, no overshoot, tempo di settling
                // controllato da smoothTime (~2x per ~90% settling).
                _yawOffset = Mathf.SmoothDamp(
                    _yawOffset, 0f, ref _yawVelocity, recenterSettlingTime, Mathf.Infinity, dt);
                _pitchOffset = Mathf.SmoothDamp(
                    _pitchOffset, 0f, ref _pitchVelocity, recenterSettlingTime, Mathf.Infinity, dt);

                // Nessun coupling — input a zero, la nave decelera per inerzia
                // rotazionale già presente in ShipMovement (RC3-c comportamento).
                // Ricomincia dalle velocità SmoothDamp: perché la camera sta
                // già "rilasciando" per conto suo, il ricalcolo di _yawVelocity
                // continua da SmoothDamp.
            }
            else
            {
                // Input attivo: consuma il delta camera prima, coupling dopo.
                // Reset velocità SmoothDamp perché stiamo interrompendo il recenter
                // (se era in corso) — evita "attriti fantasma" quando il player
                // ricomincia a muovere il mouse durante il recenter.
                _yawVelocity = 0f;
                _pitchVelocity = 0f;

                // Yaw
                (float newYaw, float excessYaw) =
                    ConsumeInputAndComputeExcess(_yawOffset, lookInput.x, coneYawDegrees);
                _yawOffset = newYaw;
                couplingYawInput = NormalizeAndEase(excessYaw);

                // Pitch — convenzione: input.y positivo = mouse su = _pitchOffset
                // aumenta (positive pitch offset = guardo in alto). L'eccedenza
                // ha lo stesso segno logico dell'input.y.
                (float newPitch, float excessPitch) =
                    ConsumeInputAndComputeExcess(_pitchOffset, lookInput.y, conePitchDegrees);
                _pitchOffset = newPitch;
                couplingPitchInput = NormalizeAndEase(excessPitch);
            }

            // ── Applica rotation camera (FRAME-a: localRotation) ─────────
            // Convenzione Unity Quaternion.Euler: X=pitch verso il basso,
            // Y=yaw destro, Z=roll. Il nostro _pitchOffset positivo = "guardo
            // in alto", quindi in Quaternion.Euler serve NEGATIVO su X per
            // ottenere pitch-up (rotazione muso su → camera guarda in alto).
            _boundCamera.transform.localRotation =
                Quaternion.Euler(-_pitchOffset, _yawOffset, 0f);

            // ── Passa coupling + roll a ShipMovement ─────────────────────
            // Il .y qui è l'input pitch nel FRAME dell'input mouse (positivo =
            // muso su). ShipMovement lo negherà internamente per convertirlo
            // in delta Quaternion (dove x positivo = muso giù). Coerente col
            // pattern usato in AH.1 quando PilotStation passava lookDelta
            // diretto.
            var ship = ShipMovement.Instance;
            if (ship != null)
            {
                ship.SetManualLookInput(new Vector3(
                    couplingYawInput,
                    couplingPitchInput,
                    Mathf.Clamp(rollInput, -1f, 1f)));
            }

            if (debugVerbose && (Mathf.Abs(couplingYawInput) > 0.01f
                              || Mathf.Abs(couplingPitchInput) > 0.01f
                              || Mathf.Abs(rollInput) > 0.01f))
            {
                Debug.Log($"[LookToSteer] offset=({_yawOffset:F1},{_pitchOffset:F1})° " +
                          $"coupling=({couplingYawInput:F2},{couplingPitchInput:F2}) " +
                          $"roll={rollInput:F2}");
            }
        }

        // =========================================================================
        // HELPER
        // =========================================================================

        /// <summary>
        /// Data una configurazione (offset corrente, input delta gradi, cono
        /// max), ritorna:
        ///   newOffset: nuovo offset clampato entro [-cone, +cone]
        ///   excessDeg: eccedenza (con segno) che sarebbe stata applicata
        ///              oltre il cono se non ci fosse stato il clamp.
        ///
        /// Se l'input non porta l'offset a saturare il cono, excessDeg = 0.
        /// Se il cono è già saturato E l'input spinge nella stessa direzione,
        /// tutto l'input diventa eccedenza (nessun consumo camera).
        /// Se il cono è saturato ma l'input spinge in direzione opposta,
        /// l'input consuma camera (rientro dal bordo) e excessDeg = 0.
        /// </summary>
        private static (float newOffset, float excessDeg) ConsumeInputAndComputeExcess(
            float offset, float inputDeg, float coneMax)
        {
            float rawNew = offset + inputDeg;
            float clamped = Mathf.Clamp(rawNew, -coneMax, coneMax);
            float excess = rawNew - clamped;
            return (clamped, excess);
        }

        /// <summary>
        /// Normalizza l'eccedenza (gradi/frame) contro couplingReferenceExcessDeg
        /// e applica l'easing quadratic (o custom via couplingEasingExponent).
        /// Ritorna un valore in [-1, +1] adatto a ShipMovement.SetManualLookInput.
        ///
        /// Formula: sign(excess) * clamp(|excess| / reference, 0, 1) ^ exponent
        ///   - lineare (exponent=1): risposta immediata
        ///   - quadratic (exponent=2): ease-in dolce, coupling piccolo per
        ///     piccole eccedenze, coupling pieno per grandi
        ///   - cubic (exponent=3): ancora più dolce all'inizio, salita rapida
        ///     nella parte alta.
        /// </summary>
        private float NormalizeAndEase(float excessDeg)
        {
            if (Mathf.Abs(excessDeg) < 0.001f || couplingReferenceExcessDeg < 0.001f)
                return 0f;

            float normalized = Mathf.Clamp01(Mathf.Abs(excessDeg) / couplingReferenceExcessDeg);
            float eased = Mathf.Pow(normalized, couplingEasingExponent);
            return Mathf.Sign(excessDeg) * eased;
        }

        private void ResetInternalState()
        {
            _yawOffset = 0f;
            _pitchOffset = 0f;
            _yawVelocity = 0f;
            _pitchVelocity = 0f;
        }
    }
}
