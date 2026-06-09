using UnityEngine;
using UnityEngine.UI;
using TMPro;
using SpaceSurvivor.Ship;

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
///   Dati reali da InventorySystem (M2).
///   Aggiornamento event-driven via InventorySystem.OnQuantityChanged.
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
    [SerializeField] private TextMeshProUGUI o2LevelText;
    [SerializeField] private TextMeshProUGUI o2RateText;
    [SerializeField] private TextMeshProUGUI o2AutonText;
    [SerializeField] private TextMeshProUGUI o2StatusBadge;
    [SerializeField] private TextMeshProUGUI lifeSupportBadge;

    // ── SEZIONE C — Scorte mediche ────────────────────────────────────────────

    [Header("Sezione C — Medical Supplies")]
    [SerializeField] private SciFiSegmentedBar medkitBasicBar;
    [SerializeField] private SciFiSegmentedBar medkitAdvancedBar;
    [SerializeField] private SciFiSegmentedBar o2TankBar;
    [SerializeField] private SciFiSegmentedBar antidoteBar;
    [SerializeField] private TextMeshProUGUI medkitBasicText;
    [SerializeField] private TextMeshProUGUI medkitAdvancedText;
    [SerializeField] private TextMeshProUGUI o2TankText;
    [SerializeField] private TextMeshProUGUI antidoteText;

    // ── COLORI ────────────────────────────────────────────────────────────────

    [Header("Status Colors")]
    [SerializeField] private Color colorOnline = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorWarning = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);
    [SerializeField] private Color colorOffline = new Color(0.5f, 0.5f, 0.5f);

    // ── RIFERIMENTI SISTEMI ───────────────────────────────────────────────────

    private OxygenSystem oxygenSystem;
    private InventorySystem inventorySystem;

    // ── LIFECYCLE ─────────────────────────────────────────────────────────────

    private void Start()
    {
        // OxygenSystem
        if (OxygenSystem.Instance != null)
            oxygenSystem = OxygenSystem.Instance;
        else
            OxygenSystem.OnInstanceReady += OnOxygenReady;

        // InventorySystem
        if (InventorySystem.Instance != null)
            ConnectInventory();
        else
            InventorySystem.OnInstanceReady += OnInventoryReady;

        SetCrewStub();
        UpdateMedicalSupplies();
    }

    private void OnDestroy()
    {
        OxygenSystem.OnInstanceReady -= OnOxygenReady;
        InventorySystem.OnInstanceReady -= OnInventoryReady;
        InventorySystem.OnQuantityChanged -= OnInventoryQuantityChanged;
        CancelInvoke(nameof(UpdateUI));
    }

    private void OnOxygenReady()
    {
        OxygenSystem.OnInstanceReady -= OnOxygenReady;
        oxygenSystem = OxygenSystem.Instance;
    }

    private void OnInventoryReady()
    {
        InventorySystem.OnInstanceReady -= OnInventoryReady;
        ConnectInventory();
    }

    private void ConnectInventory()
    {
        inventorySystem = InventorySystem.Instance;
        InventorySystem.OnQuantityChanged += OnInventoryQuantityChanged;
        UpdateMedicalSupplies();
    }

    private void OnInventoryQuantityChanged(ItemType type, int _)
    {
        // Aggiorna Sezione C solo per item medici
        if (type >= ItemType.MedkitBase)
            UpdateMedicalSupplies();
    }

    // ── OPEN / CLOSE ──────────────────────────────────────────────────────────

    public void Open()
    {
        if (oxygenSystem == null && OxygenSystem.Instance != null)
            oxygenSystem = OxygenSystem.Instance;

        if (inventorySystem == null && InventorySystem.Instance != null)
            ConnectInventory();

        UpdateUI();
        UpdateMedicalSupplies();
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
    }

    // ── SEZIONE B — O₂ ───────────────────────────────────────────────────────

    private void UpdateO2Section()
    {
        if (oxygenSystem == null) return;

        float level = oxygenSystem.O2Level;
        float percent = oxygenSystem.O2Percentage;
        float netRate = oxygenSystem.NetRatePerMinute;
        float genRate = oxygenSystem.GenerationRatePerMinute;

        if (o2Bar != null) o2Bar.SetValue(percent);

        if (o2LevelText != null)
            o2LevelText.text = $"{level:F1}%";

        if (o2RateText != null)
        {
            string sign = netRate >= 0f ? "+" : "";
            o2RateText.text = $"{sign}{netRate:F1} / min";
            o2RateText.color = netRate >= 0f ? colorOnline : colorCritical;
        }

        if (o2AutonText != null)
            o2AutonText.text = ComputeAutonomy(level, netRate);

        UpdateO2Badge(level, percent);
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
        else if (genRate < 2.0f)
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
        if (crewEntries == null) return;

        for (int i = 0; i < crewEntries.Length; i++)
        {
            if (crewEntries[i] == null) continue;

            if (i == 0)
            {
                crewEntries[i].SetData("CREW 01", 100f, 100f, colorOnline);
                crewEntries[i].gameObject.SetActive(true);
            }
            else
            {
                crewEntries[i].gameObject.SetActive(false);
            }
        }
    }

    // ── SEZIONE C — MEDICAL SUPPLIES ─────────────────────────────────────────

    private void UpdateMedicalSupplies()
    {
        if (inventorySystem == null)
        {
            // Fallback stub finché InventorySystem non è pronto
            SetSupplyEntry(medkitBasicBar, medkitBasicText, "Medikit Base", 0, 10);
            SetSupplyEntry(medkitAdvancedBar, medkitAdvancedText, "Medikit Avanzato", 0, 5);
            SetSupplyEntry(o2TankBar, o2TankText, "O₂ Tank", 0, 5);
            SetSupplyEntry(antidoteBar, antidoteText, "Antidoto", 0, 5);
            return;
        }

        SetSupplyEntry(medkitBasicBar, medkitBasicText, "Medikit Base",
            inventorySystem.GetQuantity(ItemType.MedkitBase),
            inventorySystem.GetMaxStack(ItemType.MedkitBase));

        SetSupplyEntry(medkitAdvancedBar, medkitAdvancedText, "Medikit Avanzato",
            inventorySystem.GetQuantity(ItemType.MedkitAdvanced),
            inventorySystem.GetMaxStack(ItemType.MedkitAdvanced));

        SetSupplyEntry(o2TankBar, o2TankText, "O₂ Tank",
            inventorySystem.GetQuantity(ItemType.O2EmergencyTank),
            inventorySystem.GetMaxStack(ItemType.O2EmergencyTank));

        SetSupplyEntry(antidoteBar, antidoteText, "Antidoto",
            inventorySystem.GetQuantity(ItemType.Antidote),
            inventorySystem.GetMaxStack(ItemType.Antidote));
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
        badge.text = text;
        badge.color = color;
    }
}