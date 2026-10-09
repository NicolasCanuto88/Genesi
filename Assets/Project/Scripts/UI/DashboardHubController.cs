using UnityEngine;

/// <summary>
/// DashboardHubController — navigazione a hub per la Engineering Station
/// (Rev BG - Stage A).
///
/// Sostituisce MonitorSwitcher SOLO sulla postazione Ingegneria: al posto del ciclo
/// lineare Previous/Next tra monitor, presenta un HUB con un button per sezione;
/// selezionando un button si apre la sezione, un button HOME riporta all'hub.
/// MonitorSwitcher resta intatto nel progetto (lo usano Tablet/TabletStation).
///
/// NON muove la camera: con l'hub le sezioni vivono nella stessa area dashboard,
/// quindi non c'e piu movimento di camera tra sezioni. Il puntamento all'area
/// avviene ancora all'ingresso, dentro EngineeringStation - invariata.
///
/// COLLOCAZIONE: va sul GameObject che porta il CanvasGroup dell'HUB. Cosi la rete
/// di sicurezza selezione (DashboardSelection.EnsureSafety) vede il CanvasGroup hub
/// come proprio genitore e i button hub come propri figli, senza casi speciali.
///
/// CABLAGGIO in scena (vedi guida UI):
///   - ogni button dell'hub -> OnClick -> ShowSection(i)  [i = indice sezione]
///   - il button HOME di ogni sezione -> OnClick -> ShowHub()
///   - 'sections' elencate nello stesso ordine degli indici usati dai button hub
///   - ogni sezione porta un IDashboardPanel (su di se o su un figlio):
///     Open()/Close() vengono chiamati all'apertura/chiusura della sezione.
///
/// HUB A RIPOSO (Rev BX-b · Q119-a): quando nessuno è seduto l'hub resta visibile sul
/// monitor (come all'avvio della scena) ma NON è interattivo, non riceve raycast e non ha
/// nessun elemento selezionato. Prima, dall'avvio della scena, l'hub era interattivo e il suo
/// primo button era selezionato anche senza nessuno seduto: camminando con WASD (che è anche
/// Navigate nella mappa UI) la selezione e il riquadro ciano si spostavano sul monitor, e col
/// gamepad A (Jump, che è anche Submit) apriva una sezione a caso. All'uscita dalla postazione
/// l'hub ora torna nello stesso stato di riposo (prima spariva: monitor vuoto dopo il primo uso).
/// L'hub diventa interattivo e seleziona il primo button solo all'ingresso del giocatore.
/// ShowSection e ShowHub non fanno nulla a postazione libera (difesa: nessun click a riposo).
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class DashboardHubController : MonoBehaviour
{
    [Header("Gate stazione")]
    [Tooltip("EngineeringStation della postazione: l'hub e attivo solo mentre il " +
             "giocatore e seduto (IsUsingStation).")]
    [SerializeField] private EngineeringStation engineeringStation;

    [Header("Sezioni (stesso ordine dei button hub)")]
    [Tooltip("Un CanvasGroup per sezione: Luci/Power, Ship Systems, Inventory, e in " +
             "Stage B il Relitto. L'ordine deve combaciare con gli indici passati a ShowSection.")]
    [SerializeField] private CanvasGroup[] sections;

    [Header("Debug")]
    [Tooltip("Log verbosi non critici - standard Rev BA - default off.")]
    [SerializeField] private bool logVerbose = false;

    private CanvasGroup hubGroup;               // il CanvasGroup su questo GameObject
    private IDashboardPanel[] panels;           // uno per sezione (null se assente)
    private int currentSection = -1;            // -1 = hub mostrato
    private bool dashboardWasActive;

    /// <summary>
    /// Rev BX-b — vero mentre il giocatore locale è seduto alla postazione e non si sta alzando.
    /// L'uscita dura una transizione (fino a 1 s): prima, in quel tempo, la rete di sicurezza
    /// riselezionava il primo button e il riquadro saltava lì mentre la camera si allontanava.
    /// Ora l'hub va a riposo all'inizio dell'uscita.
    /// </summary>
    private bool IsStationActive => engineeringStation != null
                                    && engineeringStation.IsUsingStation
                                    && !engineeringStation.IsExiting;

    private void Awake()
    {
        hubGroup = GetComponent<CanvasGroup>();
    }

    private void Start()
    {
        // Cache degli IDashboardPanel: come MonitorSwitcher, cercati anche sui figli
        // perche tipicamente lo script del pannello sta su un figlio del CanvasGroup.
        panels = new IDashboardPanel[sections != null ? sections.Length : 0];
        for (int i = 0; i < panels.Length; i++)
        {
            if (sections[i] != null)
                panels[i] = sections[i].GetComponentInChildren<IDashboardPanel>(includeInactive: true);
        }

        // Rev BX-b — all'avvio della scena nessuno è seduto: hub a riposo, niente selezione.
        ShowRestState();
    }

    private void Update()
    {
        bool active = IsStationActive;

        // All'apertura della postazione: reset all'hub.
        if (active && !dashboardWasActive)
            ShowHubInternal();

        // Alla chiusura: chiudi la sezione attiva e torna all'hub a riposo (copre tutte le
        // sezioni, non solo la prima — a differenza del vecchio Close esplicito).
        if (!active && dashboardWasActive)
            ShowRestState();

        dashboardWasActive = active;

        // Rete di sicurezza selezione per l'hub (le sezioni la gestiscono da se in
        // Open via DashboardSelection). Solo quando l'hub e effettivamente mostrato.
        if (active && currentSection == -1)
            DashboardSelection.EnsureSafety(this, ChooseHubSelection, logVerbose);
    }

    // ── API pubblica per i button (cablata in scena via OnClick) ───────────────

    /// <summary>Button HOME di una sezione: torna all'hub.</summary>
    public void ShowHub()
    {
        // Rev BX-b — a postazione libera l'hub resta a riposo.
        if (!IsStationActive)
        {
            ShowRestState();
            return;
        }

        ShowHubInternal();
    }

    /// <summary>Button dell'hub: apre la sezione all'indice indicato.</summary>
    public void ShowSection(int index)
    {
        // Rev BX-b — a postazione libera nessuna sezione si apre (difesa: a riposo l'hub non
        // è interattivo, ma un Submit su una selezione rimasta non deve aprire nulla).
        if (!IsStationActive)
        {
            if (logVerbose) Debug.Log("[DashboardHub] ShowSection ignorato: postazione libera.");
            return;
        }

        if (sections == null || index < 0 || index >= sections.Length)
        {
            if (logVerbose) Debug.LogWarning("[DashboardHub] ShowSection indice fuori range: " + index);
            return;
        }

        SetGroup(hubGroup, false);

        for (int i = 0; i < sections.Length; i++)
        {
            bool show = (i == index);
            SetGroup(sections[i], show);

            if (panels != null && i < panels.Length && panels[i] != null)
            {
                if (show) panels[i].Open();
                else if (i == currentSection) panels[i].Close(); // chiudi solo la precedente
            }
        }

        currentSection = index;
        // La selezione iniziale della sezione la imposta il pannello stesso in Open().
        if (logVerbose) Debug.Log("[DashboardHub] Sezione aperta: " + index);
    }

    // ── Interno ────────────────────────────────────────────────────────────────

    private void ShowHubInternal()
    {
        // Chiudi la sezione eventualmente aperta.
        CloseCurrentSection();

        SetGroup(hubGroup, true);
        HideAllSections();

        currentSection = -1;

        // Seleziona il primo button dell'hub (cyan) il frame successivo.
        DashboardSelection.SetInitial(this, ChooseHubSelection, logVerbose);
        if (logVerbose) Debug.Log("[DashboardHub] Hub mostrato.");
    }

    /// <summary>
    /// Rev BX-b — hub a riposo: visibile ma non interattivo, senza raycast, sezioni nascoste,
    /// nessuna sezione aperta e nessuna selezione su hub o sezioni.
    /// </summary>
    private void ShowRestState()
    {
        CloseCurrentSection();

        if (hubGroup != null)
        {
            hubGroup.alpha = 1f;
            hubGroup.interactable = false;
            hubGroup.blocksRaycasts = false;
        }
        HideAllSections();

        currentSection = -1;
        ClearSelectionIfOurs();
        if (logVerbose) Debug.Log("[DashboardHub] Hub a riposo.");
    }

    /// <summary>
    /// Rev BX-b — azzera la selezione EventSystem solo se cade sull'hub o su una sua sezione:
    /// una selezione rimasta su un button a riposo mostrerebbe il riquadro sul monitor.
    /// Le selezioni di altri pannelli non si toccano.
    /// </summary>
    private void ClearSelectionIfOurs()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null) return;

        GameObject sel = es.currentSelectedGameObject;
        if (sel == null) return;

        bool ours = sel.transform.IsChildOf(transform);
        if (!ours && sections != null)
        {
            for (int i = 0; i < sections.Length && !ours; i++)
                ours = sections[i] != null && sel.transform.IsChildOf(sections[i].transform);
        }

        if (ours) es.SetSelectedGameObject(null);
    }

    private void CloseCurrentSection()
    {
        if (currentSection >= 0 && panels != null && currentSection < panels.Length && panels[currentSection] != null)
            panels[currentSection].Close();
    }

    private void HideAllSections()
    {
        if (sections == null) return;
        for (int i = 0; i < sections.Length; i++)
            SetGroup(sections[i], false);
    }

    // Chooser dell'hub: il primo button interactable sotto questo GameObject
    // (i button dell'hub sono figli del CanvasGroup hub, che sta qui).
    private GameObject ChooseHubSelection()
        => DashboardSelection.FirstInteractableSelectable(transform);

    private static void SetGroup(CanvasGroup cg, bool show)
    {
        if (cg == null) return;
        cg.alpha = show ? 1f : 0f;
        cg.interactable = show;
        cg.blocksRaycasts = show;
    }
}