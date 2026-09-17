using UnityEngine;
using TMPro;

/// <summary>
/// PowerReadout — Chrome energia persistente della dashboard (Rev BG - Stage A).
///
/// Mostra sempre la POTENZA DISPONIBILE (MaxPowerOutput meno consumo) in cifre, su
/// ogni schermata della Engineering Station: vive sul canvas dashboard FUORI dai
/// CanvasGroup delle sezioni, cosi resta visibile qualunque sezione sia attiva
/// - hub incluso - e dalla sezione Relitto - Stage B - dove serve leggere il
/// margine mentre l'ombelicale consuma in proporzione alla massa del POI.
///
/// COLORE - calibrato sul comportamento reale del PowerManager:
///   - blackout, oppure disponibile minore-uguale 0 - deficit -> critical
///   - frazione disponibile minore-uguale warningAvailableFraction -> warning - giallo
///   - altrimenti -> normal
/// Il blackout scatta con riserva esaurita E consumo maggiore-uguale generazione:
/// il vero "vicino al blackout" e il margine disponibile che si avvicina a 0, non
/// lo stato Critical - che scatta molto prima, al 20 percento. Per questo il giallo
/// e legato al margine, non a IsInCriticalState.
///
/// EVENTO, NON POLLING - paletto: si sottoscrive a PowerManager.OnPowerLevelChanged,
/// emesso ogni frame via RPC. Il testo si aggiorna da solo e cattura anche il drift
/// di MaxPowerOutput da degradazione reattore. Nessun prefab, nessuna barra: solo cifre.
///
/// SPAWN ORDER: se PowerManager.Instance non e ancora pronto all'OnEnable, si aggancia
/// via PowerManager.OnInstanceReady - stesso pattern dei dashboard esistenti.
///
/// SETUP: un GameObject figlio del canvas dashboard, NON dentro un CanvasGroup di
/// sezione. Assegna 'label', oppure lascia che venga cercata su questo GameObject.
/// </summary>
public class PowerReadout : MonoBehaviour
{
    [Header("Riferimenti")]
    [Tooltip("La label che mostra le cifre. Se non assegnata, viene cercata su questo GameObject.")]
    [SerializeField] private TextMeshProUGUI label;

    [Header("Formato")]
    [Tooltip("Prefisso mostrato prima del valore. Il suffisso W e aggiunto dopo il numero.")]
    [SerializeField] private string prefix = "DISPONIBILE ";

    [Header("Soglia giallo")]
    [Tooltip("Frazione di potenza disponibile 0-1 su MaxPowerOutput sotto la quale il " +
             "readout vira al giallo. E il warning vicino-al-blackout. Default 0.15 = 15 percento.")]
    [Range(0f, 1f)]
    [SerializeField] private float warningAvailableFraction = 0.15f;

    [Header("Colori")]
    [SerializeField] private Color colorNormal = new Color(0f, 0.784f, 0.937f, 1f);  // #00C8EF cyan progetto
    [SerializeField] private Color colorWarning = new Color(1f, 0.85f, 0f, 1f);      // giallo
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f, 1f);      // rosso

    [Header("Debug")]
    [Tooltip("Log verbosi non critici - standard Rev BA - default off.")]
    [SerializeField] private bool logVerbose = false;

    private bool _subscribed;
    private string _lastText;
    private bool _hasLastColor;
    private Color _lastColor;

    private void Awake()
    {
        if (label == null) label = GetComponent<TextMeshProUGUI>();
        if (label == null)
            Debug.LogWarning("[PowerReadout] Nessuna TextMeshProUGUI assegnata ne trovata su questo GameObject.");
    }

    private void OnEnable()
    {
        if (PowerManager.Instance != null)
        {
            Subscribe();
            Refresh();
        }
        else
        {
            PowerManager.OnInstanceReady += HandleInstanceReady;
        }
    }

    private void OnDisable()
    {
        PowerManager.OnInstanceReady -= HandleInstanceReady;
        Unsubscribe();
    }

    private void HandleInstanceReady()
    {
        Subscribe();
        Refresh();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        var pm = PowerManager.Instance;
        if (pm == null) return;
        pm.OnPowerLevelChanged += HandlePowerLevelChanged;
        _subscribed = true;
        LogV("[PowerReadout] Sottoscritto a PowerManager.OnPowerLevelChanged.");
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        var pm = PowerManager.Instance;
        if (pm != null) pm.OnPowerLevelChanged -= HandlePowerLevelChanged;
        _subscribed = false;
    }

    // Il parametro percent non serve: leggiamo direttamente le property
    // - disponibile = max meno consumo - per non dipendere dalla metrica dell'evento.
    private void HandlePowerLevelChanged(float percent) => Refresh();

    private void Refresh()
    {
        var pm = PowerManager.Instance;
        if (pm == null || label == null) return;

        float max = pm.MaxPowerOutput;
        float available = max - pm.CurrentPowerConsumption;
        float frac = max > 0f ? available / max : 0f;

        // Testo - solo cifre, nessun prefab, nessuna barra.
        string txt = prefix + Mathf.RoundToInt(available) + " W";
        if (txt != _lastText)
        {
            label.text = txt;
            _lastText = txt;
        }

        // Colore calibrato sul comportamento reale del blackout.
        Color c;
        if (pm.IsInBlackout || available <= 0f) c = colorCritical;
        else if (frac <= warningAvailableFraction) c = colorWarning;
        else c = colorNormal;

        if (!_hasLastColor || c != _lastColor)
        {
            label.color = c;
            _lastColor = c;
            _hasLastColor = true;
        }
    }

    private void LogV(string msg) { if (logVerbose) Debug.Log(msg); }
}
