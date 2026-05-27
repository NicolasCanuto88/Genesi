using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

/// <summary>
/// Engineering Dashboard UI - Power management interface
/// Displays power status and allows manual control of systems
/// </summary>
public class EngineeringDashboardUI : MonoBehaviour
{
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
    }
    
    public void Open()
    {
        RefreshLightsList();
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
        
        // Get all manual lights
        List<ShipLight> manualLights = powerManager.GetManualLights();
        
        // Create UI entry for each
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
    
    private void OnLightToggled(ShipLight light, bool isOn)
    {
        if (light != null)
        {
            light.SetManualState(isOn);
            Debug.Log($"[EngineeringDashboard] Light {light.gameObject.name} set to {(isOn ? "ON" : "OFF")}");
        }
    }
    
    private void OnRestorePowerClicked()
    {
        if (powerManager != null)
        {
            bool success = powerManager.TryManualPowerRestore();
            
            if (success)
            {
                Debug.Log("[EngineeringDashboard] Power restored successfully!");
            }
            else
            {
                Debug.LogWarning("[EngineeringDashboard] Failed to restore power");
            }
        }
    }
}
