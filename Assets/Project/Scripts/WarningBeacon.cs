using UnityEngine;

/// <summary>
/// Rotating warning beacon driven by AlarmSystem (Milestone 1B).
/// Subscribes to AlarmSystem.OnAlarmStateChanged and adjusts rotation speed,
/// light intensity and colour by severity, with smooth fade in/out.
///
/// Emissive material is updated via MaterialPropertyBlock (no material instances),
/// always setting _BaseColor and _EmissionColor together (project light rule).
/// Multiple beacons can exist; each subscribes independently.
/// Supports multiple lights per beacon (e.g. two symmetric spot lights).
/// </summary>
[DisallowMultipleComponent]
public class WarningBeacon : MonoBehaviour
{
    [Header("Rotation")]
    [Tooltip("Transform che ruota (mesh o luce). Se vuoto usa questo transform.")]
    [SerializeField] private Transform rotatingPart;
    [SerializeField] private Vector3 rotationAxis = Vector3.forward; // asse Z default
    [SerializeField] private float warningRotationSpeed = 90f;
    [SerializeField] private float criticalRotationSpeed = 180f;
    [SerializeField] private float emergencyRotationSpeed = 300f;

    [Header("Lights")]
    [Tooltip("Tutte le luci del beacon (es. due Spot simmetriche). Se vuoto, cerca in GetComponentsInChildren.")]
    [SerializeField] private Light[] beaconLights;
    [SerializeField] private float maxIntensity = 4f;
    [Tooltip("Velocità di transizione (lerp/sec) per intensità, colore e rotazione.")]
    [SerializeField] private float fadeSpeed = 6f;

    [Header("Colors by severity")]
    [SerializeField] private Color warningColor = new Color(1f, 0.65f, 0f);
    [SerializeField] private Color criticalColor = new Color(1f, 0.25f, 0f);
    [SerializeField] private Color emergencyColor = new Color(1f, 0.05f, 0.05f);

    [Header("Emissive Mesh (optional)")]
    [SerializeField] private Renderer[] emissiveRenderers;
    [Tooltip("Intensità HDR emission per il bloom URP.")]
    [SerializeField] private float emissionIntensity = 3f;

    private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
    private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");

    private MaterialPropertyBlock propertyBlock;
    private AlarmSystem alarmSystem;

    private float targetIntensity = 0f;
    private float currentRotationSpeed = 0f;
    private float targetRotationSpeed = 0f;
    private Color targetColor;

    private void Awake()
    {
        if (rotatingPart == null) rotatingPart = transform;

        // Auto-populate lights array if not set in Inspector
        if (beaconLights == null || beaconLights.Length == 0)
            beaconLights = GetComponentsInChildren<Light>();

        if (emissiveRenderers != null && emissiveRenderers.Length > 0)
            propertyBlock = new MaterialPropertyBlock();

        targetColor = emergencyColor;

        // Start silent/off
        foreach (var l in beaconLights)
        {
            if (l == null) continue;
            l.intensity = 0f;
            l.color = targetColor;
        }

        ApplyEmission(0f);
    }

    private void Start()
    {
        alarmSystem = AlarmSystem.Instance;

        if (alarmSystem != null)
        {
            alarmSystem.OnAlarmStateChanged += HandleAlarmStateChanged;
            HandleAlarmStateChanged(alarmSystem.CurrentSeverity);
        }
        else
        {
            Debug.LogWarning($"[WarningBeacon {gameObject.name}] AlarmSystem not found");
        }
    }

    private void OnDestroy()
    {
        if (alarmSystem != null)
            alarmSystem.OnAlarmStateChanged -= HandleAlarmStateChanged;
    }

    private void HandleAlarmStateChanged(AlarmSystem.AlarmSeverity severity)
    {
        // In blackout la nave è già al buio — il beacon si spegne con tutto il resto.
        // L'audio AlarmAudioController continua indipendentemente (batteria separata).
        bool isBlackout = PowerManager.Instance != null && PowerManager.Instance.IsInBlackout;

        if (severity == AlarmSystem.AlarmSeverity.None || isBlackout)
        {
            targetRotationSpeed = 0f;
            targetIntensity = 0f;
            return;
        }

        switch (severity)
        {
            case AlarmSystem.AlarmSeverity.Warning:
                targetColor = warningColor;
                targetRotationSpeed = warningRotationSpeed;
                targetIntensity = maxIntensity;
                break;

            case AlarmSystem.AlarmSeverity.Critical:
                targetColor = criticalColor;
                targetRotationSpeed = criticalRotationSpeed;
                targetIntensity = maxIntensity;
                break;

            case AlarmSystem.AlarmSeverity.Emergency:
                targetColor = emergencyColor;
                targetRotationSpeed = emergencyRotationSpeed;
                targetIntensity = maxIntensity;
                break;
        }
    }

    private void Update()
    {
        float step = fadeSpeed * Time.deltaTime;

        // Smooth rotation speed, then rotate
        currentRotationSpeed = Mathf.Lerp(currentRotationSpeed, targetRotationSpeed, step);
        if (currentRotationSpeed > 0.01f && rotatingPart != null)
            rotatingPart.Rotate(rotationAxis.normalized * currentRotationSpeed * Time.deltaTime, Space.Self);

        // Update all lights
        float emissionRatio = 0f;

        if (beaconLights != null && beaconLights.Length > 0)
        {
            foreach (var l in beaconLights)
            {
                if (l == null) continue;
                l.intensity = Mathf.Lerp(l.intensity, targetIntensity, step);
                l.color = Color.Lerp(l.color, targetColor, step);
            }

            // Use first light as reference for emission ratio
            var first = beaconLights[0];
            if (first != null && maxIntensity > 0f)
                emissionRatio = Mathf.Clamp01(first.intensity / maxIntensity);
        }
        else
        {
            emissionRatio = targetIntensity > 0f ? 1f : 0f;
        }

        ApplyEmission(emissionRatio);
    }

    private void ApplyEmission(float ratio)
    {
        if (propertyBlock == null || emissiveRenderers == null) return;

        Color emission = targetColor * emissionIntensity * ratio;
        Color baseCol = targetColor * Mathf.Lerp(0.15f, 1f, ratio);

        foreach (var renderer in emissiveRenderers)
        {
            if (renderer == null) continue;
            propertyBlock.SetColor(BaseColorID, baseCol);
            propertyBlock.SetColor(EmissionColorID, emission);
            renderer.SetPropertyBlock(propertyBlock);
        }
    }
}