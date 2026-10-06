using SpaceSurvivor.Ship;
using UnityEngine;

/// <summary>
/// Effetto di un lanciabile alla detonazione (Rev BS-a · Q51-a). Enum con switch sul server
/// (ThrowableSystem), come EffectKind degli stati: un tipo nuovo di lanciabile aggiunge un
/// valore IN CODA, mai riordinare (viaggia come byte nelle RPC tramite il catalogo).
/// </summary>
public enum ThrowEffectKind : byte
{
    None = 0,          // Rev BS-a: solo l'area visiva, nessun effetto di gioco
    HealingField = 1   // Rev BS-b: campo che cura chi ci sta dentro (bomba curativa del Corpsman)
}

/// <summary>
/// ThrowableData — parametri di un oggetto lanciabile (Rev BS-a, workshop bomba curativa +
/// framework di lancio Q51–Q62). ScriptableObject secondo la convenzione del progetto
/// ("tutti i valori numerici di tuning vengono da SO, mai hardcodati").
///
/// NAMESPACE GLOBALE: coerenza di dominio con MedKitConfig e con i sibling player.
///
/// CONTENUTO:
///   - LANCIO: velocità, alzo aggiunto allo sguardo, intervallo minimo tra due lanci.
///   - VOLO (Q52-a · Q54-a · Q62-a): parabola con gravità propria nel riferimento della nave
///     (Q53-a: spazio Unity), sfera di raggio projectileRadius controllata con uno sweep contro
///     tutto ciò che sta in collisionMask (pareti, porte, console, giocatori, in futuro NPC).
///     Chi lancia è sempre ignorato. Rimbalzi supportati (maxBounces, restitution) ma la bomba
///     curativa ne usa 0: esplode al primo contatto.
///   - AREA (Q55-b): raggio e durata dell'area che resta dopo la detonazione.
///   - CAMPO CURATIVO (Rev BS-b · Q55-b · Q63-a): HP al secondo, a tick, a ogni giocatore vivo con
///     una parte del corpo nel raggio e in linea di vista dal centro (pareti e porte bloccano, i
///     corpi no). La cura passa da PlayerHealthSystem.ApplyHeal (solo Alive, HP max dinamico) ed è
///     moltiplicata per il profilo di ruolo di chi lancia, preso al lancio (Q57-a).
///   - SORGENTE (Rev BS-b · Q65-a): il pezzo del kit medico consumato a ogni lancio. Altre sorgenti
///     (Armeria del Quartermaster) arriveranno con i loro lanciabili.
///   - VISUALE (Q60-a): prefab LOCALI, mai di rete. ThrowableSystem e PlayerThrower li
///     istanziano su ogni client e ne disattivano i collider: un collider sulla visuale
///     fermerebbe gli sweep.
///
/// Ogni ThrowableData lanciato DEVE stare nel catalogo di ThrowableSystem (scena Game):
/// le RPC viaggiano con l'indice del catalogo. Fuori catalogo → errore a log, nessun lancio.
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/Throwable Data", fileName = "TD_NewThrowable")]
public class ThrowableData : ScriptableObject
{
    [Header("Identità")]
    [Tooltip("Nome di gioco (testi in inglese), usato nelle righe di feedback.")]
    [SerializeField] private string displayName = "Throwable";

    [Header("Lancio")]
    [Tooltip("Velocità iniziale, in m/s, lungo la direzione di lancio.")]
    [Min(0.1f)]
    [SerializeField] private float launchSpeed = 10f;

    [Tooltip("Gradi aggiunti all'inclinazione dello sguardo: guardando dritto davanti la bomba parte " +
             "un po' verso l'alto. L'alzo risultante resta tra −89° e +89°.")]
    [Range(-30f, 45f)]
    [SerializeField] private float aimPitchOffsetDegrees = 10f;

    [Tooltip("Secondi minimi tra due lanci dello stesso giocatore (controllati da client e server).")]
    [Min(0f)]
    [SerializeField] private float throwCooldownSeconds = 0.75f;

    [Header("Volo — Q52-a · Q54-a")]
    [Tooltip("Accelerazione di gravità, in m/s², verso il basso del riferimento della nave.")]
    [Min(0f)]
    [SerializeField] private float gravity = 9.81f;

    [Tooltip("Raggio della sfera usata per gli sweep, in metri.")]
    [Min(0.01f)]
    [SerializeField] private float projectileRadius = 0.08f;

    [Tooltip("Durata massima del volo, in secondi. Allo scadere la bomba detona dove si trova.")]
    [Min(0.1f)]
    [SerializeField] private float maxFlightSeconds = 3f;

    [Tooltip("Rimbalzi prima della detonazione. 0 = detona al primo contatto (bomba curativa, Q54-a).")]
    [Min(0)]
    [SerializeField] private int maxBounces = 0;

    [Tooltip("Frazione della velocità conservata a ogni rimbalzo.")]
    [Range(0f, 1f)]
    [SerializeField] private float restitution = 0.4f;

    [Tooltip("Sotto questa velocità, in m/s, un rimbalzo diventa detonazione.")]
    [Min(0f)]
    [SerializeField] private float minBounceSpeed = 1.5f;

    [Tooltip("Layer contro cui vola la bomba. Pareti, porte e corpi (CharacterController) devono " +
             "restarci. Default: tutto tranne Ignore Raycast e UI. I trigger sono sempre ignorati.")]
    [SerializeField] private LayerMask collisionMask = ~((1 << 2) | (1 << 5));

    [Header("Sorgente — Rev BS-b · Q65-a")]
    [Tooltip("Pezzo del kit medico personale (PlayerMedKit) consumato a ogni lancio partito.")]
    [SerializeField] private ItemType kitItem = ItemType.HealingGrenade;

    [Header("Effetto e area — Q55-b")]
    [Tooltip("Effetto alla detonazione. None = solo l'area visiva; HealingField = campo che cura (Rev BS-b).")]
    [SerializeField] private ThrowEffectKind effectKind = ThrowEffectKind.None;

    [Tooltip("Raggio dell'area, in metri.")]
    [Min(0.1f)]
    [SerializeField] private float areaRadius = 3f;

    [Tooltip("Durata dell'area, in secondi. 0 = nessuna area (solo detonazione).")]
    [Min(0f)]
    [SerializeField] private float areaDurationSeconds = 8f;

    [Header("Campo curativo (HealingField) — Rev BS-b · Q63-a")]
    [Tooltip("HP al secondo con effetto pieno (Corpsman). Chi non è Corpsman applica il moltiplicatore del " +
             "profilo di default del kit (0.6).")]
    [Min(0f)]
    [SerializeField] private float healPerSecond = 5f;

    [Tooltip("Secondi tra due tick di cura. Il primo tick è alla detonazione.")]
    [Min(0.05f)]
    [SerializeField] private float healTickSeconds = 0.5f;

    [Header("Visuale (prefab locali, NON di rete) — Q60-a")]
    [Tooltip("Bomba in volo. Senza collider (vengono comunque disattivati all'istanza).")]
    [SerializeField] private GameObject projectileVisualPrefab;

    [Tooltip("Area dopo la detonazione: una sfera di DIAMETRO 1, scalata a 2 × raggio dell'area. Il colore " +
             "(_BaseColor) sfuma negli ultimi istanti.")]
    [SerializeField] private GameObject areaVisualPrefab;

    [Tooltip("Marcatore del punto d'arrivo durante la mira (solo chi lancia). Il suo asse Y si allinea " +
             "alla normale della superficie colpita.")]
    [SerializeField] private GameObject aimMarkerPrefab;

    [Tooltip("Materiale dell'arco di mira (LineRenderer creato a runtime, solo chi lancia).")]
    [SerializeField] private Material aimArcMaterial;

    [Tooltip("Spessore dell'arco di mira, in metri.")]
    [Min(0.001f)]
    [SerializeField] private float aimArcWidth = 0.03f;

    [Tooltip("Frazione finale della durata dell'area in cui la visuale sfuma (0 = nessuna sfumatura).")]
    [Range(0f, 1f)]
    [SerializeField] private float areaFadeFraction = 0.25f;

    [Header("Feedback a schermo")]
    [Tooltip("Secondi per cui resta visibile la riga di esito del lancio.")]
    [Min(0f)]
    [SerializeField] private float feedbackHoldSeconds = 2f;

    public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;

    public float LaunchSpeed => Mathf.Max(0.1f, launchSpeed);
    public float AimPitchOffsetDegrees => Mathf.Clamp(aimPitchOffsetDegrees, -30f, 45f);
    public float ThrowCooldownSeconds => Mathf.Max(0f, throwCooldownSeconds);

    public float Gravity => Mathf.Max(0f, gravity);
    public float ProjectileRadius => Mathf.Max(0.01f, projectileRadius);
    public float MaxFlightSeconds => Mathf.Max(0.1f, maxFlightSeconds);
    public int MaxBounces => Mathf.Clamp(maxBounces, 0, 16);
    public float Restitution => Mathf.Clamp01(restitution);
    public float MinBounceSpeed => Mathf.Max(0f, minBounceSpeed);
    public int CollisionMask => collisionMask.value;

    public ItemType KitItem => kitItem;

    public ThrowEffectKind EffectKind => effectKind;
    public float AreaRadius => Mathf.Max(0.1f, areaRadius);
    public float AreaDurationSeconds => Mathf.Max(0f, areaDurationSeconds);
    public float HealPerSecond => Mathf.Max(0f, healPerSecond);
    public float HealTickSeconds => Mathf.Max(0.05f, healTickSeconds);

    public GameObject ProjectileVisualPrefab => projectileVisualPrefab;
    public GameObject AreaVisualPrefab => areaVisualPrefab;
    public GameObject AimMarkerPrefab => aimMarkerPrefab;
    public Material AimArcMaterial => aimArcMaterial;
    public float AimArcWidth => Mathf.Max(0.001f, aimArcWidth);
    public float AreaFadeFraction => Mathf.Clamp01(areaFadeFraction);

    public float FeedbackHoldSeconds => Mathf.Max(0f, feedbackHoldSeconds);
}