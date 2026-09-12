using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerOxygen — TANK PERSONALE per-player (D26, Rev BE). SERVER-AUTHORITATIVE.
///
/// PATTERN (identico a PlayerHealthSystem / PlayerStatusEffects — NON è un singleton):
/// - Vive sul root del Player prefab: UNA istanza per client connesso, col proprio
///   OwnerClientId.
/// - Registro statico per-clientId (activeByClientId) + LocalInstance + TryGetByClientId,
///   così i futuri sistemi server (EVA/relitto) e la futura UI trovano l'istanza di
///   un membro dato il suo clientId.
///
/// SCOPE DI REPLICA (Q1-a): il livello del tank è un NetworkVari&lt;float&gt; REPLICATO.
/// Deviazione giustificata dal default "server-only finché non serve un consumatore
/// client" (invariante Rev BC): qui la replica serve davvero, perché il livello del
/// proprio O2 è un dato che la HUD del giocatore mostrerà (consumatore client reale).
///
/// MODELLO (Q2-b, Fase 1):
/// - A bordo (default) → RICARICA passiva verso config.MaxLevel.
/// - Fuori (EVA/relitto) → CONSUMO. Lo stato "fuori" è un SEAM: non esiste ancora
///   (EVA = D31, accesso relitto = D22). I futuri sistemi chiameranno SetConsuming(bool);
///   in Fase 1 nessun chiamante reale → il tank resta pieno, salvo debugForceConsume.
///
/// SEAM O2 PERSONALE = 0 → danno: NON cablato in Fase 1 (è conseguenza dell'EVA, che
/// non esiste). Quando l'EVA esisterà, il suo sistema deciderà l'effetto del tank a 0
/// (probabilmente via PlayerHealthSystem.ApplyDamage, lo stesso choke point del danno
/// da soffocamento nave). Qui resta solo un livello che può arrivare a 0.
///
/// I TRE POOL O2 NON INTERCOMUNICANO: il tank personale non travasa O2 col tank nave
/// (OxygenSystem) né col residuo relitto. Semantica di ricarica/consumo indipendente.
/// </summary>
public class PlayerOxygen : NetworkBehaviour
{
    // ===== Config (SO) =====

    [Header("Config (ScriptableObject)")]
    [Tooltip("Assegna l'asset PersonalOxygenConfig. Senza config il tick è inerte (warning a spawn server).")]
    [SerializeField] private PersonalOxygenConfig config;

    [Header("Debug")]
    [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development Build). Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugUI = false;

    [Tooltip("Log verboso del lifecycle tank personale. Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    [Tooltip("SEAM DI DEBUG (Q2-b): se ON, forza il CONSUMO del tank anche a bordo, per " +
             "validare il refill al Gate (accendi → drena; spegni → ricarica). Solo server.")]
    [SerializeField] private bool debugForceConsume = false;

    // ===== NetworkVariable (server scrive, tutti leggono) =====

    private readonly NetworkVariable<float> netPersonalO2 =
        new NetworkVariable<float>(100f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

    // ===== Stato server-only =====

    // SEAM EVA/relitto: true = "fuori atmosfera" (consuma). Default false = a bordo (ricarica).
    private bool isConsuming = false;

    // ===== Registro statico per-clientId — stesso pattern di PlayerHealthSystem =====

    private static readonly Dictionary<ulong, PlayerOxygen> activeByClientId = new();

    /// <summary>Istanza del client locale (IsOwner) — scorciatoia per la futura HUD del proprio O2.</summary>
    public static PlayerOxygen LocalInstance { get; private set; }

    /// <summary>
    /// Trova l'istanza del client indicato. I futuri sistemi server (EVA/relitto) la
    /// useranno per impostare il consumo di un membro specifico via SetConsuming.
    /// </summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerOxygen instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    // ===== Properties pubbliche =====

    public float PersonalO2 => netPersonalO2.Value;
    public float PersonalO2Percentage =>
        config != null && config.MaxLevel > 0f ? netPersonalO2.Value / config.MaxLevel : 0f;
    public bool IsConsuming => isConsuming;

    // ===== Events (fired su tutti i client via NetworkVariable.OnValueChanged) =====

    /// <summary>Fired quando il livello del tank personale cambia. Parametro: nuovo valore.</summary>
    public event Action<float> OnPersonalO2Changed;

    // ===== NGO Lifecycle =====

    public override void OnNetworkSpawn()
    {
        activeByClientId[OwnerClientId] = this;

        if (IsOwner)
            LocalInstance = this;

        // Tutti i client ascoltano i cambi per la futura UI locale.
        netPersonalO2.OnValueChanged += HandleO2Changed;

        if (IsServer)
        {
            if (config == null)
            {
                Debug.LogError("[PlayerOxygen] PersonalOxygenConfig non assegnata sul Player prefab. " +
                               "Il tank personale resterà inerte. Assegnare l'asset PersonalOxygenConfig " +
                               "sul componente PlayerOxygen (root del Player prefab).");
            }
            else
            {
                netPersonalO2.Value = config.MaxLevel;   // il crew parte con la tuta piena
            }
        }
    }

    public override void OnNetworkDespawn()
    {
        netPersonalO2.OnValueChanged -= HandleO2Changed;

        if (activeByClientId.TryGetValue(OwnerClientId, out var registered) && registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;
    }

    private void HandleO2Changed(float _, float newVal) => OnPersonalO2Changed?.Invoke(newVal);

    // ===== Tick server =====

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;
        if (config == null) return;

        // Consumo effettivo = seam EVA/relitto OPPURE drain di debug (Q2-b).
        bool consuming = isConsuming || debugForceConsume;

        float dt = Time.deltaTime;
        float current = netPersonalO2.Value;
        float next = consuming
            ? current - config.ConsumptionPerSecond * dt
            : current + config.RechargePerSecond * dt;

        next = Mathf.Clamp(next, 0f, config.MaxLevel);

        // Evita write inutili quando il tank è già pieno e a bordo (niente churn NetVar).
        if (!Mathf.Approximately(next, current))
            netPersonalO2.Value = next;
    }

    // ===== API pubblica =====

    /// <summary>
    /// SEAM EVA/relitto (D31/D22): il futuro stato "fuori atmosfera" chiamerà
    /// SetConsuming(true) all'uscita e SetConsuming(false) al rientro a bordo.
    /// In Fase 1 nessun chiamante reale → il tank resta pieno (refill passivo).
    /// Solo server.
    /// </summary>
    public void SetConsuming(bool consuming)
    {
        if (!IsServer) return;
        if (isConsuming == consuming) return;
        isConsuming = consuming;
        LogV($"[PlayerOxygen/{OwnerClientId}] SetConsuming({consuming})");
    }

    // ===== Debug logging (Rev BA) =====
    private void LogV(string msg) { if (logVerbose) Debug.Log(msg); }

    // ===== Debug GUI =====

    private void OnGUI()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (!showDebugUI) return;

        // Banda verticale per OwnerClientId, per non sovrapporre i pannelli in ParrelSync.
        float y = 260 + (OwnerClientId * 60f);
        string where = IsServer ? "SERVER" : "CLIENT";
        bool consuming = isConsuming || debugForceConsume;
        GUI.Label(new Rect(340, y, 320, 20),
            $"[PlayerO2/{where}] Client {OwnerClientId}: {netPersonalO2.Value:F1}" +
            (config != null ? $"/{config.MaxLevel:F0}" : "/?") +
            $" — {(consuming ? "CONSUMO" : "ricarica")}");
#endif
    }
}
