using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace creepycat.scifikitvol4
{

    /// <summary>
    /// Double door auto-open script with integrated power system
    /// Based on CreepyCat's original script with added IPowerConsumer
    /// </summary>
    public class DoubleDoorOpenAuto : MonoBehaviour, IPowerConsumer
    {
        [Header("Door Parts")]
        public Transform doorL = null;
        public Transform doorR = null;

        public enum Direction { X, Y, Z };
        public Direction directionType = Direction.Y;
        public float speed = 2.0f;
        public float openDistance = 2.0f;

        [Header("Power Settings")]
        public bool requiresPower = true;
        public float powerConsumption = 5f; // Watts when operating
        public int priority = 5; // Priority for power management (1-10, 10 = critical)

        [Header("Audio")]
        public AudioClip openSound;
        public AudioClip closeSound;
        public AudioClip deniedSound; // When door can't open (no power)

        // Internal state
        private Vector3 initialDoorL;
        private Vector3 initialDoorR;
        private Vector3 doorDirection;
        private float point = 0.0f;
        private bool opening = false;
        private bool isPowered = true;
        private int objectsInTrigger = 0;
        private AudioSource audioSource;
        private bool lastOpeningState = false;

        void Start()
        {
            // Record initial positions
            if (doorL)
            {
                initialDoorL = doorL.localPosition;
            }

            if (doorR)
            {
                initialDoorR = doorR.localPosition;
            }

            // Setup audio source
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
            }

            // Register with PowerManager
            if (requiresPower && PowerManager.Instance != null)
            {
                PowerManager.Instance.RegisterPowerConsumer(this);
                PowerManager.Instance.OnPowerRestored += OnPowerRestoredEvent;
            }
        }

        void OnDestroy()
        {
            // Unregister from PowerManager
            if (requiresPower && PowerManager.Instance != null)
            {
                PowerManager.Instance.UnregisterPowerConsumer(this);
                PowerManager.Instance.OnPowerRestored -= OnPowerRestoredEvent;
            }
        }

        // Something approaching? Open doors
        void OnTriggerEnter(Collider other)
        {
            if (other.gameObject.CompareTag("Player") || other.gameObject.CompareTag("MainCamera"))
            {
                objectsInTrigger++;

                // Only play sound if door is actually closed or closing
                bool wasClosedOrClosing = (point < 0.5f);

                // Check if door can open
                if (CanOpen())
                {
                    opening = true;

                    // Only play sound if door wasn't already open
                    if (wasClosedOrClosing)
                    {
                        PlaySound(openSound);
                    }
                }
                else
                {
                    // Door can't open (no power) - only play denied if trying to open
                    if (wasClosedOrClosing)
                    {
                        PlaySound(deniedSound);
                    }
                }
            }
        }

        // Something left? Close doors
        void OnTriggerExit(Collider other)
        {
            if (other.gameObject.CompareTag("Player") || other.gameObject.CompareTag("MainCamera"))
            {
                objectsInTrigger--;
                objectsInTrigger = Mathf.Max(0, objectsInTrigger);

                // Only close if no one in trigger
                if (objectsInTrigger == 0)
                {
                    // Only play sound if door was actually open
                    bool wasOpen = (point > 0.5f);

                    opening = false;

                    if (wasOpen)
                    {
                        PlaySound(closeSound);
                    }
                }
            }
        }

        // Open or close doors
        void Update()
        {
            // Determine door direction
            if (directionType == Direction.X)
            {
                doorDirection = Vector3.right;
            }
            else if (directionType == Direction.Y)
            {
                doorDirection = Vector3.up;
            }
            else if (directionType == Direction.Z)
            {
                doorDirection = Vector3.back;
            }

            // Check power status - if power lost while opening, close immediately
            if (opening && !CanOpen())
            {
                opening = false;
                PlaySound(deniedSound);
            }

            // Animate door position
            if (opening)
            {
                point = Mathf.Lerp(point, 1.0f, Time.deltaTime * speed);
            }
            else
            {
                point = Mathf.Lerp(point, 0.0f, Time.deltaTime * speed);
            }

            // Move doors
            if (doorL)
            {
                doorL.localPosition = initialDoorL + (doorDirection * point * openDistance);
            }

            if (doorR)
            {
                doorR.localPosition = initialDoorR + (-doorDirection * point * openDistance);
            }

            // Track state change for audio
            lastOpeningState = opening;
        }

        // ===== POWER SYSTEM INTEGRATION =====

        private bool CanOpen()
        {
            // If doesn't require power, always can open
            if (!requiresPower)
            {
                return true;
            }

            // Check if has power
            return isPowered;
        }

        // IPowerConsumer implementation
        public float GetPowerDemand()
        {
            // Only consume power when actively opening/closing (moving)
            if (Mathf.Abs(point - (opening ? 1.0f : 0.0f)) > 0.01f)
            {
                return powerConsumption;
            }
            return 0f; // No power when static
        }

        public int GetPriority()
        {
            return priority;
        }

        public bool IsActive()
        {
            return requiresPower && Mathf.Abs(point - (opening ? 1.0f : 0.0f)) > 0.01f;
        }

        public bool CanBeDisabled()
        {
            return priority < 10; // Can be disabled if not critical
        }

        public void SetPowerState(bool isOn)
        {
            isPowered = isOn;

            if (!isOn && opening)
            {
                // Power lost - emergency close
                opening = false;
                PlaySound(deniedSound);
                Debug.LogWarning($"[Door {gameObject.name}] Power lost - closing door");
            }
        }
        private void OnPowerRestoredEvent()
        {
            isPowered = true;
        }

        public string GetSystemName()
        {
            return $"Door: {gameObject.name}";
        }

        // ===== AUDIO MANAGEMENT =====

        private void PlaySound(AudioClip clip)
        {
            if (clip != null && audioSource != null)
            {
                // Stop current sound
                audioSource.Stop();

                // Play new sound
                audioSource.clip = clip;
                audioSource.Play();
            }
        }

        // ===== DEBUG =====

        void OnDrawGizmos()
        {
            // Draw trigger area (if has trigger collider)
            Collider trigger = GetComponent<Collider>();
            if (trigger != null && trigger.isTrigger)
            {
                Gizmos.color = isPowered ? Color.green : Color.red;
                Gizmos.DrawWireCube(transform.position + trigger.bounds.center, trigger.bounds.size);
            }
        }
    }
}