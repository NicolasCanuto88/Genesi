using UnityEngine;

/// <summary>
/// RoleColors — Milestone 3, Blocco 1.
///
/// Fonte unica dei colori identificativi per ruolo (Pilota/Ingegnere/Scanner/
/// Medico), usata sia da MainMenuManager (badge personaggio nel MainMenuPanel)
/// sia da CharacterEntryUI (left stripe nella lista CharacterSelectPanel).
///
/// Prima di questo file i colori erano duplicati come SerializeField separati
/// in MainMenuManager — centralizzati qui per evitare che badge, dot e stripe
/// possano andare fuori sincrono se modificati in un punto solo.
///
/// Valori allineati alla palette del mockup estetico concordato: Pilota
/// cyan (#00C8EF), Ingegnere amber (#E08020), Scanner viola (#8850D0),
/// Medico verde (#00C87A).
/// </summary>
public static class RoleColors
{
    public static readonly Color Pilota    = new Color(0.000f, 0.784f, 0.937f); // #00C8EF
    public static readonly Color Ingegnere = new Color(0.878f, 0.502f, 0.125f); // #E08020
    public static readonly Color Scanner   = new Color(0.533f, 0.314f, 0.816f); // #8850D0
    public static readonly Color Medico    = new Color(0.000f, 0.784f, 0.478f); // #00C87A

    /// <summary>Grigio neutro per ruolo non assegnato o sconosciuto.</summary>
    public static readonly Color Default = new Color(0.4f, 0.4f, 0.4f, 1f);

    public static Color Get(string ruolo)
    {
        switch (ruolo)
        {
            case "Pilota":    return Pilota;
            case "Ingegnere": return Ingegnere;
            case "Scanner":   return Scanner;
            case "Medico":    return Medico;
            default:          return Default;
        }
    }
}
