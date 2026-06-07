using UnityEngine;
using Unity.Netcode;
using System;
using System.Collections;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ShieldSystem — Milestone 2
    /// NetworkBehaviour + IPowerConsumer. Server authority.
    ///
    /// STATI:
    ///   Off      → scudi inattivi, nessun consumo, nessuna regen
    ///   Charging → spin-up in corso, scudi NON operativi, consumo pieno
    ///   On       → scudi operativi, assorbono danno, regen attiva
    ///
    /// FLUSSO DANNO (separato da HullSystem — zero GetComponent):
    ///   AbsorbDamage(float) → assorbe absorptionPercent
    ///                       → residuo → HullSystem.NotifyDamagePassthrough()
    ///
    /// CONSUMO ENERGETICO:
    ///   Varia per contesto (normale / combat / radiation storm / asteroid storm).
    ///   Il contesto viene impostato da ZoneManager (M2) via SetZoneContext().
    ///   In M2 il contesto di default è Normal.
    ///
    /// ATTIVAZIONE:
    ///   TryActivate() — chiamato dall'input del Pilota (tasto F / LB gamepad).
    ///   ⚠️ Dipende da: PilotStation input binding (M2).
    ///   In M2 si può testare via debug GUI o ContextMenu.
    ///
    /// ⚠️ DIPENDE DA: ZoneManager (M2) per contesto zona (radiation/asteroid storm).
    /// ⚠️ DIPENDE DA: PilotStation (M2) per input attivazione.
    /// ⚠️ DIPENDE DA: EncounterSystem (M2) per danno reale in ingresso.
    /// </summary>
    public class ShieldSystem : NetworkBehaviour, IPowerConsumer
    {
        // ===== SINGLETON + INSTANCE READY =====

        public static ShieldSystem Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ===== STATI =====

        public enum ShieldState { Off, Charging, On }

        public enum ZoneContext { Normal, Combat, RadiationStorm, AsteroidStorm }

        // ===== NETWORKVARABLES =====

        private NetworkVariable<float> netCurrentHP = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<float> netMaxHP = new NetworkVariable<float>(
            50f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<int> netState = new NetworkVariable<int>(
            (int)ShieldState.Off, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<float> netSpinUpProgress = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<int> netZoneContext = new NetworkVariable<int>(
            (int)ZoneContext.Normal, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ===== EVENTI PUBBLICI =====

        /// <summary>Fired su tutti i client al cambio stato.</summary>
        public event Action<ShieldState> OnStateChanged;

        /// <summary>Fired su tutti i client quando HP cambia. (currentHP, maxHP, percent 0-1)</summary>
        public event Action<float, float, float> OnShieldHPChanged;

        /// <summary>Fired sul server quando gli scudi collassano (HP = 0).</summary>
        public event Action OnShieldCollapse;

        // ===== INSPECTOR =====

        [Header("Upgrade Data")]
        [Tooltip("Array tier T0…T4. Index 0 = nessuno scudo (T0), Index 1 = T1 Basic, ecc.")]
        [SerializeField] private ShieldUpgradeData[] allTiers;

        [Tooltip("Tier iniziale (0 = nessun scudo, 1 = T1 Basic).")]
        [SerializeField] private int startingTierIndex = 0;

        [Header("Debug")]
        [SerializeField] private bool showDebugGUI = true;

        // ===== STATO PRIVATO (server-only) =====

        private ShieldUpgradeData currentUpgrade;
        private float regenTimer = 0f;          // tempo trascorso dall'ultimo colpo
        private bool regenPaused = false;
        private Coroutine spinUpCoroutine;
        private PowerManager powerManager;

        // ===== PUBLIC READ API =====

        public float CurrentHP => netCurrentHP.Value;
        public float MaxHP => netMaxHP.Value;
        public float ShieldPercent => netMaxHP.Value > 0f ? Mathf.Clamp01(netCurrentHP.Value / netMaxHP.Value) : 0f;
        public ShieldState State => (ShieldState)netState.Value;
        public ZoneContext Context => (ZoneContext)netZoneContext.Value;
        public bool IsOperational => State == ShieldState.On && netCurrentHP.Value > 0f;
        public float SpinUpProgress => netSpinUpProgress.Value; // 0–1, solo durante Charging

        // ===== NGO LIFECYCLE =====

        public override void OnNetworkSpawn()
        {
            if (Instance == null)
                Instance = this;
            else
            {
                Destroy(gameObject);
                return;
            }

            // Tutti i client reagiscono ai cambi di stato
            netState.OnValueChanged += HandleStateChanged;
            netCurrentHP.OnValueChanged += HandleHPChanged;
            netMaxHP.OnValueChanged += HandleHPChanged;

            if (IsServer)
            {
                InitUpgrade();

                if (PowerManager.Instance != null)
                    RegisterWithPowerManager();
                else
                    PowerManager.OnInstanceReady += RegisterWithPowerManager;
            }

            OnInstanceReady?.Invoke();
            Debug.Log($"[ShieldSystem] Online — Tier {currentUpgrade?.tier ?? 0}, State: {State}");
        }

        public override void OnNetworkDespawn()
        {
            netState.OnValueChanged -= HandleStateChanged;
            netCurrentHP.OnValueChanged -= HandleHPChanged;
            netMaxHP.OnValueChanged -= HandleHPChanged;

            PowerManager.OnInstanceReady -= RegisterWithPowerManager;

            if (powerManager != null)
                powerManager.UnregisterPowerConsumer(this);

            if (Instance == this) Instance = null;
        }

        private void RegisterWithPowerManager()
        {
            PowerManager.OnInstanceReady -= RegisterWithPowerManager;
            powerManager = PowerManager.Instance;
            powerManager.RegisterPowerConsumer(this);
            Debug.Log("[ShieldSystem] Registered with PowerManager.");
        }

        // ===== INIZIALIZZAZIONE UPGRADE =====

        private void InitUpgrade()
        {
            if (allTiers == null || allTiers.Length == 0)
            {
                Debug.LogWarning("[ShieldSystem] Nessun ShieldUpgradeData assegnato — scudi disabilitati.");
                return;
            }

            int idx = Mathf.Clamp(startingTierIndex, 0, allTiers.Length - 1);
            currentUpgrade = allTiers[idx];

            netMaxHP.Value = currentUpgrade.maxHP;
            netCurrentHP.Value = currentUpgrade.maxHP; // HP pieni al deploy
            netState.Value = (int)ShieldState.Off;  // scudi spenti all'avvio
        }

        // ===== SERVER UPDATE (regen) =====

        private void Update()
        {
            if (!IsServer) return;
            if (State != ShieldState.On) return;
            if (currentUpgrade == null) return;

            // Regen HP
            if (!regenPaused)
            {
                if (netCurrentHP.Value < netMaxHP.Value)
                {
                    netCurrentHP.Value = Mathf.Min(
                        netMaxHP.Value,
                        netCurrentHP.Value + currentUpgrade.regenRate * Time.deltaTime);
                }
            }
            else
            {
                regenTimer += Time.deltaTime;
                if (regenTimer >= currentUpgrade.regenPause)
                {
                    regenPaused = false;
                    regenTimer = 0f;
                }
            }
        }

        // ===== PUBLIC API — ATTIVAZIONE =====

        /// <summary>
        /// Toggle scudi ON/OFF. Chiamato dall'input del Pilota.
        /// Se OFF → avvia spin-up → ON.
        /// Se ON o Charging → spegne istantaneamente.
        /// Chiamabile da qualsiasi client.
        /// </summary>
        public void TryActivate()
        {
            if (IsServer)
                TryActivateInternal();
            else
                TryActivateRpc();
        }

        [Rpc(SendTo.Server)]
        private void TryActivateRpc() => TryActivateInternal();

        private void TryActivateInternal()
        {
            if (!IsServer) return;
            if (currentUpgrade == null || currentUpgrade.maxHP <= 0f)
            {
                Debug.Log("[ShieldSystem] Nessun upgrade scudi installato.");
                return;
            }

            if (State == ShieldState.Off)
            {
                // Avvia spin-up
                if (spinUpCoroutine != null) StopCoroutine(spinUpCoroutine);
                spinUpCoroutine = StartCoroutine(SpinUpRoutine());
            }
            else
            {
                // Spegni istantaneamente
                if (spinUpCoroutine != null)
                {
                    StopCoroutine(spinUpCoroutine);
                    spinUpCoroutine = null;
                }
                netState.Value = (int)ShieldState.Off;
                netSpinUpProgress.Value = 0f;
                regenPaused = true;  // pausa regen alla riattivazione
                regenTimer = 0f;
                Debug.Log("[ShieldSystem] Scudi disattivati.");
            }
        }

        private IEnumerator SpinUpRoutine()
        {
            netState.Value = (int)ShieldState.Charging;
            Debug.Log($"[ShieldSystem] Spin-up avviato ({currentUpgrade.spinUpTime}s)...");

            float elapsed = 0f;
            while (elapsed < currentUpgrade.spinUpTime)
            {
                elapsed += Time.deltaTime;
                netSpinUpProgress.Value = Mathf.Clamp01(elapsed / currentUpgrade.spinUpTime);
                yield return null;
            }

            netState.Value = (int)ShieldState.On;
            netSpinUpProgress.Value = 1f;
            regenPaused = true;
            regenTimer = 0f;
            spinUpCoroutine = null;
            Debug.Log("[ShieldSystem] Scudi operativi.");
        }

        // ===== PUBLIC API — DANNO =====

        /// <summary>
        /// Assorbe danno in ingresso. Il residuo viene inoltrato a HullSystem via evento statico.
        /// Chiamare solo se IsOperational == true.
        /// Chiamabile da qualsiasi client (eseguito sul server).
        /// </summary>
        public void AbsorbDamage(float incomingDamage)
        {
            if (incomingDamage <= 0f) return;

            if (IsServer)
                AbsorbDamageInternal(incomingDamage);
            else
                AbsorbDamageRpc(incomingDamage);
        }

        [Rpc(SendTo.Server)]
        private void AbsorbDamageRpc(float damage) => AbsorbDamageInternal(damage);

        private void AbsorbDamageInternal(float incomingDamage)
        {
            if (!IsServer) return;

            if (!IsOperational)
            {
                // Scudi non operativi — tutto il danno va allo scafo
                HullSystem.NotifyDamagePassthrough(incomingDamage);
                return;
            }

            // Calcola danno assorbito
            float absorbed = incomingDamage * currentUpgrade.absorptionPercent;
            absorbed = Mathf.Min(absorbed, netCurrentHP.Value);

            netCurrentHP.Value -= absorbed;
            netCurrentHP.Value = Mathf.Max(0f, netCurrentHP.Value);

            float remaining = incomingDamage - absorbed;

            // Pausa regen
            regenPaused = true;
            regenTimer = 0f;

            Debug.Log($"[ShieldSystem] Danno ricevuto: {incomingDamage:F1} — assorbito: {absorbed:F1}, residuo: {remaining:F1}");

            // Residuo → HullSystem
            if (remaining > 0f)
                HullSystem.NotifyDamagePassthrough(remaining);

            // Collasso scudi
            if (netCurrentHP.Value <= 0f)
            {
                netState.Value = (int)ShieldState.Off;
                netSpinUpProgress.Value = 0f;
                OnShieldCollapse?.Invoke();
                Debug.LogWarning("[ShieldSystem] ⚠ SCUDI COLLASSATI — HP esauriti.");
            }
        }

        // ===== PUBLIC API — ZONA =====

        /// <summary>
        /// Imposta il contesto di zona. Chiamato da ZoneManager (M2).
        /// Influenza GetPowerDemand().
        /// </summary>
        public void SetZoneContext(ZoneContext context)
        {
            if (IsServer)
                netZoneContext.Value = (int)context;
            else
                SetZoneContextRpc(context);
        }

        [Rpc(SendTo.Server)]
        private void SetZoneContextRpc(ZoneContext context)
        {
            netZoneContext.Value = (int)context;
        }

        // ===== PUBLIC API — UPGRADE =====

        public void ApplyUpgrade(int tierIndex)
        {
            if (IsServer)
                ApplyUpgradeInternal(tierIndex);
            else
                ApplyUpgradeRpc(tierIndex);
        }

        [Rpc(SendTo.Server)]
        private void ApplyUpgradeRpc(int tierIndex) => ApplyUpgradeInternal(tierIndex);

        private void ApplyUpgradeInternal(int tierIndex)
        {
            if (!IsServer) return;
            if (allTiers == null || tierIndex < 0 || tierIndex >= allTiers.Length) return;

            // Spegni scudi prima dell'upgrade
            if (State != ShieldState.Off)
                TryActivateInternal();

            float oldPercent = ShieldPercent;
            currentUpgrade = allTiers[tierIndex];

            netMaxHP.Value = currentUpgrade.maxHP;
            netCurrentHP.Value = currentUpgrade.maxHP * oldPercent;

            Debug.Log($"[ShieldSystem] Upgrade a T{currentUpgrade.tier}");
        }

        // ===== IPOWERCONSUMER =====

        public float GetPowerDemand()
        {
            // Scudi spenti o nessun upgrade → zero consumo
            if (State == ShieldState.Off) return 0f;
            if (currentUpgrade == null) return 0f;

            // Durante Charging consuma come se fossero On (spin-up richiede energia)
            return Context switch
            {
                ZoneContext.RadiationStorm => currentUpgrade.powerRadiationStorm,
                ZoneContext.AsteroidStorm => currentUpgrade.powerAsteroidStorm,
                ZoneContext.Combat => currentUpgrade.powerCombat,
                _ => currentUpgrade.powerNormal,
            };
        }

        public int GetPriority() => currentUpgrade?.powerPriority ?? 7;
        public bool IsActive() => State != ShieldState.Off;
        public bool CanBeDisabled() => true; // PowerManager può spegnere gli scudi in emergenza
        public string GetSystemName() => "ShieldSystem";

        public void SetPowerState(bool isOn)
        {
            if (!IsServer) return;
            if (!isOn && State != ShieldState.Off)
            {
                // PowerManager ha tagliato la corrente — scudi giù istantaneamente
                if (spinUpCoroutine != null) { StopCoroutine(spinUpCoroutine); spinUpCoroutine = null; }
                netState.Value = (int)ShieldState.Off;
                netSpinUpProgress.Value = 0f;
                Debug.LogWarning("[ShieldSystem] Power cut by PowerManager — scudi disattivati.");
            }
        }

        // ===== NETWORKVAR CALLBACKS (tutti i client) =====

        private void HandleStateChanged(int previous, int current)
        {
            OnStateChanged?.Invoke((ShieldState)current);
        }

        private void HandleHPChanged(float previous, float current)
        {
            OnShieldHPChanged?.Invoke(netCurrentHP.Value, netMaxHP.Value, ShieldPercent);
        }

        // ===== DEBUG GUI =====

        private void OnGUI()
        {
            if (!showDebugGUI) return;

            int y = 440;
            GUI.Label(new Rect(10, y, 400, 20),
                $"=== SHIELDS: {State} | {netCurrentHP.Value:F0}/{netMaxHP.Value:F0} HP ({ShieldPercent * 100f:F1}%) ===");
            y += 20;

            if (IsServer)
            {
                if (GUI.Button(new Rect(10, y, 150, 22), "Toggle Shields"))
                    TryActivateInternal();

                if (GUI.Button(new Rect(170, y, 170, 22), "Simulate Hit -20 dmg"))
                    AbsorbDamageInternal(20f);
                y += 28;

                GUI.Label(new Rect(10, y, 300, 20),
                    $"Context: {Context} | W: {GetPowerDemand():F1}");
                y += 20;

                if (GUI.Button(new Rect(10, y, 130, 22), "Zone: Normal"))
                    netZoneContext.Value = (int)ZoneContext.Normal;
                if (GUI.Button(new Rect(150, y, 160, 22), "Zone: Rad.Storm"))
                    netZoneContext.Value = (int)ZoneContext.RadiationStorm;
            }
        }
    }
}