using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// FTL Drive System - handles faster-than-light jumps
    /// TO BE IMPLEMENTED in Step 3
    /// </summary>
    public class FTLDrive : ShipSystem
    {
        [Header("FTL Stats (Placeholder)")]
        [SerializeField] private float maxJumpRange = 50f; // AU
        [SerializeField] private float cooldown = 900f; // 15 min
        [SerializeField] private float spinUpTime = 45f; // seconds

        private float cooldownRemaining = 0f;
        private bool isSpinningUp = false;

        public bool CanJump => cooldownRemaining <= 0f && !isSpinningUp && isOperational && isPowered;

        protected override void Start()
        {
            base.Start();
            systemName = "FTL Drive";
            powerDemand = 200f; // High power demand
            priority = 7; // High priority
        }

        protected override void Update()
        {
            base.Update();

            if (cooldownRemaining > 0f)
            {
                cooldownRemaining -= Time.deltaTime;
            }
        }

        // TODO: Implement jump mechanics in Step 3
        public void InitiateJump(Vector3 destination)
        {
            Debug.Log("[FTLDrive] Jump mechanics TO BE IMPLEMENTED in Step 3");
        }
    }
}