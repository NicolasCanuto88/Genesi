using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// IconGateBoard — thread Icone UI, lotto pilota (Q10-a). Strumento di sviluppo.
///
/// Tavola di prova per il gate visivo: dispone le icone a più dimensioni
/// bersaglio (pixel schermo a 1080p) dentro un canvas VERO — HUD Pilota
/// (Overlay), monitor di plancia, monitor medico — così il giudizio avviene con
/// la camera reale della postazione, con luci e post-processing veri.
///
/// USO: GameObject vuoto (RectTransform) figlio del canvas da provare →
/// Add Component → IconGateBoard → menu contestuale "Load icons from
/// Art/UI/Icons" → scegliere il preset del canvas. Non salvare la scena: i
/// figli generati sono DontSave (stesso schema di SciFiCardFrame), il
/// componente stesso va rimosso a fine gate.
///
/// DIMENSIONI: unità canvas = px bersaglio / pxPerUnit. I pxPerUnit dei preset
/// sono stime dell'audit (FOV 60°, occhi a 1,6 m sopra lo snap point, 1080p):
/// il gate serve anche a verificarle.
///
/// TAGLI (Q5-b): sotto smallCutMaxPx si usa il taglio S se presente.
/// Colori (Q3-a): gli sprite sono bianchi, la tinta arriva da Image.color.
/// Le tinte di default ricalcano la scala semantica serializzata in scena
/// (Q4-a) e RoleColors per i ruoli.
///
/// Attivo solo in Editor e Development Build.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public class IconGateBoard : MonoBehaviour
{
    public enum CanvasPreset
    {
        Overlay,        // HUD Screen Space, 1920×1080, match 0.5 → 1 u = 1 px a 1080p
        BridgeMonitor,  // monitor Ingegneria/Scanner: 0,69 mm/u a 1,24 m → ≈ 0,52 px/u
        PilotMonitor,   // monitor Pilota (aggancio): 0,69 mm/u a 0,88 m → ≈ 0,74 px/u
        MedicalMonitor, // monitor medico: 2,88 mm/u a 1,58 m → ≈ 1,7 px/u
        Custom
    }

    [Serializable]
    public class Entry
    {
        public string label;
        [Tooltip("Taglio M (o segno radar).")]
        public Sprite main;
        [Tooltip("Taglio S, opzionale (suffisso _s).")]
        public Sprite small;
        public Color tint = Color.white;
    }

    [Header("Icone")]
    [SerializeField] private List<Entry> entries = new List<Entry>();

    [Header("Canvas")]
    [SerializeField] private CanvasPreset preset = CanvasPreset.Overlay;
    [Tooltip("Usato solo con il preset Custom.")]
    [SerializeField] private float customPxPerUnit = 1f;

    [Header("Dimensioni bersaglio (px schermo a 1080p)")]
    [SerializeField] private float[] targetPx = { 12f, 16f, 20f, 24f, 32f, 48f };
    [Tooltip("Fino a questa dimensione si usa il taglio S, se presente (Q5-b).")]
    [SerializeField] private float smallCutMaxPx = 16f;

    [Header("Impaginazione (px schermo)")]
    [SerializeField] private float gapPx = 10f;
    [SerializeField] private float labelColumnPx = 150f;
    [SerializeField] private float labelFontPx = 13f;
    [SerializeField] private TMP_FontAsset labelFont;
    [SerializeField] private Color labelColor = new Color(0.8f, 0.8f, 0.8f, 1f);
    [SerializeField] private bool drawBackdrop = true;
    [SerializeField] private Color backdropColor = new Color(0.020f, 0.024f, 0.039f, 0.92f); // #05060A

    private const string RootName = "__IconGateBoard";
    private RectTransform _root;

    // Tinte di default del gate (ricalcano i valori serializzati in Game.unity).
    private static readonly Color TintInfo = new Color(0.000f, 0.784f, 0.937f); // #00C8EF
    private static readonly Color TintWarning = new Color(1.000f, 0.671f, 0.000f); // #FFAB00
    private static readonly Color TintCritical = new Color(1.000f, 0.200f, 0.000f); // #FF3300
    private static readonly Color TintRadar = new Color(0.200f, 1.000f, 0.400f); // #33FF66

    private float PxPerUnit
    {
        get
        {
            switch (preset)
            {
                case CanvasPreset.Overlay: return 1f;
                case CanvasPreset.BridgeMonitor: return 0.52f;
                case CanvasPreset.PilotMonitor: return 0.74f;
                case CanvasPreset.MedicalMonitor: return 1.7f;
                default: return Mathf.Max(0.01f, customPxPerUnit);
            }
        }
    }

    private void OnEnable() { Rebuild(); }

    private void OnDisable()
    {
        if (_root != null) DestroyBoard();
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

    [ContextMenu("Load icons from Art/UI/Icons")]
    private void LoadIconsFromFolder()
    {
        var mains = new SortedDictionary<string, Sprite>();
        var smalls = new Dictionary<string, Sprite>();
        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Sprite", new[] { "Assets/Project/Art/UI/Icons" });
        foreach (string guid in guids)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) continue;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith("_s")) smalls[name.Substring(0, name.Length - 2)] = sprite;
            else mains[name] = sprite;
        }

        UnityEditor.Undo.RecordObject(this, "Load icons");
        entries.Clear();
        foreach (var kv in mains)
        {
            smalls.TryGetValue(kv.Key, out Sprite small);
            entries.Add(new Entry
            {
                label = kv.Key.StartsWith("ico_") ? kv.Key.Substring(4) : kv.Key,
                main = kv.Value,
                small = small,
                tint = DefaultTint(kv.Key)
            });
        }
        UnityEditor.EditorUtility.SetDirty(this);
        Rebuild();
    }
#endif

    /// <summary>Tinta di default dal nome file (ico_famiglia_nome).</summary>
    private static Color DefaultTint(string fileName)
    {
        string n = fileName.StartsWith("ico_") ? fileName.Substring(4) : fileName;
        if (n.StartsWith("alert_critical")) return TintCritical;
        if (n.StartsWith("alert_warning")) return TintWarning;
        if (n.StartsWith("radar_")) return TintRadar;
        if (n.StartsWith("role_")
            && Enum.TryParse(n.Substring(5), true, out CrewRole role)
            && role != CrewRole.None)
            return RoleColors.Get(role);
        return TintInfo;
    }

    private void DestroyBoard()
    {
        // Rimuove eventuali tavole esistenti (anche duplicati da ricompilazioni).
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            if (ch != null && ch.name == RootName)
            {
                if (Application.isPlaying) Destroy(ch.gameObject);
                else DestroyImmediate(ch.gameObject);
            }
        }
        _root = null;
    }

    private void Rebuild()
    {
        DestroyBoard();
        // Solo Editor e Development Build (isDebugBuild è true in entrambi).
        // Guardia a runtime invece di #if: evita i warning CS0414 sui campi
        // serializzati nelle build di release.
        if (!Debug.isDebugBuild) return;
        if (targetPx == null || targetPx.Length == 0) return;

        float ppu = PxPerUnit;
        float gap = gapPx / ppu;
        float labelW = labelColumnPx / ppu;
        float fontU = labelFontPx / ppu;
        float headerH = fontU * 2f;

        // Larghezze di colonna: ogni colonna è larga quanto la sua dimensione.
        float width = gap + labelW;
        float maxCell = 0f;
        foreach (float px in targetPx)
        {
            float u = px / ppu;
            width += u + gap;
            maxCell = Mathf.Max(maxCell, u);
        }
        float rowH = maxCell + gap;
        float height = gap + headerH + entries.Count * rowH + gap;

        _root = NewRect(RootName, transform);
        _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0f, 1f);
        _root.anchoredPosition = Vector2.zero;
        _root.sizeDelta = new Vector2(width, height);
        // Se il genitore ha un LayoutGroup, non deve comprimere la tavola.
        _root.gameObject.AddComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;

        if (drawBackdrop)
        {
            var bg = _root.gameObject.AddComponent<UnityEngine.UI.Image>();
            bg.color = backdropColor;
            bg.raycastTarget = false;
        }

        // Intestazione: dimensione bersaglio di ogni colonna.
        float x = gap + labelW;
        foreach (float px in targetPx)
        {
            float u = px / ppu;
            NewLabel($"{px:0} px", new Vector2(x, -gap), new Vector2(u + gap, headerH), fontU,
                     TextAlignmentOptions.BottomLeft);
            x += u + gap;
        }

        // Righe: etichetta + icona a ogni dimensione.
        float y = -(gap + headerH);
        foreach (var e in entries)
        {
            if (e == null) continue;
            float rowTop = y;
            NewLabel(e.label, new Vector2(gap, rowTop), new Vector2(labelW, rowH), fontU,
                     TextAlignmentOptions.MidlineLeft);

            x = gap + labelW;
            foreach (float px in targetPx)
            {
                float u = px / ppu;
                bool useSmall = px <= smallCutMaxPx && e.small != null;
                Sprite s = useSmall ? e.small : e.main;
                if (s != null)
                {
                    // Posizione arrotondata al pixel schermo: sull'Overlay evita
                    // che un'icona ritoccata al pixel finisca su mezzi pixel.
                    float cy = rowTop - (rowH - u) * 0.5f;
                    var pos = new Vector2(Snap(x, ppu), Snap(cy, ppu));
                    NewIcon($"{e.label}_{px:0}{(useSmall ? "_s" : "")}", s, e.tint, pos, u);
                }
                x += u + gap;
            }
            y -= rowH;
        }
    }

    private static float Snap(float units, float ppu) => Mathf.Round(units * ppu) / ppu;

    private void NewIcon(string name, Sprite sprite, Color tint, Vector2 topLeft, float size)
    {
        var rt = NewRect(name, _root);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = topLeft;
        rt.sizeDelta = new Vector2(size, size);
        var img = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
        img.sprite = sprite;
        img.color = tint;
        img.preserveAspect = true;
        img.raycastTarget = false;
    }

    private void NewLabel(string text, Vector2 topLeft, Vector2 size, float fontSize, TextAlignmentOptions align)
    {
        var rt = NewRect("Label", _root);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = topLeft;
        rt.sizeDelta = size;
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (labelFont != null) t.font = labelFont;
        t.text = text;
        t.fontSize = fontSize;
        t.color = labelColor;
        t.alignment = align;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Overflow;
        t.raycastTarget = false;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }
}
