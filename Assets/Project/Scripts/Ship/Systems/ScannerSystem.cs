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
    /// MODELLO IBRIDO (Rev BH — Fase 2b, D29 Q2-c IMPLEMENTATO):
    ///   Lo Scanner è ora ibrido passivo+attivo:
    ///     - PASSIVO (T1, sempre attivo): PerformScan() automatico rileva i POI
    ///       in range → Unknown→Detected. Info T1 (tipo/massa/distanza) visibili
    ///       appena Detected. passiveMode resta TRUE (non si spegne più).
    ///     - ATTIVO (T2+, on-demand): RequestScanRpc(targetPoiId) esegue uno
    ///       scan mirato su un POI già Detected → Detected→Scanned e alza il suo
    ///       RevealedInfoTier fino al tier effettivo (tier nave + bonus ruolo).
    ///       Cooldown-gated via _lastScanTime. Reveal IMMEDIATO (Q2-a).
    ///   L'informazione rivelata vive su PoiInstance (ScanState + RevealedInfoTier,
    ///   NetworkVariable per-POI lette da tutti) → condivisa crew-wide gratis
    ///   (D29 Q4 sharing). Info statiche persistono (RevealedInfoTier monotòno);
    ///   info combat dinamiche (nemici/HP, T3+) saranno live-only al Combat (M4.7).
    ///
    ///   STUB in 2b (dipendenze non ancora esistenti):
    ///     - Bonus/malus di RUOLO: non esiste un registro networked
    ///       OwnerClientId→ruolo (il ruolo è una stringa su profilo di menu).
    ///       GetRoleTierBonus/GetRoleCooldownMultiplier ritornano valori neutri.
    ///       dipende da: sistema ruolo networked per-player.
    ///     - Tier nave: hardcoded (debugStartTier) fino al sistema di upgrade
    ///       nave (Blocco 5). Alzarlo in Inspector per testare il reveal T2/T3.
    ///     - Info T3 nemici / T4 blueprint-sistemi-layout: campi STUB su PoiData
    ///       (validazione piena al Combat, M4.7).
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

        [Header("Modalità passiva (T1, sempre attiva)")]
        [Tooltip("Se true, il server esegue PerformScan() automaticamente ogni " +
                 "scanIntervalSeconds (rilevamento passivo T1: Unknown→Detected). " +
                 "Rev BH: nel modello ibrido D29 resta TRUE — il passivo NON si " +
                 "spegne più. Lo scan ATTIVO (T2+) è additivo, via RequestScanRpc.")]
        [SerializeField] private bool passiveMode = true;

        [Header("Cadenza scan passiva")]
        [Tooltip("Intervallo tra due scan passive (secondi). Default 0.5 = " +
                 "2Hz, sufficiente per la scala del gioco (POI si muovono di " +
                 "~50m in 0.5s a velocità di crociera).")]
        [Min(0.05f)]
        [SerializeField] private float scanIntervalSeconds = 0.5f;

        [Header("Scan attivo (T2+, Rev BH — D29)")]
        [Tooltip("Cooldown minimo (secondi) tra due scan ATTIVI riusciti. " +
                 "Riferito al tempo server. Il bonus di ruolo Scanner (quando " +
                 "esisterà) lo ridurrà via moltiplicatore. Default 2s.")]
        [Min(0f)]
        [SerializeField] private float scanCooldownSeconds = 2f;

        [Header("Debug tier (TEMP fino a Blocco 5 upgrade nave)")]
        [Tooltip("Tier scanner nave impostato al boot dal server. In 2b il " +
                 "sistema di upgrade nave non esiste ancora: alzare qui (2/3/4) " +
                 "per testare il reveal T2/T3/T4 in playtest. Da rimuovere quando " +
                 "il tier arriverà dal Fleet Account / upgrade nave (Blocco 5).")]
        [Range(1, 4)]
        [SerializeField] private int debugStartTier = 1;

        [Header("Debug")]
        [Tooltip("Log dettagliati di ogni transizione ScanState. Lasciare OFF " +
                 "in produzione.")]
        [SerializeField] private bool logVerbose = false;

        // ── NetworkVariable server-authoritative ─────────────────────────────
        //
        // Tier corrente dello scanner. In 2b hardcoded a 1 al boot (via
        // InitializeServerState in OnNetworkSpawn). In futuro (Blocco 5) verrà
        // scritto dal sistema di upgrade nave.
        private readonly NetworkVariable<int> _currentTier =
            new NetworkVariable<int>(1,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // Time.time (server) dell'ultimo scan ATTIVO riuscito. Base del cooldown
        // dello scan attivo (Rev BH). NON più scritto dal passivo (che gira ogni
        // 0.5s e azzererebbe di continuo il cooldown).
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
                // Rev BH: tier dal campo debug (TEMP). Quando il sistema di
                // upgrade nave arriverà (Blocco 5), qui si leggerà il tier dal
                // Fleet Account. Clamp difensivo su [1,4].
                _currentTier.Value = Mathf.Clamp(debugStartTier, 1, 4);
                _lastScanTime.Value = 0f;
                _timeSinceLastPassiveScan = 0f;

                if (logVerbose)
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
                if (logVerbose)
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

                    if (logVerbose)
                    {
                        float dist = Mathf.Sqrt(distSqr);
                        Debug.Log($"[ScannerSystem] Detected " +
                                  $"{poi.Data?.DisplayName ?? "POI"} @ {dist:F0}m " +
                                  $"(range {ScanRange:F0}m).");
                    }
                }
            }

            // Rev BH: NON scriviamo _lastScanTime qui — è il timestamp del
            // cooldown dello scan ATTIVO. Il passivo non ha cooldown.

            if (logVerbose && newlyDetected > 0)
            {
                Debug.Log($"[ScannerSystem] Scan passivo complete. {newlyDetected} new detections.");
            }
        }

        // ── API per UI (attivo — dormiente in 2b) ────────────────────────────

        /// <summary>
        /// [Rev BH — Fase 2b, D29] Richiesta di SCAN ATTIVO su un POI specifico.
        /// Chiamabile da QUALUNQUE client (no esclusiva di ruolo — D29 principio
        /// invariante; il ruolo dà bonus/malus, non l'accesso). Server-side:
        ///   1. cooldown (scanCooldownSeconds × moltiplicatore ruolo)
        ///   2. risoluzione bersaglio via PoiRegistry.TryGet
        ///   3. gate: bersaglio già Detected (passivo) + entro ScanRange
        /// In caso positivo: Detected→Scanned e RevealedInfoTier alzato al tier
        /// effettivo (tier nave + bonus ruolo), reveal IMMEDIATO (Q2-a).
        ///
        /// targetPoiId = NetworkObjectId del PoiInstance bersaglio (lo passa la
        /// ScannerUI dal POI selezionato).
        /// </summary>
        [Rpc(SendTo.Server)]
        public void RequestScanRpc(ulong targetPoiId, RpcParams rpcParams = default)
        {
            if (!IsServer) return;
            TryActiveScan(targetPoiId, rpcParams.Receive.SenderClientId);
        }

        /// <summary>
        /// [Rev BH] Logica dello scan attivo, server-only. Estratta da
        /// RequestScanRpc così che il self-test editor possa invocarla senza
        /// fabbricare RpcParams. requesterClientId serve solo agli hook di ruolo.
        /// </summary>
        private void TryActiveScan(ulong targetPoiId, ulong requesterClientId)
        {
            if (!IsServer) return;

            // 1. Cooldown (con moltiplicatore ruolo — stub neutro in 2b).
            float now = Time.time;
            float effectiveCooldown = scanCooldownSeconds * GetRoleCooldownMultiplier(requesterClientId);
            if (now - _lastScanTime.Value < effectiveCooldown)
            {
                if (logVerbose)
                    Debug.Log($"[ScannerSystem] Scan attivo da client {requesterClientId} " +
                              $"rifiutato: cooldown ({now - _lastScanTime.Value:F2}s < " +
                              $"{effectiveCooldown:F2}s).");
                return;
            }

            // 2. Risoluzione bersaglio.
            if (!PoiRegistry.TryGet(targetPoiId, out var poi) || poi == null)
            {
                if (logVerbose)
                    Debug.LogWarning($"[ScannerSystem] Scan attivo: POI id {targetPoiId} " +
                                     "non registrato (despawnato?).");
                return;
            }

            // 3. Gate: dev'essere già rilevato passivamente (non si fa deep-scan
            //    di ciò che non è ancora Detected — T1 è passivo, T2+ è attivo).
            if (poi.ScanState == PoiScanState.Unknown)
            {
                if (logVerbose)
                    Debug.Log($"[ScannerSystem] Scan attivo su {poi.Data?.DisplayName ?? "POI"}: " +
                              "ancora Unknown, ignorato (attendere rilevamento passivo).");
                return;
            }

            // 3b. Gate range (spazio logico).
            var ship = ShipMovement.Instance;
            if (ship == null)
            {
                if (logVerbose)
                    Debug.LogWarning("[ScannerSystem] Scan attivo: ShipMovement.Instance null.");
                return;
            }
            float distSqr = (poi.LogicalPosition - ship.LogicalPosition).sqrMagnitude;
            float rangeSqr = ScanRange * ScanRange;
            if (distSqr > rangeSqr)
            {
                if (logVerbose)
                    Debug.Log($"[ScannerSystem] Scan attivo su {poi.Data?.DisplayName ?? "POI"}: " +
                              $"fuori range ({Mathf.Sqrt(distSqr):F0}m > {ScanRange:F0}m).");
                return;
            }

            // 4. Successo: Detected→Scanned (solo se attualmente Detected; non
            //    tocchiamo lo stato Anchored) + reveal tier.
            if (poi.ScanState == PoiScanState.Detected)
            {
                poi.SetScanState(PoiScanState.Scanned);
            }

            int effectiveTier = Mathf.Clamp(CurrentTier + GetRoleTierBonus(requesterClientId), 1, 4);
            poi.SetRevealedInfoTier(effectiveTier);
            _lastScanTime.Value = now;

            if (logVerbose)
                Debug.Log($"[ScannerSystem] Scan attivo OK su " +
                          $"{poi.Data?.DisplayName ?? "POI"} da client {requesterClientId}: " +
                          $"RevealedInfoTier→{effectiveTier} (tier nave {CurrentTier}).");
        }

        // ── Modificatori di ruolo (STUB Rev BH) ──────────────────────────────
        //
        // dipende da: sistema ruolo networked per-player (OwnerClientId→ruolo).
        // Oggi il ruolo è una stringa su profilo di menu, non replicata a
        // runtime → questi hook ritornano valori NEUTRI. Quando il registro
        // ruolo esisterà, qui si leggerà il ruolo di clientId e si applicheranno
        // i modificatori (Scanner: cooldown ridotto + tier bonus; altri: neutri
        // o malus). La firma non cambierà.

        /// <summary>Moltiplicatore del cooldown scan attivo per ruolo. STUB=1.0.</summary>
        private float GetRoleCooldownMultiplier(ulong clientId) => 1f;

        /// <summary>Bonus di tier info per ruolo (Scanner). STUB=0.</summary>
        private int GetRoleTierBonus(ulong clientId) => 0;

#if UNITY_EDITOR
        // ── Debug scaffolding (TEMP fino a Blocco 5 upgrade nave) ────────────
        [ContextMenu("DEBUG/Alza tier scanner (+1)")]
        private void DebugRaiseTier()
        {
            if (!Application.isPlaying || !IsServer)
            {
                Debug.LogWarning("[ScannerSystem] DebugRaiseTier: usabile solo in Play " +
                                 "sul server/host.");
                return;
            }
            _currentTier.Value = Mathf.Clamp(_currentTier.Value + 1, 1, 4);
            Debug.Log($"[ScannerSystem] DEBUG tier nave → {_currentTier.Value}.");
        }
#endif

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