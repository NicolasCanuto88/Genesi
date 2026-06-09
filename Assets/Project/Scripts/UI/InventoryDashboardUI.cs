using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SpaceSurvivor.Ship;

/// <summary>
/// InventoryDashboardUI — Milestone 2
/// Monitor 3 della Engineering Station — Inventario Materiali.
///
/// LAYOUT (GDD §9.3):
///   Lista engineering items con barra colore + quantità testo.
///   Sezione "SCORTE BASSE ⚠" visibile automaticamente quando almeno
///   un item è sotto la soglia critica (< 20% maxStack).
///
/// COLORI BARRA:
///   Verde  > 50% · Giallo 20–50% · Rosso < 20% · Grigio = 0
///
/// PATTERN Open()/Close() via IDashboardPanel — chiamato da MonitorSwitcher.
/// Aggiornamento event-driven: si sottoscrive a InventorySystem.OnQuantityChanged.
/// </summary>
public class InventoryDashboardUI : MonoBehaviour, IDashboardPanel
{
    // ── Engineering Items ─────────────────────────────────────────────────────

    [Header("Mechanical Part")]
    [SerializeField] private Slider mechPartBar;
    [SerializeField] private Image mechPartFill;
    [SerializeField] private TextMeshProUGUI mechPartText;

    [Header("Wire Bundle")]
    [SerializeField] private Slider wireBundleBar;
    [SerializeField] private Image wireBundleFill;
    [SerializeField] private TextMeshProUGUI wireBundleText;

    [Header("Electronic Component")]
    [SerializeField] private Slider electronicBar;
    [SerializeField] private Image electronicFill;
    [SerializeField] private TextMeshProUGUI electronicText;

    [Header("Hull Plate")]
    [SerializeField] private Slider hullPlateBar;
    [SerializeField] private Image hullPlateFill;
    [SerializeField] private TextMeshProUGUI hullPlateText;

    [Header("Coolant Canister")]
    [SerializeField] private Slider coolantBar;
    [SerializeField] private Image coolantFill;
    [SerializeField] private TextMeshProUGUI coolantText;

    [Header("Fuel Cell")]
    [SerializeField] private Slider fuelCellBar;
    [SerializeField] private Image fuelCellFill;
    [SerializeField] private TextMeshProUGUI fuelCellText;

    // ── Sezione Scorte Basse ──────────────────────────────────────────────────

    [Header("Sezione Scorte Basse")]
    [Tooltip("GameObject contenitore dell'intera sezione warning. Si attiva/disattiva.")]
    [SerializeField] private GameObject lowStockSection;
    [SerializeField] private TextMeshProUGUI lowStockListText;

    // ── Colori ────────────────────────────────────────────────────────────────

    [Header("Colori")]
    [SerializeField] private Color colorOk = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorWarning = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);
    [SerializeField] private Color colorEmpty = new Color(0.4f, 0.4f, 0.4f);

    [Header("Soglie")]
    [Range(0f, 0.5f)]
    [SerializeField] private float thresholdWarning = 0.5f; // 50% → giallo
    [Range(0f, 0.3f)]
    [SerializeField] private float thresholdCritical = 0.2f; // 20% → rosso

    // ── Riferimento sistema ───────────────────────────────────────────────────

    private InventorySystem _inventory;
    private bool _isOpen;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        if (InventorySystem.Instance != null)
            ConnectInventory();
        else
            InventorySystem.OnInstanceReady += ConnectInventory;
    }

    private void OnDestroy()
    {
        InventorySystem.OnInstanceReady -= ConnectInventory;
        InventorySystem.OnQuantityChanged -= OnQuantityChanged;
    }

    private void ConnectInventory()
    {
        InventorySystem.OnInstanceReady -= ConnectInventory;
        _inventory = InventorySystem.Instance;

        // Sottoscrive all'evento per aggiornamenti in tempo reale
        InventorySystem.OnQuantityChanged += OnQuantityChanged;

        // Prima lettura: aggiorna tutta la UI con i valori correnti
        if (_isOpen) RefreshAll();
    }

    // ── IDashboardPanel ───────────────────────────────────────────────────────

    public void Open()
    {
        _isOpen = true;
        if (_inventory == null && InventorySystem.Instance != null)
            ConnectInventory();

        RefreshAll();
    }

    public void Close()
    {
        _isOpen = false;
    }

    // ── Aggiornamento ─────────────────────────────────────────────────────────

    /// <summary>Chiamato da InventorySystem.OnQuantityChanged su tutti i client.</summary>
    private void OnQuantityChanged(ItemType type, int newQty)
    {
        // Aggiorna solo l'item che è cambiato
        if (type >= ItemType.MedkitBase) return; // item medici: non di competenza di questo monitor

        UpdateEntry(type, newQty);
        RefreshLowStockSection();
    }

    /// <summary>Aggiorna tutti e 6 gli engineering items.</summary>
    private void RefreshAll()
    {
        if (_inventory == null) return;

        UpdateEntry(ItemType.MechanicalPart, _inventory.GetQuantity(ItemType.MechanicalPart));
        UpdateEntry(ItemType.WireBundle, _inventory.GetQuantity(ItemType.WireBundle));
        UpdateEntry(ItemType.ElectronicComponent, _inventory.GetQuantity(ItemType.ElectronicComponent));
        UpdateEntry(ItemType.HullPlate, _inventory.GetQuantity(ItemType.HullPlate));
        UpdateEntry(ItemType.CoolantCanister, _inventory.GetQuantity(ItemType.CoolantCanister));
        UpdateEntry(ItemType.FuelCell, _inventory.GetQuantity(ItemType.FuelCell));

        RefreshLowStockSection();
    }

    // ── Entry singola ─────────────────────────────────────────────────────────

    private void UpdateEntry(ItemType type, int qty)
    {
        GetEntryRefs(type, out var bar, out var fill, out var label);
        if (bar == null) return;

        int max = _inventory != null ? _inventory.GetMaxStack(type) : 99;
        float percent = max > 0 ? (float)qty / max : 0f;
        Color color = GetBarColor(percent, qty);

        bar.value = percent;
        if (fill != null) fill.color = color;
        if (label != null)
        {
            label.text = $"{GetDisplayName(type)}: {qty}/{max}";
            label.color = color;
        }
    }

    // ── Sezione Scorte Basse ──────────────────────────────────────────────────

    private void RefreshLowStockSection()
    {
        if (lowStockSection == null || _inventory == null) return;

        System.Text.StringBuilder sb = new();
        bool anyLow = false;

        for (int i = 0; i < (int)ItemType.MedkitBase; i++) // solo engineering
        {
            var type = (ItemType)i;
            int qty = _inventory.GetQuantity(type);
            int max = _inventory.GetMaxStack(type);
            float percent = max > 0 ? (float)qty / max : 0f;

            if (percent < thresholdCritical)
            {
                sb.AppendLine(qty == 0
                    ? $"<color=#FF3333>● {GetDisplayName(type)}: ESAURITO</color>"
                    : $"<color=#FF5500>● {GetDisplayName(type)}: {qty}/{max}</color>");
                anyLow = true;
            }
        }

        lowStockSection.SetActive(anyLow);
        if (lowStockListText != null)
            lowStockListText.text = sb.ToString().TrimEnd();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Color GetBarColor(float percent, int qty)
    {
        if (qty == 0) return colorEmpty;
        if (percent < thresholdCritical) return colorCritical;
        if (percent < thresholdWarning) return colorWarning;
        return colorOk;
    }

    private static string GetDisplayName(ItemType type) => type switch
    {
        ItemType.MechanicalPart => "Mech Part",
        ItemType.WireBundle => "Wire Bundle",
        ItemType.ElectronicComponent => "Electronic",
        ItemType.HullPlate => "Hull Plate",
        ItemType.CoolantCanister => "Coolant",
        ItemType.FuelCell => "Fuel Cell",
        _ => type.ToString()
    };

    /// <summary>Restituisce i riferimenti UI per un dato ItemType.</summary>
    private void GetEntryRefs(ItemType type,
                               out Slider bar,
                               out Image fill,
                               out TextMeshProUGUI label)
    {
        switch (type)
        {
            case ItemType.MechanicalPart:
                bar = mechPartBar; fill = mechPartFill; label = mechPartText; break;
            case ItemType.WireBundle:
                bar = wireBundleBar; fill = wireBundleFill; label = wireBundleText; break;
            case ItemType.ElectronicComponent:
                bar = electronicBar; fill = electronicFill; label = electronicText; break;
            case ItemType.HullPlate:
                bar = hullPlateBar; fill = hullPlateFill; label = hullPlateText; break;
            case ItemType.CoolantCanister:
                bar = coolantBar; fill = coolantFill; label = coolantText; break;
            case ItemType.FuelCell:
                bar = fuelCellBar; fill = fuelCellFill; label = fuelCellText; break;
            default:
                bar = null; fill = null; label = null; break;
        }
    }
}