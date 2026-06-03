using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>
/// Central alarm coordinator (Milestone 1B).
/// SAFETY-CRITICAL: intentionally NOT an IPowerConsumer — the alarm must work
/// even during blackout, so it is never registered with PowerManager / load shedding.
///
/// Usa PowerManager.OnInstanceReady per gestire l'ordine di spawn NGO.
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
    [SerializeField] private bool reactToPowerCritical = true;
    [SerializeField] private bool reactToBlackout = true;

    private readonly Dictionary<AlarmSource, AlarmSeverity> activeAlarms =
        new Dictionary<AlarmSource, AlarmSeverity>();

    private AlarmSeverity currentSeverity = AlarmSeverity.None;

    public AlarmSeverity CurrentSeverity => currentSeverity;
    public bool IsAlarmActive => currentSeverity != AlarmSeverity.None;

    public event Action<AlarmSeverity> OnAlarmStateChanged;

    private PowerManager powerManager;

    private void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
        {
            Destroy(gameObject);
            return;
        }
    }

    private void Start()
    {
        if (PowerManager.Instance != null)
            InitWithPowerManager();
        else
            PowerManager.OnInstanceReady += InitWithPowerManager;
    }

    private void InitWithPowerManager()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        powerManager = PowerManager.Instance;
        powerManager.OnBlackout += HandleBlackout;
        powerManager.OnPowerRestored += HandlePowerRestored;
        powerManager.OnPowerLevelChanged += HandlePowerLevelChanged;
    }

    private void OnDestroy()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;

        if (powerManager != null)
        {
            powerManager.OnBlackout -= HandleBlackout;
            powerManager.OnPowerRestored -= HandlePowerRestored;
            powerManager.OnPowerLevelChanged -= HandlePowerLevelChanged;
        }

        if (Instance == this) Instance = null;
    }

    // ===== PUBLIC API =====

    public void RaiseAlarm(AlarmSource source, AlarmSeverity severity)
    {
        if (severity == AlarmSeverity.None)
        {
            ClearAlarm(source);
            return;
        }

        if (activeAlarms.TryGetValue(source, out AlarmSeverity existing) && existing == severity)
            return;

        activeAlarms[source] = severity;
        RecomputeSeverity();
    }

    public void ClearAlarm(AlarmSource source)
    {
        if (activeAlarms.Remove(source))
            RecomputeSeverity();
    }

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
            if (kv.Value > highest) highest = kv.Value;
        }

        if (highest != currentSeverity)
        {
            currentSeverity = highest;
            OnAlarmStateChanged?.Invoke(currentSeverity);
        }
    }

    // ===== POWER TRIGGERS =====

    private void HandleBlackout()
    {
        if (reactToBlackout)
            RaiseAlarm(AlarmSource.PowerBlackout, AlarmSeverity.Emergency);
    }

    private void HandlePowerRestored()
    {
        ClearAlarm(AlarmSource.PowerBlackout);
        ClearAlarm(AlarmSource.PowerCritical);
    }

    private void HandlePowerLevelChanged(float percent)
    {
        if (!reactToPowerCritical || powerManager == null) return;
        if (powerManager.IsInBlackout) return;

        if (powerManager.IsInCriticalState)
            RaiseAlarm(AlarmSource.PowerCritical, AlarmSeverity.Warning);
        else
            ClearAlarm(AlarmSource.PowerCritical);
    }

    // ===== FUTURE SYSTEM HOOKS (Milestone 2) =====
    //
    // dipende da: OxygenSystem (M2)
    //   Quando O₂ < 20%:   AlarmSystem.Instance?.RaiseAlarm(AlarmSource.OxygenLow, AlarmSeverity.Emergency);
    //   Quando O₂ rientra: AlarmSystem.Instance?.ClearAlarm(AlarmSource.OxygenLow);
    //
    // dipende da: HullSystem (M2)
    //   Quando Hull critico:  AlarmSystem.Instance?.RaiseAlarm(AlarmSource.HullCritical, AlarmSeverity.Critical);
    //   Quando Hull riparato: AlarmSystem.Instance?.ClearAlarm(AlarmSource.HullCritical);
}