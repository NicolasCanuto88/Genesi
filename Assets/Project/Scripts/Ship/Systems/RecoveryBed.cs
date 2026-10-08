using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// RecoveryBed — letto della Recovery Bay (Rev BO-a · Rev BO-b · Fase 3a Corpsman · M4.2).
    /// Coordinatore server del trattamento medico: fratello di RepairPanel /
    /// StabilizationPanel, ma con OCCUPAZIONE DI RETE (i pannelli non ce l'hanno).
    ///
    /// FLUSSO:
    ///   1. Un giocatore ferito (Alive, HP &lt; max) interagisce → "Sdraiati".
    ///      Il server lo accetta come paziente (netPatient). Il suo client blocca il
    ///      movimento e porta la CAMERA sul patientViewPoint.
    ///   2. Senza trattamento in corso il letto cura da solo fino al tetto del tier
    ///      (T1: 50% di maxHP — Q5-a, valori da MedbayConfig).
    ///   3. Rev BO-b (Q8/Q9): il trattamento si avvia dalla CONSOLE (MedicalStation),
    ///      non più dal letto. Chi siede alla postazione chiama RequestTreatment(); il
    ///      server lo accetta come operatore (netOperator) e il letto pubblica
    ///      OperatorChanged: la console apre il minigame sul proprio monitor. Il letto
    ///      è il "robot chirurgico", la console lo teleopera. Durante la sessione
    ///      l'auto-cura è sospesa.
    ///   4. Ogni soglia del minigame chiama ApplyTreatmentThresholdRpc: il server porta
    ///      gli HP del paziente ad ALMENO soglia% di maxHP (Q3-a, idempotente).
    ///   5. Il paziente si alza con Cancel. Le soglie già raggiunte restano.
    ///
    /// FASI (Rev BP-b · Q20-a / Q21-a / Q22-a): una sessione di trattamento cura UNA
    /// condizione (TreatmentPhase). Ordine proposto dalla console: prima gli stati
    /// (Veleno → Radiazioni → Ferite Composte), poi gli HP (Trauma) — fermare il DoT
    /// prima di curare. Il server accetta qualsiasi fase CURABILE ADESSO (l'ordine è
    /// della console, non una regola), la memorizza per la sessione e al 100% di una
    /// fase stato chiama PlayerStatusEffects.TryCure. Soglie 50/75 di una fase stato:
    /// solo ritmo e tacche, nessun effetto. Fase Trauma: soglie come in BO-a.
    /// Curabilità di uno stato: tier con CuresStatuses (MedbayConfig: T2+) E regola dello
    /// stato (Ferite Composte T3+). Nessun vincolo sulla composizione della crew
    /// (Q25-b): chiunque può operare, col malus Rev U se non è Corpsman.
    ///
    /// TRATTAMENTO UNICO (Rev BU-a · Q89-a): con il flag TreatsAllInOneSession del tier
    /// (MedbayConfig: T4) e almeno DUE cose da curare (stati curabili a questo tier e/o HP
    /// sotto il massimo), la prossima fase è AllConditions: una sola sessione. Soglie
    /// 50/75/100 sugli HP come la fase Trauma; al 100% prima si curano TUTTI gli stati
    /// curabili, poi gli HP vanno al 100% del massimo, che può essere appena risalito
    /// (Ferite Composte curate: Rev BU-a). Con una sola cosa da curare resta la fase singola.
    /// Il minigame non cambia: legge solo l'etichetta della fase.
    ///
    /// Sdraiarsi: solo se c'è qualcosa da curare a questo tier (HP sotto il massimo o
    /// uno stato curabile).
    ///
    /// RESPONSABILITÀ (Rev BO-b): autorità server (occupazione, auto-cura, cure di
    /// soglia, regola di disponibilità del trattamento) + lato locale del PAZIENTE.
    /// Il lato locale dell'OPERATORE (PlayerController, tablet, Cancel, UI) vive nella
    /// console: un solo scrittore per ruolo (audit Rev AF/AG — in BO-a letto e
    /// postazione avrebbero scritto entrambi PlayerController.enabled).
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
    /// SCELTE LOCALI DEL PAZIENTE:
    ///   - Solo la CAMERA del paziente si sposta sul letto; il root del player NON si
    ///     muove e il CharacterController NON viene toccato. Motivi: niente teletrasporto
    ///     dentro la geometria, e un paziente che va a terra resta un bersaglio valido
    ///     per il defibrillatore (collider attivo, regola Rev BD). La posa sdraiata vera
    ///     arriverà con il corpo del player (D33).
    ///   - Al ripristino PlayerController torna attivo SOLO se il giocatore è Alive:
    ///     il freeze Downed di PlayerHealthSystem resta l'autorità (l'ordine dei
    ///     callback tra NetworkObject diversi non è garantito).
    ///   - Rev BW-b (Q111-a): prima di salvare e prima di ripristinare la posa della camera si
    ///     chiama CameraShaker.CancelShake, così un urto preso mentre ci si sdraia o ci si alza
    ///     non lascia un residuo di shake sulla camera.
    ///   - Il tablet è bloccato in apertura per tutta la durata (TabletStation.SetOpenBlocked):
    ///     tablet e letto salvano/ripristinano entrambi PlayerController e camera.
    ///     Audit Rev AF/AG, vedi TabletStation.
    ///
    /// ⚠️ SETUP SCENA (guide Editor Rev BO-a / BO-b): GameObject con NetworkObject +
    /// collider sulla layer Interactable + questo componente; figli PatientViewPoint e
    /// OccupiedVisual (senza collider). Il canvas del minigame medico sta sul monitor
    /// della MedicalStation (Rev BO-b), non più qui. Oggetto di scena: nessuna
    /// registrazione in NetworkPrefabs.
    /// </summary>
    public class RecoveryBed : NetworkBehaviour, IInteractable
    {
        /// <summary>Sentinella "nessun client" per paziente e operatore.</summary>
        public const ulong NoClient = ulong.MaxValue;

        /// <summary>
        /// Rev BO-b — esito della regola "questo client può avviare un trattamento?".
        /// UNA sola regola: la usa il server per accettare RequestTreatRpc e la console
        /// per la riga di stato sul monitor (stesse condizioni di BO-a).
        /// </summary>
        public enum TreatmentAvailability
        {
            /// <summary>Letto non spawnato o non configurato.</summary>
            NotReady,
            /// <summary>Nessun paziente (o paziente non più Alive: sta per essere liberato).</summary>
            NoPatient,
            /// <summary>Il candidato è il paziente stesso.</summary>
            SelfIsPatient,
            /// <summary>C'è già un operatore.</summary>
            InProgress,
            /// <summary>Il candidato non è Alive.</summary>
            OperatorUnable,
            /// <summary>Paziente con HP al massimo e nessuno stato: niente da curare.</summary>
            PatientStable,
            /// <summary>
            /// Rev BP-b — HP al massimo, ma con stati che questo tier non cura (per esempio
            /// Veleno a T1, Ferite Composte sotto T3).
            /// </summary>
            NotTreatableAtTier,
            /// <summary>Trattamento avviabile.</summary>
            Ready
        }

        /// <summary>
        /// Rev BP-b — la condizione curata da una sessione di trattamento (Q20-a: una
        /// sessione, una condizione). Byte: viaggia nella RPC di richiesta.
        /// </summary>
        public enum TreatmentPhase : byte
        {
            None = 0,
            /// <summary>HP: soglie 50/75/100 portano gli HP almeno a quella percentuale.</summary>
            Hp = 1,
            Poison = 2,
            Radiation = 3,
            CompoundWounds = 4,
            /// <summary>
            /// Rev BU-a (Q89-a) — trattamento unico da T4: soglie sugli HP come Hp e, al 100%,
            /// cura di tutti gli stati curabili. Proposta solo con almeno due cose da curare.
            /// </summary>
            AllConditions = 5
        }

        /// <summary>Q21-a — ordine delle fasi stato proposto dalla console (prima gli stati, poi gli HP).</summary>
        private static readonly TreatmentPhase[] StatusPhaseOrder =
        {
            TreatmentPhase.Poison,
            TreatmentPhase.Radiation,
            TreatmentPhase.CompoundWounds
        };

        [Header("Config")]
        [Tooltip("Asset MedbayConfig: tetto e velocità dell'auto-cura per tier, parametri del " +
                 "trattamento, malus Rev U. Obbligatorio.")]
        [SerializeField] private MedbayConfig config;

        [Header("Paziente")]
        [Tooltip("Posa della CAMERA del paziente sdraiato: posizione poco sopra il cuscino, " +
                 "asse Z (blu) verso i piedi del letto e la stanza (−30° circa). Il root del player non si sposta.")]
        [SerializeField] private Transform patientViewPoint;

        [Header("Visibilità agli altri (Q1.v-a)")]
        [Tooltip("Placeholder 'letto occupato' (es. capsula sdraiata, SENZA collider: un collider " +
                 "sulla layer Interactable ferma il raggio di InteractionSystem). Acceso su tutti i " +
                 "client quando c'è un paziente, tranne che per il paziente stesso. Sostituito dal " +
                 "corpo del player quando arriverà D33.")]
        [SerializeField] private GameObject occupiedVisual;

        [Header("Prompt")]
        [SerializeField] private string lieDownPrompt = "[{interact}] Lie down";   // Rev BT-c (Q75-a)

        [Header("Tempi")]
        [Tooltip("Pausa dopo una richiesta al server, per non inviarne una a ogni pressione.")]
        [SerializeField] private float requestCooldown = 0.5f;
        [Tooltip("Pausa dopo l'uscita dal letto: evita di risdraiarsi subito con la stessa pressione.")]
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

        /// <summary>Rev BO-b — config del letto, passata dalla console al minigame.</summary>
        public MedbayConfig Config => config;

        /// <summary>Rev BO-b — etichetta del paziente per l'header del minigame (ruolo, non nome).</summary>
        public string PatientLabel => BuildPatientLabel();

        /// <summary>
        /// Rev BO-b — (precedente, attuale) a ogni cambio dell'operatore, su TUTTI i client.
        /// La console lo usa per aprire/chiudere la sessione locale. Invocato anche allo
        /// despawn del letto se c'era un operatore (attuale = NoClient), così nessuna UI
        /// resta aperta.
        /// </summary>
        public event Action<ulong, ulong> OperatorChanged;

        // ── Stato server ──
        private float _autoHealAccumulator;
        private TreatmentPhase _sessionPhase;   // Rev BP-b: fase accettata per la sessione in corso

        // ── Stato locale (client che è paziente di questo letto) ──
        private bool _localIsPatient;
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
            if (patientViewPoint == null)
                Debug.LogError($"[RecoveryBed] {name}: PatientViewPoint non assegnato — letto disattivato.");
        }

        public override void OnNetworkDespawn()
        {
            _despawning = true;
            netPatient.OnValueChanged -= HandlePatientChanged;
            netOperator.OnValueChanged -= HandleOperatorChanged;

            // Non lasciare mai UI o giocatori bloccati se il letto sparisce.
            // La console chiude la propria sessione; nessuna RPC parte (flag _despawning).
            ulong operatorId = netOperator.Value;
            if (operatorId != NoClient)
                OperatorChanged?.Invoke(operatorId, NoClient);

            if (_localIsPatient)
                ExitLyingLocal();
        }

        private bool IsConfigured => config != null && patientViewPoint != null;

        // ── Update: cooldown, tick server, Cancel del paziente ─────────────────

        private void Update()
        {
            if (_cooldown > 0f) _cooldown -= Time.deltaTime;

            if (IsServer && IsSpawned)
                ServerTick(Time.deltaTime);

            if (!_localIsPatient || _localCancel == null) return;
            if (!_localCancel.WasPressedThisFrame()) return;

            // Alzarsi: lo decide il server; il ripristino arriva da OnValueChanged.
            if (!_leaveRequested)
            {
                _leaveRequested = true;
                RequestLeaveRpc();
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
                    ServerClearOperator();
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
            ServerClearOperator();
            netPatient.Value = NoClient;
            _autoHealAccumulator = 0f;
        }

        /// <summary>Chiude la sessione lato server: nessun operatore, nessuna fase.</summary>
        private void ServerClearOperator()
        {
            if (!IsServer) return;
            netOperator.Value = NoClient;
            _sessionPhase = TreatmentPhase.None;
        }

        private static bool TryGetAliveHealth(ulong clientId, out PlayerHealthSystem health)
        {
            health = null;
            if (clientId == NoClient) return false;
            return PlayerHealthSystem.TryGetByClientId(clientId, out health)
                   && health != null
                   && health.IsAlive;
        }

        // ── Regola di disponibilità del trattamento (server + console) ─────────

        /// <summary>
        /// Rev BO-b — il candidato può avviare un trattamento adesso? Stesse condizioni
        /// che in BO-a stavano dentro RequestTreatRpc. Sul server decide; sulla console
        /// (client) serve solo a scegliere il testo di stato: l'ultima parola resta al
        /// server, che la ricalcola alla ricezione della RPC.
        /// </summary>
        public TreatmentAvailability GetTreatmentAvailability(ulong candidateClientId)
        {
            if (!IsSpawned || !IsConfigured) return TreatmentAvailability.NotReady;

            ulong patient = netPatient.Value;
            if (patient == NoClient) return TreatmentAvailability.NoPatient;
            if (candidateClientId == patient) return TreatmentAvailability.SelfIsPatient;
            if (netOperator.Value != NoClient) return TreatmentAvailability.InProgress;
            if (!TryGetAliveHealth(candidateClientId, out _)) return TreatmentAvailability.OperatorUnable;
            if (!TryGetAliveHealth(patient, out _))
                return TreatmentAvailability.NoPatient;
            if (GetNextPhase(patient) == TreatmentPhase.None)
                return HasActiveStatus(patient)
                    ? TreatmentAvailability.NotTreatableAtTier
                    : TreatmentAvailability.PatientStable;

            return TreatmentAvailability.Ready;
        }

        // ── Fasi (Rev BP-b) — stesse regole su server e client ─────────────────

        /// <summary>
        /// Prossima fase da curare per il client indicato, nell'ordine Q21-a: prima gli
        /// stati curabili a questo tier, poi gli HP. None se non c'è niente da curare (o
        /// il client non è Alive). Rev BU-a: con il trattamento unico del tier e almeno due
        /// cose da curare, AllConditions. Legge solo stato replicato: vale su server e client.
        /// </summary>
        public TreatmentPhase GetNextPhase(ulong patientClientId)
        {
            if (!TryGetAliveHealth(patientClientId, out PlayerHealthSystem health)) return TreatmentPhase.None;

            if (IsAllConditionsTreatable(patientClientId, health))
                return TreatmentPhase.AllConditions;

            for (int i = 0; i < StatusPhaseOrder.Length; i++)
            {
                if (IsStatusPhaseTreatable(patientClientId, StatusPhaseOrder[i]))
                    return StatusPhaseOrder[i];
            }

            return health.CurrentHP < health.MaxHP ? TreatmentPhase.Hp : TreatmentPhase.None;
        }

        /// <summary>
        /// La fase indicata è curabile ADESSO sul client indicato? Il server la usa per
        /// validare la richiesta della console (l'ordine resta della console).
        /// </summary>
        public bool IsPhaseTreatable(ulong patientClientId, TreatmentPhase phase)
        {
            if (!TryGetAliveHealth(patientClientId, out PlayerHealthSystem health)) return false;
            if (phase == TreatmentPhase.Hp) return health.CurrentHP < health.MaxHP;
            if (phase == TreatmentPhase.AllConditions) return IsAllConditionsTreatable(patientClientId, health);
            return IsStatusPhaseTreatable(patientClientId, phase);
        }

        /// <summary>
        /// Rev BU-a (Q89-a) — trattamento unico possibile: il tier lo prevede (MedbayConfig) e
        /// le cose da curare sono almeno due (stati curabili a questo tier, più gli HP se sotto
        /// il massimo). Con una sola cosa da curare resta la fase singola.
        /// </summary>
        private bool IsAllConditionsTreatable(ulong patientClientId, PlayerHealthSystem health)
        {
            if (config == null || health == null) return false;
            if (!config.GetTier(MedbaySystem.CurrentTierOrDefault).TreatsAllInOneSession) return false;

            int count = health.CurrentHP < health.MaxHP ? 1 : 0;
            for (int i = 0; i < StatusPhaseOrder.Length; i++)
            {
                if (IsStatusPhaseTreatable(patientClientId, StatusPhaseOrder[i]))
                    count++;
            }
            return count >= 2;
        }

        /// <summary>
        /// Fase stato curabile: il tier corrente cura gli stati (MedbayConfig), lo stato è
        /// attivo (maschera replicata) e la sua regola ammette questo tier (Ferite Composte T3+).
        /// </summary>
        private bool IsStatusPhaseTreatable(ulong patientClientId, TreatmentPhase phase)
        {
            if (config == null) return false;
            if (!TryGetStatus(phase, out StatusEffectType status)) return false;

            int tier = MedbaySystem.CurrentTierOrDefault;
            if (!config.GetTier(tier).CuresStatuses) return false;

            return PlayerStatusEffects.TryGetByClientId(patientClientId, out PlayerStatusEffects effects)
                   && effects != null
                   && effects.IsActive(status)
                   && effects.IsCurableAtMedbay(status, tier);
        }

        /// <summary>
        /// true se il paziente ha almeno una CONDIZIONE attiva. Rev BR: i buff (droghe) non
        /// contano — un paziente sano con una Combat Stim è "stabile", non "non curabile".
        /// </summary>
        private static bool HasActiveStatus(ulong clientId)
        {
            return PlayerStatusEffects.TryGetByClientId(clientId, out PlayerStatusEffects effects)
                   && effects != null
                   && effects.ConditionMask != 0;
        }

        /// <summary>Stato di alterazione curato da una fase (false per None e Hp).</summary>
        public static bool TryGetStatus(TreatmentPhase phase, out StatusEffectType status)
        {
            switch (phase)
            {
                case TreatmentPhase.Poison: status = StatusEffectType.Poison; return true;
                case TreatmentPhase.Radiation: status = StatusEffectType.Radiation; return true;
                case TreatmentPhase.CompoundWounds: status = StatusEffectType.CompoundWounds; return true;
                default: status = default; return false;
            }
        }

        /// <summary>Etichetta della fase per monitor e minigame (testi nuovi in inglese).</summary>
        public static string PhaseLabel(TreatmentPhase phase)
        {
            switch (phase)
            {
                case TreatmentPhase.Hp: return "TRAUMA";
                case TreatmentPhase.Poison: return "POISON";
                case TreatmentPhase.Radiation: return "RADIATION";
                case TreatmentPhase.CompoundWounds: return "COMPOUND WOUNDS";
                case TreatmentPhase.AllConditions: return "ALL CONDITIONS";   // Rev BU-a
                default: return "—";
            }
        }

        // ── API client della console (Rev BO-b) ────────────────────────────────

        /// <summary>
        /// Chiede al server di diventare operatore per la fase indicata (Rev BP-b). L'esito
        /// arriva come OperatorChanged (accettata: la sessione cura QUELLA fase) oppure non
        /// arriva (rifiutata: la console resta sul dashboard).
        /// </summary>
        public bool RequestTreatment(TreatmentPhase phase)
        {
            if (!IsSpawned || _despawning || phase == TreatmentPhase.None) return false;
            RequestTreatRpc(phase);
            return true;
        }

        /// <summary>
        /// Chiude il trattamento lato server. Invia la RPC solo se il server considera
        /// ancora questo client l'operatore: dopo una fine decisa dal server non parte nulla.
        /// </summary>
        public void EndTreatment()
        {
            if (!IsSpawned || _despawning || NetworkManager == null) return;
            if (netOperator.Value != NetworkManager.LocalClientId) return;
            EndTreatmentRpc();
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
            if (!TryGetAliveHealth(sender, out _)) return;
            if (GetNextPhase(sender) == TreatmentPhase.None)
            {
                LogV($"[RecoveryBed] {name}: sdraio rifiutato a {sender} — niente da curare a questo tier.");
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
        private void RequestTreatRpc(TreatmentPhase phase, RpcParams rpcParams = default)
        {
            if (!IsServer) return;
            ulong sender = rpcParams.Receive.SenderClientId;

            TreatmentAvailability availability = GetTreatmentAvailability(sender);
            if (availability != TreatmentAvailability.Ready)
            {
                LogV($"[RecoveryBed] {name}: trattamento rifiutato a {sender} — {availability}.");
                return;
            }
            if (!IsPhaseTreatable(netPatient.Value, phase))
            {
                LogV($"[RecoveryBed] {name}: trattamento rifiutato a {sender} — fase {phase} non curabile adesso.");
                return;
            }

            _sessionPhase = phase;
            netOperator.Value = sender;
            _autoHealAccumulator = 0f;
            LogV($"[RecoveryBed] {name}: operatore {sender}, fase {phase}.");
        }

        [Rpc(SendTo.Server)]
        private void EndTreatmentRpc(RpcParams rpcParams = default)
        {
            if (!IsServer) return;
            if (rpcParams.Receive.SenderClientId != netOperator.Value) return;
            ServerClearOperator();
            _autoHealAccumulator = 0f;
        }

        /// <summary>
        /// Effetto di soglia del trattamento. Chiamato dal minigame medico sul client
        /// dell'operatore a ogni soglia, eseguito sul server. L'effetto dipende dalla fase
        /// della sessione (Rev BP-b), decisa dal server all'accettazione:
        ///   - Hp (Q3-a): porta gli HP ad ALMENO progressPct% di maxHP;
        ///   - fase stato (Q22-a): solo al 100%, PlayerStatusEffects.TryCure;
        ///   - AllConditions (Rev BU-a · Q89-a): come Hp, e al 100% prima cura tutti gli stati.
        /// Idempotente in entrambi i casi (un duplicato non cura due volte). Accettato solo
        /// dall'operatore corrente.
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

            ulong patient = netPatient.Value;
            if (!TryGetAliveHealth(patient, out PlayerHealthSystem patientHealth)) return;

            if (_sessionPhase == TreatmentPhase.Hp)
            {
                ServerApplyHpThreshold(patientHealth, progressPct);
                return;
            }

            // Rev BU-a (Q89-a): trattamento unico. Al 100% prima gli stati (curare le Ferite
            // Composte fa risalire l'HP max), poi gli HP al 100% del massimo aggiornato.
            if (_sessionPhase == TreatmentPhase.AllConditions)
            {
                if (progressPct >= 100f)
                    ServerCureAllStatuses(patient);
                ServerApplyHpThreshold(patientHealth, progressPct);
                return;
            }

            if (!TryGetStatus(_sessionPhase, out StatusEffectType status))
            {
                LogV($"[RecoveryBed] {name}: soglia {progressPct:F0}% ignorata — nessuna fase in corso.");
                return;
            }

            // Q22-a: una fase stato cura solo al 100%. 50/75 cambiano solo ritmo e tacche.
            if (progressPct < 100f)
            {
                LogV($"[RecoveryBed] {name}: soglia {progressPct:F0}% (fase {_sessionPhase}) — nessun effetto.");
                return;
            }

            int tier = MedbaySystem.CurrentTierOrDefault;
            if (config == null || !config.GetTier(tier).CuresStatuses)
            {
                LogV($"[RecoveryBed] {name}: T{tier} non cura gli stati — {status} resta.");
                return;
            }
            if (!PlayerStatusEffects.TryGetByClientId(patient, out PlayerStatusEffects effects) || effects == null)
                return;

            bool cured = effects.TryCure(status, tier);
            LogV($"[RecoveryBed] {name}: fase {_sessionPhase} al 100% → {(cured ? "curato" : "non curato")} " +
                 $"(paziente {patient}, T{tier}).");
        }

        /// <summary>Fase Hp (Q3-a): HP del paziente ad ALMENO progressPct% di maxHP. SERVER.</summary>
        private void ServerApplyHpThreshold(PlayerHealthSystem patientHealth, float progressPct)
        {
            float target = patientHealth.MaxHP * Mathf.Clamp01(progressPct / 100f);
            float missing = target - patientHealth.CurrentHP;
            if (missing <= 0f) return;

            float healed = patientHealth.ApplyHeal(missing);
            LogV($"[RecoveryBed] {name}: soglia {progressPct:F0}% → +{healed:F1} HP " +
                 $"(paziente {netPatient.Value}, ora {patientHealth.CurrentHP:F0}/{patientHealth.MaxHP:F0}).");
        }

        /// <summary>
        /// Rev BU-a (Q89-a) — cura tutti gli stati curabili ADESSO sul paziente (stesse regole
        /// delle fasi singole: tier che cura gli stati, stato attivo, regola dello stato).
        /// Idempotente: uno stato già curato viene saltato. SERVER.
        /// </summary>
        private void ServerCureAllStatuses(ulong patient)
        {
            if (!PlayerStatusEffects.TryGetByClientId(patient, out PlayerStatusEffects effects) || effects == null)
                return;

            int tier = MedbaySystem.CurrentTierOrDefault;
            for (int i = 0; i < StatusPhaseOrder.Length; i++)
            {
                TreatmentPhase phase = StatusPhaseOrder[i];
                if (!IsStatusPhaseTreatable(patient, phase)) continue;
                if (!TryGetStatus(phase, out StatusEffectType status)) continue;

                bool cured = effects.TryCure(status, tier);
                LogV($"[RecoveryBed] {name}: ALL CONDITIONS al 100% → {status} {(cured ? "curato" : "non curato")} " +
                     $"(paziente {patient}, T{tier}).");
            }
        }

        // ── Reazione locale ai cambi di occupazione (tutti i client) ───────────

        private void HandlePatientChanged(ulong previous, ulong current)
        {
            ApplyOccupiedVisual(current);

            ulong me = NetworkManager.LocalClientId;
            if (current == me && !_localIsPatient)
                EnterLyingLocal();
            else if (previous == me && current != me && _localIsPatient)
                ExitLyingLocal();
        }

        private void HandleOperatorChanged(ulong previous, ulong current)
        {
            // Rev BO-b: il letto non gestisce più il lato locale dell'operatore.
            // Lo pubblica e basta: la console (MedicalStation) apre o chiude la sessione.
            OperatorChanged?.Invoke(previous, current);
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

            _localIsPatient = true;
            _leaveRequested = false;

            // Rev BW-b (Q111-a): la posa salvata non deve contenere il residuo di uno shake in corso,
            // altrimenti al ripristino resterebbe sulla camera per sempre.
            CameraShaker.LocalInstance?.CancelShake();

            _savedCameraLocalPosition = _localCamera.localPosition;
            _savedCameraLocalRotation = _localCamera.localRotation;

            _localController.enabled = false;
            _localCamera.SetPositionAndRotation(patientViewPoint.position, patientViewPoint.rotation);

            if (_localTablet != null) _localTablet.SetOpenBlocked(true);
        }

        private void ExitLyingLocal()
        {
            if (!_localIsPatient) return;

            _localIsPatient = false;
            _leaveRequested = false;
            _cooldown = exitCooldown;

            if (_localCamera != null)
            {
                // Rev BW-b (Q111-a): lo shake in corso si chiude prima del ripristino, così i suoi
                // delta successivi non spostano la posa appena ripristinata.
                CameraShaker.LocalInstance?.CancelShake();

                _localCamera.localPosition = _savedCameraLocalPosition;
                _localCamera.localRotation = _savedCameraLocalRotation;
            }

            RestoreLocalController();
            if (_localTablet != null) _localTablet.SetOpenBlocked(false);
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
            return $"Patient · {role}";
        }

        // ── IInteractable ──────────────────────────────────────────────────────

        /// <summary>
        /// Solo "Sdraiati": letto vuoto e giocatore locale con qualcosa da curare a questo
        /// tier (Rev BP-b: HP sotto il massimo o uno stato curabile). Rev BO-b: il
        /// trattamento si avvia dalla console, quindi un letto occupato non offre prompt.
        /// Mai durante un uso in corso, nel cooldown, a tablet aperto o con il movimento
        /// già bloccato da altro (postazione, tablet, a terra).
        /// </summary>
        public bool CanInteract()
        {
            if (!IsSpawned || !IsConfigured) return false;
            if (_cooldown > 0f || _localIsPatient) return false;
            if (netPatient.Value != NoClient) return false;
            if (!EnsureLocalRig()) return false;
            if (!_localHealth.IsAlive) return false;
            if (!_localController.enabled) return false;
            if (_localTablet != null && _localTablet.IsBusy) return false;

            return GetNextPhase(_localHealth.OwnerClientId) != TreatmentPhase.None;
        }

        public string GetInteractionPrompt() => lieDownPrompt;

        public void Interact(GameObject interactor)
        {
            if (!CanInteract()) return;

            _cooldown = requestCooldown;
            RequestLieDownRpc();
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
                ? $"Operatore: client {operatorId} ({CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(operatorId))}) · fase {_sessionPhase}"
                : "Operatore: —";
            string phaseLine = "Prossima fase: —";
            if (patient != NoClient)
            {
                ushort mask = PlayerStatusEffects.TryGetByClientId(patient, out PlayerStatusEffects fx) && fx != null
                    ? fx.ConditionMask
                    : (ushort)0;
                phaseLine = $"Prossima fase: {GetNextPhase(patient)} · maschera condizioni {mask}";
            }

            GUILayout.BeginArea(new Rect(600, 10, 380, 140));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[RecoveryBed] {name} · T{MedbaySystem.CurrentTierOrDefault}");
            GUILayout.Label(patientLine);
            GUILayout.Label(operatorLine);
            GUILayout.Label(phaseLine);
            GUILayout.Label(healLine);
            if (GUILayout.Button("Espelli paziente")) ServerReleasePatient();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}