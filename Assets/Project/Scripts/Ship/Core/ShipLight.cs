using UnityEngine;

/// <summary>
/// Ship light system with power integration and emergency modes.
/// Automatic: sempre accesa quando c'è corrente.
/// Manual: stato controllato dalla dashboard via LightNetworkManager (NetworkList).
///
/// Non è un NetworkBehaviour — lo stato Manual è sincronizzato da LightNetworkManager
/// che usa una NetworkList centralizzata (un solo NetworkObject per tutte le luci).
/// </summary>
[RequireComponent(typeof(Light))]
public class ShipLight : MonoBehaviour, IPowerConsumer
{
    [Header("Light Configuration")]
    [SerializeField] private LightMode lightMode = LightMode.Automatic;
    [SerializeField] private float powerConsumption = 50f;
    [SerializeField] private int priority = 3;

    [Header("Normal Operation")]
    [SerializeField] private Color normalColor = new Color(1f, 0.95f, 0.9f);
    [SerializeField] private float normalIntensity = 1.5f;

    [Header("Emergency Mode")]
    [SerializeField] private Color emergencyColor = new Color(1f, 0.1f, 0.05f);
    [SerializeField] private float emergencyIntensity = 0.5f;

    [Header("Flickering (Critical Power)")]
    [SerializeField] private bool enableFlickering = true;
    [SerializeField] private float flickerSpeed = 10f;
    [SerializeField] private float flickerAmount = 0.3f;

    [Header("Emissive Materials (Optional)")]
    [SerializeField] private Renderer[] emissiveRenderers;
    [SerializeField] private float normalEmissionIntensity = 2f;
    [SerializeField] private float emergencyEmissionIntensity = 1f;
    [SerializeField] private bool updateEmissiveMaterials = true;

    [Header("Transition")]
    [SerializeField] private float colorTransitionSpeed = 2f;

    private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");
    private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
    private MaterialPropertyBlock propertyBlock;

    private Light lightComponent;
    private PowerManager powerManager;

    private LightState currentState = LightState.Off;
    private bool manuallyEnabled = true;
    private bool isPowered = false;
    private float targetIntensity = 0f;
    private Color targetColor = Color.white;
    private float flickerTimer = 0f;

    // Indice nel LightNetworkManager — assegnato alla registrazione
    private int networkIndex = -1;

    public enum LightMode { Automatic, Manual }
    public enum LightState { Off, Normal, Emergency }

    private void Awake()
    {
        lightComponent = GetComponent<Light>();

        if (normalColor == Color.white) normalColor = lightComponent.color;
        if (normalIntensity == 0f) normalIntensity = lightComponent.intensity;

        if (updateEmissiveMaterials && emissiveRenderers != null && emissiveRenderers.Length > 0)
            propertyBlock = new MaterialPropertyBlock();
    }

    private void Start()
    {
        isPowered = true;

        // PowerManager
        if (PowerManager.Instance != null)
            InitWithPowerManager();
        else
            PowerManager.OnInstanceReady += InitWithPowerManager;

        // LightNetworkManager
        if (LightNetworkManager.Instance != null)
            RegisterWithLightManager();
        else
            LightNetworkManager.OnInstanceReady += RegisterWithLightManager;

        UpdateLightState();
    }

    private void InitWithPowerManager()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        powerManager = PowerManager.Instance;
        powerManager.RegisterPowerConsumer(this);
        powerManager.OnPowerRestored += OnPowerRestoredEvent;
    }

    private void RegisterWithLightManager()
    {
        LightNetworkManager.OnInstanceReady -= RegisterWithLightManager;

        if (lightMode == LightMode.Manual)
            networkIndex = LightNetworkManager.Instance.RegisterLight(this, manuallyEnabled);
    }

    private void OnDestroy()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        LightNetworkManager.OnInstanceReady -= RegisterWithLightManager;

        if (powerManager != null)
        {
            powerManager.UnregisterPowerConsumer(this);
            powerManager.OnPowerRestored -= OnPowerRestoredEvent;
        }

        if (networkIndex >= 0 && LightNetworkManager.Instance != null)
            LightNetworkManager.Instance.UnregisterLight(networkIndex);
    }

    private void OnPowerRestoredEvent()
    {
        isPowered = true;
    }

    /// <summary>
    /// Chiamato da LightNetworkManager quando la NetworkList cambia.
    /// Aggiorna lo stato locale su tutti i client.
    /// </summary>
    public void OnNetworkManualStateChanged(bool isOn)
    {
        manuallyEnabled = isOn;
    }

    private void Update()
    {
        UpdateLightState();
        ApplyLightTransition();

        if (enableFlickering && currentState == LightState.Normal && powerManager != null)
        {
            if (powerManager.IsInCriticalState && !powerManager.IsInBlackout)
                ApplyFlickering();
        }
    }

    private void UpdateLightState()
    {
        if (powerManager == null)
        {
            if (currentState != LightState.Normal)
            {
                currentState = LightState.Normal;
                OnStateChanged();
            }
            return;
        }

        LightState newState = LightState.Off;

        if (powerManager.IsInBlackout)
        {
            newState = LightState.Off;
        }
        else if (powerManager.IsInCriticalState)
        {
            if (ShouldBeOn()) newState = LightState.Emergency;
        }
        else if (isPowered)
        {
            if (ShouldBeOn()) newState = LightState.Normal;
        }

        if (newState != currentState)
        {
            currentState = newState;
            OnStateChanged();
        }
    }

    private bool ShouldBeOn()
    {
        return lightMode == LightMode.Automatic ? true : manuallyEnabled;
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
        lightComponent.color = Color.Lerp(lightComponent.color, targetColor, colorTransitionSpeed * Time.deltaTime);
        lightComponent.intensity = Mathf.Lerp(lightComponent.intensity, targetIntensity, colorTransitionSpeed * Time.deltaTime);

        if (updateEmissiveMaterials && propertyBlock != null)
            UpdateEmissiveMaterials();
    }

    private void UpdateEmissiveMaterials()
    {
        if (emissiveRenderers == null || emissiveRenderers.Length == 0) return;

        Color baseColor;
        Color emissionColor;
        float emissionIntensity;

        switch (currentState)
        {
            case LightState.Off:
                baseColor = new Color(0.2f, 0.2f, 0.2f);
                emissionColor = Color.black;
                emissionIntensity = 0f;
                break;
            case LightState.Normal:
                baseColor = normalColor;
                emissionColor = normalColor;
                emissionIntensity = normalEmissionIntensity;
                break;
            case LightState.Emergency:
                baseColor = emergencyColor;
                emissionColor = emergencyColor;
                emissionIntensity = emergencyEmissionIntensity;
                break;
            default:
                return;
        }

        Color finalEmission = emissionColor * emissionIntensity;

        foreach (var renderer in emissiveRenderers)
        {
            if (renderer == null) continue;
            propertyBlock.SetColor(BaseColorID, baseColor);
            propertyBlock.SetColor(EmissionColorID, finalEmission);
            renderer.SetPropertyBlock(propertyBlock);
        }
    }

    private void ApplyFlickering()
    {
        flickerTimer += Time.deltaTime * flickerSpeed;
        float flicker = Mathf.Clamp01(Mathf.PerlinNoise(flickerTimer, 0f));
        lightComponent.intensity = targetIntensity * (1f - flickerAmount * (1f - flicker));
    }

    // ===== IPowerConsumer =====

    public float GetPowerDemand()
    {
        if (currentState == LightState.Off) return 0f;

        float multiplier = ElectricalDegradationManager.Instance != null
            ? ElectricalDegradationManager.Instance.GetTotalMultiplier()
            : 1.0f;

        return powerConsumption * multiplier;
    }

    public int GetPriority() => priority;
    public bool IsActive() => currentState != LightState.Off;
    public bool CanBeDisabled() => lightMode == LightMode.Manual;

    public void SetPowerState(bool isOn)
    {
        isPowered = isOn;
        if (!isOn) Debug.Log($"[ShipLight {gameObject.name}] Power cut by PowerManager");
    }

    public string GetSystemName() => $"Light_{gameObject.name}";

    // ===== Public API =====

    /// <summary>
    /// Imposta lo stato manual. In rete: passa per LightNetworkManager → ServerRpc.
    /// In single player: aggiorna direttamente.
    /// </summary>
    public void SetManualState(bool enabled)
    {
        if (lightMode != LightMode.Manual)
        {
            Debug.LogWarning($"[ShipLight {gameObject.name}] Cannot manually control Automatic light!");
            return;
        }

        if (networkIndex >= 0 && LightNetworkManager.Instance != null)
        {
            // Multiplayer: il server aggiorna la NetworkList → tutti i client ricevono il cambio
            LightNetworkManager.Instance.SetManualState(networkIndex, enabled);
        }
        else
        {
            // Fallback single player senza LightNetworkManager
            manuallyEnabled = enabled;
        }
    }

    public bool GetManualState()
    {
        if (networkIndex >= 0 && LightNetworkManager.Instance != null)
            return LightNetworkManager.Instance.GetManualState(networkIndex);
        return manuallyEnabled;
    }

    public float PowerConsumption => powerConsumption;
    public LightMode GetLightMode() => lightMode;
    public LightState GetCurrentState() => currentState;

    public void SetLightMode(LightMode mode)
    {
        lightMode = mode;
    }

    private void OnDrawGizmosSelected()
    {
        if (lightComponent == null) lightComponent = GetComponent<Light>();

        Gizmos.color = currentState == LightState.Emergency ? emergencyColor : normalColor;
        Gizmos.color = new Color(Gizmos.color.r, Gizmos.color.g, Gizmos.color.b, 0.3f);
        Gizmos.DrawWireSphere(transform.position, lightComponent.range);

        Gizmos.color = lightMode == LightMode.Automatic ? Color.green : Color.yellow;
        Gizmos.DrawSphere(transform.position, 0.1f);
    }
}