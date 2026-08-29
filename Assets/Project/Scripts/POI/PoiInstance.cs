using System;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Ship;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiInstance — Milestone 3, Blocco 3, Sottofase 2b (moto proprio 3.2.b).
    ///
    /// NetworkBehaviour server-authoritative che rappresenta un'istanza
    /// concreta di POI (Point of Interest) nello spazio logico della sessione.
    ///
    /// RESPONSABILITÀ:
    ///   1. NetworkVariable server-authoritative: LogicalPosition,
    ///      LogicalRotation, LogicalVelocity, ScanState
    ///   2. Referenziare un PoiData (parametri statici)
    ///   3. Sincronizzare il proprio PoiVisual via SetLogicalOverride
    ///   4. Auto-registrarsi nel PoiRegistry server-side
    ///   5. Esporre eventi:
    ///        - OnScanStateChanged (per-instance, feedback visuale/UI)
    ///        - OnAnyPoiSpawned / OnAnyPoiDespawned (statici, per
    ///          subscriber client-side come ScannerUI che devono mantenere
    ///          liste dinamiche indipendentemente da PoiRegistry
    ///          server-only)
    ///   6. [Blocco 3.2.b] Integrazione server-side del proprio moto:
    ///      LogicalPosition += LogicalVelocity * dt, con damping esponenziale
    ///      opzionale (τ = dampingTau, default 30 s → deriva percepibile solo
    ///      su tempi lunghi, feel "spazio" sui primi secondi dopo l'urto).
    ///      La rotazione NON viene modificata (invariante Rev Z): l'asse di
    ///      approccio del POI deve restare stabile per permettere al pilota
    ///      di riprovare l'attracco dopo un urto.
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

        // ── Moto proprio (Blocco 3.2.b) ──────────────────────────────────────
        [Header("Moto proprio (Blocco 3.2.b)")]
        [Tooltip("Costante di tempo del damping esponenziale applicato alla " +
                 "velocità logica del POI, in secondi. Ogni FixedUpdate:\n" +
                 "  velocity *= exp(-dt / dampingTau)\n\n" +
                 "Default 30 s: sui primi secondi dopo l'urto il decadimento è " +
                 "impercettibile (feel newtoniano \"spazio\"), su tempi lunghi " +
                 "il POI decelera evitando che si allontani infinitamente dalla " +
                 "playzone. Valore giustificato narrativamente da microforze " +
                 "cosmiche residue (drag da polvere interstellare, gravitazione " +
                 "diffusa) — coerente con la direzione di design per il debito " +
                 "D2 (viaggio gratuito della nave in COASTING).\n\n" +
                 "Impostare a 0 per Newton puro (nessun damping). Impostare a " +
                 "valori bassi (es. 3-5 s) per feel \"attrito nel vuoto\" — " +
                 "sconsigliato ma disponibile per test.")]
        [Min(0f)]
        [SerializeField] private float dampingTau = 30f;

        [Tooltip("Soglia di velocità sotto cui il POI viene considerato fermo: " +
                 "la velocità viene azzerata e l'integrazione viene saltata " +
                 "finché non arriva un nuovo AddImpulse.\n\n" +
                 "Il damping esponenziale (dampingTau) matematicamente non " +
                 "raggiunge mai lo zero: senza una soglia di sleep, un POI " +
                 "colpito continuerebbe a strisciare per ore a velocità " +
                 "impercettibili ma non-zero, e il pilota si troverebbe a " +
                 "inseguire un bersaglio che sembra fermo ma \"scivola via\" " +
                 "appena si avvicina — feel pessimo.\n\n" +
                 "Default 0.05 u/s (5 cm/s): al di sotto il moto del POI è " +
                 "sotto la soglia percettiva. Con τ = 30 s e un deltaV " +
                 "iniziale di 0.3 u/s, il POI raggiunge il sleep in ~54 s dopo " +
                 "aver percorso ~9 m: sbalzato via visibilmente, poi fermo. " +
                 "Con deltaV piccolo (urto leggero) il POI è già sotto soglia " +
                 "e si ferma al primo tick.")]
        [Min(0f)]
        [SerializeField] private float sleepSpeedThreshold = 0.05f;

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

        /// <summary>
        /// Velocità logica del POI, in unità/secondo nello spazio logico
        /// (worldspace di riferimento). Scritta esclusivamente dal server
        /// (integrazione + AddImpulse). Replicata a tutti i client per
        /// consumer futuri (radar, UI cinetica) e per garantire snapshot
        /// consistente in caso di riconnessione.
        /// </summary>
        private readonly NetworkVariable<Vector3> _logicalVelocity =
            new NetworkVariable<Vector3>(
                Vector3.zero,
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
        public Vector3 LogicalVelocity => _logicalVelocity.Value;
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

        // ── Integrazione moto proprio (Blocco 3.2.b, server-only) ────────────

        private void FixedUpdate()
        {
            if (!IsSpawned) return;
            if (!IsServer) return;

            Vector3 v = _logicalVelocity.Value;

            // Fast path: POI fermo → nessuna scrittura, nessun evento.
            if (v.sqrMagnitude < sleepSpeedThreshold * sleepSpeedThreshold)
            {
                if (v != Vector3.zero)
                {
                    _logicalVelocity.Value = Vector3.zero;
                }
                return;
            }

            float dt = Time.fixedDeltaTime;

            // Integrazione posizione (Euler esplicito, sufficiente per un
            // moto damped a bassa velocità: no problemi di stabilità).
            _logicalPosition.Value += v * dt;

            // Damping esponenziale: v(t) = v(0) * exp(-t / tau).
            // Se dampingTau <= 0 → Newton puro (nessun decadimento).
            if (dampingTau > 0f)
            {
                float decay = Mathf.Exp(-dt / dampingTau);
                _logicalVelocity.Value = v * decay;
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
            _logicalVelocity.Value = Vector3.zero;

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

        /// <summary>
        /// Aggiunge un impulso alla velocità logica del POI. Server-only.
        /// Consumer previsto (Blocco 3.2.b.2): ShipImpactHandler, che in
        /// seguito a OnHardCollision calcola il deltaV secondo il modello
        /// di trasferimento di momento e chiama questo metodo.
        ///
        /// Semantica: deltaV è un incremento di velocità in unità/secondo
        /// nello spazio logico (worldspace di riferimento). Non è un impulso
        /// fisico in senso stretto (kg·m/s) — la massa è già stata
        /// contabilizzata a monte nel calcolo di chi chiama.
        /// </summary>
        public void AddImpulse(Vector3 deltaV)
        {
            if (!IsServer)
            {
                Debug.LogError("[PoiInstance] AddImpulse called on client — ignored.");
                return;
            }

            _logicalVelocity.Value += deltaV;
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