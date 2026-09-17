using System;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// WreckUmbilical — alimentazione remota del relitto ancorato (Rev BG - Stage B).
    ///
    /// Quando l'Ingegnere lo attiva, la nave alimenta il relitto attualmente ANCORATO
    /// (PropulsionSystem.Docked + AnchoredPoiId). Consuma potenza dal PowerManager in
    /// proporzione alla massa del POI, con un tetto (WreckOperationsTuning). Essendo un
    /// "lusso" ha priorita bassa: primo IPowerConsumer sheddato in deficit, spento in blackout.
    ///
    /// E' l'unica fonte di verita su "quale relitto e alimentato": espone IsPowered e la
    /// WreckOxygenReserve del relitto ancorato, che il WreckOxygenPump legge per l'Harvest
    /// (il pump opera solo se IsPowered — Q0-a, pump accoppiato all'ombelicale).
    ///
    /// Semantica IPowerConsumer (coerente con PowerManager):
    ///   - GetPowerDemand(): domanda TEORICA (richiesto + relitto ancorato), indipendente
    ///     dallo shedding, cosi RestoreLoad conosce il costo di riaccensione.
    ///   - IsActive(): sta consumando ORA (richiesto + ancorato + non sheddato).
    ///
    /// Server-authoritative. Singleton di nave, come OxygenSystem/PowerManager.
    /// </summary>
    public class WreckUmbilical : NetworkBehaviour, IPowerConsumer
    {
        public static WreckUmbilical Instance { get; private set; }
        public static event Action OnInstanceReady;

        [SerializeField] private WreckOperationsTuning tuning;
        [SerializeField] private bool logVerbose = false;

        // Richiesta utente (via UI/RPC): l'ombelicale deve essere acceso.
        private readonly NetworkVariable<bool> netRequested =
            new NetworkVariable<bool>(false,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // Stato reale: sta alimentando (requested + ancorato + non sheddato). Per la UI client.
        private readonly NetworkVariable<bool> netPowered =
            new NetworkVariable<bool>(false,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // Server-only: il PowerManager non ci ha sheddati.
        private bool _managerAllowed = true;

        public bool IsRequested => netRequested.Value;
        public bool IsPowered => netPowered.Value;

        public event Action<bool> OnRequestedChanged;
        public event Action<bool> OnPoweredChanged;

        /// <summary>[Server] WreckOxygenReserve del relitto ancorato, o null. Usata dal pump.</summary>
        public WreckOxygenReserve CurrentReserve
        {
            get
            {
                var poi = ResolveAnchoredWreck();
                return poi != null ? poi.GetComponent<WreckOxygenReserve>() : null;
            }
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;

            netRequested.OnValueChanged += HandleRequestedChanged;
            netPowered.OnValueChanged += HandlePoweredChanged;

            OnInstanceReady?.Invoke();

            if (IsServer)
                RegisterToPowerManager();
        }

        public override void OnNetworkDespawn()
        {
            netRequested.OnValueChanged -= HandleRequestedChanged;
            netPowered.OnValueChanged -= HandlePoweredChanged;

            if (IsServer && PowerManager.Instance != null)
                PowerManager.Instance.UnregisterPowerConsumer(this);

            if (Instance == this) Instance = null;
        }

        private void OnDestroy()
        {
            PowerManager.OnInstanceReady -= RegisterToPowerManager;
            if (Instance == this) Instance = null;
        }

        private void HandleRequestedChanged(bool _, bool v) => OnRequestedChanged?.Invoke(v);
        private void HandlePoweredChanged(bool _, bool v) => OnPoweredChanged?.Invoke(v);

        private void RegisterToPowerManager()
        {
            if (PowerManager.Instance != null)
            {
                PowerManager.Instance.RegisterPowerConsumer(this);
                PowerManager.OnInstanceReady -= RegisterToPowerManager;
            }
            else
            {
                PowerManager.OnInstanceReady += RegisterToPowerManager;
            }
        }

        // ── Comando (UI) ────────────────────────────────────────────────────────

        /// <summary>Accende/spegne l'ombelicale su richiesta dell'Ingegnere.</summary>
        [Rpc(SendTo.Server)]
        public void RequestUmbilicalServerRpc(bool on)
        {
            netRequested.Value = on;
            LogV("[WreckUmbilical] Richiesta ombelicale: " + on);
        }

        // ── Stato server ─────────────────────────────────────────────────────────

        private void Update()
        {
            if (!IsServer) return;

            bool active = IsActive();
            if (netPowered.Value != active)
                netPowered.Value = active;
        }

        private bool WreckAnchored() => ResolveAnchoredWreck() != null;

        private PoiInstance ResolveAnchoredWreck()
        {
            var prop = PropulsionSystem.Instance;
            if (prop == null || prop.CurrentNavState != NavigationState.Docked) return null;

            ulong id = prop.AnchoredPoiId;
            if (id == 0) return null;

            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) return null;
            if (nm.SpawnManager.SpawnedObjects.TryGetValue(id, out var no) && no != null)
                return no.GetComponent<PoiInstance>();
            return null;
        }

        private float ComputeDemand()
        {
            var poi = ResolveAnchoredWreck();
            if (poi == null || poi.Data == null || tuning == null) return 0f;
            float demand = tuning.umbilicalBaseWatt + poi.Data.Mass * tuning.umbilicalWattPerMass;
            return Mathf.Min(demand, tuning.umbilicalMaxWatt);
        }

        // ── IPowerConsumer ─────────────────────────────────────────────────────────

        public float GetPowerDemand()
            => (netRequested.Value && WreckAnchored()) ? ComputeDemand() : 0f;

        public bool IsActive()
            => netRequested.Value && _managerAllowed && WreckAnchored();

        public bool CanBeDisabled() => true;

        public void SetPowerState(bool isOn) => _managerAllowed = isOn;

        public int GetPriority() => tuning != null ? tuning.umbilicalPriority : 2;

        public string GetSystemName() => "Wreck Umbilical";

        private void LogV(string m) { if (logVerbose) Debug.Log(m); }
    }
}