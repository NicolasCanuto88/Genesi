using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerShieldFilm — visuale dello scudo sul giocatore (Rev BV-b · workshop Quartermaster, Q102-a,
/// Q103-a).
///
/// COSA FA: finché il giocatore ha lo stato Shielded (scudo personale del Quartermaster; da Rev BV-c
/// anche la Bubble Shield), sui renderer del corpo compare una pellicola: il materiale dello shader
/// "SpaceSurvivor/Shield Film" aggiunto come materiale in più. Unity disegna un materiale in più sopra
/// l'ultima sotto-mesh, quindi funziona con la capsula di oggi e con la mesh animata della ciurma di
/// domani (MeshRenderer o SkinnedMeshRenderer), senza oggetti aggiuntivi.
///
/// SU TUTTI I CLIENT: legge la maschera replicata di PlayerStatusEffects (IsActive), quindi la vede
/// chiunque, anche chi entra in partita con uno scudo già attivo. Nessuna rete propria.
///
/// IN PRIMA PERSONA: la camera sta dentro il corpo e il film ha le facce posteriori scartate, quindi
/// chi ha lo scudo non lo vede su di sé. Al proprietario resta la riga dell'HUD (statusText):
/// "Personal Shield — N s" per lo scudo che si è dato da solo, "Shield active" negli altri casi.
///
/// DISSOLVENZA: il materiale entra e esce con una sfumatura breve sulla proprietà _Intensity, scritta
/// con un MaterialPropertyBlock sul solo indice del film (il materiale del corpo non cambia). Il
/// materiale aggiunto si toglie a sfumatura finita, ripristinando l'array originale.
///
/// NON FA: nessun effetto sul danno. Lo scudo para solo i proiettili (Q95/Q99), regola del Combat.
///
/// ⚠️ SETUP EDITOR (guida di Rev BV-b): sul root del Player prefab, Body Renderers = la Capsule
/// (domani la mesh della ciurma), Film Material = M_ShieldFilm, Status Text = ShieldStatus
/// (TMP nel Canvas del Player, accanto a ThrowFeedback).
/// </summary>
[RequireComponent(typeof(PlayerStatusEffects))]
public class PlayerShieldFilm : NetworkBehaviour
{
    [Header("Film (Q103-a)")]
    [Tooltip("Renderer del corpo che ricevono il film. Oggi la Capsule del Player; con la mesh della ciurma, i " +
             "suoi renderer. Vuoto = nessun film (avviso a log).")]
    [SerializeField] private Renderer[] bodyRenderers;

    [Tooltip("Materiale con lo shader SpaceSurvivor/Shield Film (M_ShieldFilm), Cull Back.")]
    [SerializeField] private Material filmMaterial;

    [Tooltip("Secondi della dissolvenza in entrata e in uscita.")]
    [Min(0.01f)]
    [SerializeField] private float fadeSeconds = 0.2f;

    [Header("HUD del proprietario")]
    [Tooltip("Testo TMP nel Canvas HUD del Player (ShieldStatus). Scritto solo dal proprietario mentre è schermato.")]
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("Debug")]
    [Tooltip("Log verboso di accensione e spegnimento del film. Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;

    private static readonly int IntensityId = Shader.PropertyToID("_Intensity");

    private PlayerStatusEffects statusEffects;
    private Material[][] originalMaterials;   // per renderer, l'array prima del film
    private int[] filmIndex;                  // per renderer, l'indice del film (-1 = assente)
    private MaterialPropertyBlock propertyBlock;
    private float intensity;                  // 0 … 1, sfumatura corrente
    private bool filmApplied;
    private bool warnedSetup;
    private string lastStatus;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Awake()
    {
        statusEffects = GetComponent<PlayerStatusEffects>();
        propertyBlock = new MaterialPropertyBlock();

        int count = bodyRenderers != null ? bodyRenderers.Length : 0;
        originalMaterials = new Material[count][];
        filmIndex = new int[count];
        for (int i = 0; i < count; i++)
            filmIndex[i] = -1;
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
            SetStatus(string.Empty);
    }

    public override void OnNetworkDespawn()
    {
        RemoveFilm();
        if (IsOwner)
            SetStatus(string.Empty);
    }

    // ── Update: tutti i client ─────────────────────────────────────────────────

    private void Update()
    {
        if (!IsSpawned) return;

        bool shielded = statusEffects != null && statusEffects.IsActive(StatusEffectType.Shielded);

        UpdateFilm(shielded, Time.deltaTime);

        if (IsOwner)
            UpdateStatus(shielded);
    }

    private void UpdateFilm(bool shielded, float deltaTime)
    {
        float target = shielded ? 1f : 0f;
        if (!filmApplied && target <= 0f) return;

        if (!filmApplied)
        {
            if (!ApplyFilm()) return;
            intensity = 0f;
        }

        intensity = Mathf.MoveTowards(intensity, target, deltaTime / fadeSeconds);
        WriteIntensity(intensity);

        if (target <= 0f && intensity <= 0f)
            RemoveFilm();
    }

    /// <summary>Aggiunge il materiale del film a ogni renderer del corpo. false se manca il setup.</summary>
    private bool ApplyFilm()
    {
        if (filmMaterial == null || bodyRenderers == null || bodyRenderers.Length == 0)
        {
            if (!warnedSetup)
            {
                warnedSetup = true;
                Debug.LogWarning("[PlayerShieldFilm] Film Material o Body Renderers non assegnati sul Player prefab: " +
                                 "lo scudo non si vede. Vedi guida Editor di Rev BV-b.");
            }
            return false;
        }

        for (int i = 0; i < bodyRenderers.Length; i++)
        {
            Renderer body = bodyRenderers[i];
            if (body == null) continue;

            Material[] original = body.sharedMaterials;
            Material[] withFilm = new Material[original.Length + 1];
            for (int m = 0; m < original.Length; m++)
                withFilm[m] = original[m];
            withFilm[original.Length] = filmMaterial;

            originalMaterials[i] = original;
            filmIndex[i] = original.Length;
            body.sharedMaterials = withFilm;
        }

        filmApplied = true;
        LogV("Film acceso.");
        return true;
    }

    /// <summary>Toglie il film e ripristina gli array originali.</summary>
    private void RemoveFilm()
    {
        if (!filmApplied) return;

        for (int i = 0; i < bodyRenderers.Length; i++)
        {
            Renderer body = bodyRenderers[i];
            if (body != null && filmIndex[i] >= 0)
            {
                body.SetPropertyBlock(null, filmIndex[i]);
                if (originalMaterials[i] != null)
                    body.sharedMaterials = originalMaterials[i];
            }
            originalMaterials[i] = null;
            filmIndex[i] = -1;
        }

        filmApplied = false;
        intensity = 0f;
        LogV("Film spento.");
    }

    /// <summary>_Intensity sul solo indice del film (MaterialPropertyBlock per indice di materiale).</summary>
    private void WriteIntensity(float value)
    {
        for (int i = 0; i < bodyRenderers.Length; i++)
        {
            Renderer body = bodyRenderers[i];
            if (body == null || filmIndex[i] < 0) continue;

            body.GetPropertyBlock(propertyBlock, filmIndex[i]);
            propertyBlock.SetFloat(IntensityId, value);
            body.SetPropertyBlock(propertyBlock, filmIndex[i]);
        }
    }

    // ── HUD del proprietario ───────────────────────────────────────────────────

    private void UpdateStatus(bool shielded)
    {
        if (!shielded)
        {
            SetStatus(string.Empty);
            return;
        }

        PlayerQuartermasterGadgets gadgets = PlayerQuartermasterGadgets.LocalInstance;
        float own = gadgets != null ? gadgets.OwnShieldRemaining : 0f;
        SetStatus(own > 0f ? $"Personal Shield — {Mathf.CeilToInt(own)} s" : "Shield active");
    }

    private void SetStatus(string text)
    {
        if (statusText == null || text == lastStatus) return;
        lastStatus = text;
        statusText.text = text;
    }

    private void LogV(string message)
    {
        if (logVerbose) Debug.Log($"[PlayerShieldFilm/{OwnerClientId}] {message}");
    }
}
