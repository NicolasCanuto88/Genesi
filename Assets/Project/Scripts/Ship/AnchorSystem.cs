using System;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.Ship
{
    // ─── Enum AnchorabilityState ──────────────────────────────────────────────

    /// <summary>
    /// Stato dell'ancorabilità del candidato POI attualmente più vicino.
    /// Fase 3 Blocco 3.1 — replicato via NetworkVariable su AnchorSystem.
    /// La UI (PilotHUD/PilotFlightHUD) leggerà questo enum per decidere quale
    /// prompt mostrare (nessuno / warning velocità / disponibile).
    /// </summary>
    public enum AnchorabilityState : byte
    {
        None = 0,
        InRangeTooFast = 1,
        Anchorable = 2
    }

    // ─── AnchorSystem ─────────────────────────────────────────────────────────

    /// <summary>
    /// AnchorSystem — Milestone 3 Fase 3 Blocco 3.1 (Sotto-step 3.1.2,
    /// aggiornato in 3.1.4).
    /// NetworkBehaviour singleton — GameObject dedicato figlio di Nave.
    ///
    /// RESPONSABILITÀ:
    ///   1. Detection del POI candidato all'ancoraggio (server-only, tick 4Hz)
    ///   2. Espone NetVar CurrentAnchorableId + AnchorabilityState
    ///   3. RPC RequestStartDocking / RequestUndock
    ///
    /// SEMANTICA UNDOCK (aggiornata 3.1.4):
    ///   Target = MANUAL come default — chi disancora sta comunque pilotando.
    ///   Fallback = COASTING se Manual non è ammesso (sistema Offline).
    ///   Il caso "il pilota si alza durante Docking/Docked" è gestito da
    ///   PilotStation, che chiama RequestUndock prima dell'uscita. Il codice
    ///   esistente Manual→Coasting in TryExitStation gestirà poi il "nessuno
    ///   al timone" quando il pilota effettivamente si alza.
    /// </summary>
    public class AnchorSystem : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static AnchorSystem Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── Tuning ────────────────────────────────────────────────────────────
        [Header("Detection")]
        [Tooltip("Frequenza del tick di detection ancorabilità sul server. " +
                 "Default 0.25s (4Hz). Costo trascurabile.")]
        [Min(0.05f)]
        [SerializeField] private float detectionTickInterval = 0.25f;

        [Header("Precondizioni ingresso Docking")]
        [Tooltip("Velocità massima della nave per poter iniziare la manovra di " +
                 "attracco (u/s). Sopra: AnchorabilityState.InRangeTooFast, prompt " +
                 "grigio. In futuro modulabile per ruolo del Pilota.")]
        [Min(0f)]
        [SerializeField] private float maxSpeedToStartDocking = 30f;

        // ── Network Variables ─────────────────────────────────────────────────
        private readonly NetworkVariable<ulong> _netCurrentAnchorableId =
            new(0ul, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<AnchorabilityState> _netAnchorabilityState =
            new(AnchorabilityState.None,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // ── Runtime (server) ──────────────────────────────────────────────────
        private float _tickTimer;

        // ── Accessors pubblici ────────────────────────────────────────────────
        public ulong CurrentAnchorableId => _netCurrentAnchorableId.Value;
        public AnchorabilityState CurrentAnchorabilityState => _netAnchorabilityState.Value;

        // ── Lifecycle NGO ─────────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            Instance = this;
            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // ── Update (server-only) ──────────────────────────────────────────────
        private void Update()
        {
            if (!IsServer) return;

            _tickTimer += Time.deltaTime;
            if (_tickTimer < detectionTickInterval) return;
            _tickTimer = 0f;

            RunDetectionTick();
        }

        private void RunDetectionTick()
        {
            var propulsion = PropulsionSystem.Instance;
            var movement = ShipMovement.Instance;
            if (propulsion == null || movement == null)
            {
                SetAnchorability(0ul, AnchorabilityState.None);
                return;
            }

            var navState = propulsion.CurrentNavState;
            if (navState == NavigationState.Docking || navState == NavigationState.Docked)
            {
                SetAnchorability(_netCurrentAnchorableId.Value, AnchorabilityState.None);
                return;
            }

            bool stateAllowsDocking = navState == NavigationState.Manual
                                   || navState == NavigationState.Coasting;
            if (!stateAllowsDocking)
            {
                SetAnchorability(0ul, AnchorabilityState.None);
                return;
            }

            Vector3 shipPos = movement.LogicalPosition;
            PoiInstance bestPoi = null;
            float bestDistSqr = float.PositiveInfinity;

            foreach (var poi in PoiRegistry.All)
            {
                if (poi == null || poi.Data == null) continue;
                if (poi.ScanState < PoiScanState.Detected) continue;

                Vector3 fromPoiToShip = shipPos - poi.LogicalPosition;
                float distSqr = fromPoiToShip.sqrMagnitude;

                // Check 1: dentro dockingRadius (sfera).
                float dockRadius = poi.Data.DockingRadius;
                if (distSqr > dockRadius * dockRadius) continue;

                // Check 2: dentro il cono di approccio (Fase 3.1.5).
                // La nave deve trovarsi nel semispazio "lato di attracco" del
                // POI, entro un cono di apertura dockingConeAngleDeg attorno
                // all'asse di approccio. Coerente col cono visuale (mesh
                // trasparente) sul prefab: se il pilota è dentro il cono che
                // vede, il POI è ancorabile.
                //
                // Calcolo: axialNormalized = Dot(fromPoiToShip_norm, approachAxis).
                // Se >= cos(halfAngle) → dentro il cono.
                // Con angolo 60°: cos(30°) ≈ 0.866.
                //
                // Rev AB (Q6=B): approachAxis è ora esposto da PoiInstance
                // (DockingAnchorForwardWorld), derivato dal DockingAnchor
                // Transform sul prefab POI. Sostituisce il vecchio
                // poi.LogicalRotation × poi.Data.DockingApproachDirectionLocal
                // (rimosso da PoiData in Rev AB). La property ritorna già
                // normalizzata con fallback interno (LogicalRotation × Vector3.up
                // se il DockingAnchor non è configurato sul prefab).
                //
                // Edge case: se ship == poi (distSqr ≈ 0), evitiamo divisione
                // per zero — la nave è già "dentro" il POI, l'attracco non ha
                // senso. Skip.
                if (distSqr < 1e-4f) continue;

                Vector3 approachAxisWorld = poi.DockingAnchorForwardWorld;
                float approachMag = approachAxisWorld.magnitude;
                if (approachMag > 1e-4f) approachAxisWorld /= approachMag;
                else continue; // degenere, POI mal configurato — skip conservativo

                float axialNormalized =
                    Vector3.Dot(fromPoiToShip / Mathf.Sqrt(distSqr), approachAxisWorld);
                if (axialNormalized < poi.Data.DockingConeMinDot) continue;

                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    bestPoi = poi;
                }
            }

            if (bestPoi == null)
            {
                SetAnchorability(0ul, AnchorabilityState.None);
                return;
            }

            float shipSpeed = propulsion.CurrentSpeed;
            var newState = shipSpeed > maxSpeedToStartDocking
                ? AnchorabilityState.InRangeTooFast
                : AnchorabilityState.Anchorable;

            SetAnchorability(bestPoi.NetworkObjectId, newState);
        }

        private void SetAnchorability(ulong poiId, AnchorabilityState state)
        {
            if (_netCurrentAnchorableId.Value != poiId)
                _netCurrentAnchorableId.Value = poiId;
            if (_netAnchorabilityState.Value != state)
                _netAnchorabilityState.Value = state;
        }

        // ── API pubblica ──────────────────────────────────────────────────────

        public void RequestStartDocking()
        {
            if (IsServer) RequestStartDockingInternal();
            else RequestStartDockingRpc();
        }

        [Rpc(SendTo.Server)]
        private void RequestStartDockingRpc() => RequestStartDockingInternal();

        private void RequestStartDockingInternal()
        {
            if (_netAnchorabilityState.Value != AnchorabilityState.Anchorable)
            {
                Debug.LogWarning($"[AnchorSystem] Ingresso Docking rifiutato — " +
                                 $"AnchorabilityState = {_netAnchorabilityState.Value}");
                return;
            }

            ulong poiId = _netCurrentAnchorableId.Value;
            if (poiId == 0ul)
            {
                Debug.LogWarning("[AnchorSystem] Ingresso Docking rifiutato — " +
                                 "CurrentAnchorableId = 0.");
                return;
            }

            var propulsion = PropulsionSystem.Instance;
            if (propulsion == null)
            {
                Debug.LogError("[AnchorSystem] PropulsionSystem.Instance null.");
                return;
            }

            propulsion.SetAnchoredPoiId(poiId);
            propulsion.RequestNavigationState(NavigationState.Docking);

            Debug.Log($"[AnchorSystem] Docking avviato — POI NetworkObjectId {poiId}");
        }

        /// <summary>
        /// Undock: torna a Manual se possibile, Coasting come fallback.
        /// Chiamabile da qualsiasi client. Validazione server-side: accetta
        /// solo se stato attuale è Docking o Docked.
        /// </summary>
        public void RequestUndock()
        {
            if (IsServer) RequestUndockInternal();
            else RequestUndockRpc();
        }

        [Rpc(SendTo.Server)]
        private void RequestUndockRpc() => RequestUndockInternal();

        private void RequestUndockInternal()
        {
            var propulsion = PropulsionSystem.Instance;
            if (propulsion == null)
            {
                Debug.LogError("[AnchorSystem] PropulsionSystem.Instance null — undock impossibile.");
                return;
            }

            var navState = propulsion.CurrentNavState;
            if (navState != NavigationState.Docking && navState != NavigationState.Docked)
            {
                Debug.LogWarning($"[AnchorSystem] Undock rifiutato — stato attuale {navState} " +
                                 "(atteso Docking o Docked).");
                return;
            }

            // Riporta ScanState del POI a Scanned se ancora referenziato.
            ulong anchoredId = propulsion.AnchoredPoiId;
            if (anchoredId != 0ul
                && NetworkManager.Singleton != null
                && NetworkManager.Singleton.SpawnManager != null
                && NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(anchoredId, out var no)
                && no != null)
            {
                var poi = no.GetComponent<PoiInstance>();
                if (poi != null && poi.ScanState == PoiScanState.Anchored)
                {
                    poi.SetScanState(PoiScanState.Scanned);
                }
            }

            // Azzera AnchoredPoiId prima di cambiare stato.
            propulsion.SetAnchoredPoiId(0ul);

            // Target: Manual come default, Coasting come fallback.
            NavigationState target = CanEnterManual(propulsion)
                ? NavigationState.Manual
                : NavigationState.Coasting;

            propulsion.RequestNavigationState(target);

            Debug.Log($"[AnchorSystem] Undock completato — {target}");
        }

        /// <summary>
        /// Verifica se PropulsionSystem accetterebbe Manual ora. Replica la
        /// logica interna di RequestNavStateInternal (sistema non Offline,
        /// non FTL). FTL è escluso qui: se fossimo in FTL saremmo in Anchored,
        /// mentre l'invariante del chiamante è Docking/Docked → non FTL.
        /// </summary>
        private static bool CanEnterManual(PropulsionSystem propulsion)
        {
            if (propulsion == null) return false;
            if (propulsion.CurrentHealthPercent < 0.25f) return false;
            return true;
        }

        // ── Debug GUI (solo lettura — cursore-safe) ──────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 300, 100));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[Anchor] {(IsServer ? "SRV" : "CLT")}");
            GUILayout.Label($"CurrentAnchorableId: {_netCurrentAnchorableId.Value}");
            GUILayout.Label($"AnchorabilityState:  {_netAnchorabilityState.Value}");
            GUILayout.Label($"Tick every {detectionTickInterval:F2}s");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}