using System;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    // ─── Enum NavigationState ─────────────────────────────────────────────────
    // Definito qui — usato da PilotStation, PilotHUD, FTLDrive, AnchorSystem.

    public enum NavigationState
    {
        Anchored,   // nave ferma a riposo (base default), 0W, 0 fuel
        Coasting,   // inerzia (Pilota fuori postazione o OFFLINE), 0W, 0 fuel
        Autopilot,  // rotta automatica verso POI, 50W, 0.5 fuel/min
        Manual,     // controllo diretto Pilota, 80W, 1.0 fuel/min
        Docking,    // [Fase 3 Blocco 3.1] minigioco di attracco attivo (strafe RCS),
                    // 60W, 0 fuel — la nave non usa motori principali, solo thrusters
        Docked      // [Fase 3 Blocco 3.1] attracco completato, nave ferma ancorata
                    // a un POI, 0W, 0 fuel. AnchoredPoiId identifica il POI.
    }

    // ─── PropulsionSystem ─────────────────────────────────────────────────────

    /// <summary>
    /// PropulsionSystem — Milestone 2, esteso Rev T per modello di volo 3D,
    /// esteso Fase 3 Blocco 3.1 per ancoraggio a POI (Docking/Docked).
    /// NetworkBehaviour + IPowerConsumer + IRepairable.
    ///
    /// RESPONSABILITÀ:
    ///   - Gestisce NavigationState
    ///     (Anchored / Coasting / Autopilot / Manual / Docking / Docked)
    ///   - Consuma watt da PowerManager in base allo stato
    ///   - Consuma FuelCell da InventorySystem ogni secondo (server)
    ///   - Implementa IRepairable → pannello fisico in sala motori
    ///   - Riceve SetAutopilotAvailable() da ZoneManager (AsteroidField)
    ///   - Riceve SetFTLOverride() da FTLDrive durante la carica
    ///   - Espone AnchoredPoiId (Fase 3 Blocco 3.1) come riferimento al POI
    ///     attualmente ancorato; scritto server-side da AnchorSystem
    ///
    /// MODELLO DI VOLO (Rev T — modello arcade throttle target):
    ///   - CurrentSpeed è dinamico (NetworkVariable), non più sempre al max.
    ///   - TargetSpeed è la velocità verso cui CurrentSpeed accelera con rate
    ///     _data.accelerationRate (scalato dal degrado).
    ///   - In MANUAL, il Pilota preme W/S → SetManualThrottleInput(±1) →
    ///     TargetSpeed si sposta con lo stesso rate (sensazione "leva")
    ///     verso [0, MaxSpeedAtDegradation]. Rilasciando la leva, target
    ///     si congela — il Pilota mantiene la velocità impostata.
    ///   - In AUTOPILOT, TargetSpeed = MaxSpeedAtDegradation (aggiornato
    ///     automaticamente al variare del degrado).
    ///   - In COASTING, TargetSpeed = CurrentSpeed al momento della
    ///     transizione (freeze inerzia — spazio vuoto, nessun attrito).
    ///   - In ANCHORED, TargetSpeed = CurrentSpeed = 0 (snap immediato).
    ///   - In DOCKING (3.1), CurrentSpeed = TargetSpeed = 0. La traslazione
    ///     avviene tramite strafe RCS gestito da DockingController, che
    ///     modifica direttamente ship.LogicalPosition senza passare per
    ///     CurrentSpeed. Il modello throttle Rev T è quindi "sospeso".
    ///   - In DOCKED, come Anchored: 0/0, tutto fermo. Il POI ancorato è
    ///     identificato da AnchoredPoiId.
    ///
    /// LETTORI DEL MOVIMENTO:
    ///   ShipMovement legge CurrentSpeed e CurrentNavState per accumulare
    ///   LogicalPosition e per esporre LogicalForward. In DOCKING/DOCKED,
    ///   CurrentSpeed = 0 → nessuna traslazione via il modello Rev T;
    ///   il DockingController scriverà ship.LogicalPosition direttamente
    ///   per lo strafe.
    ///
    /// REPAIR PANEL:
    ///   Assegna questa istanza come repairableTarget al RepairPanel
    ///   posizionato fisicamente in sala motori.
    ///
    /// DIPENDE DA:
    ///   PowerManager (IPowerConsumer) · InventorySystem (FuelCell)
    /// </summary>
    public class PropulsionSystem : NetworkBehaviour, IPowerConsumer, IRepairable
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static PropulsionSystem Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── Upgrade Data ──────────────────────────────────────────────────────
        [Header("Upgrade Data")]
        [Tooltip("Array di tier. Index 0 = T1, 1 = T2, ecc.")]
        [SerializeField] private PropulsionUpgradeData[] allTiers;

        [SerializeField] private int startingTierIndex = 0;

        // ── Stato iniziale ────────────────────────────────────────────────────
        [Header("Stato Iniziale")]
        [SerializeField] private NavigationState startingState = NavigationState.Anchored;
        [SerializeField] private bool startPowered = true;

        // ── Network Variables ─────────────────────────────────────────────────
        private readonly NetworkVariable<float> _netHealth =
            new(100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> _netNavState =
            new((int)NavigationState.Anchored,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<bool> _netAutopilotAvailable =
            new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Rev T — velocità dinamiche
        private readonly NetworkVariable<float> _netCurrentSpeed =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<float> _netTargetSpeed =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Fase 3 Blocco 3.1 — POI attualmente ancorato (0 = nessuno).
        // Scritto server-side da AnchorSystem durante le transizioni
        // Docking→Docked e Docked→Coasting. Contiene NetworkObjectId del
        // PoiInstance, permettendo a chiunque di risolvere il POI concreto
        // via NetworkManager.Singleton.SpawnManager.SpawnedObjects.
        private readonly NetworkVariable<ulong> _netAnchoredPoiId =
            new(0ul, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ── Runtime (server) ──────────────────────────────────────────────────
        private PropulsionUpgradeData _data;
        private PowerManager _powerManager;
        private bool _isPowered;
        private bool _ftlOverride;        // FTLDrive sopprime i motori
        private float _fuelAccumulator;    // fuel frazionario accumulato
        private float _fuelTickTimer;
        private float _manualThrottleInput; // Rev T — [-1, +1], W/S dal Pilota
        private const float FuelTickInterval = 1f;

        // ── Proprietà pubbliche ───────────────────────────────────────────────
        public NavigationState CurrentNavState => (NavigationState)_netNavState.Value;
        public bool AutopilotAvailable => _netAutopilotAvailable.Value;
        public float CurrentHealth => _netHealth.Value;
        public float CurrentHealthPercent => _data != null && _data.maxHealth > 0f
                                                  ? _netHealth.Value / _data.maxHealth : 1f;

        /// <summary>Velocità corrente reale (m/s) — accelera verso TargetSpeed.</summary>
        public float CurrentSpeed => _netCurrentSpeed.Value;

        /// <summary>Velocità target verso cui CurrentSpeed si smussa. In MANUAL
        /// è controllata dal throttle del Pilota, in AUTOPILOT è MaxSpeedAtDegradation.</summary>
        public float TargetSpeed => _netTargetSpeed.Value;

        /// <summary>Cap massimo del TargetSpeed dato il degrado attuale.</summary>
        public float MaxSpeedAtDegradation => _data != null
                                                  ? _data.maxSpeed * GetDegradationMults().speed : 0f;

        /// <summary>Accelerazione angolare yaw (deg/sec²) scalata dal degrado. Per ShipMovement.</summary>
        public float YawAcceleration => _data != null
                                        ? _data.yawAcceleration * GetDegradationMults().speed : 0f;

        /// <summary>Accelerazione angolare pitch (deg/sec²) scalata dal degrado. Per ShipMovement.</summary>
        public float PitchAcceleration => _data != null
                                          ? _data.pitchAcceleration * GetDegradationMults().speed : 0f;

        /// <summary>
        /// Fase 3 Blocco 3.1 — NetworkObjectId del POI attualmente ancorato,
        /// o 0 se la nave non è ancorata. Scritto server-side da AnchorSystem.
        /// Coerente con CurrentNavState: != 0 solo se stato è Docking o Docked.
        /// </summary>
        public ulong AnchoredPoiId => _netAnchoredPoiId.Value;

        // ── Lifecycle NGO ─────────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            Instance = this;

            if (allTiers != null && startingTierIndex < allTiers.Length)
                _data = allTiers[startingTierIndex];

            if (_data != null)
                _netHealth.Value = _data.maxHealth;

            _netNavState.Value = (int)startingState;
            _netAutopilotAvailable.Value = true;
            _netCurrentSpeed.Value = 0f;
            _netTargetSpeed.Value = 0f;
            _netAnchoredPoiId.Value = 0ul;

            _netHealth.OnValueChanged += OnHealthChanged;
            _netNavState.OnValueChanged += OnNavStateChanged;

            if (PowerManager.Instance != null)
                InitWithPowerManager();
            else
                PowerManager.OnInstanceReady += InitWithPowerManager;

            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            PowerManager.OnInstanceReady -= InitWithPowerManager;

            _netHealth.OnValueChanged -= OnHealthChanged;
            _netNavState.OnValueChanged -= OnNavStateChanged;

            if (IsServer && _isPowered && _powerManager != null)
                _powerManager.UnregisterPowerConsumer(this);

            if (Instance == this) Instance = null;
        }

        private void InitWithPowerManager()
        {
            PowerManager.OnInstanceReady -= InitWithPowerManager;
            _powerManager = PowerManager.Instance;
            if (!IsServer) return;

            _powerManager.RegisterPowerConsumer(this);
            _isPowered = startPowered;
        }

        // ── Update (server — fuel tick + throttle/speed) ──────────────────────
        private void Update()
        {
            if (!IsServer) return;

            UpdateThrottleAndSpeed();

            _fuelTickTimer += Time.deltaTime;
            if (_fuelTickTimer < FuelTickInterval) return;
            _fuelTickTimer = 0f;

            ConsumeFuelTick();
        }

        /// <summary>
        /// Rev T — server-only. Aggiorna TargetSpeed dallo stato + input Pilota,
        /// poi fa accelerare CurrentSpeed verso TargetSpeed con accelerationRate
        /// del data (scalato dal degrado).
        ///
        /// Comportamento per stato:
        ///   ANCHORED  → target/current forzati a 0 (già gestito in OnNavStateChanged,
        ///               ma tenuto qui come safety in caso di modifiche esterne)
        ///   COASTING  → target congelato, current inseguirà (di solito già uguale)
        ///   AUTOPILOT → target = MaxSpeedAtDegradation (si aggiorna col degrado)
        ///   MANUAL    → target += throttle × accelerationRate × dt (clamp)
        ///   DOCKING   → target/current forzati a 0 (Fase 3 3.1). Lo strafe RCS
        ///               è gestito da DockingController scrivendo direttamente
        ///               ship.LogicalPosition. Il throttle main è sospeso.
        ///   DOCKED    → target/current forzati a 0. Come Anchored ma con
        ///               AnchoredPoiId != 0.
        /// </summary>
        private void UpdateThrottleAndSpeed()
        {
            if (_data == null) return;

            // FTL override sopprime tutto — motori spenti
            if (_ftlOverride)
            {
                if (_netTargetSpeed.Value != 0f) _netTargetSpeed.Value = 0f;
                if (_netCurrentSpeed.Value != 0f) _netCurrentSpeed.Value = 0f;
                return;
            }

            float accel = _data.accelerationRate * GetDegradationMults().speed;
            float dt = Time.deltaTime;
            var navState = CurrentNavState;
            float maxCap = MaxSpeedAtDegradation;

            // 1. Aggiorna TargetSpeed in base allo stato
            switch (navState)
            {
                case NavigationState.Anchored:
                    _netTargetSpeed.Value = 0f;
                    break;

                case NavigationState.Autopilot:
                    // Segue automaticamente il cap del degrado
                    _netTargetSpeed.Value = maxCap;
                    break;

                case NavigationState.Manual:
                    // Throttle sposta il target con lo stesso rate dell'accelerazione
                    if (!Mathf.Approximately(_manualThrottleInput, 0f))
                    {
                        float newTarget = _netTargetSpeed.Value
                                        + _manualThrottleInput * accel * dt;
                        _netTargetSpeed.Value = Mathf.Clamp(newTarget, 0f, maxCap);
                    }
                    else
                    {
                        // Rilasciato — clampa comunque per gestire cambio degrado runtime
                        _netTargetSpeed.Value = Mathf.Clamp(_netTargetSpeed.Value, 0f, maxCap);
                    }
                    break;

                case NavigationState.Coasting:
                    // Target invariato — inerzia nello spazio vuoto
                    break;

                case NavigationState.Docking:
                case NavigationState.Docked:
                    // Fase 3 Blocco 3.1 — throttle main sospeso, tutto a 0.
                    // Il DockingController gestisce lo strafe scrivendo
                    // direttamente ship.LogicalPosition.
                    _netTargetSpeed.Value = 0f;
                    break;
            }

            // 2. Smoothing CurrentSpeed → TargetSpeed
            float current = _netCurrentSpeed.Value;
            float target = _netTargetSpeed.Value;

            if (!Mathf.Approximately(current, target))
            {
                float diff = target - current;
                float step = accel * dt;

                if (Mathf.Abs(diff) <= step)
                    _netCurrentSpeed.Value = target;
                else
                    _netCurrentSpeed.Value = current + Mathf.Sign(diff) * step;
            }
        }

        // ── IPowerConsumer ────────────────────────────────────────────────────
        public float GetPowerDemand()
        {
            if (_data == null || !_isPowered || _ftlOverride) return 0f;

            return CurrentNavState switch
            {
                NavigationState.Autopilot => _data.wattsAutopilot * GetDegradationMults().watts,
                NavigationState.Manual => _data.wattsManual * GetDegradationMults().watts,
                NavigationState.Docking => _data.wattsDocking * GetDegradationMults().watts,
                // Anchored, Coasting, Docked → 0W
                _ => 0f
            };
        }

        public int GetPriority() => _data?.powerPriority ?? 6;
        public bool IsActive() => _isPowered;
        public bool CanBeDisabled() => true;
        public string GetSystemName() => _data?.displayName ?? "Propulsion System";

        public void SetPowerState(bool isOn)
        {
            if (!IsServer || _isPowered == isOn) return;
            _isPowered = isOn;

            if (!isOn)
            {
                // Perdita energia → coasting forzato.
                // Se in Docking/Docked, il pilota viene sbalzato in Coasting
                // (l'ancoraggio non sopravvive alla perdita di potenza —
                // AnchorSystem ripulirà AnchoredPoiId nel suo callback su
                // OnNavStateChanged. In 3.1.1 lo azzeriamo qui direttamente
                // per sicurezza, anche se ridondante.)
                if (_netAnchoredPoiId.Value != 0ul)
                    _netAnchoredPoiId.Value = 0ul;

                SetNavStateInternal(NavigationState.Coasting);
                Debug.LogWarning("[PropulsionSystem] Power OFF — COASTING forzato");
            }
            else
            {
                Debug.Log("[PropulsionSystem] Power ON");
            }
        }

        // ── IRepairable ───────────────────────────────────────────────────────
        string IRepairable.GetSystemName() => GetSystemName();
        ShipSystemState IRepairable.GetCurrentState() => HealthToState(CurrentHealthPercent);
        float IRepairable.GetHealthPercent() => CurrentHealthPercent;
        bool IRepairable.IsRepairable() => CurrentHealthPercent < 0.75f;

        RepairThreshold[] IRepairable.GetRepairThresholds()
            => _data?.repairThresholds ?? Array.Empty<RepairThreshold>();

        void IRepairable.ApplyRepair(float progressPercent)
        {
            if (!IsServer || _data == null) return;

            float targetHP = _data.maxHealth * (progressPercent / 100f);
            _netHealth.Value = Mathf.Max(_netHealth.Value, targetHP);

            Debug.Log($"[PropulsionSystem] Repair {progressPercent}% → HP {_netHealth.Value:F0}/{_data.maxHealth}");
        }

        // ── API Pubblica ──────────────────────────────────────────────────────

        /// <summary>
        /// Richiede cambio di stato di navigazione.
        /// Validazione: non AUTOPILOT se AutopilotAvailable=false,
        /// non MANUAL/AUTOPILOT/DOCKING/DOCKED se sistema OFFLINE o FTL attivo.
        /// Chiamabile da qualsiasi client (PilotStation, AnchorSystem).
        ///
        /// NOTA Fase 3 Blocco 3.1:
        ///   Docking/Docked sono tipicamente richiesti solo da AnchorSystem
        ///   (che valida ulteriormente la presenza di un candidato ancorabile),
        ///   ma questa API non blocca ingressi arbitrari — la validazione
        ///   aggiuntiva è responsabilità del chiamante. In particolare,
        ///   l'ingresso a Docked senza aver prima settato AnchoredPoiId
        ///   produce uno stato "docked a nessuno", inconsistente ma non
        ///   crashogeno; TODO in 3.1.2 valutare se rendere hard-only via
        ///   metodo dedicato.
        /// </summary>
        public void RequestNavigationState(NavigationState newState)
        {
            if (IsServer) RequestNavStateInternal(newState);
            else RequestNavStateRpc(newState);
        }

        [Rpc(SendTo.Server)]
        private void RequestNavStateRpc(NavigationState s) => RequestNavStateInternal(s);

        private void RequestNavStateInternal(NavigationState newState)
        {
            // Validazione
            if (newState == NavigationState.Autopilot && !_netAutopilotAvailable.Value)
            {
                Debug.LogWarning("[PropulsionSystem] Autopilota non disponibile (AsteroidField attivo).");
                return;
            }

            if (_ftlOverride && newState != NavigationState.Anchored)
            {
                Debug.LogWarning("[PropulsionSystem] FTL in corso — solo ANCHORED permesso.");
                return;
            }

            // OFFLINE: solo Coasting/Anchored permessi. Docking/Docked bloccati
            // automaticamente da questo check perché non compaiono nell'allowlist.
            if (HealthToState(CurrentHealthPercent) == ShipSystemState.Offline
                && newState != NavigationState.Coasting
                && newState != NavigationState.Anchored)
            {
                Debug.LogWarning("[PropulsionSystem] Sistema OFFLINE — solo COASTING/ANCHORED.");
                return;
            }

            SetNavStateInternal(newState);
        }

        /// <summary>
        /// Fase 3 Blocco 3.1 — setta il POI attualmente ancorato (o 0 per
        /// disancorare). Server-only, chiamato da AnchorSystem durante le
        /// transizioni Docking→Docked (set) e Docked→Coasting (clear).
        /// NON esegue di per sé cambio di NavigationState: quello resta
        /// responsabilità di AnchorSystem tramite RequestNavigationState.
        /// </summary>
        public void SetAnchoredPoiId(ulong poiNetworkObjectId)
        {
            if (!IsServer)
            {
                Debug.LogError("[PropulsionSystem] SetAnchoredPoiId called on client — ignored.");
                return;
            }
            _netAnchoredPoiId.Value = poiNetworkObjectId;
        }

        /// <summary>
        /// Chiamato da ZoneManager quando ZoneEvent.AsteroidField è attivo/inattivo.
        /// Aggiorna il parametro passivo di disponibilità autopilota.
        /// NON cambia lo stato di navigazione corrente — è il Pilota a decidere.
        /// </summary>
        public void SetAutopilotAvailable(bool available)
        {
            if (IsServer) SetAutopilotAvailableInternal(available);
            else SetAutopilotAvailableRpc(available);
        }

        [Rpc(SendTo.Server)]
        private void SetAutopilotAvailableRpc(bool v) => SetAutopilotAvailableInternal(v);

        private void SetAutopilotAvailableInternal(bool available)
        {
            _netAutopilotAvailable.Value = available;

            // Se autopilota è attivo e viene disabilitato: forza COASTING
            if (!available && CurrentNavState == NavigationState.Autopilot)
            {
                SetNavStateInternal(NavigationState.Coasting);
                Debug.LogWarning("[PropulsionSystem] Autopilota disabilitato (AsteroidField) → COASTING");
            }
        }

        /// <summary>
        /// Chiamato da FTLDrive durante la carica.
        /// Sopprime i motori (0W, 0 fuel, target/current = 0) e forza ANCHORED.
        /// NON è un comando di protezione — è un vincolo fisico del salto FTL.
        /// </summary>
        public void SetFTLOverride(bool ftlActive)
        {
            if (!IsServer) return;
            _ftlOverride = ftlActive;

            if (ftlActive)
            {
                // Se ancorati, disancoriamo prima del salto FTL.
                if (_netAnchoredPoiId.Value != 0ul)
                    _netAnchoredPoiId.Value = 0ul;

                SetNavStateInternal(NavigationState.Anchored);
                _netTargetSpeed.Value = 0f;
                _netCurrentSpeed.Value = 0f;
                _fuelAccumulator = 0f;
                _manualThrottleInput = 0f;
                Debug.Log("[PropulsionSystem] FTL override ON — motori spenti");
            }
            else
            {
                Debug.Log("[PropulsionSystem] FTL override OFF — motori disponibili");
            }
        }

        /// <summary>Applica danno al sistema (da ShipMovement M3 o debug).</summary>
        public void ApplyDamage(float amount)
        {
            if (IsServer) ApplyDamageInternal(amount);
            else ApplyDamageRpc(amount);
        }

        [Rpc(SendTo.Server)]
        private void ApplyDamageRpc(float amount) => ApplyDamageInternal(amount);

        private void ApplyDamageInternal(float amount)
        {
            if (_data == null) return;
            _netHealth.Value = Mathf.Clamp(_netHealth.Value - amount, 0f, _data.maxHealth);
        }

        /// <summary>
        /// Rev T — chiamato da PilotStation, una volta per frame, mentre il Pilota
        /// è seduto e NavigationState == Manual. throttle atteso in [-1, +1]:
        /// +1 = W tenuto (accelera in avanti), -1 = S tenuto (decelera),
        /// 0 = nessun input (target congelato).
        ///
        /// Il target viene spostato con lo stesso rate dell'accelerazione del data —
        /// scelta di design: sensazione uniforme "leva che risponde con la stessa
        /// inerzia della nave", nessuna dissociazione tra "quanto veloce muovo
        /// la leva" e "quanto veloce risponde la nave".
        ///
        /// NOTA Fase 3 3.1: in Docking/Docked il throttle input viene ignorato
        /// dall'UpdateThrottleAndSpeed (target forzato a 0). Il PilotStation
        /// dovrebbe già smettere di inviare throttle in questi stati, ma se
        /// arriva comunque, viene semplicemente scritto in _manualThrottleInput
        /// senza effetto — nessun rischio.
        /// </summary>
        public void SetManualThrottleInput(float throttle)
        {
            float clamped = Mathf.Clamp(throttle, -1f, 1f);

            if (IsServer) _manualThrottleInput = clamped;
            else SetManualThrottleInputRpc(clamped);
        }

        [Rpc(SendTo.Server)]
        private void SetManualThrottleInputRpc(float throttle) => _manualThrottleInput = throttle;

        /// <summary>
        /// Blocco 3.2.c — server-only setter di CurrentSpeed dedicato al
        /// PoiCollisionResolver. Chiamato quando il clamp posizionale contro
        /// un POI ha ridotto la velocità della nave: il resolver calcola il
        /// nuovo scalare CurrentSpeed proiettando la velocità tangenziale
        /// post-clamp su LogicalForward e passa il risultato qui.
        ///
        /// SEMANTICA: forza CurrentSpeed a newSpeed, clampato in
        /// [-MaxSpeedAtDegradation, +MaxSpeedAtDegradation] per coerenza col
        /// range fisico del sistema. TargetSpeed NON viene toccato — se il
        /// pilota tiene W premuto, il tick successivo lo smoothing
        /// UpdateThrottleAndSpeed rialzerà CurrentSpeed verso TargetSpeed e
        /// il resolver reintarba (PA1.a confermato Rev AA — "martellamento"
        /// contro la mesh come feedback fisico voluto).
        ///
        /// Guard su !IsServer coerente con SetLogicalPosition di ShipMovement
        /// e SetAnchoredPoiId (Fase 3 Blocco 3.1). Nessun RPC: il chiamante
        /// (PoiCollisionResolver) gira server-side per costruzione.
        /// </summary>
        public void SetCurrentSpeedFromCollision(float newSpeed)
        {
            if (!IsServer)
            {
                Debug.LogError("[PropulsionSystem] SetCurrentSpeedFromCollision called on client — ignored.");
                return;
            }

            float cap = MaxSpeedAtDegradation;
            // Se il data non è ancora inizializzato (edge case boot), non
            // possiamo clampare — meglio saltare la scrittura che scrivere
            // un valore fuori range.
            if (cap <= 0f) return;

            _netCurrentSpeed.Value = Mathf.Clamp(newSpeed, -cap, cap);
        }

        // ── Fuel Consumption ──────────────────────────────────────────────────
        private void ConsumeFuelTick()
        {
            if (!_isPowered || _data == null || InventorySystem.Instance == null) return;

            // Solo Autopilot e Manual consumano carburante.
            // Docking/Docked/Coasting/Anchored → 0 fuel.
            // (Il consumo Docking è solo elettrico via wattsDocking; MVP scelta —
            //  se emergerà exploit "resto in Docking a costo zero", valutare
            //  aggiunta di fuelPerMinDocking basso.)
            var state = CurrentNavState;
            if (state != NavigationState.Autopilot && state != NavigationState.Manual) return;

            float fuelPerMin = state == NavigationState.Autopilot
                ? _data.fuelPerMinAutopilot
                : _data.fuelPerMinManual;

            fuelPerMin *= GetDegradationMults().fuel;
            _fuelAccumulator += (fuelPerMin / 60f) * FuelTickInterval;

            int toConsume = Mathf.FloorToInt(_fuelAccumulator);
            if (toConsume <= 0) return;

            bool consumed = InventorySystem.Instance.TryConsume(ItemType.FuelCell, toConsume);
            if (consumed)
            {
                _fuelAccumulator -= toConsume;
            }
            else
            {
                Debug.LogWarning("[PropulsionSystem] Carburante esaurito → COASTING");
                SetNavStateInternal(NavigationState.Coasting);
                _fuelAccumulator = 0f;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void SetNavStateInternal(NavigationState s)
        {
            _netNavState.Value = (int)s;
        }

        private static ShipSystemState HealthToState(float percent)
        {
            if (percent >= 0.75f) return ShipSystemState.Online;
            if (percent >= 0.50f) return ShipSystemState.DegradedLight;
            if (percent >= 0.25f) return ShipSystemState.DegradedHeavy;
            return ShipSystemState.Offline;
        }

        private struct DegradationMults { public float speed, watts, fuel; }

        private DegradationMults GetDegradationMults()
        {
            if (_data == null) return new DegradationMults { speed = 1, watts = 1, fuel = 1 };

            int idx = CurrentHealthPercent >= 0.75f ? 0
                    : CurrentHealthPercent >= 0.50f ? 1
                    : 2;

            float SafeGet(float[] arr, int i) =>
                arr != null && i < arr.Length ? arr[i] : 1f;

            return new DegradationMults
            {
                speed = SafeGet(_data.speedMultipliers, idx),
                watts = SafeGet(_data.wattsMultipliers, idx),
                fuel = SafeGet(_data.fuelMultipliers, idx)
            };
        }

        // ── Callback NetworkVariables ─────────────────────────────────────────
        private void OnHealthChanged(float prev, float curr)
        {
            // OFFLINE: forza COASTING se non già in stato statico
            if (IsServer && HealthToState(CurrentHealthPercent) == ShipSystemState.Offline)
            {
                var state = CurrentNavState;
                if (state == NavigationState.Autopilot
                    || state == NavigationState.Manual
                    || state == NavigationState.Docking
                    || state == NavigationState.Docked)
                {
                    // Se ancorati, disancoriamo forzatamente.
                    if (_netAnchoredPoiId.Value != 0ul)
                        _netAnchoredPoiId.Value = 0ul;

                    SetNavStateInternal(NavigationState.Coasting);
                }
            }

            // Notifica PowerManager per aggiornare il demand
            // (GetPowerDemand() viene ricalcolato automaticamente al prossimo frame)
        }

        private void OnNavStateChanged(int prev, int curr)
        {
            var newState = (NavigationState)curr;

            // Setup target/current in base al nuovo stato (server-only)
            if (IsServer)
            {
                _fuelAccumulator = 0f;

                switch (newState)
                {
                    case NavigationState.Anchored:
                        // Snap immediato a 0
                        _netTargetSpeed.Value = 0f;
                        _netCurrentSpeed.Value = 0f;
                        _manualThrottleInput = 0f;
                        break;

                    case NavigationState.Coasting:
                        // Freeze inerzia: target = current attuale
                        _netTargetSpeed.Value = _netCurrentSpeed.Value;
                        _manualThrottleInput = 0f;
                        break;

                    case NavigationState.Autopilot:
                        // Target = max cap (poi UpdateThrottleAndSpeed lo mantiene aggiornato)
                        _netTargetSpeed.Value = MaxSpeedAtDegradation;
                        _manualThrottleInput = 0f;
                        break;

                    case NavigationState.Manual:
                        // Continua da dov'era — il Pilota controllerà con throttle
                        // (target invariato, _manualThrottleInput azzerato per pulizia,
                        // sarà il PilotStation a impostarlo ogni frame)
                        _manualThrottleInput = 0f;
                        break;

                    case NavigationState.Docking:
                    case NavigationState.Docked:
                        // Fase 3 3.1 — snap a 0 come Anchored. Lo strafe RCS
                        // (in Docking) è gestito da DockingController scrivendo
                        // direttamente ship.LogicalPosition.
                        _netTargetSpeed.Value = 0f;
                        _netCurrentSpeed.Value = 0f;
                        _manualThrottleInput = 0f;
                        break;
                }
            }

            Debug.Log($"[PropulsionSystem] NavState → {newState}" +
                      $" | Target: {_netTargetSpeed.Value:F1} m/s · Demand: {GetPowerDemand():F1}W");
        }

        // ── Upgrade ───────────────────────────────────────────────────────────
        public void ApplyUpgrade(int tierIndex)
        {
            if (IsServer) ApplyUpgradeInternal(tierIndex);
            else ApplyUpgradeRpc(tierIndex);
        }

        [Rpc(SendTo.Server)]
        private void ApplyUpgradeRpc(int i) => ApplyUpgradeInternal(i);

        private void ApplyUpgradeInternal(int tierIndex)
        {
            if (allTiers == null || tierIndex < 0 || tierIndex >= allTiers.Length) return;
            var newData = allTiers[tierIndex];
            if (newData == null || newData.tier <= (_data?.tier ?? 0)) return;

            float prevMaxHP = _data?.maxHealth ?? 100f;
            float hpRatio = _netHealth.Value / prevMaxHP;

            _data = newData;
            _netHealth.Value = _data.maxHealth * hpRatio;

            Debug.Log($"[PropulsionSystem] Upgraded to {_data.displayName}");
        }

        // ── Debug GUI ─────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            var mults = GetDegradationMults();
            GUILayout.BeginArea(new Rect(Screen.width - 260, 10, 250, 400));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[Propulsion] {(IsServer ? "SRV" : "CLT")}");
            GUILayout.Label($"State:  {CurrentNavState}");
            GUILayout.Label($"HP:     {CurrentHealth:F0}/{(_data?.maxHealth ?? 0):F0} ({CurrentHealthPercent * 100:F0}%)");
            GUILayout.Label($"Status: {HealthToState(CurrentHealthPercent)}");
            GUILayout.Label($"Demand: {GetPowerDemand():F1}W");
            GUILayout.Label($"Speed:  {CurrentSpeed:F1} / {TargetSpeed:F1} (max {MaxSpeedAtDegradation:F0}) m/s");
            GUILayout.Label($"Accel:  {(_data?.accelerationRate ?? 0):F1} × {mults.speed:F2} m/s²");
            GUILayout.Label($"Autopilot: {(_netAutopilotAvailable.Value ? "OK" : "DISABLED")}");
            GUILayout.Label($"FTL Override: {_ftlOverride}");
            GUILayout.Label($"AnchoredPoi: {_netAnchoredPoiId.Value}");

            if (IsServer)
            {
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Anchored")) RequestNavStateInternal(NavigationState.Anchored);
                if (GUILayout.Button("Autopilot")) RequestNavStateInternal(NavigationState.Autopilot);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Manual")) RequestNavStateInternal(NavigationState.Manual);
                if (GUILayout.Button("Coasting")) RequestNavStateInternal(NavigationState.Coasting);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Docking")) RequestNavStateInternal(NavigationState.Docking);
                if (GUILayout.Button("Docked")) RequestNavStateInternal(NavigationState.Docked);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Throttle+")) _manualThrottleInput = +1f;
                if (GUILayout.Button("Throttle-")) _manualThrottleInput = -1f;
                if (GUILayout.Button("Throttle 0")) _manualThrottleInput = 0f;
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("-20 HP")) ApplyDamageInternal(20f);
                if (GUILayout.Button("-50 HP")) ApplyDamageInternal(50f);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Repair 100%")) ((IRepairable)this).ApplyRepair(100f);
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}