using UnityEngine;
using System.Collections.Generic;
using System;

/// <summary>
/// Central power management system for the ship
/// Manages power generation, distribution, and consumption
/// </summary>
public class PowerManager : MonoBehaviour
{
    [Header("Power Generation")]
    [SerializeField] private float maxPowerOutput = 1000f; // Total power available
    [SerializeField] private float currentReactorEfficiency = 1.0f; // 0-1, can be damaged
    [SerializeField] private float reactorDegradationRate = 0.01f; // Per hour of operation

    [Header("Power Status")]
    [SerializeField] private float currentPowerGeneration;
    [SerializeField] private float currentPowerConsumption;
    [SerializeField] private float powerReserve; // Battery backup
    [SerializeField] private float maxPowerReserve = 500f;

    [Header("Critical Settings")]
    [SerializeField] private float criticalPowerThreshold = 0.2f; // 20% - trigger warnings
    [SerializeField] private float blackoutThreshold = 0.05f; // 5% - start shutting down systems

    [Header("Blackout Settings")]
    [SerializeField] private bool requireManualRecovery = true; // Blackout needs manual restart
    private bool blackoutManualResetNeeded = false; // Set during blackout if manual recovery required

    // Power consumers (all systems that need power)
    private List<IPowerConsumer> powerConsumers = new List<IPowerConsumer>();

    // Events
    public event Action<float> OnPowerLevelChanged; // Current percentage
    public event Action OnCriticalPower;
    public event Action OnBlackout;
    public event Action OnPowerRestored;

    // State
    private bool isInCriticalState;
    private bool isInBlackout;

    // Singleton (for easy access)
    public static PowerManager Instance { get; private set; }

    // Properties
    public float MaxPowerOutput => maxPowerOutput * currentReactorEfficiency;
    public float CurrentPowerGeneration => currentPowerGeneration;
    public float CurrentPowerConsumption => currentPowerConsumption;
    public float PowerPercentage => (currentPowerGeneration > 0) ? (currentPowerConsumption / currentPowerGeneration) : 0;
    public float PowerReservePercentage => powerReserve / maxPowerReserve;
    public bool IsInCriticalState => isInCriticalState;
    public bool IsInBlackout => isInBlackout;
    public bool IsBlackoutManualResetNeeded => blackoutManualResetNeeded;

    private void Awake()
    {
        // Singleton setup
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        // Initialize
        currentPowerGeneration = MaxPowerOutput;
        powerReserve = maxPowerReserve;
    }

    private void Update()
    {
        UpdatePowerGeneration();
        CalculatePowerConsumption();
        HandlePowerDeficit();
        CheckCriticalStates();

        // Reactor degradation over time (simulation)
        DegradeReactor();
    }

    private void UpdatePowerGeneration()
    {
        // Current generation based on reactor efficiency
        currentPowerGeneration = MaxPowerOutput;

        // In blackout, generation is severely limited
        if (isInBlackout)
        {
            currentPowerGeneration *= 0.1f; // Only 10% during blackout (emergency power)
        }
    }

    private void CalculatePowerConsumption()
    {
        currentPowerConsumption = 0f;

        foreach (var consumer in powerConsumers)
        {
            if (consumer != null && consumer.IsActive())
            {
                currentPowerConsumption += consumer.GetPowerDemand();
            }
        }
    }

    private void HandlePowerDeficit()
    {
        float powerDeficit = currentPowerConsumption - currentPowerGeneration;

        if (powerDeficit > 0)
        {
            // We're consuming more than we generate - drain reserves
            powerReserve -= powerDeficit * Time.deltaTime;
            powerReserve = Mathf.Max(0f, powerReserve);

            // If reserves depleted, we need to shed load
            if (powerReserve <= 0)
            {
                ShedLoad(powerDeficit);
            }
        }
        else
        {
            // We're generating surplus - charge reserves
            float surplus = Mathf.Abs(powerDeficit);
            powerReserve += surplus * Time.deltaTime * 0.5f; // Charges slower than it drains
            powerReserve = Mathf.Min(maxPowerReserve, powerReserve);
        }
    }

    private void ShedLoad(float deficitAmount)
    {
        // Automatic load shedding - turn off lowest priority systems
        // Sort consumers by priority (lowest first)
        powerConsumers.Sort((a, b) => a.GetPriority().CompareTo(b.GetPriority()));

        float shedAmount = 0f;

        foreach (var consumer in powerConsumers)
        {
            if (consumer != null && consumer.IsActive() && consumer.CanBeDisabled())
            {
                float consumerDemand = consumer.GetPowerDemand();
                consumer.SetPowerState(false); // Turn off
                shedAmount += consumerDemand;

                Debug.LogWarning($"[PowerManager] Auto-disabled {consumer.GetSystemName()} (Priority {consumer.GetPriority()}) to save {consumerDemand}W");

                if (shedAmount >= deficitAmount)
                {
                    break; // We've shed enough load
                }
            }
        }
    }

    private void CheckCriticalStates()
    {
        float powerPercent = PowerPercentage;

        // Check for critical power
        if (powerPercent >= criticalPowerThreshold && !isInCriticalState)
        {
            isInCriticalState = true;
            OnCriticalPower?.Invoke();
            Debug.LogWarning("[PowerManager] CRITICAL POWER - Load at " + (powerPercent * 100f).ToString("F1") + "%");
        }
        else if (powerPercent < criticalPowerThreshold * 0.8f && isInCriticalState)
        {
            isInCriticalState = false;
        }

        // Check for blackout ENTRY
        if (powerReserve <= 0 && powerPercent >= 1.0f && !isInBlackout)
        {
            EnterBlackout();
        }

        // Check for blackout EXIT
        if (isInBlackout)
        {
            // If manual recovery required, prevent auto-exit
            if (requireManualRecovery && blackoutManualResetNeeded)
            {
                // Reserve can recharge, but blackout persists until manual reset
                // Player must use Engineering Dashboard to restore
                // (do nothing - wait for TryManualPowerRestore() call)
            }
            else
            {
                // Auto-exit blackout (only if manual recovery disabled)
                float fullGenerationPercent = currentPowerConsumption / MaxPowerOutput;

                if (fullGenerationPercent < 0.5f && powerReserve > maxPowerReserve * 0.3f)
                {
                    ExitBlackout();
                }
            }
        }

        // Notify listeners of power level changes
        OnPowerLevelChanged?.Invoke(powerPercent);
    }

    private void EnterBlackout()
    {
        isInBlackout = true;
        blackoutManualResetNeeded = requireManualRecovery; // Set flag for manual recovery

        OnBlackout?.Invoke();
        Debug.LogError("[PowerManager] BLACKOUT! Systems shutting down! Manual recovery required.");

        // Force disable all non-critical systems
        foreach (var consumer in powerConsumers)
        {
            if (consumer != null && consumer.GetPriority() < 10) // Priority 10+ = critical
            {
                consumer.SetPowerState(false);
            }
        }
    }

    private void ExitBlackout()
    {
        isInBlackout = false;
        OnPowerRestored?.Invoke();
        Debug.Log("[PowerManager] Power restored from blackout");
    }

    /// <summary>
    /// Manually restore power from blackout - called by Engineering Dashboard
    /// </summary>
    public bool TryManualPowerRestore()
    {
        if (!isInBlackout)
        {
            Debug.LogWarning("[PowerManager] Not in blackout - cannot restore");
            return false;
        }

        if (!blackoutManualResetNeeded)
        {
            Debug.LogWarning("[PowerManager] Blackout doesn't require manual reset");
            return false;
        }

        // Requirement 1: Reserve must have at least 30% charge
        float reservePercent = powerReserve / maxPowerReserve;
        if (reservePercent < 0.3f)
        {
            Debug.LogWarning($"[PowerManager] Insufficient reserve ({reservePercent * 100f:F0}%) - need 30% minimum");
            return false;
        }

        // Requirement 2: Consumption must be under 70% of max
        float consumptionPercent = currentPowerConsumption / MaxPowerOutput;
        if (consumptionPercent > 0.7f)
        {
            Debug.LogWarning($"[PowerManager] Load too high ({consumptionPercent * 100f:F0}%) - reduce consumption first");
            return false;
        }

        // All checks passed - restore power
        Debug.Log("[PowerManager] ✅ Manual power restore successful!");
        blackoutManualResetNeeded = false;
        ExitBlackout();
        return true;
    }

    /// <summary>
    /// Check if manual power restore is currently possible
    /// </summary>
    public bool CanRestorePower(out string reason)
    {
        reason = "";

        if (!isInBlackout)
        {
            reason = "Not in blackout";
            return false;
        }

        if (!blackoutManualResetNeeded)
        {
            reason = "Auto-recovery in progress";
            return false;
        }

        float reservePercent = powerReserve / maxPowerReserve;
        if (reservePercent < 0.3f)
        {
            reason = $"Reserve too low ({reservePercent * 100f:F0}% / 30% required)";
            return false;
        }

        float consumptionPercent = currentPowerConsumption / MaxPowerOutput;
        if (consumptionPercent > 0.7f)
        {
            reason = $"Load too high ({consumptionPercent * 100f:F0}% / 70% max)";
            return false;
        }

        reason = "Ready to restore";
        return true;
    }

    /// <summary>
    /// Get all manual lights for dashboard control
    /// </summary>
    public List<ShipLight> GetManualLights()
    {
        List<ShipLight> manualLights = new List<ShipLight>();

        foreach (var consumer in powerConsumers)
        {
            if (consumer is ShipLight light)
            {
                if (light.GetLightMode() == ShipLight.LightMode.Manual)
                {
                    manualLights.Add(light);
                }
            }
        }

        return manualLights;
    }

    private void DegradeReactor()
    {
        // Simulate reactor wear over time
        currentReactorEfficiency -= reactorDegradationRate * Time.deltaTime / 3600f; // Per hour
        currentReactorEfficiency = Mathf.Max(0.5f, currentReactorEfficiency); // Min 50% efficiency
    }

    // Public methods for systems to register/unregister
    public void RegisterPowerConsumer(IPowerConsumer consumer)
    {
        if (!powerConsumers.Contains(consumer))
        {
            powerConsumers.Add(consumer);
            Debug.Log($"[PowerManager] Registered consumer: {consumer.GetSystemName()}");
        }
    }

    public void UnregisterPowerConsumer(IPowerConsumer consumer)
    {
        powerConsumers.Remove(consumer);
    }

    // Manual reactor control
    public void SetReactorEfficiency(float efficiency)
    {
        currentReactorEfficiency = Mathf.Clamp01(efficiency);
    }

    public void RepairReactor(float amount)
    {
        currentReactorEfficiency += amount;
        currentReactorEfficiency = Mathf.Min(1.0f, currentReactorEfficiency);
    }

    // Debug
    private void OnGUI()
    {
        if (!Debug.isDebugBuild) return;

        int y = 100;
        GUI.Label(new Rect(10, y, 300, 20), $"=== POWER SYSTEM ===");
        y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Generation: {currentPowerGeneration:F0}W / {MaxPowerOutput:F0}W");
        y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Consumption: {currentPowerConsumption:F0}W ({(PowerPercentage * 100f):F1}%)");
        y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Reserve: {powerReserve:F0}W / {maxPowerReserve:F0}W");
        y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Reactor Efficiency: {(currentReactorEfficiency * 100f):F1}%");
        y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Active Consumers: {powerConsumers.Count}");
        y += 20;

        if (isInCriticalState)
        {
            GUI.color = Color.yellow;
            GUI.Label(new Rect(10, y, 300, 20), "⚠ CRITICAL POWER");
            y += 20;
        }

        if (isInBlackout)
        {
            GUI.color = Color.red;
            GUI.Label(new Rect(10, y, 300, 20), "⚠⚠⚠ BLACKOUT ⚠⚠⚠");
        }

        GUI.color = Color.white;
    }
}

/// <summary>
/// Interface for all systems that consume power
/// </summary>
public interface IPowerConsumer
{
    /// <summary>
    /// Current power demand in Watts
    /// </summary>
    float GetPowerDemand();

    /// <summary>
    /// Priority level (0-10, 10 = critical, cannot be auto-disabled)
    /// </summary>
    int GetPriority();

    /// <summary>
    /// Is this system currently active and consuming power?
    /// </summary>
    bool IsActive();

    /// <summary>
    /// Can this system be automatically disabled during power shortage?
    /// </summary>
    bool CanBeDisabled();

    /// <summary>
    /// Set power state (on/off) - called by PowerManager during load shedding
    /// </summary>
    void SetPowerState(bool isOn);

    /// <summary>
    /// System name for debugging
    /// </summary>
    string GetSystemName();
}