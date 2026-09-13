namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Rev BF — Fase 2a (Danni Differenziati), Stage B.
    /// Identità dei subsystem nave che possono ricevere danno instradato dal
    /// ShipDamageRouter (residuo post-scudo).
    ///
    /// SEMANTICA DEI MEMBRI:
    ///   - Hull       : lo scafo. È il FLOOR del danno instradato — una quota del
    ///                  residuo post-scudo va SEMPRE qui, mai bypassabile
    ///                  (l'integrità core che porta alla distruzione della nave).
    ///                  Non viene "selezionato": è la destinazione garantita del
    ///                  hull-floor e il fallback quando la selezione non produce
    ///                  un bersaglio valido.
    ///   - Propulsion : PropulsionSystem. Selezionabile. Il danno abbassa la
    ///                  salute → degrada velocità e accelerazioni (catena effetto
    ///                  esistente via GetDegradationMults). NB: ortogonale allo
    ///                  stall transiente di TriggerEngineFailure (timer, non HP).
    ///   - FTL        : FTLDrive. Selezionabile. Il danno abbassa la salute →
    ///                  stato Offline → annulla/blocca la carica del salto.
    ///
    /// ESCLUSO DELIBERATAMENTE (workshop QB2):
    ///   - Shield : NON è un bersaglio instradato. È il PRE-LAYER — assorbe per
    ///              primo, a monte del router. Includerlo qui significherebbe
    ///              fargli ricevere il danno due volte.
    ///   - Life Support / Sensori / Docking : entreranno quando avranno un sink
    ///              danno + catena effetto reali. Niente placeholder inerti
    ///              (linea coerente col paletto Stabilizzazione).
    /// </summary>
    public enum ShipSubsystem : byte
    {
        Hull = 0,
        Propulsion = 1,
        FTL = 2,
    }
}
