using UnityEngine;

/// <summary>
/// Ship light system with power integration and emergency modes
/// Supports both automatic (always on) and manual (dashboard controlled) operation
/// </summary>
[RequireComponent(typeof(Light))]
public class ShipLight : MonoBehaviour, IPowerConsumer
{
    [Header("Light Configuration")]
    [SerializeField] private LightMode lightMode = LightMode.Automatic;
    [SerializeField] private float powerConsumption = 50f; // Watts when ON
    [SerializeField] private int priority = 3; // 0-10, lower = disabled first during load shedding

    [Header("Normal Operation")]
    [SerializeField] private Color normalColor = new Color(1f, 0.95f, 0.9f); // Warm white
    [SerializeField] private float normalIntensity = 1.5f;

    [Header("Emergency Mode")]
    [SerializeField] private Color emergencyColor = new Color(1f, 0.1f, 0.05f); // Red
    [SerializeField] private float emergencyIntensity = 0.5f;

    [Header("Flickering (Critical Power)")]
    [SerializeField] private bool enableFlickering = true;
    [SerializeField] private float flickerSpeed = 10f;
    [SerializeField] private float flickerAmount = 0.3f;

    [Header("Emissive Materials (Optional)")]
    [SerializeField] private Renderer[] emissiveRenderers; // Lampadine/bulbs
    [SerializeField] private float normalEmissionIntensity = 2f;
    [SerializeField] private float emergencyEmissionIntensity = 1f;
    [SerializeField] private bool updateEmissiveMaterials = true;

    [Header("Transition")]
    [SerializeField] private float colorTransitionSpeed = 2f;

    // Material property IDs (cached for performance)
    private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");
    private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
    private MaterialPropertyBlock propertyBlock;

    // Components
    private Light lightComponent;
    private PowerManager powerManager;

    // State
    private LightState currentState = LightState.Off;
    private bool manuallyEnabled = true; // For manual mode - dashboard controls this
    private bool isPowered = false;
    private float targetIntensity = 0f;
    private Color targetColor = Color.white;
    private float flickerTimer = 0f;

    public enum LightMode
    {
        Automatic,  // Always on when power available
        Manual      // Controlled by engineering dashboard
    }

    public enum LightState
    {
        Off,
        Normal,
        Emergency
    }

    private void Awake()
    {
        lightComponent = GetComponent<Light>();

        // Store initial settings if not set
        if (normalColor == Color.white)
        {
            normalColor = lightComponent.color;
        }
        if (normalIntensity == 0f)
        {
            normalIntensity = lightComponent.intensity;
        }

        // Initialize MaterialPropertyBlock for emissive materials
        if (updateEmissiveMaterials && emissiveRenderers != null && emissiveRenderers.Length > 0)
        {
            propertyBlock = new MaterialPropertyBlock();
        }
    }

    private void Start()
    {
        powerManager = PowerManager.Instance;

        if (powerManager != null)
        {
            powerManager.RegisterPowerConsumer(this);

            // Subscribe to power events
            powerManager.OnPowerRestored += OnPowerRestoredEvent;
        }
        else
        {
            Debug.LogError($"[ShipLight {gameObject.name}] PowerManager not found!");
        }

        // Initialize power state (start with power available)
        isPowered = true;

        // Initialize light state
        UpdateLightState();
    }

    private void OnDestroy()
    {
        if (powerManager != null)
        {
            powerManager.UnregisterPowerConsumer(this);

            // Unsubscribe from events
            powerManager.OnPowerRestored -= OnPowerRestoredEvent;
        }
    }

    // Event handler for power restored
    private void OnPowerRestoredEvent()
    {
        Debug.Log($"[ShipLight {gameObject.name}] Received power restored event - re-enabling");
        isPowered = true; // Restore power to this light
    }

    private void Update()
    {
        // Update light state based on power conditions
        UpdateLightState();

        // Apply visual changes
        ApplyLightTransition();

        // Flickering effect during critical power
        if (enableFlickering && currentState == LightState.Normal && powerManager != null)
        {
            if (powerManager.IsInCriticalState && !powerManager.IsInBlackout)
            {
                ApplyFlickering();
            }
        }
    }

    private void UpdateLightState()
    {
        if (powerManager == null) return;

        LightState newState = LightState.Off;

        // Determine desired state based on power and mode
        if (powerManager.IsInBlackout)
        {
            // Blackout: all lights off
            newState = LightState.Off;
        }
        else if (powerManager.IsInCriticalState)
        {
            // Critical power: emergency mode
            if (ShouldBeOn())
            {
                newState = LightState.Emergency;
            }
        }
        else if (isPowered)
        {
            // Normal operation
            if (ShouldBeOn())
            {
                newState = LightState.Normal;
            }
        }

        // State changed?
        if (newState != currentState)
        {
            currentState = newState;
            OnStateChanged();
        }
    }

    private bool ShouldBeOn()
    {
        switch (lightMode)
        {
            case LightMode.Automatic:
                return true; // Always on when power available

            case LightMode.Manual:
                return manuallyEnabled; // Only if dashboard enabled it

            default:
                return false;
        }
    }

    private void OnStateChanged()
    {
        switch (currentState)
        {
            case LightState.Off:
                targetIntensity = 0f;
                targetColor = normalColor;
                lightComponent.enabled = false;
                break;

            case LightState.Normal:
                targetIntensity = normalIntensity;
                targetColor = normalColor;
                lightComponent.enabled = true;
                break;

            case LightState.Emergency:
                targetIntensity = emergencyIntensity;
                targetColor = emergencyColor;
                lightComponent.enabled = true;
                break;
        }
    }

    private void ApplyLightTransition()
    {
        // Smooth color transition
        lightComponent.color = Color.Lerp(
            lightComponent.color,
            targetColor,
            colorTransitionSpeed * Time.deltaTime
        );

        // Smooth intensity transition
        lightComponent.intensity = Mathf.Lerp(
            lightComponent.intensity,
            targetIntensity,
            colorTransitionSpeed * Time.deltaTime
        );

        // Update emissive materials (bulbs/glass)
        if (updateEmissiveMaterials && propertyBlock != null)
        {
            UpdateEmissiveMaterials();
        }
    }

    private void UpdateEmissiveMaterials()
    {
        if (emissiveRenderers == null || emissiveRenderers.Length == 0) return;

        // Calculate emission color based on current state
        Color baseColor = Color.white;
        Color emissionColor = Color.black;
        float emissionIntensity = 0f;

        switch (currentState)
        {
            case LightState.Off:
                baseColor = new Color(0.2f, 0.2f, 0.2f); // Dark grey when off
                emissionColor = Color.black;
                emissionIntensity = 0f;
                break;

            case LightState.Normal:
                baseColor = normalColor; // Warm white base
                emissionColor = normalColor;
                emissionIntensity = normalEmissionIntensity;
                break;

            case LightState.Emergency:
                baseColor = emergencyColor; // RED base color ⬅️ QUESTO!
                emissionColor = emergencyColor;
                emissionIntensity = emergencyEmissionIntensity;
                break;
        }

        // Apply HDR emission color (intensity > 1 for bloom effect)
        Color finalEmission = emissionColor * emissionIntensity;

        // Update all emissive renderers
        foreach (var renderer in emissiveRenderers)
        {
            if (renderer != null)
            {
                propertyBlock.SetColor(BaseColorID, baseColor); // Base color (albedo)
                propertyBlock.SetColor(EmissionColorID, finalEmission); // Emission (glow)
                renderer.SetPropertyBlock(propertyBlock);
            }
        }
    }

    private void ApplyFlickering()
    {
        flickerTimer += Time.deltaTime * flickerSpeed;

        // Perlin noise for organic flickering
        float flicker = Mathf.PerlinNoise(flickerTimer, 0f);
        flicker = Mathf.Clamp01(flicker);

        // Apply to intensity
        float flickerIntensity = targetIntensity * (1f - flickerAmount * (1f - flicker));
        lightComponent.intensity = flickerIntensity;
    }

    // ===== IPOWER CONSUMER (PowerManager Interface) =====

    public float GetPowerDemand()
    {
        if (currentState == LightState.Off) return 0f;

        float multiplier = ElectricalDegradationManager.Instance != null
            ? ElectricalDegradationManager.Instance.GetTotalMultiplier()
            : 1.0f;

        return powerConsumption * multiplier;
    }

    public int GetPriority()
    {
        return priority;
    }

    public bool IsActive()
    {
        return currentState != LightState.Off;
    }

    public bool CanBeDisabled()
    {
        // Automatic lights cannot be disabled (always need light in corridors)
        // Manual lights can be disabled during load shedding
        return lightMode == LightMode.Manual;
    }

    public void SetPowerState(bool isOn)
    {
        isPowered = isOn;

        if (!isOn)
        {
            Debug.Log($"[ShipLight {gameObject.name}] Power cut by PowerManager");
        }
    }

    public string GetSystemName()
    {
        return $"Light_{gameObject.name}";
    }

    // ===== PUBLIC CONTROL METHODS (for Engineering Dashboard) =====

    /// <summary>
    /// Enable/disable this light (only works in Manual mode)
    /// </summary>
    public void SetManualState(bool enabled)
    {
        if (lightMode == LightMode.Manual)
        {
            manuallyEnabled = enabled;
            Debug.Log($"[ShipLight {gameObject.name}] Manual state: {(enabled ? "ON" : "OFF")}");
        }
        else
        {
            Debug.LogWarning($"[ShipLight {gameObject.name}] Cannot manually control Automatic light!");
        }
    }

    /// <summary>
    /// Get current manual state
    /// </summary>
    public bool GetManualState()
    {
        return manuallyEnabled;
    }

    /// <summary>
    /// Get current light mode
    /// </summary>
    // Public properties for external access
    public float PowerConsumption => powerConsumption;
    public LightMode GetLightMode() => lightMode;
    public LightState GetCurrentState() => currentState;

    /// <summary>
    /// Change light mode at runtime (for reconfiguration)
    /// </summary>
    public void SetLightMode(LightMode mode)
    {
        lightMode = mode;
        Debug.Log($"[ShipLight {gameObject.name}] Mode changed to: {mode}");
    }

    // ===== DEBUG =====

    private void OnDrawGizmosSelected()
    {
        if (lightComponent == null)
        {
            lightComponent = GetComponent<Light>();
        }

        // Draw sphere showing light range
        Gizmos.color = currentState == LightState.Emergency ? emergencyColor : normalColor;
        Gizmos.color = new Color(Gizmos.color.r, Gizmos.color.g, Gizmos.color.b, 0.3f);
        Gizmos.DrawWireSphere(transform.position, lightComponent.range);

        // Draw icon based on mode
        Gizmos.color = lightMode == LightMode.Automatic ? Color.green : Color.yellow;
        Gizmos.DrawSphere(transform.position, 0.1f);
    }
}