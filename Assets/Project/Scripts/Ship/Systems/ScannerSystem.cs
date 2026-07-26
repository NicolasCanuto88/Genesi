using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.Ship.Systems
{
    /// <summary>
    /// ScannerSystem — Milestone 3, Blocco 3, Sottofase 2b.
    ///
    /// NetworkBehaviour server-authoritative sulla Nave. Sistema di detection
    /// dei POI. Rileva POI entro il proprio scanRange e aggiorna il loro
    /// ScanState (Unknown → Detected) sul server. I client leggono lo stato
    /// aggiornato via NetworkVariable dei PoiInstance stessi (nessuna
    /// replicazione aggiuntiva da questo sistema).
    ///
    /// PRINCIPIO INVARIANTE (chiarito in review architetturale 2b):
    ///   Nessun ruolo ha esclusive di interazione. Tutti i player possono
    ///   interagire con questo sistema. I ruoli (in particolare "Scanner")
    ///   determinano BONUS/MALUS di performance (range moltiplicato, cooldown
    ///   ridotto, info aggiuntive con Deep Scan), non l'accesso all'azione.
    ///
    /// MODELLO DI DETECTION (2b: passivo automatico):
    ///   Ogni scanIntervalSeconds il server chiama PerformScan(). Questa
    ///   itera PoiRegistry.All, calcola distanza logica dalla nave, e ogni
    ///   POI Unknown entro scanRange passa a Detected. La transizione è
    ///   irreversibile per ora (i POI Detected NON tornano Unknown se si
    ///   allontanano — decisione volontaria, si può cambiare in futuro se
    ///   il gameplay lo richiede).
    ///
    /// TRAIETTORIA DI EVOLUZIONE (registrata come debito di design):
    ///   Blocco 3 Fase 3 / Blocco 4 → transizione da passivo automatico ad
    ///   attivo on-demand. La UI Scanner (Punto 5, prossimo) esporrà un
    ///   pulsante "Scan!" che chiamerà RequestScanRpc(). In quel momento:
    ///     - passiveMode diventerà false
    ///     - RequestScanRpc introdurrà cooldown (LastScanTime già presente)
    ///     - modificatori ruolo (bonus Scanner, malus altri) applicati
    ///       consultando il ruolo del SenderClientId
    ///   L'API PerformScan() e le NetworkVariable resteranno invariate —
    ///   solo il TRIGGER cambia. Nessun refactor strutturale.
    ///
    /// PROGRESSIONE TIER (GDD §3, Scanner T1-T4):
    ///   currentTier è NetworkVariable ma in 2b è hardcoded a 1 al boot.
    ///   scanRange è derivato dal tier tramite metodo statico
    ///   ScanRangeForTier — in 2b: T1=2000m, T2=3500m, T3=5000m, T4=8000m
    ///   (valori PLACEHOLDER, non definitivi, saranno rifiniti con il
    ///   bilanciamento in Blocco 5). La progressione vera arriverà con il
    ///   sistema di upgrade nave in Blocco 5.
    ///
    /// SINGLETON:
    ///   Pattern Instance + OnInstanceReady, coerente con ShipMovement e
    ///   PropulsionSystem. La ScannerUI si iscriverà a OnInstanceReady per
    ///   inizializzarsi correttamente anche se compare in scena prima del
    ///   NetworkSpawn dello ScannerSystem.
    ///
    /// DIPENDE DA:
    ///   - ShipMovement.Instance (per LogicalPosition della nave)
    ///   - PoiRegistry (server-only, per iterare i POI attivi)
    ///   - PoiInstance (per leggere LogicalPosition e chiamare SetScanState)
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class ScannerSystem : NetworkBehaviour
    {
        // ── Singleton pattern ────────────────────────────────────────────────
        public static ScannerSystem Instance { get; private set; }
        public static event System.Action OnInstanceReady;

        [Header("Modalità (2b: passivo)")]
        [Tooltip("Se true, il server esegue PerformScan() automaticamente ogni " +
                 "scanIntervalSeconds. In 2b: sempre true. In Blocco 3 Fase 3 " +
                 "questo verrà messo a false e la scan sarà attivata dalla UI " +
                 "via RequestScanRpc.")]
        [SerializeField] private bool passiveMode = true;

        [Header("Cadenza scan passiva")]
        [Tooltip("Intervallo tra due scan passive (secondi). Default 0.5 = " +
                 "2Hz, sufficiente per la scala del gioco (POI si muovono di " +
                 "~50m in 0.5s a velocità di crociera).")]
        [Min(0.05f)]
        [SerializeField] private float scanIntervalSeconds = 0.5f;

        [Header("Debug")]
        [Tooltip("Log dettagliati di ogni transizione ScanState. Lasciare OFF " +
                 "in produzione.")]
        [SerializeField] private bool verboseLogging = false;

        // ── NetworkVariable server-authoritative ─────────────────────────────
        //
        // Tier corrente dello scanner. In 2b hardcoded a 1 al boot (via
        // InitializeServerState in OnNetworkSpawn). In futuro (Blocco 5) verrà
        // scritto dal sistema di upgrade nave.
        private readonly NetworkVariable<int> _currentTier =
            new NetworkVariable<int>(1,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // Ultima esecuzione di scan (Time.time server). Dormiente in 2b —
        // servirà come base del cooldown in Blocco 3 Fase 3 quando la scan
        // diventerà attiva.
        private readonly NetworkVariable<float> _lastScanTime =
            new NetworkVariable<float>(0f,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // ── Accessors pubblici ───────────────────────────────────────────────
        public int CurrentTier => _currentTier.Value;
        public float LastScanTime => _lastScanTime.Value;

        /// <summary>Range di scan corrente in metri logici, derivato dal
        /// tier. Property calcolata, non replicata (deriva da _currentTier
        /// che è già replicato).</summary>
        public float ScanRange => ScanRangeForTier(_currentTier.Value);

        // Timer per il tick passivo (server-only).
        private float _timeSinceLastPassiveScan;

        // ── Lifecycle NGO ────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[ScannerSystem] Instance già esistente. " +
                                 "Sovrascrivo (attenzione: dovrebbe esserci un solo " +
                                 "ScannerSystem in scena).");
            }
            Instance = this;
            OnInstanceReady?.Invoke();

            if (IsServer)
            {
                // 2b: tier hardcoded a 1. Il fatto che sia scritto qui e non
                // dal default della NetVar è intenzionale — quando il sistema
                // di upgrade nave arriverà (Blocco 5), quello sarà il posto
                // giusto per settare il tier corretto letto dal Fleet Account.
                _currentTier.Value = 1;
                _lastScanTime.Value = 0f;
                _timeSinceLastPassiveScan = 0f;

                if (verboseLogging)
                {
                    Debug.Log($"[ScannerSystem] Server ready. Tier {CurrentTier}, " +
                              $"range {ScanRange}m, passive mode: {passiveMode}, " +
                              $"interval {scanIntervalSeconds}s.");
                }
            }
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // ── Update loop server-only (passivo) ────────────────────────────────

        private void Update()
        {
            if (!IsServer) return;
            if (!passiveMode) return;

            _timeSinceLastPassiveScan += Time.deltaTime;
            if (_timeSinceLastPassiveScan < scanIntervalSeconds) return;

            _timeSinceLastPassiveScan = 0f;
            PerformScan();
        }

        // ── API principale (server-only) ─────────────────────────────────────

        /// <summary>
        /// Esegue un ciclo di scan: itera i POI registrati, aggiorna ScanState
        /// di quelli entro range. Server-only.
        ///
        /// 2b: chiamato automaticamente ogni scanIntervalSeconds dal loop
        /// passivo. In futuro sarà chiamato da RequestScanRpc su input UI.
        /// </summary>
        public void PerformScan()
        {
            if (!IsServer) return;

            var ship = ShipMovement.Instance;
            if (ship == null)
            {
                if (verboseLogging)
                    Debug.LogWarning("[ScannerSystem] ShipMovement.Instance null, scan skip.");
                return;
            }

            Vector3 shipPos = ship.LogicalPosition;
            float rangeSqr = ScanRange * ScanRange; // confronto senza sqrt

            int newlyDetected = 0;

            foreach (var poi in PoiRegistry.All)
            {
                if (poi == null) continue;
                if (poi.ScanState != PoiScanState.Unknown) continue;

                float distSqr = (poi.LogicalPosition - shipPos).sqrMagnitude;
                if (distSqr <= rangeSqr)
                {
                    poi.SetScanState(PoiScanState.Detected);
                    newlyDetected++;

                    if (verboseLogging)
                    {
                        float dist = Mathf.Sqrt(distSqr);
                        Debug.Log($"[ScannerSystem] Detected " +
                                  $"{poi.Data?.DisplayName ?? "POI"} @ {dist:F0}m " +
                                  $"(range {ScanRange:F0}m).");
                    }
                }
            }

            _lastScanTime.Value = Time.time;

            if (verboseLogging && newlyDetected > 0)
            {
                Debug.Log($"[ScannerSystem] Scan complete. {newlyDetected} new detections.");
            }
        }

        // ── API per UI (attivo — dormiente in 2b) ────────────────────────────

        /// <summary>
        /// [Blocco 3 Fase 3+] Richiesta di scan attivo dalla UI. Chiamabile
        /// da qualunque client. Il server valuterà:
        ///   - cooldown rispetto a LastScanTime
        ///   - ruolo del SenderClientId per applicare bonus/malus
        ///   - eventuale gate di setup (es. player deve essere davanti alla
        ///     consolle Scanner, se decidiamo di legarlo a una postazione)
        /// e in caso positivo chiamerà PerformScan().
        ///
        /// In 2b: la scan è passiva, questa RPC non è necessaria. È presente
        /// come stub per fissare la firma corretta ora — quando la UI userà
        /// questo entry point, non dovremo cambiarne signature.
        /// </summary>
        [Rpc(SendTo.Server)]
        public void RequestScanRpc(RpcParams rpcParams = default)
        {
            // In 2b: no-op. Documentato ma non attivo.
            if (verboseLogging)
            {
                ulong sender = rpcParams.Receive.SenderClientId;
                Debug.Log($"[ScannerSystem] RequestScanRpc da client {sender} — " +
                          $"ignorato in 2b (passive mode).");
            }
        }

        // ── Progressione tier ────────────────────────────────────────────────

        /// <summary>
        /// Range di scan in metri logici in funzione del tier. Numeri
        /// PLACEHOLDER — bilanciamento definitivo in Blocco 5.
        ///
        /// T1 = 2000m  (base, "vicino")
        /// T2 = 3500m  (Analyst)
        /// T3 = 5000m  (Decoder)
        /// T4 = 8000m  (Oracle)
        /// </summary>
        public static float ScanRangeForTier(int tier)
        {
            switch (tier)
            {
                case 1: return 2000f;
                case 2: return 3500f;
                case 3: return 5000f;
                case 4: return 8000f;
                default: return 2000f; // fallback difensivo
            }
        }
    }
}
