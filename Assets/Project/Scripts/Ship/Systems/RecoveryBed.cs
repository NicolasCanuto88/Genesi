using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// RecoveryBed — letto della Recovery Bay (Rev BO-a · Fase 3a Corpsman · M4.2).
    /// Coordinatore server del trattamento medico: fratello di RepairPanel /
    /// StabilizationPanel, ma con OCCUPAZIONE DI RETE (i pannelli non ce l'hanno).
    ///
    /// FLUSSO (Q1-a):
    ///   1. Un giocatore ferito (Alive, HP &lt; max) interagisce → "Sdraiati".
    ///      Il server lo accetta come paziente (netPatient). Il suo client blocca il
    ///      movimento e porta la CAMERA sul patientViewPoint (sdraiato, sguardo in alto).
    ///   2. Senza trattamento in corso il letto cura da solo fino al tetto del tier
    ///      (T1: 50% di maxHP — Q5-a, valori da MedbayConfig).
    ///   3. Un altro membro interagisce → "Cura paziente (ruolo)". Il server lo
    ///      accetta come operatore (netOperator) e il suo client apre
    ///      StabilizationMinigameMedical. Durante la sessione l'auto-cura è sospesa.
    ///   4. Ogni soglia del minigame chiama ApplyTreatmentThresholdRpc: il server porta
    ///      gli HP del paziente ad ALMENO soglia% di maxHP (Q3-a, idempotente).
    ///   5. Il paziente si alza con Cancel. Le soglie già raggiunte restano.
    ///
    /// AUTORITÀ: ogni richiesta è una RPC verso il server, che identifica il mittente
    /// con SenderClientId (precedente Rev BM): nessun clientId nel payload. Paziente e
    /// operatore sono NetworkVariable scritte solo dal server → niente doppio paziente
    /// né doppio operatore, anche con richieste simultanee.
    ///
    /// FINI FORZATE (server, ogni frame):
    ///   - paziente non più Alive (a terra) o disconnesso → letto liberato, sessione chiusa;
    ///   - operatore non più Alive o disconnesso → sessione chiusa, auto-cura riprende.
    ///   I client reagiscono ai cambi delle NetworkVariable (OnValueChanged): nessuna
    ///   RPC verso i client.
    ///
    /// SCELTE LOCALI (paziente e operatore):
    ///   - Solo la CAMERA del paziente si sposta sul letto; il root del player NON si
    ///     muove e il CharacterController NON viene toccato. Motivi: niente teletrasporto
    ///     dentro la geometria, e un paziente che va a terra resta un bersaglio valido
    ///     per il defibrillatore (collider attivo, regola Rev BD). La posa sdraiata vera
    ///     arriverà con il corpo del player (D33).
    ///   - Al ripristino PlayerController torna attivo SOLO se il giocatore è Alive:
    ///     il freeze Downed di PlayerHealthSystem resta l'autorità (l'ordine dei
    ///     callback tra NetworkObject diversi non è garantito).
    ///   - Il tablet è bloccato in apertura per tutta la durata (TabletStation.SetOpenBlocked):
    ///     tablet e letto salvano/ripristinano entrambi PlayerController e camera.
    ///     Audit Rev AF/AG, vedi TabletStation.
    ///
    /// ⚠️ SETUP SCENA (guida Editor Rev BO-a): GameObject con NetworkObject + collider
    /// sulla layer Interactable + questo componente; figli PatientViewPoint,
    /// OccupiedVisual e il canvas del minigame medico. Oggetto di scena: nessuna
    /// registrazione in NetworkPrefabs.
    /// </summary>
    public class RecoveryBed : NetworkBehaviour, IInteractable
    {
        /// <summary>Sentinella "nessun client" per paziente e operatore.</summary>
        public const ulong NoClient = ulong.MaxValue;

        private enum LocalRole { None, Patient, Operator }

        [Header("Config")]
        [Tooltip("Asset MedbayConfig: tetto e velocità dell'auto-cura per tier, parametri del " +
                 "trattamento, malus Rev U. Obbligatorio.")]
        [SerializeField] private MedbayConfig config;

        [Header("Minigame di trattamento")]
        [Tooltip("Lo StabilizationMinigameMedical di questo letto (di solito il canvas figlio).")]
        [SerializeField] private StabilizationMinigameMedical treatmentMinigame;

        [Header("Paziente")]
        [Tooltip("Posa della CAMERA del paziente sdraiato: posizione poco sopra il cuscino, " +
                 "asse Z (blu) rivolto verso il soffitto. Il root del player non si sposta.")]
        [SerializeField] private Transform patientViewPoint;

        [Header("Visibilità agli altri (Q1.v-a)")]
        [Tooltip("Placeholder 'letto occupato' (es. capsula sdraiata, senza collider). Acceso su " +
                 "tutti i client quando c'è un paziente, tranne che per il paziente stesso. " +
                 "Sostituito dal corpo del player quando arriverà D33.")]
        [SerializeField] private GameObject occupiedVisual;

        [Header("Prompt")]
        [SerializeField] private string lieDownPrompt = "Sdraiati sul lettino";
        [Tooltip("Prompt per curare. {0} = ruolo del paziente (il nome del personaggio non è replicato).")]
        [SerializeField] private string treatPromptFormat = "Cura paziente ({0})";

        [Header("Tempi")]
        [Tooltip("Pausa dopo una richiesta al server, per non inviarne una a ogni pressione.")]
        [SerializeField] private float requestCooldown = 0.5f;
        [Tooltip("Pausa dopo l'uscita dal letto o dal trattamento: evita di rientrare subito " +
                 "(E è sia Interact sia RepairMash).")]
        [SerializeField] private float exitCooldown = 1.0f;

        [Header("Debug")]
        [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development, solo server). Standard Rev BA — default off.")]
        [SerializeField] private bool showDebugUI = false;
        [Tooltip("Log verbosi (richieste rifiutate, cure applicate). Standard Rev BA — default off.")]
        [SerializeField] private bool logVerbose = false;

        // ── Occupazione — NetworkVariable (server scrive, tutti leggono) ──
        private readonly NetworkVariable<ulong> netPatient = new NetworkVariable<ulong>(
            NoClient, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<ulong> netOperator = new NetworkVariable<ulong>(
            NoClient, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public ulong PatientClientId => netPatient.Value;
        public ulong OperatorClientId => netOperator.Value;
        public bool IsOccupied => netPatient.Value != NoClient;

        // ── Stato server ──
        private float _autoHealAccumulator;

        // ── Stato locale (client che è paziente oppure operatore di questo letto) ──
        private LocalRole _localRole = LocalRole.None;
        private float _cooldown;
        private bool _leaveRequested;
        private bool _despawning;

        private PlayerHealthSystem _localHealth;
        private PlayerController _localController;
        private Transform _localCamera;
        private TabletStation _localTablet;
        private InputAction _localCancel;

        private Vector3 _savedCameraLocalPosition;
        private Quaternion _savedCameraLocalRotation;

        // ── Lifecycle NGO ──────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            _despawning = false;
            netPatient.OnValueChanged += HandlePatientChanged;
            netOperator.OnValueChanged += HandleOperatorChanged;

            ApplyOccupiedVisual(netPatient.Value);

            if (config == null)
                Debug.LogError($"[RecoveryBed] {name}: MedbayConfig non assegnato — letto disattivato.");
            if (treatmentMinigame == null)
                Debug.LogError($"[RecoveryBed] {name}: StabilizationMinigameMedical non assegnato — letto disattivato.");
            if (patientViewPoint == null)
                Debug.LogError($"[RecoveryBed] {name}: PatientViewPoint non assegnato — letto disattivato.");
        }

        public override void OnNetworkDespawn()
        {
            _despawning = true;
            netPatient.OnValueChanged -= HandlePatientChanged;
            netOperator.OnValueChanged -= HandleOperatorChanged;

            // Non lasciare mai il giocatore locale bloccato se il letto sparisce.
            if (_localRole == LocalRole.Operator)
            {
                if (treatmentMinigame != null) treatmentMinigame.Interrupt();
                EndOperatingLocal(notifyServer: false);
            }
            else if (_localRole == LocalRole.Patient)
            {
                ExitLyingLocal();
            }
        }

        private bool IsConfigured =>
            config != null && treatmentMinigame != null && patientViewPoint != null;

        // ── Update: cooldown, tick server, Cancel locale ────────────────────────

        private void Update()
        {
            if (_cooldown > 0f) _cooldown -= Time.deltaTime;

            if (IsServer && IsSpawned)
                ServerTick(Time.deltaTime);

            if (_localRole == LocalRole.None || _localCancel == null) return;
            if (!_localCancel.WasPressedThisFrame()) return;

            if (_localRole == LocalRole.Patient)
            {
                // Alzarsi: lo decide il server; il ripristino arriva da OnValueChanged.
                if (!_leaveRequested)
                {
                    _leaveRequested = true;
                    RequestLeaveRpc();
                }
            }
            else if (_localRole == LocalRole.Operator)
            {
                // Uscita dal trattamento: Interrupt → OnMinigameInterrupted → EndOperatingLocal.
                if (treatmentMinigame != null) treatmentMinigame.Interrupt();
                EndOperatingLocal(notifyServer: true);   // idempotente: copre il minigame già chiuso
            }
        }

        // ── Server: validazione occupazione + auto-cura (Q5-a) ─────────────────

        private void ServerTick(float deltaTime)
        {
            ulong patient = netPatient.Value;
            if (patient == NoClient)
            {
                _autoHealAccumulator = 0f;
                return;
            }

            // Paziente a terra o disconnesso → letto liberato (chiude anche la sessione).
            if (!TryGetAliveHealth(patient, out PlayerHealthSystem patientHealth))
            {
                LogV($"[RecoveryBed] {name}: paziente {patient} non più Alive/connesso → letto liberato.");
                ServerReleasePatient();
                return;
            }

            ulong operatorId = netOperator.Value;
            if (operatorId != NoClient)
            {
                if (!TryGetAliveHealth(operatorId, out _))
                {
                    LogV($"[RecoveryBed] {name}: operatore {operatorId} non più Alive/connesso → trattamento chiuso.");
                    netOperator.Value = NoClient;
                }
                else
                {
                    // Q5-a: l'auto-cura è sospesa mentre qualcuno sta curando.
                    _autoHealAccumulator = 0f;
                    return;
                }
            }

            if (config == null) return;

            MedbayConfig.TierData tier = config.GetTier(MedbaySystem.CurrentTierOrDefault);
            float interval = config.AutoHealTickInterval;

            _autoHealAccumulator += deltaTime;
            while (_autoHealAccumulator >= interval)
            {
                _autoHealAccumulator -= interval;

                float cap = patientHealth.MaxHP * tier.AutoHealCapFraction;
                float room = cap - patientHealth.CurrentHP;
                if (room <= 0f)
                {
                    _autoHealAccumulator = 0f;   // già al tetto: niente da accumulare
                    break;
                }

                patientHealth.ApplyHeal(Mathf.Min(tier.AutoHealPerSecond * interval, room));
            }
        }

        private void ServerReleasePatient()
        {
            if (!IsServer) return;
            netOperator.Value = NoClient;
            netPatient.Value = NoClient;
            _autoHealAccumulator = 0f;
        }

        private static bool TryGetAliveHealth(ulong clientId, out PlayerHealthSystem health)
        {
            health = null;
            if (clientId == NoClient) return false;
            return PlayerHealthSystem.TryGetByClientId(clientId, out health)
                   && health != null
                   && health.IsAlive;
        }

        // ── RPC verso il server (mittente = SenderClientId, mai dal payload) ──

        [Rpc(SendTo.Server)]
        private void RequestLieDownRpc(RpcParams rpcParams = default)
        {
            if (!IsServer) return;
            ulong sender = rpcParams.Receive.SenderClientId;

            if (!IsConfigured) return;
            if (netPatient.Value != NoClient)
            {
                LogV($"[RecoveryBed] {name}: sdraio rifiutato a {sender} — letto occupato.");
                return;
            }
            if (!TryGetAliveHealth(sender, out PlayerHealthSystem health)) return;
            if (health.CurrentHP >= health.MaxHP)
            {
                LogV($"[RecoveryBed] {name}: sdraio rifiutato a {sender} — HP al massimo.");
                return;
            }

            netPatient.Value = sender;
            _autoHealAccumulator = 0f;
        }

        [Rpc(SendTo.Server)]
        private void RequestLeaveRpc(RpcParams rpcParams = default)
        {
            if (!IsServer) return;
            if (rpcParams.Receive.SenderClientId != netPatient.Value) return;
            ServerReleasePatient();
        }

        [Rpc(SendTo.Server)]
        private void RequestTreatRpc(RpcParams rpcParams = default)
        {
            if (!IsServer) return;
            ulong sender = rpcParams.Receive.SenderClientId;
            ulong patient = netPatient.Value;

            if (!IsConfigured) return;
            if (patient == NoClient || sender == patient) return;
            if (netOperator.Value != NoClient)
            {
                LogV($"[RecoveryBed] {name}: trattamento rifiutato a {sender} — operatore già presente.");
                return;
            }
            if (!TryGetAliveHealth(sender, out _)) return;
            if (!TryGetAliveHealth(patient, out PlayerHealthSystem patientHealth)) return;
            if (patientHealth.CurrentHP >= patientHealth.MaxHP) return;

            netOperator.Value = sender;
            _autoHealAccumulator = 0f;
        }

        [Rpc(SendTo.Server)]
        private void EndTreatmentRpc(RpcParams rpcParams = default)
        {
            if (!IsServer) return;
            if (rpcParams.Receive.SenderClientId != netOperator.Value) return;
            netOperator.Value = NoClient;
            _autoHealAccumulator = 0f;
        }

        /// <summary>
        /// Effetto di soglia del trattamento (Q3-a). Chiamato dal minigame medico sul
        /// client dell'operatore, eseguito sul server. Porta gli HP del paziente ad
        /// ALMENO progressPct% di maxHP: idempotente, un duplicato non cura due volte.
        /// Accettato solo dall'operatore corrente. A T1 cura solo HP.
        /// </summary>
        [Rpc(SendTo.Server)]
        public void ApplyTreatmentThresholdRpc(float progressPct, RpcParams rpcParams = default)
        {
            if (!IsServer) return;
            ulong sender = rpcParams.Receive.SenderClientId;

            if (sender != netOperator.Value)
            {
                LogV($"[RecoveryBed] {name}: soglia {progressPct:F0}% ignorata — {sender} non è l'operatore.");
                return;
            }
            if (!TryGetAliveHealth(netPatient.Value, out PlayerHealthSystem patientHealth)) return;

            float target = patientHealth.MaxHP * Mathf.Clamp01(progressPct / 100f);
            float missing = target - patientHealth.CurrentHP;
            if (missing <= 0f) return;

            float healed = patientHealth.ApplyHeal(missing);
            LogV($"[RecoveryBed] {name}: soglia {progressPct:F0}% → +{healed:F1} HP " +
                 $"(paziente {netPatient.Value}, ora {patientHealth.CurrentHP:F0}/{patientHealth.MaxHP:F0}).");
        }

        // ── Reazione locale ai cambi di occupazione (tutti i client) ───────────

        private void HandlePatientChanged(ulong previous, ulong current)
        {
            ApplyOccupiedVisual(current);

            ulong me = NetworkManager.LocalClientId;
            if (current == me && _localRole == LocalRole.None)
                EnterLyingLocal();
            else if (previous == me && current != me && _localRole == LocalRole.Patient)
                ExitLyingLocal();
        }

        private void HandleOperatorChanged(ulong previous, ulong current)
        {
            ulong me = NetworkManager.LocalClientId;
            if (current == me && _localRole == LocalRole.None)
            {
                BeginOperatingLocal();
            }
            else if (previous == me && current != me && _localRole == LocalRole.Operator)
            {
                // Fine decisa dal server (paziente alzato o espulso, operatore a terra):
                // chiude la UI senza rimandare nulla al server.
                if (treatmentMinigame != null) treatmentMinigame.Interrupt();
                EndOperatingLocal(notifyServer: false);
            }
        }

        private void ApplyOccupiedVisual(ulong patient)
        {
            if (occupiedVisual == null) return;
            bool visible = patient != NoClient
                           && (NetworkManager == null || patient != NetworkManager.LocalClientId);
            occupiedVisual.SetActive(visible);
        }

        // ── Paziente (locale) ──────────────────────────────────────────────────

        private void EnterLyingLocal()
        {
            if (!EnsureLocalRig() || patientViewPoint == null)
            {
                Debug.LogError($"[RecoveryBed] {name}: impossibile sdraiare il giocatore locale " +
                               "(player o PatientViewPoint mancanti) — letto liberato.");
                if (!_despawning && IsSpawned) RequestLeaveRpc();
                return;
            }

            _localRole = LocalRole.Patient;
            _leaveRequested = false;

            _savedCameraLocalPosition = _localCamera.localPosition;
            _savedCameraLocalRotation = _localCamera.localRotation;

            _localController.enabled = false;
            _localCamera.SetPositionAndRotation(patientViewPoint.position, patientViewPoint.rotation);

            if (_localTablet != null) _localTablet.SetOpenBlocked(true);
        }

        private void ExitLyingLocal()
        {
            if (_localRole != LocalRole.Patient) return;

            _localRole = LocalRole.None;
            _leaveRequested = false;
            _cooldown = exitCooldown;

            if (_localCamera != null)
            {
                _localCamera.localPosition = _savedCameraLocalPosition;
                _localCamera.localRotation = _savedCameraLocalRotation;
            }

            RestoreLocalController();
            if (_localTablet != null) _localTablet.SetOpenBlocked(false);
        }

        // ── Operatore (locale) ─────────────────────────────────────────────────

        private void BeginOperatingLocal()
        {
            if (!EnsureLocalRig() || treatmentMinigame == null)
            {
                Debug.LogError($"[RecoveryBed] {name}: impossibile aprire il trattamento " +
                               "(player o minigame mancanti) — sessione chiusa.");
                if (!_despawning && IsSpawned) EndTreatmentRpc();
                return;
            }

            _localRole = LocalRole.Operator;
            _localController.enabled = false;
            if (_localTablet != null) _localTablet.SetOpenBlocked(true);

            treatmentMinigame.Open(this, config, BuildPatientLabel(),
                                   OnMinigameComplete, OnMinigameInterrupted);
        }

        private void OnMinigameComplete() => EndOperatingLocal(notifyServer: true);

        private void OnMinigameInterrupted() => EndOperatingLocal(notifyServer: true);

        /// <summary>
        /// Ripristino locale dell'operatore. Idempotente (guard sul ruolo locale).
        /// Avvisa il server solo se il server lo considera ancora operatore.
        /// </summary>
        private void EndOperatingLocal(bool notifyServer)
        {
            if (_localRole != LocalRole.Operator) return;

            _localRole = LocalRole.None;
            _cooldown = exitCooldown;

            RestoreLocalController();
            if (_localTablet != null) _localTablet.SetOpenBlocked(false);

            if (notifyServer && !_despawning && IsSpawned
                && NetworkManager != null && netOperator.Value == NetworkManager.LocalClientId)
                EndTreatmentRpc();
        }

        // ── Rig del giocatore locale ───────────────────────────────────────────

        /// <summary>
        /// Risolve e mette in cache i componenti del player locale (una volta per
        /// istanza di PlayerHealthSystem.LocalInstance, non a ogni frame).
        /// </summary>
        private bool EnsureLocalRig()
        {
            PlayerHealthSystem me = PlayerHealthSystem.LocalInstance;
            if (me == null)
            {
                _localHealth = null;
                return false;
            }

            if (me != _localHealth)
            {
                _localHealth = me;
                GameObject player = me.gameObject;

                _localController = player.GetComponent<PlayerController>();
                Camera cam = player.GetComponentInChildren<Camera>();
                _localCamera = cam != null ? cam.transform : null;
                _localTablet = player.GetComponent<TabletStation>();

                // Cancel via PlayerInput del player (mai hardcodato).
                PlayerInput playerInput = player.GetComponent<PlayerInput>();
                _localCancel = playerInput != null && playerInput.actions != null
                    ? playerInput.actions.FindAction("Cancel", throwIfNotFound: false)
                    : null;
            }

            return _localController != null && _localCamera != null;
        }

        private void RestoreLocalController()
        {
            if (_localController == null) return;

            // PlayerController non azzera la velocità da disabilitato (stesso fix delle postazioni).
            _localController.ResetVelocity();

            // Mai riattivare il movimento di un giocatore a terra: il freeze Downed di
            // PlayerHealthSystem resta l'autorità.
            _localController.enabled = _localHealth == null || _localHealth.IsAlive;
        }

        private string BuildPatientLabel()
        {
            string role = CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(netPatient.Value));
            return $"Paziente · {role}";
        }

        // ── IInteractable ──────────────────────────────────────────────────────

        /// <summary>
        /// Letto vuoto: "Sdraiati" se il giocatore locale è ferito. Letto occupato da un
        /// altro: "Cura" se nessuno sta già curando e il paziente è ferito. Mai durante
        /// un uso in corso, a tablet aperto o nel cooldown.
        /// </summary>
        public bool CanInteract()
        {
            if (!IsSpawned || !IsConfigured) return false;
            if (_cooldown > 0f || _localRole != LocalRole.None) return false;
            if (!EnsureLocalRig()) return false;
            if (!_localHealth.IsAlive) return false;
            if (_localTablet != null && _localTablet.IsBusy) return false;

            ulong patient = netPatient.Value;
            if (patient == NoClient)
                return _localHealth.CurrentHP < _localHealth.MaxHP;

            if (patient == NetworkManager.LocalClientId) return false;
            if (netOperator.Value != NoClient) return false;

            return TryGetAliveHealth(patient, out PlayerHealthSystem patientHealth)
                   && patientHealth.CurrentHP < patientHealth.MaxHP;
        }

        public string GetInteractionPrompt()
        {
            ulong patient = netPatient.Value;
            if (patient == NoClient) return lieDownPrompt;

            string role = CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(patient));
            return (treatPromptFormat ?? string.Empty).Replace("{0}", role);
        }

        public void Interact(GameObject interactor)
        {
            if (!CanInteract()) return;

            _cooldown = requestCooldown;
            if (netPatient.Value == NoClient) RequestLieDownRpc();
            else RequestTreatRpc();
        }

        public bool IsContinuousInteraction() => false;
        public void OnLookEnter() { }
        public void OnLookExit() { }

        // ── Debug ──────────────────────────────────────────────────────────────

        private void LogV(string msg) { if (logVerbose) Debug.Log(msg); }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showDebugUI) return;
            if (!IsServer || !IsSpawned) return;

            ulong patient = netPatient.Value;
            ulong operatorId = netOperator.Value;

            string patientLine = "Paziente: —";
            string healLine = "Auto-cura: —";
            if (patient != NoClient)
            {
                string hp = PlayerHealthSystem.TryGetByClientId(patient, out PlayerHealthSystem h) && h != null
                    ? $"{h.CurrentHP:F0}/{h.MaxHP:F0}"
                    : "?";
                patientLine = $"Paziente: client {patient} ({CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(patient))}) · HP {hp}";

                if (operatorId != NoClient)
                {
                    healLine = "Auto-cura: sospesa (trattamento)";
                }
                else if (config != null && h != null)
                {
                    float cap = h.MaxHP * config.GetTier(MedbaySystem.CurrentTierOrDefault).AutoHealCapFraction;
                    healLine = $"Auto-cura: attiva → tetto {cap:F0} HP";
                }
            }

            string operatorLine = operatorId != NoClient
                ? $"Operatore: client {operatorId} ({CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(operatorId))})"
                : "Operatore: —";

            GUILayout.BeginArea(new Rect(600, 10, 340, 118));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[RecoveryBed] {name} · T{MedbaySystem.CurrentTierOrDefault}");
            GUILayout.Label(patientLine);
            GUILayout.Label(operatorLine);
            GUILayout.Label(healLine);
            if (GUILayout.Button("Espelli paziente")) ServerReleasePatient();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}
