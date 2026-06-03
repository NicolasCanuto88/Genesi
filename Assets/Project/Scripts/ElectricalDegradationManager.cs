using UnityEngine;
using Unity.Netcode;
using System;

/// <summary>
/// Electrical degradation manager (GDD 9.7), Milestone 1B — NGO v2 NetworkBehaviour.
/// Effective light consumption = base × hull × em × ballast (see ShipLight.GetPowerDemand).
///
/// Authority: Server only. I moltiplicatori sono NetworkVariable — tutti i client
/// leggono valori aggiornati senza polling. OnDegradationChanged è fired localmente
/// su ogni client tramite NetworkVariable.OnValueChanged.
///
/// M1B default: degradationEnabled = false → timer ballast fermo, ogni moltiplicatore ×1.0.
/// Nessun clamp su GetTotalMultiplier() — valori GDD puri, blackout-prone by design.
///
/// hull / em guidati da HullSystem / ZoneManager (M2) via setter pubblici (ServerRpc).
/// </summary>
public class ElectricalDegradationManager : NetworkBehaviour
{
    public enum BallastState { Integro, Lieve, Medio, Avanzato }
    public enum EMIntensity { None, Weak, Moderate, Strong, Extreme }

    public static ElectricalDegradationManager Instance { get; private set; }
    public static event Action OnInstanceReady;

    [Header("Master Switch")]
    [Tooltip("M1B: false = degrado inerte. Attivalo per testare il degrado.")]
    [SerializeField] private bool degradationEnabled = false;

    [Header("Ballast Fault Probability (GDD 9.7)")]
    [SerializeField] private float baseFaultChancePerHour = 0.005f;
    [SerializeField] private float blackoutFaultBonusPerEvent = 0.01f;
    [SerializeField] private float faultRollInterval = 60f;

    // ===== NetworkVariables (server scrive, tutti leggono) =====
    private NetworkVariable<float> netHullMultiplier = new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netEMMultiplier = new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netBallastMultiplier = new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netBallastFaulted = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<int> netBallastState = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netBlackoutFaultBonus = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ===== Stato server-only =====
    private float hullPercent = 100f;
    private float ballastFaultElapsedSeconds = 0f;
    private float faultRollTimer = 0f;

    // ===== Public read-only API (leggono NetworkVariable — safe da tutti i client) =====
    public float HullMultiplier => netHullMultiplier.Value;
    public float EMMultiplier => netEMMultiplier.Value;
    public float BallastMultiplier => netBallastMultiplier.Value;
    public bool IsBallastDamaged => netBallastFaulted.Value;
    public BallastState CurrentBallastState => (BallastState)netBallastState.Value;
    public bool IsDegradationEnabled => degradationEnabled;

    /// <summary>Fired su tutti i client quando il moltiplicatore totale cambia.</summary>
    public event Action<float> OnDegradationChanged;

    private PowerManager powerManager;

    // ===== NGO Lifecycle =====

    public override void OnNetworkSpawn()
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

        // Tutti i client reagiscono ai cambi di moltiplicatore
        netHullMultiplier.OnValueChanged += (_, __) => OnDegradationChanged?.Invoke(GetTotalMultiplier());
        netEMMultiplier.OnValueChanged += (_, __) => OnDegradationChanged?.Invoke(GetTotalMultiplier());
        netBallastMultiplier.OnValueChanged += (_, __) => OnDegradationChanged?.Invoke(GetTotalMultiplier());

        if (PowerManager.Instance != null)
            InitWithPowerManager();
        else
            PowerManager.OnInstanceReady += InitWithPowerManager;

        OnInstanceReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;

        if (powerManager != null)
            powerManager.OnBlackout -= HandleBlackout;

        if (Instance == this) Instance = null;
    }

    private void InitWithPowerManager()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        powerManager = PowerManager.Instance;
        if (powerManager != null)
            powerManager.OnBlackout += HandleBlackout;
    }

    // ===== Update (solo server) =====

    private void Update()
    {
        if (!IsServer) return;
        if (!degradationEnabled) return;

        if (netBallastFaulted.Value)
            UpdateBallastTier();
        else
            RollForBallastFault();
    }

    // ===== TOTAL MULTIPLIER =====

    /// <summary>
    /// Prodotto dei tre moltiplicatori. Intentionally NOT clamped (GDD-pure).
    /// Safe da chiamare su qualsiasi client — legge NetworkVariable.
    /// </summary>
    public float GetTotalMultiplier()
    {
        return netHullMultiplier.Value * netEMMultiplier.Value * netBallastMultiplier.Value;
    }

    // ===== BALLAST TIMER (server only) =====

    private void RollForBallastFault()
    {
        faultRollTimer += Time.deltaTime;
        if (faultRollTimer < faultRollInterval) return;
        faultRollTimer = 0f;

        float perHour = baseFaultChancePerHour + netBlackoutFaultBonus.Value;
        if (hullPercent < 25f) perHour *= 2f;

        float perRoll = perHour * (faultRollInterval / 3600f);
        if (UnityEngine.Random.value < perRoll)
            TriggerBallastFault();
    }

    private void TriggerBallastFault()
    {
        netBallastFaulted.Value = true;
        ballastFaultElapsedSeconds = 0f;
        SetBallast(BallastState.Lieve, 1.12f);
        Debug.LogWarning("[ElectricalDegradation] Guasto ballast! Degrado lieve (×1.12)");
    }

    private void UpdateBallastTier()
    {
        ballastFaultElapsedSeconds += Time.deltaTime;
        float minutes = ballastFaultElapsedSeconds / 60f;

        if (minutes < 60f) SetBallast(BallastState.Lieve, 1.12f);
        else if (minutes < 120f) SetBallast(BallastState.Medio, 1.28f);
        else SetBallast(BallastState.Avanzato, 1.50f);
    }

    private void SetBallast(BallastState state, float multiplier)
    {
        bool changed = !Mathf.Approximately(netBallastMultiplier.Value, multiplier)
                    || (BallastState)netBallastState.Value != state;

        netBallastState.Value = (int)state;
        netBallastMultiplier.Value = multiplier;

        // OnDegradationChanged è fired automaticamente da OnValueChanged su netBallastMultiplier
        // Solo logghiamo se c'è un cambio di tier
        if (changed)
            Debug.Log($"[ElectricalDegradation] Ballast → {state} ×{multiplier:0.00}");
    }

    // ===== PUBLIC HOOKS =====

    /// <summary>Attiva/disattiva il degrado (timer ballast). Solo server.</summary>
    public void EnableDegradation(bool enabled)
    {
        if (!IsServer) return;
        degradationEnabled = enabled;
    }

    /// <summary>
    /// Ripara il ballast (GDD: 1× Electronic Component + 2× Wire Bundle, 45s).
    /// Riporta il ballast a Integro ×1.0. Il bonus probabilità da blackout resta cumulato.
    /// Chiamabile da qualsiasi client — eseguito sul server via Rpc.
    /// </summary>
    public void RepairBallast()
    {
        if (IsServer)
            RepairBallastInternal();
        else
            RepairBallastRpc();
    }

    [Rpc(SendTo.Server)]
    private void RepairBallastRpc()
    {
        RepairBallastInternal();
    }

    private void RepairBallastInternal()
    {
        netBallastFaulted.Value = false;
        ballastFaultElapsedSeconds = 0f;
        faultRollTimer = 0f;
        SetBallast(BallastState.Integro, 1f);
        Debug.Log("[ElectricalDegradation] Ballast riparato (×1.0)");
    }

    // dipende da: HullSystem (M2)
    /// <summary>Aggiorna il moltiplicatore hull. Chiamabile da qualsiasi client.</summary>
    public void SetHullPercent(float percent)
    {
        if (IsServer)
            SetHullPercentInternal(percent);
        else
            SetHullPercentRpc(percent);
    }

    [Rpc(SendTo.Server)]
    private void SetHullPercentRpc(float percent)
    {
        SetHullPercentInternal(percent);
    }

    private void SetHullPercentInternal(float percent)
    {
        hullPercent = Mathf.Clamp(percent, 0f, 100f);

        float m;
        if (hullPercent >= 75f) m = 1.0f;
        else if (hullPercent >= 50f) m = 1.15f;
        else if (hullPercent >= 25f) m = 1.30f;
        else if (hullPercent >= 10f) m = 1.50f;
        else m = 1.75f;

        if (!Mathf.Approximately(netHullMultiplier.Value, m))
            netHullMultiplier.Value = m;
    }

    // dipende da: ZoneManager (M2)
    /// <summary>Imposta il moltiplicatore EM. Chiamabile da qualsiasi client.</summary>
    public void SetEMIntensity(EMIntensity intensity)
    {
        if (IsServer)
            SetEMIntensityInternal(intensity);
        else
            SetEMIntensityRpc(intensity);
    }

    [Rpc(SendTo.Server)]
    private void SetEMIntensityRpc(EMIntensity intensity)
    {
        SetEMIntensityInternal(intensity);
    }

    private void SetEMIntensityInternal(EMIntensity intensity)
    {
        float m;
        switch (intensity)
        {
            case EMIntensity.Weak: m = 1.10f; break;
            case EMIntensity.Moderate: m = 1.25f; break;
            case EMIntensity.Strong: m = 1.45f; break;
            case EMIntensity.Extreme: m = 1.80f; break;
            default: m = 1.0f; break;
        }

        if (!Mathf.Approximately(netEMMultiplier.Value, m))
            netEMMultiplier.Value = m;
    }

    /// <summary>Diagnostica testuale per Monitor 2 (GDD 9.3 sezione C). Safe da tutti i client.</summary>
    public string GetDiagnosticsSummary()
    {
        string ballast = netBallastFaulted.Value
            ? $"DEGRADED [{CurrentBallastState}] ×{netBallastMultiplier.Value:0.00}"
            : "OK ×1.00";

        return $"HULL ×{netHullMultiplier.Value:0.00} · EM ×{netEMMultiplier.Value:0.00} · BALLAST {ballast}\n" +
               $"TOTALE ×{GetTotalMultiplier():0.00}";
    }

    // ===== POWER EVENTS =====

    private void HandleBlackout()
    {
        if (!IsServer) return;
        netBlackoutFaultBonus.Value += blackoutFaultBonusPerEvent;
    }
}