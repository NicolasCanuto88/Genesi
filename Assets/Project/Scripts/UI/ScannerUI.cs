using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using SpaceSurvivor.Poi;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;

namespace SpaceSurvivor.UI
{
    /// <summary>
    /// ScannerUI — Milestone 3, Blocco 3 · Rev BH (Fase 2b, D29) · Rev BJ (toggle Radar).
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
    /// VISTA RADAR (Rev BJ):
    ///   Vista SORELLA (ScannerRadarUI) alternata alla lista da un bottone toggle.
    ///   La commutazione NON usa SetActive su questo GameObject (evita il deadlock
    ///   di auto-disattivazione: Update non gira su oggetti inattivi); usa un
    ///   CanvasGroup sul contenuto lista + SetVisible() sul radar. Il bottone
    ///   toggle vive su una barra NON sfumata (sempre cliccabile).
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
    ///   - ScannerRadarUI (vista radar sorella, Rev BJ; opzionale)
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

        [Header("Vista Radar (Rev BJ)")]
        [Tooltip("Pannello radar ping-sonar (vista sorella). Se null, il toggle è " +
                 "nascosto e resta attiva solo la lista.")]
        [SerializeField] private ScannerRadarUI radarPanel;

        [Tooltip("CanvasGroup del contenuto lista/dettaglio: mostrato/nascosto senza " +
                 "SetActive (evita il deadlock di auto-disattivazione). Deve avvolgere " +
                 "header + lista + dettaglio, MA non il bottone toggle.")]
        [SerializeField] private CanvasGroup listContentCanvasGroup;

        [Tooltip("CanvasGroup che racchiude l'INTERA vista Scanner (lista + radar + " +
                 "toggle) — tipicamente su BackgroundPanel, sibling del minigame. " +
                 "Nascosto (alpha 0) durante il minigame di aggancio così lo Scanner " +
                 "sparisce e il minigame resta solo. Se null, fallback: nasconde " +
                 "listContentCanvasGroup + il pulsante toggle.")]
        [SerializeField] private CanvasGroup scannerViewGroup;

        [Tooltip("Bottone che alterna Lista ⇄ Radar. Deve vivere su una barra NON " +
                 "sfumata (fuori dal CanvasGroup lista e dal CanvasGroup radar).")]
        [SerializeField] private Button toggleViewButton;

        [Tooltip("Label del bottone toggle (mostra la vista di DESTINAZIONE).")]
        [SerializeField] private TMP_Text toggleViewLabel;

        [Header("Aggancio (Rev BK)")]
        [Tooltip("Pulsante AGGANCIA/SGANCIA nell'area dettaglio. Vive DENTRO il " +
                 "CanvasGroup lista (si congela col resto durante il minigame e " +
                 "sparisce in modalità radar). Nell'anello di navigazione esplicito " +
                 "chiude il ciclo Toggle↔Righe↔Aggancia. Se null, l'aggancio è " +
                 "disabilitato (resta solo scan + radar).")]
        [SerializeField] private Button lockButton;

        [Tooltip("Label del pulsante aggancio (AGGANCIA / SGANCIA / AGGANCIO N/D).")]
        [SerializeField] private TMP_Text lockButtonLabel;

        [Tooltip("Minigame di aggancio (LockMinigameScanner), tipicamente su un " +
                 "Canvas World Space figlio del monitor Scanner. Se null, il " +
                 "pulsante AGGANCIA non apre nulla.")]
        [SerializeField] private LockMinigameScanner lockMinigame;

        [Tooltip("Colore riga per il POI AGGANCIATO (live). Default verde acceso, " +
                 "distinto da Detected (cyan) / Scanned (ambra).")]
        [SerializeField] private Color lockedColor = new Color(0.2f, 1f, 0.4f);

        [Header("Debug")]
        [SerializeField] private bool logVerbose = false;

        // POI Detected/Scanned → riga. I POI Unknown sono tracciati ma senza riga.
        private readonly Dictionary<PoiInstance, ScannerUIEntry> _entries
            = new Dictionary<PoiInstance, ScannerUIEntry>();

        // POI tracciati (iscritti a OnScanStateChanged), Unknown compresi.
        private readonly HashSet<PoiInstance> _tracked = new HashSet<PoiInstance>();

        private bool isOpen = false;
        private bool _radarMode = false;
        private readonly StringBuilder _sb = new StringBuilder(256);

        // ── Aggancio (Rev BK) ─────────────────────────────────────────────────
        // Minigame in corso: durante il minigame la lista è congelata
        // (interactable off) e EnsureSafety/SetInitial sospesi.
        private bool _lockMinigameActive = false;
        // Ultimo POI selezionato da una RIGA (persiste quando il focus passa al
        // pulsante Aggancia, che altrimenti non risolverebbe alcun POI).
        private PoiInstance _lastSelectedPoi;
        // Diff dello stato di aggancio: ricoloriamo le righe SOLO quando cambia
        // (impostare il colore ogni frame cancella il tint Highlighted/Selected).
        private ulong _prevLockedId = ulong.MaxValue;

        // Cache ordinata per la navigazione esplicita (evita alloc a 10Hz).
        private readonly List<ScannerUIEntry> _navOrdered = new List<ScannerUIEntry>();

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

            // Wiring toggle Radar (Rev BJ). Il bottone appare solo se c'è un radar.
            if (toggleViewButton != null)
            {
                toggleViewButton.onClick.RemoveListener(ToggleView);
                toggleViewButton.onClick.AddListener(ToggleView);
                toggleViewButton.gameObject.SetActive(radarPanel != null);
            }

            // Wiring pulsante Aggancio (Rev BK).
            if (lockButton != null)
            {
                lockButton.onClick.RemoveListener(OnLockButtonClicked);
                lockButton.onClick.AddListener(OnLockButtonClicked);
            }

            SetRadarMode(false); // default: lista

            UpdateUI();
            InvokeRepeating(nameof(UpdateUI), 0f, 0.1f);
            DashboardSelection.SetInitial(this, ChooseInitialSelection, logVerbose);
        }

        public void Close()
        {
            isOpen = false;
            CancelInvoke(nameof(UpdateUI));

            // Se il minigame di aggancio è in corso, interrompilo (nessun lock:
            // l'effetto scatta solo a 100%). Interrupt → OnLockInterrupted, ma
            // isOpen è già false → nessuna riselezione.
            if (_lockMinigameActive && lockMinigame != null)
                lockMinigame.Interrupt();
            _lockMinigameActive = false;
            _lastSelectedPoi = null;

            if (toggleViewButton != null)
                toggleViewButton.onClick.RemoveListener(ToggleView);
            if (lockButton != null)
                lockButton.onClick.RemoveListener(OnLockButtonClicked);

            if (radarPanel != null)
                radarPanel.SetVisible(false);
        }

        private void Update()
        {
            // In modalità radar non forziamo la riselezione della lista (ruberebbe
            // il focus alla vista radar). Durante il minigame di aggancio la lista
            // è congelata → EnsureSafety sospeso.
            if (isOpen && !_radarMode && !_lockMinigameActive)
                DashboardSelection.EnsureSafety(this, ChooseInitialSelection, logVerbose);
        }

        // ── Toggle Lista ⇄ Radar (Rev BJ) ─────────────────────────────────────

        private void ToggleView() => SetRadarMode(!_radarMode);

        private void SetRadarMode(bool radar)
        {
            _radarMode = radar && radarPanel != null;

            // Contenuto lista: visibilità via CanvasGroup (niente SetActive su self).
            if (listContentCanvasGroup != null)
            {
                listContentCanvasGroup.alpha = _radarMode ? 0f : 1f;
                listContentCanvasGroup.interactable = !_radarMode;
                listContentCanvasGroup.blocksRaycasts = !_radarMode;
            }

            // Radar.
            if (radarPanel != null)
                radarPanel.SetVisible(_radarMode);

            // Label mostra la vista di destinazione.
            if (toggleViewLabel != null)
                toggleViewLabel.text = _radarMode ? "◂ LISTA" : "RADAR ▸";

            // Navigazione: anello chiuso Toggle↔Lista (lista) o nessuna direzione (radar).
            RewireNavigation();

            if (_radarMode)
            {
                // Radar: seleziona il toggle così Submit torna alla lista; le direzioni
                // non fanno nulla (Navigation.None sul toggle). Niente selezione lista.
                if (toggleViewButton != null && EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(toggleViewButton.gameObject);
            }
            else if (isOpen)
            {
                DashboardSelection.SetInitial(this, ChooseInitialSelection, logVerbose);
            }
        }

        // ── Refresh (10Hz mentre aperto) ──────────────────────────────────────

        private void UpdateUI()
        {
            UpdateHeader();
            UpdateDistances();
            UpdateDetail();
            UpdateLockButton();
            RefreshLockColorsIfChanged();
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

            // L'ordine è cambiato: ricabla la navigazione esplicita (contenimento).
            RewireNavigation();
        }

        // ── Dettaglio per-tier del POI selezionato ────────────────────────────

        private void UpdateDetail()
        {
            if (detailText == null) return;

            // Aggiorna l'ultimo POI selezionato SOLO quando il focus è su una riga
            // (quando passa al pulsante Aggancia, ResolveSelectedPoi torna null ma
            // il dettaglio deve restare sul POI).
            PoiInstance sel = ResolveSelectedPoi();
            if (sel != null && !ReferenceEquals(sel, _lastSelectedPoi))
            {
                _lastSelectedPoi = sel;
                UpdateLockReturnTarget(); // la freccia SINISTRA del pulsante torna qui
            }

            PoiInstance poi = _lastSelectedPoi;
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

            // Riga stato aggancio (Rev BK).
            if (IsLocked(poi))
                _sb.AppendLine("<b>● AGGANCIATO (live)</b>");

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

        // ── Aggancio (Rev BK) ─────────────────────────────────────────────────

        /// <summary>Click sul pulsante Aggancia/Sgancia. Opera sull'ULTIMO POI
        /// selezionato da una riga (_lastSelectedPoi).</summary>
        private void OnLockButtonClicked()
        {
            var poi = _lastSelectedPoi;
            if (poi == null || poi.NetworkObject == null) return;

            var scanner = ScannerSystem.Instance;
            if (scanner == null) return;

            ulong id = poi.NetworkObject.NetworkObjectId;

            // Già agganciato → sgancio ISTANTANEO (nessun minigame).
            if (scanner.LockedPoiId != 0ul && scanner.LockedPoiId == id)
            {
                scanner.RequestUnlockRpc(id);
                if (logVerbose)
                    Debug.Log($"[ScannerUI] Sgancio '{poi.Data?.DisplayName ?? "POI"}'.");
                return;
            }

            // Gate Q2-b: aggancio solo se almeno Scanned. Altrimenti no-op (la
            // label mostra 'AGGANCIO N/D').
            if ((byte)poi.ScanState >= (byte)PoiScanState.Scanned)
                OpenLockMinigame(poi);
        }

        private void OpenLockMinigame(PoiInstance poi)
        {
            if (lockMinigame == null)
            {
                Debug.LogWarning("[ScannerUI] lockMinigame non assegnato — aggancio " +
                                 "non disponibile.");
                return;
            }
            if (_lockMinigameActive) return;

            _lockMinigameActive = true;
            SetScannerViewVisible(false); // nascondi lo Scanner durante il minigame

            // Azzera la selezione UI: la lista è nascosta e non-interattiva; senza
            // questo, premere le frecce (che il minigame usa per lo slider) farebbe
            // comparire l'highlight su righe invisibili.
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);

            lockMinigame.Open(poi, OnLockComplete, OnLockInterrupted);

            if (logVerbose)
                Debug.Log($"[ScannerUI] Minigame aggancio aperto su " +
                          $"'{poi.Data?.DisplayName ?? "POI"}'.");
        }

        private void OnLockComplete() => EndLockMinigame();
        private void OnLockInterrupted() => EndLockMinigame();

        private void EndLockMinigame()
        {
            if (!_lockMinigameActive) return;
            _lockMinigameActive = false;

            // Riappare lo Scanner (torniamo sempre in modalità lista dopo il minigame).
            SetScannerViewVisible(true);

            // Ripristina la selezione lista (sospesa durante il minigame).
            if (isOpen && !_radarMode)
                DashboardSelection.SetInitial(this, ChooseInitialSelection, logVerbose);
        }

        /// <summary>
        /// Mostra/nasconde l'INTERA vista Scanner durante il minigame di aggancio.
        /// Preferisce scannerViewGroup (BackgroundPanel: nasconde lista+radar+toggle
        /// e lo sfondo). Fallback null-safe: nasconde la lista (alpha) e il pulsante
        /// toggle. Non tocca il radar figlio, che mantiene il suo stato lista/radar.
        /// </summary>
        private void SetScannerViewVisible(bool visible)
        {
            if (scannerViewGroup != null)
            {
                scannerViewGroup.alpha = visible ? 1f : 0f;
                scannerViewGroup.interactable = visible;
                scannerViewGroup.blocksRaycasts = visible;
                return;
            }

            // Fallback (nessun scannerViewGroup assegnato).
            if (listContentCanvasGroup != null)
            {
                listContentCanvasGroup.alpha = visible ? 1f : 0f;
                listContentCanvasGroup.interactable = visible;
                listContentCanvasGroup.blocksRaycasts = visible;
            }
            if (toggleViewButton != null)
                toggleViewButton.gameObject.SetActive(visible);
        }

        private void UpdateLockButton()
        {
            if (lockButtonLabel == null) return;

            var scanner = ScannerSystem.Instance;
            ulong lockedId = scanner != null ? scanner.LockedPoiId : 0ul;

            string label;
            var poi = _lastSelectedPoi;
            if (poi == null || poi.NetworkObject == null)
            {
                label = "AGGANCIO —";
            }
            else
            {
                ulong id = poi.NetworkObject.NetworkObjectId;
                if (lockedId != 0ul && id == lockedId)
                    label = "SGANCIA";
                else if ((byte)poi.ScanState >= (byte)PoiScanState.Scanned)
                    label = "AGGANCIA";
                else
                    label = "AGGANCIO N/D";
            }

            lockButtonLabel.text = label;
        }

        /// <summary>Ricolora le righe SOLO quando lo stato di aggancio cambia
        /// (event-driven). Impostare il colore ogni frame calpesterebbe il tint
        /// di Highlighted/Selected dei Button (regressione Rev BK).</summary>
        private void RefreshLockColorsIfChanged()
        {
            ulong lockedId = ScannerSystem.Instance != null
                ? ScannerSystem.Instance.LockedPoiId : 0ul;
            if (lockedId == _prevLockedId) return;
            _prevLockedId = lockedId;

            foreach (var kv in _entries)
            {
                if (kv.Key == null) continue;
                UpdateEntryColor(kv.Key);
            }
        }

        private bool IsLocked(PoiInstance poi)
        {
            if (poi == null || poi.NetworkObject == null) return false;
            var scanner = ScannerSystem.Instance;
            if (scanner == null || scanner.LockedPoiId == 0ul) return false;
            return scanner.LockedPoiId == poi.NetworkObject.NetworkObjectId;
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
            if (first != null) return first.Button.gameObject;

            // Nessuna voce selezionabile: ripiega sul toggle, così il controller non
            // resta mai senza focus (e può passare al radar).
            return toggleViewButton != null ? toggleViewButton.gameObject : null;
        }

        // ── Navigazione esplicita (contenimento) ──────────────────────────────
        //
        // Le voci sono prefab istanziati a runtime → la navigazione va cablata in
        // codice (non in Inspector). Obiettivo: la navigazione direzionale NON deve
        // poter uscire dal pannello Scanner verso altri canvas (es. i monitor
        // dell'Ingegnere). Costruiamo un anello chiuso Toggle↔Lista con left/right
        // = None. In modalità radar disattiviamo del tutto la navigazione del toggle.

        private void RewireNavigation()
        {
            if (_radarMode)
            {
                if (toggleViewButton != null)
                    toggleViewButton.navigation = new Navigation { mode = Navigation.Mode.None };
                if (lockButton != null)
                    lockButton.navigation = new Navigation { mode = Navigation.Mode.None };
                return;
            }

            // Ordina le voci per sibling index (= ordine visivo dopo il sort).
            _navOrdered.Clear();
            foreach (var kv in _entries)
            {
                if (kv.Value == null || kv.Value.Button == null) continue;
                _navOrdered.Add(kv.Value);
            }
            _navOrdered.Sort((a, b) =>
                a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

            int n = _navOrdered.Count;
            Selectable toggleSel = toggleViewButton;   // può essere null
            Selectable lockSel = lockButton;           // può essere null (aggancio disattivato)

            // Anello VERTICALE: Toggle ↔ riga0 ↔ … ↔ rigaN-1 ↔ Toggle (il pulsante
            // Aggancia NON è nell'anello verticale). Ogni riga raggiunge il pulsante
            // ORIZZONTALMENTE (freccia destra): così andare al pulsante NON cambia il
            // POI selezionato — il gate valuta quello su cui eri davvero (fix Rev BK).
            for (int i = 0; i < n; i++)
            {
                Button b = _navOrdered[i].Button;
                Selectable up = i > 0 ? _navOrdered[i - 1].Button : toggleSel;
                Selectable down = i < n - 1 ? _navOrdered[i + 1].Button : toggleSel;
                b.navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = up,
                    selectOnDown = down,
                    selectOnLeft = null,
                    selectOnRight = lockSel, // destra → Aggancia (dalla riga corrente)
                };
            }

            if (toggleViewButton != null)
            {
                Selectable tDown = n > 0 ? _navOrdered[0].Button
                                 : (lockSel != null ? lockSel : null);
                Selectable tUp = n > 0 ? _navOrdered[n - 1].Button
                               : (lockSel != null ? lockSel : null);
                // Lista vuota: il pulsante resta raggiungibile a destra dal toggle.
                Selectable tRight = n == 0 ? lockSel : null;
                toggleViewButton.navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnDown = tDown,
                    selectOnUp = tUp,
                    selectOnLeft = null,
                    selectOnRight = tRight,
                };
            }

            if (lockButton != null)
            {
                // selectOnLeft è DINAMICO (la riga di provenienza) → impostato da
                // UpdateLockReturnTarget in base al POI selezionato. Qui inizializziamo.
                lockButton.navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = null,
                    selectOnDown = null,
                    selectOnLeft = toggleSel, // placeholder; aggiornato subito sotto
                    selectOnRight = null,
                };
                UpdateLockReturnTarget();
            }
        }

        /// <summary>Fa sì che, dal pulsante Aggancia, la freccia SINISTRA riporti
        /// alla riga da cui si è arrivati (il POI selezionato). Aggiornato quando
        /// cambia la selezione, così il ritorno è sempre coerente.</summary>
        private void UpdateLockReturnTarget()
        {
            if (lockButton == null) return;

            Selectable left = null;
            if (_lastSelectedPoi != null
                && _entries.TryGetValue(_lastSelectedPoi, out var e)
                && e != null && e.Button != null)
                left = e.Button;
            else if (toggleViewButton != null)
                left = toggleViewButton;

            var nav = lockButton.navigation;
            nav.selectOnLeft = left;
            lockButton.navigation = nav;
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

            // Unica sorgente di verità per il colore riga: lock > scanned > detected.
            Color c = IsLocked(poi) ? lockedColor
                    : poi.ScanState == PoiScanState.Scanned ? scannedColor
                    : detectedColor;
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

            // Rev BK: se era il POI di riferimento del dettaglio/aggancio, azzera.
            if (ReferenceEquals(poi, _lastSelectedPoi))
                _lastSelectedPoi = null;

            // La lista è cambiata: ricabla la navigazione (evita link verso la riga
            // appena distrutta e riallinea il ritorno del pulsante Aggancia).
            RewireNavigation();

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