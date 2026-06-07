using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Ship Systems Dashboard UI — Monitor 2, Engineering Station.
/// Milestone 2 — Sezione A: stato subsystem con dati reali.
///
/// PATCH HullSystem:
///   - HullSection collegata a HullSystem.OnHullChanged (evento, no polling)
///   - hullBar e hullStatusBadge aggiornati in tempo reale
///   - Si sottoscrive a HullSystem.OnInstanceReady se non ancora pronto
///
/// ⚠️  SEZIONE B (Repair): dipende da RepairSystem + InventorySystem (M2)
/// ⚠️  SEZIONE C (Diagnostica Elettrica): collegabile in M2 step successivo.
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

    // ── Hull (dati reali da HullSystem) ──────────────────────────────────────

    [Header("Hull (HullSystem M2)")]
    [SerializeField] private TextMeshProUGUI hullStatusBadge;
    [SerializeField] private SciFiSegmentedBar hullBar;
    [SerializeField] private TextMeshProUGUI hullLevelText;

    // ── Stub sections ────────────────────────────────────────────────────────

    [Header("Shields (stub — dipende da: ShieldSystem M2)")]
    [SerializeField] private TextMeshProUGUI shieldsStatusBadge;
    [SerializeField] private SciFiSegmentedBar shieldsBar;

    [Header("Propulsion (stub — dipende da: PropulsionSystem M2)")]
    [SerializeField] private TextMeshProUGUI propulsionStatusBadge;

    [Header("FTL (stub — dipende da: FTLSystem M2)")]
    [SerializeField] private TextMeshProUGUI ftlStatusBadge;

    [Header("Reactor (dati PowerManager)")]
    [SerializeField] private TextMeshProUGUI reactorStatusBadge;
    [SerializeField] private SciFiSegmentedBar reactorBar;

    // ── Colori stato ──────────────────────────────────────────────────────────

    [Header("Status Colors")]
    [SerializeField] private Color colorOnline = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorDegraded = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);
    [SerializeField] private Color colorOffline = new Color(0.5f, 0.5f, 0.5f);

    // ── Stato interno ─────────────────────────────────────────────────────────

    private SpaceSurvivor.Ship.OxygenSystem oxygenSystem;
    private SpaceSurvivor.Ship.HullSystem hullSystem;
    private SpaceSurvivor.Ship.ShieldSystem shieldSystem;
    private PowerManager powerManager;

    // Cache hull per aggiornamento badge senza polling
    private float cachedHullPercent = 1f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        // OxygenSystem
        if (SpaceSurvivor.Ship.OxygenSystem.Instance != null)
            InitWithOxygenSystem();
        else
            SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady += InitWithOxygenSystem;

        // HullSystem
        if (SpaceSurvivor.Ship.HullSystem.Instance != null)
            InitWithHullSystem();
        else
            SpaceSurvivor.Ship.HullSystem.OnInstanceReady += InitWithHullSystem;

        // ShieldSystem
        if (SpaceSurvivor.Ship.ShieldSystem.Instance != null)
            InitWithShieldSystem();
        else
            SpaceSurvivor.Ship.ShieldSystem.OnInstanceReady += InitWithShieldSystem;

        // PowerManager
        if (PowerManager.Instance != null)
            InitWithPowerManager();
        else
            PowerManager.OnInstanceReady += InitWithPowerManager;

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

        // Sottoscrivi all'evento: aggiorna cache e badge immediatamente
        hullSystem.OnHullChanged += HandleHullChanged;

        // Aggiorna subito con i valori attuali
        HandleHullChanged(hullSystem.CurrentHP, hullSystem.MaxHP, hullSystem.HullPercent);
    }

    private void InitWithShieldSystem()
    {
        SpaceSurvivor.Ship.ShieldSystem.OnInstanceReady -= InitWithShieldSystem;
        shieldSystem = SpaceSurvivor.Ship.ShieldSystem.Instance;

        shieldSystem.OnShieldHPChanged += HandleShieldHPChanged;
        shieldSystem.OnStateChanged += HandleShieldStateChanged;

        // Aggiorna subito
        HandleShieldHPChanged(shieldSystem.CurrentHP, shieldSystem.MaxHP, shieldSystem.ShieldPercent);
        HandleShieldStateChanged(shieldSystem.State);
    }

    private void InitWithPowerManager()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        powerManager = PowerManager.Instance;
    }

    private void OnDestroy()
    {
        SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady -= InitWithOxygenSystem;
        SpaceSurvivor.Ship.HullSystem.OnInstanceReady -= InitWithHullSystem;
        SpaceSurvivor.Ship.ShieldSystem.OnInstanceReady -= InitWithShieldSystem;
        PowerManager.OnInstanceReady -= InitWithPowerManager;

        if (hullSystem != null)
            hullSystem.OnHullChanged -= HandleHullChanged;

        if (shieldSystem != null)
        {
            shieldSystem.OnShieldHPChanged -= HandleShieldHPChanged;
            shieldSystem.OnStateChanged -= HandleShieldStateChanged;
        }

        CancelInvoke(nameof(UpdateUI));
    }

    // ── Hull event handler (fired da HullSystem su tutti i client) ────────────

    private void HandleHullChanged(float currentHP, float maxHP, float percent)
    {
        cachedHullPercent = percent;

        if (hullBar != null)
            hullBar.SetValue(percent);

        if (hullLevelText != null)
            hullLevelText.text = $"{currentHP:F0} / {maxHP:F0} HP";

        UpdateHullBadge(percent);
    }

    private void UpdateHullBadge(float percent)
    {
        if (hullStatusBadge == null) return;

        if (percent <= 0f)
        {
            SetBadge(hullStatusBadge, "DESTROYED", colorCritical);
        }
        else if (percent < 0.20f)
        {
            SetBadge(hullStatusBadge, "CRITICAL", colorCritical);
        }
        else if (percent < 0.50f)
        {
            SetBadge(hullStatusBadge, "DAMAGED", colorDegraded);
        }
        else
        {
            SetBadge(hullStatusBadge, "INTACT", colorOnline);
        }
    }

    // ── Open / Close ──────────────────────────────────────────────────────────

    public void Open()
    {
        // Fallback se lo spawn è avvenuto dopo Start()
        if (oxygenSystem == null && SpaceSurvivor.Ship.OxygenSystem.Instance != null)
            oxygenSystem = SpaceSurvivor.Ship.OxygenSystem.Instance;

        if (hullSystem == null && SpaceSurvivor.Ship.HullSystem.Instance != null)
            InitWithHullSystem();

        if (shieldSystem == null && SpaceSurvivor.Ship.ShieldSystem.Instance != null)
            InitWithShieldSystem();

        if (powerManager == null && PowerManager.Instance != null)
            powerManager = PowerManager.Instance;

        UpdateUI();
        InvokeRepeating(nameof(UpdateUI), 0f, 0.2f);
    }

    public void Close()
    {
        CancelInvoke(nameof(UpdateUI));
    }

    // ── Aggiornamento UI (polling per O2 e Reactor) ───────────────────────────

    private void UpdateUI()
    {
        UpdateO2Section();
        UpdateReactorSection();
        // Hull aggiornato via evento — nessun polling necessario
    }

    // ── O₂ Section ────────────────────────────────────────────────────────────

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

    // ── Reactor Section ───────────────────────────────────────────────────────

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

    // ── Shield event handlers ─────────────────────────────────────────────────

    private void HandleShieldHPChanged(float currentHP, float maxHP, float percent)
    {
        if (shieldsBar != null)
            shieldsBar.SetValue(percent);
    }

    private void HandleShieldStateChanged(SpaceSurvivor.Ship.ShieldSystem.ShieldState state)
    {
        if (shieldsStatusBadge == null) return;

        switch (state)
        {
            case SpaceSurvivor.Ship.ShieldSystem.ShieldState.On:
                SetBadge(shieldsStatusBadge, "ONLINE", colorOnline);
                break;
            case SpaceSurvivor.Ship.ShieldSystem.ShieldState.Charging:
                SetBadge(shieldsStatusBadge, "CHARGING", colorDegraded);
                break;
            case SpaceSurvivor.Ship.ShieldSystem.ShieldState.Off:
                SetBadge(shieldsStatusBadge, "OFFLINE", colorOffline);
                break;
        }
    }

    // ── Stub badges ───────────────────────────────────────────────────────────

    private void SetStubBadges()
    {
        // Shields gestiti da eventi — solo init se ShieldSystem non è ancora pronto
        if (shieldSystem == null)
        {
            SetBadge(shieldsStatusBadge, "OFFLINE", colorOffline);
            if (shieldsBar != null) shieldsBar.SetValue(0f);
        }

        SetBadge(propulsionStatusBadge, "ONLINE", colorOnline);
        SetBadge(ftlStatusBadge, "ONLINE", colorOnline);
    }

    private void SetBadge(TextMeshProUGUI badge, string text, Color color)
    {
        if (badge == null) return;
        badge.text = text;
        badge.color = color;
    }
}