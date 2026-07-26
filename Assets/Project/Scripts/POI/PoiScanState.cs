namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// Stato di scansione di un PoiInstance — Milestone 3, Blocco 3, Sottofase 2b.
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
    ///              vicino (dipende da: azione utente nella ScannerUI — 2b/
    ///              Blocco 3 Fase 3). Sblocca eventuali informazioni aggiuntive
    ///              future (loot preview con Scanner T2+, "Deep Scan").
    ///
    /// NOTE FUTURE (registrate ma NON implementate in 2b):
    ///
    ///   Anchored — quando la nave si ancorerà al relitto (Blocco 3 Fase 3
    ///              con lo stato NavigationState.Anchored su PropulsionSystem)
    ///              servirà un ulteriore stato per marcare il POI come "punto
    ///              di ancoraggio attivo" e triggerare l'eventuale attivazione
    ///              di collider/interior per l'abbordaggio. In 2b non ci
    ///              arriviamo — lo aggiungeremo in coda a questo enum quando
    ///              lo scriveremo davvero, per evitare di lasciare stati morti
    ///              nel codice.
    /// </summary>
    public enum PoiScanState : byte
    {
        Unknown = 0,
        Detected = 1,
        Scanned = 2
    }
}
