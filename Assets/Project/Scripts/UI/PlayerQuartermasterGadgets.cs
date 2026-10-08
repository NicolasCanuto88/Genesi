using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PlayerQuartermasterGadgets — gadget del Quartermaster (Rev BV-b · workshop Quartermaster, Q95-a,
/// Q97-a, Q101-a, Q102-a). In BV-b: lo Scudo Personale. La Bubble Shield arriva in BV-c.
///
/// SCUDO PERSONALE (Q95-a · Q102-a): premendo F (Y col gamepad) il Quartermaster si dà lo stato
/// Shielded (Buff a tempo, senza modificatori) per la durata del tier, poi ricarica. Durata e
/// ricarica dal tier (QuartermasterGadgetConfig, Q101-a: T2 = 7 s, ricarica 40 s dall'attivazione).
/// Lo scudo para SOLO i proiettili (Q95/Q99): prima del Combat non cambia il danno, si vede
/// (film su tutti i client, PlayerShieldFilm) e ha tempi veri. Il Combat interrogherà lo stato.
///
/// SOLO QUARTERMASTER (Q95-a, Q7-a): i gadget sono l'equipaggiamento del ruolo. Senza
/// Quartermaster in crew gli scudi mancano. Un altro ruolo che preme F riceve "Shields: Quartermaster
/// gear" e non succede nulla.
///
/// PATTERN per-player (come PlayerMedKit / PlayerNanomedicDrone): vive sul root del Player prefab,
/// una istanza per client connesso; registro statico per OwnerClientId + LocalInstance +
/// TryGetByClientId.
///
/// RETE:
///   - lo scudo è uno stato di PlayerStatusEffects: maschera replicata (film visibile a tutti,
///     anche a chi entra in partita), rinnovo e pulizia al clone già pronti;
///   - la ricarica vive sul server (autorità) e sull'owner (testi): stesso schema di PlayerThrower,
///     con una tolleranza sul server per il jitter delle RPC;
///   - nessuna NetworkVariable nuova.
///
/// INPUT: azione "Shield" (F / Y) della mappa Player, via SendMessages di PlayerInput (OnShield).
/// In BV-b basta la pressione. F e Y sono anche RepairKey_2 e RepairKey_1, usati solo nei minigame,
/// che disattivano il PlayerController (gate qui sotto). Rev BV-c: tocco = scudo, tenuto = mira
/// della bolla (Q97-a).
///
/// GATE (come kit, lancio e drone): vivo, PlayerController attivo (non a postazione, tablet, letto,
/// pannello o a terra), tablet chiuso, nessuna interazione continua, nessun canale del kit, nessuna
/// mira di lancio e nessuna pressione del drone in corso.
///
/// AUTORITÀ: la RPC accetta solo il proprietario. Il server rilegge vivo, ruolo, tier e ricarica.
/// </summary>
[RequireComponent(typeof(PlayerHealthSystem))]
public class PlayerQuartermasterGadgets : NetworkBehaviour
{
    [Header("Config (ScriptableObject)")]
    [Tooltip("Assegna l'asset QuartermasterGadgetConfig. Senza config i gadget non si usano (errore a log allo " +
             "spawn sul server).")]
    [SerializeField] private QuartermasterGadgetConfig config;

    [Header("Debug")]
    [Tooltip("Overlay OnGUI con tier e ricarica (solo Editor/Development Build, solo server). Standard Rev BA — " +
             "default off.")]
    [SerializeField] private bool showDebugUI = false;

    [Tooltip("Log verboso di attivazioni e rifiuti. Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    /// <summary>
    /// Tolleranza della ricarica sul server: le RPC arrivano con un jitter, quindi il server accetta
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
    private enum ShieldResult : byte
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
    private PlayerThrower thrower;              // owner: gate (mira di lancio)
    private PlayerNanomedicDrone drone;         // owner: gate (pressione del drone)

    // ── Stato owner ──
    private float nextLocalShieldTime;
    private float localShieldEndTime;

    // ── Stato server ──
    private float nextServerShieldTime;

    /// <summary>true se questo giocatore è Quartermaster (ruolo replicato).</summary>
    public bool IsQuartermaster => PlayerCrewRole.HasRole(OwnerClientId, CrewRole.Quartermaster);

    /// <summary>Tier dei gadget (oggi dallo SO, Q101-a). T1 senza config.</summary>
    public int Tier => config != null ? config.CurrentTier : QuartermasterGadgetConfig.MinTier;

    /// <summary>Secondi alla fine della ricarica dello scudo personale (solo owner, 0 = pronto).</summary>
    public float ShieldCooldownRemaining => Mathf.Max(0f, nextLocalShieldTime - Time.time);

    /// <summary>Secondi rimasti dello scudo personale attivato da sé (solo owner, 0 = nessuno).</summary>
    public float OwnShieldRemaining => Mathf.Max(0f, localShieldEndTime - Time.time);

    /// <summary>Durata e ricarica dello scudo personale al tier corrente (false se non disponibile).</summary>
    public bool TryGetPersonalShieldStats(out float durationSeconds, out float cooldownSeconds)
    {
        durationSeconds = 0f;
        cooldownSeconds = 0f;
        return config != null && config.TryGetPersonalShield(Tier, out durationSeconds, out cooldownSeconds);
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

            if (config == null)
                Debug.LogError("[PlayerQuartermasterGadgets] QuartermasterGadgetConfig non assegnato sul Player " +
                               "prefab: i gadget del Quartermaster non si usano. Assegnare l'asset " +
                               "QuartermasterGadgetConfig.");
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
            nextLocalShieldTime = 0f;
            localShieldEndTime = 0f;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (activeByClientId.TryGetValue(OwnerClientId, out PlayerQuartermasterGadgets registered) &&
            registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;
    }

    // ── Input (SendMessages di PlayerInput) ────────────────────────────────────

    /// <summary>Azione "Shield" (F / Y). BV-b: la pressione attiva lo scudo personale.</summary>
    public void OnShield(InputValue value)
    {
        if (value.isPressed)
            TryActivatePersonalShield();
    }

    private void TryActivatePersonalShield()
    {
        if (!IsOwner || !IsSpawned) return;
        if (!CanUseLocally()) return;   // postazione, tablet, letto, a terra, canale, mira, drone: ignorato in silenzio

        if (!IsQuartermaster)
        {
            Feedback("Shields: Quartermaster gear");
            return;
        }
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

    /// <summary>
    /// Si può usare un gadget: vivo, movimento libero (non a postazione, tablet, letto, pannello o a
    /// terra), tablet chiuso, nessuna interazione continua, nessun canale del kit, nessuna mira di
    /// lancio e nessuna pressione del drone. Stesse regole del kit medico, di PlayerThrower e del drone.
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

    // ── RPC: scudo personale (owner → server) ──────────────────────────────────

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

        ShieldResult result = ServerTryActivateShield(out float duration, out float cooldownLeft);
        ShieldResultOwnerRpc(result, duration, cooldownLeft);
    }

    /// <summary>
    /// Validazione e attivazione. SERVER ONLY. In caso di successo cooldownLeft è la ricarica intera;
    /// se la ricarica non è finita, è il tempo che manca.
    /// </summary>
    private ShieldResult ServerTryActivateShield(out float duration, out float cooldownLeft)
    {
        duration = 0f;
        cooldownLeft = 0f;

        if (config == null || statusEffects == null) return ShieldResult.Failed;
        if (health == null || !health.IsAlive) return ShieldResult.Failed;
        if (!IsQuartermaster) return ShieldResult.NotQuartermaster;
        if (!config.TryGetPersonalShield(config.CurrentTier, out duration, out float cooldown))
            return ShieldResult.Unavailable;

        if (Time.time < nextServerShieldTime)
        {
            cooldownLeft = nextServerShieldTime - Time.time;
            return ShieldResult.Cooldown;
        }

        statusEffects.ApplyEffectForSeconds(StatusEffectType.Shielded, duration);
        nextServerShieldTime = Time.time + Mathf.Max(0f, cooldown - ServerCooldownTolerance);
        cooldownLeft = cooldown;

        LogV($"Scudo personale attivo: {duration:F0} s, ricarica {cooldown:F0} s (T{config.CurrentTier}).");
        return ShieldResult.Activated;
    }

    [Rpc(SendTo.Owner)]
    private void ShieldResultOwnerRpc(ShieldResult result, float duration, float cooldownLeft)
    {
        switch (result)
        {
            case ShieldResult.Activated:
                nextLocalShieldTime = Time.time + cooldownLeft;
                localShieldEndTime = Time.time + duration;
                Feedback($"Personal Shield — {Mathf.RoundToInt(duration)} s");
                break;
            case ShieldResult.Cooldown:
                nextLocalShieldTime = Time.time + cooldownLeft;
                Feedback($"Personal Shield recharging — {Mathf.CeilToInt(cooldownLeft)} s");
                break;
            case ShieldResult.NotQuartermaster:
                nextLocalShieldTime = 0f;
                Feedback("Shields: Quartermaster gear");
                break;
            case ShieldResult.Unavailable:
                nextLocalShieldTime = 0f;
                Feedback("Personal Shield unavailable");
                break;
            default:
                nextLocalShieldTime = 0f;
                Feedback("Shield failed");
                break;
        }
    }

    /// <summary>Debug: il server ha azzerato la ricarica, l'owner fa lo stesso (testi coerenti).</summary>
    [Rpc(SendTo.Owner)]
    private void CooldownResetOwnerRpc()
    {
        nextLocalShieldTime = 0f;
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
        float left = Mathf.Max(0f, nextServerShieldTime - Time.time);
        GUILayout.Label($"[QM] Client {OwnerClientId} — {(IsQuartermaster ? "Quartermaster" : "altro ruolo")} · " +
                        $"T{Tier} · scudo {(left > 0f ? $"tra {left:F0} s" : "pronto")}");
        if (left > 0f && GUILayout.Button("Azzera ricarica"))
        {
            nextServerShieldTime = 0f;
            CooldownResetOwnerRpc();
        }
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}
