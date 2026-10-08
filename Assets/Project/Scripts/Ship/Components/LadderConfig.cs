using UnityEngine;

/// <summary>
/// LadderConfig — valori di bilanciamento delle scale (Rev BW-c · L6).
/// ScriptableObject secondo la convenzione del progetto ("tutti i valori numerici di tuning vengono
/// da SO, mai hardcodati"). Un solo asset, condiviso da tutte le scale della nave.
///
/// I default coincidono con i valori serializzati sulle due scale di Game.unity prima di Rev BW-c
/// (velocità 3, distanza di aggancio 0,8) e con lo spostamento fisso del vecchio codice (0,5 m):
/// creare l'asset senza toccarlo lascia la salita invariata.
///
/// FUORI DA QUI (come Q72-a per PlayerController): moltiplicatore e limiti della visuale in salita
/// restano sul componente Ladder, perché sono comfort dell'utente; la distanza della camera dalla
/// scala resta sul componente perché è geometria della singola scala.
///
/// NAMESPACE GLOBALE: come Ladder.
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/Ladder Config", fileName = "LadderConfig")]
public class LadderConfig : ScriptableObject
{
    [Header("Salita")]
    [Tooltip("Velocità di salita e discesa (m/s).")]
    [Min(0f)]
    [SerializeField] private float climbSpeed = 3f;

    [Header("Uscita (Rev BW-c · Q113-a)")]
    [Tooltip("Entro questa distanza verticale (m) da un pianerottolo, Interact fa scendere sul " +
             "pianerottolo. Più lontano, Interact lascia la presa e si cade.")]
    [Min(0f)]
    [SerializeField] private float exitReach = 0.8f;

    [Tooltip("Spostamento (m) verso la scala applicato al punto di uscita quando si scende su un " +
             "pianerottolo (il vecchio valore fisso era 0,5).")]
    [Min(0f)]
    [SerializeField] private float exitForwardOffset = 0.5f;

    [Tooltip("Quando si lascia la presa a metà scala, il corpo si stacca di questi metri dalla scala " +
             "prima di cadere, così il CharacterController non riparte compenetrato.")]
    [Min(0f)]
    [SerializeField] private float letGoBackOffset = 0.15f;

    [Header("A terra sulla scala (Rev BW-c · L4)")]
    [Tooltip("Chi va a terra sulla scala viene portato al pianerottolo e poi appoggiato al pavimento " +
             "con un movimento del CharacterController verso il basso, lungo al massimo questi metri. " +
             "A terra PlayerController è spento e la gravità non agisce: senza questo il corpo resta " +
             "sospeso all'altezza del punto di uscita.")]
    [Min(0f)]
    [SerializeField] private float downedGroundSnap = 2f;

    // ===== Getter pubblici =====

    public float ClimbSpeed => climbSpeed;
    public float ExitReach => exitReach;
    public float ExitForwardOffset => exitForwardOffset;
    public float LetGoBackOffset => letGoBackOffset;
    public float DownedGroundSnap => downedGroundSnap;
}