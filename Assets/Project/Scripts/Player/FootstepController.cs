using UnityEngine;

/// <summary>
/// Footstep audio (Milestone 1B). Decoupled: reads CharacterController velocity /
/// grounded state and PlayerController's sprint/crouch flags — it never drives movement.
///
/// Automatically silent when the CharacterController is disabled (on ladder, at a
/// station), so it never overlaps the Ladder's own climb audio.
///
/// Surface-aware: raycasts down and picks a clip set by the ground collider's tag
/// (e.g. "MetalGrating"), falling back to a default set.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class FootstepController : MonoBehaviour
{
    [System.Serializable]
    private class SurfaceFootsteps
    {
        [Tooltip("Tag del collider del pavimento (es. 'MetalGrating').")]
        public string surfaceTag = "Untagged";
        public AudioClip[] clips;
    }

    [Header("Audio")]
    [SerializeField] private AudioSource footstepSource;
    [Tooltip("Set usato quando nessun tag corrisponde.")]
    [SerializeField] private AudioClip[] defaultClips;
    [SerializeField] private SurfaceFootsteps[] surfaces;

    [Header("Step Cadence (seconds between steps)")]
    [SerializeField] private float walkStepInterval = 0.5f;
    [SerializeField] private float sprintStepInterval = 0.35f;
    [SerializeField] private float crouchStepInterval = 0.75f;

    [Header("Volume")]
    [Range(0f, 1f)][SerializeField] private float walkVolume = 0.6f;
    [Range(0f, 1f)][SerializeField] private float sprintVolume = 0.85f;
    [Range(0f, 1f)][SerializeField] private float crouchVolume = 0.3f;
    [SerializeField] private float pitchVariation = 0.08f;

    [Header("Detection")]
    [Tooltip("Velocità orizzontale minima per considerare il player 'in movimento'.")]
    [SerializeField] private float moveThreshold = 0.5f;
    [Tooltip("Distanza del raycast verso il basso per leggere la superficie.")]
    [SerializeField] private float groundRayDistance = 1.5f;
    [SerializeField] private LayerMask groundMask = ~0;

    private CharacterController controller;
    private PlayerController playerController; // optional, for sprint/crouch flags

    private float stepTimer;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        playerController = GetComponent<PlayerController>();

        if (footstepSource == null)
        {
            footstepSource = GetComponent<AudioSource>();
        }
    }

    private void Update()
    {
        // Silent when controller is off (ladder / station) or airborne.
        if (controller == null || !controller.enabled || !controller.isGrounded)
        {
            stepTimer = 0f;
            return;
        }

        Vector3 horizontalVelocity = controller.velocity;
        horizontalVelocity.y = 0f;

        if (horizontalVelocity.magnitude < moveThreshold)
        {
            stepTimer = 0f; // reset so the first step after standing still is immediate
            return;
        }

        stepTimer -= Time.deltaTime;
        if (stepTimer <= 0f)
        {
            PlayFootstep();
            stepTimer = GetStepInterval();
        }
    }

    private float GetStepInterval()
    {
        if (playerController != null)
        {
            if (playerController.IsSprinting) return sprintStepInterval;
            if (playerController.IsCrouching) return crouchStepInterval;
        }
        return walkStepInterval;
    }

    private float GetStepVolume()
    {
        if (playerController != null)
        {
            if (playerController.IsSprinting) return sprintVolume;
            if (playerController.IsCrouching) return crouchVolume;
        }
        return walkVolume;
    }

    private void PlayFootstep()
    {
        if (footstepSource == null) return;

        AudioClip[] clips = ResolveSurfaceClips();
        if (clips == null || clips.Length == 0) return;

        AudioClip clip = clips[Random.Range(0, clips.Length)];
        footstepSource.pitch = 1f + Random.Range(-pitchVariation, pitchVariation);
        footstepSource.PlayOneShot(clip, GetStepVolume());
    }

    private AudioClip[] ResolveSurfaceClips()
    {
        if (surfaces != null && surfaces.Length > 0)
        {
            if (Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit, groundRayDistance, groundMask, QueryTriggerInteraction.Ignore))
            {
                foreach (var surface in surfaces)
                {
                    if (surface != null && surface.clips != null && surface.clips.Length > 0 &&
                        hit.collider.CompareTag(surface.surfaceTag))
                    {
                        return surface.clips;
                    }
                }
            }
        }

        return defaultClips;
    }
}
