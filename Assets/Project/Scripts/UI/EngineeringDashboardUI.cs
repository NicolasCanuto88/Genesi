using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Engineering Dashboard UI - Power management interface
/// Displays power status and allows manual control of systems
/// </summary>
public class EngineeringDashboardUI : MonoBehaviour
{
    public enum LightSortMode
    {
        Name,        // Alfabetico per nome GameObject
        Consumption, // Per wattaggio (PowerConsumption)
        Priority     // Per priorità PowerManager (GetPriority)
    }

    [Header("Power Status Display")]
    [SerializeField] private TextMeshProUGUI powerGenerationText;
    [SerializeField] private TextMeshProUGUI powerConsumptionText;
    [SerializeField] private TextMeshProUGUI powerReserveText;
    [SerializeField] private TextMeshProUGUI powerStatusText;
    [SerializeField] private Slider powerGenerationSlider;
    [SerializeField] private Slider powerReserveSlider;

    [Header("Blackout Recovery")]
    [SerializeField] private GameObject blackoutPanel;
    [SerializeField] private Button restorePowerButton;
    [SerializeField] private TextMeshProUGUI restorePowerStatusText;

    [Header("Manual Lights Control")]
    [SerializeField] private Transform lightsListParent;
    [SerializeField] private GameObject lightControlPrefab;

    [Header("Lights Sorting")]
    [Tooltip("Criterio di ordinamento della lista luci.")]
    [SerializeField] private LightSortMode sortMode = LightSortMode.Name;
    [Tooltip("True = crescente (A→Z, basso→alto). False = decrescente.")]
    [SerializeField] private bool sortAscending = true;
    [Tooltip("Opzionale: bottone per ciclare la modalità di ordinamento a runtime.")]
    [SerializeField] private Button sortModeButton;
    [Tooltip("Opzionale: label che mostra la modalità di ordinamento attiva.")]
    [SerializeField] private TextMeshProUGUI sortModeLabel;

    private PowerManager powerManager;
    private List<LightControlEntry> lightControls = new List<LightControlEntry>();

    private class LightControlEntry
    {
        public ShipLight light;
        public GameObject uiElement;
        public Toggle toggle;
        public TextMeshProUGUI label;
    }

    private void Awake()
    {
        powerManager = PowerManager.Instance;

        if (restorePowerButton != null)
        {
            restorePowerButton.onClick.AddListener(OnRestorePowerClicked);
        }

        if (sortModeButton != null)
        {
            sortModeButton.onClick.AddListener(CycleSortMode);
        }
    }

    public void Open()
    {
        RefreshLightsList();
        UpdateSortModeLabel();
        UpdateUI();
        InvokeRepeating(nameof(UpdateUI), 0f, 0.1f); // Update 10 times per second
    }

    public void Close()
    {
        CancelInvoke(nameof(UpdateUI));
    }

    private void UpdateUI()
    {
        if (powerManager == null) return;

        // Power generation
        if (powerGenerationText != null)
        {
            powerGenerationText.text = $"{powerManager.CurrentPowerGeneration:F0}W / {powerManager.MaxPowerOutput:F0}W";
        }

        if (powerGenerationSlider != null)
        {
            powerGenerationSlider.value = powerManager.CurrentPowerGeneration / powerManager.MaxPowerOutput;
        }

        // Power consumption
        if (powerConsumptionText != null)
        {
            float percent = (powerManager.CurrentPowerGeneration > 0)
                ? (powerManager.CurrentPowerConsumption / powerManager.CurrentPowerGeneration) * 100f
                : 0f;
            powerConsumptionText.text = $"{powerManager.CurrentPowerConsumption:F0}W ({percent:F0}%)";
        }

        // Power reserve
        if (powerReserveText != null)
        {
            powerReserveText.text = $"Reserve: {powerManager.PowerReservePercentage * 100f:F0}%";
        }

        if (powerReserveSlider != null)
        {
            powerReserveSlider.value = powerManager.PowerReservePercentage;
        }

        // Power status
        if (powerStatusText != null)
        {
            if (powerManager.IsInBlackout)
            {
                powerStatusText.text = "⚠️⚠️⚠️ BLACKOUT ⚠️⚠️⚠️";
                powerStatusText.color = Color.red;
            }
            else if (powerManager.IsInCriticalState)
            {
                powerStatusText.text = "⚠️ CRITICAL POWER";
                powerStatusText.color = Color.yellow;
            }
            else
            {
                powerStatusText.text = "✅ OPERATIONAL";
                powerStatusText.color = Color.green;
            }
        }

        // Blackout panel
        if (blackoutPanel != null)
        {
            blackoutPanel.SetActive(powerManager.IsInBlackout && powerManager.IsBlackoutManualResetNeeded);
        }

        // Restore power button
        if (restorePowerButton != null && restorePowerStatusText != null)
        {
            string reason;
            bool canRestore = powerManager.CanRestorePower(out reason);

            restorePowerButton.interactable = canRestore;
            restorePowerStatusText.text = reason;

            if (canRestore)
            {
                restorePowerStatusText.color = Color.green;
            }
            else
            {
                restorePowerStatusText.color = Color.red;
            }
        }

        // Update light toggles
        foreach (var entry in lightControls)
        {
            if (entry.light != null && entry.toggle != null)
            {
                // Update toggle without triggering callback
                entry.toggle.SetIsOnWithoutNotify(entry.light.GetManualState());

                // Update label color based on light state
                if (entry.label != null)
                {
                    var state = entry.light.GetCurrentState();
                    switch (state)
                    {
                        case ShipLight.LightState.Normal:
                            entry.label.color = Color.white;
                            break;
                        case ShipLight.LightState.Emergency:
                            entry.label.color = Color.red;
                            break;
                        case ShipLight.LightState.Off:
                            entry.label.color = Color.grey;
                            break;
                    }
                }
            }
        }
    }

    private void RefreshLightsList()
    {
        // Clear existing
        foreach (var entry in lightControls)
        {
            if (entry.uiElement != null)
            {
                Destroy(entry.uiElement);
            }
        }
        lightControls.Clear();

        if (powerManager == null || lightsListParent == null || lightControlPrefab == null)
            return;

        // Get all manual lights, then sort by the active criterion
        List<ShipLight> manualLights = SortLights(powerManager.GetManualLights());

        // Create UI entry for each (already in sorted order)
        foreach (var light in manualLights)
        {
            GameObject entryGO = Instantiate(lightControlPrefab, lightsListParent);

            LightControlEntry entry = new LightControlEntry
            {
                light = light,
                uiElement = entryGO,
                toggle = entryGO.GetComponentInChildren<Toggle>(),
                label = entryGO.GetComponentInChildren<TextMeshProUGUI>()
            };

            if (entry.label != null)
            {
                entry.label.text = $"{light.gameObject.name} - {light.PowerConsumption:F0}W";
            }

            if (entry.toggle != null)
            {
                entry.toggle.isOn = light.GetManualState();

                // Add listener
                ShipLight capturedLight = light; // Capture for closure
                entry.toggle.onValueChanged.AddListener((bool isOn) => OnLightToggled(capturedLight, isOn));
            }

            lightControls.Add(entry);
        }
    }

    /// <summary>
    /// Ordina le luci secondo sortMode + sortAscending.
    /// Il nome è sempre il tie-breaker (ordinamento stabile e deterministico).
    /// </summary>
    private List<ShipLight> SortLights(List<ShipLight> lights)
    {
        if (lights == null || lights.Count <= 1)
            return lights ?? new List<ShipLight>();

        // Scarta eventuali riferimenti nulli (luci distrutte)
        IEnumerable<ShipLight> valid = lights.Where(l => l != null);

        IOrderedEnumerable<ShipLight> ordered;

        switch (sortMode)
        {
            case LightSortMode.Consumption:
                ordered = sortAscending
                    ? valid.OrderBy(l => l.PowerConsumption)
                    : valid.OrderByDescending(l => l.PowerConsumption);
                ordered = ordered.ThenBy(l => l.gameObject.name, System.StringComparer.OrdinalIgnoreCase);
                break;

            case LightSortMode.Priority:
                ordered = sortAscending
                    ? valid.OrderBy(l => l.GetPriority())
                    : valid.OrderByDescending(l => l.GetPriority());
                ordered = ordered.ThenBy(l => l.gameObject.name, System.StringComparer.OrdinalIgnoreCase);
                break;

            case LightSortMode.Name:
            default:
                ordered = sortAscending
                    ? valid.OrderBy(l => l.gameObject.name, System.StringComparer.OrdinalIgnoreCase)
                    : valid.OrderByDescending(l => l.gameObject.name, System.StringComparer.OrdinalIgnoreCase);
                break;
        }

        return ordered.ToList();
    }

    // ===== SORTING PUBLIC API =====

    /// <summary>
    /// Cicla tra le modalità di ordinamento (Name → Consumption → Priority → Name).
    /// Collegabile al sortModeButton o richiamabile da altri sistemi.
    /// </summary>
    public void CycleSortMode()
    {
        int count = System.Enum.GetValues(typeof(LightSortMode)).Length;
        sortMode = (LightSortMode)(((int)sortMode + 1) % count);
        ApplySortChange();
    }

    /// <summary>
    /// Imposta una modalità di ordinamento specifica.
    /// </summary>
    public void SetSortMode(LightSortMode mode)
    {
        sortMode = mode;
        ApplySortChange();
    }

    /// <summary>
    /// Inverte la direzione di ordinamento (crescente/decrescente).
    /// </summary>
    public void ToggleSortDirection()
    {
        sortAscending = !sortAscending;
        ApplySortChange();
    }

    private void ApplySortChange()
    {
        RefreshLightsList();
        UpdateSortModeLabel();
    }

    private void UpdateSortModeLabel()
    {
        if (sortModeLabel == null) return;

        string arrow = sortAscending ? "▲" : "▼";
        string modeName;
        switch (sortMode)
        {
            case LightSortMode.Consumption: modeName = "Consumption"; break;
            case LightSortMode.Priority: modeName = "Priority"; break;
            default: modeName = "Name"; break;
        }

        sortModeLabel.text = $"Sort: {modeName} {arrow}";
    }

    private void OnLightToggled(ShipLight light, bool isOn)
    {
        if (light != null)
        {
            light.SetManualState(isOn);
        }
    }

    private void OnRestorePowerClicked()
    {
        if (powerManager != null)
        {
            powerManager.TryManualPowerRestore();
        }
    }
}