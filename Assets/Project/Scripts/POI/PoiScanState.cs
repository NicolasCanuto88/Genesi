namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// Stato di scansione di un PoiInstance — Milestone 3, Blocco 3.
    ///
    /// Progressione naturale attesa (server-authoritative, replicata via
    /// NetworkVariable&lt;PoiScanState&gt; su PoiInstance):
    ///
    ///   Unknown  → il POI esiste nello spazio logico ma nessun ScannerSystem
    ///              di alcuna nave lo ha ancora rilevato. Non visibile alla
    ///              ScannerUI. Il PoiVisual esiste e si muove col mondo esterno
    ///              (visibile a occhio se il pilota ci finisce vicino), ma non
    ///              è "conosciuto" dal team.
    ///
    ///   Detected → lo ScannerSystem lo ha rilevato entro il proprio scanRange.
    ///              Compare nella lista della ScannerUI con tipo e distanza.
    ///              Transizione automatica al primo tick in cui la distanza
    ///              logica dalla nave è entro il raggio.
    ///
    ///   Scanned  → il team ha eseguito uno scan attivo dal PoiInstance da
    ///              vicino (dipende da: azione utente nella ScannerUI). Sblocca
    ///              eventuali informazioni aggiuntive future (loot preview con
    ///              Scanner T2+, "Deep Scan").
    ///
    ///   Anchored → [Fase 3 Blocco 3.1] la nave si è ancorata a questo POI
    ///              tramite il minigioco di docking. Stato terminale del loop
    ///              di attracco. Un solo POI alla volta può essere Anchored
    ///              per una data nave — l'unicità è garantita da
    ///              PropulsionSystem.AnchoredPoiId lato server. In uscita da
    ///              Docked, torna a Scanned (era necessariamente già stato
    ///              scansionato per essere ancorabile).
    /// </summary>
    public enum PoiScanState : byte
    {
        Unknown = 0,
        Detected = 1,
        Scanned = 2,
        Anchored = 3
    }
}