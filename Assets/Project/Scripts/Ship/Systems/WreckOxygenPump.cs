using System;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.Ship
{
    /// <summary>Modalita del pump O2 relitto (Rev BG - Stage B).</summary>
    public enum WreckPumpMode { Off, Supply, Harvest }

    /// <summary>
    /// WreckOxygenPump — pump O2 del relitto (Rev BG - Stage B).
    ///
    /// Toggle Off/Supply/Harvest. Opera solo se l'ombelicale alimenta il relitto
    /// (WreckUmbilical.IsPowered — Q0-a, pump accoppiato all'ombelicale).
    ///   - Harvest (ATTIVO): travasa il residuo del relitto (WreckOxygenReserve) nel
    ///     tank nave (OxygenSystem) a rate da tuning. Withdraw + TransferShipOxygen +
    ///     Refund dell'eccesso: nessun O2 creato o perso.
    ///   - Supply (PREDISPOSTO-INERTE): immettere O2 nel relitto per pressurizzarlo ha
    ///     senso solo con crew FISICAMENTE dentro (EVA/boarding — D22/D31, non ancora
    ///     implementati). Finche non esistono, Supply e un no-op: il toggle lo accetta
    ///     e lo mostra, ma non trasferisce nulla. Si attivera senza rilavorare l'impianto.
    ///
    /// Server-authoritative. Singleton di nave.
    /// </summary>
    public class WreckOxygenPump : NetworkBehaviour
    {
        public static WreckOxygenPump Instance { get; private set; }
        public static event Action OnInstanceReady;

        [SerializeField] private WreckOperationsTuning tuning;
        [SerializeField] private bool logVerbose = false;

        private readonly NetworkVariable<WreckPumpMode> netMode =
            new NetworkVariable<WreckPumpMode>(WreckPumpMode.Off,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        public WreckPumpMode Mode => netMode.Value;
        public event Action<WreckPumpMode> OnModeChanged;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            netMode.OnValueChanged += HandleModeChanged;
            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            netMode.OnValueChanged -= HandleModeChanged;
            if (Instance == this) Instance = null;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void HandleModeChanged(WreckPumpMode _, WreckPumpMode v) => OnModeChanged?.Invoke(v);

        // ── Comando (UI) ────────────────────────────────────────────────────────

        /// <summary>Imposta la modalita del pump (comando dell'Ingegnere).</summary>
        [Rpc(SendTo.Server)]
        public void SetPumpModeServerRpc(WreckPumpMode mode)
        {
            netMode.Value = mode;
            LogV("[WreckOxygenPump] Modalita: " + mode);
        }

        // ── Travaso (solo server) ─────────────────────────────────────────────────

        private void Update()
        {
            if (!IsServer) return;

            // Off e Supply non travasano: Off per scelta, Supply perche inerte finche
            // non esiste EVA/boarding (D22/D31).
            if (netMode.Value != WreckPumpMode.Harvest) return;

            // Il pump opera solo con ombelicale attivo (Q0-a).
            var umb = WreckUmbilical.Instance;
            if (umb == null || !umb.IsPowered) return;

            var reserve = umb.CurrentReserve;
            var ship = OxygenSystem.Instance;
            if (reserve == null || ship == null || tuning == null) return;

            float want = tuning.harvestRatePerSecond * Time.deltaTime;
            if (want <= 0f) return;

            float taken = reserve.Withdraw(want);
            if (taken <= 0f) return; // residuo esaurito

            float accepted = ship.TransferShipOxygen(taken);
            float leftover = taken - accepted;
            if (leftover > 0f) reserve.Refund(leftover); // tank saturo: nessuna perdita
        }

        private void LogV(string m) { if (logVerbose) Debug.Log(m); }
    }
}