namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Tutti i tipi di materiale gestiti dall'InventorySystem.
    /// I valori interi sono stabili — non riordinarli mai: sono usati come indici.
    /// Tutto ciò che viene da MedkitBase in poi è materiale MEDICO (le UI filtrano con
    /// "type &gt;= MedkitBase"): i nuovi item medici vanno in coda, prima di COUNT.
    /// </summary>
    public enum ItemType
    {
        // ── Engineering ───────────────────────────────
        MechanicalPart = 0,
        WireBundle = 1,
        ElectronicComponent = 2,
        HullPlate = 3,
        CoolantCanister = 4,
        FuelCell = 5,

        // ── Medical ───────────────────────────────────
        MedkitBase = 6,
        MedkitAdvanced = 7,
        O2EmergencyTank = 8,
        Antidote = 9,

        // ── Medical: droghe del Corpsman (Rev BR-b · Q50-a) ──
        Adrenaline = 10,
        HazmatInjection = 11,
        CombatStim = 12,

        // ── Sentinel — SEMPRE ULTIMA ─────────────────
        COUNT = 13
    }

    public enum ItemCategory { Engineering, Medical }
}