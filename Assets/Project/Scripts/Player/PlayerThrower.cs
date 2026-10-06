using System.Collections.Generic;
using SpaceSurvivor.Ship;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// <summary>
/// PlayerThrower — lancio di oggetti dal giocatore (Rev BS-a framework, Rev BS-b bomba curativa;
/// workshop bomba curativa + framework di lancio Q51–Q67, tutte come raccomandate).
///
/// PATTERN per-player (come PlayerMedKit / PlayerHealthSystem): vive sul root del Player
/// prefab, una istanza per client connesso; registro statico per OwnerClientId +
/// LocalInstance + TryGetByClientId.
///
/// RUOLI DEGLI ATTORI:
///   - OWNER: input, mira (arco e punto d'arrivo visibili solo a chi lancia), richiesta di
///     lancio al server con origine e direzione della propria camera.
///   - SERVER: valida (mittente = proprietario, vivo, intervallo minimo, qualcosa da lanciare) e
///     passa a ThrowableSystem, che simula il volo e avvisa tutti.
///
/// INPUT (Q59-a): azione "ThrowGrenade" (G / RB) con interazione Press "Press and Release",
/// ricevuta via SendMessages di PlayerInput (OnThrowGrenade). Tieni premuto per mirare,
/// rilascia per lanciare. Il rilascio arriva dal messaggio (isPressed false); come rete di
/// sicurezza, durante la mira l'azione del PlayerInput viene anche interrogata
/// (InputAction.IsPressed, New Input System): le due vie convergono sullo stesso rilascio
/// idempotente. G e RB sono condivisi con RepairKey_3: gli slider dei minigame si iscrivono solo
/// mentre giocano, e tutti i minigame disabilitano il PlayerController, che qui è un gate.
///
/// GATE (come il kit medico): vivo, PlayerController attivo (non a postazione, tablet, letto,
/// pannello o a terra), tablet chiuso, nessuna interazione continua, nessun canale del kit in
/// corso (Rev BS-b · Q66-a; il kit a sua volta ignora H/J/K/L durante la mira). Se un gate cade
/// durante la mira, la mira si chiude senza lanciare (nessun annullo esplicito in v1).
///
/// ARCO DI MIRA (Q60-a): LineRenderer creato a runtime come figlio (solo owner) e marcatore dal
/// prefab dello SO, entrambi senza collider. Stessa simulazione del server (ThrowBallistics):
/// quello che si vede è quello che succede, salvo chi si muove dopo il lancio.
///
/// SORGENTE (Rev BS-b · Q58-a · Q65-a): il kit medico personale (PlayerMedKit), pezzo
/// ThrowableData.KitItem. Il client controlla il conteggio replicato per aprire la mira; il
/// server lo ricontrolla e consuma UN pezzo solo se il lancio parte (ServerTryConsume). Con
/// debugFreeThrows (Editor e Development Build) si lancia anche a kit vuoto e non si consuma
/// nulla (Q67-a).
///
/// RUOLO (Q57-a): il server prende al lancio il moltiplicatore del profilo del kit (Corpsman 1,
/// altri 0.6) e lo passa a ThrowableSystem, che lo applica alla cura del campo.
///
/// ESITI (Q60-a · Q64-a): alla detonazione "landed" / "hit <ruolo>"; a fine campo il riepilogo
/// ("Healing field: +N HP to K crew" o "no one healed"), mandato dal server al proprietario.
/// </summary>
[RequireComponent(typeof(PlayerHealthSystem))]
public class PlayerThrower : NetworkBehaviour
{
    [Header("Lanciabile")]
    [Tooltip("ThrowableData che questo giocatore lancia. Deve stare anche nel catalogo di ThrowableSystem " +
             "(scena Game).")]
    [SerializeField] private ThrowableData throwable;

    [Header("Feedback a schermo")]
    [Tooltip("Testo TMP nel Canvas HUD del Player, sotto la riga del kit medico (ThrowFeedback). Scritto " +
             "solo dal proprietario.")]
    [SerializeField] private TextMeshProUGUI feedbackText;

    [Header("Debug")]
    [Tooltip("Log verboso di mira e lanci. Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    [Tooltip("Lanci anche a kit vuoto e senza consumare nulla (solo Editor e Development Build: ignorato " +
             "nelle build di rilascio, Q67-a). Default off.")]
    [SerializeField] private bool debugFreeThrows = false;

    private const string ThrowActionName = "ThrowGrenade";
    private const float PreviewStepSeconds = 1f / 30f;
    private const int MaxPreviewPoints = 128;

    /// <summary>
    /// Tolleranza dell'intervallo minimo sul server: le RPC arrivano con un jitter, quindi il server
    /// accetta un lancio fino a questi secondi prima della fine dell'intervallo visto dal client.
    /// </summary>
    private const float ServerCooldownTolerance = 0.15f;

    // ── Registro statico per-clientId ──
    private static readonly Dictionary<ulong, PlayerThrower> activeByClientId = new Dictionary<ulong, PlayerThrower>();

    /// <summary>Istanza del client locale (IsOwner).</summary>
    public static PlayerThrower LocalInstance { get; private set; }

    /// <summary>Trova il lanciatore del client indicato.</summary>
    public static bool TryGetByClientId(ulong clientId, out PlayerThrower instance)
        => activeByClientId.TryGetValue(clientId, out instance);

    // ── Esiti (server → owner) ──
    private enum ThrowResult : byte
    {
        Launched = 0,
        NothingToThrow = 1,
        Cooldown = 2,
        Failed = 3
    }

    // ── Riferimenti sibling ──
    private PlayerHealthSystem health;       // server e owner
    private PlayerController controller;     // owner: gate
    private InteractionSystem interaction;   // owner: gate
    private TabletStation tablet;            // owner: gate
    private PlayerMedKit medKit;             // server e owner: sorgente, ruolo, esclusione col canale (Rev BS-b)
    private Transform cameraTransform;       // owner: origine e direzione del lancio
    private PlayerInput playerInput;         // owner: rete di sicurezza sul rilascio
    private InputAction throwAction;

    // ── Stato owner ──
    private bool aiming;
    private float nextLocalThrowTime;
    private float feedbackTimer;
    private LineRenderer arcLine;
    private GameObject aimMarker;
    private bool warnedMissingArcMaterial;
    private readonly Vector3[] previewPoints = new Vector3[MaxPreviewPoints];
    private readonly RaycastHit[] previewHits = new RaycastHit[16];

    // ── Stato server ──
    private float nextServerThrowTime;

    /// <summary>true mentre il proprietario tiene premuto per mirare (solo owner).</summary>
    public bool IsAiming => aiming;

    /// <summary>Il lanciabile assegnato (può essere null).</summary>
    public ThrowableData Throwable => throwable;

    // ── Lifecycle NGO ──────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        health = GetComponent<PlayerHealthSystem>();
        medKit = GetComponent<PlayerMedKit>();
        activeByClientId[OwnerClientId] = this;

        if (IsServer)
        {
            if (throwable == null)
                Debug.LogWarning("[PlayerThrower] Nessun ThrowableData assegnato sul Player prefab: non si lancia nulla.");
            if (medKit == null)
                Debug.LogWarning("[PlayerThrower] Nessun PlayerMedKit sul Player: niente sorgente, si lancia solo " +
                                 "con debugFreeThrows.");
            ThrowableSystem.OnServerAreaFinished += HandleServerAreaFinished;
        }

        if (IsOwner)
        {
            LocalInstance = this;
            controller = GetComponent<PlayerController>();
            interaction = GetComponent<InteractionSystem>();
            tablet = GetComponent<TabletStation>();
            playerInput = GetComponent<PlayerInput>();
            Camera cam = GetComponentInChildren<Camera>();
            cameraTransform = cam != null ? cam.transform : null;

            if (cameraTransform == null)
                Debug.LogError("[PlayerThrower] Nessuna Camera figlia del Player: impossibile lanciare.");
            if (feedbackText == null)
                Debug.LogWarning("[PlayerThrower] feedbackText non assegnato: nessuna riga di esito a schermo. " +
                                 "Vedi guida Editor di Rev BS-a (ThrowFeedback).");

            ThrowableSystem.OnClientDetonated += HandleClientDetonated;
            SetFeedback(string.Empty, 0f);
        }
    }

    public override void OnNetworkDespawn()
    {
        ThrowableSystem.OnClientDetonated -= HandleClientDetonated;
        ThrowableSystem.OnServerAreaFinished -= HandleServerAreaFinished;

        if (activeByClientId.TryGetValue(OwnerClientId, out PlayerThrower registered) && registered == this)
            activeByClientId.Remove(OwnerClientId);

        if (LocalInstance == this)
            LocalInstance = null;

        aiming = false;
        if (arcLine != null) Destroy(arcLine.gameObject);
        if (aimMarker != null) Destroy(aimMarker);
        arcLine = null;
        aimMarker = null;
    }

    // ── Input (SendMessages di PlayerInput — Q59-a) ────────────────────────────

    /// <summary>Azione "ThrowGrenade" (G / RB), Press and Release: premuto → mira, rilasciato → lancio.</summary>
    public void OnThrowGrenade(InputValue value)
    {
        if (value.isPressed)
            TryBeginAim();
        else
            ReleaseAim();
    }

    private void TryBeginAim()
    {
        if (!IsOwner || !IsSpawned || aiming) return;
        if (!CanThrowLocally()) return;   // postazione, tablet, letto, a terra: ignorato in silenzio

        if (throwable == null || cameraTransform == null)
        {
            SetFeedback("Nothing to throw", 2f);
            return;
        }
        if (ThrowableSystem.Instance == null)
        {
            SetFeedback("Throwing unavailable", HoldSeconds);
            return;
        }
        if (Time.time < nextLocalThrowTime) return;
        if (!HasThrowableLocally())
        {
            SetFeedback($"No {throwable.DisplayName}", HoldSeconds);
            return;
        }

        if (throwAction == null && playerInput != null && playerInput.actions != null)
            throwAction = playerInput.actions.FindAction(ThrowActionName, throwIfNotFound: false);

        EnsureAimVisuals();
        aiming = true;
        UpdatePreview();
        LogV("Mira avviata.");
    }

    private void ReleaseAim()
    {
        if (!aiming) return;
        aiming = false;
        SetAimVisible(false);

        if (!CanThrowLocally() || cameraTransform == null || throwable == null) return;

        nextLocalThrowTime = Time.time + throwable.ThrowCooldownSeconds;
        ThrowServerRpc(cameraTransform.position, cameraTransform.forward);
        LogV("Lancio richiesto.");
    }

    private void CancelAim()
    {
        if (!aiming) return;
        aiming = false;
        SetAimVisible(false);
        LogV("Mira chiusa senza lancio (gate).");
    }

    // ── Update (owner) ─────────────────────────────────────────────────────────

    private void Update()
    {
        if (!IsOwner || !IsSpawned) return;

        if (aiming)
        {
            if (!CanThrowLocally() || throwable == null || cameraTransform == null)
                CancelAim();
            else if (throwAction != null && !throwAction.IsPressed())
                ReleaseAim();   // rete di sicurezza: rilascio senza messaggio
            else
                UpdatePreview();
        }

        if (feedbackTimer > 0f)
        {
            feedbackTimer -= Time.deltaTime;
            if (feedbackTimer <= 0f)
                SetFeedback(string.Empty, 0f);
        }
    }

    /// <summary>
    /// Si può mirare e lanciare: vivo, movimento libero (non a postazione, tablet, letto, pannello o a
    /// terra), tablet chiuso, nessuna interazione continua in corso. Stesse regole del kit medico.
    /// </summary>
    private bool CanThrowLocally()
    {
        if (health == null || !health.IsAlive) return false;
        if (controller == null || !controller.enabled) return false;
        if (tablet != null && tablet.IsBusy) return false;
        if (interaction != null && interaction.IsInteracting) return false;
        if (medKit != null && medKit.IsChanneling) return false;   // Rev BS-b · Q66-a
        return true;
    }

    /// <summary>
    /// C'è qualcosa da lanciare? Il pezzo nel kit (conteggio replicato), oppure debugFreeThrows
    /// (Editor e Development Build).
    /// </summary>
    private bool HasThrowableLocally() => DebugFreeThrowsActive || KitCount() > 0;

    private int KitCount() => medKit != null && throwable != null ? medKit.GetCount(throwable.KitItem) : 0;

    private bool DebugFreeThrowsActive => debugFreeThrows && Debug.isDebugBuild;

    // ── Arco di mira (owner) ───────────────────────────────────────────────────

    private void EnsureAimVisuals()
    {
        if (arcLine == null)
        {
            var arcObject = new GameObject("ThrowAimArc");
            arcObject.transform.SetParent(transform, false);
            arcLine = arcObject.AddComponent<LineRenderer>();
            arcLine.useWorldSpace = true;
            arcLine.positionCount = 0;
            arcLine.shadowCastingMode = ShadowCastingMode.Off;
            arcLine.receiveShadows = false;
            arcLine.numCapVertices = 2;
            arcLine.enabled = false;
        }

        if (throwable != null)
        {
            arcLine.widthMultiplier = throwable.AimArcWidth;
            if (throwable.AimArcMaterial != null)
                arcLine.sharedMaterial = throwable.AimArcMaterial;
            else if (!warnedMissingArcMaterial)
            {
                warnedMissingArcMaterial = true;
                Debug.LogWarning($"[PlayerThrower] {throwable.name} senza Aim Arc Material: arco con il materiale di default.");
            }
        }

        if (aimMarker == null && throwable != null && throwable.AimMarkerPrefab != null)
        {
            aimMarker = Instantiate(throwable.AimMarkerPrefab);
            ThrowableSystem.DisableColliders(aimMarker);
            SetMarkerVisible(false);
        }
    }

    private void UpdatePreview()
    {
        Vector3 origin = cameraTransform.position;
        Vector3 velocity = ThrowBallistics.LaunchDirection(cameraTransform.forward, throwable.AimPitchOffsetDegrees) *
                           throwable.LaunchSpeed;

        int count = ThrowBallistics.SimulatePath(origin, velocity, throwable, transform, previewHits, previewPoints,
                                                 PreviewStepSeconds, out Vector3 endPoint, out Vector3 endNormal,
                                                 out bool _);

        // Il primo punto è dentro la camera: l'arco parte dal secondo.
        int first = count > 2 ? 1 : 0;
        int visible = count - first;
        if (arcLine != null)
        {
            arcLine.positionCount = Mathf.Max(0, visible);
            for (int i = 0; i < visible; i++)
                arcLine.SetPosition(i, previewPoints[first + i]);
            arcLine.enabled = visible >= 2;
        }

        if (aimMarker != null)
        {
            aimMarker.transform.SetPositionAndRotation(endPoint + endNormal * 0.01f,
                                                       Quaternion.FromToRotation(Vector3.up, endNormal));
            SetMarkerVisible(true);
        }
    }

    private void SetAimVisible(bool visible)
    {
        if (arcLine != null) arcLine.enabled = visible;
        SetMarkerVisible(visible);
    }

    /// <summary>Renderer.enabled, non SetActive (invariante dei componenti visivi).</summary>
    private void SetMarkerVisible(bool visible)
    {
        if (aimMarker == null) return;
        Renderer[] renderers = aimMarker.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            renderers[i].enabled = visible;
    }

    // ── RPC: lancio (owner → server) ───────────────────────────────────────────

    [Rpc(SendTo.Server)]
    private void ThrowServerRpc(Vector3 origin, Vector3 lookDirection, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (sender != OwnerClientId)
        {
            Debug.LogWarning($"[PlayerThrower] Lancio rifiutato: il client {sender} ha chiesto di lanciare per il " +
                             $"client {OwnerClientId}.");
            return;
        }

        ThrowResult result = ServerTryThrow(origin, lookDirection);
        if (result != ThrowResult.Launched)
            ThrowResultOwnerRpc(result);
    }

    /// <summary>
    /// Validazione e lancio. SERVER ONLY. Il pezzo del kit si consuma solo se il lancio parte e
    /// debugFreeThrows non è attivo (Q67-a).
    /// </summary>
    private ThrowResult ServerTryThrow(Vector3 origin, Vector3 lookDirection)
    {
        if (throwable == null) return ThrowResult.Failed;
        if (health == null || !health.IsAlive) return ThrowResult.Failed;
        if (Time.time < nextServerThrowTime) return ThrowResult.Cooldown;
        if (!ServerHasThrowable()) return ThrowResult.NothingToThrow;

        ThrowableSystem system = ThrowableSystem.Instance;
        if (system == null)
        {
            Debug.LogError("[PlayerThrower] ThrowableSystem assente in scena: impossibile lanciare.");
            return ThrowResult.Failed;
        }

        // Q57-a: moltiplicatore del profilo di ruolo, preso ora e registrato con l'area.
        float effectScale = medKit != null ? medKit.ProfileFor(OwnerClientId).EffectMultiplier : 1f;
        if (!system.ServerLaunch(throwable, origin, lookDirection, OwnerClientId, effectScale))
            return ThrowResult.Failed;

        bool consumed = false;
        if (!DebugFreeThrowsActive && medKit != null)
            consumed = medKit.ServerTryConsume(throwable.KitItem);

        nextServerThrowTime = Time.time + Mathf.Max(0f, throwable.ThrowCooldownSeconds - ServerCooldownTolerance);
        LogV($"Lanciato {throwable.name} (effetto ×{effectScale:F2}, " +
             $"{(consumed ? "consumato dal kit" : "senza consumo")}).");
        return ThrowResult.Launched;
    }

    /// <summary>C'è qualcosa da lanciare, lato server? Il pezzo nel kit, oppure debugFreeThrows.</summary>
    private bool ServerHasThrowable() => DebugFreeThrowsActive || KitCount() > 0;

    [Rpc(SendTo.Owner)]
    private void ThrowResultOwnerRpc(ThrowResult result)
    {
        string label = throwable != null ? throwable.DisplayName : "Throwable";
        switch (result)
        {
            case ThrowResult.NothingToThrow:
                SetFeedback($"No {label}", HoldSeconds);
                break;
            case ThrowResult.Cooldown:
                SetFeedback("Not ready", HoldSeconds);
                break;
            default:
                SetFeedback("Throw failed", HoldSeconds);
                break;
        }
    }

    // ── Esito della detonazione (tutti i client; reagisce solo il proprietario che ha lanciato) ──

    private void HandleClientDetonated(ulong thrower, ulong hitClient, ThrowableData data)
    {
        if (!IsOwner || thrower != OwnerClientId) return;

        string label = data != null ? data.DisplayName : "Throwable";
        if (hitClient != ThrowableSystem.NoClient && hitClient != OwnerClientId)
            SetFeedback($"{label} hit {CrewRoles.ToDisplayName(PlayerCrewRole.GetRole(hitClient))}", HoldSeconds);
        else
            SetFeedback($"{label} landed", HoldSeconds);
    }

    // ── Riepilogo del campo (server → proprietario che ha lanciato, Q64-a) ──────

    private void HandleServerAreaFinished(ulong thrower, ThrowableData data, float totalHealed, int crewCount)
    {
        if (!IsServer || !IsSpawned || thrower != OwnerClientId || data == null) return;
        AreaSummaryOwnerRpc(data.EffectKind, Mathf.RoundToInt(totalHealed), crewCount);
    }

    [Rpc(SendTo.Owner)]
    private void AreaSummaryOwnerRpc(ThrowEffectKind effectKind, int healedHp, int crewCount)
    {
        switch (effectKind)
        {
            case ThrowEffectKind.HealingField:
                SetFeedback(crewCount > 0 && healedHp > 0
                                ? $"Healing field: +{healedHp} HP to {crewCount} crew"
                                : "Healing field: no one healed",
                            HoldSeconds);
                break;
        }
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private float HoldSeconds => throwable != null ? throwable.FeedbackHoldSeconds : 2f;

    private void SetFeedback(string text, float holdSeconds)
    {
        feedbackTimer = holdSeconds;
        if (feedbackText != null)
            feedbackText.text = text;
    }

    private void LogV(string message)
    {
        if (logVerbose) Debug.Log($"[PlayerThrower/{OwnerClientId}] {message}");
    }
}