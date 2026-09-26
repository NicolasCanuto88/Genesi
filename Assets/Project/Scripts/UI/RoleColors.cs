using UnityEngine;

/// <summary>
/// RoleColors — Milestone 3, Blocco 1 · aggiornato Rev BM.
///
/// Fonte unica dei colori identificativi per ruolo, usata sia da MainMenuManager
/// (badge personaggio nel MainMenuPanel) sia da CharacterEntryUI (left stripe
/// nella lista CharacterSelectPanel).
///
/// Prima di questo file i colori erano duplicati come SerializeField separati
/// in MainMenuManager — centralizzati qui per evitare che badge, dot e stripe
/// possano andare fuori sincrono se modificati in un punto solo.
///
/// Valori allineati alla palette del mockup estetico concordato: Pilota
/// cyan (#00C8EF), Ingegnere amber (#E08020), Scanner viola (#8850D0),
/// Corpsman verde (#00C87A — ex Medico, stesso colore).
///
/// REV BM:
///   - "Medico" → "Corpsman" (rinomina M3 close). Il colore resta invariato.
///   - Quartermaster aggiunto: verde oliva (#8FA33A) — PROVVISORIO, fuori dalla
///     palette del mockup (che precede il quinto ruolo). Tinta scelta per distanza
///     dagli altri quattro (Ingegnere ~30°, Corpsman ~155°, Pilota ~190°,
///     Scanner ~266° → Quartermaster ~71°). Da ratificare col mockup ufficiale.
///   - La stringa viene convertita con CrewRoles.Parse (fonte unica della
///     mappatura): i profili legacy con "Medico" ottengono il colore Corpsman.
/// </summary>
public static class RoleColors
{
    public static readonly Color Pilota = new Color(0.000f, 0.784f, 0.937f); // #00C8EF
    public static readonly Color Ingegnere = new Color(0.878f, 0.502f, 0.125f); // #E08020
    public static readonly Color Scanner = new Color(0.533f, 0.314f, 0.816f); // #8850D0
    public static readonly Color Corpsman = new Color(0.000f, 0.784f, 0.478f); // #00C87A (ex Medico)
    public static readonly Color Quartermaster = new Color(0.561f, 0.639f, 0.227f); // #8FA33A (provvisorio)

    /// <summary>Grigio neutro per ruolo non assegnato o sconosciuto.</summary>
    public static readonly Color Default = new Color(0.4f, 0.4f, 0.4f, 1f);

    /// <summary>Colore per ruolo tipizzato (Rev BM).</summary>
    public static Color Get(CrewRole role)
    {
        switch (role)
        {
            case CrewRole.Pilot: return Pilota;
            case CrewRole.Engineer: return Ingegnere;
            case CrewRole.Scanner: return Scanner;
            case CrewRole.Corpsman: return Corpsman;
            case CrewRole.Quartermaster: return Quartermaster;
            default: return Default;
        }
    }

    /// <summary>
    /// Colore per la stringa ruolo persistita nel profilo (API storica, stessa firma).
    /// Delega a CrewRoles.Parse → alias legacy "Medico" incluso.
    /// </summary>
    public static Color Get(string ruolo) => Get(CrewRoles.Parse(ruolo));
}