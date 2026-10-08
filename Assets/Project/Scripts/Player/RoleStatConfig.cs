using System;
using UnityEngine;

/// <summary>
/// RoleStatConfig — modificatori di statistica per ruolo (Rev BV-a · workshop Quartermaster, Q93-a).
/// ScriptableObject di tuning, convenzione del progetto: nessun valore numerico nel codice.
/// Crea asset: Assets &gt; Create &gt; SpaceSurvivor &gt; Role Stat Config
///
/// REGOLA (Q93-a): le percentuali del ruolo si sommano a quelle degli stati attivi nello stesso
/// calcolo di PlayerStatusEffects (Q40-a: per statistica le percentuali si sommano, il
/// moltiplicatore 1 + somma/100 viene poi limitato da StatModifierLimits). Il ruolo è replicato
/// (PlayerCrewRole), quindi server e client ottengono lo stesso moltiplicatore senza
/// NetworkVariable nuove.
///
/// VALORI DI PARTENZA (scheda M3 del Quartermaster): HP max +50%, velocità −15%. Gli altri
/// ruoli non hanno voci (identità): questo è anche il punto in cui arriveranno i bonus/malus di
/// statistica degli altri ruoli.
///
/// NON QUI (Q94-a): "−50% recupero della stamina in combattimento" del Quartermaster. Non esiste
/// uno stato di combattimento né una statistica per la stamina: si progetta con il Combat.
///
/// NAMESPACE GLOBALE: dominio player, come StatusEffectData / StatModifierLimits.
///
/// ⚠️ VERIFICA EDITOR: assegnare l'asset al campo "Role Stats" di PlayerStatusEffects sul
/// Player prefab. Senza asset nessun ruolo modifica le statistiche (errore a log sul server).
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/Role Stat Config", fileName = "RoleStatConfig")]
public class RoleStatConfig : ScriptableObject
{
    /// <summary>Modificatori di statistica di un ruolo.</summary>
    [Serializable]
    public struct RoleEntry
    {
        [Tooltip("Ruolo a cui si applicano i modificatori. Una voce per ruolo; un ruolo senza voce non " +
                 "modifica nulla.")]
        public CrewRole role;

        [Tooltip("Variazioni percentuali (+50 = +50%). Si sommano a quelle degli stati attivi, poi valgono i limiti " +
                 "di StatModifierLimits.")]
        public StatModifierEntry[] modifiers;
    }

    [Header("Modificatori per ruolo — Q93-a")]
    [Tooltip("Una voce per ruolo. Default: Quartermaster HP max +50%, velocità −15% (scheda M3).")]
    [SerializeField] private RoleEntry[] roles =
    {
        new RoleEntry
        {
            role = CrewRole.Quartermaster,
            modifiers = new[]
            {
                new StatModifierEntry { stat = StatKind.MaxHP, percent = 50f },
                new StatModifierEntry { stat = StatKind.MoveSpeed, percent = -15f }
            }
        }
    };

    /// <summary>
    /// Somma alle percentuali indicate (indice = (int)StatKind) quelle del ruolo. Un ruolo senza
    /// voce, o None, non aggiunge nulla. Voci duplicate dello stesso ruolo si sommano.
    /// </summary>
    public void AddRolePercents(CrewRole role, float[] percentSums)
    {
        if (role == CrewRole.None || roles == null || percentSums == null) return;

        for (int r = 0; r < roles.Length; r++)
        {
            if (roles[r].role != role || roles[r].modifiers == null) continue;

            StatModifierEntry[] modifiers = roles[r].modifiers;
            for (int m = 0; m < modifiers.Length; m++)
            {
                int stat = (int)modifiers[m].stat;
                if (stat < 0 || stat >= percentSums.Length) continue;
                percentSums[stat] += modifiers[m].percent;
            }
        }
    }
}
