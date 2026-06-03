using UnityEngine;
using UnityEngine.Audio;
using System.Collections;

/// <summary>
/// Klaxon audio controller driven by AlarmSystem (Milestone 1B).
/// Loops a severity-specific clip and fades volume in/out through an AudioMixer
/// exposed parameter (in dB). Falls back to AudioSource.volume if no mixer is set.
///
/// Subscribes to AlarmSystem.OnAlarmStateChanged — no polling, no coupling.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AlarmAudioController : MonoBehaviour
{
    [Header("Audio Source")]
    [SerializeField] private AudioSource audioSource;

    [Header("Klaxon Clips (per severity, optional)")]
    [Tooltip("Se una clip manca, viene usata la più vicina disponibile.")]
    [SerializeField] private AudioClip warningClip;
    [SerializeField] private AudioClip criticalClip;
    [SerializeField] private AudioClip emergencyClip;

    [Header("Mixer Fade")]
    [Tooltip("AudioMixer con un parametro Volume esposto (dB). Lascia vuoto per usare AudioSource.volume.")]
    [SerializeField] private AudioMixer mixer;
    [Tooltip("Nome esatto del parametro esposto nel mixer (es. 'AlarmVolume').")]
    [SerializeField] private string volumeParameter = "AlarmVolume";
    [SerializeField] private float fadeInDuration = 0.5f;
    [SerializeField] private float fadeOutDuration = 1.2f;

    [Header("Volume / Pitch")]
    [Range(0f, 1f)]
    [Tooltip("Volume lineare target a regime (0..1).")]
    [SerializeField] private float targetVolume = 1f;
    [SerializeField] private float warningPitch = 0.9f;
    [SerializeField] private float criticalPitch = 1.0f;
    [SerializeField] private float emergencyPitch = 1.15f;

    private AlarmSystem alarmSystem;
    private Coroutine fadeRoutine;

    private const float MIN_DB = -80f;
    private const float MAX_DB = 0f;

    private void Awake()
    {
        if (audioSource == null) audioSource = GetComponent<AudioSource>();

        audioSource.loop = true;
        audioSource.playOnAwake = false;

        SetMixerVolumeLinear(0f); // start silent
        audioSource.spatialBlend = 1f; // forza 3D indipendentemente dalla configurazione in Inspector
    }

    private void Start()
    {
        alarmSystem = AlarmSystem.Instance;

        if (alarmSystem != null)
        {
            alarmSystem.OnAlarmStateChanged += HandleAlarmStateChanged;
            HandleAlarmStateChanged(alarmSystem.CurrentSeverity); // sync initial state
        }
        else
        {
            Debug.LogWarning("[AlarmAudioController] AlarmSystem not found");
        }
    }

    private void OnDestroy()
    {
        if (alarmSystem != null)
        {
            alarmSystem.OnAlarmStateChanged -= HandleAlarmStateChanged;
        }
    }

    private void HandleAlarmStateChanged(AlarmSystem.AlarmSeverity severity)
    {
        // Opzione A: audio solo in Emergency (blackout).
        // Warning e Critical → solo beacon visivi, atmosfera preservata.
        if (severity != AlarmSystem.AlarmSeverity.Emergency)
        {
            StartFade(0f, fadeOutDuration, stopAtEnd: true);
            return;
        }

        AudioClip clip = SelectClip(severity);
        audioSource.pitch = SelectPitch(severity);

        if (clip != null && audioSource.clip != clip)
        {
            audioSource.clip = clip;
            audioSource.Play();
        }
        else if (!audioSource.isPlaying)
        {
            audioSource.Play();
        }

        StartFade(targetVolume, fadeInDuration, stopAtEnd: false);
    }

    private AudioClip SelectClip(AlarmSystem.AlarmSeverity severity)
    {
        switch (severity)
        {
            case AlarmSystem.AlarmSeverity.Warning:
                return warningClip != null ? warningClip
                     : (criticalClip != null ? criticalClip : emergencyClip);

            case AlarmSystem.AlarmSeverity.Critical:
                return criticalClip != null ? criticalClip : emergencyClip;

            case AlarmSystem.AlarmSeverity.Emergency:
                return emergencyClip != null ? emergencyClip : criticalClip;

            default:
                return null;
        }
    }

    private float SelectPitch(AlarmSystem.AlarmSeverity severity)
    {
        switch (severity)
        {
            case AlarmSystem.AlarmSeverity.Warning:   return warningPitch;
            case AlarmSystem.AlarmSeverity.Critical:  return criticalPitch;
            case AlarmSystem.AlarmSeverity.Emergency: return emergencyPitch;
            default:                                  return 1f;
        }
    }

    private void StartFade(float toLinearVolume, float duration, bool stopAtEnd)
    {
        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(FadeRoutine(toLinearVolume, duration, stopAtEnd));
    }

    private IEnumerator FadeRoutine(float toLinear, float duration, bool stopAtEnd)
    {
        float fromDb = GetMixerVolumeDb();
        float toDb = LinearToDb(toLinear);

        if (duration <= 0f)
        {
            SetMixerVolumeDb(toDb);
        }
        else
        {
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime; // works while paused / time-scaled
                SetMixerVolumeDb(Mathf.Lerp(fromDb, toDb, t / duration));
                yield return null;
            }
            SetMixerVolumeDb(toDb);
        }

        if (stopAtEnd && audioSource.isPlaying)
        {
            audioSource.Stop();
        }

        fadeRoutine = null;
    }

    // ===== Mixer helpers (fallback to AudioSource.volume if no mixer assigned) =====

    private void SetMixerVolumeLinear(float linear) => SetMixerVolumeDb(LinearToDb(linear));

    private void SetMixerVolumeDb(float db)
    {
        if (mixer != null && !string.IsNullOrEmpty(volumeParameter))
        {
            mixer.SetFloat(volumeParameter, db);
        }
        else
        {
            audioSource.volume = DbToLinear(db);
        }
    }

    private float GetMixerVolumeDb()
    {
        if (mixer != null && !string.IsNullOrEmpty(volumeParameter) &&
            mixer.GetFloat(volumeParameter, out float db))
        {
            return db;
        }
        return LinearToDb(audioSource.volume);
    }

    private static float LinearToDb(float linear)
    {
        return linear <= 0.0001f ? MIN_DB : Mathf.Clamp(Mathf.Log10(linear) * 20f, MIN_DB, MAX_DB);
    }

    private static float DbToLinear(float db)
    {
        return db <= MIN_DB ? 0f : Mathf.Pow(10f, db / 20f);
    }
}
