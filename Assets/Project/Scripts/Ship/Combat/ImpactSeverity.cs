namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Classificazione discreta della severità di un impatto (Rev AE, Blocco 3.2.d
    /// parte 2). Prodotta da <see cref="ImpactThresholdTable.Classify"/> a partire
    /// dalla radial impact velocity, consumata dai tre canali di feedback teatrale:
    /// screen shake (<see cref="CameraShaker"/>) e audio one-shot
    /// (<see cref="ImpactAudioController"/>).
    ///
    /// Discreta e non continua (QB-2 confermata Rev AE): UX prevedibile, un'unica
    /// tabella di soglie design-facing invece di 2 curve indipendenti per shake e
    /// audio → coerenza percettiva garantita.
    ///
    /// Range di riferimento (Rev AE default, tunabili in ImpactThresholdTable):
    ///   Light  : v ∈ [ConfirmMaxVelocity, 3.0) u/s — bump lieve
    ///   Medium : v ∈ [3.0, 8.0)                    — urto medio
    ///   Hard   : v ∈ [8.0, +∞)                     — collisione dura
    ///
    /// Sotto ConfirmMaxVelocity nessun impatto è generato (invariante Rev X):
    /// <c>ShipImpactHandler.HandleHardCollision</c> scarta il fire prima di
    /// invocare la classificazione. Non serve una severity "None".
    /// </summary>
    public enum ImpactSeverity : byte
    {
        Light = 0,
        Medium = 1,
        Hard = 2,
    }
}
