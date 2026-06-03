using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>
/// Central alarm coordinator (Milestone 1B).
/// SAFETY-CRITICAL: intentionally NOT an IPowerConsumer — the alarm must work
/// even during blackout, so it is never registered with PowerManager / load shedding.
///
/// Decoupled by design: subscribers (WarningBeacon, AlarmAudioController, future UI)
/// listen to OnAlarmStateChanged. Other ship systems raise/clear alarms through the
/// public API — no GetComponent chains.
///
/// Active triggers: PowerManager blackout + critical power.
/// Placeholder hooks (Milestone 2): OxygenSystem, HullSystem.
/// </summary>
public class AlarmSystem : MonoBehaviour
{
    public enum AlarmSeverity
    {
        None = 0,
        Warning = 1,
        Critical = 2,
        Emergency = 3
    }

    public enum AlarmSource
    {
        PowerCritical,  // active now
        PowerBlackout,  // active now
        OxygenLow,      // dipende da: OxygenSystem (M2)
        HullCritical    // dipende da: HullSystem (M2)
    }

    public static AlarmSystem Instance { get; private set; }

    [Header("Power Triggers (available now)")]
    [Tooltip("Se attivo, l'allarme reagisce al critical power di PowerManager (severità Warning).")]
    [SerializeField] private bool reactToPowerCritical = true;
    [Tooltip("Se attivo, l'allarme reagisce al blackout di PowerManager (severità Emergency).")]
    [SerializeField] private bool reactToBlackout = true;

    // Active alarms keyed by source; highest severity wins.
    private readonly Dictionary<AlarmSource, AlarmSeverity> activeAlarms =
        new Dictionary<AlarmSource, AlarmSeverity>();

    private AlarmSeverity currentSeverity = AlarmSeverity.None;

    /// <summary>Highest currently-active severity (None if no alarm).</summary>
    public AlarmSeverity CurrentSeverity => currentSeverity;
    public bool IsAlarmActive => currentSeverity != AlarmSeverity.None;

    /// <summary>Fired only when the highest active severity actually changes.</summary>
    public event Action<AlarmSeverity> OnAlarmStateChanged;

    private PowerManager powerManager;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
            return;
        }
    }

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
            Debug.LogWarning("[AlarmSystem] PowerManager not found - power triggers disabled");
        }
    }

    private void OnDestroy()
    {
        if (powerManager != null)
        {
            powerManager.OnBlackout -= HandleBlackout;
            powerManager.OnPowerRestored -= HandlePowerRestored;
            powerManager.OnPowerLevelChanged -= HandlePowerLevelChanged;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

    // ===== PUBLIC API (used now + by future systems) =====

    /// <summary>
    /// Raise (or upgrade) an alarm for a given source. Idempotent: re-raising the
    /// same severity does nothing. Passing None clears the source instead.
    /// </summary>
    public void RaiseAlarm(AlarmSource source, AlarmSeverity severity)
    {
        if (severity == AlarmSeverity.None)
        {
            ClearAlarm(source);
            return;
        }

        if (activeAlarms.TryGetValue(source, out AlarmSeverity existing) && existing == severity)
        {
            return; // no change
        }

        activeAlarms[source] = severity;
        RecomputeSeverity();
    }

    /// <summary>Clear the alarm for a given source.</summary>
    public void ClearAlarm(AlarmSource source)
    {
        if (activeAlarms.Remove(source))
        {
            RecomputeSeverity();
        }
    }

    /// <summary>Clear every active alarm (e.g. mission end / safe state).</summary>
    public void ClearAllAlarms()
    {
        if (activeAlarms.Count == 0) return;

        activeAlarms.Clear();
        RecomputeSeverity();
    }

    private void RecomputeSeverity()
    {
        AlarmSeverity highest = AlarmSeverity.None;

        foreach (var kv in activeAlarms)
        {
            if (kv.Value > highest)
            {
                highest = kv.Value;
            }
        }

        if (highest != currentSeverity)
        {
            currentSeverity = highest;
            OnAlarmStateChanged?.Invoke(currentSeverity);
        }
    }

    // ===== POWER TRIGGERS (available now) =====

    private void HandleBlackout()
    {
        if (reactToBlackout)
        {
            RaiseAlarm(AlarmSource.PowerBlackout, AlarmSeverity.Emergency);
        }
    }

    private void HandlePowerRestored()
    {
        ClearAlarm(AlarmSource.PowerBlackout);
        ClearAlarm(AlarmSource.PowerCritical);
    }

    private void HandlePowerLevelChanged(float percent)
    {
        if (!reactToPowerCritical || powerManager == null) return;

        // Blackout owns the higher-severity alarm; don't double-handle here.
        if (powerManager.IsInBlackout) return;

        if (powerManager.IsInCriticalState)
        {
            RaiseAlarm(AlarmSource.PowerCritical, AlarmSeverity.Warning);
        }
        else
        {
            ClearAlarm(AlarmSource.PowerCritical);
        }
    }

    // ===== FUTURE SYSTEM HOOKS (Milestone 2) =====
    //
    // dipende da: OxygenSystem (M2)
    //   Quando O₂ < 20% (GDD 9.4):  AlarmSystem.Instance?.RaiseAlarm(AlarmSource.OxygenLow, AlarmSeverity.Emergency);
    //   Quando O₂ rientra:          AlarmSystem.Instance?.ClearAlarm(AlarmSource.OxygenLow);
    //
    // dipende da: HullSystem (M2)
    //   Quando Hull critico:        AlarmSystem.Instance?.RaiseAlarm(AlarmSource.HullCritical, AlarmSeverity.Critical);
    //   Quando Hull riparato:       AlarmSystem.Instance?.ClearAlarm(AlarmSource.HullCritical);
}
