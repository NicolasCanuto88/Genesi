using UnityEngine;

/// <summary>
/// TabletDashboardUI — Milestone 2.
/// Pannello radice del Tablet. Si appoggia a MonitorSwitcher (lo stesso
/// componente già usato per i monitor della Engineering Station) per
/// alternare i tab "Profilo"/"Nave" — nessuna modifica a MonitorSwitcher
/// è stata necessaria per questo riuso, accetta qualunque array di CanvasGroup
/// e il riferimento opzionale a EngineeringStation può restare vuoto.
///
/// Implementa IDashboardPanel per essere compatibile con il pattern Open()/Close()
/// già usato da TabletStation, EngineeringStation, ecc.
/// </summary>
public class TabletDashboardUI : MonoBehaviour, IDashboardPanel
{
    [Tooltip("CanvasGroup[0] = tab Profilo, CanvasGroup[1] = tab Nave (ordine di navigazione).")]
    [SerializeField] private MonitorSwitcher tabSwitcher;

    public void Open()
    {
        if (tabSwitcher != null)
            tabSwitcher.ShowMonitor(0, instant: true); // riparte sempre dal tab "Profilo"
    }

    public void Close()
    {
        // MonitorSwitcher chiama Open()/Close() solo AL CAMBIO tab — quando l'intero
        // tablet si chiude bisogna chiudere esplicitamente anche il tab rimasto visibile,
        // altrimenti il suo InvokeRepeating (polling dati) continuerebbe in background.
        tabSwitcher?.GetCurrentPanel()?.Close();
    }
}
