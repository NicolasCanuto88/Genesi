using UnityEngine;
using System.Collections.Generic;

namespace creepycat.toolbox
{
    [AddComponentMenu("creepycat/Toolbox/Turret Controller")]
    public class TurretController : MonoBehaviour
    {
        [Header("Turret Hierarchy")]
        public Transform turretBase;
        public List<Transform> cannons = new List<Transform>();
        public List<Transform> shootPoints = new List<Transform>();

        [Header("Recoil Settings")]
        public bool enableRecoil = true;
        public float recoilDistance = 0.15f;
        public Vector3 recoilAxis = new Vector3(0, 0, -1);
        public float recoilTimeUp = 0.05f;
        public float recoilTimeBack = 0.3f;

        [Header("Target")]
        public Transform target;

        [Header("Rotation Parameters")]
        public float yawSpeed = 90f;
        public float pitchSpeed = 45f;

        [Header("Pitch Limits")]
        public float minPitchAngle = -20f;
        public float maxPitchAngle = 60f;

        [Header("Rotation Options")]
        public bool invertPitch = false;
        public float maxTargetDistance = 0f;
        [Range(0f, 360f)]
        public float fieldOfViewAngle = 360f;

        [Header("Firing Control")]
        public GameObject projectilePrefab;
        public float projectileSpeed = 50f;
        public float fireRate = 2f;

        [Header("Muzzle Flash")]
        public GameObject muzzleFlashPrefab;

        [Header("Firing Direction")]
        public bool preciseAimAtTarget = false;

        [Header("Audio")]
        public AudioClip idleSound;
        [Range(0f, 1f)] public float idleVolume = 0.4f;
        public AudioClip attackStartSound;
        [Range(0f, 1f)] public float attackStartVolume = 0.7f;
        public AudioClip attackEndSound;
        [Range(0f, 1f)] public float attackEndVolume = 0.6f;
        public AudioClip shotSound;
        [Range(0f, 1f)] public float shotVolume = 0.8f;
        [Range(0f, 1f)] public float shotPitchVariation = 0.2f;

        [Header("Audio Transitions")]
        [Tooltip("Délai minimum entre changements d'état pour éviter les transitions trop rapides")]
        public float minTransitionDelay = 0.2f;

        [Header("Gizmos")]
        public bool showGizmos = true;
        public Color gizmoDistanceColor = new Color(1f, 0.5f, 0f, 0.3f);
        public Color gizmoFOVColor = new Color(0f, 1f, 0f, 0.2f);
        public int gizmoSegments = 32;

        [Header("Auto-fire")]
        public bool autoFire = true;

        private AudioSource _audioSource;
        private AudioSource _transitionAudioSource; // Pour les sons de transition
        private Vector3 _yawVelocity;
        private float _pitchVelocity;
        private float _lastFireTime;
        private int _currentShootPointIndex = 0;

        private bool _wasInRangeLastFrame = false;
        private bool _canFire = false;
        private float _attackStartSoundEndTime = 0f;
        private float _lastTransitionTime = 0f;
        private bool _isFirstFrame = true;

        // États audio pour éviter les conflits
        private enum AudioState { Idle, TransitionToAttack, Attacking, TransitionToIdle }
        private AudioState _currentAudioState = AudioState.Idle;

        private class CannonRecoil
        {
            public Transform cannon;
            public Vector3 initialLocalPos;
            public Vector3 recoilOffset;
            public float timer;
            public float totalTime;
            public bool isRecoiling;
        }

        private List<CannonRecoil> _recoilStates = new List<CannonRecoil>();

        [HideInInspector] public Transform turretCannon;
        [HideInInspector] public Transform shootPoint;

        void Start()
        {
            // AudioSource principal pour idle et sons continus
            _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
            {
                _audioSource = gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.spatialBlend = 1f;
            }

            // AudioSource secondaire pour les transitions (évite les interruptions)
            _transitionAudioSource = gameObject.AddComponent<AudioSource>();
            _transitionAudioSource.playOnAwake = false;
            _transitionAudioSource.spatialBlend = 1f;

            if (idleSound != null)
            {
                _audioSource.clip = idleSound;
                _audioSource.volume = idleVolume;
                _audioSource.loop = true;
                _audioSource.Play();
                _currentAudioState = AudioState.Idle;
            }

            if (cannons.Count == 0 && turretCannon != null)
                cannons.Add(turretCannon);
            if (shootPoints.Count == 0 && shootPoint != null)
                shootPoints.Add(shootPoint);

            _recoilStates.Clear();
            foreach (Transform cannon in cannons)
            {
                if (cannon == null) continue;
                _recoilStates.Add(new CannonRecoil
                {
                    cannon = cannon,
                    initialLocalPos = cannon.localPosition
                });
            }
        }

        void LateUpdate()
        {
            bool hasValidTarget = IsTargetValid();

            // Éviter les transitions trop rapides
            bool canTransition = Time.time >= _lastTransitionTime + minTransitionDelay;

            // Au premier frame, si la cible est déjà là, forcer la transition
            if (_isFirstFrame && hasValidTarget)
            {
                _wasInRangeLastFrame = false;
                canTransition = true;
                _isFirstFrame = false;
            }

            if (hasValidTarget)
            {
                if (canTransition)
                    HandleAttackStartTransition(hasValidTarget);
                
                if (Time.time >= _attackStartSoundEndTime)
                    _canFire = true;
            }
            else
            {
                if (canTransition)
                    HandleAttackEndTransition(hasValidTarget);
                
                _canFire = false;
            }

            _wasInRangeLastFrame = hasValidTarget;

            if (!hasValidTarget || turretBase == null || cannons.Count == 0 || shootPoints.Count == 0)
                return;

            Vector3 turretPos = turretBase.position;
            Vector3 directionToTarget = (target.position - turretPos).normalized;

            Vector3 horizontalDir = new Vector3(directionToTarget.x, 0f, directionToTarget.z).normalized;
            Quaternion desiredYawRotation = Quaternion.LookRotation(horizontalDir);
            float targetYaw = desiredYawRotation.eulerAngles.y;
            float currentYaw = turretBase.eulerAngles.y;
            float smoothedYaw = Mathf.SmoothDampAngle(
                currentYaw, targetYaw, ref _yawVelocity.y, 1f / yawSpeed);
            turretBase.rotation = Quaternion.Euler(0f, smoothedYaw, 0f);

            Vector3 localTargetDir = turretBase.InverseTransformDirection(directionToTarget);
            float pitchAngle = Mathf.Atan2(localTargetDir.y, localTargetDir.z) * Mathf.Rad2Deg;
            if (invertPitch) pitchAngle = -pitchAngle;
            pitchAngle = Mathf.Clamp(pitchAngle, minPitchAngle, maxPitchAngle);

            foreach (Transform cannon in cannons)
            {
                if (cannon == null) continue;

                float currentPitch = cannon.localEulerAngles.x;
                if (currentPitch > 180f) currentPitch -= 360f;

                float smoothedPitch = Mathf.SmoothDampAngle(
                    currentPitch, pitchAngle, ref _pitchVelocity, 1f / pitchSpeed);

                cannon.localRotation = Quaternion.Euler(smoothedPitch, 0f, 0f);
            }

            if (enableRecoil)
            {
                foreach (var recoil in _recoilStates)
                {
                    if (!recoil.isRecoiling) continue;

                    recoil.timer += Time.deltaTime;

                    if (recoil.timer <= recoilTimeUp)
                    {
                        float t = recoil.timer / recoilTimeUp;
                        recoil.cannon.localPosition =
                            recoil.initialLocalPos + recoil.recoilOffset * t;
                    }
                    else
                    {
                        float returnT = (recoil.timer - recoilTimeUp) / recoilTimeBack;
                        float eased = 1f - Mathf.Pow(1f - returnT, 3f);
                        recoil.cannon.localPosition = recoil.initialLocalPos + recoil.recoilOffset * (1f - eased);
                    }

                    if (recoil.timer >= recoilTimeUp + recoilTimeBack)
                    {
                        recoil.cannon.localPosition = recoil.initialLocalPos;
                        recoil.isRecoiling = false;
                    }
                }
            }

            if (autoFire && _canFire && projectilePrefab != null &&
                Time.time >= _lastFireTime + (1f / fireRate))
            {
                Fire();
            }
        }

        private void HandleAttackStartTransition(bool inRangeNow)
        {
            if (inRangeNow && !_wasInRangeLastFrame && 
                _currentAudioState != AudioState.TransitionToAttack && 
                _currentAudioState != AudioState.Attacking)
            {
                _canFire = false;
                _lastTransitionTime = Time.time;
                _currentAudioState = AudioState.TransitionToAttack;

                // Annuler les invokes en cours
                CancelInvoke(nameof(RestartIdle));

                if (attackStartSound != null)
                {
                    _audioSource.Stop();
                    _transitionAudioSource.Stop();
                    _transitionAudioSource.PlayOneShot(attackStartSound, attackStartVolume);
                    _attackStartSoundEndTime = Time.time + attackStartSound.length;
                    Invoke(nameof(SetAttackingState), attackStartSound.length);
                }
                else
                {
                    _canFire = true;
                    _attackStartSoundEndTime = Time.time;
                    _currentAudioState = AudioState.Attacking;
                }
            }
        }

        private void HandleAttackEndTransition(bool inRangeNow)
        {
            if (!inRangeNow && _wasInRangeLastFrame && 
                (_currentAudioState == AudioState.Attacking || 
                 _currentAudioState == AudioState.TransitionToAttack))
            {
                _canFire = false;
                _lastTransitionTime = Time.time;
                _currentAudioState = AudioState.TransitionToIdle;

                // Annuler les invokes en cours
                CancelInvoke(nameof(SetAttackingState));

                // Reset complet des recoils
                ResetAllRecoil();

                _audioSource.Stop();
                _transitionAudioSource.Stop();

                if (attackEndSound != null)
                {
                    _transitionAudioSource.PlayOneShot(attackEndSound, attackEndVolume);
                    Invoke(nameof(RestartIdle), attackEndSound.length);
                }
                else
                {
                    RestartIdle();
                }
            }
        }

        private void SetAttackingState()
        {
            _currentAudioState = AudioState.Attacking;
        }

        private void ResetAllRecoil()
        {
            foreach (var recoil in _recoilStates)
            {
                if (recoil.cannon == null) continue;

                recoil.cannon.localPosition = recoil.initialLocalPos;
                recoil.isRecoiling = false;
                recoil.timer = 0f;
            }
        }

        private void RestartIdle()
        {
            if (idleSound != null)
            {
                _audioSource.Stop();
                _transitionAudioSource.Stop();
                _audioSource.clip = idleSound;
                _audioSource.volume = idleVolume;
                _audioSource.pitch = 1f;
                _audioSource.loop = true;
                _audioSource.Play();
                _currentAudioState = AudioState.Idle;
            }
        }

        public void Fire()
        {
            Transform currentShootPoint = shootPoints[_currentShootPointIndex];
            _currentShootPointIndex = (_currentShootPointIndex + 1) % shootPoints.Count;

            if (currentShootPoint == null) return;

            if (muzzleFlashPrefab != null)
                Instantiate(muzzleFlashPrefab, currentShootPoint.position,
                            currentShootPoint.rotation, currentShootPoint);

            GameObject projectile = Instantiate(
                projectilePrefab, currentShootPoint.position, currentShootPoint.rotation);

            Rigidbody rb = projectile.GetComponent<Rigidbody>();
            if (rb != null)
            {
                Vector3 dir = preciseAimAtTarget && target != null
                    ? (target.position - currentShootPoint.position).normalized
                    : currentShootPoint.forward;

                rb.linearVelocity = dir * projectileSpeed;
            }

            if (enableRecoil)
            {
                Transform firingCannon = currentShootPoint.parent;
                CannonRecoil recoil =
                    _recoilStates.Find(r => r.cannon == firingCannon);

                if (recoil != null && recoilAxis != Vector3.zero)
                {
                    Vector3 normalizedAxis = recoilAxis.normalized;
                    Vector3 worldDir = firingCannon.TransformDirection(normalizedAxis);
                    Vector3 localDir = firingCannon.parent.InverseTransformDirection(worldDir);

                    recoil.recoilOffset = localDir * recoilDistance;
                    recoil.timer = 0f;
                    recoil.isRecoiling = true;
                }
            }

            if (shotSound != null)
            {
                _audioSource.pitch = 1f + Random.Range(-shotPitchVariation, shotPitchVariation);
                _audioSource.PlayOneShot(shotSound, shotVolume);
            }

            _lastFireTime = Time.time;
        }

        public void SetTarget(Transform newTarget) => target = newTarget;
        public void ClearTarget() => target = null;

        void OnDestroy()
        {
            CancelInvoke();
        }

        private bool IsTargetValid()
        {
            if (target == null) return false;

            Vector3 turretPos = turretBase != null ? turretBase.position : transform.position;
            Vector3 toTarget = target.position - turretPos;
            float distance = toTarget.magnitude;

            // Vérification distance
            if (maxTargetDistance > 0f && distance > maxTargetDistance)
                return false;

            // Vérification champ de vision
            if (fieldOfViewAngle < 360f)
            {
                Vector3 turretForward = turretBase != null ? turretBase.forward : transform.forward;
                float angle = Vector3.Angle(turretForward, toTarget);
                
                if (angle > fieldOfViewAngle / 2f)
                    return false;
            }

            return true;
        }

        void OnDrawGizmosSelected()
        {
            if (!showGizmos) return;

            Vector3 origin = turretBase != null ? turretBase.position : transform.position;
            Vector3 forward = turretBase != null ? turretBase.forward : transform.forward;

            // Gizmo de distance (cercle complet)
            if (maxTargetDistance > 0f)
            {
                Gizmos.color = gizmoDistanceColor;
                DrawCircle(origin, maxTargetDistance, gizmoSegments);
            }

            // Gizmo du champ de vision
            if (fieldOfViewAngle < 360f)
            {
                Gizmos.color = gizmoFOVColor;
                float radius = maxTargetDistance > 0f ? maxTargetDistance : 20f;
                DrawFieldOfView(origin, forward, fieldOfViewAngle, radius, gizmoSegments);
            }
            else if (maxTargetDistance > 0f)
            {
                // Si FOV = 360° et qu'on a une distance, afficher un cercle plein
                Gizmos.color = gizmoFOVColor;
                DrawCircle(origin, maxTargetDistance, gizmoSegments);
            }
        }

        private void DrawCircle(Vector3 center, float radius, int segments)
        {
            float angleStep = 360f / segments;
            Vector3 prevPoint = center + new Vector3(radius, 0, 0);

            for (int i = 1; i <= segments; i++)
            {
                float angle = angleStep * i * Mathf.Deg2Rad;
                Vector3 newPoint = center + new Vector3(
                    Mathf.Cos(angle) * radius,
                    0,
                    Mathf.Sin(angle) * radius
                );
                Gizmos.DrawLine(prevPoint, newPoint);
                prevPoint = newPoint;
            }
        }

        private void DrawFieldOfView(Vector3 origin, Vector3 forward, float fovAngle, float radius, int segments)
        {
            float halfAngle = fovAngle / 2f;
            
            // Lignes des bords du FOV
            Vector3 leftBoundary = Quaternion.Euler(0, -halfAngle, 0) * forward * radius;
            Vector3 rightBoundary = Quaternion.Euler(0, halfAngle, 0) * forward * radius;
            
            Gizmos.DrawLine(origin, origin + leftBoundary);
            Gizmos.DrawLine(origin, origin + rightBoundary);

            // Arc du FOV
            int arcSegments = Mathf.Max(1, (int)(segments * (fovAngle / 360f)));
            float angleStep = fovAngle / arcSegments;
            Vector3 prevPoint = origin + leftBoundary;

            for (int i = 1; i <= arcSegments; i++)
            {
                float angle = -halfAngle + (angleStep * i);
                Vector3 direction = Quaternion.Euler(0, angle, 0) * forward;
                Vector3 newPoint = origin + direction * radius;
                Gizmos.DrawLine(prevPoint, newPoint);
                prevPoint = newPoint;
            }
        }
    }
}