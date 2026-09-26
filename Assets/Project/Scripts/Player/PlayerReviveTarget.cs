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
///
/// REV BM — PROFILO DI RUOLO CABLATO: ResolveProfile consulta PlayerCrewRole
/// (identità di ruolo networked). Rianimatore Corpsman → profilo Corpsman di
/// DefibConfig (2s / 60%); chiunque altro (o ruolo non ancora dichiarato) →
/// profilo di default (4s / 30%, malus Rev U). Il server NON si fida più del
/// clientId passato dal client: il rianimatore è ricavato da SenderClientId
/// (altrimenti un client potrebbe dichiararsi "il Corpsman" per ottenere il 60%).
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

    [Tooltip("Fallback profilo Corpsman se config è null (Rev BM). GDD ruoli: 2s.")]
    [SerializeField] private float fallbackCorpsmanChannelSeconds = 2f;
    [Tooltip("Fallback profilo Corpsman se config è null (Rev BM). GDD ruoli: 60%.")]
    [Range(0f, 1f)]
    [SerializeField] private float fallbackCorpsmanHpRestoreFraction = 0.60f;

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
        // Rev BM: nessun clientId nel payload — il server usa SenderClientId.
        ReviveServerRpc();
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
    private void ReviveServerRpc(ServerRpcParams rpcParams = default)
    {
        // Rev BM: il rianimatore è CHI HA INVIATO l'RPC, non un id nel payload —
        // ora che il profilo dipende dal ruolo, un id dichiarato dal client sarebbe
        // falsificabile (60% HP "da Corpsman" per chiunque).
        ulong requesterClientId = rpcParams.Receive.SenderClientId;

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
    /// Risolve il profilo defib per il rianimatore (Q5-a Rev BD → cablato Rev BM).
    /// Corpsman (ruolo networked via PlayerCrewRole) → profilo Corpsman di
    /// DefibConfig (2s / 60%); qualsiasi altro ruolo, o ruolo non ancora dichiarato
    /// (None nella finestra di spawn) → profilo di DEFAULT (4s / 30%, malus Rev U).
    /// Deterministico su stato replicato: client (durata canale) e server (frazione
    /// HP) leggono lo stesso NetworkVariable → stesso profilo. In caso di cambio
    /// ruolo a canale in corso vince il server (autorità sulla frazione HP).
    /// </summary>
    private DefibProfile ResolveProfile(ulong reviverClientId)
    {
        bool isCorpsman = PlayerCrewRole.HasRole(reviverClientId, CrewRole.Corpsman);

        if (config != null)
        {
            return isCorpsman
                ? new DefibProfile(config.CorpsmanChannelSeconds, config.CorpsmanHpRestoreFraction)
                : new DefibProfile(config.DefaultChannelSeconds, config.DefaultHpRestoreFraction);
        }

        return isCorpsman
            ? new DefibProfile(Mathf.Max(0.01f, fallbackCorpsmanChannelSeconds), Mathf.Clamp01(fallbackCorpsmanHpRestoreFraction))
            : new DefibProfile(Mathf.Max(0.01f, fallbackChannelSeconds), Mathf.Clamp01(fallbackHpRestoreFraction));
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