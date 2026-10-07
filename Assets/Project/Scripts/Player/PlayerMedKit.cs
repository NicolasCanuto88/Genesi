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
/// DROGHE (Rev BR-b · workshop StatModifier Q41–Q50, tutte "a"): tre contatori replicati in
/// più (Adrenaline, Hazmat, Combat Stim), stessa capienza da SO e stesso armadietto.
/// - INPUT: "UseDrug" (K / D-pad destra) usa la droga SELEZIONATA; "CycleDrug" (L / D-pad
///   sinistra) passa alla successiva tra quelle PRESENTI nel kit, in ordine fisso
///   Adrenaline → Hazmat → Combat Stim (Q46-a). La selezione è solo locale (owner): al server
///   arriva il tipo nell'RPC d'uso, come per i medikit.
/// - BERSAGLIO: Adrenaline e Hazmat solo su se stessi (auto-somministrate); Combat Stim sul
///   compagno sotto il mirino, altrimenti su se stessi (regola Q31-a).
/// - CANALE: lo stesso profilo di ruolo dei medikit (Corpsman 1,5 s, altri 3 s).
/// - EFFETTI (server): Adrenaline +HP da SO uguali per tutti e stamina piena (RPC d'esito al
///   proprietario, dove vive la stamina); Hazmat e Combat Stim applicano gli stati buff di
///   PlayerStatusEffects (valori negli asset SED_*). La Combat Stim di un non-Corpsman dura
///   il 60% (moltiplicatore del profilo, Q43-a).
/// - CONSUMO (Q47-a): sempre, a canale completo con bersaglio vivo. Unico blocco preventivo:
///   Adrenaline con HP e stamina già pieni (lo sa il client).
/// - TIER (Q48-a): sotto Medbay T2 l'armadietto non versa Combat Stim (capienza di
///   rifornimento 0, il kit risulta pieno senza) e lo dice nell'esito. Le stim già nel kit
///   restano usabili.
///
/// BOMBA CURATIVA (Rev BS-b · workshop bomba curativa Q51–Q67, tutte come raccomandate): un
/// contatore replicato in più (HealingGrenade), capienza da SO, stesso armadietto, nessun tier
/// minimo (Q65-a). Il kit la PORTA soltanto: la lancia PlayerThrower (G / RB), che sul server la
/// consuma con ServerTryConsume solo se il lancio parte e usa ProfileFor per il moltiplicatore di
/// ruolo (Q57-a). Mira e kit si escludono (Q66-a): durante la mira H/J/K/L sono ignorati, durante
/// un canale la mira non si apre (IsChanneling).
///
/// NANOMEDIC DRONE (Rev BU-b · workshop Corpsman T3–T4 Q86-a / Q87-a): un contatore replicato in
/// più (NanomedicDrone), capienza da SO, stesso armadietto, prelevabile da Medbay T3 (come la stim
/// da T2: sotto il tier l'armadietto non lo versa e lo dice nell'esito). Il kit lo PORTA soltanto:
/// lo lancia PlayerNanomedicDrone (V / LB), che usa FindLookedTeammate per il bersaglio (stessa
/// regola Q31-a), ShowFeedback per la riga di esito, ServerTryConsume e ProfileFor sul server.
///
/// ANTIDOTE INJECTOR (Rev BU-c · workshop Corpsman T3–T4 Q88-a): un contatore replicato in più
/// (AntidoteInjector), capienza da SO, stesso armadietto, prelevabile da Medbay T4. Cura in campo
/// Veleno, Radiazioni E Ferite Composte (InjectorCures), senza la regola di tier della Recovery Bay.
/// Nessun tasto nuovo: J (UseAntidote) sceglie da solo (come base/avanzato, Q35-a): l'iniettore se
/// il bersaglio ha le Ferite Composte, altrimenti l'antidoto, e l'iniettore se l'antidoto è finito.
/// Stesso canale di ruolo dell'antidoto; consumo solo se ha curato qualcosa. Rev BU-c: le note
/// "needs Medbay Tn" del rifornimento viaggiano in una maschera per tipo (TierBlockedMask).
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

    // Rev BR-b — droghe
    private readonly NetworkVariable<byte> netAdrenaline = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> netHazmat = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> netCombatStim = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Rev BS-b — bomba curativa (la lancia PlayerThrower; il kit la porta)
    private readonly NetworkVariable<byte> netHealingGrenade = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Rev BU-b — Nanomedic Drone (lo lancia PlayerNanomedicDrone; il kit lo porta)
    private readonly NetworkVariable<byte> netNanomedicDrone = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Rev BU-c — Antidote Injector (lo usa J al posto dell'antidoto quando serve)
    private readonly NetworkVariable<byte> netAntidoteInjector = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Tutti i tipi che il kit può contenere (rifornimento, pienezza, debug).</summary>
    private static readonly ItemType[] KitTypes =
    {
        ItemType.MedkitBase,
        ItemType.MedkitAdvanced,
        ItemType.Antidote,
        ItemType.Adrenaline,
        ItemType.HazmatInjection,
        ItemType.CombatStim,
        ItemType.HealingGrenade,  // Rev BS-b
        ItemType.NanomedicDrone,  // Rev BU-b
        ItemType.AntidoteInjector // Rev BU-c
    };

    /// <summary>Rev BR-b (Q46-a) — droghe nell'ordine fisso del ciclo di selezione.</summary>
    private static readonly ItemType[] DrugOrder =
    {
        ItemType.Adrenaline,
        ItemType.HazmatInjection,
        ItemType.CombatStim
    };

    /// <summary>
    /// Stati curati dall'antidoto (Q34-a). Decisione di design, non tuning: le Ferite
    /// Composte NON ci sono e non devono entrarci (restano alla Recovery Bay T3).
    /// </summary>
    private static readonly StatusEffectType[] AntidoteCures =
    {
        StatusEffectType.Poison,
        StatusEffectType.Radiation
    };

    /// <summary>
    /// Rev BU-c (Q88-a) — stati curati dall'Antidote Injector: quelli dell'antidoto più le Ferite
    /// Composte, in campo e senza la regola di tier della Recovery Bay. Decisione di design, non tuning.
    /// </summary>
    private static readonly StatusEffectType[] InjectorCures =
    {
        StatusEffectType.Poison,
        StatusEffectType.Radiation,
        StatusEffectType.CompoundWounds
    };

    // ── Registro statico per-clientId — stesso pattern di PlayerHealthSystem ──
    private static readonly Dictionary<ulong, PlayerMedKit> activeByClientId = new Dictionary<ulong, PlayerMedKit>();

    /// <summary>Istanza del client locale (IsOwner): la usano l'armadietto e il tab Profilo.</summary>
    public static PlayerMedKit LocalInstance { get; private set; }

    /// <summary>Trova il kit del client indicato.</summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerMedKit instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    /// <summary>
    /// Fired SOLO sul client proprietario quando il contenuto del proprio kit cambia, o quando
    /// cambia la droga selezionata (Rev BR-b).
    /// </summary>
    public static event Action OnLocalKitChanged;

    // ── Esiti (server → owner) ──
    private enum UseResult : byte
    {
        Healed = 0,
        Cured = 1,
        NothingToTreat = 2,
        NoItem = 3,
        TargetInvalid = 4,
        Failed = 5,
        DrugApplied = 6   // Rev BR-b
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
        Antidote = 1,
        Drug = 2   // Rev BR-b: la droga selezionata
    }

    // ── Riferimenti sibling ──
    private PlayerHealthSystem health;       // server e owner
    private PlayerController controller;     // owner: gate di disponibilità
    private InteractionSystem interaction;   // owner: niente kit durante un'interazione continua
    private TabletStation tablet;            // owner: niente kit a tablet aperto
    private Transform cameraTransform;       // owner: origine del raggio di mira
    private PlayerThrower thrower;           // owner: niente kit durante la mira (Rev BS-b · Q66-a)

    // ── Canale (solo owner) ──
    private bool channeling;
    private ItemType channelItem;
    private PlayerHealthSystem channelTarget;
    private bool channelOnSelf;
    private float channelElapsed;
    private float channelDuration;
    private Vector3 channelStartPosition;
    private string channelLabel = string.Empty;

    // ── Selezione droga (solo owner, Rev BR-b · Q46-a) ──
    private ItemType selectedDrug = ItemType.Adrenaline;

    // ── Feedback (solo owner) ──
    private float feedbackTimer;

    private readonly RaycastHit[] rayHits = new RaycastHit[8];

    // ── API pubblica (server e client) ─────────────────────────────────────────

    /// <summary>Pezzi nel kit per il tipo indicato. Tipi fuori dal kit → 0. Replicato.</summary>
    public int GetCount(ItemType type)
    {
        switch (type)
        {
            case ItemType.MedkitBase: return netMedkitBase.Value;
            case ItemType.MedkitAdvanced: return netMedkitAdvanced.Value;
            case ItemType.Antidote: return netAntidote.Value;
            case ItemType.Adrenaline: return netAdrenaline.Value;
            case ItemType.HazmatInjection: return netHazmat.Value;
            case ItemType.CombatStim: return netCombatStim.Value;
            case ItemType.HealingGrenade: return netHealingGrenade.Value;
            case ItemType.NanomedicDrone: return netNanomedicDrone.Value;
            case ItemType.AntidoteInjector: return netAntidoteInjector.Value;
            default: return 0;
        }
    }

    /// <summary>Capienza del kit per il tipo indicato (da MedKitConfig). Senza config → 0.</summary>
    public int GetCap(ItemType type) => config != null ? config.CapFor(type) : 0;

    /// <summary>
    /// Rev BR-b (Q48-a) — capienza ai fini del RIFORNIMENTO: 0 se il tier della Medbay è sotto il
    /// minimo del tipo (Combat Stim sotto T2). Vale su server e client (tier replicato).
    /// </summary>
    public int GetRestockCap(ItemType type)
    {
        if (config == null) return 0;
        return MedbaySystem.CurrentTierOrDefault >= config.MinMedbayTierFor(type) ? config.CapFor(type) : 0;
    }

    /// <summary>
    /// true se nessun tipo del kit può ricevere altro dall'armadietto (anche senza config).
    /// Rev BR-b: considera la capienza di rifornimento, quindi sotto T2 il kit è pieno senza stim.
    /// </summary>
    public bool IsFull
    {
        get
        {
            for (int i = 0; i < KitTypes.Length; i++)
                if (GetCount(KitTypes[i]) < GetRestockCap(KitTypes[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// Rev BS-b (Q66-a) — true mentre il proprietario incanala medikit, antidoto o droga (solo
    /// owner). PlayerThrower non apre la mira finché è true.
    /// </summary>
    public bool IsChanneling => channeling;

    /// <summary>
    /// Rev BS-b (Q57-a) — profilo di ruolo del client indicato (Corpsman oppure default). Stesso
    /// calcolo dei medikit; PlayerThrower ne usa il moltiplicatore per la cura del campo.
    /// </summary>
    public MedKitProfile ProfileFor(ulong userClientId) => ResolveProfile(userClientId);

    /// <summary>
    /// Rev BU-b — compagno sotto il mirino entro la portata del kit (regola Q31-a), oppure null
    /// (nessuno, parete, oggetto). Solo owner. Lo usa PlayerNanomedicDrone per il bersaglio.
    /// </summary>
    public PlayerHealthSystem FindLookedTeammate() => IsOwner ? FindLookedPlayer() : null;

    /// <summary>
    /// Rev BU-b — scrive una riga di esito sotto il mirino per la durata standard del kit. Solo
    /// owner (sugli altri client non fa nulla). La usa PlayerNanomedicDrone.
    /// </summary>
    public void ShowFeedback(string text)
    {
        if (!IsOwner) return;
        SetFeedback(text, HoldSeconds);
    }

    /// <summary>
    /// Rev BR-b — droga selezionata dal proprietario (solo owner). Se nel kit non c'è nessuna
    /// droga, HasSelectedDrug è false.
    /// </summary>
    public ItemType SelectedDrug => selectedDrug;

    /// <summary>Rev BR-b — true se la droga selezionata è presente nel kit (solo owner).</summary>
    public bool HasSelectedDrug => GetCount(selectedDrug) > 0;

    /// <summary>Rev BR-b — nome di gioco della droga (testi in inglese).</summary>
    public static string DrugLabel(ItemType type)
    {
        switch (type)
        {
            case ItemType.Adrenaline: return "Adrenaline";
            case ItemType.HazmatInjection: return "Hazmat";
            case ItemType.CombatStim: return "Combat Stim";
            default: return type.ToString();
        }
    }

    // ── Lifecycle NGO ──────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        health = GetComponent<PlayerHealthSystem>();
        activeByClientId[OwnerClientId] = this;

        netMedkitBase.OnValueChanged += HandleKitChanged;
        netMedkitAdvanced.OnValueChanged += HandleKitChanged;
        netAntidote.OnValueChanged += HandleKitChanged;
        netAdrenaline.OnValueChanged += HandleKitChanged;
        netHazmat.OnValueChanged += HandleKitChanged;
        netCombatStim.OnValueChanged += HandleKitChanged;
        netHealingGrenade.OnValueChanged += HandleKitChanged;
        netNanomedicDrone.OnValueChanged += HandleKitChanged;
        netAntidoteInjector.OnValueChanged += HandleKitChanged;

        if (IsServer)
        {
            // Q30-a: il kit parte vuoto; si riempie all'armadietto medico.
            netMedkitBase.Value = 0;
            netMedkitAdvanced.Value = 0;
            netAntidote.Value = 0;
            netAdrenaline.Value = 0;
            netHazmat.Value = 0;
            netCombatStim.Value = 0;
            netHealingGrenade.Value = 0;
            netNanomedicDrone.Value = 0;
            netAntidoteInjector.Value = 0;

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
            thrower = GetComponent<PlayerThrower>();
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
        netAdrenaline.OnValueChanged -= HandleKitChanged;
        netHazmat.OnValueChanged -= HandleKitChanged;
        netCombatStim.OnValueChanged -= HandleKitChanged;
        netHealingGrenade.OnValueChanged -= HandleKitChanged;
        netNanomedicDrone.OnValueChanged -= HandleKitChanged;
        netAntidoteInjector.OnValueChanged -= HandleKitChanged;

        if (activeByClientId.TryGetValue(OwnerClientId, out var registered) && registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;

        channeling = false;
        channelTarget = null;
    }

    private void HandleKitChanged(byte previous, byte current)
    {
        if (!IsOwner) return;

        // Rev BR-b (Q46-a): se la droga selezionata è finita, passa alla successiva presente.
        if (GetCount(selectedDrug) <= 0)
            SelectNextDrug(selectedDrug, includeCurrent: false);

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

    /// <summary>Rev BR-b — azione "UseDrug" (K / D-pad destra): usa la droga selezionata.</summary>
    public void OnUseDrug(InputValue value)
    {
        if (value.isPressed)
            TryBeginUse(UseKind.Drug);
    }

    /// <summary>
    /// Rev BR-b — azione "CycleDrug" (L / D-pad sinistra): seleziona la droga successiva tra
    /// quelle presenti nel kit (Q46-a). Ignorata negli stessi casi dell'uso (postazione,
    /// tablet, letto, a terra) e durante un canale.
    /// </summary>
    public void OnCycleDrug(InputValue value)
    {
        if (!value.isPressed) return;
        if (!IsOwner || !IsSpawned) return;
        if (channeling || !CanUseLocally()) return;

        if (!SelectNextDrug(selectedDrug, includeCurrent: false))
        {
            SetFeedback("No drugs in kit — restock at the medical locker", HoldSeconds);
            return;
        }

        SetFeedback($"Drug: {DrugLabel(selectedDrug)} ({GetCount(selectedDrug)})", HoldSeconds);
        OnLocalKitChanged?.Invoke();
    }

    /// <summary>
    /// Rev BR-b — seleziona la prima droga presente nel kit a partire da quella DOPO "from"
    /// (o da "from" stessa se includeCurrent), nell'ordine DrugOrder, ciclando. Ritorna false
    /// se il kit non contiene droghe (la selezione resta invariata).
    /// </summary>
    private bool SelectNextDrug(ItemType from, bool includeCurrent)
    {
        int start = Array.IndexOf(DrugOrder, from);
        if (start < 0) start = 0;

        for (int step = includeCurrent ? 0 : 1; step <= DrugOrder.Length; step++)
        {
            ItemType candidate = DrugOrder[(start + step) % DrugOrder.Length];
            if (GetCount(candidate) > 0)
            {
                selectedDrug = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>Rev BR-b — Adrenaline e Hazmat sono auto-somministrate: sempre su se stessi.</summary>
    private static bool IsSelfOnly(ItemType drug)
        => drug == ItemType.Adrenaline || drug == ItemType.HazmatInjection;

    /// <summary>
    /// Rev BR-b — stato buff applicato dalla droga (decisione di design, non tuning). Adrenaline
    /// non ha stato: il suo effetto è istantaneo.
    /// </summary>
    private static bool TryGetDrugStatus(ItemType drug, out StatusEffectType status)
    {
        switch (drug)
        {
            case ItemType.HazmatInjection: status = StatusEffectType.Hazmat; return true;
            case ItemType.CombatStim: status = StatusEffectType.CombatStim; return true;
            default: status = default; return false;
        }
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

        // Rev BR-b: la droga si risolve PRIMA del bersaglio (Adrenaline e Hazmat solo su se stessi).
        if (kind == UseKind.Drug && !SelectNextDrug(selectedDrug, includeCurrent: true))
        {
            SetFeedback("No drugs — restock at the medical locker", HoldSeconds);
            return;
        }
        bool selfOnly = kind == UseKind.Drug && IsSelfOnly(selectedDrug);

        // Q31-a: compagno vivo sotto il mirino entro la portata, altrimenti se stessi.
        PlayerHealthSystem looked = selfOnly ? null : FindLookedPlayer();
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
        else if (kind == UseKind.Drug)
        {
            item = selectedDrug;

            // Q47-a: unico blocco preventivo — Adrenaline con HP e stamina già pieni.
            if (item == ItemType.Adrenaline &&
                health.MaxHP - health.CurrentHP <= 0.01f &&
                controller != null && controller.CurrentStamina >= controller.MaxStamina)
            {
                SetFeedback("Already at full health and stamina", HoldSeconds);
                return;
            }

            string drug = DrugLabel(item);
            channelLabel = onSelf ? $"Injecting {drug}" : $"Injecting {drug} — {who}";
        }
        else
        {
            // Rev BU-c (Q88-a): antidoto o iniettore, scelto da solo.
            if (!ChooseAntidote(target, out item, out string failText))
            {
                SetFeedback(onSelf ? Capitalize(failText) : $"{who}: {failText}", HoldSeconds);
                return;
            }
            channelLabel = item == ItemType.AntidoteInjector
                ? (onSelf ? "Using antidote injector" : $"Injecting {who}")
                : (onSelf ? "Applying antidote" : $"Applying antidote to {who}");
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

    /// <summary>
    /// Rev BU-c (Q88-a) — antidoto o iniettore, scelto in automatico come base/avanzato (Q35-a):
    ///   1. Ferite Composte sul bersaglio e un iniettore nel kit → iniettore (cura tutto);
    ///   2. Veleno o Radiazioni e un antidoto → antidoto;
    ///   3. Veleno o Radiazioni, antidoto finito, iniettore nel kit → iniettore.
    /// Altrimenti false con il testo dell'esito in minuscolo (il chiamante aggiunge il ruolo o la
    /// maiuscola iniziale).
    /// Legge solo stato replicato (client).
    /// </summary>
    private bool ChooseAntidote(PlayerHealthSystem target, out ItemType item, out string failText)
    {
        bool hasAntidote = GetCount(ItemType.Antidote) > 0;
        bool hasInjector = GetCount(ItemType.AntidoteInjector) > 0;
        bool antidoteCurable = HasAntidoteCurable(target);
        bool hasWounds = HasCompoundWounds(target);

        item = ItemType.Antidote;
        failText = string.Empty;

        if (hasWounds && hasInjector) { item = ItemType.AntidoteInjector; return true; }
        if (antidoteCurable && hasAntidote) { item = ItemType.Antidote; return true; }
        if (antidoteCurable && hasInjector) { item = ItemType.AntidoteInjector; return true; }

        if (antidoteCurable)
            failText = "no antidote — restock at the medical locker";
        else if (hasWounds)
            failText = "compound wounds need an Antidote Injector or Medbay T3";
        else
            failText = "no poison or radiation to treat";
        return false;
    }

    /// <summary>Rev BU-c — prima lettera maiuscola (testi di esito che aprono la riga).</summary>
    private static string Capitalize(string text)
        => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

    /// <summary>Rev BU-c — il bersaglio ha le Ferite Composte? Maschera replicata (client).</summary>
    private static bool HasCompoundWounds(PlayerHealthSystem target)
    {
        if (target == null) return false;
        PlayerStatusEffects effects = target.GetComponent<PlayerStatusEffects>();
        return effects != null && effects.IsActive(StatusEffectType.CompoundWounds);
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
    /// terra), tablet chiuso, nessuna interazione continua in corso (per esempio il defib),
    /// nessuna mira di lancio aperta (Rev BS-b · Q66-a).
    /// </summary>
    private bool CanUseLocally()
    {
        if (health == null || !health.IsAlive) return false;
        if (controller == null || !controller.enabled) return false;
        if (tablet != null && tablet.IsBusy) return false;
        if (interaction != null && interaction.IsInteracting) return false;
        if (thrower != null && thrower.IsAiming) return false;
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
        UseResultOwnerRpc(result, item, targetClientId, healed, curedMask);
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

        if (IsDrug(item))
            return ServerApplyDrug(item, targetClientId, out healed);

        if (item == ItemType.Antidote || item == ItemType.AntidoteInjector)
        {
            if (!PlayerStatusEffects.TryGetByClientId(targetClientId, out PlayerStatusEffects effects) ||
                effects == null)
                return UseResult.TargetInvalid;

            // Rev BU-c (Q88-a): l'iniettore cura anche le Ferite Composte, senza regola di tier.
            StatusEffectType[] cures = item == ItemType.AntidoteInjector ? InjectorCures : AntidoteCures;
            for (int i = 0; i < cures.Length; i++)
            {
                StatusEffectType type = cures[i];
                if (!effects.HasEffect(type)) continue;
                effects.RemoveEffect(type);
                curedMask |= (byte)(1 << (int)type);
            }

            if (curedMask == 0) return UseResult.NothingToTreat;

            ServerSetCount(item, GetCount(item) - 1);
            LogV($"{item} su client {targetClientId}: maschera curata {curedMask}.");
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

    /// <summary>Rev BR-b — il tipo è una droga del kit.</summary>
    private static bool IsDrug(ItemType item) => Array.IndexOf(DrugOrder, item) >= 0;

    /// <summary>
    /// Rev BR-b — effetto di una droga. SERVER ONLY. Chiamato da ServerApplyUse dopo i controlli
    /// comuni (config, chi usa è vivo, item nel kit, bersaglio vivo). Consumo sempre (Q47-a).
    /// </summary>
    private UseResult ServerApplyDrug(ItemType item, ulong targetClientId, out float healed)
    {
        healed = 0f;

        // Auto-somministrate: il bersaglio dichiarato dal client deve essere il proprietario.
        if (IsSelfOnly(item) && targetClientId != OwnerClientId)
            return UseResult.TargetInvalid;

        if (item == ItemType.Adrenaline)
        {
            // HP uguali per tutti (Q43-a). La stamina si riempie sul proprietario (RPC d'esito).
            healed = health.ApplyHeal(config.AdrenalineHealAmount);
            ServerSetCount(item, GetCount(item) - 1);
            LogV($"Adrenaline: +{healed:F1} HP, stamina piena.");
            return UseResult.DrugApplied;
        }

        if (!TryGetDrugStatus(item, out StatusEffectType status)) return UseResult.Failed;
        if (!PlayerStatusEffects.TryGetByClientId(targetClientId, out PlayerStatusEffects effects) ||
            effects == null)
            return UseResult.TargetInvalid;

        // Combat Stim (Q43-a): durata × effetto del profilo di chi inietta (non-Corpsman 60%).
        // Hazmat: auto-somministrata, durata piena per tutti.
        float durationScale = item == ItemType.CombatStim
            ? ResolveProfile(OwnerClientId).EffectMultiplier
            : 1f;

        effects.ApplyEffect(status, durationScale);
        ServerSetCount(item, GetCount(item) - 1);
        LogV($"{item} su client {targetClientId} (durata ×{durationScale:F2}).");
        return UseResult.DrugApplied;
    }

    [Rpc(SendTo.Owner)]
    private void UseResultOwnerRpc(UseResult result, ItemType item, ulong targetClientId, float healed, byte curedMask)
    {
        bool onSelf = targetClientId == OwnerClientId;
        string suffix = onSelf ? string.Empty : $" ({CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(targetClientId))})";

        switch (result)
        {
            case UseResult.DrugApplied:
                if (item == ItemType.Adrenaline)
                {
                    if (controller != null) controller.RefillStamina();   // Rev BR-b: la stamina vive qui
                    SetFeedback(healed > 0f
                        ? $"Adrenaline: +{Mathf.RoundToInt(healed)} HP, stamina restored"
                        : "Adrenaline: stamina restored", HoldSeconds);
                }
                else
                {
                    SetFeedback($"{DrugLabel(item)} active{suffix}", HoldSeconds);
                }
                break;
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

    /// <summary>Esito di una cura di stati: "Poison cured", "Poison and radiation cured", … (Rev BU-c: + ferite).</summary>
    private static string CuredLabel(byte curedMask)
    {
        var names = new List<string>(3);
        if ((curedMask & (1 << (int)StatusEffectType.Poison)) != 0) names.Add("poison");
        if ((curedMask & (1 << (int)StatusEffectType.Radiation)) != 0) names.Add("radiation");
        if ((curedMask & (1 << (int)StatusEffectType.CompoundWounds)) != 0) names.Add("compound wounds");
        if (names.Count == 0) return "Nothing to treat";

        string list = names.Count == 1
            ? names[0]
            : string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
        return Capitalize(list) + " cured";
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

        RestockResult result = ServerRestockFromShip(out RestockTaken taken);
        RestockResultOwnerRpc(result, taken.Base, taken.Advanced, taken.Antidote,
                              taken.Adrenaline, taken.Hazmat, taken.CombatStim, taken.HealingGrenade,
                              taken.NanomedicDrone, taken.AntidoteInjector, taken.TierBlockedMask);
    }

    /// <summary>
    /// Rev BR-b — pezzi prelevati dalla stiva in un rifornimento (solo server). Rev BS-b: + bomba.
    /// Rev BU-b: + Nanomedic Drone. Rev BU-c: + Antidote Injector; i blocchi di tier in una maschera.
    /// </summary>
    private struct RestockTaken
    {
        public byte Base, Advanced, Antidote, Adrenaline, Hazmat, CombatStim, HealingGrenade, NanomedicDrone,
                    AntidoteInjector;

        /// <summary>
        /// Q48-a / Rev BU-c — bit (1 &lt;&lt; ItemType) dei tipi rimasti fuori SOLO per il tier della Medbay
        /// (spazio nel kit e scorte in stiva, ma capienza di rifornimento 0): Combat Stim, drone, iniettore.
        /// </summary>
        public ushort TierBlockedMask;
    }

    /// <summary>
    /// Riempie il kit fino alla capienza prelevando dalla stiva (InventorySystem.TryConsume).
    /// SERVER ONLY. Trasferimento esplicito: quello che entra nel kit esce dalla stiva.
    /// </summary>
    private RestockResult ServerRestockFromShip(out RestockTaken taken)
    {
        taken = default;

        if (!IsServer || config == null) return RestockResult.Failed;
        InventorySystem inventory = InventorySystem.Instance;
        if (inventory == null)
        {
            Debug.LogError("[PlayerMedKit] InventorySystem assente: impossibile rifornire il kit medico.");
            return RestockResult.Failed;
        }

        // Q48-a: segnala la stim bloccata dal tier (spazio nel kit e scorte in stiva, ma Medbay sotto il minimo).
        for (int i = 0; i < KitTypes.Length; i++)
        {
            if (IsBlockedByTier(inventory, KitTypes[i]))
                taken.TierBlockedMask |= (ushort)(1 << (int)KitTypes[i]);
        }

        if (IsFull) return RestockResult.KitFull;

        taken.Base = TakeFromShip(inventory, ItemType.MedkitBase);
        taken.Advanced = TakeFromShip(inventory, ItemType.MedkitAdvanced);
        taken.Antidote = TakeFromShip(inventory, ItemType.Antidote);
        taken.Adrenaline = TakeFromShip(inventory, ItemType.Adrenaline);
        taken.Hazmat = TakeFromShip(inventory, ItemType.HazmatInjection);
        taken.CombatStim = TakeFromShip(inventory, ItemType.CombatStim);
        taken.HealingGrenade = TakeFromShip(inventory, ItemType.HealingGrenade);   // Rev BS-b
        taken.NanomedicDrone = TakeFromShip(inventory, ItemType.NanomedicDrone);   // Rev BU-b
        taken.AntidoteInjector = TakeFromShip(inventory, ItemType.AntidoteInjector);   // Rev BU-c

        int total = taken.Base + taken.Advanced + taken.Antidote + taken.Adrenaline + taken.Hazmat + taken.CombatStim +
                    taken.HealingGrenade + taken.NanomedicDrone + taken.AntidoteInjector;
        if (total == 0) return RestockResult.NoSupplies;

        LogV($"Rifornito dalla stiva: base +{taken.Base}, avanzato +{taken.Advanced}, antidoto +{taken.Antidote}, " +
             $"adrenaline +{taken.Adrenaline}, hazmat +{taken.Hazmat}, stim +{taken.CombatStim}, " +
             $"bomba +{taken.HealingGrenade}, drone +{taken.NanomedicDrone}, iniettore +{taken.AntidoteInjector}.");
        return RestockResult.Restocked;
    }

    /// <summary>
    /// Q48-a / Rev BU-b — il tipo resta fuori dal kit SOLO per il tier della Medbay: spazio nel kit e
    /// scorte in stiva ci sono, ma la capienza di rifornimento è 0. Vale per i tipi con un tier minimo
    /// (Combat Stim, drone, iniettore); per gli altri la capienza di rifornimento non è mai 0. SERVER.
    /// </summary>
    private bool IsBlockedByTier(InventorySystem inventory, ItemType type)
        => GetRestockCap(type) == 0 &&
           GetCount(type) < GetCap(type) &&
           inventory.GetQuantity(type) > 0;

    private byte TakeFromShip(InventorySystem inventory, ItemType type)
    {
        int space = GetRestockCap(type) - GetCount(type);   // Rev BR-b: capienza di rifornimento (tier)
        if (space <= 0) return 0;

        int take = Mathf.Min(space, inventory.GetQuantity(type));
        if (take <= 0) return 0;
        if (!inventory.TryConsume(type, take)) return 0;

        ServerSetCount(type, GetCount(type) + take);
        return (byte)Mathf.Clamp(take, 0, 255);
    }

    [Rpc(SendTo.Owner)]
    private void RestockResultOwnerRpc(RestockResult result, byte takenBase, byte takenAdvanced, byte takenAntidote,
                                       byte takenAdrenaline, byte takenHazmat, byte takenStim, byte takenGrenade,
                                       byte takenDrone, byte takenInjector, ushort tierBlockedMask)
    {
        // Rev BR-b (Q48-a): nota sui pezzi bloccati dal tier, in coda a qualsiasi esito.
        // Rev BU-c: un pezzo per bit della maschera, nell'ordine del kit.
        string stimNote = string.Empty;
        for (int i = 0; i < KitTypes.Length; i++)
        {
            ItemType type = KitTypes[i];
            if ((tierBlockedMask & (1 << (int)type)) == 0) continue;
            int tier = config != null ? config.MinMedbayTierFor(type) : 1;
            stimNote += $" · {KitItemLabel(type)} needs Medbay T{tier}";
        }

        switch (result)
        {
            case RestockResult.Restocked:
                var parts = new List<string>(9);
                if (takenBase > 0) parts.Add($"+{takenBase} medkit");
                if (takenAdvanced > 0) parts.Add($"+{takenAdvanced} advanced medkit");
                if (takenAntidote > 0) parts.Add($"+{takenAntidote} antidote");
                if (takenAdrenaline > 0) parts.Add($"+{takenAdrenaline} adrenaline");
                if (takenHazmat > 0) parts.Add($"+{takenHazmat} Hazmat");
                if (takenStim > 0) parts.Add($"+{takenStim} Combat Stim");
                if (takenGrenade > 0) parts.Add($"+{takenGrenade} Healing Grenade");   // Rev BS-b
                if (takenDrone > 0) parts.Add($"+{takenDrone} Nanomedic Drone");       // Rev BU-b
                if (takenInjector > 0) parts.Add($"+{takenInjector} Antidote Injector"); // Rev BU-c
                SetFeedback("Kit restocked: " + string.Join(", ", parts) + stimNote, HoldSeconds);
                break;
            case RestockResult.KitFull:
                SetFeedback("Medical kit already full" + stimNote, HoldSeconds);
                break;
            case RestockResult.NoSupplies:
                SetFeedback("No medical supplies aboard" + stimNote, HoldSeconds);
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
        netAdrenaline.Value = 0;
        netHazmat.Value = 0;
        netCombatStim.Value = 0;
        netHealingGrenade.Value = 0;
        netNanomedicDrone.Value = 0;
        netAntidoteInjector.Value = 0;
        LogV("Kit svuotato.");
    }

    /// <summary>
    /// Rev BS-b — toglie un pezzo del tipo indicato dal kit. SERVER ONLY. Lo usa PlayerThrower per
    /// la bomba curativa, solo dopo un lancio partito, e PlayerNanomedicDrone per il drone (Rev BU-b).
    /// false se il kit non ne contiene.
    /// </summary>
    public bool ServerTryConsume(ItemType type)
    {
        if (!IsServer) return false;
        int count = GetCount(type);
        if (count <= 0) return false;
        ServerSetCount(type, count - 1);
        LogV($"Consumato {type} fuori dal canale (rimasti {count - 1}).");
        return true;
    }

    private void ServerSetCount(ItemType type, int value)
    {
        if (!IsServer) return;
        byte clamped = (byte)Mathf.Clamp(value, 0, 255);
        switch (type)
        {
            case ItemType.MedkitBase: netMedkitBase.Value = clamped; break;
            case ItemType.MedkitAdvanced: netMedkitAdvanced.Value = clamped; break;
            case ItemType.Antidote: netAntidote.Value = clamped; break;
            case ItemType.Adrenaline: netAdrenaline.Value = clamped; break;
            case ItemType.HazmatInjection: netHazmat.Value = clamped; break;
            case ItemType.CombatStim: netCombatStim.Value = clamped; break;
            case ItemType.HealingGrenade: netHealingGrenade.Value = clamped; break;
            case ItemType.NanomedicDrone: netNanomedicDrone.Value = clamped; break;
            case ItemType.AntidoteInjector: netAntidoteInjector.Value = clamped; break;
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

    /// <summary>Rev BU-c — nome di gioco dei pezzi con un tier minimo, per le note del rifornimento.</summary>
    private static string KitItemLabel(ItemType type)
    {
        switch (type)
        {
            case ItemType.CombatStim: return "Combat Stim";
            case ItemType.NanomedicDrone: return "Nanomedic Drone";
            case ItemType.AntidoteInjector: return "Antidote Injector";
            default: return type.ToString();
        }
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

        // Rev BR-b: accanto al pannello di PlayerStatusEffects (x 570–930 da Rev BR-a), stessa
        // banda per OwnerClientId (passo 250).
        float y = 310 + (OwnerClientId * 250f);

        GUILayout.BeginArea(new Rect(940, y, 320, 130));   // Rev BS-b: riga della bomba (Rev BU-b: + drone, stessa riga)
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[PlayerMedKit] Client {OwnerClientId} — base {GetCount(ItemType.MedkitBase)}/" +
                        $"{GetCap(ItemType.MedkitBase)} · avanz. {GetCount(ItemType.MedkitAdvanced)}/" +
                        $"{GetCap(ItemType.MedkitAdvanced)} · antid. {GetCount(ItemType.Antidote)}/" +
                        $"{GetCap(ItemType.Antidote)} · iniett. {GetCount(ItemType.AntidoteInjector)}/" +
                        $"{GetCap(ItemType.AntidoteInjector)} (rif. {GetRestockCap(ItemType.AntidoteInjector)})");
        GUILayout.Label($"Droghe — adren. {GetCount(ItemType.Adrenaline)}/{GetCap(ItemType.Adrenaline)} · " +
                        $"hazmat {GetCount(ItemType.HazmatInjection)}/{GetCap(ItemType.HazmatInjection)} · " +
                        $"stim {GetCount(ItemType.CombatStim)}/{GetCap(ItemType.CombatStim)} " +
                        $"(rifornibili {GetRestockCap(ItemType.CombatStim)})");
        GUILayout.Label($"Bomba curativa {GetCount(ItemType.HealingGrenade)}/{GetCap(ItemType.HealingGrenade)} · " +
                        $"drone {GetCount(ItemType.NanomedicDrone)}/{GetCap(ItemType.NanomedicDrone)} " +
                        $"(rifornibili {GetRestockCap(ItemType.NanomedicDrone)})");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Dalla stiva"))
        {
            RestockResult result = ServerRestockFromShip(out RestockTaken t);
            RestockResultOwnerRpc(result, t.Base, t.Advanced, t.Antidote, t.Adrenaline, t.Hazmat, t.CombatStim,
                                  t.HealingGrenade, t.NanomedicDrone, t.AntidoteInjector, t.TierBlockedMask);
        }
        if (GUILayout.Button("Pieno (gratis)"))   // debug: ignora il tier della Medbay
        {
            for (int i = 0; i < KitTypes.Length; i++)
                ServerSetCount(KitTypes[i], GetCap(KitTypes[i]));
        }
        if (GUILayout.Button("Svuota")) ServerClearKit();
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}