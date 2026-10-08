using System;
using UnityEngine;

/// <summary>
/// QuartermasterGadgetConfig — parametri dei gadget del Quartermaster (Rev BV-b · workshop
/// Quartermaster, Q95-a, Q96-a, Q101-a). ScriptableObject secondo la convenzione del progetto:
/// nessun valore di tuning hardcodato nel codice.
/// Crea asset: Assets &gt; Create &gt; SpaceSurvivor &gt; Quartermaster Gadget Config
///
/// TIER (Q101-a): durata e ricarica dipendono dal tier. L'Armeria, che darà il tier vero, arriva
/// con il Combat (Q98-a): fino ad allora vale testTier di questo asset (default T2, così al test
/// ci sono sia lo scudo personale sia la bolla).
///
/// TABELLE (indice 0 = T1 … 3 = T4), valori di Q101-a:
///   - Scudo Personale: 6 s / 45 s · 7 s / 40 s · 8 s / 35 s · 10 s / 30 s;
///   - Bubble Shield:   —        · 8 s / 60 s · 10 s / 55 s · 12 s / 50 s (T1 non ce l'ha: durata 0).
/// La ricarica parte dall'attivazione (per la bolla: dal lancio). Le due ricariche sono separate.
///
/// COMANDI (Rev BV-c · Q97-a): un solo tasto (Shield, F / Y). Tocco sotto tapThresholdSeconds =
/// Scudo Personale al rilascio; pressione più lunga = mira della Bubble Shield, lancio al rilascio.
/// La bolla è il lanciabile bubbleShieldThrowable (TD_BubbleShield), lanciato con il framework di
/// lancio (ThrowableSystem): deve stare anche nel catalogo di ThrowableSystem in Game.unity.
///
/// COSA PARANO (Q95/Q99): solo i proiettili. Prima del Combat lo scudo è uno stato a tempo che si
/// vede (film) e non cambia il danno: stati e soffocamento passano.
///
/// NAMESPACE GLOBALE: dominio player, come MedKitConfig / NanomedicDroneConfig.
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/Quartermaster Gadget Config", fileName = "QuartermasterGadgetConfig")]
public class QuartermasterGadgetConfig : ScriptableObject
{
    public const int MinTier = 1;
    public const int MaxTier = 4;

    /// <summary>Durata e ricarica di un gadget a un tier.</summary>
    [Serializable]
    public struct GadgetTier
    {
        [Tooltip("Durata in secondi. 0 = gadget non disponibile a questo tier.")]
        [Min(0f)] public float durationSeconds;

        [Tooltip("Ricarica in secondi, dall'attivazione.")]
        [Min(0f)] public float cooldownSeconds;

        public GadgetTier(float durationSeconds, float cooldownSeconds)
        {
            this.durationSeconds = durationSeconds;
            this.cooldownSeconds = cooldownSeconds;
        }
    }

    [Header("Tier (fino all'Armeria — Q98-a / Q101-a)")]
    [Tooltip("Tier dei gadget finché l'Armeria non esiste. T2: scudo personale e bolla disponibili.")]
    [Range(MinTier, MaxTier)]
    [SerializeField] private int testTier = 2;

    [Header("Comandi — Rev BV-c · Q97-a")]
    [Tooltip("Pressione di Shield più breve di questa soglia, in secondi = tocco: Scudo Personale al rilascio. " +
             "Oltre la soglia si apre la mira della Bubble Shield e la bolla parte al rilascio.")]
    [Min(0.05f)]
    [SerializeField] private float tapThresholdSeconds = 0.3f;

    [Header("Bubble Shield — lanciabile (Rev BV-c)")]
    [Tooltip("Asset ThrowableData della bolla (TD_BubbleShield, Effect Kind = BubbleShield). Deve stare anche nel " +
             "catalogo di ThrowableSystem (Game.unity). La durata dell'area viene dalla tabella qui sotto.")]
    [SerializeField] private ThrowableData bubbleShieldThrowable;

    [Header("Scudo Personale — Q95-a (indice 0 = T1 … 3 = T4)")]
    [Tooltip("Durata e ricarica per tier. Valori di Q101-a.")]
    [SerializeField]
    private GadgetTier[] personalShield =
    {
        new GadgetTier(6f, 45f),
        new GadgetTier(7f, 40f),
        new GadgetTier(8f, 35f),
        new GadgetTier(10f, 30f)
    };

    [Header("Bubble Shield — Q96-a (indice 0 = T1 … 3 = T4)")]
    [Tooltip("Durata e ricarica per tier. T1 senza bolla (durata 0). Valori di Q101-a.")]
    [SerializeField]
    private GadgetTier[] bubbleShield =
    {
        new GadgetTier(0f, 0f),
        new GadgetTier(8f, 60f),
        new GadgetTier(10f, 55f),
        new GadgetTier(12f, 50f)
    };

    /// <summary>Tier corrente dei gadget (oggi testTier, domani dall'Armeria).</summary>
    public int CurrentTier => Mathf.Clamp(testTier, MinTier, MaxTier);

    /// <summary>Rev BV-c — soglia tra tocco (scudo personale) e pressione tenuta (mira della bolla).</summary>
    public float TapThresholdSeconds => Mathf.Max(0.05f, tapThresholdSeconds);

    /// <summary>Rev BV-c — lanciabile della Bubble Shield (può essere null: bolla non disponibile).</summary>
    public ThrowableData BubbleShieldThrowable => bubbleShieldThrowable;

    /// <summary>Scudo personale al tier indicato. false se non disponibile (durata 0 o tabella corta).</summary>
    public bool TryGetPersonalShield(int tier, out float durationSeconds, out float cooldownSeconds)
        => TryGet(personalShield, tier, out durationSeconds, out cooldownSeconds);

    /// <summary>Bubble Shield al tier indicato. false se non disponibile (T1, durata 0 o tabella corta).</summary>
    public bool TryGetBubbleShield(int tier, out float durationSeconds, out float cooldownSeconds)
        => TryGet(bubbleShield, tier, out durationSeconds, out cooldownSeconds);

    private static bool TryGet(GadgetTier[] table, int tier, out float durationSeconds, out float cooldownSeconds)
    {
        durationSeconds = 0f;
        cooldownSeconds = 0f;

        int index = Mathf.Clamp(tier, MinTier, MaxTier) - 1;
        if (table == null || index >= table.Length) return false;

        durationSeconds = Mathf.Max(0f, table[index].durationSeconds);
        cooldownSeconds = Mathf.Max(0f, table[index].cooldownSeconds);
        return durationSeconds > 0f;
    }
}