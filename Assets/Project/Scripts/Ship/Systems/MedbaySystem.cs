using System;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// MedbaySystem — sistema-nave del modulo Medbay (Rev BO-a · Fase 3a Corpsman).
    ///
    /// RESPONSABILITÀ UNICA: il TIER del modulo Medbay (GDD v0.9.39 Q5-a — "tier =
    /// livello modulo Medbay, upgrade nave"). Non contiene logica di cura: la cura
    /// vive nel letto (RecoveryBed), che legge il tier da qui e i valori per tier da
    /// MedbayConfig. Così il tier ha una sola autorità, condivisa da tutti i futuri
    /// consumatori (cariche defib BP, cura Ferite Composte T3+ via TryCure, Chemistry Lab).
    ///
    /// PATTERN: singleton di scena Instance + OnInstanceReady, identico a
    /// DefibChargePool / ScannerSystem. Tier come NetworkVariable scritta dal server,
    /// letta da tutti (i client ne hanno bisogno per prompt e UI future).
    ///
    /// ORIGINE DEL TIER: a oggi debugStartTier, applicato dal server allo spawn —
    /// come ScannerSystem finché non esiste l'upgrade nave. Dipende da: Blocco 5
    /// (upgrade nave / Fleet Account), che chiamerà ServerSetTier.
    ///
    /// ENERGIA (Q4.1-a): NON implementa IPowerConsumer in BO. Il modulo non ha
    /// ancora un consumo di design; "Medbay in rete elettrica" è un debito di design
    /// tracciato nel GDD delta v0.9.41 (stesso trattamento di ScannerSystem).
    ///
    /// ⚠️ SETUP SCENA: GameObject root "MedbaySystem" con NetworkObject (oggetto di
    /// scena, nessuna registrazione in NetworkPrefabs). Vedi guida Editor Rev BO-a.
    /// </summary>
    public class MedbaySystem : NetworkBehaviour
    {
        public const int MinTier = 1;
        public const int MaxTier = 4;

        public static MedbaySystem Instance { get; private set; }

        /// <summary>Fired dopo OnNetworkSpawn — i dipendenti si sottoscrivono se Instance è null al loro Start.</summary>
        public static event Action OnInstanceReady;

        [Header("Tier (TEMP fino al Blocco 5 — upgrade nave)")]
        [Tooltip("Tier del modulo Medbay impostato dal server allo spawn. Placeholder finché " +
                 "il tier non arriverà dall'upgrade nave (Blocco 5). T1 = Recovery Bay base.")]
        [Range(MinTier, MaxTier)]
        [SerializeField] private int debugStartTier = MinTier;

        private readonly NetworkVariable<int> netTier = new NetworkVariable<int>(
            MinTier, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Tier corrente del modulo Medbay (replicato).</summary>
        public int CurrentTier => netTier.Value;

        /// <summary>
        /// Tier corrente se il sistema è in scena e spawnato, altrimenti T1. Usato dai
        /// consumatori che non devono rompersi se MedbaySystem manca (log una volta a
        /// loro carico).
        /// </summary>
        public static int CurrentTierOrDefault =>
            Instance != null && Instance.IsSpawned ? Instance.CurrentTier : MinTier;

        // ── Lifecycle NGO ──────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (Instance != null && Instance != this)
                Debug.LogWarning("[MedbaySystem] Instance già esistente. Deve esserci un solo " +
                                 "MedbaySystem in scena.");

            Instance = this;

            if (IsServer)
                netTier.Value = Mathf.Clamp(debugStartTier, MinTier, MaxTier);

            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // ── API server ─────────────────────────────────────────────────────────

        /// <summary>
        /// Imposta il tier del modulo. SERVER ONLY. Futuro chiamante: upgrade nave
        /// (Blocco 5). Oggi usato solo dall'overlay di debug.
        /// </summary>
        public void ServerSetTier(int tier)
        {
            if (!IsServer) return;
            netTier.Value = Mathf.Clamp(tier, MinTier, MaxTier);
        }

        // ── Debug GUI (Editor/Development, solo server) — standard Rev BA ──
        [Header("Debug")]
        [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development, solo server). Standard Rev BA — default off.")]
        [SerializeField] private bool showDebugUI = false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showDebugUI) return;
            if (!IsServer) return;

            GUILayout.BeginArea(new Rect(600, 130, 240, 70));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[MedbaySystem] Tier: T{netTier.Value}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Tier -1")) ServerSetTier(netTier.Value - 1);
            if (GUILayout.Button("Tier +1")) ServerSetTier(netTier.Value + 1);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}
