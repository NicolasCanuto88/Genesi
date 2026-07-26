namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// Tipo statico di un POI (Point of Interest) — Milestone 3, Blocco 3,
    /// Sottofase 2b.
    ///
    /// Il tipo determina la fiction e (in futuro) il comportamento
    /// dell'abbordaggio. In 2b il tipo serve principalmente alla ScannerUI
    /// per mostrare un'etichetta comprensibile ("RELITTO ABBANDONATO").
    ///
    /// Coerente con GDD §2 Fase 3: tre scenari di abbordaggio previsti
    /// (relitto abbandonato / con naufraghi / con saccheggiatori). Per la
    /// Sottofase 2b lavoriamo esclusivamente con WreckAbandoned. Gli altri
    /// valori sono elencati come promemoria di design ma nessun sistema li
    /// gestisce ancora — decommentare/estendere quando si arriva alla
    /// Fase 3 (Sistema Incontri, GDD §4).
    /// </summary>
    public enum PoiType : byte
    {
        WreckAbandoned = 0

        // NOTE FUTURE (Blocco 3 Fase 3 / Blocco 4):
        // WreckSurvivors  = 1   // relitto con naufraghi (scelta morale)
        // WreckScavengers = 2   // relitto con saccheggiatori (combattimento)
        // AsteroidLarge   = 3   // asteroide di dimensioni interessanti
        // Station         = 4   // stazione spaziale abbandonata / attiva
    }
}
