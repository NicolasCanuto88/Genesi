using UnityEngine;

namespace creepycat.scifikitvol4
{
    /// <summary>
    /// Double door auto-open script with integrated power system.
    /// Based on CreepyCat's original script with added IPowerConsumer.
    /// Usa PowerManager.OnInstanceReady per gestire l'ordine di spawn NGO.
    /// </summary>
    public class DoubleDoorOpenAuto : MonoBehaviour, IPowerConsumer
    {
        [Header("Door Parts")]
        public Transform doorL = null;
        public Transform doorR = null;

        public enum Direction { X, Y, Z }
        public Direction directionType = Direction.Y;
        public float speed = 2.0f;
        public float openDistance = 2.0f;

        [Header("Power Settings")]
        public bool requiresPower = true;
        public float powerConsumption = 5f;
        public int priority = 5;

        [Header("Audio")]
        public AudioClip openSound;
        public AudioClip closeSound;
        public AudioClip deniedSound;

        private Vector3 initialDoorL;
        private Vector3 initialDoorR;
        private Vector3 doorDirection;
        private float point = 0.0f;
        private bool opening = false;
        private bool isPowered = true;
        private int objectsInTrigger = 0;
        private AudioSource audioSource;
        private bool lastOpeningState = false;

        private PowerManager powerManager;

        void Start()
        {
            if (doorL) initialDoorL = doorL.localPosition;
            if (doorR) initialDoorR = doorR.localPosition;

            audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();

            if (requiresPower)
            {
                if (PowerManager.Instance != null)
                    InitWithPowerManager();
                else
                    PowerManager.OnInstanceReady += InitWithPowerManager;
            }
        }

        private void InitWithPowerManager()
        {
            PowerManager.OnInstanceReady -= InitWithPowerManager;
            powerManager = PowerManager.Instance;
            powerManager.RegisterPowerConsumer(this);
            powerManager.OnPowerRestored += OnPowerRestoredEvent;
        }

        void OnDestroy()
        {
            PowerManager.OnInstanceReady -= InitWithPowerManager;

            if (requiresPower && powerManager != null)
            {
                powerManager.UnregisterPowerConsumer(this);
                powerManager.OnPowerRestored -= OnPowerRestoredEvent;
            }
        }

        void OnTriggerEnter(Collider other)
        {
            if (other.gameObject.CompareTag("Player") || other.gameObject.CompareTag("MainCamera"))
            {
                objectsInTrigger++;
                bool wasClosedOrClosing = (point < 0.5f);

                if (CanOpen())
                {
                    opening = true;
                    if (wasClosedOrClosing) PlaySound(openSound);
                }
                else
                {
                    if (wasClosedOrClosing) PlaySound(deniedSound);
                }
            }
        }

        void OnTriggerExit(Collider other)
        {
            if (other.gameObject.CompareTag("Player") || other.gameObject.CompareTag("MainCamera"))
            {
                objectsInTrigger--;
                objectsInTrigger = Mathf.Max(0, objectsInTrigger);

                if (objectsInTrigger == 0)
                {
                    bool wasOpen = (point > 0.5f);
                    opening = false;
                    if (wasOpen) PlaySound(closeSound);
                }
            }
        }

        void Update()
        {
            switch (directionType)
            {
                case Direction.X: doorDirection = Vector3.right; break;
                case Direction.Y: doorDirection = Vector3.up; break;
                case Direction.Z: doorDirection = Vector3.back; break;
            }

            if (opening && !CanOpen())
            {
                opening = false;
                PlaySound(deniedSound);
            }

            point = opening
                ? Mathf.Lerp(point, 1.0f, Time.deltaTime * speed)
                : Mathf.Lerp(point, 0.0f, Time.deltaTime * speed);

            if (doorL) doorL.localPosition = initialDoorL + (doorDirection * point * openDistance);
            if (doorR) doorR.localPosition = initialDoorR + (-doorDirection * point * openDistance);

            lastOpeningState = opening;
        }

        private bool CanOpen()
        {
            if (!requiresPower) return true;
            return isPowered;
        }

        // ===== IPowerConsumer =====

        public float GetPowerDemand()
        {
            return Mathf.Abs(point - (opening ? 1.0f : 0.0f)) > 0.01f ? powerConsumption : 0f;
        }

        public int GetPriority() => priority;

        public bool IsActive()
        {
            return requiresPower && Mathf.Abs(point - (opening ? 1.0f : 0.0f)) > 0.01f;
        }

        public bool CanBeDisabled() => priority < 10;

        public void SetPowerState(bool isOn)
        {
            isPowered = isOn;

            if (!isOn && opening)
            {
                opening = false;
                PlaySound(deniedSound);
                Debug.LogWarning($"[Door {gameObject.name}] Power lost - closing door");
            }
        }

        private void OnPowerRestoredEvent()
        {
            isPowered = true;
            Debug.Log($"[Door {gameObject.name}] Power restored - door re-armed");
        }

        public string GetSystemName() => $"Door: {gameObject.name}";

        private void PlaySound(AudioClip clip)
        {
            if (clip != null && audioSource != null)
            {
                audioSource.Stop();
                audioSource.clip = clip;
                audioSource.Play();
            }
        }

        void OnDrawGizmos()
        {
            Collider trigger = GetComponent<Collider>();
            if (trigger != null && trigger.isTrigger)
            {
                Gizmos.color = isPowered ? Color.green : Color.red;
                Gizmos.DrawWireCube(transform.position + trigger.bounds.center, trigger.bounds.size);
            }
        }
    }
}