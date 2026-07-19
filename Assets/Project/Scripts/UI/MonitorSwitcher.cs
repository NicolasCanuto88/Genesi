using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Monitor switcher for the Engineering Station (Milestone 1B).
/// Cycles between dashboard pages (Monitor 1/2/3) with the Player map's
/// Previous/Next actions (← → keyboard, L/R gamepad) while the dashboard is open.
///
/// Self-contained: gates navigation on VirtualCursor.Instance.IsActive and reads
/// input from a PlayerInput reference — it does NOT modify EngineeringStation, so
/// the existing enter/exit/camera transition is untouched.
///
/// Pages are shown/hidden via CanvasGroup (not SetActive) so Monitor 1's live
/// EngineeringDashboardUI keeps updating in the background.
///
/// PATCH M2: ogni CanvasGroup può avere opzionalmente un IDashboardPanel
/// sullo stesso GameObject. ShowMonitor chiama Open() sul pannello attivo
/// e Close() su quello precedente, così i dashboard aggiornano i dati
/// solo quando sono visibili (InvokeRepeating parte/stop correttamente).
/// </summary>
public class MonitorSwitcher : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("PlayerInput del player (stessa reference usata dagli altri sistemi).")]
    [SerializeField] private PlayerInput playerInput;
    [SerializeField] private string previousActionName = "Previous";
    [SerializeField] private string nextActionName = "Next";

    [Header("Monitors (in order: 1, 2, 3...)")]
    [Tooltip("Un CanvasGroup per pagina-monitor, nell'ordine di navigazione.")]
    [SerializeField] private CanvasGroup[] monitors;

    [Tooltip("Monitor mostrato all'apertura della dashboard (0 = Monitor 1).")]
    [SerializeField] private int defaultMonitorIndex = 0;

    [Tooltip("Se true, dopo l'ultimo monitor si torna al primo (e viceversa).")]
    [SerializeField] private bool wrapAround = false;

    [Header("Camera")]
    [Tooltip("Riferimento opzionale per animare la camera verso il monitor attivo.")]
    [SerializeField] private EngineeringStation engineeringStation;

    private InputAction previousAction;
    private InputAction nextAction;

    private int currentIndex = 0;
    private bool dashboardWasActive = false;

    // Cache dei pannelli IDashboardPanel — uno per monitor (null se assente)
    private IDashboardPanel[] panels;

    public int CurrentIndex => currentIndex;

    /// <summary>
    /// AGGIUNTA (Tablet support): espone il pannello IDashboardPanel del monitor/tab
    /// attualmente visibile, così chi possiede questo switcher (es. TabletDashboardUI)
    /// può chiamarne Close() esplicitamente quando l'intero contenitore si chiude —
    /// MonitorSwitcher di per sé chiama Open()/Close() solo AL CAMBIO di pagina.
    /// Puramente additivo: nessun comportamento esistente modificato.
    /// </summary>
    public IDashboardPanel GetCurrentPanel()
        => (panels != null && currentIndex >= 0 && currentIndex < panels.Length) ? panels[currentIndex] : null;

    private void Start()
    {
        if (playerInput != null)
        {
            previousAction = playerInput.actions.FindAction(previousActionName, throwIfNotFound: false);
            nextAction = playerInput.actions.FindAction(nextActionName, throwIfNotFound: false);

            if (previousAction == null || nextAction == null)
                Debug.LogWarning("[MonitorSwitcher] Action Previous/Next non trovate nel PlayerInput (map Player).");
        }
        else
        {
            Debug.LogWarning("[MonitorSwitcher] PlayerInput reference non assegnato.");
        }

        // Cerca IDashboardPanel su ogni CanvasGroup, oppure sui suoi figli.
        // GetComponentInChildren (non solo GetComponent) è essenziale: nella
        // scena tipica, EngineeringDashboardUI sta su un GameObject FIGLIO
        // del CanvasGroup del monitor, non sul CanvasGroup stesso.
        // GetComponent restituisce null in quel caso, panels[i] resta null,
        // e la Open/Close via IDashboardPanel non viene mai chiamata → la
        // selezione EventSystem non viene ripristinata al cambio monitor.
        // GetComponentInChildren funziona anche quando lo script è sullo
        // stesso GameObject del CanvasGroup, quindi copre entrambi i casi.
        panels = new IDashboardPanel[monitors != null ? monitors.Length : 0];
        for (int i = 0; i < panels.Length; i++)
        {
            if (monitors[i] != null)
                panels[i] = monitors[i].GetComponentInChildren<IDashboardPanel>(includeInactive: true);
        }

        ShowMonitor(defaultMonitorIndex, instant: true);
    }

    private void Update()
    {
        // Gate "sono seduto alla postazione?": in origine era
        // VirtualCursor.Instance.IsActive, che accendeva/spegneva la
        // navigazione dei monitor insieme al cursore virtuale. Rimosso
        // VirtualCursor con la conversione a navigazione a tasti
        // direzionali, il segnale corretto è direttamente lo stato della
        // stazione — è comunque il vero significato di "la dashboard è
        // attiva": il giocatore è seduto e la sta usando.
        //
        // Se engineeringStation non è cablato in Inspector (config
        // sbagliata), degrada in modo prudente: navigazione DISATTIVA.
        // Meglio che spara switch inaspettati quando il giocatore preme
        // ← → in gameplay normale.
        bool dashboardActive = engineeringStation != null && engineeringStation.IsUsingStation;

        // On open: reset to the default monitor so reopening always starts on Monitor 1.
        if (dashboardActive && !dashboardWasActive)
            ShowMonitor(defaultMonitorIndex, instant: true);

        dashboardWasActive = dashboardActive;

        if (!dashboardActive) return;

        if (nextAction != null && nextAction.WasPressedThisFrame())
            Next();
        else if (previousAction != null && previousAction.WasPressedThisFrame())
            Previous();
    }

    public void Next() => Navigate(+1);
    public void Previous() => Navigate(-1);

    private void Navigate(int direction)
    {
        if (monitors == null || monitors.Length == 0) return;

        int target = currentIndex + direction;

        if (wrapAround)
            target = (target + monitors.Length) % monitors.Length;
        else
            target = Mathf.Clamp(target, 0, monitors.Length - 1);

        ShowMonitor(target);
    }

    public void ShowMonitor(int index, bool instant = false)
    {
        if (monitors == null || monitors.Length == 0) return;

        int previousIndex = currentIndex;
        currentIndex = Mathf.Clamp(index, 0, monitors.Length - 1);

        for (int i = 0; i < monitors.Length; i++)
        {
            CanvasGroup cg = monitors[i];
            if (cg == null) continue;

            bool show = (i == currentIndex);
            cg.alpha = show ? 1f : 0f;
            cg.interactable = show;
            cg.blocksRaycasts = show;

            // Notifica il pannello se implementa IDashboardPanel
            if (panels != null && i < panels.Length && panels[i] != null)
            {
                if (show)
                    panels[i].Open();
                else if (i == previousIndex)   // chiudi solo quello che era aperto
                    panels[i].Close();
            }
        }

        // Anima la camera verso il monitor attivo
        if (engineeringStation != null)
            engineeringStation.LookAtMonitor(currentIndex);
    }
}