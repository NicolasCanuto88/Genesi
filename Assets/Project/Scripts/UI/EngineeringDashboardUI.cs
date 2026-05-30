using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

/// <summary>
/// Engineering Dashboard UI — Power management interface.
/// Aggiornamento M1B: Slider standard sostituiti con SciFiSegmentedBar.
///
/// MODIFICA RISPETTO ALLA VERSIONE PRECEDENTE:
///   - Rimossi: [SerializeField] Slider powerGenerationSlider / powerReserveSlider
///   - Aggiunti: [SerializeField] SciFiSegmentedBar powerGenerationBar / powerReserveBar
///   - UpdateUI() aggiorna bar.SetValue() invece di slider.value
///   - Il resto della logica è INVARIATO (PowerManager, lights, blackout)
/// </summary>
public class EngineeringDashboardUI : MonoBehaviour
{
    [Header("Power Status Display")]
    [SerializeField] private TextMeshProUGUI powerGenerationText;
    [SerializeField] private TextMeshProUGUI powerConsumptionText;
    [SerializeField] private TextMeshProUGUI powerReserveText;
    [SerializeField] private TextMeshProUGUI powerStatusText;

    // ── SOSTITUITI: erano Slider, ora SciFiSegmentedBar ──────────────────────
    [SerializeField] private SciFiSegmentedBar powerGenerationBar;
    [SerializeField] private SciFiSegmentedBar powerReserveBar;
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Blackout Recovery")]
    [SerializeField] private GameObject blackoutPanel;
    [SerializeField] private Button restorePowerButton;
    [SerializeField] private TextMeshProUGUI restorePowerStatusText;

    [Header("Manual Lights Control")]
    [SerializeField] private Transform lightsListParent;
    [SerializeField] private GameObject lightControlPrefab;

    private PowerManager powerManager;
    private List<LightControlEntry> lightControls = new List<LightControlEntry>();

    private class LightControlEntry
    {
        public ShipLight light;
        public GameObject uiElement;
        public Toggle toggle;
        public TextMeshProUGUI label;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        powerManager = PowerManager.Instance;

        if (restorePowerButton != null)
            restorePowerButton.onClick.AddListener(OnRestorePowerClicked);
    }

    public void Open()
    {
        RefreshLightsList();
        UpdateUI();
        InvokeRepeating(nameof(UpdateUI), 0f, 0.1f);
    }

    public void Close()
    {
        CancelInvoke(nameof(UpdateUI));
    }

    // ── Aggiornamento UI ─────────────────────────────────────────────────────

    private void UpdateUI()
    {
        if (powerManager == null) return;

        // — Generazione —
        if (powerGenerationText != null)
        {
            powerGenerationText.text =
                $"{powerManager.CurrentPowerGeneration:F0}W / {powerManager.MaxPowerOutput:F0}W";
        }

        // SetValue() invece di slider.value
        if (powerGenerationBar != null)
        {
            powerGenerationBar.SetValue(
                powerManager.CurrentPowerGeneration / powerManager.MaxPowerOutput);
        }

        // — Consumo —
        if (powerConsumptionText != null)
        {
            float percent = (powerManager.CurrentPowerGeneration > 0)
                ? (powerManager.CurrentPowerConsumption / powerManager.CurrentPowerGeneration) * 100f
                : 0f;
            powerConsumptionText.text =
                $"{powerManager.CurrentPowerConsumption:F0}W ({percent:F0}%)";
        }

        // — Riserva —
        if (powerReserveText != null)
        {
            powerReserveText.text =
                $"Reserve: {powerManager.PowerReservePercentage * 100f:F0}%";
        }

        // SetValue() invece di slider.value
        if (powerReserveBar != null)
        {
            powerReserveBar.SetValue(powerManager.PowerReservePercentage);
        }

        // — Stato testuale —
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

        // — Pannello blackout —
        if (blackoutPanel != null)
        {
            blackoutPanel.SetActive(
                powerManager.IsInBlackout && powerManager.IsBlackoutManualResetNeeded);
        }

        // — Pulsante restore —
        if (restorePowerButton != null && restorePowerStatusText != null)
        {
            bool canRestore = powerManager.CanRestorePower(out string reason);
            restorePowerButton.interactable = canRestore;
            restorePowerStatusText.text = reason;
            restorePowerStatusText.color = canRestore ? Color.green : Color.red;
        }

        // — Toggle luci —
        foreach (var entry in lightControls)
        {
            if (entry.light == null || entry.toggle == null) continue;

            entry.toggle.SetIsOnWithoutNotify(entry.light.GetManualState());

            if (entry.label != null)
            {
                entry.label.color = entry.light.GetCurrentState() switch
                {
                    ShipLight.LightState.Normal => Color.white,
                    ShipLight.LightState.Emergency => Color.red,
                    ShipLight.LightState.Off => Color.grey,
                    _ => Color.white
                };
            }
        }
    }

    // ── Lista luci ───────────────────────────────────────────────────────────

    private void RefreshLightsList()
    {
        foreach (var entry in lightControls)
        {
            if (entry.uiElement != null)
                Destroy(entry.uiElement);
        }
        lightControls.Clear();

        if (powerManager == null || lightsListParent == null || lightControlPrefab == null)
            return;

        List<ShipLight> manualLights = powerManager.GetManualLights();

        foreach (var light in manualLights)
        {
            GameObject entryGO = Instantiate(lightControlPrefab, lightsListParent);

            var entry = new LightControlEntry
            {
                light = light,
                uiElement = entryGO,
                toggle = entryGO.GetComponentInChildren<Toggle>(),
                label = entryGO.GetComponentInChildren<TextMeshProUGUI>()
            };

            if (entry.label != null)
                entry.label.text = $"{light.gameObject.name} - {light.PowerConsumption:F0}W";

            if (entry.toggle != null)
            {
                entry.toggle.isOn = light.GetManualState();

                ShipLight capturedLight = light;
                entry.toggle.onValueChanged.AddListener(
                    (bool isOn) => OnLightToggled(capturedLight, isOn));
            }

            lightControls.Add(entry);
        }
    }

    // ── Handler pulsanti ─────────────────────────────────────────────────────

    private void OnLightToggled(ShipLight light, bool isOn)
    {
        if (light != null)
        {
            light.SetManualState(isOn);
            Debug.Log($"[EngineeringDashboard] {light.gameObject.name} → {(isOn ? "ON" : "OFF")}");
        }
    }

    private void OnRestorePowerClicked()
    {
        if (powerManager == null) return;

        bool success = powerManager.TryManualPowerRestore();
        Debug.Log(success
            ? "[EngineeringDashboard] Power restored successfully!"
            : "[EngineeringDashboard] Failed to restore power");
    }
}