using System;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Ship;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiInstance — Milestone 3, Blocco 3, Sottofase 2b.
    ///
    /// NetworkBehaviour server-authoritative che rappresenta un'istanza
    /// concreta di POI (Point of Interest) nello spazio logico della sessione.
    ///
    /// RESPONSABILITÀ:
    ///   1. NetworkVariable server-authoritative: LogicalPosition,
    ///      LogicalRotation, ScanState
    ///   2. Referenziare un PoiData (parametri statici)
    ///   3. Sincronizzare il proprio PoiVisual via SetLogicalOverride
    ///   4. Auto-registrarsi nel PoiRegistry server-side
    ///   5. Esporre eventi:
    ///        - OnScanStateChanged (per-instance, feedback visuale/UI)
    ///        - OnAnyPoiSpawned / OnAnyPoiDespawned (statici, per
    ///          subscriber client-side come ScannerUI che devono mantenere
    ///          liste dinamiche indipendentemente da PoiRegistry
    ///          server-only)
    ///
    /// STRUTTURA PREFAB ATTESA:
    ///   PoiInstance_Wreck (root)
    ///   ├─ NetworkObject
    ///   ├─ PoiInstance (questo script)
    ///   └─ Visual (child)
    ///      ├─ ExternalWorldFollower
    ///      ├─ Mesh + Renderer
    ///      └─ PoiVisualIndicator
    ///
    /// DIPENDE DA:
    ///   - ExternalWorldFollower (Rev T.2, con SetLogicalOverride)
    ///   - ShipMovement (Instance, letto dal Follower)
    ///   - PoiRegistry (auto-registrazione server-side)
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class PoiInstance : NetworkBehaviour
    {
        [Header("Riferimenti")]
        [Tooltip("Il PoiData (ScriptableObject) che descrive la categoria di " +
                 "questo POI.")]
        [SerializeField] private PoiData data;

        [Tooltip("Riferimento all'ExternalWorldFollower del GameObject figlio " +
                 "\"Visual\". Assegnare a mano nell'inspector del prefab.")]
        [SerializeField] private ExternalWorldFollower visualFollower;

        // ── NetworkVariable server-authoritative ─────────────────────────────
        private readonly NetworkVariable<Vector3> _logicalPosition =
            new NetworkVariable<Vector3>(
                Vector3.zero,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<Quaternion> _logicalRotation =
            new NetworkVariable<Quaternion>(
                Quaternion.identity,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<PoiScanState> _scanState =
            new NetworkVariable<PoiScanState>(
                PoiScanState.Unknown,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // ── Accessors pubblici ───────────────────────────────────────────────
        public PoiData Data => data;
        public Vector3 LogicalPosition => _logicalPosition.Value;
        public Quaternion LogicalRotation => _logicalRotation.Value;
        public PoiScanState ScanState => _scanState.Value;

        // ── Eventi pubblici (per-instance) ───────────────────────────────────

        /// <summary>
        /// Evento (client-side + server host) che notifica cambio di ScanState.
        /// Firma: (previousState, newState).
        ///
        /// PATTERN DI ISCRIZIONE:
        ///   poi.OnScanStateChanged += HandleChange;
        ///   HandleChange(default, poi.ScanState); // sync stato iniziale
        /// </summary>
        public event Action<PoiScanState, PoiScanState> OnScanStateChanged;

        // ── Eventi statici (lifecycle globale) ───────────────────────────────

        /// <summary>
        /// Fira su OGNI client (server host incluso) quando un PoiInstance
        /// completa OnNetworkSpawn. Sostituisce PoiRegistry sul client (che
        /// è server-only per design).
        ///
        /// PATTERN DI ISCRIZIONE (client-side subscriber):
        ///   OnEnable:
        ///     PoiInstance.OnAnyPoiSpawned += HandleSpawn;
        ///     PoiInstance.OnAnyPoiDespawned += HandleDespawn;
        ///     foreach (var existing in FindObjectsByType&lt;PoiInstance&gt;(...))
        ///        HandleSpawn(existing);  // POI già in scena
        ///   OnDisable:
        ///     PoiInstance.OnAnyPoiSpawned -= HandleSpawn;
        ///     PoiInstance.OnAnyPoiDespawned -= HandleDespawn;
        ///
        /// Motivazione del "foreach existing": il subscriber potrebbe
        /// caricarsi in scena DOPO che alcuni POI sono già spawnati (es.
        /// UI aperta dal player dopo qualche minuto di gioco). L'evento
        /// statico gestisce solo il futuro; per il presente serve una
        /// scansione iniziale.
        /// </summary>
        public static event Action<PoiInstance> OnAnyPoiSpawned;

        /// <summary>
        /// Fira su OGNI client quando un PoiInstance esegue OnNetworkDespawn.
        /// </summary>
        public static event Action<PoiInstance> OnAnyPoiDespawned;

        // ── Lifecycle NGO ────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                PoiRegistry.Register(this);
            }

            _logicalPosition.OnValueChanged += HandleLogicalPositionChanged;
            _logicalRotation.OnValueChanged += HandleLogicalRotationChanged;
            _scanState.OnValueChanged += HandleScanStateChanged;

            ApplyLogicalToVisual();

            // Notifica subscriber globali (client-side liste, ScannerUI, ecc.)
            OnAnyPoiSpawned?.Invoke(this);
        }

        public override void OnNetworkDespawn()
        {
            // Notifica prima della pulizia, così i subscriber possono
            // ancora leggere lo stato del POI in fase di rimozione.
            OnAnyPoiDespawned?.Invoke(this);

            _logicalPosition.OnValueChanged -= HandleLogicalPositionChanged;
            _logicalRotation.OnValueChanged -= HandleLogicalRotationChanged;
            _scanState.OnValueChanged -= HandleScanStateChanged;

            if (IsServer)
            {
                PoiRegistry.Unregister(this);
            }
        }

        // ── API server-only ──────────────────────────────────────────────────

        public void InitializeLogicalPose(Vector3 logicalPosition, Quaternion logicalRotation)
        {
            if (!IsServer)
            {
                Debug.LogError("[PoiInstance] InitializeLogicalPose called on client — ignored.");
                return;
            }

            _logicalPosition.Value = logicalPosition;
            _logicalRotation.Value = logicalRotation;

            ApplyLogicalToVisual();
        }

        public void SetScanState(PoiScanState newState)
        {
            if (!IsServer)
            {
                Debug.LogError("[PoiInstance] SetScanState called on client — ignored.");
                return;
            }

            _scanState.Value = newState;
        }

        // ── Callback NetVar ──────────────────────────────────────────────────

        private void HandleLogicalPositionChanged(Vector3 _, Vector3 __)
        {
            ApplyLogicalToVisual();
        }

        private void HandleLogicalRotationChanged(Quaternion _, Quaternion __)
        {
            ApplyLogicalToVisual();
        }

        private void HandleScanStateChanged(PoiScanState previous, PoiScanState next)
        {
            OnScanStateChanged?.Invoke(previous, next);
        }

        // ── Applicazione al visual ───────────────────────────────────────────

        private void ApplyLogicalToVisual()
        {
            if (visualFollower == null)
            {
                Debug.LogError($"[PoiInstance] {name}: visualFollower non assegnato nell'inspector del prefab.");
                return;
            }

            visualFollower.SetLogicalOverride(_logicalPosition.Value, _logicalRotation.Value);
        }
    }
}