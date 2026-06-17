using System;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// EconomyManager — Milestone 2.
    /// Gestisce il Fleet Account: il conto condiviso della nave/lobby.
    ///
    /// REGOLE DI DESIGN (decise dall'utente, sessione Rev N):
    ///   - NESSUNA distribuzione automatica di crediti ai singoli giocatori.
    ///   - Tutti i pagamenti (missioni, vendite) finiscono SEMPRE sul Fleet Account,
    ///     mai direttamente in un Personal Account.
    ///   - Solo l'HOST della sessione può trasferire crediti dal Fleet Account al
    ///     Personal Account di un membro dell'equipaggio — decisione manuale,
    ///     nessun automatismo.
    ///   - I crediti personali NON sono sincronizzati in rete da questo sistema:
    ///     vivono nel salvataggio locale del personaggio (LocalCharacterProfile)
    ///     e seguono il giocatore tra sessioni/host diversi (GDD §2B "Personal
    ///     Account" — "si portano dietro cambiando lobby").
    ///
    /// Fleet Account: NetworkVariable, SESSION-ONLY in M2 (si azzera a fine sessione,
    /// nessun salvataggio). La persistenza del Fleet Account tra sessioni della STESSA
    /// nave è debito tecnico già segnalato in Rev M ("tecnologia da scegliere in M3 —
    /// Steam Cloud / file / backend") — non affrontata qui.
    ///
    /// AUTORITÀ HOST: verificata server-side via RpcParams.Receive.SenderClientId ==
    /// NetworkManager.ServerClientId. Questo check è "migration-safe": se in futuro
    /// un altro giocatore assume il ruolo di host tecnico (GDD §2B), il controllo
    /// segue automaticamente il nuovo ServerClientId — nessuna logica aggiuntiva.
    ///
    /// ⚠️ Dipende da: nessuno (standalone, come InventorySystem).
    /// ⚠️ Setup scena: questo componente richiede un NetworkObject sul proprio
    ///    GameObject, NON nidificato sotto un altro NetworkObject — stessa lezione
    ///    imparata con RepairPanel (Rev M, bug #33): altrimenti gli Rpc falliscono
    ///    con "NetworkBehaviour must be spawned".
    /// ⚠️ Sintassi RpcTarget.Single/RpcTargetUse.Temp da verificare in Editor contro
    ///    NGO v2.11.2 — scritta secondo l'API del changelog Rpc overhaul; se non
    ///    compila, sostituire con l'equivalente corrente per il targeting di un
    ///    singolo client.
    /// </summary>
    public class EconomyManager : NetworkBehaviour
    {
        public static EconomyManager Instance { get; private set; }

        /// <summary>Fired quando EconomyManager è pronto (dopo OnNetworkSpawn) — pattern OnInstanceReady esistente.</summary>
        public static event Action OnInstanceReady;

        /// <summary>Fired su tutti i client quando il saldo del Fleet Account cambia.</summary>
        public static event Action<int> OnFleetCreditsChanged;

        // ── NetworkVariable (server scrive, tutti leggono) ───────────────────
        private readonly NetworkVariable<int> netFleetCredits =
            new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Saldo corrente del Fleet Account. Leggibile da qualsiasi client.</summary>
        public int FleetCredits => netFleetCredits.Value;

        // ── Lifecycle NGO ──────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            Instance = this;
            netFleetCredits.OnValueChanged += HandleFleetCreditsChanged;
            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            netFleetCredits.OnValueChanged -= HandleFleetCreditsChanged;
            if (Instance == this) Instance = null;
        }

        private void HandleFleetCreditsChanged(int previous, int current)
            => OnFleetCreditsChanged?.Invoke(current);

        // ── API pubblica — entrate ────────────────────────────────────────

        /// <summary>
        /// Aggiunge crediti al Fleet Account. SERVER ONLY.
        /// Chiamata futura da: sistema Missioni / sistema Vendite (M3+ — non ancora
        /// implementati: "dipende da" finché quei sistemi non esistono).
        /// In M2 è richiamabile solo dai pulsanti di debug (OnGUI) per testare la UI
        /// del Tablet senza dover aspettare le missioni reali.
        /// </summary>
        public void AddFleetCredits(int amount)
        {
            if (!IsServer)
            {
                Debug.LogWarning("[EconomyManager] AddFleetCredits chiamato lato client — " +
                                 "instradare via RPC dal sistema chiamante quando Missioni/Vendite (M3+) saranno implementati.");
                return;
            }

            if (amount <= 0) return;
            netFleetCredits.Value += amount;
        }

        // ── API pubblica — trasferimento Fleet → Personal (solo host) ──────

        /// <summary>
        /// Richiesta di trasferimento Fleet Account → Personal Account di un membro
        /// dell'equipaggio. Chiamabile da QUALSIASI client (il gating "solo host" in
        /// UI — vedi ShipTabUI/CrewCreditEntry — è solo UX), ma eseguita ed eventualmente
        /// negata SOLO sul server, che valida che il richiedente sia davvero l'host.
        ///
        /// Nota: targetClientId può coincidere con il client dell'host stesso — il
        /// capitano versa sul proprio Personal Account esattamente come su quello di
        /// chiunque altro (è comunque il SUO personaggio, persistente, che potrebbe
        /// portare in una sessione futura ospitata da un host diverso).
        /// </summary>
        [Rpc(SendTo.Server)]
        public void RequestTransferToPlayerRpc(ulong targetClientId, int amount, RpcParams rpcParams = default)
        {
            ulong senderClientId = rpcParams.Receive.SenderClientId;

            if (senderClientId != NetworkManager.ServerClientId)
            {
                Debug.LogWarning($"[EconomyManager] Trasferimento negato — il client {senderClientId} non è l'host.");
                return;
            }

            if (amount <= 0) return;

            if (netFleetCredits.Value < amount)
            {
                Debug.LogWarning("[EconomyManager] Fondi Fleet Account insufficienti per il trasferimento.");
                return;
            }

            netFleetCredits.Value -= amount;

            ReceiveFleetPaymentRpc(amount, RpcTarget.Single(targetClientId, RpcTargetUse.Temp));

            Debug.Log($"[EconomyManager] Host ha trasferito {amount} cr al client {targetClientId}.");
        }

        /// <summary>
        /// Eseguito SOLO sul client target. Scrive il guadagno nel salvataggio locale
        /// del personaggio (LocalCharacterProfile) — non in una NetworkVariable: da
        /// questo momento il credito appartiene al personaggio, non più alla sessione.
        /// </summary>
        [Rpc(SendTo.SpecifiedInParams)]
        private void ReceiveFleetPaymentRpc(int amount, RpcParams rpcParams = default)
        {
            if (LocalCharacterProfile.Instance == null)
            {
                Debug.LogError("[EconomyManager] LocalCharacterProfile.Instance è null sul client — " +
                                "il pagamento di " + amount + " cr non è stato salvato! Verificare che " +
                                "LocalCharacterProfile sia presente in scena prima di entrare in sessione.");
                return;
            }

            LocalCharacterProfile.Instance.AddPersonalCredits(amount);
        }

        // ── Debug GUI (solo per testare la UI prima che Missioni/Vendite esistano) ──
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!IsServer) return;

            GUILayout.BeginArea(new Rect(280, 200, 240, 100));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[EconomyManager] Fleet: {netFleetCredits.Value} cr");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+800 (test missione)")) AddFleetCredits(800);
            if (GUILayout.Button("+1500 (test vendita)")) AddFleetCredits(1500);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}