using TMPro;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;
using UnityEngine;

/// <summary>
/// PilotHUD — Milestone 2
/// Dashboard cockpit del Pilota. Canvas World Space, display-only.
///
/// SEZIONI:
///   Zona       — tipo zona + evento attivo (icona emoji + label)
///   Navigazione — stato corrente (colore + label) + warning autopilota
///   Propulsione — fuel cells (contatore + SciFiSegmentedBar)
///   FTL        — stato + barra carica (Charging) + timer MM:SS (Cooldown/Lockout)
///   Scudi      — stato (Off/SpinUp/On) + HP + SciFiSegmentedBar
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
    [Tooltip("Label stato navigazione: ANCORATA / INERZIA / AUTOPILOTA / MANUALE / FTL…")]
    [SerializeField] private TextMeshProUGUI labelNavState;

    [Tooltip("Panel attivato quando l'autopilota NON è disponibile (AsteroidField)")]
    [SerializeField] private GameObject panelAutopilotWarning;

    // =========================================================================
    // SEZIONE PROPULSIONE — FUEL
    // =========================================================================

    [Header("— Propulsione —")]
    [Tooltip("Contatore fuel cells: 'FUEL: 18'")]
    [SerializeField] private TextMeshProUGUI labelFuelCount;

    [Tooltip("Barra segmentata fuel. SetValue() con valore 0–1")]
    [SerializeField] private SciFiSegmentedBar barFuel;

    [Tooltip("Massimo fuel cells per la barra (default 30). Non hardcoded nel GDD → configurabile")]
    [SerializeField] private int maxFuelDisplay = 30;

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
    // COLORI DI STATO
    // =========================================================================

    [Header("— Colori —")]
    [SerializeField] private Color colorAnchored  = new Color(0.50f, 0.50f, 0.50f);
    [SerializeField] private Color colorCoasting  = new Color(0.40f, 0.80f, 1.00f);
    [SerializeField] private Color colorAutopilot = new Color(0.20f, 1.00f, 0.40f);
    [SerializeField] private Color colorManual    = new Color(1.00f, 0.90f, 0.20f);
    [SerializeField] private Color colorFTL       = new Color(0.60f, 0.20f, 1.00f);
    [SerializeField] private Color colorWarning   = new Color(1.00f, 0.50f, 0.00f);
    [SerializeField] private Color colorOn        = new Color(0.20f, 1.00f, 0.50f);
    [SerializeField] private Color colorOff       = new Color(0.40f, 0.40f, 0.40f);

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
        RefreshFTL();
        RefreshShields();
    }

    // ── Zona ─────────────────────────────────────────────────────────────

    private void RefreshZone()
    {
        var zm = ZoneManager.Instance;
        if (zm == null)
        {
            SetText(labelZoneType,  "---");
            SetText(labelZoneEvent, "");
            return;
        }

        SetText(labelZoneType,  ZoneTypeLabel(zm.CurrentZone));
        SetText(labelZoneEvent, ZoneEventLabel(zm.ActiveEvent));
    }

    // ── Navigazione ───────────────────────────────────────────────────────

    private void RefreshNavState()
    {
        var ftl = FTLDrive.Instance;
        var ps  = PropulsionSystem.Instance;

        string navText  = "---";
        Color  navColor = Color.white;

        // Priorità: stato FTL sovrascrive la navigazione standard
        if (ftl != null && ftl.CurrentState == FTLState.Charging)
        {
            navText  = "FTL — CARICA IN CORSO";
            navColor = colorFTL;
        }
        else if (ftl != null && ftl.CurrentState == FTLState.Jumping)
        {
            navText  = "FTL — SALTO IN CORSO";
            navColor = Color.white;
        }
        else if (ps != null)
        {
            (navText, navColor) = ps.CurrentNavState switch
            {
                NavigationState.Anchored  => ("ANCORATA",  colorAnchored),
                NavigationState.Coasting  => ("INERZIA",   colorCoasting),
                NavigationState.Autopilot => ("AUTOPILOTA", colorAutopilot),
                NavigationState.Manual    => ("MANUALE",   colorManual),
                _                        => ("---", Color.white)
            };
        }

        if (labelNavState != null)
        {
            labelNavState.text  = navText;
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
        int qty  = inv != null ? inv.GetQuantity(ItemType.FuelCell) : 0;

        SetText(labelFuelCount, $"FUEL: {qty}");

        if (barFuel != null)
            barFuel.SetValue(maxFuelDisplay > 0 ? Mathf.Clamp01((float)qty / maxFuelDisplay) : 0f);
    }

    // ── FTL Drive ─────────────────────────────────────────────────────────

    private void RefreshFTL()
    {
        var ftl = FTLDrive.Instance;

        if (ftl == null)
        {
            SetText(labelFTLStatus, "FTL: N/A");
            SetActive(panelFTLCharge, false);
            SetActive(panelFTLTimer,  false);
            return;
        }

        bool isCharging = ftl.CurrentState == FTLState.Charging;
        bool hasTimer   = ftl.CurrentState == FTLState.Cooldown
                       || ftl.CurrentState == FTLState.Lockout;

        SetActive(panelFTLCharge, isCharging);
        SetActive(panelFTLTimer,  hasTimer);

        if (isCharging && barFTLCharge != null)
            barFTLCharge.SetValue(ftl.ChargeProgress);

        if (hasTimer)
            SetText(labelFTLTimer, FormatTime(ftl.TimeRemaining));

        // Label stato + colore
        string ftlText  = ftl.CurrentState switch
        {
            FTLState.Ready    => "FTL: PRONTO",
            FTLState.Charging => $"FTL: CARICA  {ftl.ChargeProgress * 100f:F0}%",
            FTLState.Jumping  => "FTL: SALTO IN CORSO",
            FTLState.Cooldown => "FTL: COOLDOWN",
            FTLState.Lockout  => "FTL: LOCKOUT",
            _                 => "FTL: ---"
        };

        Color ftlColor = ftl.CurrentState switch
        {
            FTLState.Ready    => colorOn,
            FTLState.Charging => colorFTL,
            FTLState.Jumping  => Color.white,
            FTLState.Cooldown => colorCoasting,
            FTLState.Lockout  => colorWarning,
            _                 => colorOff
        };

        if (labelFTLStatus != null)
        {
            labelFTLStatus.text  = ftlText;
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
            SetText(labelShieldHP,     "");
            if (barShieldHP != null) barShieldHP.SetValue(0f);
            return;
        }

        (string stateText, Color stateColor) = sh.State switch
        {
            ShieldSystem.ShieldState.On       => ("SCUDI: ATTIVI",   colorOn),
            ShieldSystem.ShieldState.Charging => ("SCUDI: SPIN-UP",  colorFTL),
            ShieldSystem.ShieldState.Off      => ("SCUDI: INATTIVI", colorOff),
            _                                 => ("SCUDI: ---",      Color.white)
        };

        if (labelShieldStatus != null)
        {
            labelShieldStatus.text  = stateText;
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
        ZoneType.Inner    => "SISTEMA INTERNO",
        ZoneType.Frontier => "FRONTIERA",
        ZoneType.DeepVoid => "VUOTO PROFONDO",
        _                 => "---"
    };

    private static string ZoneEventLabel(ZoneEvent evt) => evt switch
    {
        ZoneEvent.None           => "— ROTTA LIBERA —",
        ZoneEvent.RadiationStorm => "☢  TEMPESTA RADIAZIONI",
        ZoneEvent.AsteroidField  => "☄  CAMPO DI METEORITI",
        ZoneEvent.SolarStorm     => "☀  TEMPESTA SOLARE",
        ZoneEvent.EMAnomaly      => "⚡  ANOMALIA EM",
        _                        => ""
    };
}
