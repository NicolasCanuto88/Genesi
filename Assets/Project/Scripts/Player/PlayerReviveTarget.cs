using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerReviveTarget — bersaglio di defibrillazione di un giocatore a terra
/// (Morte &amp; Rianimazione, D27). Vive sul root del Player prefab, accanto a
/// PlayerHealthSystem. Implementa IInteractable (convenzione di progetto): mentre
/// il proprietario è DOWNED, un ALTRO membro dell'equipaggio che lo guarda vede il
/// prompt e può defibrillare.
///
/// MODELLO (Q3-a):
///   - CanInteract(): true solo se questo giocatore è Downed E ci sono cariche
///     defib (DefibChargePool). A T1 (0 cariche) → nessun prompt (Q6-a).
///   - Interazione CONTINUA (canale): il rianimatore tiene premuto per la durata
///     del profilo di ruolo. È il PRIMO utente del path continuous di
///     InteractionSystem (Ladder/Door usano solo one-shot → zero regressione).
///   - Canale INTERROMPIBILE: aborta se il rianimatore distoglie lo sguardo o
///     subisce danno (vulnerabilità co-op). Nessun lock condiviso.
///   - A canale completo → ReviveServerRpc(RequireOwnership=false): il rianimatore
///     NON è owner di questo oggetto, quindi l'RPC non richiede ownership. Il
///     server è l'unico arbitro (first-completer-wins, Q4-a).
///
/// CONTESA (Q4-a, first-completer-wins): ogni rianimatore ha la PROPRIA istanza
/// (replicata) di questo componente sul proprio client, con il proprio canale
/// indipendente. Il primo che completa manda l'RPC; il server rianima; gli altri
/// canali vedono lo stato non più Downed e abortano. Nessun lock da rilasciare →
/// nessun lock orfano se un rianimatore si disconnette a metà.
///
/// DISCONNECT del downed: se questo giocatore si disconnette, NGO despawna il suo
/// NetworkObject → questo componente sparisce su tutti i client → i canali in
/// corso perdono il bersaglio (il raycast non lo colpisce più → abort) e nessun
/// RPC può essere consegnato a un oggetto despawnato. Gestione naturale.
///
/// NOTA input "hold": l'azione Interact usa una Hold interaction (soglia). La
/// soglia è il gate d'AVVIO del canale; la vulnerabilità co-op durante il canale
/// deriva da look-away e danno (entrambi rilevati client-side qui), non dal
/// rilascio del pulsante.
/// </summary>
[RequireComponent(typeof(PlayerHealthSystem))]
public class PlayerReviveTarget : NetworkBehaviour, IInteractable
{
    [Header("Config")]
    [Tooltip("SO di tuning defib (profilo di default). Se null, valori di fallback qui sotto.")]
    [SerializeField] private DefibConfig config;

    [Header("Fallback (se config è null)")]
    [SerializeField] private float fallbackChannelSeconds = 4f;
    [Range(0f, 1f)]
    [SerializeField] private float fallbackHpRestoreFraction = 0.30f;

    [Header("Prompt")]
    [Tooltip("Prompt mostrato al rianimatore. {interact} è sostituito dal tasto dal InputDeviceManager.")]
    [SerializeField] private string revivePrompt = "[{interact}] Rianima (tieni premuto)";

    // ── Riferimenti ──
    private PlayerHealthSystem health;

    // ── Stato del canale (client-side, valorizzato solo sul rianimatore) ──
    private bool channeling;
    private float channelElapsed;
    private float channelDuration;
    private InteractionSystem reviverInteraction;   // InteractionSystem del rianimatore
    private PlayerHealthSystem reviverHealth;        // salute del rianimatore (abort su danno)
    private ulong reviverClientId;
    private float reviverHpAtStart;

    private void Awake()
    {
        health = GetComponent<PlayerHealthSystem>();
    }

    // ── IInteractable ─────────────────────────────────────────────────────────

    public bool CanInteract()
    {
        // Interagibile solo se questo giocatore è a terra e ci sono cariche defib.
        if (health == null || health.State != PlayerHealthSystem.LifeState.Downed)
            return false;
        return DefibChargePool.Instance != null && DefibChargePool.Instance.HasCharge;
    }

    public string GetInteractionPrompt() => revivePrompt;

    public bool IsContinuousInteraction() => true;

    public void OnLookEnter() { }
    public void OnLookExit() { }   // l'abort su look-away è rilevato nel canale (Update)

    public void Interact(GameObject interactor)
    {
        // Chiamato una volta all'inizio dell'hold, sul client del rianimatore.
        if (channeling) return;
        if (interactor == null) return;
        if (!CanInteract()) return;

        reviverInteraction = interactor.GetComponent<InteractionSystem>();
        reviverHealth = interactor.GetComponent<PlayerHealthSystem>();

        NetworkObject no = interactor.GetComponent<NetworkObject>();
        if (no == null) return;
        reviverClientId = no.OwnerClientId;

        DefibProfile profile = ResolveProfile(reviverClientId);
        channelDuration = Mathf.Max(0.01f, profile.ChannelSeconds);
        channelElapsed = 0f;
        reviverHpAtStart = reviverHealth != null ? reviverHealth.CurrentHP : float.MaxValue;
        channeling = true;
    }

    // ── Canale (gira sul client del rianimatore mentre channeling) ──────────────

    private void Update()
    {
        if (!channeling) return;

        // Abort: bersaglio non più a terra (già rianimato da un altro — Q4-a).
        if (health == null || health.State != PlayerHealthSystem.LifeState.Downed)
        {
            AbortChannel();
            return;
        }
        // Abort: rianimatore ha distolto lo sguardo (InteractionSystem ha cambiato/perso il target).
        if (reviverInteraction == null || !ReferenceEquals(reviverInteraction.CurrentInteractable, this))
        {
            AbortChannel();
            return;
        }
        // Abort: rianimatore ha subito danno durante il canale (vulnerabilità co-op).
        if (reviverHealth != null && reviverHealth.CurrentHP < reviverHpAtStart)
        {
            AbortChannel();
            return;
        }

        channelElapsed += Time.deltaTime;
        if (channelElapsed >= channelDuration)
            CompleteChannel();
    }

    private void CompleteChannel()
    {
        channeling = false;
        // Il rianimatore non è owner di questo oggetto → RequireOwnership = false.
        ReviveServerRpc(reviverClientId);
        reviverInteraction?.EndInteraction();
        reviverInteraction = null;
        reviverHealth = null;
    }

    private void AbortChannel()
    {
        channeling = false;
        reviverInteraction?.EndInteraction();
        reviverInteraction = null;
        reviverHealth = null;
    }

    // ── RPC server: rianimazione (Q3-a, Q4-a) ──────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    private void ReviveServerRpc(ulong requesterClientId)
    {
        // Validazione server-authoritative (non ci si fida del client).
        if (health == null) return;
        if (health.State != PlayerHealthSystem.LifeState.Downed) return;   // first-completer-wins

        DefibChargePool pool = DefibChargePool.Instance;
        if (pool == null || !pool.HasCharge) return;

        DefibProfile profile = ResolveProfile(requesterClientId);
        bool revived = health.ServerTryRevive(profile.HpRestoreFraction);   // applica anche CompoundWounds (Q8-a)
        if (revived)
            pool.TryConsumeCharge();
    }

    // ── Seam modificatore di ruolo (Q5-a) ──────────────────────────────────────

    /// <summary>
    /// Risolve il profilo defib per il rianimatore. SEAM: oggi ritorna sempre
    /// l'identità di DEFAULT (non-Corpsman). Quando il sistema ruoli esisterà, qui
    /// si consulterà il ruolo del rianimatore (Corpsman → 2s / 60%). Nessun ramo
    /// Corpsman costruito ora — stesso hook a identità di default di Rev BB. È puro
    /// e deterministico, così client (durata canale) e server (frazione HP)
    /// calcolano lo stesso profilo.
    /// </summary>
    private DefibProfile ResolveProfile(ulong reviverClientId)
    {
        float ch = config != null ? config.DefaultChannelSeconds : Mathf.Max(0.01f, fallbackChannelSeconds);
        float hp = config != null ? config.DefaultHpRestoreFraction : Mathf.Clamp01(fallbackHpRestoreFraction);
        return new DefibProfile(ch, hp);
    }
}

/// <summary>Profilo defib risolto per un rianimatore (Q5-a).</summary>
public readonly struct DefibProfile
{
    public readonly float ChannelSeconds;
    public readonly float HpRestoreFraction;

    public DefibProfile(float channelSeconds, float hpRestoreFraction)
    {
        ChannelSeconds = channelSeconds;
        HpRestoreFraction = Mathf.Clamp01(hpRestoreFraction);
    }
}
