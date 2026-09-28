using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerHealthSystem — salute per-player, server-authoritative.
///
/// STORIA: nato come stub M2 (solo HP + registro per UI). Rev BD (D27) aggiunge
/// il ciclo Morte &amp; Rianimazione: Downed → Defibrillatore → Respawn, sempre
/// lato server, con applicazione di Ferite Composte (CompoundWounds) alla
/// rianimazione via defibrillatore.
///
/// DIFFERENZA ARCHITETTURALE rispetto ai sistemi-nave (OxygenSystem, PowerManager,
/// HullSystem, ecc.): quelli sono SINGLETON (una istanza in scena, Instance +
/// OnInstanceReady). Questo vive sul Player prefab: UNA ISTANZA PER CLIENT
/// CONNESSO, ciascuna col proprio OwnerClientId, la propria HP e il proprio
/// LifeState. Pattern per-player consolidato (Rev BC): registro statico
/// activeByClientId + LocalInstance + TryGetByClientId.
///
/// STATO REPLICATO (D27): oltre a HP (già replicata da M2), ora anche LifeState
/// è NetworkVariable. È il PRIMO stato per-player che richiede davvero replica
/// client-side (deviazione GIUSTIFICATA dal default "server-only finché non serve
/// una UI" di Rev BC): il proprietario deve reagire (congelare il movimento) e
/// gli altri client devono poter mirare il downed (CanInteract legge lo stato).
///
/// TRIGGER DI MORTE UNICO (Q2-a): la transizione a Downed avviene DENTRO
/// ApplyDamage, dopo il clamp a 0. ApplyDamage è l'unico mutatore server-only di
/// HP verso il basso ed è la via usata ANCHE dal DoT degli Stati (Veleno/
/// Radiazioni): così un singolo choke point cattura sia il combat futuro sia gli
/// Stati. Nessuna via "combat-only".
///
/// FREEZE DEL DOWNED: il movimento è client-side (PlayerController MonoBehaviour +
/// CharacterController, NetworkTransform owner-authoritative). Congelare va fatto
/// SULL'OWNER, guidato dallo stato replicato. Si disabilita PlayerController
/// (il movimento è applicato nel suo Update → si ferma) MA NON il CharacterController
/// (il suo collider deve restare per il raycast del rianimatore).
///
/// RESPAWN (Q7-a): in-place, sullo STESSO NetworkObject (nessun despawn/respawn).
/// Il timer di respawn è diegetico (clonazione Associazione Genesi, GDD §7). Il
/// clone è un CORPO NUOVO → NESSUNA Ferita Composta al respawn (Q8-a); le Ferite
/// Composte si applicano SOLO alla rianimazione via defibrillatore.
///
/// IMMUNITÀ (Rev BP-a · Q27-a): ServerTryRevive riceve i secondi di immunità del
/// profilo del rianimatore (Corpsman 3 s, default 0). Finché dura, ApplyDamage non ha
/// effetto: essendo il choke point unico, l'immunità copre anche il DoT degli Stati.
/// Stato SOLO server (nessun consumatore client oggi). Azzerata a terra e al respawn:
/// il clone non la eredita.
///
/// STATI AL RESPAWN (Rev BP-b): ServerRespawn azzera anche gli stati di alterazione
/// (PlayerStatusEffects.ServerClearAll). Con Veleno e Radiazioni persistenti il clone
/// non deve ereditarli. La rianimazione via defib invece li conserva (stesso corpo).
///
/// KIT AL RESPAWN (Rev BQ · Q30-a): ServerRespawn svuota anche il kit medico personale
/// (PlayerMedKit.ServerClearKit): il clone nasce senza kit. La rianimazione lo conserva.
///
/// CURA (Rev BO-a): ApplyHeal è il choke point server-only VERSO L'ALTO, speculare
/// ad ApplyDamage. Lo usano la Recovery Bay (auto-cura e soglie del trattamento), il
/// medikit in campo (PlayerMedKit, Rev BQ) e, in futuro, la bomba curativa. Cura SOLO un
/// giocatore Alive: un
/// Downed si rialza col defibrillatore, non con la cura. Le due eccezioni restano
/// le transizioni della macchina a stati (ServerTryRevive / ServerRespawn), che
/// impostano HP come parte del cambio di stato.
///
/// MODIFICATORI (Rev BR · Q39-b / Q40-a): due statistiche degli stati StatModifier passano
/// di qui.
/// - HP MAX DINAMICO: MaxHP = HP max base × moltiplicatore MaxHP di PlayerStatusEffects.
///   Il moltiplicatore si ricava dalla maschera replicata, quindi MaxHP vale uguale su server
///   e client senza NetworkVariable nuove. Quando la maschera cambia, il server taglia gli
///   HP correnti al nuovo massimo (ServerClampHPToMax); se il massimo risale gli HP tagliati
///   NON tornano (Combat Stim: il costo è reale). Sull'owner, un cambio di maschera rilancia
///   OnLocalHealthChanged, così la UI vede il nuovo massimo.
/// - DANNO SUBITO: ApplyDamage moltiplica il danno per il moltiplicatore DamageTaken, dopo
///   il controllo di immunità. Il danno da Radiazioni ha in più un moltiplicatore suo,
///   applicato nel tick di PlayerStatusEffects (Hazmat).
/// La base (campo maxHP) resta il valore di (ri)nascita: il clone nasce senza stati.
///
/// ⚠️ VERIFICA EDITOR: il Player prefab deve avere NetworkObject + essere il
/// "Player Prefab" di NetworkManager (setup NGO standard, non in codice). Su di
/// esso convivono PlayerHealthSystem, PlayerStatusEffects (Rev BC) e
/// PlayerReviveTarget (Rev BD). Vedi note di consegna.
/// </summary>
public class PlayerHealthSystem : NetworkBehaviour
{
    /// <summary>Stato vitale replicato (D27). Alive è il default (0).</summary>
    public enum LifeState : byte
    {
        Alive = 0,
        Downed = 1,        // a terra, rianimabile via defibrillatore (30s)
        RespawnWait = 2    // morto, in attesa del clone (timer respawn)
    }

    [Header("Configurazione HP")]
    [Tooltip("HP massimi del giocatore. Il GDD (§7, §9.4) definisce soglie percentuali " +
             "(40% FERITO, 20% CRITICO) ma nessun valore assoluto: 100 è coerente con gli " +
             "stub di MedicalDashboardUI/CrewHPEntry. Da confermare quando esisterà danno reale (Combat).")]
    [SerializeField] private float maxHP = 100f;

    [Header("Morte & Rianimazione (D27)")]
    [Tooltip("Durata dello stato Downed prima del passaggio a RespawnWait, in secondi. " +
             "GDD ruoli: 30s. Entro questa finestra un compagno può defibrillare.")]
    [SerializeField] private float downedDuration = 30f;

    [Tooltip("Ritardo del respawn (clonazione, GDD §7 diegetico) da RespawnWait al ritorno Alive, " +
             "in secondi. Valore da calibrare in design (GDD §7).")]
    [SerializeField] private float respawnDelay = 8f;

    [Tooltip("Layer su cui viene messo il player mentre è DOWNED, così il raycast di interazione " +
             "del rianimatore lo colpisce. DEVE coincidere con la layer inclusa nell'interactionLayer " +
             "di InteractionSystem (nel progetto: 6 = 'Interactable'). Su Alive/RespawnWait la layer " +
             "originale viene ripristinata — un player vivo NON è interagibile.")]
    [SerializeField] private int downedInteractionLayer = 6;

    // ── HP — NetworkVariable (server scrive, tutti leggono) — pattern EconomyManager ──
    private readonly NetworkVariable<float> netCurrentHP = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ── LifeState — NetworkVariable replicata (D27) ──
    private readonly NetworkVariable<LifeState> netLifeState = new NetworkVariable<LifeState>(
        LifeState.Alive, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public float CurrentHP => netCurrentHP.Value;

    /// <summary>HP massimi di base (senza modificatori): valore di (ri)nascita. Rev BR.</summary>
    public float BaseMaxHP => maxHP;

    /// <summary>
    /// HP massimi effettivi (Rev BR): base × moltiplicatore MaxHP degli stati attivi
    /// (Combat Stim −30%). Uguale su server e client: deriva dalla maschera replicata.
    /// Mai sotto 1 HP.
    /// </summary>
    public float MaxHP => Mathf.Max(1f, maxHP * StatMultiplier(StatKind.MaxHP));

    public float HealthPercent
    {
        get
        {
            float max = MaxHP;
            return max > 0f ? netCurrentHP.Value / max : 0f;
        }
    }

    /// <summary>Stato vitale corrente (replicato).</summary>
    public LifeState State => netLifeState.Value;

    /// <summary>Vivo e in piedi (non downed, non in attesa di respawn).</summary>
    public bool IsAlive => netLifeState.Value == LifeState.Alive;

    /// <summary>A terra, rianimabile via defibrillatore.</summary>
    public bool IsDowned => netLifeState.Value == LifeState.Downed;

    // ── Registro statico per-clientId — equivalente "per-player" di Instance ──
    private static readonly Dictionary<ulong, PlayerHealthSystem> activeByClientId = new();

    /// <summary>Istanza del client locale (IsOwner) — scorciatoia per ProfileTabUI.</summary>
    public static PlayerHealthSystem LocalInstance { get; private set; }

    /// <summary>Fired SOLO per l'istanza locale quando il proprio HP cambia: (current, max).</summary>
    public static event Action<float, float> OnLocalHealthChanged;

    /// <summary>Fired SOLO per l'istanza locale quando il proprio LifeState cambia.</summary>
    public static event Action<LifeState> OnLocalLifeStateChanged;

    // ── Riferimenti sibling ──
    private PlayerStatusEffects statusEffects;   // tutti (Awake): modificatori; server: CompoundWounds e pulizia al respawn
    private PlayerMedKit medKit;                  // server: kit medico svuotato al respawn (Rev BQ)
    private PlayerController playerController;    // owner: per congelare il movimento

    // ── Timer server ──
    private float downedTimer;
    private float respawnTimer;

    // ── Immunità al danno (Rev BP-a) — SOLO server, istante di fine in Time.time ──
    private float immuneUntil;

    // ── Layer originale (per il ripristino dopo il downed) ──
    private int originalLayer;

    private void Awake()
    {
        originalLayer = gameObject.layer;

        // Rev BR: serve su TUTTI i client (MaxHP dinamico), non solo sul server.
        statusEffects = GetComponent<PlayerStatusEffects>();
    }

    // ── Lifecycle NGO ──────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // (Re)connessione: il clone parte a HP piena e Alive. Il RESPAWN in-place
            // (Q7-a) NON passa da qui — avviene su ServerRespawn sullo stesso
            // NetworkObject, senza despawn — quindi OnNetworkSpawn gira solo alla
            // (ri)connessione. La persistenza per-player attraverso una riconnessione
            // è fuori scope D27 (nessuno store persistente esiste).
            netCurrentHP.Value = maxHP;
            netLifeState.Value = LifeState.Alive;
            immuneUntil = 0f;

            if (statusEffects == null)
                Debug.LogError("[PlayerHealthSystem] PlayerStatusEffects mancante sullo stesso GameObject. " +
                               "Le Ferite Composte non potranno essere applicate alla rianimazione (D27). " +
                               "Aggiungere PlayerStatusEffects sul root del Player prefab.");

            medKit = GetComponent<PlayerMedKit>();
            if (medKit == null)
                Debug.LogError("[PlayerHealthSystem] PlayerMedKit mancante sullo stesso GameObject. " +
                               "Il clone non potrà ripartire con il kit vuoto (Rev BQ). " +
                               "Aggiungere PlayerMedKit sul root del Player prefab.");
        }

        activeByClientId[OwnerClientId] = this;
        netCurrentHP.OnValueChanged += HandleHPChanged;
        netLifeState.OnValueChanged += HandleLifeStateChanged;

        if (IsOwner)
        {
            LocalInstance = this;
            playerController = GetComponent<PlayerController>();

            // Rev BR: un cambio di stati può cambiare l'HP max → la UI locale deve saperlo.
            if (statusEffects != null)
                statusEffects.OnActiveMaskChanged += HandleStatusMaskChanged;

            OnLocalHealthChanged?.Invoke(netCurrentHP.Value, MaxHP);
            OnLocalLifeStateChanged?.Invoke(netLifeState.Value);
            ApplyOwnerFreeze(netLifeState.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        netCurrentHP.OnValueChanged -= HandleHPChanged;
        netLifeState.OnValueChanged -= HandleLifeStateChanged;

        if (statusEffects != null)
            statusEffects.OnActiveMaskChanged -= HandleStatusMaskChanged;

        if (activeByClientId.TryGetValue(OwnerClientId, out var registered) && registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;
    }

    private void HandleHPChanged(float previous, float current)
    {
        if (IsOwner)
            OnLocalHealthChanged?.Invoke(current, MaxHP);
    }

    /// <summary>Rev BR — sull'owner: gli stati sono cambiati, forse anche l'HP max.</summary>
    private void HandleStatusMaskChanged()
    {
        if (IsOwner)
            OnLocalHealthChanged?.Invoke(netCurrentHP.Value, MaxHP);
    }

    /// <summary>Rev BR — moltiplicatore di una statistica dagli stati attivi (1 se mancano).</summary>
    private float StatMultiplier(StatKind stat)
        => statusEffects != null ? statusEffects.GetStatMultiplier(stat) : 1f;

    private void HandleLifeStateChanged(LifeState previous, LifeState current)
    {
        // Layer swap su TUTTI i client: il downed va sulla layer di interazione così il
        // raycast di QUALSIASI rianimatore lo colpisce; ripristino su Alive/RespawnWait.
        ApplyInteractableLayer(current);

        if (IsOwner)
        {
            OnLocalLifeStateChanged?.Invoke(current);
            ApplyOwnerFreeze(current);
        }
    }

    /// <summary>
    /// Mette il GameObject sulla layer di interazione mentre è Downed (così il
    /// raycast del rianimatore lo colpisce), ripristina la layer originale altrimenti.
    /// Gira su ogni client (il rianimatore è un non-owner). Vedi InteractionSystem.interactionLayer.
    /// </summary>
    private void ApplyInteractableLayer(LifeState state)
    {
        gameObject.layer = (state == LifeState.Downed) ? downedInteractionLayer : originalLayer;
    }

    /// <summary>
    /// Congela/scongela il movimento del PROPRIO player (solo owner). Disabilita
    /// PlayerController (il movimento è applicato nel suo Update → si ferma), ma NON
    /// il CharacterController: il suo collider deve restare per il raycast del
    /// rianimatore. Coerente col toggle enabled già usato da PlayerNetworkOwnership.
    /// </summary>
    private void ApplyOwnerFreeze(LifeState state)
    {
        if (!IsOwner || playerController == null) return;
        playerController.enabled = (state == LifeState.Alive);
    }

    // ── Tick server: timer downed / respawn ────────────────────────────────

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;

        switch (netLifeState.Value)
        {
            case LifeState.Downed:
                downedTimer -= Time.deltaTime;
                if (downedTimer <= 0f)
                    ServerEnterRespawnWait();
                break;

            case LifeState.RespawnWait:
                respawnTimer -= Time.deltaTime;
                if (respawnTimer <= 0f)
                    ServerRespawn();
                break;
        }
    }

    // ── Lookup per UI ───────────────────────────────────────────────────────

    /// <summary>
    /// Cerca l'istanza HP del client indicato. Usato da MedicalDashboardUI per
    /// ogni riga equipaggio — stesso clientId di NetworkManager.ConnectedClientsIds.
    /// </summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerHealthSystem instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    // ── API pubblica — danno ──────────────────────────────────────────────

    /// <summary>
    /// Applica danno a questo giocatore. SERVER ONLY. Unico choke point verso il
    /// basso: combat futuro e DoT degli Stati passano di qui. A HP 0, se il player
    /// è ancora Alive, entra in Downed (Q2-a). Durante l'immunità post-rianimazione
    /// (Rev BP-a) il danno è ignorato. Rev BR: il danno è moltiplicato per il moltiplicatore
    /// DamageTaken degli stati attivi (ordine: vivo → immunità → modificatore → sottrazione).
    /// </summary>
    public void ApplyDamage(float amount)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerHealthSystem] ApplyDamage chiamato lato client — server only. " +
                              "Una fonte di danno reale deve inviare una Rpc al server che poi chiama questo metodo.");
            return;
        }

        if (amount <= 0f) return;
        if (netLifeState.Value != LifeState.Alive) return;   // già a terra/in respawn: nessun ulteriore danno
        if (IsImmuneServer) return;                          // Rev BP-a: immunità post-rianimazione

        amount *= StatMultiplier(StatKind.DamageTaken);      // Rev BR: resistenze / vulnerabilità
        if (amount <= 0f) return;

        netCurrentHP.Value = Mathf.Max(0f, netCurrentHP.Value - amount);

        if (netCurrentHP.Value <= 0f)
            ServerEnterDowned();
    }

    // ── API pubblica — cura (Rev BO-a) ────────────────────────────────────

    /// <summary>
    /// Ripristina HP a questo giocatore. SERVER ONLY. Choke point unico verso l'alto
    /// (speculare ad ApplyDamage): cura solo se Alive, con clamp a MaxHP (effettivo, Rev BR). Ritorna gli
    /// HP effettivamente ripristinati (0 se nulla è cambiato), così il chiamante può
    /// sapere se la cura ha avuto effetto senza rileggere lo stato.
    /// </summary>
    public float ApplyHeal(float amount)
    {
        if (!IsServer)
        {
            Debug.LogWarning("[PlayerHealthSystem] ApplyHeal chiamato lato client — server only. " +
                             "Una sorgente di cura reale deve inviare una Rpc al server che poi chiama questo metodo.");
            return 0f;
        }

        if (amount <= 0f) return 0f;
        if (netLifeState.Value != LifeState.Alive) return 0f;   // Downed/RespawnWait: la cura non rialza

        float before = netCurrentHP.Value;
        float after = Mathf.Min(MaxHP, before + amount);
        if (after <= before) return 0f;

        netCurrentHP.Value = after;
        return after - before;
    }

    /// <summary>
    /// Rev BR — taglia gli HP correnti all'HP max effettivo. SERVER ONLY. Chiamato da
    /// PlayerStatusEffects quando la maschera cambia (Combat Stim: massimo −30%). Non alza mai
    /// gli HP: se il massimo risale, gli HP tagliati non tornano (Q40-a). Nessun cambio di
    /// LifeState: il massimo effettivo è sempre ≥ 1, quindi il taglio non porta a terra.
    /// </summary>
    public void ServerClampHPToMax()
    {
        if (!IsServer) return;

        float max = MaxHP;
        if (netCurrentHP.Value > max)
            netCurrentHP.Value = max;
    }

    // ── Macchina a stati (SERVER) ─────────────────────────────────────────

    private void ServerEnterDowned()
    {
        if (!IsServer) return;
        if (netLifeState.Value != LifeState.Alive) return;

        netLifeState.Value = LifeState.Downed;
        downedTimer = Mathf.Max(0f, downedDuration);
        immuneUntil = 0f;
    }

    private void ServerEnterRespawnWait()
    {
        if (!IsServer) return;
        if (netLifeState.Value != LifeState.Downed) return;

        netLifeState.Value = LifeState.RespawnWait;
        respawnTimer = Mathf.Max(0f, respawnDelay);
    }

    private void ServerRespawn()
    {
        if (!IsServer) return;

        // Clone = corpo nuovo (Q7-a in-place, Q8-a nessuna Ferita Composta, nessuna immunità).
        // Rev BR: HP max di BASE — il clone nasce senza stati (ServerClearAll qui sotto).
        netCurrentHP.Value = maxHP;
        netLifeState.Value = LifeState.Alive;
        immuneUntil = 0f;

        // Rev BP-b: nessuno stato di alterazione sul clone (Veleno/Radiazioni sono persistenti).
        if (statusEffects != null)
            statusEffects.ServerClearAll();

        // Rev BQ: il clone nasce senza kit medico personale (Q30-a).
        if (medKit != null)
            medKit.ServerClearKit();
    }

    /// <summary>
    /// Rev BP-a — true se il giocatore è nella finestra di immunità post-rianimazione.
    /// Significativo SOLO sul server (sui client è sempre false: stato non replicato).
    /// </summary>
    public bool IsImmuneServer => IsServer && Time.time < immuneUntil;

    /// <summary>
    /// Tenta la rianimazione via defibrillatore. SERVER ONLY. Chiamato dal
    /// ReviveServerRpc di PlayerReviveTarget. First-completer-wins (Q4-a): riesce
    /// solo se il player è ancora Downed. Su successo: HP = MaxHP (effettivo) × frazione,
    /// stato → Alive, immunità al danno per immunitySeconds (Rev BP-a, profilo del
    /// rianimatore) e Ferite Composte (Q8-a). Ritorna true su successo.
    /// </summary>
    public bool ServerTryRevive(float hpRestoreFraction, float immunitySeconds = 0f)
    {
        if (!IsServer) return false;
        if (netLifeState.Value != LifeState.Downed) return false;   // già rianimato / non a terra

        // Immunità PRIMA del ritorno ad Alive: nessun danno può infilarsi tra i due passi.
        immuneUntil = immunitySeconds > 0f ? Time.time + immunitySeconds : 0f;

        float frac = Mathf.Clamp01(hpRestoreFraction);
        netCurrentHP.Value = Mathf.Max(1f, MaxHP * frac);   // almeno 1 HP: rianimato = non subito ri-downed (Rev BR: max effettivo)
        netLifeState.Value = LifeState.Alive;

        // Ferite Composte SOLO alla rianimazione via defib (Q8-a). Stato pronto da Rev BC.
        if (statusEffects != null)
            statusEffects.ApplyEffect(StatusEffectType.CompoundWounds);

        return true;
    }

    // ── Debug GUI (Editor/Development, solo server) — standard Rev BA ──
    [Header("Debug")]
    [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development, solo server). Standard Rev BA — default off.")]
    [SerializeField] private bool showDebugUI = false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!showDebugUI) return;
        if (!IsServer) return;

        // Offset verticale per OwnerClientId, per non sovrapporre i pannelli in ParrelSync.
        float y = 310 + (OwnerClientId * 120f);

        GUILayout.BeginArea(new Rect(280, y, 300, 130));
        GUILayout.BeginVertical("box");
        string immunity = IsImmuneServer ? $" · immune {immuneUntil - Time.time:F1}s" : "";
        GUILayout.Label($"[Health] Client {OwnerClientId}: {netCurrentHP.Value:F1}/{MaxHP:F0} — {netLifeState.Value}{immunity}");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-10 danno")) ApplyDamage(10f);
        if (GUILayout.Button("+10 cura")) ApplyHeal(10f);   // Rev BO-a: passa dal choke point
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Downed")) ApplyDamage(MaxHP * 10f);   // Rev BR: ×10 → a terra anche con resistenze
        if (GUILayout.Button("Revive 30%")) ServerTryRevive(0.30f);
        if (GUILayout.Button("Respawn")) ServerRespawn();
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}