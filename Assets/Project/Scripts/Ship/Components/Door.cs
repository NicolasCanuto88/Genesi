using UnityEngine;
using System.Collections;

/// <summary>
/// Interactive door system for spaceship
/// Supports: automatic sliding doors, manual doors, powered/unpowered states
/// </summary>
[RequireComponent(typeof(Collider))]
public class Door : MonoBehaviour, IInteractable, IPowerConsumer
{
    [Header("Door Type")]
    [SerializeField] private DoorType doorType = DoorType.Automatic;
    [SerializeField] private bool requiresPower = true;

    [Header("Door Parts")]
    [SerializeField] private Transform leftDoor;  // Sliding door left panel
    [SerializeField] private Transform rightDoor; // Sliding door right panel (null for single door)

    [Header("Animation")]
    [SerializeField] private float openDistance = 1.5f; // How far doors slide open
    [SerializeField] private float openSpeed = 2f;
    [SerializeField] private float autoCloseDelay = 3f; // Auto-close after X seconds (0 = no auto-close)

    [Header("Power Settings")]
    [SerializeField] private float powerConsumption = 5f; // Watts when operating
    [SerializeField] private int priority = 5; // Priority for power management

    [Header("Audio")]
    [SerializeField] private AudioClip openSound;
    [SerializeField] private AudioClip closeSound;
    [SerializeField] private AudioClip deniedSound; // When door can't open

    [Header("Lock Settings")]
    [SerializeField] private bool isLocked = false;
    [SerializeField] private string requiredKeycard = ""; // Optional: keycard system

    // State
    private DoorState currentState = DoorState.Closed;
    private Vector3 leftDoorClosedPos;
    private Vector3 rightDoorClosedPos;
    private Vector3 leftDoorOpenPos;
    private Vector3 rightDoorOpenPos;
    private Coroutine animationCoroutine;
    private Coroutine autoCloseCoroutine;
    private bool isPowered = true;
    private AudioSource audioSource;

    // Trigger detection
    private int objectsInTrigger = 0;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        // Store initial positions
        if (leftDoor != null)
        {
            leftDoorClosedPos = leftDoor.localPosition;
            leftDoorOpenPos = leftDoorClosedPos + leftDoor.right * -openDistance;
        }

        if (rightDoor != null)
        {
            rightDoorClosedPos = rightDoor.localPosition;
            rightDoorOpenPos = rightDoorClosedPos + rightDoor.right * openDistance;
        }

        // Register with PowerManager
        if (requiresPower && PowerManager.Instance != null)
        {
            PowerManager.Instance.RegisterPowerConsumer(this);
        }
    }

    private void OnDestroy()
    {
        // Unregister from PowerManager
        if (requiresPower && PowerManager.Instance != null)
        {
            PowerManager.Instance.UnregisterPowerConsumer(this);
        }
    }

    // ===== IINTERACTABLE IMPLEMENTATION =====

    public void Interact(GameObject interactor)
    {
        if (doorType == DoorType.Manual)
        {
            // Manual door - toggle state
            if (currentState == DoorState.Closed || currentState == DoorState.Closing)
            {
                OpenDoor();
            }
            else if (currentState == DoorState.Open || currentState == DoorState.Opening)
            {
                CloseDoor();
            }
        }
        else
        {
            // Automatic door - just open (will auto-close)
            OpenDoor();
        }
    }

    public bool CanInteract()
    {
        // Can't interact if already animating (unless manual override)
        if (currentState == DoorState.Opening || currentState == DoorState.Closing)
        {
            return false;
        }

        // Can't open if locked
        if (isLocked)
        {
            return false;
        }

        // Can't open if no power and requires power
        if (requiresPower && !isPowered)
        {
            return false;
        }

        return true;
    }

    public string GetInteractionPrompt()
    {
        if (isLocked)
        {
            return "[LOCKED]";
        }

        if (requiresPower && !isPowered)
        {
            return "[NO POWER]";
        }

        if (doorType == DoorType.Manual)
        {
            if (currentState == DoorState.Closed)
            {
                return "[E] Open Door";
            }
            else
            {
                return "[E] Close Door";
            }
        }
        else
        {
            return "[E] Open Door";
        }
    }

    public bool IsContinuousInteraction()
    {
        return false; // Door interaction is one-shot
    }

    public void OnLookEnter()
    {
        // Optional: Highlight door outline
    }

    public void OnLookExit()
    {
        // Optional: Remove highlight
    }

    // ===== IPOWERCONSUMER IMPLEMENTATION =====

    public float GetPowerDemand()
    {
        // Only consume power when opening/closing
        if (currentState == DoorState.Opening || currentState == DoorState.Closing)
        {
            return powerConsumption;
        }
        return 0f;
    }

    public int GetPriority()
    {
        return priority;
    }

    public bool IsActive()
    {
        return requiresPower && (currentState == DoorState.Opening || currentState == DoorState.Closing);
    }

    public bool CanBeDisabled()
    {
        return priority < 10; // Doors can be disabled if low priority
    }

    public void SetPowerState(bool isOn)
    {
        isPowered = isOn;

        if (!isOn && (currentState == DoorState.Opening || currentState == DoorState.Opening))
        {
            // Power lost during operation - emergency stop
            StopAnimation();
            PlaySound(deniedSound);
        }
    }

    public string GetSystemName()
    {
        return $"Door: {gameObject.name}";
    }

    // ===== DOOR CONTROL =====

    public void OpenDoor()
    {
        if (!CanInteract())
        {
            PlaySound(deniedSound);
            return;
        }

        if (currentState == DoorState.Open)
        {
            return; // Already open
        }

        StopAnimation();
        animationCoroutine = StartCoroutine(AnimateDoor(true));
        PlaySound(openSound);

        // Auto-close for automatic doors
        if (doorType == DoorType.Automatic && autoCloseDelay > 0)
        {
            if (autoCloseCoroutine != null)
            {
                StopCoroutine(autoCloseCoroutine);
            }
            autoCloseCoroutine = StartCoroutine(AutoCloseAfterDelay());
        }
    }

    public void CloseDoor()
    {
        if (currentState == DoorState.Closed)
        {
            return; // Already closed
        }

        // Check if anything is in the doorway
        if (objectsInTrigger > 0 && doorType == DoorType.Automatic)
        {
            // Don't close if automatic and objects in trigger
            return;
        }

        StopAnimation();
        animationCoroutine = StartCoroutine(AnimateDoor(false));
        PlaySound(closeSound);
    }

    private IEnumerator AnimateDoor(bool opening)
    {
        currentState = opening ? DoorState.Opening : DoorState.Closing;

        Vector3 leftTarget = opening ? leftDoorOpenPos : leftDoorClosedPos;
        Vector3 rightTarget = opening ? rightDoorOpenPos : rightDoorClosedPos;

        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime * openSpeed;
            t = Mathf.Clamp01(t);

            if (leftDoor != null)
            {
                leftDoor.localPosition = Vector3.Lerp(leftDoor.localPosition, leftTarget, t);
            }

            if (rightDoor != null)
            {
                rightDoor.localPosition = Vector3.Lerp(rightDoor.localPosition, rightTarget, t);
            }

            yield return null;
        }

        // Snap to final position
        if (leftDoor != null)
        {
            leftDoor.localPosition = leftTarget;
        }

        if (rightDoor != null)
        {
            rightDoor.localPosition = rightTarget;
        }

        currentState = opening ? DoorState.Open : DoorState.Closed;
        animationCoroutine = null;
    }

    private IEnumerator AutoCloseAfterDelay()
    {
        yield return new WaitForSeconds(autoCloseDelay);

        // Only auto-close if nothing in trigger
        if (objectsInTrigger == 0)
        {
            CloseDoor();
        }

        autoCloseCoroutine = null;
    }

    private void StopAnimation()
    {
        if (animationCoroutine != null)
        {
            StopCoroutine(animationCoroutine);
            animationCoroutine = null;
        }
    }

    private void PlaySound(AudioClip clip)
    {
        if (clip != null && audioSource != null)
        {
            audioSource.PlayOneShot(clip);
        }
    }

    // ===== TRIGGER DETECTION (for automatic doors) =====

    private void OnTriggerEnter(Collider other)
    {
        if (doorType == DoorType.Automatic)
        {
            // Check if it's player or NPC
            if (other.CompareTag("Player") || other.CompareTag("NPC"))
            {
                objectsInTrigger++;
                OpenDoor();
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (doorType == DoorType.Automatic)
        {
            if (other.CompareTag("Player") || other.CompareTag("NPC"))
            {
                objectsInTrigger--;
                objectsInTrigger = Mathf.Max(0, objectsInTrigger);

                // Try to close if no one in trigger
                if (objectsInTrigger == 0 && autoCloseDelay > 0)
                {
                    if (autoCloseCoroutine != null)
                    {
                        StopCoroutine(autoCloseCoroutine);
                    }
                    autoCloseCoroutine = StartCoroutine(AutoCloseAfterDelay());
                }
            }
        }
    }

    // ===== PUBLIC UTILITIES =====

    public void Lock()
    {
        isLocked = true;
        // If door is open, close it
        if (currentState == DoorState.Open)
        {
            CloseDoor();
        }
    }

    public void Unlock()
    {
        isLocked = false;
    }

    public void SetDoorType(DoorType newType)
    {
        doorType = newType;
    }
}

public enum DoorType
{
    Automatic,  // Opens when player approaches, auto-closes
    Manual      // Requires interaction to open/close
}

public enum DoorState
{
    Closed,
    Opening,
    Open,
    Closing
}