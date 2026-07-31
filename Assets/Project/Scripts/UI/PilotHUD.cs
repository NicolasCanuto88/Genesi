using TMPro;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;
using SpaceSurvivor.Poi;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PilotHUD — Milestone 2, esteso Rev T (velocità), Rev W (Docking/Anchor 3.1.6).
/// Dashboard cockpit del Pilota. Canvas World Space, display-only.
///
/// SEZIONI:
///   Zona       — tipo zona + evento attivo (icona emoji + label)
///   Navigazione — stato corrente (colore + label) + warning autopilota
///                  esteso Rev W con stati Docking / Docked
///   Propulsione — fuel cells + velocità corrente/target/max (Rev T)
///   FTL        — stato + barra carica (Charging) + timer MM:SS (Cooldown/Lockout)
///   Scudi      — stato (Off/SpinUp/On) + HP + SciFiSegmentedBar
///   Docking    — [Rev W] "DOCKED TO [POI]" quando NavState == Docked
///                        + prompt di ancoraggio quando ancorabile / troppo veloce
///
/// AGGIORNAMENTO:
///   InvokeRepeating(RefreshAll, 0.25s) in Open() — sospeso in Close().
///   Pattern identico agli altri IDashboardPanel del progetto.
///
/// CANVAS SETUP (Inspector):
///   Render Mode: World Space · Scale: 0.001,0.001,0.001
///   NO Mask + Viewport — Content come figlio diretto di Panel_Background
///
/// NESSUN VirtualCursor: il Pilota controlla via InputAction, non clic UI.
///
/// DIPENDE DA:
///   PropulsionSystem · FTLDrive · ShieldSystem · ZoneManager · InventorySystem
///   AnchorSystem · PoiInstance (via NetworkManager.SpawnManager)
/// </summary>
public class PilotHUD : MonoBehaviour, IDashboardPanel
{
    // =========================================================================
    // SEZIONE ZONA
    // =========================================================================

    [Header("— Zona —")]
    [Tooltip("Label tipo zona: SISTEMA INTERNO / FRONTIERA / VUOTO PROFONDO")]
    [SerializeField] private TextMeshProUGUI labelZoneType;

    [Tooltip("Label evento attivo: ☢ TEMPESTA RADIAZIONI / ☄ CAMPO METEORITI / ecc.")]
    [SerializeField] private TextMeshProUGUI labelZoneEvent;

    // =========================================================================
    // SEZIONE NAVIGAZIONE
    // =========================================================================

    [Header("— Navigazione —")]
    [Tooltip("Label stato navigazione: ANCORATA / INERZIA / AUTOPILOTA / MANUALE / " +
             "FTL / ATTRACCO / ATTRACCATA…")]
    [SerializeField] private TextMeshProUGUI labelNavState;

    [Tooltip("Panel attivato quando l'autopilota NON è disponibile (AsteroidField)")]
    [SerializeField] private GameObject panelAutopilotWarning;

    // =========================================================================
    // SEZIONE PROPULSIONE — FUEL
    // =========================================================================

    [Header("— Propulsione — Fuel —")]
    [Tooltip("Contatore fuel cells: 'FUEL: 18'")]
    [SerializeField] private TextMeshProUGUI labelFuelCount;

    [Tooltip("Barra segmentata fuel. SetValue() con valore 0–1")]
    [SerializeField] private SciFiSegmentedBar barFuel;

    [Tooltip("Massimo fuel cells per la barra (default 30). Non hardcoded nel GDD → configurabile")]
    [SerializeField] private int maxFuelDisplay = 30;

    // =========================================================================
    // SEZIONE PROPULSIONE — VELOCITÀ (Rev T)
    // =========================================================================

    [Header("— Propulsione — Velocità (Rev T) —")]
    [Tooltip("Label velocità unica formato: 'VEL: 42.3 → 60.0  (max 100)  m/s'. " +
             "Il target è nascosto quando ~= current (nave stabile). Opzionale — " +
             "se null, nessun display velocità.")]
    [SerializeField] private TextMeshProUGUI labelSpeedCurrent;

    [Tooltip("Barra segmentata velocità (Current/Max). Opzionale — se null, no-op.")]
    [SerializeField] private SciFiSegmentedBar barSpeed;

    [Tooltip("Colore per la scritta velocità quando la nave è STABILE (current ≈ target). " +
             "Default: colore accent principale, sensazione 'crociera'.")]
    [SerializeField] private Color colorSpeedStable = new Color(0.20f, 1.00f, 0.50f);

    [Tooltip("Colore per la scritta velocità quando la nave ACCELERA (current < target). " +
             "Default: cyan brillante, coerente con lo stile terminal.")]
    [SerializeField] private Color colorSpeedAccelerating = new Color(0.00f, 0.80f, 1.00f);

    [Tooltip("Colore per la scritta velocità quando la nave DECELERA (current > target). " +
             "Default: ambra, feedback di 'freno in atto'.")]
    [SerializeField] private Color colorSpeedDecelerating = new Color(1.00f, 0.70f, 0.10f);

    // =========================================================================
    // SEZIONE FTL
    // =========================================================================

    [Header("— FTL Drive —")]
    [Tooltip("Label stato FTL: FTL: PRONTO / FTL: CARICA 73% / FTL: COOLDOWN / ecc.")]
    [SerializeField] private TextMeshProUGUI labelFTLStatus;

    [Tooltip("Panel visibile SOLO durante FTLState.Charging — contiene barFTLCharge")]
    [SerializeField] private GameObject panelFTLCharge;

    [Tooltip("Barra avanzamento carica FTL (0–1)")]
    [SerializeField] private SciFiSegmentedBar barFTLCharge;

    [Tooltip("Panel visibile durante Cooldown e Lockout — contiene labelFTLTimer")]
    [SerializeField] private GameObject panelFTLTimer;

    [Tooltip("Timer countdown in formato MM:SS")]
    [SerializeField] private TextMeshProUGUI labelFTLTimer;

    // =========================================================================
    // SEZIONE SCUDI
    // =========================================================================

    [Header("— Scudi —")]
    [Tooltip("Label stato scudi: SCUDI: INATTIVI / SCUDI: SPIN-UP / SCUDI: ATTIVI")]
    [SerializeField] private TextMeshProUGUI labelShieldStatus;

    [Tooltip("Label HP: '50 / 50 HP  (100%)'")]
    [SerializeField] private TextMeshProUGUI labelShieldHP;

    [Tooltip("Barra HP scudi (0–1)")]
    [SerializeField] private SciFiSegmentedBar barShieldHP;

    // =========================================================================
    // SEZIONE DOCKING / ANCHOR (Rev W — Blocco 3.1.6)
    // =========================================================================

    [Header("— Docking / Anchor (Rev W) —")]
    [Tooltip("Label mostrata quando la nave è ATTRACCATA a un POI. " +
             "Formato: 'ATTRACCATA A: [displayName]'. Vuota se NavState != Docked. " +
             "Opzionale — se null, no-op.")]
    [SerializeField] private TextMeshProUGUI labelDockingStatus;

    [Tooltip("Label prompt di ancoraggio. Mostrata quando AnchorabilityState == Anchorable " +
             "(prompt di ingresso) o InRangeTooFast (warning velocità). " +
             "Vuota altrimenti. Opzionale — se null, no-op.")]
    [SerializeField] private TextMeshProUGUI labelAnchorPrompt;

    [Tooltip("Testo mostrato quando AnchorabilityState == Anchorable. " +
             "Modificabile in inspector per test di gameplay / rebind futuri.")]
    [SerializeField]
    private string anchorPromptAnchorable =
        "▲ ANCORAGGIO DISPONIBILE — premi [T/X] per iniziare";

    [Tooltip("Testo mostrato quando AnchorabilityState == InRangeTooFast. " +
             "Modificabile in inspector per tuning UX.")]
    [SerializeField]
    private string anchorPromptTooFast =
        "▼ TROPPO VELOCE — rallenta per poter attraccare";

    [Tooltip("Prefisso label docking status. Modificabile in inspector.")]
    [SerializeField] private string dockingStatusPrefix = "ATTRACCATA A: ";

    [Tooltip("Fallback per il nome del POI se non risolvibile lato client " +
             "(SpawnManager non ha ancora l'oggetto, o PoiInstance senza Data).")]
    [SerializeField] private string dockingStatusUnknownPoiName = "POI SCONOSCIUTO";

    // =========================================================================
    // COLORI DI STATO
    // =========================================================================

    [Header("— Colori Navigazione —")]
    [SerializeField] private Color colorAnchored = new Color(0.50f, 0.50f, 0.50f);
    [SerializeField] private Color colorCoasting = new Color(0.40f, 0.80f, 1.00f);
    [SerializeField] private Color colorAutopilot = new Color(0.20f, 1.00f, 0.40f);
    [SerializeField] private Color colorManual = new Color(1.00f, 0.90f, 0.20f);
    [SerializeField] private Color colorFTL = new Color(0.60f, 0.20f, 1.00f);
    [SerializeField] private Color colorWarning = new Color(1.00f, 0.50f, 0.00f);
    [SerializeField] private Color colorOn = new Color(0.20f, 1.00f, 0.50f);
    [SerializeField] private Color colorOff = new Color(0.40f, 0.40f, 0.40f);

    [Header("— Colori Docking (Rev W) —")]
    [Tooltip("Colore label NavState quando NavigationState == Docking (attracco in corso).")]
    [SerializeField] private Color colorDocking = new Color(0.00f, 0.80f, 1.00f);

    [Tooltip("Colore label NavState quando NavigationState == Docked (attraccata).")]
    [SerializeField] private Color colorDocked = new Color(0.20f, 1.00f, 0.50f);

    [Tooltip("Colore label prompt quando Anchorable (invito ad attraccare).")]
    [SerializeField] private Color colorAnchorPromptOk = new Color(0.20f, 1.00f, 0.50f);

    [Tooltip("Colore label prompt quando InRangeTooFast (warning velocità).")]
    [SerializeField] private Color colorAnchorPromptTooFast = new Color(1.00f, 0.70f, 0.10f);

    // =========================================================================
    // IDashboardPanel
    // =========================================================================

    /// <summary>
    /// Chiamato da PilotStation al termine della transizione camera.
    /// Mostra lo stato attuale e avvia il polling.
    /// </summary>
    public void Open()
    {
        RefreshAll();
        InvokeRepeating(nameof(RefreshAll), 0.25f, 0.25f);
    }

    /// <summary>
    /// Chiamato da PilotStation all'uscita. Sospende il polling.
    /// </summary>
    public void Close()
    {
        CancelInvoke(nameof(RefreshAll));
    }

    // =========================================================================
    // REFRESH — chiamato ogni 0.25s da InvokeRepeating
    // =========================================================================

    private void RefreshAll()
    {
        RefreshZone();
        RefreshNavState();
        RefreshFuel();
        RefreshSpeed();
        RefreshFTL();
        RefreshShields();
        RefreshDocking();
    }

    // ── Zona ─────────────────────────────────────────────────────────────

    private void RefreshZone()
    {
        var zm = ZoneManager.Instance;
        if (zm == null)
        {
            SetText(labelZoneType, "---");
            SetText(labelZoneEvent, "");
            return;
        }

        SetText(labelZoneType, ZoneTypeLabel(zm.CurrentZone));
        SetText(labelZoneEvent, ZoneEventLabel(zm.ActiveEvent));
    }

    // ── Navigazione ───────────────────────────────────────────────────────

    private void RefreshNavState()
    {
        var ftl = FTLDrive.Instance;
        var ps = PropulsionSystem.Instance;

        string navText = "---";
        Color navColor = Color.white;

        // Priorità: stato FTL sovrascrive la navigazione standard
        if (ftl != null && ftl.CurrentState == FTLState.Charging)
        {
            navText = "FTL — CARICA IN CORSO";
            navColor = colorFTL;
        }
        else if (ftl != null && ftl.CurrentState == FTLState.Jumping)
        {
            navText = "FTL — SALTO IN CORSO";
            navColor = Color.white;
        }
        else if (ps != null)
        {
            (navText, navColor) = ps.CurrentNavState switch
            {
                NavigationState.Anchored => ("ANCORATA", colorAnchored),
                NavigationState.Coasting => ("INERZIA", colorCoasting),
                NavigationState.Autopilot => ("AUTOPILOTA", colorAutopilot),
                NavigationState.Manual => ("MANUALE", colorManual),
                NavigationState.Docking => ("ATTRACCO IN CORSO", colorDocking),
                NavigationState.Docked => ("ATTRACCATA", colorDocked),
                _ => ("---", Color.white)
            };
        }

        if (labelNavState != null)
        {
            labelNavState.text = navText;
            labelNavState.color = navColor;
        }

        // Warning autopilota non disponibile (AsteroidField attivo)
        // Mostrato solo se l'autopilota NON è già in uso
        bool autopilotUnavailable = ps != null
            && !ps.AutopilotAvailable
            && ps.CurrentNavState != NavigationState.Autopilot;

        SetActive(panelAutopilotWarning, autopilotUnavailable);
    }

    // ── Propulsione / Fuel ────────────────────────────────────────────────

    private void RefreshFuel()
    {
        var inv = InventorySystem.Instance;
        int qty = inv != null ? inv.GetQuantity(ItemType.FuelCell) : 0;

        SetText(labelFuelCount, $"FUEL: {qty}");

        if (barFuel != null)
            barFuel.SetValue(maxFuelDisplay > 0 ? Mathf.Clamp01((float)qty / maxFuelDisplay) : 0f);
    }

    // ── Propulsione / Velocità (Rev T) ────────────────────────────────────

    /// <summary>
    /// Rev T — mostra velocità corrente, target (se diverso), e cap massimo
    /// data la degradazione. Colore adattivo:
    ///   stable        (|curr - target| < 0.5) → verde
    ///   accelerating  (curr < target)         → cyan
    ///   decelerating  (curr > target)         → ambra
    ///
    /// La label è unica per compattezza; il target è nascosto quando la nave
    /// è stabile per non affollare l'HUD durante la crociera.
    /// </summary>
    private void RefreshSpeed()
    {
        var ps = PropulsionSystem.Instance;

        if (labelSpeedCurrent == null && barSpeed == null) return; // niente da aggiornare

        if (ps == null)
        {
            SetText(labelSpeedCurrent, "VEL: ---");
            if (barSpeed != null) barSpeed.SetValue(0f);
            return;
        }

        float current = ps.CurrentSpeed;
        float target = ps.TargetSpeed;
        float maxCap = ps.MaxSpeedAtDegradation;
        float diff = target - current;

        // Testo: se stabile mostra solo current e max, altrimenti current → target
        string text;
        Color color;
        if (Mathf.Abs(diff) < 0.5f)
        {
            text = $"VEL: {current:F1}  (max {maxCap:F0})  m/s";
            color = colorSpeedStable;
        }
        else if (diff > 0f)
        {
            text = $"VEL: {current:F1} → {target:F1}  (max {maxCap:F0})  m/s";
            color = colorSpeedAccelerating;
        }
        else
        {
            text = $"VEL: {current:F1} ← {target:F1}  (max {maxCap:F0})  m/s";
            color = colorSpeedDecelerating;
        }

        if (labelSpeedCurrent != null)
        {
            labelSpeedCurrent.text = text;
            labelSpeedCurrent.color = color;
        }

        if (barSpeed != null)
        {
            float pct = maxCap > 0.01f ? Mathf.Clamp01(current / maxCap) : 0f;
            barSpeed.SetValue(pct);
        }
    }

    // ── FTL Drive ─────────────────────────────────────────────────────────

    private void RefreshFTL()
    {
        var ftl = FTLDrive.Instance;

        if (ftl == null)
        {
            SetText(labelFTLStatus, "FTL: N/A");
            SetActive(panelFTLCharge, false);
            SetActive(panelFTLTimer, false);
            return;
        }

        bool isCharging = ftl.CurrentState == FTLState.Charging;
        bool hasTimer = ftl.CurrentState == FTLState.Cooldown
                       || ftl.CurrentState == FTLState.Lockout;

        SetActive(panelFTLCharge, isCharging);
        SetActive(panelFTLTimer, hasTimer);

        if (isCharging && barFTLCharge != null)
            barFTLCharge.SetValue(ftl.ChargeProgress);

        if (hasTimer)
            SetText(labelFTLTimer, FormatTime(ftl.TimeRemaining));

        // Label stato + colore
        string ftlText = ftl.CurrentState switch
        {
            FTLState.Ready => "FTL: PRONTO",
            FTLState.Charging => $"FTL: CARICA  {ftl.ChargeProgress * 100f:F0}%",
            FTLState.Jumping => "FTL: SALTO IN CORSO",
            FTLState.Cooldown => "FTL: COOLDOWN",
            FTLState.Lockout => "FTL: LOCKOUT",
            _ => "FTL: ---"
        };

        Color ftlColor = ftl.CurrentState switch
        {
            FTLState.Ready => colorOn,
            FTLState.Charging => colorFTL,
            FTLState.Jumping => Color.white,
            FTLState.Cooldown => colorCoasting,
            FTLState.Lockout => colorWarning,
            _ => colorOff
        };

        if (labelFTLStatus != null)
        {
            labelFTLStatus.text = ftlText;
            labelFTLStatus.color = ftlColor;
        }
    }

    // ── Scudi ─────────────────────────────────────────────────────────────

    private void RefreshShields()
    {
        var sh = ShieldSystem.Instance;

        if (sh == null)
        {
            SetText(labelShieldStatus, "SCUDI: N/A");
            SetText(labelShieldHP, "");
            if (barShieldHP != null) barShieldHP.SetValue(0f);
            return;
        }

        (string stateText, Color stateColor) = sh.State switch
        {
            ShieldSystem.ShieldState.On => ("SCUDI: ATTIVI", colorOn),
            ShieldSystem.ShieldState.Charging => ("SCUDI: SPIN-UP", colorFTL),
            ShieldSystem.ShieldState.Off => ("SCUDI: INATTIVI", colorOff),
            _ => ("SCUDI: ---", Color.white)
        };

        if (labelShieldStatus != null)
        {
            labelShieldStatus.text = stateText;
            labelShieldStatus.color = stateColor;
        }

        bool hasHP = sh.State != ShieldSystem.ShieldState.Off;

        if (hasHP)
        {
            SetText(labelShieldHP,
                $"{sh.CurrentHP:F0} / {sh.MaxHP:F0} HP  ({sh.ShieldPercent * 100f:F0}%)");
            if (barShieldHP != null)
                barShieldHP.SetValue(sh.ShieldPercent);
        }
        else
        {
            SetText(labelShieldHP, "");
            if (barShieldHP != null)
                barShieldHP.SetValue(0f);
        }
    }

    // ── Docking / Anchor (Rev W — Blocco 3.1.6) ──────────────────────────

    /// <summary>
    /// Rev W — mostra due feedback complementari:
    ///
    /// 1. labelDockingStatus: "ATTRACCATA A: [displayName]" quando la nave è
    ///    fisicamente attraccata (NavigationState.Docked). Vuota altrimenti.
    ///    Non mostrata durante Docking (in progress): quella fase è già
    ///    coperta dal DockingMinigame_Canvas dedicato + labelNavState
    ///    ("ATTRACCO IN CORSO").
    ///
    /// 2. labelAnchorPrompt: prompt contestuale basato su AnchorabilityState
    ///    di AnchorSystem.
    ///    - Anchorable      → invito ad attraccare (verde)
    ///    - InRangeTooFast  → warning velocità (ambra)
    ///    - None            → label vuota
    ///    Il prompt viene soppresso se il pilota è già in Docking/Docked (non
    ///    ha più senso "premi T per iniziare" se sei già dentro).
    ///
    /// RISOLUZIONE POI displayName — client-side:
    ///   PoiRegistry è server-only. Sul client uso
    ///   NetworkManager.SpawnManager.SpawnedObjects[id]. Se la lookup fallisce
    ///   (race di spawn, id stale), mostro dockingStatusUnknownPoiName come
    ///   fallback invece di lasciare vuoto: rende visibile un eventuale bug
    ///   di sync in playtest.
    /// </summary>
    private void RefreshDocking()
    {
        var ps = PropulsionSystem.Instance;
        var anchor = AnchorSystem.Instance;

        // — labelDockingStatus: solo se Docked —
        if (labelDockingStatus != null)
        {
            if (ps != null && ps.CurrentNavState == NavigationState.Docked)
            {
                string poiName = ResolvePoiDisplayName(ps.AnchoredPoiId);
                labelDockingStatus.text = dockingStatusPrefix + poiName;
                labelDockingStatus.color = colorDocked;
            }
            else
            {
                labelDockingStatus.text = "";
            }
        }

        // — labelAnchorPrompt: solo se NON in Docking/Docked —
        if (labelAnchorPrompt != null)
        {
            bool inDockingPhase = ps != null
                && (ps.CurrentNavState == NavigationState.Docking
                    || ps.CurrentNavState == NavigationState.Docked);

            if (anchor == null || inDockingPhase)
            {
                labelAnchorPrompt.text = "";
                return;
            }

            switch (anchor.CurrentAnchorabilityState)
            {
                case AnchorabilityState.Anchorable:
                    labelAnchorPrompt.text = anchorPromptAnchorable;
                    labelAnchorPrompt.color = colorAnchorPromptOk;
                    break;

                case AnchorabilityState.InRangeTooFast:
                    labelAnchorPrompt.text = anchorPromptTooFast;
                    labelAnchorPrompt.color = colorAnchorPromptTooFast;
                    break;

                case AnchorabilityState.None:
                default:
                    labelAnchorPrompt.text = "";
                    break;
            }
        }
    }

    /// <summary>
    /// Risolve il displayName di un POI da NetworkObjectId sul client.
    /// PoiRegistry è server-only quindi qui uso NetworkManager.SpawnManager.
    /// Ritorna dockingStatusUnknownPoiName se non risolvibile.
    /// </summary>
    private string ResolvePoiDisplayName(ulong poiNetworkObjectId)
    {
        if (poiNetworkObjectId == 0ul) return dockingStatusUnknownPoiName;

        var nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null) return dockingStatusUnknownPoiName;

        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(poiNetworkObjectId, out var netObj)
            || netObj == null)
        {
            return dockingStatusUnknownPoiName;
        }

        var poi = netObj.GetComponent<PoiInstance>();
        if (poi == null || poi.Data == null) return dockingStatusUnknownPoiName;

        string name = poi.Data.DisplayName;
        return string.IsNullOrEmpty(name) ? dockingStatusUnknownPoiName : name;
    }

    // =========================================================================
    // HELPER
    // =========================================================================

    /// <summary>Converte secondi in formato MM:SS per display FTL cooldown/lockout.</summary>
    private static string FormatTime(float totalSeconds)
    {
        int m = Mathf.FloorToInt(totalSeconds / 60f);
        int s = Mathf.FloorToInt(totalSeconds % 60f);
        return $"{m:D2}:{s:D2}";
    }

    private static void SetText(TextMeshProUGUI label, string text)
    {
        if (label != null) label.text = text;
    }

    private static void SetActive(GameObject go, bool active)
    {
        if (go != null && go.activeSelf != active) go.SetActive(active);
    }

    // ── Etichette zona ────────────────────────────────────────────────────

    private static string ZoneTypeLabel(ZoneType zone) => zone switch
    {
        ZoneType.Inner => "SISTEMA INTERNO",
        ZoneType.Frontier => "FRONTIERA",
        ZoneType.DeepVoid => "VUOTO PROFONDO",
        _ => "---"
    };

    private static string ZoneEventLabel(ZoneEvent evt) => evt switch
    {
        ZoneEvent.None => "— ROTTA LIBERA —",
        ZoneEvent.RadiationStorm => "☢  TEMPESTA RADIAZIONI",
        ZoneEvent.AsteroidField => "☄  CAMPO DI METEORITI",
        ZoneEvent.SolarStorm => "☀  TEMPESTA SOLARE",
        ZoneEvent.EMAnomaly => "⚡  ANOMALIA EM",
        _ => ""
    };
}