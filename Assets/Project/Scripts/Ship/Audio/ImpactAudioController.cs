using UnityEngine;
using UnityEngine.Audio;
using SpaceSurvivor.Ship;

/// <summary>
/// Audio one-shot degli impatti sulla nave (Rev AE, Blocco 3.2.d parte 2).
/// Singleton di scena sulla Nave (o su un GameObject figlio dedicato agli
/// SFX impatti).
///
/// PATTERN: copia il pattern architetturale di AlarmAudioController
/// (AudioMixer group + volume master), ma opera in modalità ONE-SHOT
/// invece di loop persistente. Canale semanticamente distinto dal klaxon
/// AlarmAudioController — QC-B confermata Rev AE:
///   - AlarmAudioController = loop persistente su severity AlarmSystem
///     (Warning/Critical → silenzio, Emergency → klaxon).
///   - ImpactAudioController = one-shot per impatto (light/medium/hard),
///     indipendente da AlarmSystem, non modifica lo stato di allarme
///     globale.
///
/// SEVERITY → CLIP:
///   Rev AE: 1 clip per severity (light/medium/hard). Se in playtest
///   emerge ripetitività su hit medium consecutivi, promuoveremo a array
///   [clip[]] con random pick (debito futuro, non D-numerato ancora).
///   Variazione pitch random ±5% applicata comunque per rompere loop
///   percettivi immediati.
///
/// MIXER:
///   AudioMixerGroup opzionale (SerializeField). Se assegnato,
///   AudioSource.outputAudioMixerGroup viene routato al mixer — utile per
///   controlli globali volume SFX in future opzioni di accessibility.
///   Se null, l'AudioSource suona diretto (fallback).
///
/// SPATIAL:
///   La Nave non si muove fisicamente (invariante progetto) → tutti gli
///   occupanti percepirebbero un audio 3D identico a un 2D. Impostiamo
///   spatialBlend = 0 (2D) per garantire volume percettivo costante
///   indipendentemente da dove si trova il player rispetto al GameObject
///   di questo controller (che potrebbe essere lontano dalla cockpit).
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class ImpactAudioController : MonoBehaviour
{
    // ── Singleton di scena ────────────────────────────────────────────────────
    // Non NetworkBehaviour: l'audio è client-side, ogni client ha la sua
    // istanza. Il fire da server passa per ClientRpc su ShipImpactHandler
    // che invoca Instance?.PlayImpact() localmente su ogni client.
    public static ImpactAudioController Instance { get; private set; }

    [Header("Audio Source")]
    [SerializeField] private AudioSource audioSource;

    [Header("Clips per severity")]
    [Tooltip("Clip riprodotta per impatti Light (bump lieve). Se null, viene " +
             "usata la clip Medium come fallback, poi Hard.")]
    [SerializeField] private AudioClip lightClip;

    [Tooltip("Clip riprodotta per impatti Medium (urto medio). Se null, " +
             "fallback su Hard, poi Light.")]
    [SerializeField] private AudioClip mediumClip;

    [Tooltip("Clip riprodotta per impatti Hard (collisione dura). Se null, " +
             "fallback su Medium, poi Light.")]
    [SerializeField] private AudioClip hardClip;

    [Header("Mixer routing (optional)")]
    [Tooltip("AudioMixerGroup dedicato agli SFX impatti. Lascia null per " +
             "routing diretto all'AudioListener (fallback semplice).")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    [Header("Random pitch variation")]
    [Tooltip("Variazione random ±X del pitch base per severity, per rompere " +
             "loop percettivi su hit ravvicinati. Default 0.05 = ±5%.")]
    [Range(0f, 0.20f)]
    [SerializeField] private float pitchRandomization = 0.05f;

    [Header("Debug")]
    [SerializeField] private bool logImpacts = false;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[ImpactAudioController] Istanza duplicata rilevata — distruggo.");
            Destroy(gameObject);
            return;
        }
        Instance = this;

        if (audioSource == null) audioSource = GetComponent<AudioSource>();

        // Configurazione fissa: la nave non si muove → 2D è percettivamente
        // identico a 3D per tutti gli occupanti, ed è più semplice.
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f;
        if (outputMixerGroup != null) audioSource.outputAudioMixerGroup = outputMixerGroup;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Riproduce l'audio one-shot corrispondente alla severity data.
    /// Invocata da ShipImpactHandler.PlayImpactFeedbackClientRpc su ogni
    /// client (server-authoritative fire).
    /// </summary>
    public void PlayImpact(ImpactSeverity severity)
    {
        AudioClip clip = SelectClip(severity);
        if (clip == null)
        {
            Debug.LogWarning($"[ImpactAudioController] Nessuna clip assegnata per " +
                             $"{ImpactThresholdTable.DebugLabel(severity)} né fallback disponibili.");
            return;
        }

        var audioParams = ImpactThresholdTable.GetAudioParams(severity);

        // Pitch random ±pitchRandomization intorno al pitch base della severity.
        // Applicato all'AudioSource stesso (non a PlayOneShot che non supporta
        // pitch per-invocazione). NB: se un secondo impatto arriva prima che il
        // primo finisca, il pitch cambia mid-play sul primo — trascurabile su
        // clip corte (~0.3-0.8s), accettato come compromesso di semplicità.
        float pitchJitter = Random.Range(-pitchRandomization, pitchRandomization);
        audioSource.pitch = audioParams.Pitch + pitchJitter;

        // PlayOneShot permette overlap: se hit multipli in rapida successione
        // (es. bounce contro POI), si sovrappongono naturalmente invece di
        // troncarsi come farebbe Play().
        audioSource.PlayOneShot(clip, audioParams.Volume);

        if (logImpacts)
        {
            Debug.Log($"[ImpactAudioController] Play {ImpactThresholdTable.DebugLabel(severity)} " +
                      $"vol={audioParams.Volume:F2} pitch={audioSource.pitch:F2}");
        }
    }

    private AudioClip SelectClip(ImpactSeverity severity)
    {
        switch (severity)
        {
            case ImpactSeverity.Hard:
                return hardClip != null ? hardClip
                     : (mediumClip != null ? mediumClip : lightClip);
            case ImpactSeverity.Medium:
                return mediumClip != null ? mediumClip
                     : (hardClip != null ? hardClip : lightClip);
            default:
                return lightClip != null ? lightClip
                     : (mediumClip != null ? mediumClip : hardClip);
        }
    }
}
