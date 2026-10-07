using System.Collections.Generic;
using SpaceSurvivor.Ship;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PlayerNanomedicDrone — Nanomedic Drone, gadget T3 del Corpsman (Rev BU-b · workshop Corpsman
/// T3–T4, Q86-a / Q87-a, tutte come raccomandate).
///
/// COSA FA (Q86-a): il drone si lancia su un bersaglio scelto al momento — il compagno sotto il
/// mirino entro la portata del kit, altrimenti se stessi (regola Q31-a del kit medico). Resta
/// sopra la spalla del bersaglio e lo cura a tick per la durata dello SO (Corpsman 2 HP/s per
/// 30 s). Cura solo HP e solo un giocatore vivo, via PlayerHealthSystem.ApplyHeal: non cura gli
/// stati, non rialza chi è a terra. Se il bersaglio va a terra il drone resta e riprende a curare
/// se viene rianimato in tempo; finisce se il bersaglio esce dalla partita. Un compagno a terra non
/// è un bersaglio valido (serve il defibrillatore). Droni diversi sullo stesso bersaglio si
/// sommano (come i campi curativi, Q70-a); un giocatore ha al massimo un drone attivo.
///
/// SORGENTE (Q87-a): il kit medico personale (PlayerMedKit, pezzo ItemType.NanomedicDrone),
/// rifornito all'armadietto da Medbay T3. Il client controlla il conteggio replicato; il server lo
/// ricontrolla e consuma UN pezzo solo se il drone parte (ServerTryConsume). Un drone già nel kit
/// si usa anche sotto T3 (come la Combat Stim).
///
/// RUOLO (Rev U): chiunque può lanciarlo. Il server prende al lancio il moltiplicatore del profilo
/// del kit (Corpsman 1, altri 0.6) e lo applica alla cura, come la bomba curativa.
///
/// PATTERN per-player (come PlayerMedKit / PlayerThrower): vive sul root del Player prefab, una
/// istanza per client connesso; registro statico per OwnerClientId + LocalInstance +
/// TryGetByClientId.
///
/// RETE:
///   - REPLICATO: solo il bersaglio del drone attivo (NetworkVariable&lt;ulong&gt;, NoTarget = nessuno).
///     Basta ai client per la visuale; chi entra a partita in corso vede il drone.
///   - SOLO SERVER: scadenza, moltiplicatore di ruolo, accumulatore dei tick, HP curati in totale.
///   - VISUALE: ogni client istanzia da sé il prefab dello SO (senza collider) e lo fa inseguire la
///     spalla del bersaglio (posizione del Player replicata dal suo NetworkTransform). Nessun
///     traffico per il movimento.
///   - ESITI: lancio e riepilogo finale arrivano al proprietario con RPC; il testo usa la riga
///     sotto il mirino del kit (PlayerMedKit.ShowFeedback).
///
/// INPUT: azione "DeployDrone" (V / LB), ricevuta via SendMessages di PlayerInput
/// (OnDeployDrone). LB è condiviso con RepairKey_2, come RB con ThrowGrenade e RepairKey_3: i
/// minigame disattivano il PlayerController, che qui è un gate. Nessuna interazione continua: il
/// drone non si usa con E (la regola EndInteraction di BT-c non si applica).
///
/// GATE (come kit e lancio): vivo, PlayerController attivo (non a postazione, tablet, letto,
/// pannello o a terra), tablet chiuso, nessuna interazione continua, nessun canale del kit e
/// nessuna mira di lancio in corso.
///
/// AUTORITÀ: la RPC accetta solo il proprietario (SenderClientId == OwnerClientId). Il server
/// rilegge tutto: chi lancia è vivo, il kit ha il drone, il bersaglio esiste ed è vivo, nessun
/// drone già attivo, profilo di ruolo. Nessun controllo di distanza (co-op, Q2 di BM).
/// </summary>
[RequireComponent(typeof(PlayerHealthSystem))]
public class PlayerNanomedicDrone : NetworkBehaviour
{
    /// <summary>Sentinella "nessun drone attivo".</summary>
    public const ulong NoTarget = ulong.MaxValue;

    [Header("Config (ScriptableObject)")]
    [Tooltip("Assegna l'asset NanomedicDroneConfig. Senza config il drone non si lancia (errore a log allo spawn " +
             "sul server).")]
    [SerializeField] private NanomedicDroneConfig config;

    [Header("Debug")]
    [Tooltip("Overlay OnGUI con il drone attivo (solo Editor/Development Build, solo server). Standard Rev BA — " +
             "default off.")]
    [SerializeField] private bool showDebugUI = false;

    [Tooltip("Log verboso di lanci, tick e fine del drone. Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    // ── Stato replicato: bersaglio del drone attivo (server scrive, tutti leggono) ──
    private readonly NetworkVariable<ulong> netTarget = new NetworkVariable<ulong>(
        NoTarget, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Bersaglio del drone attivo, NoTarget se nessuno. Replicato.</summary>
    public ulong ActiveTargetClientId => netTarget.Value;

    /// <summary>true se questo giocatore ha un drone attivo. Replicato.</summary>
    public bool IsDroneActive => netTarget.Value != NoTarget;

    // ── Registro statico per-clientId ──
    private static readonly Dictionary<ulong, PlayerNanomedicDrone> activeByClientId =
        new Dictionary<ulong, PlayerNanomedicDrone>();

    /// <summary>Istanza del client locale (IsOwner).</summary>
    public static PlayerNanomedicDrone LocalInstance { get; private set; }

    /// <summary>Trova il drone del client indicato (chi lo lancia, non il bersaglio).</summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerNanomedicDrone instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    // ── Esiti (server → owner) ──
    private enum DeployResult : byte
    {
        Deployed = 0,
        NoDrone = 1,
        AlreadyActive = 2,
        TargetDown = 3,
        Failed = 4
    }

    // ── Riferimenti sibling ──
    private PlayerHealthSystem health;       // server e owner
    private PlayerMedKit medKit;             // server e owner: sorgente, ruolo, bersaglio, feedback
    private PlayerController controller;     // owner: gate
    private InteractionSystem interaction;   // owner: gate
    private TabletStation tablet;            // owner: gate
    private PlayerThrower thrower;           // owner: gate (mira di lancio)

    // ── Stato server ──
    private float serverEndTime;
    private float serverEffectScale = 1f;
    private float serverTickAccumulator;
    private float serverTotalHealed;

    // ── Visuale (tutti i client) ──
    private GameObject visual;
    private ulong visualTargetId = NoTarget;
    private Transform visualTargetTransform;
    private float visualPhase;

    // ── Lifecycle NGO ──────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        health = GetComponent<PlayerHealthSystem>();
        medKit = GetComponent<PlayerMedKit>();
        activeByClientId[OwnerClientId] = this;

        netTarget.OnValueChanged += HandleTargetChanged;

        if (IsServer)
        {
            netTarget.Value = NoTarget;

            if (config == null)
                Debug.LogError("[PlayerNanomedicDrone] NanomedicDroneConfig non assegnato sul Player prefab: il drone " +
                               "non si lancia. Assegnare l'asset NanomedicDroneConfig.");
            if (medKit == null)
                Debug.LogError("[PlayerNanomedicDrone] PlayerMedKit mancante sul Player: il drone non ha sorgente.");
        }

        if (IsOwner)
        {
            LocalInstance = this;
            controller = GetComponent<PlayerController>();
            interaction = GetComponent<InteractionSystem>();
            tablet = GetComponent<TabletStation>();
            thrower = GetComponent<PlayerThrower>();
        }

        // Late join: un drone già attivo va mostrato subito.
        RefreshVisual(netTarget.Value);
    }

    public override void OnNetworkDespawn()
    {
        netTarget.OnValueChanged -= HandleTargetChanged;

        if (activeByClientId.TryGetValue(OwnerClientId, out PlayerNanomedicDrone registered) && registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;

        DestroyVisual();
    }

    // ── Input (SendMessages di PlayerInput) ────────────────────────────────────

    /// <summary>Azione "DeployDrone" (V / LB).</summary>
    public void OnDeployDrone(InputValue value)
    {
        if (value.isPressed)
            TryDeploy();
    }

    private void TryDeploy()
    {
        if (!IsOwner || !IsSpawned) return;
        if (!CanDeployLocally()) return;   // postazione, tablet, letto, a terra, canale, mira: ignorato in silenzio

        if (config == null || medKit == null)
        {
            Feedback("Nanomedic Drone unavailable");
            return;
        }
        if (IsDroneActive)
        {
            Feedback("Nanomedic Drone already active");
            return;
        }
        if (medKit.GetCount(ItemType.NanomedicDrone) <= 0)
        {
            Feedback("No Nanomedic Drone");
            return;
        }

        // Regola Q31-a del kit: il compagno sotto il mirino, altrimenti se stessi.
        PlayerHealthSystem looked = medKit.FindLookedTeammate();
        if (looked != null && looked.IsDowned)
        {
            Feedback($"{RoleLabel(looked.OwnerClientId)} is down — use the defibrillator");
            return;
        }

        ulong targetId = looked != null && looked.IsAlive ? looked.OwnerClientId : OwnerClientId;
        DeployServerRpc(targetId);
        LogV($"Lancio richiesto → client {targetId}.");
    }

    /// <summary>
    /// Si può lanciare: vivo, movimento libero (non a postazione, tablet, letto, pannello o a terra),
    /// tablet chiuso, nessuna interazione continua, nessun canale del kit e nessuna mira di lancio.
    /// Stesse regole del kit medico e di PlayerThrower.
    /// </summary>
    private bool CanDeployLocally()
    {
        if (health == null || !health.IsAlive) return false;
        if (controller == null || !controller.enabled) return false;
        if (tablet != null && tablet.IsBusy) return false;
        if (interaction != null && interaction.IsInteracting) return false;
        if (medKit != null && medKit.IsChanneling) return false;
        if (thrower != null && thrower.IsAiming) return false;
        return true;
    }

    // ── RPC: lancio (owner → server) ───────────────────────────────────────────

    [Rpc(SendTo.Server)]
    private void DeployServerRpc(ulong targetClientId, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != OwnerClientId)
        {
            Debug.LogWarning($"[PlayerNanomedicDrone] Lancio rifiutato: il client {sender} ha chiesto di lanciare il " +
                             $"drone del client {OwnerClientId}.");
            return;
        }

        DeployResult result = ServerTryDeploy(targetClientId);
        DeployResultOwnerRpc(result, targetClientId);
    }

    /// <summary>
    /// Validazione e lancio. SERVER ONLY. Il pezzo del kit si consuma solo se il drone parte. Il
    /// moltiplicatore di ruolo si prende ora e vale per tutta la durata.
    /// </summary>
    private DeployResult ServerTryDeploy(ulong targetClientId)
    {
        if (config == null || medKit == null) return DeployResult.Failed;
        if (health == null || !health.IsAlive) return DeployResult.Failed;
        if (netTarget.Value != NoTarget) return DeployResult.AlreadyActive;
        if (medKit.GetCount(ItemType.NanomedicDrone) <= 0) return DeployResult.NoDrone;

        if (!PlayerHealthSystem.TryGetByClientId(targetClientId, out PlayerHealthSystem target) || target == null)
            return DeployResult.Failed;
        if (target.IsDowned) return DeployResult.TargetDown;
        if (!target.IsAlive) return DeployResult.Failed;

        if (!medKit.ServerTryConsume(ItemType.NanomedicDrone)) return DeployResult.NoDrone;

        serverEffectScale = medKit.ProfileFor(OwnerClientId).EffectMultiplier;
        serverEndTime = Time.time + config.DurationSeconds;
        serverTickAccumulator = 0f;
        serverTotalHealed = 0f;
        netTarget.Value = targetClientId;

        LogV($"Drone lanciato → client {targetClientId} (effetto ×{serverEffectScale:F2}, {config.DurationSeconds:F0} s).");
        return DeployResult.Deployed;
    }

    [Rpc(SendTo.Owner)]
    private void DeployResultOwnerRpc(DeployResult result, ulong targetClientId)
    {
        switch (result)
        {
            case DeployResult.Deployed:
                Feedback(targetClientId == OwnerClientId
                             ? "Nanomedic Drone deployed"
                             : $"Nanomedic Drone on {RoleLabel(targetClientId)}");
                break;
            case DeployResult.NoDrone:
                Feedback("No Nanomedic Drone");
                break;
            case DeployResult.AlreadyActive:
                Feedback("Nanomedic Drone already active");
                break;
            case DeployResult.TargetDown:
                Feedback($"{RoleLabel(targetClientId)} is down — use the defibrillator");
                break;
            default:
                Feedback("Deploy failed");
                break;
        }
    }

    // ── Update: tick server + visuale ──────────────────────────────────────────

    private void Update()
    {
        if (IsServer && IsSpawned)
            ServerTick(Time.deltaTime);

        UpdateVisual(Time.deltaTime);
    }

    /// <summary>
    /// Cura a tick il bersaglio finché il drone dura. SERVER. ApplyHeal cura solo un giocatore vivo e
    /// fino all'HP max effettivo: a terra o a HP pieni il tick non ha effetto, ma il tempo scorre.
    /// </summary>
    private void ServerTick(float deltaTime)
    {
        ulong target = netTarget.Value;
        if (target == NoTarget) return;

        if (!PlayerHealthSystem.TryGetByClientId(target, out PlayerHealthSystem targetHealth) || targetHealth == null)
        {
            LogV($"Bersaglio {target} uscito dalla partita: drone finito.");
            ServerEnd();
            return;
        }

        float interval = config != null ? config.HealTickInterval : 0.5f;
        float perTick = (config != null ? config.HealPerSecond : 0f) * interval * serverEffectScale;

        serverTickAccumulator += deltaTime;
        while (serverTickAccumulator >= interval)
        {
            serverTickAccumulator -= interval;
            if (perTick > 0f)
                serverTotalHealed += targetHealth.ApplyHeal(perTick);
        }

        if (Time.time >= serverEndTime)
            ServerEnd();
    }

    /// <summary>Chiude il drone e manda il riepilogo al proprietario. SERVER.</summary>
    private void ServerEnd()
    {
        ulong target = netTarget.Value;
        if (target == NoTarget) return;

        netTarget.Value = NoTarget;
        LogV($"Drone finito: +{serverTotalHealed:F1} HP al client {target}.");
        DroneSummaryOwnerRpc(target, Mathf.RoundToInt(serverTotalHealed));
    }

    [Rpc(SendTo.Owner)]
    private void DroneSummaryOwnerRpc(ulong targetClientId, int healedHp)
    {
        string who = targetClientId == OwnerClientId ? "you" : RoleLabel(targetClientId);
        Feedback(healedHp > 0
                     ? $"Nanomedic Drone: +{healedHp} HP to {who}"
                     : "Nanomedic Drone: no HP restored");
    }

    // ── Visuale (tutti i client) ───────────────────────────────────────────────

    private void HandleTargetChanged(ulong previous, ulong current) => RefreshVisual(current);

    /// <summary>
    /// Crea o distrugge la visuale secondo il bersaglio replicato. Il drone nasce all'altezza della
    /// testa di chi lo lancia e raggiunge il bersaglio con l'inseguimento morbido.
    /// </summary>
    private void RefreshVisual(ulong target)
    {
        if (target == NoTarget)
        {
            DestroyVisual();
            return;
        }

        visualTargetId = target;
        visualTargetTransform = null;   // risolto in UpdateVisual (il bersaglio può non essere ancora registrato)

        if (visual != null) return;
        if (config == null || config.VisualPrefab == null) return;

        Vector3 start = transform.position + Vector3.up * config.FollowHeight;
        visual = Instantiate(config.VisualPrefab, start, Quaternion.identity);
        visual.name = $"NanomedicDrone_{OwnerClientId}";
        ThrowableSystem.DisableColliders(visual);
        visualPhase = OwnerClientId * 1.3f;   // droni diversi non oscillano all'unisono
    }

    private void UpdateVisual(float deltaTime)
    {
        if (visual == null || visualTargetId == NoTarget || config == null) return;

        if (visualTargetTransform == null)
        {
            if (!PlayerHealthSystem.TryGetByClientId(visualTargetId, out PlayerHealthSystem targetHealth) ||
                targetHealth == null)
                return;
            visualTargetTransform = targetHealth.transform;
        }

        // Spalla destra per chi ha OwnerClientId pari, sinistra per i dispari: due droni sullo stesso
        // bersaglio non si sovrappongono.
        float side = OwnerClientId % 2 == 0 ? config.FollowSide : -config.FollowSide;
        Vector3 anchor = visualTargetTransform.position
                         + Vector3.up * config.FollowHeight
                         + visualTargetTransform.right * side
                         - visualTargetTransform.forward * config.FollowBack;
        anchor.y += Mathf.Sin((Time.time + visualPhase) * 2f * Mathf.PI * config.BobFrequency) * config.BobAmplitude;

        float blend = 1f - Mathf.Exp(-config.FollowSharpness * deltaTime);
        Transform droneTransform = visual.transform;
        droneTransform.position = Vector3.Lerp(droneTransform.position, anchor, blend);
        droneTransform.Rotate(0f, config.SpinDegreesPerSecond * deltaTime, 0f, Space.World);
    }

    private void DestroyVisual()
    {
        if (visual != null) Destroy(visual);
        visual = null;
        visualTargetId = NoTarget;
        visualTargetTransform = null;
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private void Feedback(string text)
    {
        if (medKit != null) medKit.ShowFeedback(text);
    }

    private static string RoleLabel(ulong clientId) => CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(clientId));

    private void LogV(string message)
    {
        if (logVerbose) Debug.Log($"[PlayerNanomedicDrone/{OwnerClientId}] {message}");
    }

    // ── Debug overlay (solo Editor/Development, solo server) ───────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!showDebugUI) return;
        if (!IsServer || !IsSpawned) return;

        // Sotto il pannello di PlayerMedKit (x 940, alto 130), stessa banda per OwnerClientId (passo 250).
        float y = 310 + (OwnerClientId * 250f) + 135f;

        GUILayout.BeginArea(new Rect(940, y, 320, 60));
        GUILayout.BeginVertical("box");
        ulong target = netTarget.Value;
        GUILayout.Label(target == NoTarget
                            ? $"[Drone] Client {OwnerClientId} — nessun drone attivo"
                            : $"[Drone] Client {OwnerClientId} → client {target} · " +
                              $"{Mathf.Max(0f, serverEndTime - Time.time):F1} s · +{serverTotalHealed:F0} HP · " +
                              $"×{serverEffectScale:F2}");
        if (target != NoTarget && GUILayout.Button("Termina drone")) ServerEnd();
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}
