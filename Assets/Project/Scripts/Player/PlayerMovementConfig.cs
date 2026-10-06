using UnityEngine;

/// <summary>
/// PlayerMovementConfig — valori di tuning del movimento a piedi (Rev BT-a · Q72-a).
/// ScriptableObject secondo la convenzione del progetto ("tutti i valori numerici di tuning
/// vengono da SO, mai hardcodati"). Stesso modello di PersonalOxygenConfig / MedKitConfig.
///
/// NAMESPACE GLOBALE: coerenza di dominio con PlayerController e i sibling player (deviazione
/// già documentata in Rev BC/BD).
///
/// CONTENUTO: velocità (camminata, scatto, accovacciato), accelerazione e decelerazione,
/// stamina (massimo, consumo durante lo scatto, recupero), gravità e velocità massima di caduta.
/// I default coincidono con i valori serializzati nel Player prefab prima di Rev BT-a: creare
/// l'asset senza toccarlo lascia il movimento invariato.
///
/// FUORI DA QUI (Q72-a): sensibilità, morbidezza e angolo massimo della visuale restano sul
/// componente PlayerController. Sono comfort dell'utente, destinati a un menu di opzioni, non
/// valori di bilanciamento.
///
/// MODIFICATORI: la velocità effettiva è velocità × moltiplicatore MoveSpeed di
/// PlayerStatusEffects (Combat Stim, Rev BR). Questo SO contiene solo i valori di base.
/// </summary>
[CreateAssetMenu(menuName = "SpaceSurvivor/Player Movement Config", fileName = "PlayerMovementConfig")]
public class PlayerMovementConfig : ScriptableObject
{
    [Header("Velocità (m/s)")]
    [Tooltip("Velocità di camminata.")]
    [Min(0f)]
    [SerializeField] private float walkSpeed = 3f;

    [Tooltip("Velocità di scatto (Sprint tenuto premuto, con stamina > 0).")]
    [Min(0f)]
    [SerializeField] private float sprintSpeed = 5.5f;

    [Tooltip("Velocità da accovacciati.")]
    [Min(0f)]
    [SerializeField] private float crouchSpeed = 1.5f;

    [Header("Accelerazione (fattore di interpolazione per secondo)")]
    [Tooltip("Quanto in fretta si raggiunge una velocità più alta.")]
    [Min(0f)]
    [SerializeField] private float acceleration = 10f;

    [Tooltip("Quanto in fretta si rallenta verso una velocità più bassa o ci si ferma.")]
    [Min(0f)]
    [SerializeField] private float deceleration = 10f;

    [Header("Stamina")]
    [Tooltip("Stamina massima. Il giocatore nasce con la stamina piena.")]
    [Min(1f)]
    [SerializeField] private float maxStamina = 100f;

    [Tooltip("Stamina consumata al secondo durante lo scatto.")]
    [Min(0f)]
    [SerializeField] private float sprintStaminaDrain = 20f;

    [Tooltip("Stamina recuperata al secondo quando non si scatta.")]
    [Min(0f)]
    [SerializeField] private float staminaRecovery = 15f;

    [Header("Gravità")]
    [Tooltip("Accelerazione di gravità in caduta (m/s², valore positivo).")]
    [Min(0f)]
    [SerializeField] private float gravity = 20f;

    [Tooltip("Velocità verticale minima, cioè la caduta più veloce possibile (m/s, valore negativo).")]
    [SerializeField] private float terminalVelocity = -50f;

    // ===== Getter pubblici =====

    public float WalkSpeed => walkSpeed;
    public float SprintSpeed => sprintSpeed;
    public float CrouchSpeed => crouchSpeed;
    public float Acceleration => acceleration;
    public float Deceleration => deceleration;
    public float MaxStamina => maxStamina;
    public float SprintStaminaDrain => sprintStaminaDrain;
    public float StaminaRecovery => staminaRecovery;
    public float Gravity => gravity;
    public float TerminalVelocity => Mathf.Min(0f, terminalVelocity);
}
