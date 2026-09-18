using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using SpaceSurvivor.Poi;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;

namespace SpaceSurvivor.UI
{
    /// <summary>
    /// ScannerUI — Milestone 3, Blocco 3 · Rev BH (Fase 2b, D29).
    ///
    /// Pannello della POSTAZIONE Scanner (monitor World Space), mostrato solo
    /// mentre il giocatore è seduto alla ScannerStation. Adempie la promessa
    /// registrata nella versione stub (HUD screen-space) di migrare a postazione
    /// fisica "Sensors" (GDD §9.2). Implementa IDashboardPanel: la stazione
    /// chiama Open()/Close() per gestire refresh e selezione.
    ///
    /// CONTENUTO (modello ibrido D29):
    ///   - Lista contatti: ogni POI Detected/Scanned è una riga selezionabile
    ///     (ScannerUIEntry) con nome + distanza, colorata Detected (cyan) /
    ///     Scanned (ambra). Ordinata per distanza crescente.
    ///   - Selezione (frecce/controller, cyan via DashboardSelection) → mostra il
    ///     DETTAGLIO per-tier del POI selezionato.
    ///   - Attivazione (click / Submit) di una riga → SCAN ATTIVO su quel POI via
    ///     ScannerSystem.RequestScanRpc: alza RevealedInfoTier fino al tier nave.
    ///
    /// DETTAGLIO PER-TIER (letto da PoiInstance.RevealedInfoTier + Data + reserve):
    ///   T1 (sempre, se Detected): tipo · massa · distanza
    ///   T2: composizione (PoiData.Composition) · O₂ sì/no (WreckOxygenReserve)
    ///   T3: quantità O₂ (WreckOxygenReserve.Residual, live) · nemici sì/no (STUB)
    ///   T4: blueprint · sistemi · layout (STUB — validazione al Combat, M4.7)
    ///
    /// DIPENDE DA:
    ///   - PoiInstance (eventi statici + ScanState/RevealedInfoTier/Data/LogicalPosition)
    ///   - WreckOxygenReserve (per l'O₂; opzionale, GetComponent sul POI)
    ///   - ShipMovement.Instance (distanza) · ScannerSystem.Instance (tier/range/scan)
    /// </summary>
    public class ScannerUI : MonoBehaviour, IDashboardPanel
    {
        [Header("Header UI")]
        [Tooltip("Text dell'header (es. 'SCANNER · T1 · 2000m').")]
        [SerializeField] private TMP_Text headerText;

        [Tooltip("Text quando nessun POI è Detected (es. 'NESSUN CONTATTO').")]
        [SerializeField] private TMP_Text emptyStateText;

        [Header("Lista contatti")]
        [Tooltip("Prefab della singola riga (ScannerUIEntry, con Button).")]
        [SerializeField] private ScannerUIEntry entryPrefab;

        [Tooltip("Parent delle righe istanziate (VerticalLayoutGroup + ContentSizeFitter).")]
        [SerializeField] private Transform entriesContainer;

        [Header("Dettaglio POI selezionato")]
        [Tooltip("Text multi-riga che mostra il dettaglio per-tier del POI selezionato.")]
        [SerializeField] private TMP_Text detailText;

        [Header("Palette (coerente con PoiVisualIndicator)")]
        [Tooltip("Colore riga per POI Detected. Default cyan #00C8EF.")]
        [SerializeField] private Color detectedColor = new Color(0f, 0.784f, 0.937f);

        [Tooltip("Colore riga per POI Scanned. Default ambra.")]
        [SerializeField] private Color scannedColor = new Color(1f, 0.7f, 0.15f);

        [Header("Debug")]
        [SerializeField] private bool logVerbose = false;

        // POI Detected/Scanned → riga. I POI Unknown sono tracciati ma senza riga.
        private readonly Dictionary<PoiInstance, ScannerUIEntry> _entries
            = new Dictionary<PoiInstance, ScannerUIEntry>();

        // POI tracciati (iscritti a OnScanStateChanged), Unknown compresi.
        private readonly HashSet<PoiInstance> _tracked = new HashSet<PoiInstance>();

        private bool isOpen = false;
        private readonly StringBuilder _sb = new StringBuilder(256);

        // ── Iscrizioni POI (attive quando il GameObject è attivo) ─────────────

        private void OnEnable()
        {
            PoiInstance.OnAnyPoiSpawned += HandlePoiSpawned;
            PoiInstance.OnAnyPoiDespawned += HandlePoiDespawned;

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

            foreach (var poi in _tracked)
            {
                if (poi != null)
                    poi.OnScanStateChanged -= HandlePoiScanStateChanged;
            }
            _tracked.Clear();

            foreach (var kv in _entries)
            {
                if (kv.Value != null)
                {
                    kv.Value.OnActivated -= RequestActiveScan;
                    Destroy(kv.Value.gameObject);
                }
            }
            _entries.Clear();
        }

        // ── IDashboardPanel (chiamato da ScannerStation) ──────────────────────

        public void Open()
        {
            isOpen = true;
            UpdateUI();
            InvokeRepeating(nameof(UpdateUI), 0f, 0.1f);
            DashboardSelection.SetInitial(this, ChooseInitialSelection, logVerbose);
        }

        public void Close()
        {
            isOpen = false;
            CancelInvoke(nameof(UpdateUI));
        }

        private void Update()
        {
            if (isOpen) DashboardSelection.EnsureSafety(this, ChooseInitialSelection, logVerbose);
        }

        // ── Refresh (10Hz mentre aperto) ──────────────────────────────────────

        private void UpdateUI()
        {
            UpdateHeader();
            UpdateDistances();
            UpdateDetail();
        }

        private void UpdateHeader()
        {
            if (headerText == null) return;

            var scanner = ScannerSystem.Instance;
            headerText.text = scanner == null
                ? "SCANNER · —"
                : $"SCANNER · T{scanner.CurrentTier} · {scanner.ScanRange:F0}m";
        }

        private void UpdateDistances()
        {
            var ship = ShipMovement.Instance;
            if (ship == null) return;

            Vector3 shipPos = ship.LogicalPosition;

            foreach (var kv in _entries)
            {
                if (kv.Key == null || kv.Value == null) continue;
                float dist = Vector3.Distance(kv.Key.LogicalPosition, shipPos);
                kv.Value.SetDistance(dist);
            }

            SortEntriesByDistance(shipPos);
        }

        private void SortEntriesByDistance(Vector3 shipPos)
        {
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

        // ── Dettaglio per-tier del POI selezionato ────────────────────────────

        private void UpdateDetail()
        {
            if (detailText == null) return;

            PoiInstance poi = ResolveSelectedPoi();
            if (poi == null || poi.Data == null)
            {
                detailText.text = "Seleziona un contatto.";
                return;
            }

            var ship = ShipMovement.Instance;
            float dist = ship != null
                ? Vector3.Distance(poi.LogicalPosition, ship.LogicalPosition)
                : 0f;

            int tier = poi.RevealedInfoTier;
            int shipTier = ScannerSystem.Instance != null ? ScannerSystem.Instance.CurrentTier : 1;
            var reserve = poi.GetComponent<WreckOxygenReserve>();

            _sb.Clear();
            _sb.AppendLine($"<b>{poi.Data.DisplayName}</b>");

            // T1 — sempre disponibile se rilevato (tipo/massa/distanza).
            _sb.AppendLine($"Tipo: {poi.Data.Type}");
            _sb.AppendLine($"Massa: {poi.Data.Mass:F0}");
            _sb.AppendLine(dist < 1000f ? $"Distanza: {dist:F0} m" : $"Distanza: {dist / 1000f:F1} km");

            // T2 — composizione + O₂ sì/no.
            if (tier >= 2)
            {
                string comp = string.IsNullOrWhiteSpace(poi.Data.Composition)
                    ? "Sconosciuta" : poi.Data.Composition;
                bool hasO2 = reserve != null && reserve.InitialResidual > 0f;
                _sb.AppendLine($"Composizione: {comp}");
                _sb.AppendLine($"O₂: {(hasO2 ? "sì" : "no")}");
            }

            // T3 — quantità O₂ (live) + nemici sì/no (STUB).
            if (tier >= 3)
            {
                string o2Qty = reserve != null ? $"{reserve.Residual:F0}" : "—";
                _sb.AppendLine($"O₂ residuo: {o2Qty}");
                _sb.AppendLine($"Nemici: {(poi.Data.StubHasEnemies ? "sì" : "no")} <size=70%>(stub)</size>");
            }

            // T4 — blueprint + sistemi + layout (STUB).
            if (tier >= 4)
            {
                _sb.AppendLine($"Blueprint: {StubOrDash(poi.Data.StubBlueprintInfo)} <size=70%>(stub)</size>");
                _sb.AppendLine($"Sistemi: {StubOrDash(poi.Data.StubShipSystemsInfo)} <size=70%>(stub)</size>");
                _sb.AppendLine($"Layout: {StubOrDash(poi.Data.StubLayoutInfo)} <size=70%>(stub)</size>");
            }

            // Hint se il tier rivelato non copre ancora T2+.
            if (tier < 2)
            {
                _sb.AppendLine($"<size=80%><i>Scan attivo per T2+ (tier nave T{shipTier}).</i></size>");
            }

            detailText.text = _sb.ToString();
        }

        private static string StubOrDash(string s) =>
            string.IsNullOrWhiteSpace(s) ? "—" : s;

        /// <summary>Risolve il POI attualmente selezionato dall'EventSystem
        /// mappando la selezione alla riga corrispondente.</summary>
        private PoiInstance ResolveSelectedPoi()
        {
            if (EventSystem.current == null) return null;
            var sel = EventSystem.current.currentSelectedGameObject;
            if (sel == null) return null;

            foreach (var kv in _entries)
            {
                if (kv.Value == null) continue;
                if (sel == kv.Value.gameObject || sel.transform.IsChildOf(kv.Value.transform))
                    return kv.Key;
            }
            return null;
        }

        // ── Scan attivo (attivazione riga) ────────────────────────────────────

        private void RequestActiveScan(PoiInstance poi)
        {
            if (poi == null || poi.NetworkObject == null) return;
            var scanner = ScannerSystem.Instance;
            if (scanner == null) return;

            scanner.RequestScanRpc(poi.NetworkObject.NetworkObjectId);
            if (logVerbose)
                Debug.Log($"[ScannerUI] Richiesto scan attivo su " +
                          $"'{poi.Data?.DisplayName ?? "POI"}'.");
        }

        // ── Selezione iniziale (pattern DashboardSelection) ───────────────────

        private GameObject ChooseInitialSelection()
        {
            // Prima riga (per sibling index = più vicina, dopo il sort).
            ScannerUIEntry first = null;
            int bestSibling = int.MaxValue;
            foreach (var kv in _entries)
            {
                if (kv.Value == null || kv.Value.Button == null) continue;
                if (!kv.Value.Button.interactable) continue;
                int sib = kv.Value.transform.GetSiblingIndex();
                if (sib < bestSibling) { bestSibling = sib; first = kv.Value; }
            }
            return first != null ? first.Button.gameObject : null;
        }

        // ── Lifecycle POI (eventi statici) ────────────────────────────────────

        private void HandlePoiSpawned(PoiInstance poi)
        {
            if (poi == null) return;
            if (_tracked.Contains(poi)) return;

            _tracked.Add(poi);
            poi.OnScanStateChanged += HandlePoiScanStateChanged;

            HandlePoiScanStateChanged(PoiScanState.Unknown, poi.ScanState, poi);
        }

        private void HandlePoiDespawned(PoiInstance poi)
        {
            if (poi == null) return;

            poi.OnScanStateChanged -= HandlePoiScanStateChanged;
            _tracked.Remove(poi);

            RemoveEntry(poi);
        }

        // ── Cambio di ScanState ───────────────────────────────────────────────

        private void HandlePoiScanStateChanged(PoiScanState previous, PoiScanState next)
        {
            foreach (var poi in _tracked)
            {
                if (poi == null) continue;
                if (poi.ScanState != PoiScanState.Unknown && !_entries.ContainsKey(poi))
                {
                    CreateOrUpdateEntry(poi);
                }
                else if (poi.ScanState == PoiScanState.Unknown && _entries.ContainsKey(poi))
                {
                    RemoveEntry(poi);
                }
                else if (_entries.ContainsKey(poi))
                {
                    UpdateEntryColor(poi);
                }
            }
            UpdateEmptyStateVisibility();
        }

        private void HandlePoiScanStateChanged(PoiScanState previous, PoiScanState next, PoiInstance sender)
        {
            if (sender == null) return;

            if (next == PoiScanState.Unknown)
                RemoveEntry(sender);
            else
                CreateOrUpdateEntry(sender);

            UpdateEmptyStateVisibility();
        }

        // ── Gestione righe ────────────────────────────────────────────────────

        private void CreateOrUpdateEntry(PoiInstance poi)
        {
            if (poi == null || entryPrefab == null || entriesContainer == null) return;

            if (!_entries.TryGetValue(poi, out var entry) || entry == null)
            {
                entry = Instantiate(entryPrefab, entriesContainer);
                _entries[poi] = entry;

                entry.Bind(poi);
                entry.OnActivated += RequestActiveScan;

                string displayName = poi.Data != null ? poi.Data.DisplayName : "POI";
                entry.SetName(displayName);

                if (logVerbose)
                    Debug.Log($"[ScannerUI] Riga creata per '{displayName}' (state {poi.ScanState}).");
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

            if (entry != null)
            {
                entry.OnActivated -= RequestActiveScan;
                Destroy(entry.gameObject);
            }
            _entries.Remove(poi);

            if (logVerbose)
                Debug.Log($"[ScannerUI] Riga rimossa per '{poi.Data?.DisplayName ?? "POI"}'.");
        }

        private void UpdateEmptyStateVisibility()
        {
            if (emptyStateText != null)
                emptyStateText.gameObject.SetActive(_entries.Count == 0);
        }
    }
}