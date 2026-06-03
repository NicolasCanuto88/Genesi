using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using System;

/// <summary>
/// Central power management system for the ship (NGO v2 — NetworkBehaviour).
/// Authority: Server only. Clients ricevono lo stato via NetworkVariable e vengono
/// notificati degli eventi via ClientRpc.
///
/// Il Singleton è assegnato in OnNetworkSpawn() — prima dello spawn NGO non garantisce
/// che l'oggetto sia pronto in rete.
///
/// Compatibile con tutto il codice esistente che usa PowerManager.Instance.
/// </summary>
public class PowerManager : NetworkBehaviour
{
    [Header("Power Generation")]
    [SerializeField] private float maxPowerOutput = 1000f;
    [SerializeField] private float currentReactorEfficiency = 1.0f;
    [SerializeField] private float reactorDegradationRate = 0.01f;

    [Header("Critical Settings")]
    [SerializeField] private float criticalPowerThreshold = 0.2f; // 20%
    [SerializeField] private float blackoutThreshold = 0.05f;     // 5%

    [Header("Blackout Settings")]
    [SerializeField] private bool requireManualRecovery = true;

    // ===== NetworkVariables (server scrive, tutti leggono) =====
    private NetworkVariable<float> netPowerGeneration = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netPowerConsumption = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netPowerReserve = new NetworkVariable<float>(500f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<float> netReactorEfficiency = new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netIsInBlackout = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private NetworkVariable<bool> netIsInCriticalState = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ===== Stato server-only (non sincronizzato — calcolato ogni frame) =====
    private float maxPowerReserve = 500f;
    private bool blackoutManualResetNeeded = false;
    private List<IPowerConsumer> powerConsumers = new List<IPowerConsumer>();

    // ===== Singleton =====
    public static PowerManager Instance { get; private set; }
    /// <summary>
    /// Fired quando PowerManager è pronto (dopo OnNetworkSpawn).
    /// I sistemi dipendenti si sottoscrivono se Instance è null al loro Start().
    /// </summary>
    public static event Action OnInstanceReady;

    // ===== Events (fired localmente su tutti i client via Rpc) =====
    public event Action<float> OnPowerLevelChanged;
    public event Action OnCriticalPower;
    public event Action OnBlackout;
    public event Action OnPowerRestored;

    // ===== Properties pubbliche (leggono NetworkVariable — safe da tutti i client) =====
    public float MaxPowerOutput => maxPowerOutput * netReactorEfficiency.Value;
    public float CurrentPowerGeneration => netPowerGeneration.Value;
    public float CurrentPowerConsumption => netPowerConsumption.Value;
    public float PowerPercentage => (netPowerGeneration.Value > 0) ? (netPowerConsumption.Value / netPowerGeneration.Value) : 0f;
    public float PowerReservePercentage => netPowerReserve.Value / maxPowerReserve;
    public bool IsInCriticalState => netIsInCriticalState.Value;
    public bool IsInBlackout => netIsInBlackout.Value;
    public bool IsBlackoutManualResetNeeded => blackoutManualResetNeeded;

    // ===== NGO Lifecycle =====

    public override void OnNetworkSpawn()
    {
        if (Instance == null)
        {
            Instance = this;
            OnInstanceReady?.Invoke();
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        if (IsServer)
        {
            // Inizializzazione server
            netPowerGeneration.Value = MaxPowerOutput;
            netPowerReserve.Value = maxPowerReserve;
            netReactorEfficiency.Value = currentReactorEfficiency;
        }

        // Tutti i client sottoscrivono i cambi di stato per aggiornare la UI locale
        netIsInBlackout.OnValueChanged += OnBlackoutStateChanged;
        netIsInCriticalState.OnValueChanged += OnCriticalStateChanged;
        netPowerConsumption.OnValueChanged += (_, newVal) => OnPowerLevelChanged?.Invoke(PowerPercentage);
    }

    public override void OnNetworkDespawn()
    {
        netIsInBlackout.OnValueChanged -= OnBlackoutStateChanged;
        netIsInCriticalState.OnValueChanged -= OnCriticalStateChanged;

        if (Instance == this) Instance = null;
    }

    // ===== Callbacks NetworkVariable =====

    private void OnBlackoutStateChanged(bool previous, bool current)
    {
        if (current && !previous)
        {
            OnBlackout?.Invoke();
        }
        else if (!current && previous)
        {
            OnPowerRestored?.Invoke();
        }
    }

    private void OnCriticalStateChanged(bool previous, bool current)
    {
        if (current && !previous)
        {
            OnCriticalPower?.Invoke();
        }
    }

    // ===== Update (solo server) =====

    private void Update()
    {
        if (!IsServer) return;

        UpdatePowerGeneration();
        CalculatePowerConsumption();
        HandlePowerDeficit();
        CheckCriticalStates();
        DegradeReactor();
    }

    // ===== Logica server =====

    private void UpdatePowerGeneration()
    {
        float gen = MaxPowerOutput;
        if (netIsInBlackout.Value) gen *= 0.1f;
        netPowerGeneration.Value = gen;
    }

    private void CalculatePowerConsumption()
    {
        float total = 0f;
        foreach (var consumer in powerConsumers)
        {
            if (consumer != null && consumer.IsActive())
                total += consumer.GetPowerDemand();
        }
        netPowerConsumption.Value = total;
    }

    private void HandlePowerDeficit()
    {
        float deficit = netPowerConsumption.Value - netPowerGeneration.Value;

        if (deficit > 0)
        {
            float reserve = netPowerReserve.Value - deficit * Time.deltaTime;
            netPowerReserve.Value = Mathf.Max(0f, reserve);

            if (netPowerReserve.Value <= 0)
                ShedLoad(deficit);
        }
        else
        {
            float surplus = Mathf.Abs(deficit);
            netPowerReserve.Value = Mathf.Min(maxPowerReserve, netPowerReserve.Value + surplus * Time.deltaTime * 0.5f);

            if (netPowerReserve.Value >= maxPowerReserve * 0.3f)
                RestoreLoad();
        }
    }

    private void ShedLoad(float deficitAmount)
    {
        powerConsumers.Sort((a, b) => a.GetPriority().CompareTo(b.GetPriority()));

        float shedAmount = 0f;
        foreach (var consumer in powerConsumers)
        {
            if (consumer != null && consumer.IsActive() && consumer.CanBeDisabled())
            {
                float demand = consumer.GetPowerDemand();
                consumer.SetPowerState(false);
                shedAmount += demand;

                Debug.LogWarning($"[PowerManager] Auto-disabled {consumer.GetSystemName()} (Priority {consumer.GetPriority()}) — {demand}W");

                if (shedAmount >= deficitAmount) break;
            }
        }
    }

    private void RestoreLoad()
    {
        if (netIsInBlackout.Value) return;

        float available = netPowerGeneration.Value - netPowerConsumption.Value;
        if (available <= 0f) return;

        powerConsumers.Sort((a, b) => b.GetPriority().CompareTo(a.GetPriority()));

        foreach (var consumer in powerConsumers)
        {
            if (consumer == null) continue;
            if (consumer.IsActive()) continue;
            if (!consumer.CanBeDisabled()) continue;

            float demand = consumer.GetPowerDemand();
            if (demand <= available)
            {
                consumer.SetPowerState(true);
                available -= demand;
                if (demand > 0)
                    Debug.Log($"[PowerManager] Restored {consumer.GetSystemName()} ({demand}W) — surplus: {available:F0}W");
            }
        }
    }

    private void CheckCriticalStates()
    {
        float powerPercent = PowerPercentage;

        // Critical state
        if (powerPercent >= criticalPowerThreshold && !netIsInCriticalState.Value)
        {
            netIsInCriticalState.Value = true;
            Debug.LogWarning("[PowerManager] CRITICAL POWER — " + (powerPercent * 100f).ToString("F1") + "%");
        }
        else if (powerPercent < criticalPowerThreshold * 0.8f && netIsInCriticalState.Value)
        {
            netIsInCriticalState.Value = false;
        }

        // Blackout entry
        if (netPowerReserve.Value <= 0 && powerPercent >= 1.0f && !netIsInBlackout.Value)
        {
            EnterBlackout();
        }

        // Blackout exit
        if (netIsInBlackout.Value)
        {
            if (!requireManualRecovery || !blackoutManualResetNeeded)
            {
                float fullGenPercent = netPowerConsumption.Value / MaxPowerOutput;
                if (fullGenPercent < 0.5f && netPowerReserve.Value > maxPowerReserve * 0.3f)
                {
                    ExitBlackout();
                }
            }
        }

        // Notify power level (ogni frame — il subscriber decide se aggiornare la UI)
        if (IsSpawned)
            NotifyPowerLevelRpc(powerPercent);
    }

    private void EnterBlackout()
    {
        netIsInBlackout.Value = true;
        blackoutManualResetNeeded = requireManualRecovery;

        Debug.LogError("[PowerManager] BLACKOUT! Manual recovery required.");

        foreach (var consumer in powerConsumers)
        {
            if (consumer != null && consumer.GetPriority() < 10)
                consumer.SetPowerState(false);
        }
    }

    private void ExitBlackout()
    {
        netIsInBlackout.Value = false;
        blackoutManualResetNeeded = false;
        Debug.Log("[PowerManager] Power restored.");
    }

    // ===== RPC =====

    /// <summary>
    /// Notifica tutti i client del livello di potenza corrente (fired dal server ogni frame).
    /// </summary>
    [Rpc(SendTo.ClientsAndHost)]
    private void NotifyPowerLevelRpc(float percent)
    {
        OnPowerLevelChanged?.Invoke(percent);
    }

    /// <summary>
    /// Il giocatore chiede il restore manuale dalla Engineering Dashboard.
    /// Può essere chiamato da qualsiasi client — l'esecuzione avviene solo sul server.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void TryManualPowerRestoreRpc()
    {
        if (!IsServer) return;

        if (!netIsInBlackout.Value)
        {
            Debug.LogWarning("[PowerManager] Not in blackout.");
            return;
        }

        if (!blackoutManualResetNeeded)
        {
            Debug.LogWarning("[PowerManager] No manual reset needed.");
            return;
        }

        float reservePercent = netPowerReserve.Value / maxPowerReserve;
        if (reservePercent < 0.3f)
        {
            Debug.LogWarning($"[PowerManager] Reserve too low ({reservePercent * 100f:F0}%) — need 30%");
            RestoreFailedRpc("Riserva insufficiente — attendere ricarica");
            return;
        }

        float consumptionPercent = netPowerConsumption.Value / MaxPowerOutput;
        if (consumptionPercent > 0.7f)
        {
            Debug.LogWarning($"[PowerManager] Load too high ({consumptionPercent * 100f:F0}%)");
            RestoreFailedRpc("Carico troppo alto — spegnere sistemi non essenziali");
            return;
        }

        Debug.Log("[PowerManager] ✅ Manual power restore successful!");
        blackoutManualResetNeeded = false;
        ExitBlackout();
    }

    /// <summary>
    /// Notifica il client chiamante che il restore è fallito (con motivazione).
    /// </summary>
    [Rpc(SendTo.ClientsAndHost)]
    private void RestoreFailedRpc(string reason)
    {
        Debug.LogWarning($"[PowerManager] Restore fallito: {reason}");
        // dipende da: EngineeringDashboardUI — aggiornare il pannello con il messaggio
    }

    // ===== API pubblica (compatibile con codice esistente) =====

    /// <summary>
    /// Versione locale di TryManualPowerRestore — chiama l'Rpc se in rete,
    /// esegue direttamente se in single player (IsServer senza client).
    /// Mantenuta per compatibilità con EngineeringDashboardUI esistente.
    /// </summary>
    public bool TryManualPowerRestore()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            TryManualPowerRestoreRpc();
            return true; // ottimistico — la UI si aggiorna via NetworkVariable
        }

        // Fallback single player (utile in editor senza host)
        return TryManualPowerRestoreLocal();
    }

    private bool TryManualPowerRestoreLocal()
    {
        if (!netIsInBlackout.Value) return false;
        if (!blackoutManualResetNeeded) return false;

        float reservePercent = netPowerReserve.Value / maxPowerReserve;
        if (reservePercent < 0.3f) return false;

        float consumptionPercent = netPowerConsumption.Value / MaxPowerOutput;
        if (consumptionPercent > 0.7f) return false;

        blackoutManualResetNeeded = false;
        ExitBlackout();
        return true;
    }

    public bool CanRestorePower(out string reason)
    {
        reason = "";

        if (!netIsInBlackout.Value) { reason = "Not in blackout"; return false; }
        if (!blackoutManualResetNeeded) { reason = "Auto-recovery in progress"; return false; }

        float reservePercent = netPowerReserve.Value / maxPowerReserve;
        if (reservePercent < 0.3f)
        {
            reason = $"Riserva troppo bassa ({reservePercent * 100f:F0}% / 30% richiesto)";
            return false;
        }

        float consumptionPercent = netPowerConsumption.Value / MaxPowerOutput;
        if (consumptionPercent > 0.7f)
        {
            reason = $"Carico troppo alto ({consumptionPercent * 100f:F0}% / 70% max)";
            return false;
        }

        reason = "Pronto al ripristino";
        return true;
    }

    public List<ShipLight> GetManualLights()
    {
        var manualLights = new List<ShipLight>();
        foreach (var consumer in powerConsumers)
        {
            if (consumer is ShipLight light && light.GetLightMode() == ShipLight.LightMode.Manual)
                manualLights.Add(light);
        }
        return manualLights;
    }

    public void RegisterPowerConsumer(IPowerConsumer consumer)
    {
        if (!powerConsumers.Contains(consumer))
        {
            powerConsumers.Add(consumer);
            Debug.Log($"[PowerManager] Registered: {consumer.GetSystemName()}");
        }
    }

    public void UnregisterPowerConsumer(IPowerConsumer consumer)
    {
        powerConsumers.Remove(consumer);
    }

    public void SetReactorEfficiency(float efficiency)
    {
        if (!IsServer) return;
        currentReactorEfficiency = Mathf.Clamp01(efficiency);
        netReactorEfficiency.Value = currentReactorEfficiency;
    }

    public void RepairReactor(float amount)
    {
        if (!IsServer) return;
        currentReactorEfficiency = Mathf.Min(1.0f, currentReactorEfficiency + amount);
        netReactorEfficiency.Value = currentReactorEfficiency;
    }

    private void DegradeReactor()
    {
        currentReactorEfficiency -= reactorDegradationRate * Time.deltaTime / 3600f;
        currentReactorEfficiency = Mathf.Max(0.5f, currentReactorEfficiency);
        netReactorEfficiency.Value = currentReactorEfficiency;
    }

    // ===== Debug GUI =====

    private void OnGUI()
    {
        if (!Debug.isDebugBuild) return;

        int y = 100;
        GUI.Label(new Rect(10, y, 300, 20), $"=== POWER SYSTEM [{(IsServer ? "SERVER" : "CLIENT")}] ==="); y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Generation: {netPowerGeneration.Value:F0}W / {MaxPowerOutput:F0}W"); y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Consumption: {netPowerConsumption.Value:F0}W ({(PowerPercentage * 100f):F1}%)"); y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Reserve: {netPowerReserve.Value:F0}W / {maxPowerReserve:F0}W"); y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Reactor: {(netReactorEfficiency.Value * 100f):F1}%"); y += 20;
        GUI.Label(new Rect(10, y, 300, 20), $"Consumers: {powerConsumers.Count}"); y += 20;

        if (netIsInCriticalState.Value)
        {
            GUI.color = Color.yellow;
            GUI.Label(new Rect(10, y, 300, 20), "⚠ CRITICAL POWER"); y += 20;
        }

        if (netIsInBlackout.Value)
        {
            GUI.color = Color.red;
            GUI.Label(new Rect(10, y, 300, 20), "⚠⚠⚠ BLACKOUT ⚠⚠⚠");
        }

        GUI.color = Color.white;
    }
}

/// <summary>
/// Interface for all systems that consume power.
/// </summary>
public interface IPowerConsumer
{
    float GetPowerDemand();
    int GetPriority();
    bool IsActive();
    bool CanBeDisabled();
    void SetPowerState(bool isOn);
    string GetSystemName();
}