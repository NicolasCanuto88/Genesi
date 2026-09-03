using UnityEngine;
using UnityEngine.UI;
using TMPro;
using SpaceSurvivor.Ship;

/// <summary>
/// Banner UI "MOTORI OFFLINE" — D13, Rev AE, Blocco 3.2.d parte 2.
///
/// HOTFIX Rev AE.hotfix — Bug banner mai visibile:
/// L'implementazione iniziale disattivava il GameObject in Awake e
/// pianificava di riattivarlo in Show() dentro Update(). Ma Update() NON
/// gira sui GameObject disattivi → deadlock: Show() non veniva mai
/// chiamato → il GameObject restava disattivo per sempre.
///
/// CORRETTO ORA:
///   - Il GameObject resta SEMPRE attivo. La visibility è governata
///     esclusivamente da CanvasGroup.alpha (0 = invisibile e non-interactable).
///   - Nessun gameObject.SetActive nel ciclo di vita del controller.
///   - Costo trascurabile: renderizzare 1 Image + 1 TMP text con alpha=0
///     produce draw call skippate dalla pipeline URP.
///
/// COMPORTAMENTO (QE.2-C + QE.3-B confermate):
///   - Polling di PropulsionSystem.IsInEngineFailure ogni frame.
///   - Attivazione: fade-in CanvasGroup.alpha da 0→1 in 0.2s.
///   - Stabile durante avaria: pulse sottile alpha 0.85↔1.0 a ~1Hz.
///   - Disattivazione: fade-out 0.3s a 0.
///   - Label progresso: "MOTORI OFFLINE — Ripristino: NN%" — ratio 0→100%
///     dal PropulsionSystem.EngineFailureRatio.
///
/// DIPENDE DA:
///   - PropulsionSystem.Instance (property IsInEngineFailure, EngineFailureRatio)
///     esposte via NetworkVariable — leggibili da tutti i client.
///   - CanvasGroup sul GameObject radice del prefab (per fade).
///   - TextMeshProUGUI child assegnato in Inspector (label).
///
/// EDITOR SETUP (aggiornato dopo hotfix):
///   1. Prefab "BannerEngineFailure":
///      - Root GameObject + CanvasGroup + questo componente + ATTIVO.
///      - Child Panel/Image (fondo rosso #C61414 alpha 0.75).
///      - Child TextMeshProUGUI (label, colore giallo #FFDD44, font 28).
///   2. Assegnare 'label' allo slot inspector.
///   3. NON disattivare il GameObject root in prefab (era l'errore).
///   4. Istanziare come figlio di ciascun canvas HUD (PilotFlightHUD_Canvas,
///      PilotHUD_Canvas, EngineeringDashboard_Canvas).
///   5. Assegnare il riferimento nei 3 HUD controller.
///
/// In multiplayer ogni client ha la sua istanza del banner (parte del
/// canvas del proprio HUD) e polla la stessa NetworkVariable → sincronizzazione
/// automatica lato UI.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CanvasGroup))]
public class EngineFailureBannerController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CanvasGroup canvasGroup;
    [SerializeField] private TextMeshProUGUI label;

    [Header("Animation")]
    [Tooltip("Durata fade-in in secondi. Rev AE default 0.2s.")]
    [SerializeField] private float fadeInDuration = 0.2f;
    [Tooltip("Durata fade-out in secondi. Rev AE default 0.3s.")]
    [SerializeField] private float fadeOutDuration = 0.3f;
    [Tooltip("Alpha minimo del pulse durante failure attiva. Rev AE default 0.85.")]
    [Range(0.5f, 1f)]
    [SerializeField] private float pulseAlphaMin = 0.85f;
    [Tooltip("Frequenza pulse in Hz. Rev AE default 1Hz.")]
    [SerializeField] private float pulseFrequencyHz = 1.0f;

    [Header("Debug")]
    [SerializeField] private bool logStateChanges = false;

    // ── Runtime state ─────────────────────────────────────────────────────────
    private enum BannerState { Hidden, FadingIn, Showing, FadingOut }
    private BannerState _state = BannerState.Hidden;
    private float _stateStartTime;
    // Alpha catturata all'inizio del FadeOut, per lerp deterministico verso 0.
    private float _fadeOutStartAlpha;

    private void Awake()
    {
        if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;
        canvasGroup.interactable = false;
        // HOTFIX: NON disattivare il GameObject. Update deve continuare a
        // pollare PropulsionSystem.IsInEngineFailure per rilevare l'ingresso
        // in avaria e triggerare lo show.
    }

    private void Update()
    {
        var propulsion = PropulsionSystem.Instance;
        bool shouldShow = propulsion != null && propulsion.IsInEngineFailure;

        if (shouldShow && (_state == BannerState.Hidden || _state == BannerState.FadingOut))
        {
            Show();
        }
        else if (!shouldShow && (_state == BannerState.Showing || _state == BannerState.FadingIn))
        {
            Hide();
        }

        UpdateVisual(propulsion);
    }

    private void Show()
    {
        _state = BannerState.FadingIn;
        _stateStartTime = Time.time;
        if (logStateChanges) Debug.Log("[EngineFailureBanner] Show → FadingIn");
    }

    private void Hide()
    {
        _state = BannerState.FadingOut;
        _stateStartTime = Time.time;
        _fadeOutStartAlpha = canvasGroup.alpha;
        if (logStateChanges) Debug.Log("[EngineFailureBanner] Hide → FadingOut");
    }

    private void UpdateVisual(PropulsionSystem propulsion)
    {
        float now = Time.time;
        float elapsed = now - _stateStartTime;

        switch (_state)
        {
            case BannerState.Hidden:
                // Canvas alpha già a 0. Nulla da fare — solo aspettare Show().
                break;

            case BannerState.FadingIn:
                if (elapsed >= fadeInDuration)
                {
                    canvasGroup.alpha = 1f;
                    _state = BannerState.Showing;
                    _stateStartTime = now;
                    if (logStateChanges) Debug.Log("[EngineFailureBanner] FadingIn → Showing");
                }
                else
                {
                    canvasGroup.alpha = Mathf.Clamp01(elapsed / fadeInDuration);
                }
                UpdateLabel(propulsion);
                break;

            case BannerState.Showing:
                float phase = Mathf.Sin(now * pulseFrequencyHz * 2f * Mathf.PI) * 0.5f + 0.5f;
                canvasGroup.alpha = Mathf.Lerp(pulseAlphaMin, 1f, phase);
                UpdateLabel(propulsion);
                break;

            case BannerState.FadingOut:
                if (elapsed >= fadeOutDuration)
                {
                    canvasGroup.alpha = 0f;
                    _state = BannerState.Hidden;
                    // HOTFIX: NON disattivare il GameObject — Update deve
                    // continuare per rilevare futuri Show().
                    if (logStateChanges) Debug.Log("[EngineFailureBanner] FadingOut → Hidden");
                }
                else
                {
                    // Lerp deterministico da _fadeOutStartAlpha (catturata in Hide)
                    // verso 0. Corretto in caso di re-hide durante FadingIn.
                    float t = Mathf.Clamp01(elapsed / fadeOutDuration);
                    canvasGroup.alpha = Mathf.Lerp(_fadeOutStartAlpha, 0f, t);
                }
                break;
        }
    }

    private void UpdateLabel(PropulsionSystem propulsion)
    {
        if (label == null || propulsion == null) return;

        int percent = Mathf.RoundToInt(propulsion.EngineFailureRatio * 100f);
        label.text = $"MOTORI OFFLINE — Ripristino: {percent}%";
    }
}