using UnityEngine;
using Unity.Netcode;

namespace creepycat.scifikitvol4
{
    /// <summary>
    /// Double door auto-open — NetworkBehaviour (NGO v2) con client-side prediction.
    /// 
    /// Server: authority su netOpening e netPoint.
    /// Client: anima localPoint autonomamente (prediction immediata),
    ///         corregge gradualmente verso netPoint quando arriva la risposta del server.
    /// </summary>
    public class DoubleDoorOpenAuto : NetworkBehaviour, IPowerConsumer
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

        [Header("Network")]
        [Tooltip("Velocità di correzione del client verso il valore autoritativo del server (0 = no correzione, 1 = snap immediato).")]
        [SerializeField] private float correctionSpeed = 5f;

        // ===== NetworkVariables =====
        private NetworkVariable<bool> netOpening = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private NetworkVariable<float> netPoint = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private NetworkVariable<bool> netPowered = new NetworkVariable<bool>(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ===== Stato locale =====
        private Vector3 initialDoorL;
        private Vector3 initialDoorR;
        private Vector3 doorDirection;
        private float localPoint = 0f;      // animazione client-side (prediction)
        private bool localOpening = false;  // stato predetto dal client
        private AudioSource audioSource;

        // ===== Stato server-only =====
        private int objectsInTrigger = 0;
        private PowerManager powerManager;

        // ===== NGO Lifecycle =====

        public override void OnNetworkSpawn()
        {
            if (doorL) initialDoorL = doorL.localPosition;
            if (doorR) initialDoorR = doorR.localPosition;

            audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();

            // Sync stato iniziale
            localPoint = netPoint.Value;
            localOpening = netOpening.Value;

            netOpening.OnValueChanged += OnOpeningChanged;
            netPowered.OnValueChanged += OnPoweredChanged;

            if (IsServer && requiresPower)
            {
                if (PowerManager.Instance != null)
                    InitWithPowerManager();
                else
                    PowerManager.OnInstanceReady += InitWithPowerManager;
            }
        }

        public override void OnNetworkDespawn()
        {
            netOpening.OnValueChanged -= OnOpeningChanged;
            netPowered.OnValueChanged -= OnPoweredChanged;
            PowerManager.OnInstanceReady -= InitWithPowerManager;

            if (IsServer && requiresPower && powerManager != null)
            {
                powerManager.UnregisterPowerConsumer(this);
                powerManager.OnPowerRestored -= OnPowerRestoredEvent;
            }
        }

        private void InitWithPowerManager()
        {
            PowerManager.OnInstanceReady -= InitWithPowerManager;
            powerManager = PowerManager.Instance;
            powerManager.RegisterPowerConsumer(this);
            powerManager.OnPowerRestored += OnPowerRestoredEvent;
        }

        // ===== Trigger =====

        void OnTriggerEnter(Collider other)
        {
            if (!other.gameObject.CompareTag("Player") && !other.gameObject.CompareTag("MainCamera")) return;

            // Client: prediction immediata — anima senza aspettare il server
            if (!IsServer)
                localOpening = true;

            RequestOpenRpc();
        }

        void OnTriggerExit(Collider other)
        {
            if (!other.gameObject.CompareTag("Player") && !other.gameObject.CompareTag("MainCamera")) return;

            // Client: prediction immediata
            if (!IsServer)
                localOpening = false;

            RequestCloseRpc();
        }

        [Rpc(SendTo.Server)]
        private void RequestOpenRpc()
        {
            objectsInTrigger++;

            if (!CanOpen())
            {
                PlaySoundClientRpc(SoundType.Denied);
                return;
            }

            if (!netOpening.Value)
                netOpening.Value = true;
        }

        [Rpc(SendTo.Server)]
        private void RequestCloseRpc()
        {
            objectsInTrigger = Mathf.Max(0, objectsInTrigger - 1);

            if (objectsInTrigger == 0 && netOpening.Value)
                netOpening.Value = false;
        }

        // ===== Update =====

        void Update()
        {
            // Direzione porta
            switch (directionType)
            {
                case Direction.X: doorDirection = Vector3.right; break;
                case Direction.Y: doorDirection = Vector3.up; break;
                case Direction.Z: doorDirection = Vector3.back; break;
            }

            if (IsServer)
            {
                // SERVER: anima netPoint — autoritativo
                float target = netOpening.Value ? 1f : 0f;
                netPoint.Value = Mathf.Lerp(netPoint.Value, target, Time.deltaTime * speed);

                if (netOpening.Value && !CanOpen())
                {
                    netOpening.Value = false;
                    PlaySoundClientRpc(SoundType.Denied);
                }

                // Server usa netPoint direttamente
                localPoint = netPoint.Value;
            }
            else
            {
                // CLIENT: anima localPoint verso localOpening (prediction immediata)
                float predictedTarget = localOpening ? 1f : 0f;
                localPoint = Mathf.Lerp(localPoint, predictedTarget, Time.deltaTime * speed);

                // Corregge gradualmente verso netPoint (risposta server)
                // correctionSpeed bassa = correzione morbida, alta = snap
                localPoint = Mathf.Lerp(localPoint, netPoint.Value, Time.deltaTime * correctionSpeed);
            }

            // Tutti animano le porte con localPoint
            if (doorL) doorL.localPosition = initialDoorL + (doorDirection * localPoint * openDistance);
            if (doorR) doorR.localPosition = initialDoorR + (-doorDirection * localPoint * openDistance);
        }

        // ===== Callbacks NetworkVariable =====

        private void OnOpeningChanged(bool previous, bool current)
        {
            localOpening = current;

            // Se il server nega la prediction (porta senza corrente),
            // snap immediato a netPoint per evitare correzione visibile
            if (!current && previous && !IsServer)
                localPoint = netPoint.Value;

            if (current && !previous) PlaySound(openSound);
            else if (!current && previous) PlaySound(closeSound);
        }

        private void OnPoweredChanged(bool previous, bool current) { }

        // ===== Audio =====

        private enum SoundType { Open, Close, Denied }

        [Rpc(SendTo.ClientsAndHost)]
        private void PlaySoundClientRpc(SoundType type)
        {
            AudioClip clip = type switch
            {
                SoundType.Open => openSound,
                SoundType.Close => closeSound,
                SoundType.Denied => deniedSound,
                _ => null
            };
            PlaySound(clip);
        }

        private void PlaySound(AudioClip clip)
        {
            if (clip != null && audioSource != null)
            {
                audioSource.Stop();
                audioSource.clip = clip;
                audioSource.Play();
            }
        }

        // ===== IPowerConsumer (server only) =====

        private bool CanOpen() => !requiresPower || netPowered.Value;

        public float GetPowerDemand()
        {
            return Mathf.Abs(netPoint.Value - (netOpening.Value ? 1f : 0f)) > 0.01f
                ? powerConsumption : 0f;
        }

        public int GetPriority() => priority;

        public bool IsActive()
        {
            return requiresPower && Mathf.Abs(netPoint.Value - (netOpening.Value ? 1f : 0f)) > 0.01f;
        }

        public bool CanBeDisabled() => priority < 10;

        public void SetPowerState(bool isOn)
        {
            if (!IsServer) return;
            netPowered.Value = isOn;

            if (!isOn && netOpening.Value)
            {
                netOpening.Value = false;
                PlaySoundClientRpc(SoundType.Denied);
                Debug.LogWarning($"[Door {gameObject.name}] Power lost - closing door");
            }
        }

        private void OnPowerRestoredEvent()
        {
            if (!IsServer) return;
            netPowered.Value = true;
            Debug.Log($"[Door {gameObject.name}] Power restored - door re-armed");
        }

        public string GetSystemName() => $"Door: {gameObject.name}";

        void OnDrawGizmos()
        {
            Collider trigger = GetComponent<Collider>();
            if (trigger != null && trigger.isTrigger)
            {
                Gizmos.color = netPowered.Value ? Color.green : Color.red;
                Gizmos.DrawWireCube(transform.position + trigger.bounds.center, trigger.bounds.size);
            }
        }
    }
}