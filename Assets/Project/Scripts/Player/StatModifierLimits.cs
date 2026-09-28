using System;
using UnityEngine;

/// <summary>
/// StatModifierLimits — limiti dei moltiplicatori di statistica (Rev BR · Q40-a).
/// ScriptableObject di tuning, convenzione del progetto: nessun valore numerico nel codice.
/// Crea asset: Assets &gt; Create &gt; SpaceSurvivor &gt; Stat Modifier Limits
///
/// REGOLA (Q40-a): per ogni statistica le percentuali degli stati attivi si sommano; il
/// moltiplicatore 1 + somma/100 viene poi limitato all'intervallo [min, max] di questo SO.
/// I limiti proteggono da combinazioni estreme (droghe future che si sommano): con le
/// droghe di BR (Hazmat, Combat Stim) nessun limite viene raggiunto.
///
/// NAMESPACE GLOBALE: dominio player, come StatusEffectData / DefibConfig / MedKitConfig.
///
/// ⚠️ VERIFICA EDITOR: assegnare l'asset al campo "Limits" di PlayerStatusEffects sul
/// Player prefab. Senza asset il componente logga un errore e limita solo i valori
/// impossibili (moltiplicatori negativi, HP max sotto il 10%).
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/Stat Modifier Limits", fileName = "StatModifierLimits")]
public class StatModifierLimits : ScriptableObject
{
    /// <summary>Intervallo ammesso per un moltiplicatore (1 = nessuna modifica).</summary>
    [Serializable]
    public struct Range
    {
        [Tooltip("Moltiplicatore minimo.")] public float min;
        [Tooltip("Moltiplicatore massimo.")] public float max;

        public Range(float min, float max) { this.min = min; this.max = max; }

        public float Clamp(float value) => Mathf.Clamp(value, min, max);
    }

    /// <summary>
    /// Pavimento assoluto per gli HP max, anche senza asset: il massimo non può mai arrivare a
    /// zero (un giocatore vivo a 0 HP max non ha senso). Invariante di sicurezza, non tuning.
    /// </summary>
    public const float AbsoluteMaxHpFloor = 0.1f;

    [Header("Limiti dei moltiplicatori (1 = nessuna modifica)")]
    [Tooltip("Velocità di movimento. Default 0.5–2.")]
    [SerializeField] private Range moveSpeed = new Range(0.5f, 2f);

    [Tooltip("Tutto il danno in ingresso. Default 0–3 (0 = immunità totale).")]
    [SerializeField] private Range damageTaken = new Range(0f, 3f);

    [Tooltip("Solo il danno da Radiazioni (DoT). Default 0–3.")]
    [SerializeField] private Range radiationDamageTaken = new Range(0f, 3f);

    [Tooltip("HP massimi. Default 0.3–2. Il minimo non scende mai sotto 0.1.")]
    [SerializeField] private Range maxHP = new Range(0.3f, 2f);

    /// <summary>Limita il moltiplicatore della statistica indicata.</summary>
    public float Clamp(StatKind stat, float multiplier)
    {
        switch (stat)
        {
            case StatKind.MoveSpeed: return moveSpeed.Clamp(multiplier);
            case StatKind.DamageTaken: return damageTaken.Clamp(multiplier);
            case StatKind.RadiationDamageTaken: return radiationDamageTaken.Clamp(multiplier);
            case StatKind.MaxHP: return Mathf.Max(AbsoluteMaxHpFloor, maxHP.Clamp(multiplier));
            default: return multiplier;
        }
    }

    /// <summary>
    /// Limite minimo senza asset: niente moltiplicatori negativi, HP max almeno al 10%.
    /// Usato da PlayerStatusEffects quando il campo Limits non è assegnato.
    /// </summary>
    public static float ClampFallback(StatKind stat, float multiplier)
        => stat == StatKind.MaxHP ? Mathf.Max(AbsoluteMaxHpFloor, multiplier) : Mathf.Max(0f, multiplier);

#if UNITY_EDITOR
    private void OnValidate()
    {
        Sanitize(ref moveSpeed, 0f);
        Sanitize(ref damageTaken, 0f);
        Sanitize(ref radiationDamageTaken, 0f);
        Sanitize(ref maxHP, AbsoluteMaxHpFloor);
    }

    private static void Sanitize(ref Range r, float floor)
    {
        r.min = Mathf.Max(floor, r.min);
        r.max = Mathf.Max(r.min, r.max);
    }
#endif
}
