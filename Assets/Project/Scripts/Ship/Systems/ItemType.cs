namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Tutti i tipi di materiale gestiti dall'InventorySystem.
    /// I valori interi sono stabili — non riordinarli mai: sono usati come indici.
    /// </summary>
    public enum ItemType
    {
        // ── Engineering ───────────────────────────────
        MechanicalPart      = 0,
        WireBundle          = 1,
        ElectronicComponent = 2,
        HullPlate           = 3,
        CoolantCanister     = 4,
        FuelCell            = 5,

        // ── Medical ───────────────────────────────────
        MedkitBase          = 6,
        MedkitAdvanced      = 7,
        O2EmergencyTank     = 8,
        Antidote            = 9,

        // ── Sentinel — SEMPRE ULTIMA ─────────────────
        COUNT               = 10
    }

    public enum ItemCategory { Engineering, Medical }
}
