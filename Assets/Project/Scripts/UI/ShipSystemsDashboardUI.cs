using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Ship Systems Dashboard UI — Monitor 2, Engineering Station.
/// Milestone 2 — Sezione A: stato subsystem con dati reali.
///
/// LAYOUT ATTESO (costruito nell'Inspector):
///   Canvas (CanvasGroup, gestito da MonitorSwitcher)
///   └── Panel
///       ├── Header ("SHIP SYSTEMS")
///       ├── OxygenSection
///       │   ├── LabelText        "LIFE SUPPORT / O₂"
///       │   ├── StatusBadge      "ONLINE" / "OFFLINE" / "CRITICAL"
///       │   ├── O2Bar            SciFiSegmentedBar
///       │   ├── O2LevelText      "87.3%"
///       │   ├── O2RateText       "+2.4 / min"
///       │   └── O2AutonText      "Autonomia: ∞ / 43 min"
///       ├── Divider
///       ├── [ShieldsSection]     stub — dipende da: ShieldSystem (M2)
///       ├── [HullSection]        stub — dipende da: HullSystem (M2)
///       ├── [PropulsionSection]  stub — dipende da: PropulsionSystem (M2)
///       ├── [FTLSection]         stub — dipende da: FTLSystem (M2)
///       └── [ReactorSection]     stub — dati da PowerManager (già disponibile, M2)
///
/// Pattern Open()/Close() — identico a EngineeringDashboardUI.
/// Pattern OnInstanceReady — si collega a OxygenSystem senza GetComponent a catena.
///
/// ⚠️  SEZIONE B (Repair): dipende da RepairSystem + InventorySystem (M2)
/// ⚠️  SEZIONE C (Diagnostica Elettrica): dati già parzialmente disponibili
///     in ElectricalDegradationManager — collegabile in M2 step successivo.
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

    // ── Stub sections (abilitate man mano che i sistemi vengono implementati) ──

    [Header("Shields (stub — dipende da: ShieldSystem M2)")]
    [SerializeField] private TextMeshProUGUI shieldsStatusBadge;
    [SerializeField] private SciFiSegmentedBar shieldsBar;

    [Header("Hull (stub — dipende da: HullSystem M2)")]
    [SerializeField] private TextMeshProUGUI hullStatusBadge;
    [SerializeField] private SciFiSegmentedBar hullBar;

    [Header("Propulsion (stub — dipende da: PropulsionSystem M2)")]
    [SerializeField] private TextMeshProUGUI propulsionStatusBadge;

    [Header("FTL (stub — dipende da: FTLSystem M2)")]
    [SerializeField] private TextMeshProUGUI ftlStatusBadge;

    [Header("Reactor (stub — dati PowerManager disponibili M2)")]
    [SerializeField] private TextMeshProUGUI reactorStatusBadge;
    [SerializeField] private SciFiSegmentedBar reactorBar;

    // ── Colori stato ─────────────────────────────────────────────────────────

    [Header("Status Colors")]
    [SerializeField] private Color colorOnline = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorDegraded = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);
    [SerializeField] private Color colorOffline = new Color(0.5f, 0.5f, 0.5f);

    // ── Stato interno ─────────────────────────────────────────────────────────

    private SpaceSurvivor.Ship.OxygenSystem oxygenSystem;
    private PowerManager powerManager;
    private bool isOpen = false;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        // OxygenSystem
        if (SpaceSurvivor.Ship.OxygenSystem.Instance != null)
            InitWithOxygenSystem();
        else
            SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady += InitWithOxygenSystem;

        // PowerManager (per sezione Reactor)
        if (PowerManager.Instance != null)
            InitWithPowerManager();
        else
            PowerManager.OnInstanceReady += InitWithPowerManager;

        // Stub statics finché i sistemi non sono pronti
        SetStubBadges();
    }

    private void InitWithOxygenSystem()
    {
        SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady -= InitWithOxygenSystem;
        oxygenSystem = SpaceSurvivor.Ship.OxygenSystem.Instance;
    }

    private void InitWithPowerManager()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        powerManager = PowerManager.Instance;
    }

    private void OnDestroy()
    {
        SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady -= InitWithOxygenSystem;
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        CancelInvoke(nameof(UpdateUI));
    }

    // ── Open / Close (pattern VirtualCursor) ─────────────────────────────────

    public void Open()
    {
        isOpen = true;

        // Fallback nel caso lo spawn sia avvenuto prima di Start()
        if (oxygenSystem == null && SpaceSurvivor.Ship.OxygenSystem.Instance != null)
            oxygenSystem = SpaceSurvivor.Ship.OxygenSystem.Instance;

        if (powerManager == null && PowerManager.Instance != null)
            powerManager = PowerManager.Instance;

        UpdateUI();
        InvokeRepeating(nameof(UpdateUI), 0f, 0.2f);
    }

    public void Close()
    {
        isOpen = false;
        CancelInvoke(nameof(UpdateUI));
    }

    // ── Aggiornamento UI ──────────────────────────────────────────────────────

    private void UpdateUI()
    {
        UpdateO2Section();
        UpdateReactorSection();
        // Le altre sezioni rimangono stub finché i sistemi non esistono
    }

    // ── O₂ Section ───────────────────────────────────────────────────────────

    private void UpdateO2Section()
    {
        if (oxygenSystem == null) return;

        float level = oxygenSystem.O2Level;          // 0-100
        float percent = oxygenSystem.O2Percentage;     // 0-1
        float netRate = oxygenSystem.NetRatePerMinute; // +/- per minuto

        // Barra
        if (o2Bar != null)
            o2Bar.SetValue(percent);

        // Livello testo
        if (o2LevelText != null)
            o2LevelText.text = $"{level:F1}%";

        // Rate testo
        if (o2RateText != null)
        {
            string sign = netRate >= 0f ? "+" : "";
            o2RateText.text = $"{sign}{netRate:F1} / min";
            o2RateText.color = netRate >= 0f ? colorOnline : colorCritical;
        }

        // Autonomia stimata
        if (o2AutonText != null)
            o2AutonText.text = ComputeAutonomy(level, netRate);

        // Status badge
        if (o2StatusBadge != null)
            SetO2Badge(level, oxygenSystem.IsAlarmActive);
    }

    private void SetO2Badge(float level, bool alarmActive)
    {
        if (alarmActive || level < 20f)
        {
            o2StatusBadge.text = "CRITICAL";
            o2StatusBadge.color = colorCritical;
        }
        else if (level < 50f)
        {
            o2StatusBadge.text = "WARNING";
            o2StatusBadge.color = colorDegraded;
        }
        else if (oxygenSystem.GenerationRatePerMinute <= 0f)
        {
            o2StatusBadge.text = "OFFLINE";
            o2StatusBadge.color = colorOffline;
        }
        else
        {
            o2StatusBadge.text = "ONLINE";
            o2StatusBadge.color = colorOnline;
        }
    }

    /// <summary>
    /// Calcola autonomia O2 restante in base al net rate corrente.
    /// Ritorna "∞" se rate positivo (livello in risalita), minuti se negativo.
    /// </summary>
    private string ComputeAutonomy(float currentLevel, float netRatePerMinute)
    {
        if (netRatePerMinute >= 0f)
            return "Autonomia: ∞";

        // tempo = livello / |rate| (in minuti)
        float minutes = currentLevel / Mathf.Abs(netRatePerMinute);

        if (minutes > 999f)
            return "Autonomia: ∞";

        int mins = Mathf.FloorToInt(minutes);
        int secs = Mathf.FloorToInt((minutes - mins) * 60f);
        return $"Autonomia: {mins:D2}:{secs:D2}";
    }

    // ── Reactor Section (PowerManager) ───────────────────────────────────────

    private void UpdateReactorSection()
    {
        if (powerManager == null) return;

        if (reactorBar != null)
            reactorBar.SetValue(1f - powerManager.PowerPercentage); // carico inverso = headroom

        if (reactorStatusBadge != null)
        {
            if (powerManager.IsInBlackout)
            {
                reactorStatusBadge.text = "BLACKOUT";
                reactorStatusBadge.color = colorCritical;
            }
            else if (powerManager.IsInCriticalState)
            {
                reactorStatusBadge.text = "CRITICAL";
                reactorStatusBadge.color = colorCritical;
            }
            else
            {
                reactorStatusBadge.text = "ONLINE";
                reactorStatusBadge.color = colorOnline;
            }
        }
    }

    // ── Stub badges (sistemi non ancora implementati) ────────────────────────

    private void SetStubBadges()
    {
        SetBadge(shieldsStatusBadge, "OFFLINE", colorOffline);
        SetBadge(hullStatusBadge, "INTACT", colorOnline);
        SetBadge(propulsionStatusBadge, "ONLINE", colorOnline);
        SetBadge(ftlStatusBadge, "ONLINE", colorOnline);

        if (shieldsBar != null) shieldsBar.SetValue(0f);
        if (hullBar != null) hullBar.SetValue(1f);
    }

    private void SetBadge(TextMeshProUGUI badge, string text, Color color)
    {
        if (badge == null) return;
        badge.text = text;
        badge.color = color;
    }
}