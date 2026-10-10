using UnityEngine;
using System;
using System.Collections.Generic;
using SpaceSurvivor.Ship;

/// <summary>
/// Central alarm coordinator (Milestone 1B).
/// Intentionally NOT an IPowerConsumer — it is never registered with PowerManager / load shedding.
///
/// Usa PowerManager.OnInstanceReady per gestire l'ordine di spawn NGO.
/// Active triggers: critical power (Warning), OxygenSystem (OxygenLow, Emergency), HullSystem
/// (HullCritical, Critical).
///
/// BLACKOUT SILENZIOSO (Rev BY · Q140-a — cambia il design di M1B): prima il blackout alzava
/// PowerBlackout (Emergency) e l'allarme era "safety-critical, funziona anche in blackout". Ora:
///   - il blackout non alza nessun allarme;
///   - finché il blackout dura, ai listener (WarningBeacon, AlarmAudioController) arriva la gravità
///     None: niente sirena né lampeggianti, restano solo gli scricchiolii di AmbientShipAudio;
///   - gli allarmi attivi restano registrati (RegisteredSeverity) e al ritorno della corrente si
///     comunica lo stato reale: con l'O₂ ancora basso la sirena riparte.
/// CurrentSeverity è la gravità comunicata ai listener (None in blackout). L'avviso di energia bassa
/// (PowerCritical, Warning) resta com'è: lampeggianti senza sirena.
///
/// O₂ E SCAFO SU OGNI CLIENT (Rev BY · Q141-a): OxygenSystem e HullSystem alzano i loro allarmi solo
/// sul server, quindi prima l'AlarmSystem dei client vedeva solo gli allarmi di energia: un client non
/// sentiva la sirena di O₂ basso né vedeva i lampeggianti di scafo critico. Ora ogni AlarmSystem legge
/// lo stato replicato: OxygenSystem.OnAlarmStateChanged / IsAlarmActive e HullSystem.OnHullAlarmChanged
/// / IsHullAlarmActive (NetworkVariable). Sul server arrivano sia la chiamata diretta sia l'evento:
/// RaiseAlarm e ClearAlarm sono idempotenti. La gravità di O₂ e scafo è la stessa delle chiamate dirette.
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
        PowerBlackout,  // Rev BY (Q140-a): non più alzato — il blackout zittisce gli allarmi. Resta per stabilità dei valori.
        OxygenLow,      // OxygenSystem (server) + stato replicato (Q141-a)
        HullCritical    // HullSystem (server) + stato replicato (Q141-a)
    }

    // Gravità di O₂ e scafo ricavate dallo stato replicato (Q141-a). Uguali alle chiamate dirette
    // in OxygenSystem.CheckAlarmThresholds e HullSystem.UpdateHullAlarm.
    private const AlarmSeverity OxygenLowSeverity = AlarmSeverity.Emergency;
    private const AlarmSeverity HullCriticalSeverity = AlarmSeverity.Critical;

    public static AlarmSystem Instance { get; private set; }

    [Header("Power Triggers (available now)")]
    [SerializeField] private bool reactToPowerCritical = true;

    // Rev BY (Q140-a): rimosso il campo reactToBlackout — il blackout non alza più allarmi, quindi non
    // c'è niente da abilitare. In Game.unity la riga "reactToBlackout: 1" resta finché la scena non viene
    // risalvata: Unity la ignora.

    private readonly Dictionary<AlarmSource, AlarmSeverity> activeAlarms =
        new Dictionary<AlarmSource, AlarmSeverity>();

    // Rev BY (Q140-a): registeredSeverity = la più alta fra gli allarmi attivi; currentSeverity =
    // quella comunicata ai listener (None durante il blackout).
    private AlarmSeverity registeredSeverity = AlarmSeverity.None;
    private AlarmSeverity currentSeverity = AlarmSeverity.None;
    private bool silencedByBlackout;

    /// <summary>Gravità comunicata ai listener. Rev BY (Q140-a): None durante il blackout.</summary>
    public AlarmSeverity CurrentSeverity => currentSeverity;

    /// <summary>Rev BY (Q140-a) — gravità più alta fra gli allarmi registrati, anche in blackout.</summary>
    public AlarmSeverity RegisteredSeverity => registeredSeverity;

    /// <summary>Rev BY (Q140-a) — true mentre il blackout zittisce gli allarmi.</summary>
    public bool IsSilencedByBlackout => silencedByBlackout;

    public bool IsAlarmActive => currentSeverity != AlarmSeverity.None;

    public event Action<AlarmSeverity> OnAlarmStateChanged;

    private PowerManager powerManager;
    private OxygenSystem oxygenSystem;   // Rev BY (Q141-a)
    private HullSystem hullSystem;       // Rev BY (Q141-a)

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

        // Rev BY (Q141-a) — O₂ e scafo dallo stato replicato, su ogni client.
        if (OxygenSystem.Instance != null)
            InitWithOxygenSystem();
        else
            OxygenSystem.OnInstanceReady += InitWithOxygenSystem;

        if (HullSystem.Instance != null)
            InitWithHullSystem();
        else
            HullSystem.OnInstanceReady += InitWithHullSystem;
    }

    private void InitWithPowerManager()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        powerManager = PowerManager.Instance;
        if (powerManager == null) return;

        powerManager.OnBlackout += HandleBlackout;
        powerManager.OnPowerRestored += HandlePowerRestored;
        powerManager.OnPowerLevelChanged += HandlePowerLevelChanged;

        // Rev BY (Q140-a): chi entra durante un blackout parte già zittito.
        SetBlackoutSilence(powerManager.IsInBlackout);
    }

    private void InitWithOxygenSystem()
    {
        OxygenSystem.OnInstanceReady -= InitWithOxygenSystem;
        if (oxygenSystem != null) return;

        oxygenSystem = OxygenSystem.Instance;
        if (oxygenSystem == null) return;

        oxygenSystem.OnAlarmStateChanged += HandleOxygenAlarmChanged;
        HandleOxygenAlarmChanged(oxygenSystem.IsAlarmActive);   // stato attuale (anche per chi entra tardi)
    }

    private void InitWithHullSystem()
    {
        HullSystem.OnInstanceReady -= InitWithHullSystem;
        if (hullSystem != null) return;

        hullSystem = HullSystem.Instance;
        if (hullSystem == null) return;

        hullSystem.OnHullAlarmChanged += HandleHullAlarmChanged;
        HandleHullAlarmChanged(hullSystem.IsHullAlarmActive);   // stato attuale (anche per chi entra tardi)
    }

    private void OnDestroy()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        OxygenSystem.OnInstanceReady -= InitWithOxygenSystem;
        HullSystem.OnInstanceReady -= InitWithHullSystem;

        if (powerManager != null)
        {
            powerManager.OnBlackout -= HandleBlackout;
            powerManager.OnPowerRestored -= HandlePowerRestored;
            powerManager.OnPowerLevelChanged -= HandlePowerLevelChanged;
        }

        // Rev BY (Q141-a). ReferenceEquals: l'istanza può essere già distrutta (chiusura della scena),
        // ma togliere l'handler dall'evento C# resta corretto.
        if (!ReferenceEquals(oxygenSystem, null))
            oxygenSystem.OnAlarmStateChanged -= HandleOxygenAlarmChanged;

        if (!ReferenceEquals(hullSystem, null))
            hullSystem.OnHullAlarmChanged -= HandleHullAlarmChanged;

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

        registeredSeverity = highest;
        PublishSeverity();
    }

    /// <summary>
    /// Rev BY (Q140-a) — comunica ai listener la gravità effettiva: None in blackout, altrimenti la
    /// più alta registrata. Solo se cambia.
    /// </summary>
    private void PublishSeverity()
    {
        AlarmSeverity effective = silencedByBlackout ? AlarmSeverity.None : registeredSeverity;

        if (effective != currentSeverity)
        {
            currentSeverity = effective;
            OnAlarmStateChanged?.Invoke(currentSeverity);
        }
    }

    private void SetBlackoutSilence(bool silenced)
    {
        if (silencedByBlackout == silenced) return;
        silencedByBlackout = silenced;
        PublishSeverity();
    }

    // ===== POWER TRIGGERS =====

    private void HandleBlackout()
    {
        // Rev BY (Q140-a): il blackout zittisce gli allarmi invece di alzarne uno.
        // Prima: if (reactToBlackout) RaiseAlarm(AlarmSource.PowerBlackout, AlarmSeverity.Emergency);
        SetBlackoutSilence(true);
    }

    private void HandlePowerRestored()
    {
        // Prima si puliscono gli allarmi di energia (ancora zittiti), poi si toglie il silenzio:
        // i listener ricevono una sola notifica, con lo stato reale.
        ClearAlarm(AlarmSource.PowerBlackout);   // difensivo: da Rev BY non viene più alzato
        ClearAlarm(AlarmSource.PowerCritical);
        SetBlackoutSilence(false);
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

    // ===== O₂ E SCAFO DALLO STATO REPLICATO (Rev BY · Q141-a) =====

    private void HandleOxygenAlarmChanged(bool active)
    {
        if (active) RaiseAlarm(AlarmSource.OxygenLow, OxygenLowSeverity);
        else ClearAlarm(AlarmSource.OxygenLow);
    }

    private void HandleHullAlarmChanged(bool active)
    {
        if (active) RaiseAlarm(AlarmSource.HullCritical, HullCriticalSeverity);
        else ClearAlarm(AlarmSource.HullCritical);
    }
}