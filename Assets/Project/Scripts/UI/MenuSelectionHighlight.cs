using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// MenuSelectionHighlight — Milestone 3 · navigazione controller.
///
/// Disegna UN solo indicatore di focus (frame sci-fi: fill traslucido +
/// bordo accent + barra accent sinistra) che scivola sull'elemento
/// attualmente selezionato dall'EventSystem. Funziona per qualunque
/// <see cref="Selectable"/> in QUALSIASI pannello del menu senza alcun
/// wiring per-bottone: legge <c>EventSystem.current.currentSelectedGameObject</c>
/// e si riposiziona da solo.
///
/// MOTIVO: i bottoni del menu hanno feedback di selezione assente o quasi
/// invisibile (NewGame ha transition=None; gli altri hanno selectedColor ≈
/// normalColor). Col controller serve un indicatore inequivocabile di "cosa
/// è selezionato adesso". Questo componente lo fornisce in modo uniforme e
/// indipendente dallo stile dei singoli bottoni.
///
/// Va messo sul Canvas del menu (MainMenuCanvas). Costruisce da solo la
/// propria gerarchia grafica a runtime — nessun prefab/sprite richiesto.
/// </summary>
[DisallowMultipleComponent]
public class MenuSelectionHighlight : MonoBehaviour
{
    [Header("Aspetto")]
    [Tooltip("Colore accent del bordo e della barra sinistra (default: ciano UI del progetto).")]
    [SerializeField] private Color accentColor = new Color(0f, 0.784f, 0.937f, 1f); // #00C8EF
    [Tooltip("Alpha del riempimento traslucido dietro l'elemento selezionato.")]
    [Range(0f, 1f)]
    [SerializeField] private float fillAlpha = 0.10f;
    [Tooltip("Spessore del bordo (px).")]
    [SerializeField] private float borderThickness = 2f;
    [Tooltip("Larghezza della barra accent sinistra (px).")]
    [SerializeField] private float leftBarWidth = 4f;
    [Tooltip("Margine attorno all'elemento selezionato (px).")]
    [SerializeField] private float padding = 6f;

    [Header("Movimento")]
    [Tooltip("Velocità di scivolamento verso il nuovo target. Alto = più immediato.")]
    [SerializeField] private float followSpeed = 18f;
    [Tooltip("Pulsazione dell'alpha del bordo per far 'respirare' il focus.")]
    [SerializeField] private bool pulse = true;
    [SerializeField] private float pulseSpeed = 3.5f;

    private RectTransform _root;      // contenitore che si muove/ridimensiona
    private RectTransform _fill;
    private Image _fillImg;
    private Image[] _edges;           // 0=top 1=bottom 2=left 3=right
    private Image _leftBar;

    private RectTransform _canvasRect;
    private GameObject _current;
    private Vector2 _targetPos, _targetSize;
    private bool _hasTarget;

    // Il frame ha senso solo con controller/tastiera: col mouse basta il
    // feedback hover (accent bar). Mostriamo il frame solo quando l'ultimo
    // input è stato di navigazione (stick/dpad/frecce), e lo nascondiamo appena
    // si muove/clicca il mouse.
    private bool _controllerMode;

    /// <summary>
    /// Modalità input corrente del menu, condivisa: true = controller/tastiera,
    /// false = mouse. Letta da <see cref="MenuButtonAccent"/> per accendere
    /// l'accent sull'elemento "selezionato" solo quando si naviga a controller.
    /// </summary>
    public static bool ControllerActive { get; private set; }

    private void Awake()
    {
        _canvasRect = GetComponent<RectTransform>();
        BuildGraphics();
        SetVisible(false);
    }

    private void BuildGraphics()
    {
        _root = NewRect("SelectionHighlight", transform);
        // Sopra i contenuti dei pannelli (fill traslucido + bordo sottile, non
        // copre il testo). Un fill basso lascia leggibile il bottone sottostante.
        _root.SetAsLastSibling();

        _fill = NewRect("Fill", _root);
        Stretch(_fill);
        _fillImg = _fill.gameObject.AddComponent<Image>();
        _fillImg.raycastTarget = false;
        _fillImg.color = new Color(accentColor.r, accentColor.g, accentColor.b, fillAlpha);

        _edges = new Image[4];
        _edges[0] = NewEdge("EdgeTop");
        _edges[1] = NewEdge("EdgeBottom");
        _edges[2] = NewEdge("EdgeLeft");
        _edges[3] = NewEdge("EdgeRight");

        var barRt = NewRect("LeftAccentBar", _root);
        _leftBar = barRt.gameObject.AddComponent<Image>();
        _leftBar.raycastTarget = false;
        _leftBar.color = accentColor;
    }

    private Image NewEdge(string name)
    {
        var rt = NewRect(name, _root);
        var img = rt.gameObject.AddComponent<Image>();
        img.raycastTarget = false;
        img.color = accentColor;
        return img;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
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

    /// <summary>Aggiorna la modalità input: mouse → nasconde, controller/tastiera → mostra.</summary>
    private void UpdateInputMode()
    {
        var mouse = Mouse.current;
        if (mouse != null)
        {
            bool mouseUsed =
                mouse.delta.ReadValue().sqrMagnitude > 1f ||
                mouse.leftButton.wasPressedThisFrame ||
                mouse.rightButton.wasPressedThisFrame ||
                mouse.scroll.ReadValue().sqrMagnitude > 0.01f;
            if (mouseUsed) _controllerMode = false;
        }

        var gp = Gamepad.current;
        bool gpUsed = gp != null && (
            gp.leftStick.ReadValue().sqrMagnitude > 0.1f ||
            gp.dpad.ReadValue().sqrMagnitude > 0.1f ||
            gp.buttonSouth.wasPressedThisFrame || gp.buttonEast.wasPressedThisFrame ||
            gp.buttonWest.wasPressedThisFrame || gp.buttonNorth.wasPressedThisFrame);

        var kb = Keyboard.current;
        bool kbUsed = kb != null && (
            kb.upArrowKey.isPressed || kb.downArrowKey.isPressed ||
            kb.leftArrowKey.isPressed || kb.rightArrowKey.isPressed ||
            kb.tabKey.wasPressedThisFrame ||
            kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame);

        if (gpUsed || kbUsed) _controllerMode = true;

        ControllerActive = _controllerMode;
    }

    private void LateUpdate()
    {
        UpdateInputMode();

        var sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;

        bool valid = _controllerMode && sel != null && sel.activeInHierarchy && sel.GetComponent<Selectable>() != null;
        if (!valid)
        {
            SetVisible(false);
            _current = null;
            _hasTarget = false;
            return;
        }

        if (sel != _current)
        {
            _current = sel;
            SetVisible(true);
        }

        ComputeTarget(sel.GetComponent<RectTransform>());
        AnimateToTarget();
        if (pulse) ApplyPulse();
    }

    /// <summary>Converte i corner-world del target in coordinate locali del canvas.</summary>
    private void ComputeTarget(RectTransform target)
    {
        if (target == null) { _hasTarget = false; return; }

        var corners = new Vector3[4];
        target.GetWorldCorners(corners); // 0=BL 1=TL 2=TR 3=BR
        Vector2 min = _canvasRect.InverseTransformPoint(corners[0]);
        Vector2 max = _canvasRect.InverseTransformPoint(corners[2]);

        Vector2 size = (max - min) + new Vector2(padding, padding) * 2f;
        Vector2 center = (min + max) * 0.5f;

        _targetSize = size;
        _targetPos = center;
        _hasTarget = true;
    }

    private void AnimateToTarget()
    {
        if (!_hasTarget) return;

        // Root ancorato al centro del canvas: posizione = offset dal centro.
        _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f);
        _root.pivot = new Vector2(0.5f, 0.5f);

        float t = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
        _root.anchoredPosition = Vector2.Lerp(_root.anchoredPosition, _targetPos, t);
        _root.sizeDelta = Vector2.Lerp(_root.sizeDelta, _targetSize, t);

        LayoutEdges();
    }

    private void LayoutEdges()
    {
        float w = _root.sizeDelta.x;
        float h = _root.sizeDelta.y;
        float bt = borderThickness;

        // Top
        SetEdge(_edges[0].rectTransform, new Vector2(0, (h - bt) * 0.5f), new Vector2(w, bt));
        // Bottom
        SetEdge(_edges[1].rectTransform, new Vector2(0, -(h - bt) * 0.5f), new Vector2(w, bt));
        // Left
        SetEdge(_edges[2].rectTransform, new Vector2(-(w - bt) * 0.5f, 0), new Vector2(bt, h));
        // Right
        SetEdge(_edges[3].rectTransform, new Vector2((w - bt) * 0.5f, 0), new Vector2(bt, h));

        // Barra accent sinistra (leggermente più spessa, dentro il bordo)
        var bar = _leftBar.rectTransform;
        SetEdge(bar, new Vector2(-(w - leftBarWidth) * 0.5f, 0), new Vector2(leftBarWidth, h));
    }

    private static void SetEdge(RectTransform rt, Vector2 anchoredPos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
    }

    private void ApplyPulse()
    {
        float a = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * pulseSpeed);
        var c = accentColor; c.a = a;
        for (int i = 0; i < _edges.Length; i++) _edges[i].color = c;
    }

    private void SetVisible(bool v)
    {
        if (_root != null && _root.gameObject.activeSelf != v)
            _root.gameObject.SetActive(v);
    }
}
