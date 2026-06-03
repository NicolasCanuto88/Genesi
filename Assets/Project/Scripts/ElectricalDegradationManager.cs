using UnityEngine;
using System;

/// <summary>
/// Electrical degradation manager (GDD 9.7), Milestone 1B.
/// Effective light consumption = base × hull × em × ballast (see ShipLight.GetPowerDemand).
///
/// M1B default: degradation is INERT — every multiplier is ×1.0 and the internal
/// ballast timer is paused until EnableDegradation(true) is called. No safety clamp:
/// GetTotalMultiplier() returns the raw GDD product (blackout-prone by design).
///
/// hull / em are driven by future systems (HullSystem, ZoneManager — M2) via the
/// public setters. ballast degrades on an internal timer once a random fault occurs.
/// </summary>
public class ElectricalDegradationManager : MonoBehaviour
{
    public enum BallastState { Integro, Lieve, Medio, Avanzato }

    public enum EMIntensity { None, Weak, Moderate, Strong, Extreme }

    public static ElectricalDegradationManager Instance { get; private set; }

    [Header("Master Switch")]
    [Tooltip("M1B: false = degrado inerte (timer ballast fermo). Attivalo per testare il degrado.")]
    [SerializeField] private bool degradationEnabled = false;

    [Header("Ballast Fault Probability (GDD 9.7)")]
    [Tooltip("Probabilità base di guasto ballast per ora di gioco attiva (0.005 = 0.5%).")]
    [SerializeField] private float baseFaultChancePerHour = 0.005f;
    [Tooltip("Bonus cumulativo aggiunto dopo ogni blackout (0.01 = +1%).")]
    [SerializeField] private float blackoutFaultBonusPerEvent = 0.01f;
    [Tooltip("Ogni quanti secondi si tira il dado per il guasto.")]
    [SerializeField] private float faultRollInterval = 60f;

    // ===== Multipliers (private state) =====
    private float hullMultiplier = 1f;   // dipende da: HullSystem (M2)
    private float emMultiplier = 1f;     // dipende da: ZoneManager (M2)
    private float ballastMultiplier = 1f; // internal timer

    private float hullPercent = 100f;    // proxy for the ×2 fault chance below 25%

    // ===== Ballast internal state =====
    private bool ballastFaulted = false;
    private float ballastFaultElapsedSeconds = 0f;
    private float faultRollTimer = 0f;
    private float blackoutFaultBonus = 0f;
    private BallastState ballastState = BallastState.Integro;

    // ===== Public read-only API (for Monitor 1/2 diagnostics) =====
    public float HullMultiplier => hullMultiplier;
    public float EMMultiplier => emMultiplier;
    public float BallastMultiplier => ballastMultiplier;
    public bool IsBallastDamaged => ballastFaulted;
    public BallastState CurrentBallastState => ballastState;
    public bool IsDegradationEnabled => degradationEnabled;

    /// <summary>Fired when the total multiplier changes (for UI indicators).</summary>
    public event Action<float> OnDegradationChanged;

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
        }
    }

    private void OnDestroy()
    {
        if (powerManager != null)
        {
            powerManager.OnBlackout -= HandleBlackout;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Update()
    {
        if (!degradationEnabled) return;

        if (ballastFaulted)
        {
            UpdateBallastTier();
        }
        else
        {
            RollForBallastFault();
        }
    }

    // ===== TOTAL MULTIPLIER (no clamp, by design) =====

    /// <summary>
    /// Product of the three multipliers. Intentionally NOT clamped (GDD-pure values).
    /// </summary>
    public float GetTotalMultiplier()
    {
        return hullMultiplier * emMultiplier * ballastMultiplier;
    }

    // ===== BALLAST TIMER (internal) =====

    private void RollForBallastFault()
    {
        faultRollTimer += Time.deltaTime;
        if (faultRollTimer < faultRollInterval) return;
        faultRollTimer = 0f;

        float perHour = baseFaultChancePerHour + blackoutFaultBonus;
        if (hullPercent < 25f) perHour *= 2f; // GDD: Hull <25% raddoppia la probabilità

        float perRoll = perHour * (faultRollInterval / 3600f);
        if (UnityEngine.Random.value < perRoll)
        {
            TriggerBallastFault();
        }
    }

    private void TriggerBallastFault()
    {
        ballastFaulted = true;
        ballastFaultElapsedSeconds = 0f;
        SetBallast(BallastState.Lieve, 1.12f);
        Debug.LogWarning("[ElectricalDegradation] Guasto ballast! Degrado lieve (×1.12)");
    }

    private void UpdateBallastTier()
    {
        ballastFaultElapsedSeconds += Time.deltaTime;
        float minutes = ballastFaultElapsedSeconds / 60f;

        if (minutes < 60f)
        {
            SetBallast(BallastState.Lieve, 1.12f);
        }
        else if (minutes < 120f)
        {
            SetBallast(BallastState.Medio, 1.28f);
        }
        else
        {
            SetBallast(BallastState.Avanzato, 1.50f);
        }
    }

    private void SetBallast(BallastState state, float multiplier)
    {
        bool changed = !Mathf.Approximately(ballastMultiplier, multiplier) || ballastState != state;
        ballastState = state;
        ballastMultiplier = multiplier;
        if (changed) OnDegradationChanged?.Invoke(GetTotalMultiplier());
    }

    // ===== PUBLIC HOOKS =====

    /// <summary>Attiva/disattiva il degrado interno (timer ballast).</summary>
    public void EnableDegradation(bool enabled)
    {
        degradationEnabled = enabled;
    }

    /// <summary>
    /// Ripara il ballast (GDD: 1× Electronic Component + 2× Wire Bundle, 45s).
    /// Riporta il ballast a Integro ×1.0. Il bonus probabilità da blackout resta cumulato.
    /// </summary>
    public void RepairBallast()
    {
        ballastFaulted = false;
        ballastFaultElapsedSeconds = 0f;
        faultRollTimer = 0f;
        SetBallast(BallastState.Integro, 1f);
        Debug.Log("[ElectricalDegradation] Ballast riparato (×1.0)");
    }

    // dipende da: HullSystem (M2) — chiamare quando l'Hull HP cambia.
    /// <summary>Aggiorna il moltiplicatore hull dalla percentuale HP scafo (GDD 9.7 tabella).</summary>
    public void SetHullPercent(float percent)
    {
        hullPercent = Mathf.Clamp(percent, 0f, 100f);

        float m;
        if (hullPercent >= 75f) m = 1.0f;
        else if (hullPercent >= 50f) m = 1.15f;
        else if (hullPercent >= 25f) m = 1.30f;
        else if (hullPercent >= 10f) m = 1.50f;
        else m = 1.75f;

        if (!Mathf.Approximately(hullMultiplier, m))
        {
            hullMultiplier = m;
            OnDegradationChanged?.Invoke(GetTotalMultiplier());
        }
    }

    // dipende da: ZoneManager (M2) — chiamare su cambio zona EM.
    /// <summary>Imposta il moltiplicatore EM dall'intensità di zona (GDD 9.7 tabella).</summary>
    public void SetEMIntensity(EMIntensity intensity)
    {
        float m;
        switch (intensity)
        {
            case EMIntensity.Weak:     m = 1.10f; break;
            case EMIntensity.Moderate: m = 1.25f; break;
            case EMIntensity.Strong:   m = 1.45f; break;
            case EMIntensity.Extreme:  m = 1.80f; break;
            default:                   m = 1.0f;  break;
        }

        if (!Mathf.Approximately(emMultiplier, m))
        {
            emMultiplier = m;
            OnDegradationChanged?.Invoke(GetTotalMultiplier());
        }
    }

    /// <summary>Diagnostica testuale per Monitor 2 (GDD 9.3 sezione C).</summary>
    public string GetDiagnosticsSummary()
    {
        string ballast = ballastFaulted
            ? $"DEGRADED [{ballastState}] ×{ballastMultiplier:0.00}"
            : "OK ×1.00";

        return $"HULL ×{hullMultiplier:0.00} · EM ×{emMultiplier:0.00} · BALLAST {ballast}\n" +
               $"TOTALE ×{GetTotalMultiplier():0.00}";
    }

    // ===== POWER EVENTS =====

    private void HandleBlackout()
    {
        // GDD: dopo ogni blackout +1.0% cumulativo alla probabilità di guasto ballast.
        blackoutFaultBonus += blackoutFaultBonusPerEvent;
    }
}
