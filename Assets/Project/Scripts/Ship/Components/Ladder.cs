using UnityEngine;

/// <summary>
/// Ladder climbing system - INPUT HANDLED BY PLAYERCONTROLLER
/// This component only provides climbing logic, PlayerController feeds it input via Input System
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class Ladder : MonoBehaviour, IInteractable
{
    [Header("Ladder Settings")]
    [SerializeField] private float climbSpeed = 3f;
    [SerializeField] private Transform topExitPoint;
    [SerializeField] private Transform bottomExitPoint;

    [Header("Camera")]
    [SerializeField] private float cameraLookSpeed = 2f;
    [SerializeField] private float maxLookAngle = 60f;
    [SerializeField] private float cameraDistanceFromLadder = 0.5f;

    [Header("Exit Settings")]
    [SerializeField] private float snapDistance = 0.8f;

    [Header("Audio")]
    [SerializeField] private AudioClip climbSound;
    [SerializeField] private float climbSoundInterval = 0.5f;

    // State
    private bool isPlayerOnLadder = false;
    private PlayerController currentPlayer;
    private CharacterController playerCharacterController;
    private Transform playerCamera;
    private Vector3 ladderNormal;
    private float verticalRotation = 0f;
    private float nextSoundTime = 0f;
    private AudioSource audioSource;

    // Public properties
    public bool IsPlayerOnLadder => isPlayerOnLadder;
    public PlayerController CurrentPlayer => currentPlayer;

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
    }

    // ===== IINTERACTABLE =====

    public void Interact(GameObject interactor)
    {
        if (isPlayerOnLadder)
        {
            ExitLadder();
        }
        else
        {
            EnterLadder(interactor);
        }
    }

    public string GetInteractionPrompt()
    {
        return isPlayerOnLadder ? "[E] Exit Ladder" : "[E] Use Ladder";
    }

    public bool CanInteract()
    {
        // Can always interact with ladder
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
    /// Called by PlayerController every frame when on ladder
    /// </summary>
    public void HandleClimbing(float verticalInput, Vector2 lookInput)
    {
        if (currentPlayer == null) return;

        // Vertical movement (W/S from PlayerController moveInput.y)
        if (Mathf.Abs(verticalInput) > 0.1f)
        {
            float newY = currentPlayer.transform.position.y + (verticalInput * climbSpeed * Time.deltaTime);

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

        // Camera look (from PlayerController lookInput)
        HandleCameraLook(lookInput);
    }

    /// <summary>
    /// Called when player presses exit button (E or Space)
    /// </summary>
    public void TryExit()
    {
        bool snapped = TrySnapToNearestExit();

        if (!snapped)
        {
            Debug.Log("[Ladder] Exit mid-climb - will fall");
        }

        ExitLadder();
    }

    // ===== PRIVATE METHODS =====

    private void EnterLadder(GameObject player)
    {
        currentPlayer = player.GetComponent<PlayerController>();
        if (currentPlayer == null) return;

        playerCharacterController = player.GetComponent<CharacterController>();
        playerCamera = currentPlayer.CameraTransform;

        isPlayerOnLadder = true;

        // Register this ladder with PlayerController
        currentPlayer.SetCurrentLadder(this);

        Vector3 playerPos = player.transform.position;

        // Disable CharacterController and force door exits
        if (playerCharacterController != null)
        {
            playerCharacterController.enabled = false;

            // Force nearby doors to trigger exit
            Collider[] nearby = Physics.OverlapSphere(player.transform.position, 5f);
            foreach (Collider col in nearby)
            {
                var door = col.GetComponent<creepycat.scifikitvol4.DoubleDoorOpenAuto>();
                if (door != null)
                {
                    door.SendMessage("OnTriggerExit", playerCharacterController, SendMessageOptions.DontRequireReceiver);
                }
            }

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

        // Face ladder
        player.transform.rotation = Quaternion.LookRotation(ladderNormal);
        verticalRotation = 0f;

        Debug.Log("[Ladder] Player entered");
    }

    private void ExitLadder()
    {
        if (currentPlayer == null) return;

        isPlayerOnLadder = false;

        // Unregister from PlayerController
        currentPlayer.SetCurrentLadder(null);

        if (playerCharacterController != null)
        {
            Vector3 exitPos = GetNearestExitPoint();
            currentPlayer.transform.position = exitPos;
            currentPlayer.transform.position += currentPlayer.transform.forward * 0.5f;

            playerCharacterController.enabled = true;
        }

        currentPlayer = null;
        playerCharacterController = null;
        playerCamera = null;

        Debug.Log("[Ladder] Player exited");
    }

    private void HandleCameraLook(Vector2 lookInput)
    {
        if (playerCamera == null) return;

        verticalRotation -= lookInput.y * cameraLookSpeed;
        verticalRotation = Mathf.Clamp(verticalRotation, -maxLookAngle, maxLookAngle);

        playerCamera.localRotation = Quaternion.Euler(verticalRotation, 0f, 0f);
        currentPlayer.transform.rotation = Quaternion.LookRotation(ladderNormal);
    }

    private bool TrySnapToNearestExit()
    {
        if (currentPlayer == null) return false;

        float playerY = currentPlayer.transform.position.y;

        // Check top
        if (topExitPoint != null)
        {
            float dist = Mathf.Abs(playerY - topExitPoint.position.y);
            if (dist < snapDistance)
            {
                currentPlayer.transform.position = topExitPoint.position;
                Debug.Log($"[Ladder] Snapped to top ({dist:F2}m)");
                return true;
            }
        }

        // Check bottom
        if (bottomExitPoint != null)
        {
            float dist = Mathf.Abs(playerY - bottomExitPoint.position.y);
            if (dist < snapDistance)
            {
                currentPlayer.transform.position = bottomExitPoint.position;
                Debug.Log($"[Ladder] Snapped to bottom ({dist:F2}m)");
                return true;
            }
        }

        return false;
    }

    private Vector3 GetNearestExitPoint()
    {
        if (topExitPoint == null && bottomExitPoint == null)
        {
            return currentPlayer.transform.position;
        }

        if (topExitPoint == null) return bottomExitPoint.position;
        if (bottomExitPoint == null) return topExitPoint.position;

        float playerY = currentPlayer.transform.position.y;
        float distTop = Mathf.Abs(playerY - topExitPoint.position.y);
        float distBottom = Mathf.Abs(playerY - bottomExitPoint.position.y);

        return (distTop < distBottom) ? topExitPoint.position : bottomExitPoint.position;
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