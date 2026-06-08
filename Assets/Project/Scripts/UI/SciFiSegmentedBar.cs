using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Barra di progresso sci-fi a segmenti LED.
/// Sostituisce il componente Slider standard nel dashboard energetico.
///
/// Setup prefab:
///   GameObject (SciFiSegmentedBar)
///   └── SegmentsContainer (HorizontalLayoutGroup)
///       └── [N figli Image clonati da segmentPrefab — creati in runtime]
///
/// Milestone: M1B (UI polish Engineering Station)
/// </summary>
public class SciFiSegmentedBar : MonoBehaviour
{
    // ── Configurazione visiva ────────────────────────────────────────────────

    [Header("Layout")]
    [Tooltip("Contenitore con HorizontalLayoutGroup che ospita i segmenti")]
    [SerializeField] private RectTransform segmentsContainer;

    [Tooltip("Prefab di un singolo segmento: Image con LayoutElement")]
    [SerializeField] private GameObject segmentPrefab;

    [Tooltip("Numero totale di segmenti")]
    [SerializeField][Range(5, 40)] private int segmentCount = 20;

    [Header("Colori per stato")]
    [Tooltip("Colore normale (es. ciano per generazione, verde per riserva)")]
    [SerializeField] private Color colorNormal = new Color(0f, 0.9f, 1f, 1f); // ciano
    [SerializeField] private Color colorWarning = new Color(1f, 0.67f, 0f, 1f); // arancio
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f, 1f); // rosso
    [SerializeField] private Color colorOff = new Color(1f, 1f, 1f, 0.07f);

    [Header("Soglie (0–1) — mirror di PowerManager")]
    [Tooltip("Sotto questa soglia il colore diventa rosso (corrisponde a blackout 5%)")]
    [SerializeField][Range(0f, 1f)] private float thresholdCritical = 0.05f;

    [Tooltip("Sotto questa soglia il colore diventa arancio (corrisponde a critical 25%)")]
    [SerializeField][Range(0f, 1f)] private float thresholdWarning = 0.25f;

    [Header("Marker divisore")]
    [Tooltip("Aggiunge un marker visivo a questa percentuale (0 = nessun marker)")]
    [SerializeField][Range(0f, 1f)] private float markerAt = 0.75f;

    [SerializeField] private Color markerColor = new Color(1f, 1f, 1f, 0.45f);

    // ── Stato interno ────────────────────────────────────────────────────────

    private Image[] _segments;
    private float _currentValue; // 0–1
    private bool _initialized;

    // ── API pubblica ─────────────────────────────────────────────────────────

    /// <summary>Valore corrente della barra, range 0–1.</summary>
    public float Value
    {
        get => _currentValue;
        set => SetValue(value);
    }

    /// <summary>
    /// Imposta il valore e ridisegna i segmenti.
    /// Equivalente a slider.value = x nel vecchio codice.
    /// </summary>
    public void SetValue(float normalizedValue)
    {
        _currentValue = Mathf.Clamp01(normalizedValue);
        if (_initialized) Redraw();
    }

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        Build();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Permette di vedere le variazioni in editor senza entrare in Play Mode
        if (Application.isPlaying && _initialized) Redraw();
    }
#endif

    // ── Costruzione segmenti ─────────────────────────────────────────────────

    private void Build()
    {
        if (segmentsContainer == null || segmentPrefab == null)
        {
            Debug.LogError($"[SciFiSegmentedBar] {name}: segmentsContainer o segmentPrefab non assegnati.", this);
            return;
        }

        // Pulizia eventuali figli pre-esistenti (utile in editor)
        foreach (Transform child in segmentsContainer)
            Destroy(child.gameObject);

        _segments = new Image[segmentCount];

        int markerIndex = markerAt > 0f ? Mathf.RoundToInt(markerAt * segmentCount) - 1 : -1;

        for (int i = 0; i < segmentCount; i++)
        {
            GameObject go = Instantiate(segmentPrefab, segmentsContainer);
            go.name = $"Seg_{i:D2}";

            Image img = go.GetComponent<Image>();
            if (img == null)
            {
                Debug.LogError($"[SciFiSegmentedBar] Il segmentPrefab deve avere un componente Image.", this);
                return;
            }

            // Marker divisore: istanzia lo stesso segmentPrefab con colore e larghezza dedicati
            if (i == markerIndex)
            {
                GameObject marker = Instantiate(segmentPrefab, segmentsContainer);
                marker.name = "Marker";
                marker.transform.SetSiblingIndex(i + 1); // subito dopo questo segmento

                // Sovrascrive LayoutElement per larghezza fissa
                LayoutElement mle = marker.GetComponent<LayoutElement>();
                if (mle == null) mle = marker.AddComponent<LayoutElement>();
                mle.minWidth = 4f;
                mle.preferredWidth = 4f;
                mle.flexibleWidth = 0f;

                Image markerImg = marker.GetComponent<Image>();
                if (markerImg != null)
                {
                    markerImg.color = markerColor;
                    markerImg.raycastTarget = false;
                }
            }

            img.color = colorOff;
            _segments[i] = img;
        }

        _initialized = true;
        Redraw();
    }

    // ── Ridisegno ────────────────────────────────────────────────────────────

    private void Redraw()
    {
        if (_segments == null) return;

        int filledCount = Mathf.RoundToInt(_currentValue * segmentCount);
        Color activeColor = GetActiveColor(_currentValue);

        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i] == null) continue;
            _segments[i].color = (i < filledCount) ? activeColor : colorOff;
        }
    }

    private Color GetActiveColor(float value)
    {
        if (value <= thresholdCritical) return colorCritical;
        if (value <= thresholdWarning) return colorWarning;
        return colorNormal;
    }
}