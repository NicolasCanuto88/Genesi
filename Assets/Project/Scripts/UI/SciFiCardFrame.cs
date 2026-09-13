using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// SciFiCardFrame — cornice sci-fi con parentesi angolari a L, identica al
/// motivo delle card del menu principale (barre ciano #00C8EF ai quattro
/// angoli). Si mette su un RectTransform (un pannello/card): genera da solo
/// le 4 parentesi come figli non-serializzati, quindi il prefab/scena resta
/// pulito e la cornice è coerente ovunque senza wiring manuale.
///
/// Pensato per allineare gli schermi di bordo (dashboard ingegnere, ecc.)
/// allo stile del menu. Il colore/lunghezza/spessore/inset sono regolabili
/// nell'Inspector. Con [ExecuteAlways] la cornice è visibile anche in edit
/// mode; i figli generati hanno hideFlags DontSave per non finire serializzati
/// (vengono ricreati a ogni caricamento, come un gizmo).
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public class SciFiCardFrame : MonoBehaviour
{
    [Header("Aspetto")]
    [Tooltip("Colore delle parentesi (default: ciano UI del progetto #00C8EF).")]
    [SerializeField] private Color accentColor = new Color(0f, 0.784f, 0.937f, 1f);
    [Tooltip("Lunghezza dei due bracci di ogni parentesi (px).")]
    [SerializeField] private float armLength = 14f;
    [Tooltip("Spessore dei bracci (px).")]
    [SerializeField] private float thickness = 2f;
    [Tooltip("Distanza delle parentesi dal bordo del pannello (px, positivo = verso l'interno).")]
    [SerializeField] private float inset = 0f;

    [Header("Angoli")]
    [SerializeField] private bool topLeft = true;
    [SerializeField] private bool topRight = true;
    [SerializeField] private bool bottomLeft = true;
    [SerializeField] private bool bottomRight = true;

    private const string RootName = "__SciFiFrame";
    private RectTransform _frame;

    private void OnEnable() { Rebuild(); }

    private void OnDisable()
    {
        if (_frame != null) DestroyFrame();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!isActiveAndEnabled) return;
        // Rimanda: OnValidate può scattare durante import/serializzazione.
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this != null && isActiveAndEnabled) Rebuild();
        };
    }
#endif

    private void DestroyFrame()
    {
        // Rimuove eventuali frame esistenti (anche duplicati da ricompilazioni).
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            if (ch != null && ch.name == RootName)
            {
                if (Application.isPlaying) Destroy(ch.gameObject);
                else DestroyImmediate(ch.gameObject);
            }
        }
        _frame = null;
    }

    private void Rebuild()
    {
        DestroyFrame();

        _frame = NewRect(RootName, transform);
        Stretch(_frame);
        _frame.gameObject.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;

        // I pannelli usano spesso un LayoutGroup (Vertical/Horizontal) che
        // altrimenti comprimerebbe questo frame ad altezza 0: ignoreLayout fa
        // sì che il LayoutGroup lo salti e lasci valere lo stretch sugli anchor,
        // così la cornice riempie l'intero pannello e le parentesi finiscono
        // agli angoli reali.
        var le = _frame.gameObject.AddComponent<LayoutElement>();
        le.ignoreLayout = true;

        if (topLeft) BuildCorner(new Vector2(0f, 1f), +1f, -1f);
        if (topRight) BuildCorner(new Vector2(1f, 1f), -1f, -1f);
        if (bottomLeft) BuildCorner(new Vector2(0f, 0f), +1f, +1f);
        if (bottomRight) BuildCorner(new Vector2(1f, 0f), -1f, +1f);
    }

    /// <summary>Costruisce una parentesi a L su un angolo. dx/dy = direzione verso l'interno.</summary>
    private void BuildCorner(Vector2 anchor, float dx, float dy)
    {
        // Braccio orizzontale
        var h = NewImage("H", anchor);
        var hrt = h.rectTransform;
        hrt.pivot = new Vector2(dx > 0 ? 0f : 1f, dy > 0 ? 0f : 1f);
        hrt.sizeDelta = new Vector2(armLength, thickness);
        hrt.anchoredPosition = new Vector2(dx * inset, dy * inset);

        // Braccio verticale
        var v = NewImage("V", anchor);
        var vrt = v.rectTransform;
        vrt.pivot = new Vector2(dx > 0 ? 0f : 1f, dy > 0 ? 0f : 1f);
        vrt.sizeDelta = new Vector2(thickness, armLength);
        vrt.anchoredPosition = new Vector2(dx * inset, dy * inset);
    }

    private Image NewImage(string name, Vector2 anchor)
    {
        var rt = NewRect(name, _frame);
        rt.anchorMin = rt.anchorMax = anchor;
        var img = rt.gameObject.AddComponent<Image>();
        img.raycastTarget = false;
        img.color = accentColor;
        return img;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}
