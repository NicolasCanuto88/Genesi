using System;
using System.Collections.Generic;
using SpaceSurvivor.Ship;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PlayerMedKit — kit medico personale e cura in campo (Rev BQ, Fase 3a).
/// SERVER-AUTHORITATIVE sul contenuto del kit e sugli effetti; il canale d'uso gira
/// sul client di chi usa il kit, come il defibrillatore (PlayerReviveTarget).
///
/// PATTERN per-player (identico a PlayerHealthSystem / PlayerStatusEffects / PlayerOxygen):
/// vive sul root del Player prefab, una istanza per client connesso; registro statico
/// per OwnerClientId + LocalInstance + TryGetByClientId.
///
/// KIT PERSONALE (Q30-a): tre contatori replicati (NetworkVariable&lt;byte&gt;: medikit base,
/// medikit avanzato, antidoto), con capienza dallo SO MedKitConfig. Il kit si riempie
/// all'armadietto medico (MedicalSupplyLocker), che preleva dalla stiva della nave
/// (InventorySystem.TryConsume): il materiale passa solo con un trasferimento esplicito,
/// come per i pool di O₂. Il kit parte VUOTO allo spawn e il clone lo perde
/// (ServerClearKit da PlayerHealthSystem.ServerRespawn: il clone è un corpo nuovo).
/// Non è il modello zaino + stiva dei collezionabili, che lo assorbirà quando esisterà.
///
/// BERSAGLIO (Q31-a): automatico. Se il primo collider sotto il mirino, entro
/// TargetRange, è un compagno vivo, si cura lui; altrimenti se stessi. Il raggio è
/// dedicato (targetRayMask): InteractionSystem non viene toccato, perché la sua maschera
/// vede solo il layer Interactable e i giocatori vivi non ci stanno. Pareti e oggetti
/// bloccano il raggio: si cura solo chi si vede.
///
/// CANALE (Q32-a): durata dal profilo di ruolo (Corpsman 1,5 s, altri 3 s). Si
/// interrompe se il bersaglio non è più vivo, se chi cura distoglie lo sguardo dal
/// compagno, se si sposta oltre MaxMoveDistance, se va a una postazione, apre il tablet,
/// si sdraia o finisce a terra (PlayerController disabilitato). NON si interrompe per
/// danno: con il Veleno (un tick ogni 4 s) un avvelenato non finirebbe mai l'antidoto.
/// Un canale interrotto non consuma nulla.
///
/// EFFETTI (sul server, a canale completo):
///   - MEDIKIT (Q33-a): HP nominali × moltiplicatore del profilo (Corpsman 1, altri 0.6)
///     via PlayerHealthSystem.ApplyHeal (solo Alive, clamp). Nessun HP curato → nessun
///     consumo.
///   - ANTIDOTO (Q34-a): rimuove Veleno e Radiazioni (AntidoteCures) via
///     PlayerStatusEffects.RemoveEffect, senza la regola di tier della Recovery Bay: è la
///     cura in campo per chi non ha la Bay o non può tornarci (Q25-b di BP). Le Ferite
///     Composte restano alla Bay T3. Nessuno stato rimosso → nessun consumo.
///
/// INPUT (Q35-a): azioni "UseMedkit" (H / D-pad giù) e "UseAntidote" (J / D-pad su),
/// ricevute via SendMessages di PlayerInput (OnUseMedkit / OnUseAntidote). Il medikit
/// sceglie da solo: l'avanzato se al bersaglio mancano almeno AdvancedHealAmount HP,
/// altrimenti il base (poi l'altro, se uno dei due è finito).
///
/// FEEDBACK (Q36-a): una riga di testo sotto il mirino (feedbackText, solo owner):
/// avanzamento durante il canale, esito per FeedbackHoldSeconds dopo. Testi in inglese.
///
/// AUTORITÀ: le RPC verso il server accettano solo il proprietario del kit
/// (SenderClientId == OwnerClientId). Il server rilegge tutto: item disponibile,
/// bersaglio vivo, qualcosa da curare, profilo di ruolo (mai dichiarato dal client).
/// Nessun controllo di distanza lato server (co-op, nessun anti-cheat: Q2 di BM).
/// </summary>
[RequireComponent(typeof(PlayerHealthSystem))]
public class PlayerMedKit : NetworkBehaviour
{
    [Header("Config (ScriptableObject)")]
    [Tooltip("Assegna l'asset MedKitConfig. Senza config il kit non si usa e non si rifornisce " +
             "(errore a log allo spawn sul server).")]
    [SerializeField] private MedKitConfig config;

    [Header("Bersaglio (Q31-a)")]
    [Tooltip("Layer considerati dal raggio di mira del kit. Un compagno è bersaglio solo se è il " +
             "PRIMO collider colpito: pareti e oggetti devono restare nella maschera per bloccare il " +
             "raggio. Default: tutto tranne Ignore Raycast e UI.")]
    [SerializeField] private LayerMask targetRayMask = ~((1 << 2) | (1 << 5));

    [Header("Feedback a schermo (Q36-a)")]
    [Tooltip("Testo TMP nel Canvas HUD del Player, sotto il mirino. Scritto solo dal proprietario.")]
    [SerializeField] private TextMeshProUGUI feedbackText;

    [Header("Debug")]
    [Tooltip("Overlay OnGUI di diagnostica e prova (solo Editor/Development Build, solo server). " +
             "Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugUI = false;

    [Tooltip("Log verboso di uso e rifornimento del kit. Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    // ── Kit replicato (Q30-a) — server scrive, tutti leggono ──
    private readonly NetworkVariable<byte> netMedkitBase = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> netMedkitAdvanced = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> netAntidote = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>
    /// Stati curati dall'antidoto (Q34-a). Decisione di design, non tuning: le Ferite
    /// Composte NON ci sono e non devono entrarci (restano alla Recovery Bay T3).
    /// </summary>
    private static readonly StatusEffectType[] AntidoteCures =
    {
        StatusEffectType.Poison,
        StatusEffectType.Radiation
    };

    // ── Registro statico per-clientId — stesso pattern di PlayerHealthSystem ──
    private static readonly Dictionary<ulong, PlayerMedKit> activeByClientId = new Dictionary<ulong, PlayerMedKit>();

    /// <summary>Istanza del client locale (IsOwner): la usano l'armadietto e il tab Profilo.</summary>
    public static PlayerMedKit LocalInstance { get; private set; }

    /// <summary>Trova il kit del client indicato.</summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerMedKit instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    /// <summary>Fired SOLO sul client proprietario quando il contenuto del proprio kit cambia.</summary>
    public static event Action OnLocalKitChanged;

    // ── Esiti (server → owner) ──
    private enum UseResult : byte
    {
        Healed = 0,
        Cured = 1,
        NothingToTreat = 2,
        NoItem = 3,
        TargetInvalid = 4,
        Failed = 5
    }

    private enum RestockResult : byte
    {
        Restocked = 0,
        KitFull = 1,
        NoSupplies = 2,
        Failed = 3
    }

    private enum UseKind : byte
    {
        Medkit = 0,
        Antidote = 1
    }

    // ── Riferimenti sibling ──
    private PlayerHealthSystem health;       // server e owner
    private PlayerController controller;     // owner: gate di disponibilità
    private InteractionSystem interaction;   // owner: niente kit durante un'interazione continua
    private TabletStation tablet;            // owner: niente kit a tablet aperto
    private Transform cameraTransform;       // owner: origine del raggio di mira

    // ── Canale (solo owner) ──
    private bool channeling;
    private ItemType channelItem;
    private PlayerHealthSystem channelTarget;
    private bool channelOnSelf;
    private float channelElapsed;
    private float channelDuration;
    private Vector3 channelStartPosition;
    private string channelLabel = string.Empty;

    // ── Feedback (solo owner) ──
    private float feedbackTimer;

    private readonly RaycastHit[] rayHits = new RaycastHit[8];

    // ── API pubblica (server e client) ─────────────────────────────────────────

    /// <summary>Pezzi nel kit per il tipo indicato. Tipi fuori dal kit → 0. Replicato.</summary>
    public int GetCount(ItemType type)
    {
        switch (type)
        {
            case ItemType.MedkitBase:     return netMedkitBase.Value;
            case ItemType.MedkitAdvanced: return netMedkitAdvanced.Value;
            case ItemType.Antidote:       return netAntidote.Value;
            default:                      return 0;
        }
    }

    /// <summary>Capienza del kit per il tipo indicato (da MedKitConfig). Senza config → 0.</summary>
    public int GetCap(ItemType type) => config != null ? config.CapFor(type) : 0;

    /// <summary>true se nessun tipo del kit ha spazio libero (anche senza config).</summary>
    public bool IsFull =>
        GetCount(ItemType.MedkitBase) >= GetCap(ItemType.MedkitBase) &&
        GetCount(ItemType.MedkitAdvanced) >= GetCap(ItemType.MedkitAdvanced) &&
        GetCount(ItemType.Antidote) >= GetCap(ItemType.Antidote);

    // ── Lifecycle NGO ──────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        health = GetComponent<PlayerHealthSystem>();
        activeByClientId[OwnerClientId] = this;

        netMedkitBase.OnValueChanged += HandleKitChanged;
        netMedkitAdvanced.OnValueChanged += HandleKitChanged;
        netAntidote.OnValueChanged += HandleKitChanged;

        if (IsServer)
        {
            // Q30-a: il kit parte vuoto; si riempie all'armadietto medico.
            netMedkitBase.Value = 0;
            netMedkitAdvanced.Value = 0;
            netAntidote.Value = 0;

            if (config == null)
                Debug.LogError("[PlayerMedKit] MedKitConfig non assegnato sul Player prefab: il kit medico " +
                               "non si può usare né rifornire. Assegnare l'asset MedKitConfig.");
        }

        if (IsOwner)
        {
            LocalInstance = this;
            controller = GetComponent<PlayerController>();
            interaction = GetComponent<InteractionSystem>();
            tablet = GetComponent<TabletStation>();
            Camera cam = GetComponentInChildren<Camera>();
            cameraTransform = cam != null ? cam.transform : null;

            if (cameraTransform == null)
                Debug.LogError("[PlayerMedKit] Nessuna Camera figlia del Player: il bersaglio compagno non " +
                               "può essere risolto (il kit si userà solo su se stessi).");
            if (feedbackText == null)
                Debug.LogWarning("[PlayerMedKit] feedbackText non assegnato: nessuna riga di feedback a " +
                                 "schermo (Q36-a). Vedi guida Editor di Rev BQ.");

            SetFeedback(string.Empty, 0f);
            OnLocalKitChanged?.Invoke();
        }
    }

    public override void OnNetworkDespawn()
    {
        netMedkitBase.OnValueChanged -= HandleKitChanged;
        netMedkitAdvanced.OnValueChanged -= HandleKitChanged;
        netAntidote.OnValueChanged -= HandleKitChanged;

        if (activeByClientId.TryGetValue(OwnerClientId, out var registered) && registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;

        channeling = false;
        channelTarget = null;
    }

    private void HandleKitChanged(byte previous, byte current)
    {
        if (IsOwner)
            OnLocalKitChanged?.Invoke();
    }

    // ── Input (SendMessages di PlayerInput — Q35-a) ────────────────────────────

    /// <summary>Azione "UseMedkit" (H / D-pad giù).</summary>
    public void OnUseMedkit(InputValue value)
    {
        if (value.isPressed)
            TryBeginUse(UseKind.Medkit);
    }

    /// <summary>Azione "UseAntidote" (J / D-pad su).</summary>
    public void OnUseAntidote(InputValue value)
    {
        if (value.isPressed)
            TryBeginUse(UseKind.Antidote);
    }

    // ── Avvio del canale (owner) ───────────────────────────────────────────────

    private void TryBeginUse(UseKind kind)
    {
        if (!IsOwner || !IsSpawned) return;
        if (channeling) return;
        if (!CanUseLocally()) return;   // a postazione, tablet, letto, a terra: ignorato in silenzio

        if (config == null)
        {
            SetFeedback("Medical kit unavailable", HoldSeconds);
            return;
        }

        // Q31-a: compagno vivo sotto il mirino entro la portata, altrimenti se stessi.
        PlayerHealthSystem looked = FindLookedPlayer();
        PlayerHealthSystem target = health;
        bool onSelf = true;
        if (looked != null)
        {
            if (!looked.IsAlive)
            {
                SetFeedback(looked.IsDowned
                    ? $"{RoleLabel(looked)} is down — use the defibrillator"
                    : $"{RoleLabel(looked)} can't be treated", HoldSeconds);
                return;
            }
            target = looked;
            onSelf = false;
        }

        if (target == null) return;
        string who = onSelf ? string.Empty : RoleLabel(target);

        ItemType item;
        if (kind == UseKind.Medkit)
        {
            float missing = target.MaxHP - target.CurrentHP;
            if (missing <= 0.01f)
            {
                SetFeedback(onSelf ? "Already at full health" : $"{who} is at full health", HoldSeconds);
                return;
            }
            if (!ChooseMedkit(missing, out item))
            {
                SetFeedback("No medkits — restock at the medical locker", HoldSeconds);
                return;
            }
            channelLabel = onSelf ? "Using medkit" : $"Treating {who}";
        }
        else
        {
            if (GetCount(ItemType.Antidote) <= 0)
            {
                SetFeedback("No antidote — restock at the medical locker", HoldSeconds);
                return;
            }
            if (!HasAntidoteCurable(target))
            {
                SetFeedback(onSelf ? "No poison or radiation to treat" : $"{who}: no poison or radiation",
                            HoldSeconds);
                return;
            }
            item = ItemType.Antidote;
            channelLabel = onSelf ? "Applying antidote" : $"Applying antidote to {who}";
        }

        MedKitProfile profile = ResolveProfile(OwnerClientId);
        channelItem = item;
        channelTarget = target;
        channelOnSelf = onSelf;
        channelDuration = profile.ChannelSeconds;
        channelElapsed = 0f;
        channelStartPosition = transform.position;
        channeling = true;
        ShowChannelProgress();

        LogV($"Canale avviato: {item} su {(onSelf ? "se stesso" : $"client {target.OwnerClientId}")} " +
             $"({channelDuration:F1}s)");
    }

    /// <summary>
    /// Q35-a — medikit scelto in automatico: l'avanzato se mancano almeno
    /// AdvancedHealAmount HP, altrimenti il base; se uno dei due è finito, l'altro.
    /// </summary>
    private bool ChooseMedkit(float missingHp, out ItemType item)
    {
        bool hasBase = GetCount(ItemType.MedkitBase) > 0;
        bool hasAdvanced = GetCount(ItemType.MedkitAdvanced) > 0;

        if (hasAdvanced && missingHp >= config.AdvancedHealAmount) { item = ItemType.MedkitAdvanced; return true; }
        if (hasBase) { item = ItemType.MedkitBase; return true; }
        if (hasAdvanced) { item = ItemType.MedkitAdvanced; return true; }

        item = ItemType.MedkitBase;
        return false;
    }

    /// <summary>Il bersaglio ha almeno uno stato curabile dall'antidoto? Legge la maschera replicata (client).</summary>
    private static bool HasAntidoteCurable(PlayerHealthSystem target)
    {
        if (target == null) return false;
        PlayerStatusEffects effects = target.GetComponent<PlayerStatusEffects>();
        if (effects == null) return false;

        for (int i = 0; i < AntidoteCures.Length; i++)
        {
            if (effects.IsActive(AntidoteCures[i]))
                return true;
        }
        return false;
    }

    // ── Canale (owner) ─────────────────────────────────────────────────────────

    private void Update()
    {
        if (!IsOwner || !IsSpawned) return;

        if (channeling)
        {
            TickChannel();
            return;
        }

        if (feedbackTimer > 0f)
        {
            feedbackTimer -= Time.deltaTime;
            if (feedbackTimer <= 0f)
                SetFeedback(string.Empty, 0f);
        }
    }

    private void TickChannel()
    {
        // Postazione, tablet, letto, a terra: il movimento è bloccato da altro → interrompi.
        if (!CanContinueLocally())
        {
            AbortChannel();
            return;
        }

        // Bersaglio sparito (disconnessione) o non più vivo.
        if (channelTarget == null || !channelTarget.IsSpawned || !channelTarget.IsAlive)
        {
            AbortChannel();
            return;
        }

        // Spostamento oltre la soglia dal punto d'inizio.
        float maxMove = config != null ? config.MaxMoveDistance : 1f;
        if ((transform.position - channelStartPosition).sqrMagnitude > maxMove * maxMove)
        {
            AbortChannel();
            return;
        }

        // Compagno: deve restare sotto il mirino, entro la portata.
        if (!channelOnSelf && !ReferenceEquals(FindLookedPlayer(), channelTarget))
        {
            AbortChannel();
            return;
        }

        channelElapsed += Time.deltaTime;
        if (channelElapsed >= channelDuration)
        {
            CompleteChannel();
            return;
        }

        ShowChannelProgress();
    }

    private void CompleteChannel()
    {
        channeling = false;
        ulong targetClientId = channelTarget != null ? channelTarget.OwnerClientId : OwnerClientId;
        channelTarget = null;

        // In attesa dell'esito dal server: la riga resta sul canale; l'esito la sostituisce.
        SetFeedback($"{channelLabel}…", HoldSeconds);
        UseItemServerRpc(channelItem, targetClientId);
    }

    private void AbortChannel()
    {
        channeling = false;
        channelTarget = null;
        SetFeedback("Treatment interrupted", HoldSeconds);
        LogV("Canale interrotto.");
    }

    private void ShowChannelProgress()
    {
        int percent = channelDuration > 0f
            ? Mathf.Clamp(Mathf.FloorToInt(channelElapsed / channelDuration * 100f), 0, 99)
            : 0;
        SetFeedback($"{channelLabel}… {percent}%", 0f);
    }

    // ── Gate locali (owner) ────────────────────────────────────────────────────

    /// <summary>
    /// Si può avviare un uso: vivo, movimento libero (non a postazione, tablet, letto o a
    /// terra), tablet chiuso, nessuna interazione continua in corso (per esempio il defib).
    /// </summary>
    private bool CanUseLocally()
    {
        if (health == null || !health.IsAlive) return false;
        if (controller == null || !controller.enabled) return false;
        if (tablet != null && tablet.IsBusy) return false;
        if (interaction != null && interaction.IsInteracting) return false;
        return true;
    }

    /// <summary>Si può proseguire un canale già avviato (stesse condizioni tranne l'interazione).</summary>
    private bool CanContinueLocally()
    {
        if (health == null || !health.IsAlive) return false;
        if (controller == null || !controller.enabled) return false;
        if (tablet != null && tablet.IsBusy) return false;
        return true;
    }

    /// <summary>
    /// Q31-a — giocatore sotto il mirino entro la portata: il collider più vicino colpito
    /// dal raggio dedicato, escluso il proprio. Se è un compagno torna la sua salute,
    /// altrimenti null (parete, oggetto, niente).
    /// </summary>
    private PlayerHealthSystem FindLookedPlayer()
    {
        if (cameraTransform == null) return null;

        float range = config != null ? config.TargetRange : 2.5f;
        Ray ray = new Ray(cameraTransform.position, cameraTransform.forward);
        int count = Physics.RaycastNonAlloc(ray, rayHits, range, targetRayMask, QueryTriggerInteraction.Ignore);

        Collider nearest = null;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Collider hitCollider = rayHits[i].collider;
            if (hitCollider == null) continue;
            if (hitCollider.transform.IsChildOf(transform)) continue;   // il proprio corpo
            if (rayHits[i].distance < nearestDistance)
            {
                nearestDistance = rayHits[i].distance;
                nearest = hitCollider;
            }
        }

        if (nearest == null) return null;
        PlayerHealthSystem other = nearest.GetComponentInParent<PlayerHealthSystem>();
        return other != null && other != health ? other : null;
    }

    // ── RPC: uso (owner → server) ──────────────────────────────────────────────

    [Rpc(SendTo.Server)]
    private void UseItemServerRpc(ItemType item, ulong targetClientId, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != OwnerClientId)
        {
            Debug.LogWarning($"[PlayerMedKit] Uso rifiutato: il client {sender} ha chiesto di usare il kit " +
                             $"del client {OwnerClientId}.");
            return;
        }

        UseResult result = ServerApplyUse(item, targetClientId, out float healed, out byte curedMask);
        UseResultOwnerRpc(result, targetClientId, healed, curedMask);
    }

    /// <summary>Validazione ed effetto dell'uso. SERVER ONLY. Consuma l'item solo se ha avuto effetto.</summary>
    private UseResult ServerApplyUse(ItemType item, ulong targetClientId, out float healed, out byte curedMask)
    {
        healed = 0f;
        curedMask = 0;

        if (config == null) return UseResult.Failed;
        if (health == null || !health.IsAlive) return UseResult.Failed;   // chi usa è finito a terra
        if (GetCap(item) <= 0 || GetCount(item) <= 0) return UseResult.NoItem;

        if (!PlayerHealthSystem.TryGetByClientId(targetClientId, out PlayerHealthSystem target) ||
            target == null || !target.IsAlive)
            return UseResult.TargetInvalid;

        if (item == ItemType.Antidote)
        {
            if (!PlayerStatusEffects.TryGetByClientId(targetClientId, out PlayerStatusEffects effects) ||
                effects == null)
                return UseResult.TargetInvalid;

            for (int i = 0; i < AntidoteCures.Length; i++)
            {
                StatusEffectType type = AntidoteCures[i];
                if (!effects.HasEffect(type)) continue;
                effects.RemoveEffect(type);
                curedMask |= (byte)(1 << (int)type);
            }

            if (curedMask == 0) return UseResult.NothingToTreat;

            ServerSetCount(item, GetCount(item) - 1);
            LogV($"Antidoto su client {targetClientId}: maschera curata {curedMask}.");
            return UseResult.Cured;
        }

        // Medikit (Q33-a): HP nominali × moltiplicatore del profilo di chi usa (ruolo dal server).
        float amount = config.HealAmountFor(item) * ResolveProfile(OwnerClientId).EffectMultiplier;
        healed = target.ApplyHeal(amount);
        if (healed <= 0f) return UseResult.NothingToTreat;

        ServerSetCount(item, GetCount(item) - 1);
        LogV($"{item} su client {targetClientId}: +{healed:F1} HP.");
        return UseResult.Healed;
    }

    [Rpc(SendTo.Owner)]
    private void UseResultOwnerRpc(UseResult result, ulong targetClientId, float healed, byte curedMask)
    {
        bool onSelf = targetClientId == OwnerClientId;
        string suffix = onSelf ? string.Empty : $" ({CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(targetClientId))})";

        switch (result)
        {
            case UseResult.Healed:
                SetFeedback($"+{Mathf.RoundToInt(healed)} HP{suffix}", HoldSeconds);
                break;
            case UseResult.Cured:
                SetFeedback(CuredLabel(curedMask) + suffix, HoldSeconds);
                break;
            case UseResult.NothingToTreat:
                SetFeedback("Nothing to treat" + suffix, HoldSeconds);
                break;
            case UseResult.NoItem:
                SetFeedback("Item not available", HoldSeconds);
                break;
            default:
                SetFeedback("Treatment failed", HoldSeconds);
                break;
        }
    }

    private static string CuredLabel(byte curedMask)
    {
        bool poison = (curedMask & (1 << (int)StatusEffectType.Poison)) != 0;
        bool radiation = (curedMask & (1 << (int)StatusEffectType.Radiation)) != 0;
        if (poison && radiation) return "Poison and radiation cured";
        if (poison) return "Poison cured";
        if (radiation) return "Radiation cured";
        return "Nothing to treat";
    }

    // ── Rifornimento all'armadietto (Q30-a) ────────────────────────────────────

    /// <summary>
    /// Chiede al server di riempire il kit dalla stiva della nave. Chiamato da
    /// MedicalSupplyLocker sul client del proprietario.
    /// </summary>
    public void RequestRestock()
    {
        if (!IsOwner || !IsSpawned) return;
        RestockServerRpc();
    }

    [Rpc(SendTo.Server)]
    private void RestockServerRpc(RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != OwnerClientId)
        {
            Debug.LogWarning($"[PlayerMedKit] Rifornimento rifiutato: il client {sender} ha chiesto di " +
                             $"riempire il kit del client {OwnerClientId}.");
            return;
        }

        if (health == null || !health.IsAlive) return;

        RestockResult result = ServerRestockFromShip(out int takenBase, out int takenAdvanced, out int takenAntidote);
        RestockResultOwnerRpc(result, (byte)takenBase, (byte)takenAdvanced, (byte)takenAntidote);
    }

    /// <summary>
    /// Riempie il kit fino alla capienza prelevando dalla stiva (InventorySystem.TryConsume).
    /// SERVER ONLY. Trasferimento esplicito: quello che entra nel kit esce dalla stiva.
    /// </summary>
    private RestockResult ServerRestockFromShip(out int takenBase, out int takenAdvanced, out int takenAntidote)
    {
        takenBase = takenAdvanced = takenAntidote = 0;

        if (!IsServer || config == null) return RestockResult.Failed;
        InventorySystem inventory = InventorySystem.Instance;
        if (inventory == null)
        {
            Debug.LogError("[PlayerMedKit] InventorySystem assente: impossibile rifornire il kit medico.");
            return RestockResult.Failed;
        }

        if (IsFull) return RestockResult.KitFull;

        takenBase = TakeFromShip(inventory, ItemType.MedkitBase);
        takenAdvanced = TakeFromShip(inventory, ItemType.MedkitAdvanced);
        takenAntidote = TakeFromShip(inventory, ItemType.Antidote);

        if (takenBase + takenAdvanced + takenAntidote == 0) return RestockResult.NoSupplies;

        LogV($"Rifornito dalla stiva: base +{takenBase}, avanzato +{takenAdvanced}, antidoto +{takenAntidote}.");
        return RestockResult.Restocked;
    }

    private int TakeFromShip(InventorySystem inventory, ItemType type)
    {
        int space = GetCap(type) - GetCount(type);
        if (space <= 0) return 0;

        int take = Mathf.Min(space, inventory.GetQuantity(type));
        if (take <= 0) return 0;
        if (!inventory.TryConsume(type, take)) return 0;

        ServerSetCount(type, GetCount(type) + take);
        return take;
    }

    [Rpc(SendTo.Owner)]
    private void RestockResultOwnerRpc(RestockResult result, byte takenBase, byte takenAdvanced, byte takenAntidote)
    {
        switch (result)
        {
            case RestockResult.Restocked:
                var parts = new List<string>(3);
                if (takenBase > 0) parts.Add($"+{takenBase} medkit");
                if (takenAdvanced > 0) parts.Add($"+{takenAdvanced} advanced medkit");
                if (takenAntidote > 0) parts.Add($"+{takenAntidote} antidote");
                SetFeedback("Kit restocked: " + string.Join(", ", parts), HoldSeconds);
                break;
            case RestockResult.KitFull:
                SetFeedback("Medical kit already full", HoldSeconds);
                break;
            case RestockResult.NoSupplies:
                SetFeedback("No medical supplies aboard", HoldSeconds);
                break;
            default:
                SetFeedback("Restock failed", HoldSeconds);
                break;
        }
    }

    // ── API server ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Svuota il kit. SERVER ONLY. Chiamato da PlayerHealthSystem.ServerRespawn: il clone
    /// è un corpo nuovo e nasce senza kit (Q30-a).
    /// </summary>
    public void ServerClearKit()
    {
        if (!IsServer) return;
        netMedkitBase.Value = 0;
        netMedkitAdvanced.Value = 0;
        netAntidote.Value = 0;
        LogV("Kit svuotato.");
    }

    private void ServerSetCount(ItemType type, int value)
    {
        if (!IsServer) return;
        byte clamped = (byte)Mathf.Clamp(value, 0, 255);
        switch (type)
        {
            case ItemType.MedkitBase:     netMedkitBase.Value = clamped; break;
            case ItemType.MedkitAdvanced: netMedkitAdvanced.Value = clamped; break;
            case ItemType.Antidote:       netAntidote.Value = clamped; break;
            default:
                Debug.LogError($"[PlayerMedKit] ItemType {type} non appartiene al kit medico.");
                break;
        }
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Profilo di chi usa il kit (Rev U): Corpsman oppure default. Deterministico su stato
    /// replicato: il client lo usa per la durata del canale, il server per l'effetto.
    /// </summary>
    private MedKitProfile ResolveProfile(ulong userClientId)
    {
        bool isCorpsman = PlayerCrewRole.HasRole(userClientId, CrewRole.Corpsman);
        if (config != null) return config.ProfileFor(isCorpsman);
        return new MedKitProfile(isCorpsman ? 1.5f : 3f, isCorpsman ? 1f : 0.6f);
    }

    private static string RoleLabel(PlayerHealthSystem player)
        => CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(player.OwnerClientId));

    private float HoldSeconds => config != null ? config.FeedbackHoldSeconds : 2f;

    private void SetFeedback(string text, float holdSeconds)
    {
        feedbackTimer = holdSeconds;
        if (feedbackText != null)
            feedbackText.text = text;
    }

    private void LogV(string message)
    {
        if (logVerbose) Debug.Log($"[PlayerMedKit/{OwnerClientId}] {message}");
    }

    // ── Debug overlay (solo Editor/Development, solo server) ───────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!showDebugUI) return;
        if (!IsServer || !IsSpawned) return;

        // Accanto al pannello di PlayerStatusEffects (x 570–910), stessa banda per OwnerClientId.
        float y = 310 + (OwnerClientId * 90f);

        GUILayout.BeginArea(new Rect(920, y, 300, 85));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[PlayerMedKit] Client {OwnerClientId} — base {GetCount(ItemType.MedkitBase)}/" +
                        $"{GetCap(ItemType.MedkitBase)} · avanz. {GetCount(ItemType.MedkitAdvanced)}/" +
                        $"{GetCap(ItemType.MedkitAdvanced)} · antid. {GetCount(ItemType.Antidote)}/" +
                        $"{GetCap(ItemType.Antidote)}");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Dalla stiva"))
        {
            RestockResult result = ServerRestockFromShip(out int b, out int a, out int x);
            RestockResultOwnerRpc(result, (byte)b, (byte)a, (byte)x);
        }
        if (GUILayout.Button("Pieno (gratis)"))
        {
            ServerSetCount(ItemType.MedkitBase, GetCap(ItemType.MedkitBase));
            ServerSetCount(ItemType.MedkitAdvanced, GetCap(ItemType.MedkitAdvanced));
            ServerSetCount(ItemType.Antidote, GetCap(ItemType.Antidote));
        }
        if (GUILayout.Button("Svuota")) ServerClearKit();
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}
