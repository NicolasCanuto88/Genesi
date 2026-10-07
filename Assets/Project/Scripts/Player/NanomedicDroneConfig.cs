using UnityEngine;

/// <summary>
/// NanomedicDroneConfig — parametri del Nanomedic Drone del Corpsman (Rev BU-b · workshop Corpsman
/// T3–T4, Q86-a / Q87-a). ScriptableObject secondo la convenzione del progetto: nessun valore di
/// tuning hardcodato nel codice.
///
/// NAMESPACE GLOBALE: coerenza di dominio con MedKitConfig e DefibConfig (SO di dominio player).
///
/// CONTENUTO:
///   - CURA (Q86-a): HP al secondo con effetto pieno (Corpsman), durata del drone, cadenza dei tick
///     sul server. Un non-Corpsman applica il moltiplicatore del profilo di default di
///     MedKitConfig (0.6), come la bomba curativa.
///   - VISUALE: prefab solo visivo (senza collider) che resta sopra la spalla del bersaglio, con
///     inseguimento morbido e una leggera oscillazione. Ogni client la anima da solo partendo dal
///     bersaglio replicato: nessun traffico di rete per il movimento.
///
/// NON QUI (di proposito):
///   - capienza nel kit e tier minimo all'armadietto: in MedKitConfig, come per gli altri pezzi del kit;
///   - chi può essere bersaglio (regola Q31-a del kit): costante di design in PlayerNanomedicDrone.
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
    public GameObject VisualPrefab => visualPrefab;
    public float FollowHeight => followHeight;
    public float FollowSide => followSide;
    public float FollowBack => followBack;
    public float FollowSharpness => Mathf.Max(0.1f, followSharpness);
    public float BobAmplitude => Mathf.Max(0f, bobAmplitude);
    public float BobFrequency => Mathf.Max(0f, bobFrequency);
    public float SpinDegreesPerSecond => spinDegreesPerSecond;
}
