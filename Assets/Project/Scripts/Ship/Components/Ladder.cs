using UnityEngine;
using creepycat.scifikitvol4;

/// <summary>
/// Ladder climbing system - INPUT HANDLED BY PLAYERCONTROLLER
/// This component only provides climbing logic, PlayerController feeds it input via Input System
///
/// VISUALE (Rev BT-b · Q79-a): HandleClimbing riceve la rotazione già convertita in GRADI da
/// PlayerController (mouse: pixel × sensibilità; stick: velocità angolare × deltaTime), quindi in
/// salita la sensibilità è la stessa che a piedi. lookScale (default 1) è solo un moltiplicatore
/// relativo. Il vecchio campo cameraLookSpeed (0.1 nelle due scale di Game.unity) moltiplicava
/// l'input grezzo — pixel del mouse o deflessione dello stick — e non è più letto.
/// File convertito in UTF-8 in Rev BT-b (era Windows-1252).
///
/// REV BW-c — REVISIONE DELLA SCALA:
///   L1  Crouch ignorato in salita (PlayerController.OnCrouch esce se IsOnLadder).
///   L2  Si sale solo da "giocatore libero" (CanInteract): vivo, PlayerController attivo (non seduto,
///       non sdraiato, non a terra, tablet chiuso), non già su un'altra scala. Stessa regola delle
///       postazioni (MedicalStation.CanInteract).
///   L3  Q113-a — Interact sulla scala: entro LadderConfig.ExitReach da un pianerottolo si scende lì
///       (come prima); più lontano si LASCIA LA PRESA e si cade (CharacterController riacceso,
///       velocità azzerata, gravità di PlayerController). Prompt "Exit Ladder" / "Let go".
///       Mentre si è sulla scala il bersaglio dell'interazione è la scala stessa
///       (InteractionSystem.SetLockedInteractable): con la visuale libera (L5) il raggio può non
///       colpirla, e nessun altro oggetto diventa bersaglio durante la salita.
///   L4  A terra sulla scala (Downed): il corpo va al pianerottolo più vicino con il
///       CharacterController acceso (il suo collider serve al raggio del defibrillatore, Rev BD) e
///       viene appoggiato al pavimento (CharacterController.Move verso il basso, al massimo
///       LadderConfig.DownedGroundSnap): a terra PlayerController è spento e la gravità non agisce,
///       quindi senza questo il corpo restava sospeso all'altezza del punto di uscita (gate BW-c).
///       PlayerController resta com'è: il freeze Downed di PlayerHealthSystem è l'autorità.
///   L5  Q114-a — visuale orizzontale libera entro ±maxYawAngle attorno alla scala: gira la camera,
///       il corpo resta rivolto alla scala. All'uscita la direzione dello sguardo passa al corpo.
///   L6  Velocità di salita, distanza di aggancio e spostamenti di uscita in LadderConfig (SO).
///       Angoli e moltiplicatore della visuale restano qui (comfort, come Q72-a).
///   L7  Lo sguardo verticale parte da quello attuale (PlayerController.LookPitch) e torna a
///       PlayerController all'uscita (SetLookPitch). Prima si azzerava salendo e tornava al valore
///       vecchio scendendo. Anche l'angolo orizzontale di partenza è quello di prima, nei limiti.
///   Q115-a — PORTE: spegnere il CharacterController non genera OnTriggerExit, quindi una porta che
///       contiene il giocatore resterebbe aperta. Prima la scala chiamava OnTriggerExit (SendMessage)
///       su TUTTE le porte entro 5 m: chiudeva porte in cui il giocatore non era e raggiungeva anche
///       porte mai spawnate (RpcException). Ora avvisa solo le porte il cui trigger contiene la
///       capsula del CharacterController, con un metodo esplicito
///       (DoubleDoorOpenAuto.NotifyColliderLeft), che ignora le porte non spawnate.
///
/// Stato locale al client del giocatore che sale (la scala non è in rete): gli altri vedono il
/// corpo muoversi via NetworkTransform.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class Ladder : MonoBehaviour, IInteractable
{
    [Header("Debug")]
    [Tooltip("Log diagnostici verbosi (ingresso/uscita/snap scala). Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;
    [Tooltip("Disegna i gizmi dei punti scala (Scene view). Standard Rev BA — default off.")]
    [SerializeField] private bool drawDebugGizmos = false;
    private void LogV(string msg) { if (logVerbose) Debug.Log(msg); }

    [Header("Config (Rev BW-c · L6)")]
    [Tooltip("Velocità di salita e regole di uscita. Asset: Assets/Project/Scripts/Ship/Components/" +
             "LadderConfig.asset, condiviso da tutte le scale. Se manca, default dello SO ed errore a log.")]
    [SerializeField] private LadderConfig config;

    [Header("Ladder Settings")]
    // Rev BW-c (L6): in LadderConfig.ClimbSpeed. Il valore serializzato in scena sparisce al prossimo salvataggio.
    // [SerializeField] private float climbSpeed = 3f;
    [SerializeField] private Transform topExitPoint;
    [SerializeField] private Transform bottomExitPoint;

    [Header("Camera")]
    // Rev BT-b: sostituito da lookScale (l'input ora arriva in gradi da PlayerController).
    // [SerializeField] private float cameraLookSpeed = 2f;
    [Tooltip("Moltiplicatore della rotazione in salita, relativo alla visuale a piedi " +
             "(1 = stessa sensibilità). Rev BT-b.")]
    [Min(0f)]
    [SerializeField] private float lookScale = 1f;

    [Tooltip("Angolo massimo della visuale verticale in salita, in gradi (comfort).")]
    [Range(0f, 89f)]
    [SerializeField] private float maxLookAngle = 60f;

    [Tooltip("Rev BW-c (Q114-a): angolo massimo della visuale orizzontale in salita, in gradi, " +
             "attorno alla direzione della scala. Gira la camera, il corpo resta rivolto alla scala.")]
    [Range(0f, 180f)]
    [SerializeField] private float maxYawAngle = 75f;

    [Tooltip("Distanza dal piano della scala a cui si tiene il giocatore (geometria della singola scala).")]
    [SerializeField] private float cameraDistanceFromLadder = 0.5f;

    // Rev BW-c (L6): in LadderConfig.ExitReach. Il valore serializzato in scena sparisce al prossimo salvataggio.
    // [Header("Exit Settings")]
    // [SerializeField] private float snapDistance = 0.8f;

    [Header("Audio")]
    [SerializeField] private AudioClip climbSound;
    [SerializeField] private float climbSoundInterval = 0.5f;

    // State
    private bool isPlayerOnLadder = false;
    private PlayerController currentPlayer;
    private CharacterController playerCharacterController;
    private Transform playerCamera;
    private PlayerHealthSystem playerHealth;          // Rev BW-c (L4)
    private InteractionSystem playerInteraction;      // Rev BW-c (L3)
    private Vector3 ladderNormal;
    private float verticalRotation = 0f;
    private float yawRotation = 0f;                   // Rev BW-c (L5)
    private float nextSoundTime = 0f;
    private AudioSource audioSource;

    // Rev BW-c (L2) — rig del giocatore locale per CanInteract (cache per istanza di LocalInstance).
    private PlayerHealthSystem rigHealth;
    private PlayerController rigController;
    private TabletStation rigTablet;

    // Public properties
    public bool IsPlayerOnLadder => isPlayerOnLadder;
    public PlayerController CurrentPlayer => currentPlayer;

    private LadderConfig Config => config;

    private void Awake()
    {
        ladderNormal = transform.right;

        BoxCollider trigger = GetComponent<BoxCollider>();
        trigger.isTrigger = true;

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f;
        }

        // Rev BW-c (L6): senza asset la scala funziona comunque — default dello SO + errore.
        if (config == null)
        {
            Debug.LogError($"[Ladder] {name}: LadderConfig non assegnato: uso i valori di default. " +
                           "Assegna Assets/Project/Scripts/Ship/Components/LadderConfig.asset.", this);
            config = ScriptableObject.CreateInstance<LadderConfig>();
        }
    }

    /// <summary>
    /// Rev BW-c (L4) — controlli che non dipendono dall'input: giocatore despawnato o a terra
    /// mentre è sulla scala. PlayerController è spento a terra, quindi HandleClimbing non gira.
    /// </summary>
    private void Update()
    {
        if (!isPlayerOnLadder) return;

        if (currentPlayer == null)
        {
            // Il giocatore è stato distrutto (disconnessione) mentre era sulla scala.
            ResetState();
            return;
        }

        if (playerHealth != null && !playerHealth.IsAlive)
        {
            LogV("[Ladder] Player down on the ladder - moved to the nearest landing");
            LeaveLadder(toLanding: true, snapToGround: true);
        }
    }

    private void OnDisable()
    {
        // Scala spenta con qualcuno sopra: lo si rimette a terra invece di lasciarlo appeso.
        if (isPlayerOnLadder) LeaveLadder(toLanding: true);
    }

    // ===== IINTERACTABLE =====

    public void Interact(GameObject interactor)
    {
        if (isPlayerOnLadder)
        {
            // Q113-a: vicino a un pianerottolo si scende lì, altrimenti si lascia la presa.
            LeaveLadder(toLanding: IsNearLanding());
        }
        else
        {
            EnterLadder(interactor);
        }
    }

    public string GetInteractionPrompt()
    {
        // Rev BT-c (Q75-a): tasto da InputDeviceManager.FormatPrompt invece di "[E]" fisso.
        // Rev BW-c (Q113-a): sulla scala il testo dice cosa succede premendo.
        if (!isPlayerOnLadder) return "[{interact}] Use Ladder";
        return IsNearLanding() ? "[{interact}] Exit Ladder" : "[{interact}] Let go";
    }

    /// <summary>
    /// Rev BW-c (L2). Sulla scala: disponibile finché PlayerController è attivo (con il tablet
    /// aperto niente prompt). Fuori: solo a un giocatore libero.
    /// </summary>
    public bool CanInteract()
    {
        if (isPlayerOnLadder)
            return currentPlayer != null && currentPlayer.enabled;

        if (!EnsureRig()) return false;
        if (!rigHealth.IsAlive) return false;
        if (rigController == null || !rigController.enabled) return false;
        if (rigController.IsOnLadder) return false;
        if (rigTablet != null && rigTablet.IsBusy) return false;
        return true;
    }

    public bool IsContinuousInteraction()
    {
        // Ladder is not continuous (one press to enter/exit)
        return false;
    }

    public void OnLookEnter()
    {
        // Optional: Could highlight ladder when looked at
    }

    public void OnLookExit()
    {
        // Optional: Could remove highlight
    }

    // ===== PUBLIC METHODS FOR PLAYERCONTROLLER =====

    /// <summary>
    /// Called by PlayerController every frame when on ladder.
    /// lookDegrees: rotazione di questo frame in gradi, già convertita da PlayerController
    /// (Rev BT-b). Rev BW-c (Q114-a): si usano entrambe le componenti.
    /// </summary>
    public void HandleClimbing(float verticalInput, Vector2 lookDegrees)
    {
        if (currentPlayer == null) return;

        // Vertical movement (W/S from PlayerController moveInput.y)
        if (Mathf.Abs(verticalInput) > 0.1f)
        {
            float newY = currentPlayer.transform.position.y + (verticalInput * Config.ClimbSpeed * Time.deltaTime);

            // Clamp between exit points
            if (bottomExitPoint != null && topExitPoint != null)
            {
                newY = Mathf.Clamp(newY, bottomExitPoint.position.y, topExitPoint.position.y);
            }

            Vector3 newPosition = currentPlayer.transform.position;
            newPosition.y = newY;
            currentPlayer.transform.position = newPosition;

            // Sound
            if (Time.time >= nextSoundTime)
            {
                PlayClimbSound();
                nextSoundTime = Time.time + climbSoundInterval;
            }
        }

        // Lock to ladder X/Z with camera offset
        Vector3 ladderPos = transform.position;
        Vector3 offsetDir = -ladderNormal;
        Vector3 lockedPos = currentPlayer.transform.position;
        lockedPos.x = ladderPos.x + (offsetDir.x * cameraDistanceFromLadder);
        lockedPos.z = ladderPos.z + (offsetDir.z * cameraDistanceFromLadder);
        currentPlayer.transform.position = lockedPos;

        // Camera look (from PlayerController, already in degrees — Rev BT-b)
        HandleCameraLook(lookDegrees);
    }

    // ===== PRIVATE METHODS =====

    private void EnterLadder(GameObject player)
    {
        currentPlayer = player.GetComponent<PlayerController>();
        if (currentPlayer == null) return;

        playerCharacterController = player.GetComponent<CharacterController>();
        playerCamera = currentPlayer.CameraTransform;
        playerHealth = player.GetComponent<PlayerHealthSystem>();
        playerInteraction = player.GetComponent<InteractionSystem>();

        // L7 / Q114-a: lo sguardo parte da quello attuale, nei limiti della scala.
        Vector3 previousForward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up);
        Vector3 ladderForward = Vector3.ProjectOnPlane(ladderNormal, Vector3.up);
        verticalRotation = Mathf.Clamp(currentPlayer.LookPitch, -maxLookAngle, maxLookAngle);
        yawRotation = Mathf.Clamp(Vector3.SignedAngle(ladderForward, previousForward, Vector3.up),
                                  -maxYawAngle, maxYawAngle);

        isPlayerOnLadder = true;

        // Register this ladder with PlayerController
        currentPlayer.SetCurrentLadder(this);

        Vector3 playerPos = player.transform.position;

        if (playerCharacterController != null)
        {
            // Q115-a: avvisa le porte che contengono il giocatore PRIMA di spegnere il collider
            // (spento, non genererebbe OnTriggerExit).
            ReleaseDoorTriggers(playerCharacterController);

            playerCharacterController.enabled = false;
            Physics.SyncTransforms();
        }

        // Position with camera offset
        Vector3 ladderPos = transform.position;
        Vector3 offsetDir = -ladderNormal;
        player.transform.position = new Vector3(
            ladderPos.x + (offsetDir.x * cameraDistanceFromLadder),
            playerPos.y,
            ladderPos.z + (offsetDir.z * cameraDistanceFromLadder)
        );

        // Face ladder; la camera mantiene lo sguardo (nessuno scatto).
        player.transform.rotation = Quaternion.LookRotation(ladderNormal);
        if (playerCamera != null)
            playerCamera.localRotation = Quaternion.Euler(verticalRotation, yawRotation, 0f);

        // L3: durante la salita il bersaglio dell'interazione è la scala.
        if (playerInteraction != null) playerInteraction.SetLockedInteractable(this);

        LogV("[Ladder] Player entered");
    }

    /// <summary>
    /// Rev BW-c — unica uscita dalla scala.
    /// toLanding = true: al pianerottolo più vicino (Interact vicino a un estremo, giocatore a terra,
    /// scala spenta). toLanding = false: lascia la presa dove si trova e cade (Q113-a).
    /// In entrambi i casi lo sguardo passa al corpo e a PlayerController (L5, L7), la velocità
    /// interna si azzera (ResetVelocity) e il CharacterController si riaccende. PlayerController.enabled
    /// non viene toccato: a terra resta spento (freeze Downed).
    /// snapToGround = true (solo a terra, L4): dopo la riaccensione il CharacterController scende fino
    /// al pavimento, perché senza PlayerController nessuno applica la gravità.
    /// </summary>
    private void LeaveLadder(bool toLanding, bool snapToGround = false)
    {
        if (currentPlayer == null)
        {
            ResetState();
            return;
        }

        Transform body = currentPlayer.transform;

        if (toLanding)
        {
            Transform landing = GetNearestExitPoint();
            Vector3 exitPos = landing != null ? landing.position : body.position;
            body.position = exitPos + ladderNormal * Config.ExitForwardOffset;
            LogV(landing != null ? $"[Ladder] Exit to landing {landing.name}" : "[Ladder] Exit (no exit points)");
        }
        else
        {
            body.position -= ladderNormal * Config.LetGoBackOffset;
            LogV("[Ladder] Let go mid-climb - falling");
        }

        // L5 / L7: la direzione dello sguardo passa al corpo, l'inclinazione a PlayerController.
        body.rotation = Quaternion.LookRotation(ladderNormal) * Quaternion.Euler(0f, yawRotation, 0f);
        currentPlayer.SetLookPitch(verticalRotation);
        currentPlayer.ResetVelocity();

        // Unregister from PlayerController
        currentPlayer.SetCurrentLadder(null);

        if (playerCharacterController != null)
        {
            playerCharacterController.enabled = true;
            Physics.SyncTransforms();

            // L4: appoggio al pavimento (il movimento si ferma al primo contatto).
            if (snapToGround && Config.DownedGroundSnap > 0f)
                playerCharacterController.Move(Vector3.down * Config.DownedGroundSnap);
        }

        if (playerInteraction != null) playerInteraction.ClearLockedInteractable(this);

        ResetState();
        LogV("[Ladder] Player exited");
    }

    // Rev BW-c (Q113-a): TryExit e TrySnapToNearestExit non erano mai chiamati (l'uscita passava
    // solo da Interact → ExitLadder, sempre al pianerottolo più vicino). Il "lascia la presa" che
    // TryExit prevedeva ora è LeaveLadder(toLanding: false). Guardia commentata:
    //
    // public void TryExit()
    // {
    //     bool snapped = TrySnapToNearestExit();
    //     if (!snapped) LogV("[Ladder] Exit mid-climb - will fall");
    //     ExitLadder();
    // }
    //
    // private bool TrySnapToNearestExit()
    // {
    //     // portava il giocatore esattamente sul punto di uscita entro snapDistance (alto o basso)
    // }

    private void ResetState()
    {
        isPlayerOnLadder = false;
        currentPlayer = null;
        playerCharacterController = null;
        playerCamera = null;
        playerHealth = null;
        playerInteraction = null;
        verticalRotation = 0f;
        yawRotation = 0f;
    }

    private void HandleCameraLook(Vector2 lookDegrees)
    {
        if (playerCamera == null) return;

        verticalRotation -= lookDegrees.y * lookScale;
        verticalRotation = Mathf.Clamp(verticalRotation, -maxLookAngle, maxLookAngle);

        // Rev BW-c (Q114-a): visuale orizzontale libera nei limiti, solo sulla camera.
        yawRotation += lookDegrees.x * lookScale;
        yawRotation = Mathf.Clamp(yawRotation, -maxYawAngle, maxYawAngle);

        playerCamera.localRotation = Quaternion.Euler(verticalRotation, yawRotation, 0f);
        currentPlayer.transform.rotation = Quaternion.LookRotation(ladderNormal);
    }

    /// <summary>
    /// Rev BW-c (Q113-a): true se il giocatore è entro LadderConfig.ExitReach (in verticale) da uno
    /// dei due pianerottoli. Senza punti di uscita: false (Interact lascia la presa).
    /// </summary>
    private bool IsNearLanding()
    {
        if (currentPlayer == null) return false;

        float playerY = currentPlayer.transform.position.y;
        float reach = Config.ExitReach;

        if (topExitPoint != null && Mathf.Abs(playerY - topExitPoint.position.y) < reach) return true;
        if (bottomExitPoint != null && Mathf.Abs(playerY - bottomExitPoint.position.y) < reach) return true;
        return false;
    }

    private Transform GetNearestExitPoint()
    {
        if (topExitPoint == null && bottomExitPoint == null) return null;
        if (topExitPoint == null) return bottomExitPoint;
        if (bottomExitPoint == null) return topExitPoint;

        float playerY = currentPlayer != null ? currentPlayer.transform.position.y : transform.position.y;
        float distTop = Mathf.Abs(playerY - topExitPoint.position.y);
        float distBottom = Mathf.Abs(playerY - bottomExitPoint.position.y);

        return (distTop < distBottom) ? topExitPoint : bottomExitPoint;
    }

    /// <summary>
    /// Rev BW-c (Q115-a) — avvisa SOLO le porte il cui trigger contiene la capsula del
    /// CharacterController (test di sovrapposizione con i trigger inclusi). Sostituisce la ricerca
    /// entro 5 m con SendMessage("OnTriggerExit"), che chiudeva anche porte in cui il giocatore non era
    /// e raggiungeva porte mai spawnate.
    /// </summary>
    private void ReleaseDoorTriggers(CharacterController cc)
    {
        Transform t = cc.transform;
        Vector3 scale = t.lossyScale;
        float radius = cc.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        float height = Mathf.Max(cc.height * Mathf.Abs(scale.y), radius * 2f);
        Vector3 center = t.TransformPoint(cc.center);
        Vector3 axis = t.up * (height * 0.5f - radius);

        Collider[] hits = Physics.OverlapCapsule(center + axis, center - axis, radius,
                                                 Physics.AllLayers, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            if (hit == null || !hit.isTrigger) continue;

            DoubleDoorOpenAuto door = hit.GetComponent<DoubleDoorOpenAuto>();
            if (door != null)
            {
                door.NotifyColliderLeft(cc);
                LogV($"[Ladder] Door {door.name}: player left its trigger (collider off on the ladder)");
            }
        }
    }

    /// <summary>
    /// Rev BW-c (L2) — risolve e mette in cache i componenti del giocatore locale (una volta per
    /// istanza di PlayerHealthSystem.LocalInstance), come MedicalStation.EnsureRig.
    /// </summary>
    private bool EnsureRig()
    {
        PlayerHealthSystem me = PlayerHealthSystem.LocalInstance;
        if (me == null)
        {
            rigHealth = null;
            return false;
        }

        if (me != rigHealth)
        {
            rigHealth = me;
            rigController = me.GetComponent<PlayerController>();
            rigTablet = me.GetComponent<TabletStation>();
        }
        return true;
    }

    private void PlayClimbSound()
    {
        if (audioSource != null && climbSound != null)
        {
            audioSource.PlayOneShot(climbSound);
        }
    }

    // Debug gizmos
    private void OnDrawGizmos()
    {
        if (!drawDebugGizmos) return;

        if (topExitPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(topExitPoint.position, 0.3f);
        }

        if (bottomExitPoint != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(bottomExitPoint.position, 0.3f);
        }

        Gizmos.color = Color.blue;
        Gizmos.DrawRay(transform.position, transform.right * 2f);
    }
}