using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using SpaceSurvivor.Poi;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;

namespace SpaceSurvivor.UI
{
    /// <summary>
    /// ScannerRadarUI — Rev BJ (Radar Scanner ping-sonar, D-Radar).
    ///
    /// Vista SORELLA della lista contatti (ScannerUI) alla postazione Scanner.
    /// Rappresentazione 2D "heading-up" di ciò che vede il Pilota: il puntatore
    /// nave è fisso al centro rivolto verso l'alto (= prua), il mondo ruota
    /// attorno al variare di ShipMovement.LogicalRotation. La nave è a (0,0);
    /// l'altezza (asse Y logico), non rappresentabile in 2D, è un'etichetta
    /// numerica con segno sul blip.
    ///
    /// SWEEP A PING (onda ad anello espandente):
    ///   Un anello cresce dal centro (r = 0) fino a scanRange in pingPeriod
    ///   secondi, poi riparte. Quando l'anello ATTRAVERSA un POI (Detected+),
    ///   in quell'istante ne cattura la posizione e mostra il blip. Il blip vive
    ///   'persistence' secondi (poi sbiadisce e sparisce), riapparendo solo al
    ///   passaggio successivo → la posizione mostrata è volutamente stale.
    ///
    /// TIER-GATING (tre assi ortogonali, tutti da dati esistenti):
    ///   1. Portata   = ScannerSystem.ScanRange (T1 2000m … T4 8000m): POI oltre
    ///                  portata non vengono mai pingati.
    ///   2. Cadenza   = pingPeriodByTier (più corto ai tier alti = sweep più veloce).
    ///   3. Persistenza = blipPersistenceByTier (più lunga ai tier alti): a tier
    ///                  alto la persistenza tende al periodo → il radar tende al "live".
    ///
    /// CONTENUTO PER-TIER (Q2-b, da PoiInstance.RevealedInfoTier):
    ///   Detected (T1)      : quadratino + etichetta altezza.
    ///   RevealedInfoTier≥2 : + codice tipo + freccia rotta (da LogicalVelocity).
    ///
    /// PALETTO: il rilevamento (Unknown→Detected) resta il sistema passivo separato
    /// (Rev BH). Lo sweep è VISUALIZZAZIONE, non rilevamento: un blip compare
    /// perché il POI era già Detected, non perché l'anello "lo ha trovato".
    ///
    /// GUIDA: pura voce in Rev BJ (nessun canale dati nuovo). L'aggancio "live" di
    /// un POI e l'interfaccia Pilota sono Rev BK / item opzionale futuro.
    ///
    /// DIPENDE DA:
    ///   - PoiInstance (eventi statici + ScanState/RevealedInfoTier/Data/
    ///     LogicalPosition/LogicalVelocity)
    ///   - ShipMovement.Instance (pose logica) · ScannerSystem.Instance (tier/range)
    ///   - RadarBlip (vista del singolo blip)
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class ScannerRadarUI : MonoBehaviour
    {
        [Header("Header UI (opzionale)")]
        [Tooltip("Text dell'header radar (es. 'RADAR · T1 · 2000m').")]
        [SerializeField] private TMP_Text headerText;

        [Tooltip("Text quando nessun POI Detected è in portata (es. 'NESSUN CONTATTO').")]
        [SerializeField] private TMP_Text emptyStateText;

        [Header("Area radar")]
        [Tooltip("RectTransform quadrato dell'area radar. Il suo semi-lato in pixel " +
                 "mappa scanRange (metri). I blip vivono qui dentro (parent di default).")]
        [SerializeField] private RectTransform radarArea;

        [Tooltip("Puntatore nave: fisso al centro, rivolto verso l'alto (heading-up). " +
                 "Non viene ruotato dal codice.")]
        [SerializeField] private RectTransform shipPointer;

        [Tooltip("Anello dello sweep: Image (idealmente un anello cavo) ancorata al " +
                 "centro. Ne guido sizeDelta e alpha ogni frame. Opzionale.")]
        [SerializeField] private Image ringImage;

        [Header("Blip")]
        [Tooltip("Prefab del blip (RadarBlip).")]
        [SerializeField] private RadarBlip blipPrefab;

        [Tooltip("Parent dei blip istanziati. Se null si usa 'radarArea'.")]
        [SerializeField] private RectTransform blipContainer;

        [Tooltip("Secondi di fade in coda alla vita del blip (prima di sparire).")]
        [SerializeField] private float blipFadeSeconds = 0.4f;

        [Header("Tier — cadenza sweep (secondi per giro), indice = tier-1")]
        [Tooltip("Periodo dell'anello per tier T1..T4. Più corto = sweep più veloce.")]
        [SerializeField] private float[] pingPeriodByTier = { 4f, 3f, 2f, 1.5f };

        [Header("Tier — persistenza blip (secondi), indice = tier-1")]
        [Tooltip("Vita del blip per tier T1..T4. Più lunga = radar più 'live'.")]
        [SerializeField] private float[] blipPersistenceByTier = { 0.8f, 1.2f, 1.8f, 2.5f };

        [Header("Freccia rotta (RevealedInfoTier ≥ 2)")]
        [Tooltip("Velocità logica minima (unità/s) per mostrare la freccia rotta.")]
        [SerializeField] private float minVelocityForArrow = 1f;

        [Header("Palette (coerente con ScannerUI / PoiVisualIndicator)")]
        [Tooltip("Colore blip per POI Detected. Default cyan #00C8EF.")]
        [SerializeField] private Color detectedColor = new Color(0f, 0.784f, 0.937f);

        [Tooltip("Colore blip per POI Scanned. Default ambra.")]
        [SerializeField] private Color scannedColor = new Color(1f, 0.7f, 0.15f);

        [Tooltip("Colore dell'anello sweep.")]
        [SerializeField] private Color ringColor = new Color(0f, 0.784f, 0.937f, 1f);

        [Tooltip("Alpha massimo dell'anello (al centro; sfuma verso il bordo).")]
        [SerializeField] private float ringMaxAlpha = 0.5f;

        [Header("Aggancio live (Rev BK)")]
        [Tooltip("Colore del blip AGGANCIATO (live), distinto da detected/scanned. " +
                 "Default verde acceso.")]
        [SerializeField] private Color lockedColor = new Color(0.2f, 1f, 0.4f);

        [Tooltip("Glifo prefisso sull'etichetta del blip agganciato (es. '◎'). " +
                 "Vuoto = nessun glifo (resta il solo colore + lockIndicator).")]
        [SerializeField] private string lockMarkerGlyph = "◎";

        [Tooltip("Alpha del blip agganciato quando è FUORI portata (clampato al " +
                 "bordo del radar come indicatore direzionale per il Pilota).")]
        [Range(0f, 1f)]
        [SerializeField] private float lockedOutOfRangeAlpha = 0.6f;

        [Header("Debug")]
        [SerializeField] private bool logVerbose = false;

        // ── Stato interno ─────────────────────────────────────────────────────

        private sealed class BlipState
        {
            public RadarBlip View;
            public float Deadline;   // Time.time oltre il quale scade
            public float Life;       // durata totale assegnata (per il fade)
        }

        private readonly HashSet<PoiInstance> _tracked = new HashSet<PoiInstance>();
        private readonly Dictionary<PoiInstance, BlipState> _active
            = new Dictionary<PoiInstance, BlipState>();
        private readonly Stack<RadarBlip> _pool = new Stack<RadarBlip>();
        private readonly List<PoiInstance> _expired = new List<PoiInstance>();

        private bool _visible = false;
        private float _ringTimer = 0f;
        private float _prevRingRadiusM = 0f;

        // ── Aggancio live (Rev BK) ────────────────────────────────────────────
        // Vista dedicata del target agganciato, gestita FUORI dal pool ping
        // (_active/_pool): è un singolo blip continuo, non un blip a scatti.
        private RadarBlip _lockedView;
        private ulong _lockedIdCached = 0ul;

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
                HandlePoiSpawned(poi);
        }

        private void OnDisable()
        {
            PoiInstance.OnAnyPoiSpawned -= HandlePoiSpawned;
            PoiInstance.OnAnyPoiDespawned -= HandlePoiDespawned;

            _tracked.Clear();
            ClearAllBlips();
        }

        // ── API vista (chiamata da ScannerUI) ─────────────────────────────────

        /// <summary>
        /// Mostra/nasconde il radar senza SetActive (evita il deadlock di
        /// auto-disattivazione: Update non gira su GameObject inattivo). La
        /// visibilità è pilotata via CanvasGroup + flag _visible che gate Update.
        /// </summary>
        public void SetVisible(bool visible)
        {
            _visible = visible;

            var cg = GetCanvasGroup();
            if (cg != null)
            {
                cg.alpha = visible ? 1f : 0f;
                cg.interactable = visible;
                cg.blocksRaycasts = visible;
            }

            if (visible)
            {
                // Sweep fresco all'ingresso.
                _ringTimer = 0f;
                _prevRingRadiusM = 0f;
            }
            else
            {
                ClearAllBlips();
            }
        }

        private CanvasGroup _cachedCg;
        private bool _cgResolved;
        private CanvasGroup GetCanvasGroup()
        {
            if (!_cgResolved)
            {
                _cachedCg = GetComponent<CanvasGroup>();
                _cgResolved = true;
            }
            return _cachedCg;
        }

        // ── Loop principale (solo quando visibile) ────────────────────────────

        private void Update()
        {
            if (!_visible) return;

            var ship = ShipMovement.Instance;
            if (ship == null) return;

            var scanner = ScannerSystem.Instance;
            int tier = scanner != null ? Mathf.Clamp(scanner.CurrentTier, 1, 4) : 1;
            float scanRange = scanner != null ? scanner.ScanRange : 2000f;
            if (scanRange <= 0f) scanRange = 2000f;

            // Rev BK: id del target agganciato (0 = nessuno). Cache per-frame:
            // usato sia per escludere il locked dal ciclo ping (niente doppio
            // blip) sia per disegnarlo live.
            _lockedIdCached = scanner != null ? scanner.LockedPoiId : 0ul;

            float pingPeriod = ByTier(pingPeriodByTier, tier, 3f);
            float persistence = ByTier(blipPersistenceByTier, tier, 1f);

            // Avanzamento anello + rilevamento attraversamenti.
            float prevM = _prevRingRadiusM;
            _ringTimer += Time.deltaTime;

            if (_ringTimer >= pingPeriod && pingPeriod > 0f)
            {
                _ringTimer -= pingPeriod;
                // Chiudi il ciclo precedente fino al bordo, poi riparti da 0.
                DetectCrossings(prevM, scanRange, ship, scanRange, persistence);
                prevM = 0f;
            }

            float currM = (pingPeriod > 0f ? _ringTimer / pingPeriod : 0f) * scanRange;
            DetectCrossings(prevM, currM, ship, scanRange, persistence);
            _prevRingRadiusM = currM;

            UpdateRingVisual(currM, scanRange);
            UpdateBlips();
            UpdateLockedBlip(ship, scanRange);
            UpdateHeader(tier, scanRange);
            UpdateEmptyState(ship, scanRange);
        }

        private static float ByTier(float[] arr, int tier, float fallback)
        {
            if (arr == null || arr.Length == 0) return fallback;
            int idx = Mathf.Clamp(tier - 1, 0, arr.Length - 1);
            float v = arr[idx];
            return v > 0f ? v : fallback;
        }

        // ── Attraversamenti dell'anello → cattura blip ────────────────────────

        private void DetectCrossings(float lo, float hi, ShipMovement ship,
                                     float scanRange, float persistence)
        {
            if (hi <= lo) return;

            Quaternion invShip = Quaternion.Inverse(ship.LogicalRotation);
            Vector3 shipPos = ship.LogicalPosition;
            float displayRadius = DisplayRadiusPixels();

            foreach (var poi in _tracked)
            {
                if (poi == null) continue;
                if (poi.ScanState == PoiScanState.Unknown) continue; // solo Detected+

                // Rev BK: il target agganciato è disegnato LIVE (UpdateLockedBlip),
                // non a ping → escludilo qui per non avere un doppio blip.
                if (_lockedIdCached != 0ul && poi.NetworkObject != null
                    && poi.NetworkObject.NetworkObjectId == _lockedIdCached)
                    continue;

                Vector3 rel = invShip * (poi.LogicalPosition - shipPos);
                float horiz = Mathf.Sqrt(rel.x * rel.x + rel.z * rel.z);
                if (horiz > scanRange) continue;                     // fuori portata

                if (horiz > lo && horiz <= hi)
                    CaptureBlip(poi, rel, invShip, scanRange, displayRadius, persistence);
            }
        }

        private void CaptureBlip(PoiInstance poi, Vector3 rel, Quaternion invShip,
                                 float scanRange, float displayRadius, float persistence)
        {
            if (blipPrefab == null) return;

            // Posizione heading-up: x = destra (rel.x), su = avanti/prua (rel.z).
            Vector2 anchored = new Vector2(rel.x, rel.z) / scanRange * displayRadius;

            int infoTier = poi.RevealedInfoTier;
            Color col = poi.ScanState == PoiScanState.Scanned ? scannedColor : detectedColor;
            string height = FormatHeight(rel.y);

            string typeCode = "";
            bool showArrow = false;
            float arrowAngle = 0f;

            if (infoTier >= 2)
            {
                typeCode = TypeCode(poi.Data);

                Vector3 relVel = invShip * poi.LogicalVelocity;
                Vector2 vel2 = new Vector2(relVel.x, relVel.z);
                if (vel2.magnitude >= minVelocityForArrow)
                {
                    showArrow = true;
                    // Freccia (0 = verso l'alto/prua) ruotata verso la rotta.
                    arrowAngle = Mathf.Atan2(-vel2.x, vel2.y) * Mathf.Rad2Deg;
                }
            }

            BlipState st = GetOrCreateBlip(poi);
            st.View.SetAnchoredPosition(anchored);
            st.View.Configure(col, height, typeCode, showArrow, arrowAngle);
            st.View.SetAlpha(1f);
            st.Deadline = Time.time + persistence;
            st.Life = persistence;

            if (logVerbose)
            {
                string dn = poi.Data != null ? poi.Data.DisplayName : "POI";
                Debug.Log($"[ScannerRadarUI] Ping '{dn}' h={height} tier={infoTier}.");
            }
        }

        // ── Aggancio live (Rev BK) ────────────────────────────────────────────

        /// <summary>
        /// Disegna il target AGGANCIATO come blip CONTINUO (posizione reale ogni
        /// frame), bypassando il ciclo ping. Se il POI è fuori portata, il blip è
        /// clampato al bordo del radar (indicatore direzionale per il Pilota) con
        /// alpha ridotta. Se non c'è lock o il POI non è risolvibile, nasconde la
        /// vista dedicata.
        /// </summary>
        private void UpdateLockedBlip(ShipMovement ship, float scanRange)
        {
            if (_lockedIdCached == 0ul) { HideLockedBlip(); return; }

            // Risoluzione client-side del PoiInstance dal NetworkObjectId. Uso
            // SpawnManager.SpawnedObjects (invariante: non NetworkManager.SpawnedObjects).
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) { HideLockedBlip(); return; }
            if (!nm.SpawnManager.SpawnedObjects.TryGetValue(_lockedIdCached, out var netObj)
                || netObj == null) { HideLockedBlip(); return; }
            if (!netObj.TryGetComponent<PoiInstance>(out var poi) || poi == null)
            { HideLockedBlip(); return; }

            Quaternion invShip = Quaternion.Inverse(ship.LogicalRotation);
            Vector3 rel = invShip * (poi.LogicalPosition - ship.LogicalPosition);
            float displayRadius = DisplayRadiusPixels();

            Vector2 dir2 = new Vector2(rel.x, rel.z);
            float horiz = dir2.magnitude;

            Vector2 anchored;
            float alpha;
            if (scanRange > 0f && horiz <= scanRange)
            {
                anchored = dir2 / scanRange * displayRadius;
                alpha = 1f;
            }
            else
            {
                // Fuori portata: clamp al bordo (edge indicator direzionale).
                Vector2 d = dir2.sqrMagnitude > 0.0001f ? dir2.normalized : Vector2.up;
                anchored = d * displayRadius;
                alpha = lockedOutOfRangeAlpha;
            }

            RadarBlip view = EnsureLockedView();
            if (view == null) return;

            int infoTier = poi.RevealedInfoTier;
            string height = FormatHeight(rel.y);

            string typeCode = "";
            bool showArrow = false;
            float arrowAngle = 0f;
            if (infoTier >= 2)
            {
                typeCode = TypeCode(poi.Data);

                Vector3 relVel = invShip * poi.LogicalVelocity;
                Vector2 vel2 = new Vector2(relVel.x, relVel.z);
                if (vel2.magnitude >= minVelocityForArrow)
                {
                    showArrow = true;
                    arrowAngle = Mathf.Atan2(-vel2.x, vel2.y) * Mathf.Rad2Deg;
                }
            }

            string heightLabel = string.IsNullOrEmpty(lockMarkerGlyph)
                ? height : $"{lockMarkerGlyph} {height}";

            view.gameObject.SetActive(true);
            view.SetLocked(true);
            view.SetAnchoredPosition(anchored);
            view.Configure(lockedColor, heightLabel, typeCode, showArrow, arrowAngle);
            view.SetAlpha(alpha);
        }

        private RadarBlip EnsureLockedView()
        {
            if (_lockedView == null)
            {
                if (blipPrefab == null) return null;
                Transform parent = blipContainer != null ? blipContainer : radarArea;
                _lockedView = Instantiate(blipPrefab, parent);
                // Sopra i blip ping (z-order): il target agganciato è prioritario.
                _lockedView.transform.SetAsLastSibling();
            }
            return _lockedView;
        }

        private void HideLockedBlip()
        {
            if (_lockedView == null) return;
            _lockedView.SetLocked(false);
            _lockedView.SetAlpha(0f);
            if (_lockedView.gameObject.activeSelf)
                _lockedView.gameObject.SetActive(false);
        }

        // ── Fade / scadenza blip ──────────────────────────────────────────────

        private void UpdateBlips()
        {
            _expired.Clear();

            foreach (var kv in _active)
            {
                if (kv.Key == null) { _expired.Add(kv.Key); continue; }

                BlipState st = kv.Value;
                float remaining = st.Deadline - Time.time;
                if (remaining <= 0f) { _expired.Add(kv.Key); continue; }

                float fade = Mathf.Min(blipFadeSeconds, st.Life);
                float a = (fade > 0f && remaining < fade) ? (remaining / fade) : 1f;
                st.View.SetAlpha(a);
            }

            for (int i = 0; i < _expired.Count; i++)
                ReturnBlip(_expired[i]);
        }

        // ── Anello sweep ──────────────────────────────────────────────────────

        private void UpdateRingVisual(float currM, float scanRange)
        {
            if (ringImage == null) return;

            float displayRadius = DisplayRadiusPixels();
            float frac = scanRange > 0f ? Mathf.Clamp01(currM / scanRange) : 0f;
            float diameter = 2f * displayRadius * frac;

            ringImage.rectTransform.sizeDelta = new Vector2(diameter, diameter);

            Color c = ringColor;
            c.a = ringMaxAlpha * (1f - frac);
            ringImage.color = c;
        }

        // ── Header / empty state ──────────────────────────────────────────────

        private void UpdateHeader(int tier, float scanRange)
        {
            if (headerText == null) return;
            headerText.text = $"RADAR · T{tier} · {scanRange:F0}m";
        }

        private void UpdateEmptyState(ShipMovement ship, float scanRange)
        {
            if (emptyStateText == null) return;

            bool any = false;
            Vector3 shipPos = ship.LogicalPosition;
            foreach (var poi in _tracked)
            {
                if (poi == null) continue;
                if (poi.ScanState == PoiScanState.Unknown) continue;
                if ((poi.LogicalPosition - shipPos).sqrMagnitude <= scanRange * scanRange)
                {
                    any = true;
                    break;
                }
            }

            if (emptyStateText.gameObject.activeSelf == any)
                emptyStateText.gameObject.SetActive(!any);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private float DisplayRadiusPixels()
        {
            if (radarArea == null) return 100f;
            return 0.5f * Mathf.Min(radarArea.rect.width, radarArea.rect.height);
        }

        private static string FormatHeight(float y)
        {
            float ry = Mathf.Round(y);
            if (Mathf.Abs(ry) < 0.5f) return "0";
            return (ry > 0f ? "+" : "") + ry.ToString("F0");
        }

        private static string TypeCode(PoiData data)
        {
            if (data == null) return "";
            string t = data.Type.ToString().ToUpperInvariant();
            if (string.IsNullOrEmpty(t)) return "";
            return t.Length <= 3 ? t : t.Substring(0, 3);
        }

        // ── Pool blip ─────────────────────────────────────────────────────────

        private BlipState GetOrCreateBlip(PoiInstance poi)
        {
            if (_active.TryGetValue(poi, out var existing) && existing.View != null)
                return existing;

            RadarBlip view = _pool.Count > 0 ? _pool.Pop() : null;
            if (view == null)
            {
                Transform parent = blipContainer != null ? blipContainer : radarArea;
                view = Instantiate(blipPrefab, parent);
            }

            view.gameObject.SetActive(true);
            var st = new BlipState { View = view, Deadline = 0f, Life = 0f };
            _active[poi] = st;
            return st;
        }

        private void ReturnBlip(PoiInstance poi)
        {
            // Un PoiInstance distrutto è "fake-null" (poi == null è true via overload
            // Unity) MA resta una chiave valida nel dizionario, che usa reference-
            // equality (EqualityComparer<T>.Default, non l'operatore == di Unity).
            // Quindi rimuoviamo per riferimento ESATTO: TryGetValue/Remove trovano
            // la chiave sia che il POI sia vivo, fake-null, o (dal despawn) vivo.
            if (_active.TryGetValue(poi, out var st))
            {
                _active.Remove(poi);
                RecycleView(st.View);
            }
        }

        private void RecycleView(RadarBlip view)
        {
            if (view == null) return;
            view.SetAlpha(0f);
            view.gameObject.SetActive(false);
            _pool.Push(view);
        }

        private void ClearAllBlips()
        {
            foreach (var kv in _active)
                RecycleView(kv.Value.View);
            _active.Clear();

            // Rev BK: nascondi anche il blip live del target agganciato (la vista
            // dedicata non fa parte del pool ping).
            HideLockedBlip();
        }

        // ── Lifecycle POI (eventi statici) ────────────────────────────────────

        private void HandlePoiSpawned(PoiInstance poi)
        {
            if (poi == null) return;
            _tracked.Add(poi);
        }

        private void HandlePoiDespawned(PoiInstance poi)
        {
            if (poi == null) return;
            _tracked.Remove(poi);
            ReturnBlip(poi);
        }
    }
}