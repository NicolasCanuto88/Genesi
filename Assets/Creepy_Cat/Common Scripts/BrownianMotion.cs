// Features:
// -----------------
// Target: Assigns a Transform to follow.
// Mode: Position only, Rotation only, or both.
// Position Offset: Follows at a relative distance/direction (e.g., camera behind the player).
// SmoothDamp: Smooth interpolation that is frame-rate independent (better than Lerp for following).
// SmoothDampAngle for rotation: avoids angle jumps.
// SmoothTime = 0 → instant follow.
// Two useful contextual buttons in the Inspector:
// Capture the current offset.
// Align immediately to the target.

// Usage examples:
// ------------------------
// Third-person camera: followPosition + followRotation, offset = (0, 2, -5), positionSmoothTime = 0.3, rotationSmoothTime = 0.1
// Fixed cinematic camera: followPosition only, custom offset
// Object that follows a player (e.g., halo, shadow, 3D UI): followBoth, low smoothTime
// Drone that follows: higher rotationSmoothTime for a "lagging" effect

// CyclicMotion
// BrownianMotion
// LinearMotion
// RandomJump
// SmoothFollow

using UnityEngine;

namespace creepycat.scifikitvol4 
{
    public class BrownianMotion : MonoBehaviour
    {
        public enum MotionSpace { Local, World }

        [Header("Movement Type")]
        public MotionSpace motionSpace = MotionSpace.Local;

        [Header("Brownian Motion Parameters")]
        [Tooltip("Displacement amplitude per axis")]
        [SerializeField]
        public Vector3 amplitude = Vector3.one;

        [Tooltip("Base frequency (noise scale)")]
        public float frequency = 1f;

        [Tooltip("Number of octaves (more = more detail, but more expensive)")]
        [Range(1, 8)]
        public int octaves = 4;

        [Tooltip("Lacunarity (frequency multiplier per octave)")]
        public float lacunarity = 2f;

        [Tooltip("Persistence (amplitude multiplier per octave)")]
        [Range(0f, 1f)]
        public float persistence = 0.5f;

        [Tooltip("Animation speed of the noise over time")]
        public float speed = 1f;

        [Tooltip("Random seed (change for a different movement pattern)")]
        public int seed = 0;

        private Vector3 initialPosition;
        private Vector3 noiseOffset;

        void Start()
        {
            initialPosition = motionSpace == MotionSpace.Local ? transform.localPosition : transform.position;
            Random.InitState(seed);
            noiseOffset = new Vector3(
                Random.Range(-1000f, 1000f),
                Random.Range(-1000f, 1000f),
                Random.Range(-1000f, 1000f)
            );
        }

        void Update()
        {
            float time = Time.time * speed;
            Vector3 offset = Vector3.zero;
            float amp = 1f;
            float freq = frequency;

            for (int i = 0; i < octaves; i++)
            {
                float x = (noiseOffset.x + time) * freq;
                float y = (noiseOffset.y + time) * freq;
                float z = (noiseOffset.z + time) * freq;

                Vector3 perlinSample = new Vector3(
                    Mathf.PerlinNoise(x, y) - 0.5f,
                    Mathf.PerlinNoise(y, z) - 0.5f,
                    Mathf.PerlinNoise(z, x) - 0.5f
                ) * 2f;

                offset += perlinSample * amp;
                amp *= persistence;
                freq *= lacunarity;
            }

            // Appliquer l'amplitude par axe
            offset = Vector3.Scale(offset, amplitude);

            Vector3 newPosition = initialPosition + offset;

            if (motionSpace == MotionSpace.Local)
            {
                transform.localPosition = newPosition;
            }
            else
            {
                transform.position = newPosition;
            }
        }

        [ContextMenu("Recalibrate initial position")]
        private void RecalibrateInitialPosition()
        {
            initialPosition = motionSpace == MotionSpace.Local ? transform.localPosition : transform.position;
        }
    }
}