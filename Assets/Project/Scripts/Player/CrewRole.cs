/// <summary>
/// CrewRole — identità di ruolo canonica di un membro dell'equipaggio (Rev BM).
///
/// PERCHÉ ESISTE: fino a Rev BL il ruolo viveva SOLO come stringa client-side in
/// LocalCharacterProfile ("Pilota", "Ingegnere", …) e non arrivava mai al server.
/// Tutti i seam di ruolo (ProgressiveMinigame.GetRole*, ScannerSystem.GetRoleTierBonus/
/// GetRoleCooldownMultiplier, PlayerReviveTarget.ResolveProfile) erano quindi
/// costretti all'identità. Questo enum è la chiave type-safe su cui il codice fa
/// switch; la stringa resta solo come dato persistito/etichetta UI.
///
/// Replica: vedi PlayerCrewRole (NetworkVariable&lt;CrewRole&gt; per-player).
///
/// NAMESPACE GLOBALE: coerenza-di-dominio con i sibling player (PlayerHealthSystem,
/// PlayerStatusEffects, PlayerReviveTarget) — deviazione già documentata in Rev BC.
///
/// Byte-backed (come PlayerHealthSystem.LifeState) → NetworkVariable/RPC compatti.
/// ⚠️ I valori numerici sono persistenti in rete: aggiungere in coda, mai rinumerare.
/// </summary>
public enum CrewRole : byte
{
    None          = 0,   // non assegnato / sconosciuto → hook di ruolo a identità
    Pilot         = 1,
    Engineer      = 2,
    Scanner       = 3,
    Corpsman      = 4,   // ex "Medico" (rinomina M3 close)
    Quartermaster = 5    // quinto ruolo (M3 close) — contenuto in Fase 3b
}

/// <summary>
/// CrewRoles — conversioni stringa ↔ CrewRole. FONTE UNICA della mappatura:
/// MainMenuManager (bottoni creazione), RoleColors (colori) e PlayerCrewRole
/// (dichiarazione al server) passano tutti da qui.
///
/// LEGACY: i profili creati prima di Rev BM hanno role = "Medico". Parse lo
/// riconosce come alias di Corpsman — nessuna migrazione del file di salvataggio
/// (non distruttivo). L'etichetta mostrata per quei profili resta "Medico" finché
/// il personaggio non viene ricreato (cosmetico, noto).
/// </summary>
public static class CrewRoles
{
    /// <summary>Etichetta per ruolo assente — coincide col default di LocalCharacterProfile.</summary>
    public const string UnassignedLabel = "Non assegnato";

    /// <summary>
    /// Ruoli selezionabili alla creazione personaggio, NELL'ORDINE dei bottoni di
    /// MainMenuManager (Pilota, Ingegnere, Scanner, Corpsman, Quartermaster).
    /// </summary>
    private static readonly CrewRole[] Selectable =
    {
        CrewRole.Pilot,
        CrewRole.Engineer,
        CrewRole.Scanner,
        CrewRole.Corpsman,
        CrewRole.Quartermaster
    };

    /// <summary>Numero di ruoli selezionabili (= numero di bottoni ruolo attesi).</summary>
    public static int SelectableCount => Selectable.Length;

    /// <summary>Ruolo selezionabile all'indice dato; None se fuori range.</summary>
    public static CrewRole GetSelectable(int index)
    {
        if (index < 0 || index >= Selectable.Length) return CrewRole.None;
        return Selectable[index];
    }

    /// <summary>Etichetta UI canonica (italiano) — è anche la stringa persistita nel profilo.</summary>
    public static string ToDisplayName(CrewRole role)
    {
        switch (role)
        {
            case CrewRole.Pilot:         return "Pilota";
            case CrewRole.Engineer:      return "Ingegnere";
            case CrewRole.Scanner:       return "Scanner";
            case CrewRole.Corpsman:      return "Corpsman";
            case CrewRole.Quartermaster: return "Quartermaster";
            default:                     return UnassignedLabel;
        }
    }

    /// <summary>
    /// Converte la stringa persistita (LocalCharacterProfile.Role) nel ruolo.
    /// Case-insensitive, tollera spazi. "Medico" = alias legacy di Corpsman.
    /// Stringa vuota/sconosciuta/"Non assegnato" → None.
    /// </summary>
    public static CrewRole Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return CrewRole.None;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "pilota":        return CrewRole.Pilot;
            case "ingegnere":     return CrewRole.Engineer;
            case "scanner":       return CrewRole.Scanner;
            case "corpsman":
            case "medico":        return CrewRole.Corpsman;   // alias legacy (pre Rev BM)
            case "quartermaster": return CrewRole.Quartermaster;
            default:              return CrewRole.None;
        }
    }

    /// <summary>
    /// true se il valore è un membro definito dell'enum. Usato dal server per
    /// validare un ruolo ricevuto via RPC (un byte arbitrario → None).
    /// </summary>
    public static bool IsValid(CrewRole role) => role <= CrewRole.Quartermaster;
}
