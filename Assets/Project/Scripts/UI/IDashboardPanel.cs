/// <summary>
/// Interfaccia implementata da tutti i dashboard UI collegati a MonitorSwitcher.
/// MonitorSwitcher chiama Open() quando il monitor diventa visibile
/// e Close() quando viene nascosto, così ogni dashboard gestisce
/// il proprio ciclo di aggiornamento (InvokeRepeating) in modo autonomo.
///
/// Implementata da:
///   - EngineeringDashboardUI  (Monitor 1)
///   - ShipSystemsDashboardUI  (Monitor 2)
///   - InventoryDashboardUI    (Monitor 3, da implementare)
/// </summary>
public interface IDashboardPanel
{
    void Open();
    void Close();
}
