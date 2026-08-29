using SpaceSurvivor.Ship;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SpaceSurvivor.UI
{
    /// <summary>
    /// DockingMinigameUI — Milestone 3 Fase 3 Blocco 3.1 Sotto-step 3.1.5.
    /// MonoBehaviour rendering-only (NON NetworkBehaviour): legge le NetVar
    /// server-authoritative del DockingController e le trasforma in feedback
    /// visivo per il pilota durante il minigioco di attracco.
    ///
    /// RESPONSABILITÀ:
    ///   - Rendere il cerchio dinamico (posizione + scala + colore) sulla base
    ///     di LateralOffset (Vector2) e AxialDistance (float) del DockingController.
    ///   - Rendere overlay testuali: velocità corrente della nave, distanza al POI.
    ///   - Show/hide del prompt "Premi Space per ATTRACCARE" in base a
    ///     IsInAnchorTolerance.
    ///
    /// ATTIVAZIONE:
    ///   Il GameObject di questo componente viene attivato/disattivato da
    ///   PilotStation durante le transizioni di NavigationState. Quando il
    ///   GameObject è disattivato, Update non gira → zero costo runtime.
    ///
    /// CONVENZIONE B (dimensione cerchio in funzione di AxialDistance):
    ///   - axial = InitialAxialDistance (ingresso Docking): scala = circleScaleMin (piccolo)
    ///   - axial = FinalDockingDistance (target ideale): scala = circleScaleMax (combacia)
    ///   - axial < FinalDockingDistance (troppo vicino): scala > max (esce dai bordi)
    ///
    /// COLORAZIONE CERCHIO:
    ///   - Grigio: fuori tolleranza normale
    ///   - Giallo: near miss (lateralError e axial entrambi entro nearToleranceFactor × tol)
    ///   - Verde: IsInAnchorTolerance = true, prompt Space visibile
    ///
    /// MOVIMENTO 2D:
    ///   pixel = LateralOffset * (canvasHalfSizePx / MaxDockingLateralRange).
    ///   Se |LateralOffset| >= MaxDockingLateralRange, il cerchio esce dai bordi
    ///   → segnale visivo di uscita forzata imminente.
    ///
    /// DIPENDE DA: DockingController (Instance, NetVar + property tuning),
    ///             PropulsionSystem (Instance, CurrentSpeed), UnityEngine.UI, TMPro.
    ///
    /// dipende da setup Editor: GameObject figlio del "Monitor" (fratello del
    ///   PilotHUD_Canvas), Canvas World Space stesse dimensioni fisiche del
    ///   PilotHUD_Canvas per allineamento visivo (SizeDelta 1920×1080,
    ///   LocalScale ~1.28, LocalEulerAngles X=-20 per matching inclinazione).
    ///   Struttura figli: Cornice (Image fissa), Cerchio (Image dinamica),
    ///   SpeedText/DistanceText (TMP), ConfirmPrompt (GameObject con TMP dentro).
    /// </summary>
    public class DockingMinigameUI : MonoBehaviour
    {
        // ── Riferimenti UI (assegnare in Inspector) ──────────────────────────
        [Header("Riferimenti UI")]
        [Tooltip("RectTransform del cerchio dinamico. Pivot centrato (0.5, 0.5) " +
                 "per scalatura simmetrica.")]
        [SerializeField] private RectTransform circleRect;

        [Tooltip("Componente Image del cerchio dinamico — cambio colore in base " +
                 "allo stato. Tipicamente stesso GameObject di circleRect.")]
        [SerializeField] private Image circleImage;

        [Tooltip("Testo velocità corrente nave (u/s). TMP. Vuoto = non renderizzato.")]
        [SerializeField] private TextMeshProUGUI speedText;

        [Tooltip("Testo distanza corrente al POI (u). TMP. Vuoto = non renderizzato.")]
        [SerializeField] private TextMeshProUGUI distanceText;

        [Tooltip("GameObject del prompt \"Premi Space\", attivato quando " +
                 "IsInAnchorTolerance = true. Vuoto = prompt non gestito.")]
        [SerializeField] private GameObject confirmPromptGO;

        // ── Tuning rendering ─────────────────────────────────────────────────
        [Header("Rendering — mappatura LateralOffset → pixel")]
        [Tooltip("Metà lato del canvas in pixel (area di movimento del cerchio). " +
                 "Il cerchio raggiunge questa distanza dal centro quando " +
                 "|LateralOffset| == MaxDockingLateralRange. Default 400 " +
                 "(assume canvas 1920×1080 con box di movimento ~800px).")]
        [Min(1f)]
        [SerializeField] private float canvasHalfSizePx = 400f;

        [Header("Rendering — scala cerchio (Convenzione B)")]
        [Tooltip("Scala minima (all'ingresso Docking, cerchio piccolo). Default 0.1.")]
        [Range(0.01f, 1f)]
        [SerializeField] private float circleScaleMin = 0.1f;

        [Tooltip("Scala massima (target ideale, cerchio combacia cornice). Default 1.0. " +
                 "Oltre il target, la scala extrapola sopra questo valore.")]
        [Range(0.5f, 3f)]
        [SerializeField] private float circleScaleMax = 1.0f;

        [Header("Colori")]
        [SerializeField] private Color colorNormal = new Color(0.7f, 0.7f, 0.7f, 0.8f);
        [SerializeField] private Color colorNearTolerance = new Color(1f, 0.85f, 0.2f, 0.9f);
        [SerializeField] private Color colorInTolerance = new Color(0.3f, 1f, 0.4f, 1f);
        [Tooltip("Colore quando axial < finalDockingDistance — la nave ha " +
                 "superato il punto ottimale di attracco. Warning 'retrocedi'.")]
        [SerializeField] private Color colorOvershoot = new Color(1f, 0.3f, 0.3f, 1f);

        [Header("Soglie stato \"near tolerance\"")]
        [Tooltip("Fattore per lo stato giallo: near-miss se lateralError e " +
                 "|axial - final| entrambi entro nearToleranceFactor × tolleranze. " +
                 "Default 2.0.")]
        [Min(1.01f)]
        [SerializeField] private float nearToleranceFactor = 2.0f;

        // =========================================================================
        // UPDATE — rendering (client-side, ogni frame quando attivo)
        // =========================================================================

        private void Update()
        {
            var dc = DockingController.Instance;
            var ps = PropulsionSystem.Instance;

            if (dc == null || ps == null) return;

            // Doppio-check: race condition tra activation e state change.
            if (ps.CurrentNavState != NavigationState.Docking) return;

            UpdateCirclePosition(dc);
            UpdateCircleScale(dc);
            UpdateCircleColor(dc);
            UpdateTextOverlays(dc, ps);
            UpdateConfirmPrompt(dc);
        }

        // =========================================================================
        // RENDERING HELPERS
        // =========================================================================

        private void UpdateCirclePosition(DockingController dc)
        {
            if (circleRect == null) return;

            Vector2 offset = dc.LateralOffset;
            float maxRange = dc.MaxDockingLateralRange;
            if (maxRange < 1e-3f) maxRange = 1f;

            Vector2 pixel = offset * (canvasHalfSizePx / maxRange);
            circleRect.anchoredPosition = pixel;
        }

        private void UpdateCircleScale(DockingController dc)
        {
            if (circleRect == null) return;

            float axial = dc.AxialDistance;
            float finalDist = dc.FinalDockingDistance;
            float radiusRef = dc.DockingRadiusReference;

            // Formula stabile: la scala del cerchio è funzione dell'AxialDistance
            // rispetto al DockingRadius del POI (default 200m), NON rispetto al
            // punto di ingresso (che varia).
            //   axial >= radiusRef  → scale = min (cerchio piccolo)
            //   axial == finalDist  → scale = max (cerchio combacia con cornice)
            //   axial <  finalDist  → scale clampata a max (NON extrapola oltre)
            // Il warning "sei troppo vicino" NON è dato dall'uscita del cerchio dai
            // bordi (che sarebbe brutta), ma dal colore (rosso in overshoot — vedi
            // UpdateCircleColor).
            float denom = radiusRef - finalDist;
            float t = denom > 1e-3f
                ? Mathf.Clamp01(1f - (axial - finalDist) / denom)
                : 1f;

            float scale = Mathf.Lerp(circleScaleMin, circleScaleMax, t);
            circleRect.localScale = new Vector3(scale, scale, 1f);
        }

        private void UpdateCircleColor(DockingController dc)
        {
            if (circleImage == null) return;

            if (dc.IsInAnchorTolerance)
            {
                circleImage.color = colorInTolerance;
                return;
            }

            // Overshoot: la nave ha superato la distanza ideale (axial < finalDist).
            // Colore rosso "retrocedi" — priorità sul near-tolerance yellow.
            if (dc.AxialDistance < dc.FinalDockingDistance)
            {
                circleImage.color = colorOvershoot;
                return;
            }

            float latErr = dc.LateralError;
            float axial = dc.AxialDistance;
            float latTol = dc.LateralTolerance;
            float axTol = dc.AxialDockingTolerance;
            float finalDist = dc.FinalDockingDistance;

            bool nearLateral = latErr <= latTol * nearToleranceFactor;
            bool nearAxial = Mathf.Abs(axial - finalDist) <= axTol * nearToleranceFactor;

            circleImage.color = (nearLateral && nearAxial) ? colorNearTolerance : colorNormal;
        }

        private void UpdateTextOverlays(DockingController dc, PropulsionSystem ps)
        {
            // Blocco 3.2.b.3 — Velocità RCS letta dal DockingController
            // (magnitude di _strafeVelocity, replicata via _netCurrentRcsSpeed).
            // PRIMA (Rev Y): leggeva ps.CurrentSpeed, che durante il Docking
            // è sempre 0 by design (PropulsionSystem.cs commento riga 41-56).
            // Il parametro ps resta in firma per possibili consumer futuri
            // (es. mostrare velocità cruise durante approccio pre-Docking).
            if (speedText != null)
                speedText.text = $"VELOCITÀ: {dc.CurrentRcsSpeed:F1} u/s";

            if (distanceText != null)
                distanceText.text = $"DISTANZA: {dc.AxialDistance:F0} u";
        }

        private void UpdateConfirmPrompt(DockingController dc)
        {
            if (confirmPromptGO == null) return;

            bool shouldShow = dc.IsInAnchorTolerance;
            if (confirmPromptGO.activeSelf != shouldShow)
                confirmPromptGO.SetActive(shouldShow);
        }
    }
}