using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Ship Systems Dashboard UI — Monitor 2, Engineering Station.
/// Milestone 2.
///
/// Sezione A — Subsystem status: O2, Reactor, Hull, Shields (dati reali)
///             Propulsion, FTL (stub)
/// Sezione B — Repair: dipende da RepairSystem + InventorySystem (M2)
/// Sezione C — Diagnostica Elettrica: dati reali ElectricalDegradationManager
///             Hull ×, EM ×, Ballast stato/×, Totale ×
///             Aggiornata via evento OnDegradationChanged (no polling)
/// </summary>
public class ShipSystemsDashboardUI : MonoBehaviour, IDashboardPanel
{
    // ── O₂ / Life Support ────────────────────────────────────────────────────

    [Header("O2 / Life Support")]
    [SerializeField] private TextMeshProUGUI o2StatusBadge;
    [SerializeField] private SciFiSegmentedBar o2Bar;
    [SerializeField] private TextMeshProUGUI o2LevelText;
    [SerializeField] private TextMeshProUGUI o2RateText;
    [SerializeField] private TextMeshProUGUI o2AutonText;

    // ── Hull ─────────────────────────────────────────────────────────────────

    [Header("Hull (HullSystem M2)")]
    [SerializeField] private TextMeshProUGUI hullStatusBadge;
    [SerializeField] private SciFiSegmentedBar hullBar;
    [SerializeField] private TextMeshProUGUI hullLevelText;

    // ── Shields ───────────────────────────────────────────────────────────────

    [Header("Shields (ShieldSystem M2)")]
    [SerializeField] private TextMeshProUGUI shieldsStatusBadge;
    [SerializeField] private SciFiSegmentedBar shieldsBar;

    // ── Stub ──────────────────────────────────────────────────────────────────

    [Header("Propulsion (stub)")]
    [SerializeField] private TextMeshProUGUI propulsionStatusBadge;

    [Header("FTL (stub)")]
    [SerializeField] private TextMeshProUGUI ftlStatusBadge;

    // ── Reactor ───────────────────────────────────────────────────────────────

    [Header("Reactor (PowerManager)")]
    [SerializeField] private TextMeshProUGUI reactorStatusBadge;
    [SerializeField] private SciFiSegmentedBar reactorBar;

    // ── Sezione C — Diagnostica Elettrica ─────────────────────────────────────

    [Header("Sezione C — Diagnostica Elettrica (ElectricalDegradationManager)")]
    [Tooltip("Riga moltiplicatore hull: es. 'HULL  ×1.15'")]
    [SerializeField] private TextMeshProUGUI diagHullText;

    [Tooltip("Riga moltiplicatore EM: es. 'EM  ×1.00'")]
    [SerializeField] private TextMeshProUGUI diagEMText;

    [Tooltip("Riga stato ballast: es. 'BALLAST  DEGRADED [Lieve] ×1.12'")]
    [SerializeField] private TextMeshProUGUI diagBallastText;

    [Tooltip("Badge stato ballast: OK / LIEVE / MEDIO / AVANZATO")]
    [SerializeField] private TextMeshProUGUI diagBallastBadge;

    [Tooltip("Riga moltiplicatore totale: es. 'TOTALE  ×1.30'")]
    [SerializeField] private TextMeshProUGUI diagTotalText;

    [Tooltip("Badge moltiplicatore totale — cambia colore sopra soglie.")]
    [SerializeField] private TextMeshProUGUI diagTotalBadge;

    // ── Soglie colore diagnostica ─────────────────────────────────────────────

    [Header("Degradation Thresholds")]
    [Tooltip("Sopra questa soglia totale il colore diventa arancio.")]
    [SerializeField] private float diagWarningThreshold = 1.20f;
    [Tooltip("Sopra questa soglia totale il colore diventa rosso.")]
    [SerializeField] private float diagCriticalThreshold = 1.50f;

    // ── Colori stato ──────────────────────────────────────────────────────────

    [Header("Status Colors")]
    [SerializeField] private Color colorOnline = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorDegraded = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);
    [SerializeField] private Color colorOffline = new Color(0.5f, 0.5f, 0.5f);

    // ── Riferimenti sistemi ───────────────────────────────────────────────────

    private SpaceSurvivor.Ship.OxygenSystem oxygenSystem;
    private SpaceSurvivor.Ship.HullSystem hullSystem;
    private SpaceSurvivor.Ship.ShieldSystem shieldSystem;
    private PowerManager powerManager;
    private ElectricalDegradationManager degradationManager;

    private float cachedHullPercent = 1f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        if (SpaceSurvivor.Ship.OxygenSystem.Instance != null)
            InitWithOxygenSystem();
        else
            SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady += InitWithOxygenSystem;

        if (SpaceSurvivor.Ship.HullSystem.Instance != null)
            InitWithHullSystem();
        else
            SpaceSurvivor.Ship.HullSystem.OnInstanceReady += InitWithHullSystem;

        if (SpaceSurvivor.Ship.ShieldSystem.Instance != null)
            InitWithShieldSystem();
        else
            SpaceSurvivor.Ship.ShieldSystem.OnInstanceReady += InitWithShieldSystem;

        if (PowerManager.Instance != null)
            InitWithPowerManager();
        else
            PowerManager.OnInstanceReady += InitWithPowerManager;

        if (ElectricalDegradationManager.Instance != null)
            InitWithDegradationManager();
        else
            ElectricalDegradationManager.OnInstanceReady += InitWithDegradationManager;

        SetStubBadges();
    }

    private void InitWithOxygenSystem()
    {
        SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady -= InitWithOxygenSystem;
        oxygenSystem = SpaceSurvivor.Ship.OxygenSystem.Instance;
    }

    private void InitWithHullSystem()
    {
        SpaceSurvivor.Ship.HullSystem.OnInstanceReady -= InitWithHullSystem;
        hullSystem = SpaceSurvivor.Ship.HullSystem.Instance;
        hullSystem.OnHullChanged += HandleHullChanged;
        HandleHullChanged(hullSystem.CurrentHP, hullSystem.MaxHP, hullSystem.HullPercent);
    }

    private void InitWithShieldSystem()
    {
        SpaceSurvivor.Ship.ShieldSystem.OnInstanceReady -= InitWithShieldSystem;
        shieldSystem = SpaceSurvivor.Ship.ShieldSystem.Instance;
        shieldSystem.OnShieldHPChanged += HandleShieldHPChanged;
        shieldSystem.OnStateChanged += HandleShieldStateChanged;
        HandleShieldHPChanged(shieldSystem.CurrentHP, shieldSystem.MaxHP, shieldSystem.ShieldPercent);
        HandleShieldStateChanged(shieldSystem.State);
    }

    private void InitWithPowerManager()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        powerManager = PowerManager.Instance;
    }

    private void InitWithDegradationManager()
    {
        ElectricalDegradationManager.OnInstanceReady -= InitWithDegradationManager;
        degradationManager = ElectricalDegradationManager.Instance;

        // Aggiornamento via evento — nessun polling per la diagnostica
        degradationManager.OnDegradationChanged += HandleDegradationChanged;

        // Aggiorna subito con i valori attuali
        UpdateDegradationSection();
    }

    private void OnDestroy()
    {
        SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady -= InitWithOxygenSystem;
        SpaceSurvivor.Ship.HullSystem.OnInstanceReady -= InitWithHullSystem;
        SpaceSurvivor.Ship.ShieldSystem.OnInstanceReady -= InitWithShieldSystem;
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        ElectricalDegradationManager.OnInstanceReady -= InitWithDegradationManager;

        if (hullSystem != null)
            hullSystem.OnHullChanged -= HandleHullChanged;

        if (shieldSystem != null)
        {
            shieldSystem.OnShieldHPChanged -= HandleShieldHPChanged;
            shieldSystem.OnStateChanged -= HandleShieldStateChanged;
        }

        if (degradationManager != null)
            degradationManager.OnDegradationChanged -= HandleDegradationChanged;

        CancelInvoke(nameof(UpdateUI));
    }

    // ── Open / Close ──────────────────────────────────────────────────────────

    public void Open()
    {
        if (oxygenSystem == null && SpaceSurvivor.Ship.OxygenSystem.Instance != null)
            oxygenSystem = SpaceSurvivor.Ship.OxygenSystem.Instance;

        if (hullSystem == null && SpaceSurvivor.Ship.HullSystem.Instance != null)
            InitWithHullSystem();

        if (shieldSystem == null && SpaceSurvivor.Ship.ShieldSystem.Instance != null)
            InitWithShieldSystem();

        if (powerManager == null && PowerManager.Instance != null)
            powerManager = PowerManager.Instance;

        if (degradationManager == null && ElectricalDegradationManager.Instance != null)
            InitWithDegradationManager();

        UpdateUI();
        InvokeRepeating(nameof(UpdateUI), 0f, 0.2f);
    }

    public void Close()
    {
        CancelInvoke(nameof(UpdateUI));
    }

    // ── Update (polling per O2 e Reactor, tutto il resto via eventi) ──────────

    private void UpdateUI()
    {
        UpdateO2Section();
        UpdateReactorSection();
        // Hull, Shields, Degradation → aggiornati via evento
    }

    // ── O₂ ───────────────────────────────────────────────────────────────────

    private void UpdateO2Section()
    {
        if (oxygenSystem == null) return;

        float level = oxygenSystem.O2Level;
        float percent = oxygenSystem.O2Percentage;
        float netRate = oxygenSystem.NetRatePerMinute;

        if (o2Bar != null) o2Bar.SetValue(percent);
        if (o2LevelText != null) o2LevelText.text = $"{level:F1}%";

        if (o2RateText != null)
        {
            string sign = netRate >= 0f ? "+" : "";
            o2RateText.text = $"{sign}{netRate:F1} / min";
            o2RateText.color = netRate >= 0f ? colorOnline : colorCritical;
        }

        if (o2AutonText != null)
            o2AutonText.text = ComputeAutonomy(level, netRate);

        if (o2StatusBadge != null)
            SetO2Badge(level, oxygenSystem.IsAlarmActive);
    }

    private void SetO2Badge(float level, bool alarmActive)
    {
        if (alarmActive || level < 20f)
            SetBadge(o2StatusBadge, "CRITICAL", colorCritical);
        else if (level < 50f)
            SetBadge(o2StatusBadge, "WARNING", colorDegraded);
        else if (oxygenSystem.GenerationRatePerMinute <= 0f)
            SetBadge(o2StatusBadge, "OFFLINE", colorOffline);
        else
            SetBadge(o2StatusBadge, "ONLINE", colorOnline);
    }

    private string ComputeAutonomy(float currentLevel, float netRatePerMinute)
    {
        if (netRatePerMinute >= 0f) return "Autonomia: ∞";
        float minutes = currentLevel / Mathf.Abs(netRatePerMinute);
        if (minutes > 999f) return "Autonomia: ∞";
        int mins = Mathf.FloorToInt(minutes);
        int secs = Mathf.FloorToInt((minutes - mins) * 60f);
        return $"Autonomia: {mins:D2}:{secs:D2}";
    }

    // ── Reactor ───────────────────────────────────────────────────────────────

    private void UpdateReactorSection()
    {
        if (powerManager == null) return;

        if (reactorBar != null)
            reactorBar.SetValue(powerManager.PowerPercentage);

        if (reactorStatusBadge != null)
        {
            if (powerManager.IsInBlackout)
                SetBadge(reactorStatusBadge, "BLACKOUT", colorCritical);
            else if (powerManager.IsInCriticalState)
                SetBadge(reactorStatusBadge, "CRITICAL", colorCritical);
            else
                SetBadge(reactorStatusBadge, "ONLINE", colorOnline);
        }
    }

    // ── Hull ─────────────────────────────────────────────────────────────────

    private void HandleHullChanged(float currentHP, float maxHP, float percent)
    {
        cachedHullPercent = percent;
        if (hullBar != null) hullBar.SetValue(percent);
        if (hullLevelText != null) hullLevelText.text = $"{currentHP:F0} / {maxHP:F0} HP";
        UpdateHullBadge(percent);
    }

    private void UpdateHullBadge(float percent)
    {
        if (hullStatusBadge == null) return;
        if (percent <= 0f) SetBadge(hullStatusBadge, "DESTROYED", colorCritical);
        else if (percent < 0.20f) SetBadge(hullStatusBadge, "CRITICAL", colorCritical);
        else if (percent < 0.50f) SetBadge(hullStatusBadge, "DAMAGED", colorDegraded);
        else SetBadge(hullStatusBadge, "INTACT", colorOnline);
    }

    // ── Shields ───────────────────────────────────────────────────────────────

    private void HandleShieldHPChanged(float currentHP, float maxHP, float percent)
    {
        if (shieldsBar != null) shieldsBar.SetValue(percent);
    }

    private void HandleShieldStateChanged(SpaceSurvivor.Ship.ShieldSystem.ShieldState state)
    {
        if (shieldsStatusBadge == null) return;
        switch (state)
        {
            case SpaceSurvivor.Ship.ShieldSystem.ShieldState.On:
                SetBadge(shieldsStatusBadge, "ONLINE", colorOnline); break;
            case SpaceSurvivor.Ship.ShieldSystem.ShieldState.Charging:
                SetBadge(shieldsStatusBadge, "CHARGING", colorDegraded); break;
            case SpaceSurvivor.Ship.ShieldSystem.ShieldState.Off:
                SetBadge(shieldsStatusBadge, "OFFLINE", colorOffline); break;
        }
    }

    // ── Sezione C — Diagnostica Elettrica ─────────────────────────────────────

    private void HandleDegradationChanged(float totalMultiplier)
    {
        UpdateDegradationSection();
    }

    private void UpdateDegradationSection()
    {
        if (degradationManager == null) return;

        float hull = degradationManager.HullMultiplier;
        float em = degradationManager.EMMultiplier;
        float ballast = degradationManager.BallastMultiplier;
        float total = degradationManager.GetTotalMultiplier();
        bool faulted = degradationManager.IsBallastDamaged;
        ElectricalDegradationManager.BallastState bState = degradationManager.CurrentBallastState;

        // Hull multiplier
        if (diagHullText != null)
        {
            diagHullText.text = $"HULL  ×{hull:0.00}";
            diagHullText.color = MultiplierColor(hull);
        }

        // EM multiplier
        if (diagEMText != null)
        {
            diagEMText.text = $"EM  ×{em:0.00}";
            diagEMText.color = MultiplierColor(em);
        }

        // Ballast testo
        if (diagBallastText != null)
        {
            diagBallastText.text = faulted
                ? $"BALLAST  ×{ballast:0.00}"
                : $"BALLAST  ×{ballast:0.00}";
            diagBallastText.color = MultiplierColor(ballast);
        }

        // Ballast badge
        if (diagBallastBadge != null)
        {
            switch (bState)
            {
                case ElectricalDegradationManager.BallastState.Integro:
                    SetBadge(diagBallastBadge, "OK", colorOnline); break;
                case ElectricalDegradationManager.BallastState.Lieve:
                    SetBadge(diagBallastBadge, "LIEVE", colorDegraded); break;
                case ElectricalDegradationManager.BallastState.Medio:
                    SetBadge(diagBallastBadge, "MEDIO", colorCritical); break;
                case ElectricalDegradationManager.BallastState.Avanzato:
                    SetBadge(diagBallastBadge, "AVANZATO", colorCritical); break;
            }
        }

        // Totale testo
        if (diagTotalText != null)
        {
            diagTotalText.text = $"TOTALE  ×{total:0.00}";
            diagTotalText.color = MultiplierColor(total);
        }

        // Totale badge
        if (diagTotalBadge != null)
        {
            if (total >= diagCriticalThreshold)
                SetBadge(diagTotalBadge, "CRITICO", colorCritical);
            else if (total >= diagWarningThreshold)
                SetBadge(diagTotalBadge, "DEGRADATO", colorDegraded);
            else
                SetBadge(diagTotalBadge, "NORMALE", colorOnline);
        }
    }

    // Soglie per singolo moltiplicatore (più basse di quelle del totale)
    private static readonly float MULT_WARNING = 1.10f; // ×1.10 → arancio
    private static readonly float MULT_CRITICAL = 1.30f; // ×1.30 → rosso

    private Color MultiplierColor(float multiplier)
    {
        if (multiplier >= MULT_CRITICAL) return colorCritical;
        if (multiplier >= MULT_WARNING) return colorDegraded;
        return colorOnline;
    }

    // ── Stub badges ───────────────────────────────────────────────────────────

    private void SetStubBadges()
    {
        if (shieldSystem == null)
        {
            SetBadge(shieldsStatusBadge, "OFFLINE", colorOffline);
            if (shieldsBar != null) shieldsBar.SetValue(0f);
        }

        SetBadge(propulsionStatusBadge, "ONLINE", colorOnline);
        SetBadge(ftlStatusBadge, "ONLINE", colorOnline);

        // Diagnostica — mostra valori neutri finché il manager non è pronto
        if (degradationManager == null)
        {
            SetText(diagHullText, "HULL  ×1.00", colorOnline);
            SetText(diagEMText, "EM  ×1.00", colorOnline);
            SetText(diagBallastText, "BALLAST  ×1.00", colorOnline);
            SetText(diagTotalText, "TOTALE  ×1.00", colorOnline);
            SetBadge(diagBallastBadge, "OK", colorOnline);
            SetBadge(diagTotalBadge, "NORMALE", colorOnline);
        }
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private void SetBadge(TextMeshProUGUI badge, string text, Color color)
    {
        if (badge == null) return;
        badge.text = text;
        badge.color = color;
    }

    private void SetText(TextMeshProUGUI label, string text, Color color)
    {
        if (label == null) return;
        label.text = text;
        label.color = color;
    }
}