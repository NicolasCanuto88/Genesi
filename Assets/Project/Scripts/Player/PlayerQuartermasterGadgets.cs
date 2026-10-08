using System.Collections.Generic;
using SpaceSurvivor.Ship;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PlayerQuartermasterGadgets — gadget del Quartermaster (Rev BV-b · BV-c, workshop Quartermaster,
/// Q95-a, Q96-a, Q97-a, Q101-a, Q102-a, Q104-a, Q105-a): Scudo Personale e Bubble Shield.
///
/// COMANDI (Q97-a): un solo tasto, l'azione "Shield" (F / Y) con interazione Press And Release.
///   - TOCCO (rilascio prima della soglia dello SO, 0,3 s): Scudo Personale, al rilascio.
///   - PRESSIONE TENUTA: oltre la soglia si apre la mira della Bubble Shield (arco e marcatore,
///     ThrowAimPreview, come la bomba curativa); al rilascio la bolla parte.
///   - Se oltre la soglia la bolla non è disponibile (T1, ricarica, sistema assente) la riga lo dice
///     e al rilascio non parte nulla: niente scudo personale per errore.
///   - Se un gate cade durante la pressione (postazione, tablet, a terra, interazione, canale del
///     kit, mira della granata, pressione del drone) la pressione si chiude senza effetto.
///
/// SCUDO PERSONALE (Q95-a · Q102-a): il Quartermaster si dà lo stato Shielded (Buff a tempo, senza
/// modificatori) per la durata del tier, poi ricarica (Q101-a: T2 = 7 s, ricarica 40 s
/// dall'attivazione).
///
/// BUBBLE SHIELD (Q96-a · Q105-a): lanciata con il framework di lancio (ThrowableSystem, lanciabile
/// dello SO). Alla detonazione resta una bolla per la durata del tier (T2 = 8 s, ricarica 60 s dal
/// lancio): chi sta dentro, vivo o a terra, riceve lo stato Shielded a ogni tick e lo perde poco
/// dopo essere uscito. Nessun pezzo da consumare: solo la ricarica. A fine bolla il riepilogo
/// ("Bubble Shield: covered N crew").
///
/// COSA PARANO (Q95/Q99): solo i proiettili. Prima del Combat non cambiano il danno, si vedono
/// (film, PlayerShieldFilm) e hanno tempi veri.
///
/// SOLO QUARTERMASTER (Q95-a, Q7-a): i gadget sono l'equipaggiamento del ruolo. Un altro ruolo che
/// preme F riceve "Shields: Quartermaster gear" e non succede nulla.
///
/// PATTERN per-player (come PlayerMedKit / PlayerNanomedicDrone): vive sul root del Player prefab,
/// una istanza per client connesso; registro statico per OwnerClientId + LocalInstance +
/// TryGetByClientId.
///
/// RETE:
///   - gli scudi sono lo stato Shielded di PlayerStatusEffects (maschera replicata: film visibile a
///     tutti, anche a chi entra in partita; rinnovo e pulizia al clone già pronti);
///   - le ricariche vivono sul server (autorità, con una tolleranza per il jitter delle RPC) e
///     sull'owner (testi): stesso schema di PlayerThrower;
///   - la bolla viaggia come ogni lanciabile (RPC di ThrowableSystem); nessuna NetworkVariable nuova.
///
/// INPUT: SendMessages di PlayerInput (OnShield), messaggio sia alla pressione sia al rilascio; come
/// rete di sicurezza, durante la pressione l'azione viene anche interrogata (InputAction.IsPressed),
/// schema di PlayerThrower e del drone. F e Y sono anche RepairKey_2 e RepairKey_1, usati solo nei
/// minigame, che disattivano il PlayerController (gate qui sotto). Il Pilota ha un'azione sua
/// (PilotShieldToggle, mappa Pilot).
///
/// AUTORITÀ: le RPC accettano solo il proprietario. Il server rilegge vivo, ruolo, tier e ricarica.
/// </summary>
[RequireComponent(typeof(PlayerHealthSystem))]
public class PlayerQuartermasterGadgets : NetworkBehaviour
{
    [Header("Config (ScriptableObject)")]
    [Tooltip("Assegna l'asset QuartermasterGadgetConfig. Senza config i gadget non si usano (errore a log allo " +
             "spawn sul server).")]
    [SerializeField] private QuartermasterGadgetConfig config;

    [Header("Debug")]
    [Tooltip("Overlay OnGUI con tier e ricariche (solo Editor/Development Build, solo server). Standard Rev BA — " +
             "default off.")]
    [SerializeField] private bool showDebugUI = false;

    [Tooltip("Log verboso di pressioni, attivazioni, lanci e rifiuti. Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    private const string ShieldActionName = "Shield";

    /// <summary>
    /// Tolleranza delle ricariche sul server: le RPC arrivano con un jitter, quindi il server accetta
    /// un'attivazione fino a questi secondi prima della fine della ricarica vista dal client
    /// (stesso valore di PlayerThrower).
    /// </summary>
    private const float ServerCooldownTolerance = 0.15f;

    // ── Registro statico per-clientId ──
    private static readonly Dictionary<ulong, PlayerQuartermasterGadgets> activeByClientId =
        new Dictionary<ulong, PlayerQuartermasterGadgets>();

    /// <summary>Istanza del client locale (IsOwner).</summary>
    public static PlayerQuartermasterGadgets LocalInstance { get; private set; }

    /// <summary>Trova i gadget del client indicato.</summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerQuartermasterGadgets instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    // ── Esiti (server → owner) ──
    private enum GadgetResult : byte
    {
        Activated = 0,
        Cooldown = 1,
        NotQuartermaster = 2,
        Unavailable = 3,
        Failed = 4
    }

    // ── Riferimenti sibling ──
    private PlayerHealthSystem health;          // server e owner
    private PlayerStatusEffects statusEffects;  // server: stato Shielded
    private PlayerMedKit medKit;                // owner: riga di esito, gate del canale
    private PlayerController controller;        // owner: gate
    private InteractionSystem interaction;      // owner: gate
    private TabletStation tablet;               // owner: gate
    private PlayerThrower thrower;              // owner: gate (mira della granata)
    private PlayerNanomedicDrone drone;         // owner: gate (pressione del drone)
    private Transform cameraTransform;          // owner: origine e direzione del lancio della bolla
    private PlayerInput playerInput;            // owner: rete di sicurezza sul rilascio
    private InputAction shieldAction;

    // ── Stato owner ──
    private float nextLocalShieldTime;
    private float localShieldEndTime;
    private float nextLocalBubbleTime;
    private bool pressing;                      // F / Y tenuto, pressione accettata
    private float pressStartTime;
    private bool aimingBubble;                  // soglia superata e bolla disponibile: arco visibile
    private bool holdBlocked;                   // soglia superata ma bolla non disponibile: il rilascio non fa nulla
    private ThrowAimPreview aimPreview;

    // ── Stato server ──
    private float nextServerShieldTime;
    private float nextServerBubbleTime;

    /// <summary>true se questo giocatore è Quartermaster (ruolo replicato).</summary>
    public bool IsQuartermaster => PlayerCrewRole.HasRole(OwnerClientId, CrewRole.Quartermaster);

    /// <summary>Tier dei gadget (oggi dallo SO, Q101-a). T1 senza config.</summary>
    public int Tier => config != null ? config.CurrentTier : QuartermasterGadgetConfig.MinTier;

    /// <summary>Secondi alla fine della ricarica dello scudo personale (solo owner, 0 = pronto).</summary>
    public float ShieldCooldownRemaining => Mathf.Max(0f, nextLocalShieldTime - Time.time);

    /// <summary>Rev BV-c — secondi alla fine della ricarica della bolla (solo owner, 0 = pronta).</summary>
    public float BubbleCooldownRemaining => Mathf.Max(0f, nextLocalBubbleTime - Time.time);

    /// <summary>Secondi rimasti dello scudo personale attivato da sé (solo owner, 0 = nessuno).</summary>
    public float OwnShieldRemaining => Mathf.Max(0f, localShieldEndTime - Time.time);

    /// <summary>Rev BV-c — true mentre il proprietario mira la Bubble Shield (solo owner).</summary>
    public bool IsAimingBubble => aimingBubble;

    /// <summary>Durata e ricarica dello scudo personale al tier corrente (false se non disponibile).</summary>
    public bool TryGetPersonalShieldStats(out float durationSeconds, out float cooldownSeconds)
    {
        durationSeconds = 0f;
        cooldownSeconds = 0f;
        return config != null && config.TryGetPersonalShield(Tier, out durationSeconds, out cooldownSeconds);
    }

    /// <summary>Rev BV-c — durata e ricarica della bolla al tier corrente (false se non disponibile).</summary>
    public bool TryGetBubbleShieldStats(out float durationSeconds, out float cooldownSeconds)
    {
        durationSeconds = 0f;
        cooldownSeconds = 0f;
        return config != null && config.BubbleShieldThrowable != null &&
               config.TryGetBubbleShield(Tier, out durationSeconds, out cooldownSeconds);
    }

    // ── Lifecycle NGO ──────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        health = GetComponent<PlayerHealthSystem>();
        statusEffects = GetComponent<PlayerStatusEffects>();
        medKit = GetComponent<PlayerMedKit>();
        activeByClientId[OwnerClientId] = this;

        if (IsServer)
        {
            nextServerShieldTime = 0f;
            nextServerBubbleTime = 0f;
            ThrowableSystem.OnServerAreaFinished += HandleServerAreaFinished;

            if (config == null)
                Debug.LogError("[PlayerQuartermasterGadgets] QuartermasterGadgetConfig non assegnato sul Player " +
                               "prefab: i gadget del Quartermaster non si usano. Assegnare l'asset " +
                               "QuartermasterGadgetConfig.");
            else if (config.BubbleShieldThrowable == null)
                Debug.LogWarning("[PlayerQuartermasterGadgets] QuartermasterGadgetConfig senza Bubble Shield " +
                                 "Throwable: la bolla non si lancia. Assegnare TD_BubbleShield (guida di Rev BV-c).");
            if (statusEffects == null)
                Debug.LogError("[PlayerQuartermasterGadgets] PlayerStatusEffects mancante sul Player: lo scudo " +
                               "non può essere applicato.");
        }

        if (IsOwner)
        {
            LocalInstance = this;
            controller = GetComponent<PlayerController>();
            interaction = GetComponent<InteractionSystem>();
            tablet = GetComponent<TabletStation>();
            thrower = GetComponent<PlayerThrower>();
            drone = GetComponent<PlayerNanomedicDrone>();
            playerInput = GetComponent<PlayerInput>();
            Camera cam = GetComponentInChildren<Camera>();
            cameraTransform = cam != null ? cam.transform : null;
            aimPreview = new ThrowAimPreview(transform, "BubbleAimArc");
            nextLocalShieldTime = 0f;
            localShieldEndTime = 0f;
            nextLocalBubbleTime = 0f;
            ResetPress();
        }
    }

    public override void OnNetworkDespawn()
    {
        ThrowableSystem.OnServerAreaFinished -= HandleServerAreaFinished;

        if (activeByClientId.TryGetValue(OwnerClientId, out PlayerQuartermasterGadgets registered) &&
            registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;

        ResetPress();
        if (aimPreview != null) aimPreview.Dispose();
        aimPreview = null;
    }

    // ── Input (SendMessages di PlayerInput — Press And Release, Rev BV-c) ──────

    /// <summary>
    /// Azione "Shield" (F / Y), Press And Release: premuto → inizio della pressione, rilasciato →
    /// tocco (scudo personale) o lancio della bolla mirata.
    /// </summary>
    public void OnShield(InputValue value)
    {
        if (value.isPressed)
            BeginPress();
        else
            ReleasePress();
    }

    private void BeginPress()
    {
        if (!IsOwner || !IsSpawned || pressing) return;
        if (!CanUseLocally()) return;   // postazione, tablet, letto, a terra, canale, mira, drone: ignorato in silenzio

        if (!IsQuartermaster)
        {
            Feedback("Shields: Quartermaster gear");
            return;
        }

        if (shieldAction == null && playerInput != null && playerInput.actions != null)
            shieldAction = playerInput.actions.FindAction(ShieldActionName, throwIfNotFound: false);

        pressing = true;
        aimingBubble = false;
        holdBlocked = false;
        pressStartTime = Time.time;
        LogV("Pressione avviata.");
    }

    /// <summary>
    /// Rilascio (dal messaggio o dalla rete di sicurezza, idempotente). Sotto la soglia: scudo
    /// personale. Con la mira aperta: lancio della bolla. Oltre la soglia senza bolla: nulla.
    /// </summary>
    private void ReleasePress()
    {
        if (!pressing) return;

        CheckThreshold();   // la soglia può essere stata superata nello stesso frame del rilascio

        bool wasAiming = aimingBubble;
        bool blocked = holdBlocked;
        ResetPress();

        if (!CanUseLocally()) return;

        if (wasAiming)
        {
            TryLaunchBubble();
            return;
        }
        if (blocked) return;

        TryActivatePersonalShield();
    }

    /// <summary>Chiude la pressione senza effetto (gate caduto).</summary>
    private void CancelPress()
    {
        if (!pressing) return;
        ResetPress();
        LogV("Pressione annullata senza effetto (gate).");
    }

    private void ResetPress()
    {
        pressing = false;
        aimingBubble = false;
        holdBlocked = false;
        if (aimPreview != null) aimPreview.Hide();
    }

    /// <summary>
    /// Oltre la soglia apre la mira della bolla, se disponibile; altrimenti spiega perché e blocca il
    /// rilascio. Una sola volta per pressione.
    /// </summary>
    private void CheckThreshold()
    {
        if (!pressing || aimingBubble || holdBlocked || config == null) return;
        if (Time.time - pressStartTime < config.TapThresholdSeconds) return;

        if (!CanAimBubbleLocally(out string reason))
        {
            holdBlocked = true;
            Feedback(reason);
            LogV($"Mira della bolla non disponibile: {reason}");
            return;
        }

        aimingBubble = true;
        if (aimPreview != null) aimPreview.Show(config.BubbleShieldThrowable, cameraTransform);
        LogV("Mira della bolla aperta.");
    }

    private bool CanAimBubbleLocally(out string reason)
    {
        reason = string.Empty;
        if (!TryGetBubbleShieldStats(out float _, out float _))
        {
            reason = "Bubble Shield unavailable";
            return false;
        }
        if (cameraTransform == null || ThrowableSystem.Instance == null)
        {
            reason = "Throwing unavailable";
            return false;
        }
        if (Time.time < nextLocalBubbleTime)
        {
            reason = $"Bubble Shield recharging — {Mathf.CeilToInt(BubbleCooldownRemaining)} s";
            return false;
        }
        return true;
    }

    // ── Update (owner) ─────────────────────────────────────────────────────────

    private void Update()
    {
        if (!IsOwner || !IsSpawned || !pressing) return;

        if (!CanUseLocally() || config == null)
        {
            CancelPress();
            return;
        }

        if (shieldAction != null && !shieldAction.IsPressed())
        {
            ReleasePress();   // rete di sicurezza: rilascio senza messaggio
            return;
        }

        CheckThreshold();

        if (aimingBubble && aimPreview != null)
            aimPreview.Update(cameraTransform);
    }

    /// <summary>
    /// Si può usare un gadget: vivo, movimento libero (non a postazione, tablet, letto, pannello o a
    /// terra), tablet chiuso, nessuna interazione continua, nessun canale del kit, nessuna mira della
    /// granata e nessuna pressione del drone. Stesse regole del kit medico, di PlayerThrower e del drone.
    /// </summary>
    private bool CanUseLocally()
    {
        if (health == null || !health.IsAlive) return false;
        if (controller == null || !controller.enabled) return false;
        if (tablet != null && tablet.IsBusy) return false;
        if (interaction != null && interaction.IsInteracting) return false;
        if (medKit != null && medKit.IsChanneling) return false;
        if (thrower != null && thrower.IsAiming) return false;
        if (drone != null && drone.IsDeployHeld) return false;
        return true;
    }

    // ── Scudo personale ────────────────────────────────────────────────────────

    private void TryActivatePersonalShield()
    {
        if (!TryGetPersonalShieldStats(out float _, out float cooldown))
        {
            Feedback("Personal Shield unavailable");
            return;
        }
        if (Time.time < nextLocalShieldTime)
        {
            Feedback($"Personal Shield recharging — {Mathf.CeilToInt(ShieldCooldownRemaining)} s");
            return;
        }

        // Ricarica locale subito (niente doppie richieste); il server la conferma o la corregge.
        nextLocalShieldTime = Time.time + cooldown;
        ActivateShieldServerRpc();
        LogV("Scudo personale richiesto.");
    }

    [Rpc(SendTo.Server)]
    private void ActivateShieldServerRpc(RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != OwnerClientId)
        {
            Debug.LogWarning($"[PlayerQuartermasterGadgets] Scudo rifiutato: il client {sender} ha chiesto lo " +
                             $"scudo del client {OwnerClientId}.");
            return;
        }

        GadgetResult result = ServerTryActivateShield(out float duration, out float cooldownLeft);
        ShieldResultOwnerRpc(result, duration, cooldownLeft);
    }

    /// <summary>
    /// Validazione e attivazione. SERVER ONLY. In caso di successo cooldownLeft è la ricarica intera;
    /// se la ricarica non è finita, è il tempo che manca.
    /// </summary>
    private GadgetResult ServerTryActivateShield(out float duration, out float cooldownLeft)
    {
        duration = 0f;
        cooldownLeft = 0f;

        if (config == null || statusEffects == null) return GadgetResult.Failed;
        if (health == null || !health.IsAlive) return GadgetResult.Failed;
        if (!IsQuartermaster) return GadgetResult.NotQuartermaster;
        if (!config.TryGetPersonalShield(config.CurrentTier, out duration, out float cooldown))
            return GadgetResult.Unavailable;

        if (Time.time < nextServerShieldTime)
        {
            cooldownLeft = nextServerShieldTime - Time.time;
            return GadgetResult.Cooldown;
        }

        statusEffects.ApplyEffectForSeconds(StatusEffectType.Shielded, duration);
        nextServerShieldTime = Time.time + Mathf.Max(0f, cooldown - ServerCooldownTolerance);
        cooldownLeft = cooldown;

        LogV($"Scudo personale attivo: {duration:F0} s, ricarica {cooldown:F0} s (T{config.CurrentTier}).");
        return GadgetResult.Activated;
    }

    [Rpc(SendTo.Owner)]
    private void ShieldResultOwnerRpc(GadgetResult result, float duration, float cooldownLeft)
    {
        switch (result)
        {
            case GadgetResult.Activated:
                nextLocalShieldTime = Time.time + cooldownLeft;
                localShieldEndTime = Time.time + duration;
                Feedback($"Personal Shield — {Mathf.RoundToInt(duration)} s");
                break;
            case GadgetResult.Cooldown:
                nextLocalShieldTime = Time.time + cooldownLeft;
                Feedback($"Personal Shield recharging — {Mathf.CeilToInt(cooldownLeft)} s");
                break;
            case GadgetResult.NotQuartermaster:
                nextLocalShieldTime = 0f;
                Feedback("Shields: Quartermaster gear");
                break;
            case GadgetResult.Unavailable:
                nextLocalShieldTime = 0f;
                Feedback("Personal Shield unavailable");
                break;
            default:
                nextLocalShieldTime = 0f;
                Feedback("Shield failed");
                break;
        }
    }

    // ── Bubble Shield (Rev BV-c) ───────────────────────────────────────────────

    private void TryLaunchBubble()
    {
        if (!TryGetBubbleShieldStats(out float _, out float cooldown) || cameraTransform == null)
        {
            Feedback("Bubble Shield unavailable");
            return;
        }

        // Ricarica locale subito (niente doppi lanci); il server la conferma o la corregge.
        nextLocalBubbleTime = Time.time + cooldown;
        LaunchBubbleServerRpc(cameraTransform.position, cameraTransform.forward);
        LogV("Lancio della bolla richiesto.");
    }

    [Rpc(SendTo.Server)]
    private void LaunchBubbleServerRpc(Vector3 origin, Vector3 lookDirection, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != OwnerClientId)
        {
            Debug.LogWarning($"[PlayerQuartermasterGadgets] Bolla rifiutata: il client {sender} ha chiesto la " +
                             $"bolla del client {OwnerClientId}.");
            return;
        }

        GadgetResult result = ServerTryLaunchBubble(origin, lookDirection, out float duration, out float cooldownLeft);
        BubbleResultOwnerRpc(result, duration, cooldownLeft);
    }

    /// <summary>
    /// Validazione e lancio. SERVER ONLY. Nessun pezzo da consumare: la bolla ha solo la ricarica.
    /// La durata dell'area è quella del tier.
    /// </summary>
    private GadgetResult ServerTryLaunchBubble(Vector3 origin, Vector3 lookDirection, out float duration,
                                               out float cooldownLeft)
    {
        duration = 0f;
        cooldownLeft = 0f;

        if (config == null || config.BubbleShieldThrowable == null) return GadgetResult.Unavailable;
        if (health == null || !health.IsAlive) return GadgetResult.Failed;
        if (!IsQuartermaster) return GadgetResult.NotQuartermaster;
        if (!config.TryGetBubbleShield(config.CurrentTier, out duration, out float cooldown))
            return GadgetResult.Unavailable;

        if (Time.time < nextServerBubbleTime)
        {
            cooldownLeft = nextServerBubbleTime - Time.time;
            return GadgetResult.Cooldown;
        }

        ThrowableSystem system = ThrowableSystem.Instance;
        if (system == null)
        {
            Debug.LogError("[PlayerQuartermasterGadgets] ThrowableSystem assente in scena: impossibile lanciare " +
                           "la bolla.");
            return GadgetResult.Failed;
        }
        if (!system.ServerLaunch(config.BubbleShieldThrowable, origin, lookDirection, OwnerClientId, 1f, duration))
            return GadgetResult.Failed;

        nextServerBubbleTime = Time.time + Mathf.Max(0f, cooldown - ServerCooldownTolerance);
        cooldownLeft = cooldown;

        LogV($"Bolla lanciata: {duration:F0} s, ricarica {cooldown:F0} s (T{config.CurrentTier}).");
        return GadgetResult.Activated;
    }

    [Rpc(SendTo.Owner)]
    private void BubbleResultOwnerRpc(GadgetResult result, float duration, float cooldownLeft)
    {
        switch (result)
        {
            case GadgetResult.Activated:
                nextLocalBubbleTime = Time.time + cooldownLeft;
                Feedback($"Bubble Shield — {Mathf.RoundToInt(duration)} s");
                break;
            case GadgetResult.Cooldown:
                nextLocalBubbleTime = Time.time + cooldownLeft;
                Feedback($"Bubble Shield recharging — {Mathf.CeilToInt(cooldownLeft)} s");
                break;
            case GadgetResult.NotQuartermaster:
                nextLocalBubbleTime = 0f;
                Feedback("Shields: Quartermaster gear");
                break;
            case GadgetResult.Unavailable:
                nextLocalBubbleTime = 0f;
                Feedback("Bubble Shield unavailable");
                break;
            default:
                nextLocalBubbleTime = 0f;
                Feedback("Bubble Shield failed");
                break;
        }
    }

    /// <summary>
    /// Rev BV-c — fine di un'area (evento server di ThrowableSystem). Interessa solo le bolle lanciate
    /// da questo giocatore: riepilogo al proprietario.
    /// </summary>
    private void HandleServerAreaFinished(ulong throwerClientId, ThrowableData data, float totalHealed, int crewCount)
    {
        if (!IsServer || !IsSpawned || throwerClientId != OwnerClientId || data == null) return;
        if (data.EffectKind != ThrowEffectKind.BubbleShield) return;
        BubbleSummaryOwnerRpc(crewCount);
    }

    [Rpc(SendTo.Owner)]
    private void BubbleSummaryOwnerRpc(int crewCount)
    {
        Feedback(crewCount > 0 ? $"Bubble Shield: covered {crewCount} crew" : "Bubble Shield: no one covered");
    }

    /// <summary>Debug: il server ha azzerato le ricariche, l'owner fa lo stesso (testi coerenti).</summary>
    [Rpc(SendTo.Owner)]
    private void CooldownResetOwnerRpc()
    {
        nextLocalShieldTime = 0f;
        nextLocalBubbleTime = 0f;
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private void Feedback(string text)
    {
        if (medKit != null) medKit.ShowFeedback(text);
    }

    private void LogV(string message)
    {
        if (logVerbose) Debug.Log($"[PlayerQuartermasterGadgets/{OwnerClientId}] {message}");
    }

    // ── Debug overlay (solo Editor/Development, solo server) ───────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        if (!showDebugUI) return;
        if (!IsServer || !IsSpawned) return;

        // Sotto il pannello del drone (x 940, banda per OwnerClientId con passo 250).
        float y = 310 + (OwnerClientId * 250f) + 200f;

        GUILayout.BeginArea(new Rect(940, y, 320, 50));
        GUILayout.BeginVertical("box");
        float shieldLeft = Mathf.Max(0f, nextServerShieldTime - Time.time);
        float bubbleLeft = Mathf.Max(0f, nextServerBubbleTime - Time.time);
        GUILayout.Label($"[QM] Client {OwnerClientId} — {(IsQuartermaster ? "Quartermaster" : "altro ruolo")} · " +
                        $"T{Tier} · scudo {(shieldLeft > 0f ? $"{shieldLeft:F0} s" : "pronto")} · " +
                        $"bolla {(bubbleLeft > 0f ? $"{bubbleLeft:F0} s" : "pronta")}");
        if ((shieldLeft > 0f || bubbleLeft > 0f) && GUILayout.Button("Azzera ricariche"))
        {
            nextServerShieldTime = 0f;
            nextServerBubbleTime = 0f;
            CooldownResetOwnerRpc();
        }
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}