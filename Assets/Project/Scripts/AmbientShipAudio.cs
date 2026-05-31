using UnityEngine;

/// <summary>
/// Ambient ship soundscape (Milestone 1B).
/// Layers a continuous hum + ventilation loop and fires random structural creaks.
/// Reacts to PowerManager state via events (same decoupled pattern as AlarmSystem):
///   Normal   → full hum + ventilation
///   Critical → hum/vent dimmed and pitched down, creaks more frequent
///   Blackout → hum/vent cut, only sparse creaks of the ship settling in the dark
/// On power restored, everything fades back to normal.
/// </summary>
public class AmbientShipAudio : MonoBehaviour
{
    private enum AmbientState { Normal, Critical, Blackout }

    [Header("Loop Sources")]
    [Tooltip("AudioSource per il ronzio di fondo (loop).")]
    [SerializeField] private AudioSource humSource;
    [Tooltip("AudioSource per la ventilazione (loop).")]
    [SerializeField] private AudioSource ventSource;

    [Header("Creak One-Shots")]
    [Tooltip("AudioSource dedicato agli scricchiolii (PlayOneShot).")]
    [SerializeField] private AudioSource creakSource;
    [SerializeField] private AudioClip[] creakClips;

    [Header("Base Volumes (Normal)")]
    [Range(0f, 1f)][SerializeField] private float humVolume = 0.35f;
    [Range(0f, 1f)][SerializeField] private float ventVolume = 0.25f;

    [Header("State Multipliers")]
    [Range(0f, 1f)][SerializeField] private float criticalHumMultiplier = 0.7f;
    [Range(0f, 1f)][SerializeField] private float criticalVentMultiplier = 0.5f;
    [Tooltip("Volume hum residuo durante il blackout (riserva/drone). 0 = silenzio totale.")]
    [Range(0f, 1f)][SerializeField] private float blackoutHumVolume = 0.05f;

    [Header("Pitch")]
    [SerializeField] private float normalHumPitch = 1.0f;
    [SerializeField] private float criticalHumPitch = 0.92f;
    [SerializeField] private float blackoutHumPitch = 0.8f;

    [Header("Spin Down / Spin Up (hum pitch nel tempo)")]
    [Tooltip("Secondi per lo spin-down del pitch hum all'ingresso del blackout.")]
    [SerializeField] private float shutdownDuration = 2.5f;
    [Tooltip("Secondi per lo spin-up del pitch hum all'uscita dal blackout.")]
    [SerializeField] private float startupDuration = 2f;

    [Header("Transitions")]
    [Tooltip("Velocità di fade volumi/pitch (unità/sec).")]
    [SerializeField] private float fadeSpeed = 1.5f;

    [Header("Creak Timing (seconds)")]
    [SerializeField] private float creakIntervalMin = 8f;
    [SerializeField] private float creakIntervalMax = 20f;
    [Tooltip("In critical/blackout gli scricchiolii sono più frequenti (moltiplicatore < 1).")]
    [SerializeField] private float stressedIntervalMultiplier = 0.5f;
    [Range(0f, 1f)][SerializeField] private float creakVolume = 0.6f;
    [SerializeField] private float creakPitchVariation = 0.1f;

    private PowerManager powerManager;
    private AmbientState state = AmbientState.Normal;

    private float targetHumVolume;
    private float targetVentVolume;
    private float targetHumPitch;

    private float nextCreakTime;

    private void Start()
    {
        powerManager = PowerManager.Instance;

        if (powerManager != null)
        {
            powerManager.OnBlackout += HandleBlackout;
            powerManager.OnPowerRestored += HandlePowerRestored;
            powerManager.OnPowerLevelChanged += HandlePowerLevelChanged;
        }
        else
        {
            Debug.LogWarning("[AmbientShipAudio] PowerManager not found - ambient will stay in Normal state");
        }

        PrepareLoopSource(humSource, humVolume);
        PrepareLoopSource(ventSource, ventVolume);

        ApplyState(AmbientState.Normal, instant: true);
        ScheduleNextCreak();
    }

    private void OnDestroy()
    {
        if (powerManager != null)
        {
            powerManager.OnBlackout -= HandleBlackout;
            powerManager.OnPowerRestored -= HandlePowerRestored;
            powerManager.OnPowerLevelChanged -= HandlePowerLevelChanged;
        }
    }

    private void PrepareLoopSource(AudioSource src, float startVolume)
    {
        if (src == null) return;
        src.loop = true;
        src.playOnAwake = false;
        src.volume = startVolume;
        if (src.clip != null && !src.isPlaying) src.Play();
    }

    // ===== STATE FROM POWER EVENTS =====

    private void HandleBlackout() => ApplyState(AmbientState.Blackout);

    private void HandlePowerRestored() => RecomputeStateFromPower();

    private void HandlePowerLevelChanged(float percent) => RecomputeStateFromPower();

    private void RecomputeStateFromPower()
    {
        if (powerManager == null) return;

        if (powerManager.IsInBlackout) ApplyState(AmbientState.Blackout);
        else if (powerManager.IsInCriticalState) ApplyState(AmbientState.Critical);
        else ApplyState(AmbientState.Normal);
    }

    private void ApplyState(AmbientState newState, bool instant = false)
    {
        state = newState;

        switch (state)
        {
            case AmbientState.Normal:
                targetHumVolume = humVolume;
                targetVentVolume = ventVolume;
                targetHumPitch = normalHumPitch;
                break;

            case AmbientState.Critical:
                targetHumVolume = humVolume * criticalHumMultiplier;
                targetVentVolume = ventVolume * criticalVentMultiplier;
                targetHumPitch = criticalHumPitch;
                break;

            case AmbientState.Blackout:
                targetHumVolume = blackoutHumVolume;
                targetVentVolume = 0f;
                targetHumPitch = blackoutHumPitch;
                break;
        }

        if (instant)
        {
            if (humSource != null) { humSource.volume = targetHumVolume; humSource.pitch = targetHumPitch; }
            if (ventSource != null) ventSource.volume = targetVentVolume;
        }
    }

    private void Update()
    {
        float step = fadeSpeed * Time.deltaTime;

        if (humSource != null)
        {
            // Direzione: verso un pitch più basso = spin-down (shutdownDuration),
            // verso un pitch più alto = spin-up (startupDuration).
            bool spinningDown = targetHumPitch < humSource.pitch;
            float duration = Mathf.Max(0.01f, spinningDown ? shutdownDuration : startupDuration);

            // Pitch del hum: rampa nel tempo.
            float pitchSpan = Mathf.Max(0.01f, normalHumPitch - blackoutHumPitch);
            float pitchRate = pitchSpan / duration;
            humSource.pitch = Mathf.MoveTowards(humSource.pitch, targetHumPitch, pitchRate * Time.deltaTime);

            // Volume del hum: rampa SULLA STESSA durata → senti lo spegnimento/riavvio.
            float volSpan = Mathf.Max(0.01f, humVolume);
            float volRate = volSpan / duration;
            humSource.volume = Mathf.MoveTowards(humSource.volume, targetHumVolume, volRate * Time.deltaTime);
        }

        if (ventSource != null)
        {
            ventSource.volume = Mathf.MoveTowards(ventSource.volume, targetVentVolume, step);
        }

        HandleCreaks();
    }

    private void HandleCreaks()
    {
        if (creakSource == null || creakClips == null || creakClips.Length == 0) return;

        if (Time.time >= nextCreakTime)
        {
            AudioClip clip = creakClips[Random.Range(0, creakClips.Length)];
            creakSource.pitch = 1f + Random.Range(-creakPitchVariation, creakPitchVariation);
            creakSource.PlayOneShot(clip, creakVolume);
            ScheduleNextCreak();
        }
    }

    private void ScheduleNextCreak()
    {
        float min = creakIntervalMin;
        float max = creakIntervalMax;

        // Ship under stress creaks more often
        if (state != AmbientState.Normal)
        {
            min *= stressedIntervalMultiplier;
            max *= stressedIntervalMultiplier;
        }

        nextCreakTime = Time.time + Random.Range(min, max);
    }
}
