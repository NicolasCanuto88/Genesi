using UnityEngine;
using Unity.Netcode;
using System;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// OxygenSystem — Milestone 2
    /// Gestisce il livello O2 della nave, il consumo crew e i trigger di allarme.
    ///
    /// RESPONSABILITÀ:
    ///   - Traccia O2Level (0-100%) come NetworkVariable (server authority)
    ///   - Calcola net O2 = generazione - consumo crew per tick
    ///   - Trigger AlarmSystem.OxygenLow quando O2 &lt; soglia (con isteresi)
    ///   - Espone eventi per ShipSystemsDashboardUI
    ///
    /// NON sa nulla di PowerManager. Riceve oxygenGenerationRate via
    /// LifeSupportConsumer.AddGenerationSource() / RemoveGenerationSource().
    ///
    /// Pattern OnInstanceReady:
    ///   OxygenSystem.OnInstanceReady viene fired dopo OnNetworkSpawn().
    ///   LifeSupportConsumer si sottoscrive se OxygenSystem non è ancora pronto.
    ///
    /// ⚠️  DIPENDE DA: PlayerSpawn (M2/M3) per ConnectedCrewCount reale.
    ///               In M2 il crew count è un SerializeField configurabile (default 1).
    /// ⚠️  DIPENDE DA: PlayerHealthSystem (M2) per consumare il countdown O2 = 0%.
    ///               L'evento OnOxygenDepleted viene esposto ma non ancora consumato.
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

        // ===== Configurazione (server-only in M2 — nessun PlayerSpawn ancora) =====

        [Header("Crew (M2 placeholder — sostituire con PlayerSpawn in M3)")]
        [SerializeField] private int placeholderCrewCount = 1;

        [Header("O2 Consumption")]
        [Tooltip("O2 consumato per crew al minuto (GDD: 0.6/min)")]
        [SerializeField] private float o2ConsumptionPerCrewPerMinute = 0.6f;

        [Header("Debug / Initial State")]
        [SerializeField] private float initialO2Level = 100f;

        // ===== Stato server-only =====

        private float totalGenerationRate = 0f;     // somma da tutti i LifeSupportConsumer
        private bool alarmRaised = false;           // isteresi locale
        private float deathCountdownRemaining = -1f; // -1 = non attivo

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
        /// Fired quando O2 = 0 e il countdown è scaduto.
        /// dipende da: PlayerHealthSystem (M2) — non ancora consumato.
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
                netCrewCount.Value = placeholderCrewCount;
                netGenerationRate.Value = 0f;
                netIsAlarmActive.Value = false;
            }

            // Tutti i client ascoltano i cambi per aggiornare UI locale
            netO2Level.OnValueChanged += (_, newVal) => OnO2LevelChanged?.Invoke(newVal);
            netIsAlarmActive.OnValueChanged += (_, newVal) => OnAlarmStateChanged?.Invoke(newVal);

            // Notifica i sistemi dipendenti (es. LifeSupportConsumer)
            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            netO2Level.OnValueChanged -= (_, newVal) => OnO2LevelChanged?.Invoke(newVal);
            netIsAlarmActive.OnValueChanged -= (_, newVal) => OnAlarmStateChanged?.Invoke(newVal);

            if (Instance == this) Instance = null;
        }

        // ===== Update (solo server) =====

        private void Update()
        {
            if (!IsServer) return;

            UpdateO2Tick();
            CheckAlarmThresholds();
            UpdateDeathCountdown();
        }

        // ===== Logica server =====

        private void UpdateO2Tick()
        {
            // consumo crew: 0.6/min per crew → /60 per secondo
            float consumptionPerSecond = netCrewCount.Value * (o2ConsumptionPerCrewPerMinute / 60f);

            // generazione da LifeSupportConsumer (già in unità/secondo)
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
                Debug.LogWarning($"[OxygenSystem] ⚠ OXYGEN LOW: {netO2Level.Value:F1}%");
            }
            else if (alarmRaised && percent >= alarmClearThreshold)
            {
                alarmRaised = false;
                netIsAlarmActive.Value = false;
                AlarmSystem.Instance?.ClearAlarm(AlarmSystem.AlarmSource.OxygenLow);
                Debug.Log($"[OxygenSystem] O2 nominale: {netO2Level.Value:F1}%");
            }
        }

        private void UpdateDeathCountdown()
        {
            if (netO2Level.Value <= 0f)
            {
                if (deathCountdownRemaining < 0f)
                {
                    // Avvio countdown
                    deathCountdownRemaining = deathCountdownDuration;
                    Debug.LogError($"[OxygenSystem] OXYGEN DEPLETED — countdown {deathCountdownDuration:F0}s");
                }
                else
                {
                    deathCountdownRemaining -= Time.deltaTime;
                    if (deathCountdownRemaining <= 0f)
                    {
                        deathCountdownRemaining = 0f;
                        // dipende da: PlayerHealthSystem (M2)
                        OnOxygenDepleted?.Invoke();
                        Debug.LogError("[OxygenSystem] CREW SUFFOCATION — PlayerHealthSystem non ancora implementato");
                    }
                }
            }
            else if (deathCountdownRemaining >= 0f)
            {
                // O2 è risalito — resetta countdown
                deathCountdownRemaining = -1f;
            }
        }

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
            Debug.Log($"[OxygenSystem] +{ratePerSecond * 60f:F1}/min — totale: {netGenerationRate.Value * 60f:F1}/min");
        }

        /// <summary>
        /// Rimuove una sorgente di generazione O2.
        /// Chiamato da LifeSupportConsumer quando perde alimentazione.
        /// Deve essere chiamato solo dal server.
        /// </summary>
        public void RemoveGenerationSource(float ratePerSecond)
        {
            if (!IsServer) return;
            totalGenerationRate = Mathf.Max(0f, totalGenerationRate - ratePerSecond);
            netGenerationRate.Value = totalGenerationRate;
            Debug.LogWarning($"[OxygenSystem] -{ratePerSecond * 60f:F1}/min — totale: {netGenerationRate.Value * 60f:F1}/min");
        }

        /// <summary>
        /// Aggiorna il numero di crew connessi.
        /// dipende da: PlayerSpawn (M3) — in M2 usa placeholderCrewCount.
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

        // ===== Debug GUI =====

        private void OnGUI()
        {
            if (!Debug.isDebugBuild) return;

            int y = 300;
            GUI.Label(new Rect(10, y, 320, 20), $"=== OXYGEN SYSTEM [{(IsServer ? "SERVER" : "CLIENT")}] ==="); y += 20;
            GUI.Label(new Rect(10, y, 320, 20), $"O2 Level: {netO2Level.Value:F1}%"); y += 20;
            GUI.Label(new Rect(10, y, 320, 20), $"Crew: {netCrewCount.Value} | Consumption: {ConsumptionRatePerMinute:F2}/min"); y += 20;
            GUI.Label(new Rect(10, y, 320, 20), $"Generation: {GenerationRatePerMinute:F2}/min | Net: {NetRatePerMinute:+0.00;-0.00}/min"); y += 20;

            if (netIsAlarmActive.Value)
            {
                GUI.color = Color.red;
                GUI.Label(new Rect(10, y, 320, 20), "⚠ OXYGEN LOW ALARM"); y += 20;
                GUI.color = Color.white;
            }

            if (deathCountdownRemaining > 0f)
            {
                GUI.color = Color.red;
                GUI.Label(new Rect(10, y, 320, 20), $"💀 SUFFOCATION IN: {deathCountdownRemaining:F0}s");
                GUI.color = Color.white;
            }
        }
    }
}
