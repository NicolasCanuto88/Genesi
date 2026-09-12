using UnityEngine;
using Unity.Netcode;
using System;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// OxygenSystem — TANK NAVE (pilastro 1 del sistema O2 tripartito, D26).
    /// Gestisce il livello O2 respirabile della nave, il consumo crew e i trigger di allarme.
    ///
    /// RESPONSABILITÀ:
    ///   - Traccia O2Level (0-100%) come NetworkVariable (server authority)
    ///   - Calcola net O2 = generazione - consumo crew per tick
    ///   - Trigger AlarmSystem.OxygenLow quando O2 &lt; soglia (con isteresi)
    ///   - A O2 = 0 (oltre la grazia): infligge danno da SOFFOCAMENTO alla crew viva
    ///   - Espone eventi per le dashboard UI
    ///
    /// NON sa nulla di PowerManager direttamente. Riceve oxygenGenerationRate via
    /// LifeSupportConsumer.AddGenerationSource() / RemoveGenerationSource().
    ///
    /// ─────────────────────────────────────────────────────────────────────────
    /// CATENA BLACKOUT → O2 → SOFFOCAMENTO (design nominato, D26/Rev BE)
    /// ─────────────────────────────────────────────────────────────────────────
    /// Questo comportamento è EMERGENTE dall'architettura power-consumer, ma è
    /// design INTENZIONALE e ora ha conseguenze reali:
    ///   1. LifeSupportConsumer è un IPowerConsumer (priorità 9).
    ///   2. In BLACKOUT totale, PowerManager.EnterBlackout() stacca tutti i consumer
    ///      con priorità &lt; 10 → Life Support cade → generazione O2 = 0.
    ///      (In load-shedding PARZIALE la priorità 9 protegge Life Support: viene
    ///       staccato per ultimo → di norma l'O2 resta stabile nei brownout.)
    ///   3. Con la generazione a 0, il consumo crew (0.6/min × crew REALE) fa
    ///      scendere l'O2 nave.
    ///   4. A O2 = 0, dopo una grazia (deathCountdownDuration), parte il danno da
    ///      soffocamento su tutta la crew viva → Downed via la catena D27.
    /// Ripristinare la corrente (recovery) prima della fine della grazia riaccende
    /// Life Support → l'O2 risale → il countdown si resetta. È una corsa contro il tempo.
    ///
    /// I TRE POOL O2 NON INTERCOMUNICANO (invariante D26): il tank nave non travasa
    /// O2 col tank personale (PlayerOxygen) né col residuo relitto. Il coupling
    /// power→O2-nave (Life Support) NON viola l'invariante: è su un asse diverso
    /// (un power-consumer alimenta il pool nave), non un travaso tra pool O2.
    ///
    /// CREW COUNT REALE (D26, Q3-a): netCrewCount è ora derivato da
    /// NetworkManager.ConnectedClientsIds (server), reattivo a connect/disconnect.
    /// placeholderCrewCount resta solo come fallback host-editor senza NetworkManager.
    ///
    /// AGGANCIO O2 = 0 → DANNO (D26, Q4-a): l'evento storico OnOxygenDepleted, finora
    /// esposto ma non consumato, è ora cablato al danno via PlayerHealthSystem.ApplyDamage
    /// (reso possibile da D27). Il choke point del danno resta unico (ApplyDamage).
    /// </summary>
    public class OxygenSystem : NetworkBehaviour
    {
        // ===== Singleton & OnInstanceReady =====

        public static OxygenSystem Instance { get; private set; }

        /// <summary>
        /// Fired dopo OnNetworkSpawn() — i sistemi dipendenti si sottoscrivono
        /// se Instance è null al loro Start().
        /// </summary>
        public static event Action OnInstanceReady;

        // ===== NetworkVariables (server scrive, tutti leggono) =====

        private readonly NetworkVariable<float> netO2Level =
            new NetworkVariable<float>(100f,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> netCrewCount =
            new NetworkVariable<int>(1,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<float> netGenerationRate =
            new NetworkVariable<float>(0f,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<bool> netIsAlarmActive =
            new NetworkVariable<bool>(false,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // ===== Configurazione =====

        [Header("Crew — fallback (D26: il valore reale viene da ConnectedClientsIds)")]
        [Tooltip("Fallback usato SOLO se NetworkManager non è disponibile (es. test in editor " +
                 "senza sessione NGO). Con una sessione attiva il crew count reale lo sovrascrive.")]
        [SerializeField] private int placeholderCrewCount = 1;

        [Header("O2 Consumption")]
        [Tooltip("O2 consumato per crew al minuto (GDD: 0.6/min)")]
        [SerializeField] private float o2ConsumptionPerCrewPerMinute = 0.6f;

        [Header("Soffocamento (D26, Q4-a)")]
        [Tooltip("Danno da soffocamento inflitto per secondo a ogni crew VIVO quando O2 = 0 e " +
                 "la grazia è scaduta. Tuning: default 5 HP/s → ~20s dal pieno (100 HP) al Downed. " +
                 "Il danno passa da PlayerHealthSystem.ApplyDamage (choke point unico, D27).")]
        [SerializeField] private float suffocationDamagePerSecond = 5f;

        [Header("Stato iniziale")]
        [SerializeField] private float initialO2Level = 100f;

        [Header("Debug")]
        [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development Build). Standard Rev BA — default off.")]
        [SerializeField] private bool showDebugUI = false;
        [Tooltip("Log diagnostici verbosi (telemetria O2/rate/transizioni). Standard Rev BA — default off. I problemi reali restano sempre a log.")]
        [SerializeField] private bool logVerbose = false;

        [Tooltip("DEBUG (Editor/Dev, solo server): forza l'O2 nave a 0 per testare la catena " +
                 "grazia→soffocamento→Downed senza attendere lo svuotamento naturale (~33 min). " +
                 "Spegni per far risalire l'O2 (Life Support ricarica) e resettare.")]
        [SerializeField] private bool debugDrainShipO2 = false;

        [Tooltip("DEBUG (Editor/Dev, solo server): con debugDrainShipO2 attivo, AZZERA la grazia " +
                 "→ soffocamento immediato (salti l'attesa del countdown). Lascia OFF per testare " +
                 "anche la grazia reale.")]
        [SerializeField] private bool debugInstantSuffocation = false;

        // ===== Stato server-only =====

        private float totalGenerationRate = 0f;     // somma da tutti i LifeSupportConsumer
        private bool alarmRaised = false;           // isteresi locale
        private float deathCountdownRemaining = -1f; // -1 = non attivo
        private bool suffocationActive = false;     // true = grazia scaduta, danno in corso

        // ===== Soglie (lette dal LifeSupportConsumer dopo init) =====
        // In assenza di UpgradeData, usiamo i valori GDD come fallback
        private float alarmThreshold = 0.20f;
        private float alarmClearThreshold = 0.25f;
        private float deathCountdownDuration = 60f;

        // ===== Properties pubbliche (leggono NetworkVariable — safe da tutti i client) =====

        public float O2Level => netO2Level.Value;
        public float O2Percentage => netO2Level.Value / 100f;
        public int CrewCount => netCrewCount.Value;
        public float GenerationRatePerMinute => netGenerationRate.Value * 60f;
        public float ConsumptionRatePerMinute => netCrewCount.Value * o2ConsumptionPerCrewPerMinute;
        public float NetRatePerMinute => GenerationRatePerMinute - ConsumptionRatePerMinute;
        public bool IsAlarmActive => netIsAlarmActive.Value;

        // ===== Events (fired su tutti i client via NetworkVariable.OnValueChanged) =====

        /// <summary>Fired quando O2Level cambia. Parametro: nuovo valore 0-100.</summary>
        public event Action<float> OnO2LevelChanged;

        /// <summary>Fired quando l'allarme O2 si attiva o disattiva.</summary>
        public event Action<bool> OnAlarmStateChanged;

        /// <summary>
        /// Fired UNA volta quando O2 = 0 e la grazia scade (inizio soffocamento).
        /// D26 (Q4-a): ora il danno è cablato internamente via ApplyDamage; l'evento
        /// resta esposto per la futura UI (es. warning "SOFFOCAMENTO").
        /// </summary>
        public static event Action OnOxygenDepleted;

        // ===== NGO Lifecycle =====

        public override void OnNetworkSpawn()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
                return;
            }

            if (IsServer)
            {
                netO2Level.Value = initialO2Level;
                netGenerationRate.Value = 0f;
                netIsAlarmActive.Value = false;

                // D26 (Q3-a): crew count reale + reattività a connect/disconnect.
                ServerRecomputeCrewCount();
                var nm = NetworkManager.Singleton;
                if (nm != null)
                {
                    nm.OnClientConnectedCallback += HandleClientConnected;
                    nm.OnClientDisconnectCallback += HandleClientDisconnected;
                }
            }

            // Tutti i client ascoltano i cambi per aggiornare UI locale
            netO2Level.OnValueChanged += HandleO2LevelChanged;
            netIsAlarmActive.OnValueChanged += HandleAlarmChanged;

            // Notifica i sistemi dipendenti (es. LifeSupportConsumer)
            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            netO2Level.OnValueChanged -= HandleO2LevelChanged;
            netIsAlarmActive.OnValueChanged -= HandleAlarmChanged;

            if (IsServer)
            {
                var nm = NetworkManager.Singleton;
                if (nm != null)
                {
                    nm.OnClientConnectedCallback -= HandleClientConnected;
                    nm.OnClientDisconnectCallback -= HandleClientDisconnected;
                }
            }

            if (Instance == this) Instance = null;
        }

        // Handler nominati (evita il bug di unsubscribe con lambda diverse per add/remove)
        private void HandleO2LevelChanged(float _, float newVal) => OnO2LevelChanged?.Invoke(newVal);
        private void HandleAlarmChanged(bool _, bool newVal) => OnAlarmStateChanged?.Invoke(newVal);

        // ===== Update (solo server) =====

        private void Update()
        {
            if (!IsServer) return;

            UpdateO2Tick();
            CheckAlarmThresholds();
            UpdateSuffocation();
        }

        // ===== Logica server =====

        private void UpdateO2Tick()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (debugDrainShipO2)
            {
                // Pinna l'O2 nave a 0: la catena allarme/grazia/soffocamento gira reale.
                if (netO2Level.Value != 0f) netO2Level.Value = 0f;
                return;
            }
#endif
            // consumo crew: 0.6/min per crew → /60 per secondo
            float consumptionPerSecond = netCrewCount.Value * (o2ConsumptionPerCrewPerMinute / 60f);

            // generazione da LifeSupportConsumer (già in unità/secondo).
            // NB: in blackout Life Support è staccato → totalGenerationRate = 0 → l'O2 scende.
            float generationPerSecond = totalGenerationRate;

            float delta = (generationPerSecond - consumptionPerSecond) * Time.deltaTime;
            float newLevel = Mathf.Clamp(netO2Level.Value + delta, 0f, 100f);
            netO2Level.Value = newLevel;
        }

        private void CheckAlarmThresholds()
        {
            float percent = netO2Level.Value / 100f;

            if (!alarmRaised && percent < alarmThreshold)
            {
                alarmRaised = true;
                netIsAlarmActive.Value = true;
                AlarmSystem.Instance?.RaiseAlarm(
                    AlarmSystem.AlarmSource.OxygenLow,
                    AlarmSystem.AlarmSeverity.Emergency);
                LogVWarn($"[OxygenSystem] ⚠ OXYGEN LOW: {netO2Level.Value:F1}%");
            }
            else if (alarmRaised && percent >= alarmClearThreshold)
            {
                alarmRaised = false;
                netIsAlarmActive.Value = false;
                AlarmSystem.Instance?.ClearAlarm(AlarmSystem.AlarmSource.OxygenLow);
                LogV($"[OxygenSystem] O2 nominale: {netO2Level.Value:F1}%");
            }
        }

        /// <summary>
        /// Soffocamento (D26, Q4-a). A O2 = 0 parte una grazia (deathCountdownDuration);
        /// alla scadenza si infligge danno da soffocamento a ogni crew VIVO, ogni tick,
        /// finché l'O2 non risale. ApplyDamage salta da solo i player Downed/RespawnWait.
        /// </summary>
        private void UpdateSuffocation()
        {
            if (netO2Level.Value <= 0f)
            {
                if (deathCountdownRemaining < 0f)
                {
                    // Avvio grazia
                    deathCountdownRemaining = deathCountdownDuration;
                    suffocationActive = false;
                    LogVError($"[OxygenSystem] OXYGEN DEPLETED — grazia {deathCountdownDuration:F0}s prima del soffocamento");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (debugInstantSuffocation)
                    {
                        deathCountdownRemaining = 0f;
                        suffocationActive = true;
                        OnOxygenDepleted?.Invoke();
                        LogVError("[OxygenSystem] DEBUG — soffocamento immediato (grazia saltata)");
                    }
#endif
                }
                else if (deathCountdownRemaining > 0f)
                {
                    deathCountdownRemaining -= Time.deltaTime;
                    if (deathCountdownRemaining <= 0f)
                    {
                        deathCountdownRemaining = 0f;
                        suffocationActive = true;
                        OnOxygenDepleted?.Invoke();   // una sola volta per episodio
                        LogVError("[OxygenSystem] SOFFOCAMENTO — danno alla crew viva in corso");
                    }
                }
                else
                {
                    // Grazia scaduta: danno continuo (DoT) alla crew viva.
                    ApplySuffocationDamage(suffocationDamagePerSecond * Time.deltaTime);
                }
            }
            else if (deathCountdownRemaining >= 0f || suffocationActive)
            {
                // O2 è risalito — resetta grazia/soffocamento
                deathCountdownRemaining = -1f;
                suffocationActive = false;
                LogV("[OxygenSystem] O2 ripristinato — soffocamento annullato");
            }
        }

        /// <summary>
        /// Infligge danno da soffocamento a tutti i crew connessi. ApplyDamage è
        /// server-only, per-player, e ignora chi non è Alive (Downed/RespawnWait) →
        /// il filtro sui vivi è naturale. PlayerHealthSystem è in namespace globale.
        /// </summary>
        private void ApplySuffocationDamage(float amount)
        {
            if (amount <= 0f) return;
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            foreach (ulong clientId in nm.ConnectedClientsIds)
            {
                if (PlayerHealthSystem.TryGetByClientId(clientId, out var health) && health != null)
                    health.ApplyDamage(amount);
            }
        }

        // ===== Crew count reale (D26, Q3-a) =====

        /// <summary>
        /// Ricalcola il crew count dalla sorgente autoritativa (ConnectedClientsIds).
        /// Stesso idioma già usato da MedicalDashboardUI / ShipTabUI. Fallback a
        /// placeholderCrewCount solo se NetworkManager è assente (test editor).
        /// </summary>
        private void ServerRecomputeCrewCount()
        {
            if (!IsServer) return;
            var nm = NetworkManager.Singleton;
            int count = (nm != null) ? nm.ConnectedClientsIds.Count : placeholderCrewCount;
            netCrewCount.Value = Mathf.Max(0, count);
            LogV($"[OxygenSystem] Crew count = {netCrewCount.Value}");
        }

        // NGO: alla disconnessione, il client è già rimosso da ConnectedClientsIds
        // prima che scatti la callback → il ricalcolo diretto è corretto.
        private void HandleClientConnected(ulong _) => ServerRecomputeCrewCount();
        private void HandleClientDisconnected(ulong _) => ServerRecomputeCrewCount();

        // ===== API pubblica (chiamata da LifeSupportConsumer) =====

        /// <summary>
        /// Aggiunge una sorgente di generazione O2 (in unità/secondo).
        /// Chiamato da LifeSupportConsumer quando viene alimentato.
        /// Deve essere chiamato solo dal server.
        /// </summary>
        public void AddGenerationSource(float ratePerSecond)
        {
            if (!IsServer) return;
            totalGenerationRate += ratePerSecond;
            netGenerationRate.Value = totalGenerationRate;
            LogV($"[OxygenSystem] +{ratePerSecond * 60f:F1}/min — totale: {netGenerationRate.Value * 60f:F1}/min");
        }

        /// <summary>
        /// Rimuove una sorgente di generazione O2.
        /// Chiamato da LifeSupportConsumer quando perde alimentazione (incl. blackout).
        /// Deve essere chiamato solo dal server.
        /// </summary>
        public void RemoveGenerationSource(float ratePerSecond)
        {
            if (!IsServer) return;
            totalGenerationRate = Mathf.Max(0f, totalGenerationRate - ratePerSecond);
            netGenerationRate.Value = totalGenerationRate;
            LogVWarn($"[OxygenSystem] -{ratePerSecond * 60f:F1}/min — totale: {netGenerationRate.Value * 60f:F1}/min");
        }

        /// <summary>
        /// Override manuale del crew count. D26: la sorgente reale è ConnectedClientsIds
        /// (ServerRecomputeCrewCount); questa API resta per test/override espliciti.
        /// </summary>
        public void SetCrewCount(int count)
        {
            if (!IsServer) return;
            netCrewCount.Value = Mathf.Max(0, count);
        }

        /// <summary>
        /// Configura le soglie di allarme dal LifeSupportUpgradeData.
        /// Chiamato da LifeSupportConsumer dopo l'upgrade.
        /// </summary>
        public void SetAlarmThresholds(float alarm, float clear, float deathCountdown)
        {
            if (!IsServer) return;
            alarmThreshold = alarm;
            alarmClearThreshold = clear;
            deathCountdownDuration = deathCountdown;
        }

        // ===== Debug logging (Rev BA) =====
        private void LogV(string msg) { if (logVerbose) Debug.Log(msg); }
        private void LogVWarn(string msg) { if (logVerbose) Debug.LogWarning(msg); }
        private void LogVError(string msg) { if (logVerbose) Debug.LogError(msg); }

        // ===== Debug GUI =====

        private void OnGUI()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!showDebugUI) return;

            int y = 300;
            GUI.Label(new Rect(10, y, 320, 20), $"=== OXYGEN SYSTEM [{(IsServer ? "SERVER" : "CLIENT")}] ==="); y += 20;
            if (debugDrainShipO2)
            {
                GUI.color = Color.yellow;
                GUI.Label(new Rect(10, y, 320, 20), $"[DEBUG] DRAIN O2 NAVE ATTIVO{(debugInstantSuffocation ? " (grazia saltata)" : "")}"); y += 20;
                GUI.color = Color.white;
            }
            GUI.Label(new Rect(10, y, 320, 20), $"O2 Level: {netO2Level.Value:F1}%"); y += 20;
            GUI.Label(new Rect(10, y, 320, 20), $"Crew: {netCrewCount.Value} | Consumption: {ConsumptionRatePerMinute:F2}/min"); y += 20;
            GUI.Label(new Rect(10, y, 320, 20), $"Generation: {GenerationRatePerMinute:F2}/min | Net: {NetRatePerMinute:+0.00;-0.00}/min"); y += 20;

            if (netIsAlarmActive.Value)
            {
                GUI.color = Color.red;
                GUI.Label(new Rect(10, y, 320, 20), "⚠ OXYGEN LOW ALARM"); y += 20;
                GUI.color = Color.white;
            }

            if (suffocationActive)
            {
                GUI.color = Color.red;
                GUI.Label(new Rect(10, y, 320, 20), "💀 SOFFOCAMENTO — danno crew in corso");
                GUI.color = Color.white;
            }
            else if (deathCountdownRemaining > 0f)
            {
                GUI.color = Color.red;
                GUI.Label(new Rect(10, y, 320, 20), $"💀 SOFFOCAMENTO IN: {deathCountdownRemaining:F0}s");
                GUI.color = Color.white;
            }
#endif
        }
    }
}