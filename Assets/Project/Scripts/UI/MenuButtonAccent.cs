using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

/// <summary>
/// MenuButtonAccent — feedback hover/focus/premuto per i bottoni del menu
/// principale. Fa apparire in dissolvenza la striscia accent "LeftAccentBar"
/// (già presente nei bottoni ma con alpha 0, quindi mai visibile) quando il
/// bottone è sotto il puntatore o selezionato via controller/tastiera, e la
/// spegne quando il focus/hover se ne va. Opzionalmente ravviva il testo.
///
/// MOTIVO: i bottoni del menu hanno transition=None → nessun feedback nemmeno
/// col mouse. Questo componente fornisce un feedback coerente col motivo
/// grafico esistente (barra accent ciano) sia a mouse che a controller,
/// riusando l'elemento LeftAccentBar già presente nella gerarchia.
///
/// Se il bottone non è interactable, l'accent resta spento.
/// </summary>
[RequireComponent(typeof(Button))]
[DisallowMultipleComponent]
public class MenuButtonAccent : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler,
    ISelectHandler, IDeselectHandler,
    IPointerDownHandler, IPointerUpHandler
{
    [Tooltip("Nome del figlio da usare come barra accent.")]
    [SerializeField] private string accentChildName = "LeftAccentBar";
    [Tooltip("Colore pieno della barra quando attiva.")]
    [SerializeField] private Color accentColor = new Color(0f, 0.784f, 0.937f, 1f); // #00C8EF
    [Tooltip("Velocità della dissolvenza (alto = più rapido).")]
    [SerializeField] private float fadeSpeed = 14f;
    [Tooltip("Ravviva il testo (TMP) quando attivo.")]
    [SerializeField] private bool brightenLabel = true;
    [Tooltip("Colore del testo quando attivo (se brightenLabel).")]
    [SerializeField] private Color labelActiveColor = Color.white;

    private Button _button;
    private Image _bar;
    private TMP_Text _label;
    private Color _labelIdle;
    private bool _hovered;
    private bool _selected;
    private bool _pressed;

    private void Awake()
    {
        _button = GetComponent<Button>();
        var barT = transform.Find(accentChildName);
        if (barT != null) _bar = barT.GetComponent<Image>();
        _label = GetComponentInChildren<TMP_Text>(true);
        if (_label != null) _labelIdle = _label.color;
        ApplyImmediate(0f);
    }

    private void OnDisable()
    {
        _hovered = _selected = _pressed = false;
        ApplyImmediate(0f);
    }

    public void OnPointerEnter(PointerEventData e) => _hovered = true;
    public void OnPointerExit(PointerEventData e) { _hovered = false; _pressed = false; }
    public void OnSelect(BaseEventData e) => _selected = true;
    public void OnDeselect(BaseEventData e) => _selected = false;
    public void OnPointerDown(PointerEventData e) => _pressed = true;
    public void OnPointerUp(PointerEventData e) => _pressed = false;

    // Hover vale sempre (mouse). Lo stato "selezionato" accende l'accent solo
    // in modalità controller/tastiera: col mouse deve illuminarsi soltanto la
    // voce effettivamente sotto il puntatore, non quella selezionata a runtime.
    private bool Active =>
        (_hovered || (_selected && MenuSelectionHighlight.ControllerActive))
        && _button != null && _button.interactable;

    private void Update()
    {
        float target = Active ? (_pressed ? 1f : 0.85f) : 0f;
        float t = 1f - Mathf.Exp(-fadeSpeed * Time.unscaledDeltaTime);

        if (_bar != null)
        {
            var c = _bar.color;
            c = Color.Lerp(c, new Color(accentColor.r, accentColor.g, accentColor.b, target * accentColor.a), t);
            _bar.color = c;
        }

        if (brightenLabel && _label != null)
        {
            var to = Active ? labelActiveColor : _labelIdle;
            _label.color = Color.Lerp(_label.color, to, t);
        }
    }

    private void ApplyImmediate(float alpha)
    {
        if (_bar != null)
        {
            var c = accentColor; c.a = alpha * accentColor.a;
            _bar.color = c;
        }
        if (brightenLabel && _label != null && Application.isPlaying)
            _label.color = _labelIdle;
    }
}
