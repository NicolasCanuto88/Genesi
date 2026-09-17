using System;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// WreckOxygenReserve — pool di O2 residuo di un relitto (Rev BG - Stage B).
    ///
    /// Terzo pilastro dell'O2 (D26): oltre al tank nave (OxygenSystem) e al tank
    /// personale (PlayerOxygen), il residuo appartiene AL RELITTO. Vive sul prefab
    /// del relitto, accanto a PoiInstance; il WreckOxygenPump lo preleva in Harvest
    /// e lo versa nel tank nave. Scala 0-100, coerente col tank nave (Q-impl1a).
    ///
    /// Server-authoritative: NetworkVariable letta da tutti, scritta dal server.
    /// Inizializzato da PoiData.WreckO2ReserveInitial in OnNetworkSpawn.
    ///
    /// L'invariante "tre pool non intercomunicano se non via trasferimento esplicito"
    /// resta rispettata: il travaso passa SEMPRE per Withdraw + pump + TransferShipOxygen.
    /// </summary>
    [RequireComponent(typeof(PoiInstance))]
    public class WreckOxygenReserve : NetworkBehaviour
    {
        private readonly NetworkVariable<float> netResidual =
            new NetworkVariable<float>(0f,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        /// <summary>O2 residuo corrente (scala 0-100). Leggibile da tutti.</summary>
        public float Residual => netResidual.Value;

        /// <summary>
        /// Residuo iniziale del relitto, da PoiData. Derivato localmente (PoiData e
        /// uno ScriptableObject presente su tutti via PoiInstance), quindi valido
        /// anche sui client per calcolare la percentuale in UI.
        /// </summary>
        public float InitialResidual =>
            (_poi != null && _poi.Data != null) ? _poi.Data.WreckO2ReserveInitial : 0f;

        /// <summary>Emesso su tutti al cambio del residuo. Per la UI.</summary>
        public event Action<float> OnResidualChanged;

        private PoiInstance _poi;

        private void Awake()
        {
            _poi = GetComponent<PoiInstance>();
        }

        public override void OnNetworkSpawn()
        {
            netResidual.OnValueChanged += HandleResidualChanged;

            if (IsServer)
                netResidual.Value = InitialResidual;
        }

        public override void OnNetworkDespawn()
        {
            netResidual.OnValueChanged -= HandleResidualChanged;
        }

        private void HandleResidualChanged(float _, float newVal) => OnResidualChanged?.Invoke(newVal);

        /// <summary>
        /// [Server] Preleva fino a 'amount' O2 dal residuo. Ritorna la quantita
        /// EFFETTIVAMENTE prelevata (minore di 'amount' se il residuo si esaurisce).
        /// </summary>
        public float Withdraw(float amount)
        {
            if (!IsServer || amount <= 0f) return 0f;
            float available = netResidual.Value;
            float taken = Mathf.Min(amount, available);
            netResidual.Value = available - taken;
            return taken;
        }

        /// <summary>
        /// [Server] Restituisce al pool l'eccesso non accettato dal tank nave (quando
        /// il tank satura durante l'Harvest), senza mai superare il residuo iniziale.
        /// Garantisce che nessun O2 venga distrutto nel travaso.
        /// </summary>
        public void Refund(float amount)
        {
            if (!IsServer || amount <= 0f) return;
            netResidual.Value = Mathf.Min(netResidual.Value + amount, InitialResidual);
        }
    }
}