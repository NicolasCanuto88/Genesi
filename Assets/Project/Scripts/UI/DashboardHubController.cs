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

        ShowHubInternal();
    }

    private void Update()
    {
        bool active = engineeringStation != null && engineeringStation.IsUsingStation;

        // All'apertura della postazione: reset all'hub.
        if (active && !dashboardWasActive)
            ShowHubInternal();

        // Alla chiusura: chiudi la sezione attiva e nascondi tutto (copre tutte le
        // sezioni, non solo la prima — a differenza del vecchio Close esplicito).
        if (!active && dashboardWasActive)
            CloseAll();

        dashboardWasActive = active;

        // Rete di sicurezza selezione per l'hub (le sezioni la gestiscono da se in
        // Open via DashboardSelection). Solo quando l'hub e effettivamente mostrato.
        if (active && currentSection == -1)
            DashboardSelection.EnsureSafety(this, ChooseHubSelection, logVerbose);
    }

    // ── API pubblica per i button (cablata in scena via OnClick) ───────────────

    /// <summary>Button HOME di una sezione: torna all'hub.</summary>
    public void ShowHub() => ShowHubInternal();

    /// <summary>Button dell'hub: apre la sezione all'indice indicato.</summary>
    public void ShowSection(int index)
    {
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
        if (currentSection >= 0 && panels != null && currentSection < panels.Length && panels[currentSection] != null)
            panels[currentSection].Close();

        SetGroup(hubGroup, true);
        if (sections != null)
        {
            for (int i = 0; i < sections.Length; i++)
                SetGroup(sections[i], false);
        }

        currentSection = -1;

        // Seleziona il primo button dell'hub (cyan) il frame successivo.
        DashboardSelection.SetInitial(this, ChooseHubSelection, logVerbose);
        if (logVerbose) Debug.Log("[DashboardHub] Hub mostrato.");
    }

    private void CloseAll()
    {
        if (currentSection >= 0 && panels != null && currentSection < panels.Length && panels[currentSection] != null)
            panels[currentSection].Close();

        SetGroup(hubGroup, false);
        if (sections != null)
        {
            for (int i = 0; i < sections.Length; i++)
                SetGroup(sections[i], false);
        }

        currentSection = -1;
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
