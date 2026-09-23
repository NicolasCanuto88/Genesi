using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpaceSurvivor.UI
{
    /// <summary>
    /// RadarBlip — Rev BJ (Radar Scanner ping-sonar).
    ///
    /// Vista di un singolo contatto sul radar dello Scanner: un quadratino
    /// posizionato in coordinate schermo (piano radar heading-up), con etichetta
    /// altezza (offset Y logico relativo alla nave, con segno) e freccia rotta
    /// opzionale (mostrata a RevealedInfoTier ≥ 2 se il POI è in moto).
    ///
    /// È un PURO elemento di presentazione: nessuna logica di rete, nessun
    /// riferimento a PoiInstance. Lo <see cref="ScannerRadarUI"/> lo posiziona e
    /// lo configura al passaggio dello sweep, poi ne guida il fade via SetAlpha().
    ///
    /// SETUP EDITOR: prefab con un Image (quadratino), un TMP_Text (etichetta) e
    /// facoltativamente un RectTransform freccia con Image. Vedi SETUP_Editor_RevBJ.md.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class RadarBlip : MonoBehaviour
    {
        [Tooltip("Quadratino del contatto.")]
        [SerializeField] private Image square;

        [Tooltip("Etichetta altezza (offset Y logico relativo alla nave, con segno). " +
                 "A RevealedInfoTier ≥ 2 può includere un codice tipo.")]
        [SerializeField] private TMP_Text label;

        [Tooltip("Freccia rotta (mostrata solo a RevealedInfoTier ≥ 2 e se il POI è " +
                 "in moto). Ruotata sul piano radar. Opzionale: se null viene ignorata.")]
        [SerializeField] private RectTransform arrow;

        [Tooltip("CanvasGroup per il fade. Se assente viene aggiunto a runtime.")]
        [SerializeField] private CanvasGroup canvasGroup;

        [Tooltip("Indicatore di AGGANCIO (Rev BK): GameObject opzionale (es. un " +
                 "anello attorno al quadratino) acceso quando il blip è il target " +
                 "locked. Se null, lo stato locked resta segnalato dal solo colore " +
                 "(lockedColor) impostato da ScannerRadarUI — degradazione grazioso.")]
        [SerializeField] private GameObject lockIndicator;

        private RectTransform _rt;
        private Image _arrowImage;

        /// <summary>RectTransform del blip (cache).</summary>
        public RectTransform Rt
        {
            get
            {
                if (_rt == null) _rt = (RectTransform)transform;
                return _rt;
            }
        }

        private void Awake()
        {
            _rt = (RectTransform)transform;

            if (canvasGroup == null)
            {
                canvasGroup = GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                    canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }

            if (arrow != null)
                _arrowImage = arrow.GetComponent<Image>();

            if (lockIndicator != null)
                lockIndicator.SetActive(false);
        }

        /// <summary>Posiziona il blip (coordinate anchored, piano radar heading-up).</summary>
        public void SetAnchoredPosition(Vector2 pos) => Rt.anchoredPosition = pos;

        /// <summary>
        /// Configura contenuto e stile del blip.
        /// <paramref name="heightLabel"/> è già formattato (es. "+40");
        /// <paramref name="typeCode"/> vuoto = nessun codice tipo mostrato.
        /// <paramref name="showArrow"/> + <paramref name="arrowAngleDeg"/> governano
        /// la freccia rotta (angolo Z locale, 0 = freccia verso l'alto/prua).
        /// </summary>
        public void Configure(Color color, string heightLabel, string typeCode,
                              bool showArrow, float arrowAngleDeg)
        {
            if (square != null) square.color = color;

            if (label != null)
            {
                label.color = color;
                label.text = string.IsNullOrEmpty(typeCode)
                    ? heightLabel
                    : $"{typeCode} {heightLabel}";
            }

            if (arrow != null)
            {
                arrow.gameObject.SetActive(showArrow);
                if (showArrow)
                {
                    arrow.localRotation = Quaternion.Euler(0f, 0f, arrowAngleDeg);
                    if (_arrowImage != null) _arrowImage.color = color;
                }
            }
        }

        /// <summary>Alpha 0..1 per il fade (guidato dallo ScannerRadarUI).</summary>
        public void SetAlpha(float a)
        {
            if (canvasGroup != null) canvasGroup.alpha = Mathf.Clamp01(a);
        }

        /// <summary>
        /// [Rev BK] Accende/spegne lo stile "agganciato" (lockIndicator opzionale).
        /// Il colore distinto è comunque applicato via Configure da ScannerRadarUI,
        /// quindi lo stato locked è visibile anche senza lockIndicator assegnato.
        /// </summary>
        public void SetLocked(bool locked)
        {
            if (lockIndicator != null) lockIndicator.SetActive(locked);
        }
    }
}