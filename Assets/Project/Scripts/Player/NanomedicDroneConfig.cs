using UnityEngine;

/// <summary>
/// NanomedicDroneConfig — parametri del Nanomedic Drone del Corpsman (Rev BU-b · workshop Corpsman
/// T3–T4, Q86-a / Q87-a; aggancio Rev BU-d · Q92-a). ScriptableObject secondo la convenzione del
/// progetto: nessun valore di tuning hardcodato nel codice.
///
/// NAMESPACE GLOBALE: coerenza di dominio con MedKitConfig e DefibConfig (SO di dominio player).
///
/// CONTENUTO:
///   - CURA (Q86-a): HP al secondo con effetto pieno (Corpsman), durata del drone, cadenza dei tick
///     sul server. Un non-Corpsman applica il moltiplicatore del profilo di default di
///     MedKitConfig (0.6), come la bomba curativa.
///   - AGGANCIO (Rev BU-d · Q92-a): soglia del tocco (sotto = drone su se stessi), angolo massimo dal
///     centro del mirino e portata dell'aggancio di un compagno tenendo premuto.
///   - VISUALE: prefab solo visivo (senza collider) che resta sopra la spalla del bersaglio, con
///     inseguimento morbido e una leggera oscillazione. Ogni client la anima da solo partendo dal
///     bersaglio replicato: nessun traffico di rete per il movimento.
///
/// NON QUI (di proposito):
///   - capienza nel kit e tier minimo all'armadietto: in MedKitConfig, come per gli altri pezzi del kit;
///   - chi può essere bersaglio (vivo, non a terra, in linea di vista) e la maschera del raggio di
///     linea di vista: regole di design e layer, in PlayerNanomedicDrone.
///
/// CAMPI NUOVI E ASSET: i campi aggiunti in Rev BU-d non sono scritti nell'asset finché non lo si
/// salva; fino ad allora valgono i default del codice (0,3 s, 10°, 15 m), in Editor, nei cloni e
/// nelle build.
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/Nanomedic Drone Config", fileName = "NanomedicDroneConfig")]
public class NanomedicDroneConfig : ScriptableObject
{
    [Header("Cura — Q86-a")]
    [Tooltip("HP curati al secondo con effetto pieno (Corpsman). Un non-Corpsman applica il moltiplicatore del " +
             "profilo di default di MedKitConfig (0.6 → 1,2 HP/s).")]
    [Min(0f)]
    [SerializeField] private float healPerSecond = 2f;

    [Tooltip("Durata del drone in secondi. Corpsman: 2 HP/s × 30 s = 60 HP al massimo.")]
    [Min(1f)]
    [SerializeField] private float durationSeconds = 30f;

    [Tooltip("Cadenza dei tick di cura sul server, in secondi (come l'auto-cura della Recovery Bay: evita di " +
             "scrivere gli HP replicati a ogni frame).")]
    [Min(0.05f)]
    [SerializeField] private float healTickInterval = 0.5f;

    [Header("Aggancio del bersaglio — Rev BU-d · Q92-a")]
    [Tooltip("Pressione più breve di questa soglia, in secondi = tocco: drone su se stessi. Oltre la soglia si " +
             "apre l'aggancio di un compagno e il drone parte al rilascio.")]
    [Min(0.05f)]
    [SerializeField] private float tapThresholdSeconds = 0.3f;

    [Tooltip("Angolo massimo, in gradi, tra il centro del mirino e il centro del corpo del compagno da agganciare.")]
    [Range(1f, 45f)]
    [SerializeField] private float lockAngleDegrees = 10f;

    [Tooltip("Portata massima dell'aggancio, in metri, dalla camera al centro del corpo del compagno.")]
    [Min(1f)]
    [SerializeField] private float lockRange = 15f;

    [Header("Visuale (solo client, senza collider)")]
    [Tooltip("Prefab SOLO visivo del drone. Eventuali collider vengono disattivati all'istanza.")]
    [SerializeField] private GameObject visualPrefab;

    [Tooltip("Altezza del drone sopra i piedi del bersaglio, in metri (capsula del Player: 1,8 m).")]
    [SerializeField] private float followHeight = 1.95f;

    [Tooltip("Spostamento laterale verso la destra del bersaglio, in metri.")]
    [SerializeField] private float followSide = 0.45f;

    [Tooltip("Spostamento all'indietro rispetto allo sguardo del bersaglio, in metri.")]
    [SerializeField] private float followBack = 0.15f;

    [Tooltip("Rapidità dell'inseguimento (più alto = più rigido). Inseguimento esponenziale, indipendente dal frame rate.")]
    [Min(0.1f)]
    [SerializeField] private float followSharpness = 8f;

    [Tooltip("Ampiezza dell'oscillazione verticale, in metri.")]
    [Min(0f)]
    [SerializeField] private float bobAmplitude = 0.05f;

    [Tooltip("Frequenza dell'oscillazione verticale, in Hz.")]
    [Min(0f)]
    [SerializeField] private float bobFrequency = 1.5f;

    [Tooltip("Velocità di rotazione del drone su se stesso, in gradi al secondo.")]
    [SerializeField] private float spinDegreesPerSecond = 90f;

    public float HealPerSecond => Mathf.Max(0f, healPerSecond);
    public float DurationSeconds => Mathf.Max(1f, durationSeconds);
    public float HealTickInterval => Mathf.Max(0.05f, healTickInterval);
    public float TapThresholdSeconds => Mathf.Max(0.05f, tapThresholdSeconds);
    public float LockAngleDegrees => Mathf.Clamp(lockAngleDegrees, 1f, 45f);
    public float LockRange => Mathf.Max(1f, lockRange);
    public GameObject VisualPrefab => visualPrefab;
    public float FollowHeight => followHeight;
    public float FollowSide => followSide;
    public float FollowBack => followBack;
    public float FollowSharpness => Mathf.Max(0.1f, followSharpness);
    public float BobAmplitude => Mathf.Max(0f, bobAmplitude);
    public float BobFrequency => Mathf.Max(0f, bobFrequency);
    public float SpinDegreesPerSecond => spinDegreesPerSecond;
}