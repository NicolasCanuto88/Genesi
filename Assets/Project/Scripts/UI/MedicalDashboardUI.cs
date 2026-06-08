using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// MedicalDashboardUI — Milestone 2
/// Monitor unico della Medical Station. Tre sezioni verticali (GDD §9.4).
///
/// SEZIONE A — Stato equipaggio:
///   HP real-time per ogni membro.
///   In M2: placeholder 1 crew a HP pieni (PlayerHealthSystem non ancora implementato).
///   ⚠️ Dipende da: PlayerHealthSystem (M3) per dati reali multiplayer.
///
/// SEZIONE B — O₂ & Life Support:
///   Dati reali da OxygenSystem (già live).
///   Livello O₂, generazione, consumo, bilancio, autonomia stimata.
///   Life Support status: ONLINE / DEGRADED / OFFLINE.
///
/// SEZIONE C — Scorte mediche:
///   Stub con valori fissi.
///   ⚠️ Dipende da: InventorySystem (M2) per dati reali.
///
/// Pattern Open()/Close() via IDashboardPanel — chiamato da MedicalStation.
/// </summary>
public class MedicalDashboardUI : MonoBehaviour, IDashboardPanel
{
    // ── SEZIONE A — Equipaggio ────────────────────────────────────────────────

    [Header("Sezione A — Crew HP (stub M2, reale M3)")]
    [Tooltip("Un elemento per ogni slot crew (massimo 5). Disattiva quelli non usati.")]
    [SerializeField] private CrewHPEntry[] crewEntries;

    // ── SEZIONE B — O₂ & Life Support ────────────────────────────────────────

    [Header("Sezione B — O₂ & Life Support")]
    [SerializeField] private SciFiSegmentedBar o2Bar;
    [SerializeField] private TextMeshProUGUI   o2LevelText;
    [SerializeField] private TextMeshProUGUI   o2RateText;
    [SerializeField] private TextMeshProUGUI   o2AutonText;
    [SerializeField] private TextMeshProUGUI   o2StatusBadge;
    [SerializeField] private TextMeshProUGUI   lifeSupportBadge;

    // ── SEZIONE C — Scorte mediche (stub) ─────────────────────────────────────

    [Header("Sezione C — Medical Supplies (stub M2)")]
    [SerializeField] private SciFiSegmentedBar medkitBasicBar;
    [SerializeField] private SciFiSegmentedBar medkitAdvancedBar;
    [SerializeField] private SciFiSegmentedBar o2TankBar;
    [SerializeField] private SciFiSegmentedBar antidoteBar;
    [SerializeField] private TextMeshProUGUI   medkitBasicText;
    [SerializeField] private TextMeshProUGUI   medkitAdvancedText;
    [SerializeField] private TextMeshProUGUI   o2TankText;
    [SerializeField] private TextMeshProUGUI   antidoteText;

    // ── COLORI ────────────────────────────────────────────────────────────────

    [Header("Status Colors")]
    [SerializeField] private Color colorOnline   = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorWarning  = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);
    [SerializeField] private Color colorOffline  = new Color(0.5f, 0.5f, 0.5f);

    // ── RIFERIMENTI SISTEMI ───────────────────────────────────────────────────

    private SpaceSurvivor.Ship.OxygenSystem oxygenSystem;

    // ── LIFECYCLE ─────────────────────────────────────────────────────────────

    private void Start()
    {
        if (SpaceSurvivor.Ship.OxygenSystem.Instance != null)
            oxygenSystem = SpaceSurvivor.Ship.OxygenSystem.Instance;
        else
            SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady += OnOxygenReady;

        SetCrewStub();
        SetMedicalSuppliesStub();
    }

    private void OnDestroy()
    {
        SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady -= OnOxygenReady;
        CancelInvoke(nameof(UpdateUI));
    }

    private void OnOxygenReady()
    {
        SpaceSurvivor.Ship.OxygenSystem.OnInstanceReady -= OnOxygenReady;
        oxygenSystem = SpaceSurvivor.Ship.OxygenSystem.Instance;
    }

    // ── OPEN / CLOSE ──────────────────────────────────────────────────────────

    public void Open()
    {
        if (oxygenSystem == null && SpaceSurvivor.Ship.OxygenSystem.Instance != null)
            oxygenSystem = SpaceSurvivor.Ship.OxygenSystem.Instance;

        UpdateUI();
        InvokeRepeating(nameof(UpdateUI), 0f, 0.2f);
    }

    public void Close()
    {
        CancelInvoke(nameof(UpdateUI));
    }

    // ── UPDATE UI ─────────────────────────────────────────────────────────────

    private void UpdateUI()
    {
        UpdateO2Section();
        // Crew e Medical Supplies sono stub — nessun polling necessario
    }

    // ── SEZIONE B — O₂ ───────────────────────────────────────────────────────

    private void UpdateO2Section()
    {
        if (oxygenSystem == null) return;

        float level      = oxygenSystem.O2Level;
        float percent    = oxygenSystem.O2Percentage;
        float netRate    = oxygenSystem.NetRatePerMinute;
        float genRate    = oxygenSystem.GenerationRatePerMinute;

        // Barra O₂
        if (o2Bar != null) o2Bar.SetValue(percent);

        // Percentuale
        if (o2LevelText != null)
            o2LevelText.text = $"{level:F1}%";

        // Rate netto
        if (o2RateText != null)
        {
            string sign  = netRate >= 0f ? "+" : "";
            o2RateText.text  = $"{sign}{netRate:F1} / min";
            o2RateText.color = netRate >= 0f ? colorOnline : colorCritical;
        }

        // Autonomia
        if (o2AutonText != null)
            o2AutonText.text = ComputeAutonomy(level, netRate);

        // Badge O₂
        UpdateO2Badge(level, percent);

        // Badge Life Support
        UpdateLifeSupportBadge(genRate);
    }

    private void UpdateO2Badge(float level, float percent)
    {
        if (o2StatusBadge == null) return;

        if (oxygenSystem.IsAlarmActive || percent < 0.20f)
            SetBadge(o2StatusBadge, "CRITICO", colorCritical);
        else if (percent < 0.50f)
            SetBadge(o2StatusBadge, "BASSO", colorWarning);
        else
            SetBadge(o2StatusBadge, "NORMALE", colorOnline);
    }

    private void UpdateLifeSupportBadge(float genRate)
    {
        if (lifeSupportBadge == null) return;

        if (genRate <= 0f)
            SetBadge(lifeSupportBadge, "OFFLINE", colorOffline);
        else if (genRate < 2.0f)   // meno della metà del T1 → degradato
            SetBadge(lifeSupportBadge, "DEGRADATO", colorWarning);
        else
            SetBadge(lifeSupportBadge, "ONLINE", colorOnline);
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

    // ── SEZIONE A — CREW (stub M2) ────────────────────────────────────────────

    private void SetCrewStub()
    {
        // M2: 1 crew visibile, HP pieni. Dati reali in M3 con PlayerHealthSystem.
        if (crewEntries == null) return;

        for (int i = 0; i < crewEntries.Length; i++)
        {
            if (crewEntries[i] == null) continue;

            if (i == 0)
            {
                // Slot 0 — host placeholder
                crewEntries[i].SetData("CREW 01", 100f, 100f, colorOnline);
                crewEntries[i].gameObject.SetActive(true);
            }
            else
            {
                // Slot 1–4 — vuoti in M2
                crewEntries[i].gameObject.SetActive(false);
            }
        }
    }

    // ── SEZIONE C — MEDICAL SUPPLIES (stub M2) ────────────────────────────────

    private void SetMedicalSuppliesStub()
    {
        // Valori fissi finché InventorySystem non è implementato (M2)
        SetSupplyEntry(medkitBasicBar,    medkitBasicText,    "Medikit Base",     3, 10);
        SetSupplyEntry(medkitAdvancedBar, medkitAdvancedText, "Medikit Avanzato", 1, 5);
        SetSupplyEntry(o2TankBar,         o2TankText,         "O₂ Tank",          2, 5);
        SetSupplyEntry(antidoteBar,       antidoteText,       "Antidoto",         0, 5);
    }

    private void SetSupplyEntry(SciFiSegmentedBar bar, TextMeshProUGUI label,
                                 string name, int current, int max)
    {
        if (bar != null)
            bar.SetValue(max > 0 ? (float)current / max : 0f);

        if (label != null)
        {
            label.text = $"{name}: {current}/{max}";
            label.color = current == 0
                ? colorOffline
                : current < max * 0.2f
                    ? colorCritical
                    : current < max * 0.5f
                        ? colorWarning
                        : colorOnline;
        }
    }

    // ── UTILITY ───────────────────────────────────────────────────────────────

    private void SetBadge(TextMeshProUGUI badge, string text, Color color)
    {
        if (badge == null) return;
        badge.text  = text;
        badge.color = color;
    }
}
