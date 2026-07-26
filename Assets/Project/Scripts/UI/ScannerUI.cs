using System.Collections.Generic;
using TMPro;
using UnityEngine;
using SpaceSurvivor.Poi;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;

namespace SpaceSurvivor.UI
{
    /// <summary>
    /// ScannerUI — Milestone 3, Blocco 3, Sottofase 2b.
    ///
    /// HUD sempre visibile (Screen Space Overlay, angolo alto-destra) che
    /// mostra la lista dei POI rilevati dallo ScannerSystem. Aggiorna
    /// distanza in tempo reale, ordina per distanza crescente.
    ///
    /// PROMESSA REGISTRATA (Sottofase 2b):
    ///   Questa è una versione "stub funzionale" per verificare end-to-end
    ///   il pipeline PoiSpawner → PoiInstance → ScannerSystem → UI. La
    ///   versione definitiva sarà una postazione fisica "Sensors" sul
    ///   Livello 3 della nave (Sala Osservazione, GDD §9.2), con
    ///   interazione via 'E' come le altre postazioni. Migrazione prevista
    ///   per Blocco 4-5, insieme all'implementazione del Livello 3.
    ///
    /// PATTERN DI ISCRIZIONE:
    ///   Si iscrive agli eventi statici lifecycle di PoiInstance
    ///   (OnAnyPoiSpawned/Despawned) per tracciare POI in vita. Alla
    ///   iscrizione iniziale (OnEnable), scansiona la scena per POI già
    ///   spawnati prima che la UI si attivasse.
    ///
    ///   Ogni PoiInstance tracciato si iscrive individualmente per
    ///   OnScanStateChanged per aggiornare visibilità/colore della sua
    ///   entry.
    ///
    /// ESCLUSIONE DA MainMenu:
    ///   Questa UI è pensata per Game.unity. In MainMenu non ci sono POI
    ///   e ShipMovement.Instance è null. Le guardie difensive nel calcolo
    ///   della distanza evitano crash — ma la scelta più pulita è NON
    ///   piazzare il Canvas in MainMenu.unity, solo in Game.unity.
    ///
    /// DIPENDE DA:
    ///   - PoiInstance (eventi statici + property LogicalPosition, Data,
    ///     ScanState)
    ///   - ShipMovement.Instance (per LogicalPosition della nave)
    ///   - ScannerSystem.Instance (per header: tier e range correnti)
    /// </summary>
    public class ScannerUI : MonoBehaviour
    {
        [Header("Header UI")]
        [Tooltip("Text dell'header (es. 'SCANNER · T1 · 2000m').")]
        [SerializeField] private TMP_Text headerText;

        [Tooltip("Text visualizzato quando nessun POI è Detected " +
                 "(es. 'NESSUN CONTATTO'). Nascosto quando la lista non è " +
                 "vuota.")]
        [SerializeField] private TMP_Text emptyStateText;

        [Header("Lista entries")]
        [Tooltip("Prefab della singola riga (ScannerUIEntry).")]
        [SerializeField] private ScannerUIEntry entryPrefab;

        [Tooltip("Parent Transform delle entries istanziate. Tipicamente un " +
                 "GameObject con VerticalLayoutGroup + ContentSizeFitter.")]
        [SerializeField] private Transform entriesContainer;

        [Header("Palette (coerente con PoiVisualIndicator)")]
        [Tooltip("Colore del testo per POI Detected. Default cyan #00C8EF.")]
        [SerializeField] private Color detectedColor = new Color(0f, 0.784f, 0.937f);

        [Tooltip("Colore del testo per POI Scanned. Default ambra.")]
        [SerializeField] private Color scannedColor = new Color(1f, 0.7f, 0.15f);

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = false;

        // Mappa PoiInstance → entry visuale corrispondente.
        // Contiene SOLO POI attualmente Detected/Scanned. I POI Unknown
        // sono tracciati (iscrizioni attive) ma non hanno entry finché
        // non passano a Detected.
        private readonly Dictionary<PoiInstance, ScannerUIEntry> _entries
            = new Dictionary<PoiInstance, ScannerUIEntry>();

        // Set di POI tracciati (iscritti a OnScanStateChanged), Unknown
        // compresi.
        private readonly HashSet<PoiInstance> _tracked = new HashSet<PoiInstance>();

        // ── Lifecycle Unity ──────────────────────────────────────────────────

        private void OnEnable()
        {
            PoiInstance.OnAnyPoiSpawned += HandlePoiSpawned;
            PoiInstance.OnAnyPoiDespawned += HandlePoiDespawned;

            // Scansione iniziale — copre POI già spawnati prima che questa
            // UI si sia attivata (es. UI in Game.unity mentre POI erano
            // già presenti dalla sessione precedente, o timing di scene load).
#if UNITY_2023_1_OR_NEWER
            var existing = FindObjectsByType<PoiInstance>(FindObjectsSortMode.None);
#else
            var existing = FindObjectsOfType<PoiInstance>();
#endif
            foreach (var poi in existing)
            {
                HandlePoiSpawned(poi);
            }

            UpdateEmptyStateVisibility();
        }

        private void OnDisable()
        {
            PoiInstance.OnAnyPoiSpawned -= HandlePoiSpawned;
            PoiInstance.OnAnyPoiDespawned -= HandlePoiDespawned;

            // Dis-iscrivi tutti i POI tracciati
            foreach (var poi in _tracked)
            {
                if (poi != null)
                    poi.OnScanStateChanged -= HandlePoiScanStateChanged;
            }
            _tracked.Clear();

            // Distruggi tutte le entry esistenti
            foreach (var kv in _entries)
            {
                if (kv.Value != null)
                    Destroy(kv.Value.gameObject);
            }
            _entries.Clear();
        }

        private void Update()
        {
            UpdateHeader();
            UpdateDistances();
        }

        // ── Header ───────────────────────────────────────────────────────────

        private void UpdateHeader()
        {
            if (headerText == null) return;

            var scanner = ScannerSystem.Instance;
            if (scanner == null)
            {
                headerText.text = "SCANNER · —";
                return;
            }

            headerText.text = $"SCANNER · T{scanner.CurrentTier} · {scanner.ScanRange:F0}m";
        }

        // ── Distanze ─────────────────────────────────────────────────────────

        private void UpdateDistances()
        {
            var ship = ShipMovement.Instance;
            if (ship == null) return;

            Vector3 shipPos = ship.LogicalPosition;

            // Aggiorna distanza in ogni entry esistente.
            foreach (var kv in _entries)
            {
                if (kv.Key == null || kv.Value == null) continue;
                float dist = Vector3.Distance(kv.Key.LogicalPosition, shipPos);
                kv.Value.SetDistance(dist);
            }

            // Riordina le entry per distanza crescente. Facciamo l'ordinamento
            // reimpostando siblingIndex — economico per liste piccole
            // (maxActivePoi=5), da ottimizzare solo se la lista cresce
            // sensibilmente.
            SortEntriesByDistance(shipPos);
        }

        private void SortEntriesByDistance(Vector3 shipPos)
        {
            // Estrae le entry attive in una lista ordinata.
            var sorted = new List<KeyValuePair<PoiInstance, ScannerUIEntry>>(_entries);
            sorted.Sort((a, b) =>
            {
                if (a.Key == null || b.Key == null) return 0;
                float da = (a.Key.LogicalPosition - shipPos).sqrMagnitude;
                float db = (b.Key.LogicalPosition - shipPos).sqrMagnitude;
                return da.CompareTo(db);
            });

            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Value != null)
                    sorted[i].Value.transform.SetSiblingIndex(i);
            }
        }

        // ── Lifecycle POI (eventi statici) ───────────────────────────────────

        private void HandlePoiSpawned(PoiInstance poi)
        {
            if (poi == null) return;
            if (_tracked.Contains(poi)) return;

            _tracked.Add(poi);
            poi.OnScanStateChanged += HandlePoiScanStateChanged;

            // Sincronizza stato iniziale — se il POI è già Detected/Scanned
            // al momento dell'iscrizione, crea subito la entry.
            HandlePoiScanStateChanged(PoiScanState.Unknown, poi.ScanState, poi);
        }

        private void HandlePoiDespawned(PoiInstance poi)
        {
            if (poi == null) return;

            poi.OnScanStateChanged -= HandlePoiScanStateChanged;
            _tracked.Remove(poi);

            RemoveEntry(poi);
        }

        // ── Cambio di ScanState ──────────────────────────────────────────────

        // Wrapper per accettare il senderPoi come contesto (l'evento
        // per-instance non passa il sender — lo colleghiamo via closure).
        private void HandlePoiScanStateChanged(PoiScanState previous, PoiScanState next)
        {
            // Non usato — vedi variante a 3 argomenti sotto. Questo è il
            // dispatch base richiesto dalla signature dell'evento; lo
            // ridirigo passando null come sender così l'iteratore delle
            // entry non fa nulla. Il vero dispatch avviene tramite la
            // closure creata in HandlePoiSpawned. Ma per sicurezza,
            // aggiorniamo tutte le entry potenzialmente influenzate.
            foreach (var poi in _tracked)
            {
                if (poi == null) continue;
                if (poi.ScanState == next && !_entries.ContainsKey(poi) && next != PoiScanState.Unknown)
                {
                    CreateOrUpdateEntry(poi);
                }
                else if (poi.ScanState == PoiScanState.Unknown && _entries.ContainsKey(poi))
                {
                    RemoveEntry(poi);
                }
                else if (_entries.ContainsKey(poi))
                {
                    // Aggiorna colore (Detected → Scanned e viceversa)
                    UpdateEntryColor(poi);
                }
            }
            UpdateEmptyStateVisibility();
        }

        // Variante che riceve esplicitamente il PoiInstance sender —
        // chiamata da HandlePoiSpawned per la sync iniziale.
        private void HandlePoiScanStateChanged(PoiScanState previous, PoiScanState next, PoiInstance sender)
        {
            if (sender == null) return;

            if (next == PoiScanState.Unknown)
            {
                RemoveEntry(sender);
            }
            else
            {
                CreateOrUpdateEntry(sender);
            }
            UpdateEmptyStateVisibility();
        }

        // ── Gestione entry ───────────────────────────────────────────────────

        private void CreateOrUpdateEntry(PoiInstance poi)
        {
            if (poi == null || entryPrefab == null || entriesContainer == null) return;

            if (!_entries.TryGetValue(poi, out var entry) || entry == null)
            {
                entry = Instantiate(entryPrefab, entriesContainer);
                _entries[poi] = entry;

                string displayName = poi.Data != null ? poi.Data.DisplayName : "POI";
                entry.SetName(displayName);

                if (verboseLogging)
                    Debug.Log($"[ScannerUI] Entry creata per '{displayName}' " +
                              $"(state {poi.ScanState}).");
            }

            UpdateEntryColor(poi);
        }

        private void UpdateEntryColor(PoiInstance poi)
        {
            if (!_entries.TryGetValue(poi, out var entry)) return;
            if (entry == null) return;

            Color c = poi.ScanState == PoiScanState.Scanned ? scannedColor : detectedColor;
            entry.SetTextColor(c);
        }

        private void RemoveEntry(PoiInstance poi)
        {
            if (!_entries.TryGetValue(poi, out var entry)) return;

            if (entry != null) Destroy(entry.gameObject);
            _entries.Remove(poi);

            if (verboseLogging)
                Debug.Log($"[ScannerUI] Entry rimossa per '{poi.Data?.DisplayName ?? "POI"}'.");
        }

        private void UpdateEmptyStateVisibility()
        {
            if (emptyStateText != null)
                emptyStateText.gameObject.SetActive(_entries.Count == 0);
        }
    }
}
