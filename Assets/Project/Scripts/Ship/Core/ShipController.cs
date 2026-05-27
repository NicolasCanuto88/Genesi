using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Main ship controller - manages all ship systems
    /// Attach to root ship GameObject
    /// </summary>
    public class ShipController : MonoBehaviour
    {
        [Header("Ship Info")]
        [SerializeField] private string shipName = "Pioneer";
        [SerializeField] private string shipClass = "Light Cargo";

        [Header("Core Systems")]
        [SerializeField] private PropulsionSystem propulsion;
        [SerializeField] private FTLDrive ftlDrive;
        [SerializeField] private ShieldSystem shields;
        [SerializeField] private LifeSupportSystem lifeSupport;
        // Future: ReactorSystem, SensorSystem, etc.

        [Header("Ship State")]
        [SerializeField] private float currentHullIntegrity = 100f;
        [SerializeField] private float maxHullIntegrity = 100f;

        // Properties - Public access for other systems
        public string ShipName => shipName;
        public string ShipClass => shipClass;
        public float HullIntegrity => currentHullIntegrity;
        public float HullIntegrityPercent => currentHullIntegrity / maxHullIntegrity;

        // System References
        public PropulsionSystem Propulsion => propulsion;
        public FTLDrive FTLDrive => ftlDrive;
        public ShieldSystem Shields => shields;
        public LifeSupportSystem LifeSupport => lifeSupport;

        // Convenience properties
        public float CurrentSpeed => propulsion != null ? propulsion.CurrentSpeed : 0f;
        public bool CanJump => ftlDrive != null && ftlDrive.CanJump;
        public bool HasShields => shields != null && shields.IsOperational;

        private void Awake()
        {
            // Auto-find systems if not assigned
            if (propulsion == null) propulsion = GetComponentInChildren<PropulsionSystem>();
            if (ftlDrive == null) ftlDrive = GetComponentInChildren<FTLDrive>();
            if (shields == null) shields = GetComponentInChildren<ShieldSystem>();
            if (lifeSupport == null) lifeSupport = GetComponentInChildren<LifeSupportSystem>();
        }

        private void Start()
        {
            InitializeSystems();
        }

        private void InitializeSystems()
        {
            Debug.Log($"[ShipController] Initializing ship: {shipName} ({shipClass})");

            // Initialize all systems with reference to this controller
            propulsion?.Initialize(this);
            ftlDrive?.Initialize(this);
            shields?.Initialize(this);
            // Note: LifeSupportSystem doesn't extend ShipSystem yet, so no Initialize() call

            Debug.Log($"[ShipController] All systems initialized. Hull: {currentHullIntegrity}/{maxHullIntegrity} HP");
        }

        #region Damage System

        /// <summary>
        /// Apply damage to ship (shields first, then hull)
        /// </summary>
        public void TakeDamage(float damage)
        {
            if (damage <= 0) return;

            // If shields active, they absorb damage first
            if (shields != null && shields.IsOperational && shields.IsPowered)
            {
                float remainingDamage = shields.AbsorbDamage(damage);

                // If shields absorbed all damage, we're done
                if (remainingDamage <= 0)
                {
                    Debug.Log($"[ShipController] Shields absorbed {damage:F1} damage");
                    return;
                }

                // Shields absorbed some, hull takes the rest
                damage = remainingDamage;
                Debug.LogWarning($"[ShipController] Shields overwhelmed! {remainingDamage:F1} damage to hull");
            }

            // Apply damage to hull
            currentHullIntegrity -= damage;
            currentHullIntegrity = Mathf.Max(0f, currentHullIntegrity);

            Debug.LogWarning($"[ShipController] Hull damage: -{damage:F1} HP (Remaining: {currentHullIntegrity:F1}/{maxHullIntegrity})");

            // Check for critical hull
            if (currentHullIntegrity <= maxHullIntegrity * 0.2f)
            {
                Debug.LogError($"[ShipController] CRITICAL HULL INTEGRITY: {HullIntegrityPercent * 100f:F1}%");
            }

            // Check for destruction
            if (currentHullIntegrity <= 0)
            {
                OnShipDestroyed();
            }
        }

        /// <summary>
        /// Repair hull damage
        /// </summary>
        public void RepairHull(float repairAmount)
        {
            if (repairAmount <= 0) return;

            currentHullIntegrity += repairAmount;
            currentHullIntegrity = Mathf.Min(maxHullIntegrity, currentHullIntegrity);

            Debug.Log($"[ShipController] Hull repaired: +{repairAmount:F1} HP (Current: {currentHullIntegrity:F1}/{maxHullIntegrity})");
        }

        private void OnShipDestroyed()
        {
            Debug.LogError("[ShipController] ⚠⚠⚠ SHIP DESTROYED ⚠⚠⚠");
            // TODO: Trigger game over / respawn logic
        }

        #endregion

        #region System Access Helpers

        /// <summary>
        /// Get system by type (for generic access)
        /// </summary>
        public T GetSystem<T>() where T : ShipSystem
        {
            return GetComponentInChildren<T>();
        }

        /// <summary>
        /// Check if specific system is operational
        /// </summary>
        public bool IsSystemOperational<T>() where T : ShipSystem
        {
            T system = GetSystem<T>();
            return system != null && system.IsOperational;
        }

        #endregion

        #region Debug

        private void OnGUI()
        {
            if (!Debug.isDebugBuild) return;

            int y = 260;
            GUI.Label(new Rect(10, y, 400, 20), $"=== SHIP: {shipName} ===");
            y += 20;
            GUI.Label(new Rect(10, y, 400, 20), $"Hull: {currentHullIntegrity:F1}/{maxHullIntegrity} ({HullIntegrityPercent * 100f:F1}%)");
            y += 20;
            GUI.Label(new Rect(10, y, 400, 20), $"Speed: {CurrentSpeed:F1} m/s");
            y += 20;
            GUI.Label(new Rect(10, y, 400, 20), $"FTL Ready: {(CanJump ? "YES" : "NO")}");
            y += 20;
            GUI.Label(new Rect(10, y, 400, 20), $"Shields: {(HasShields ? "ACTIVE" : "OFFLINE")}");

            // Test damage button
            y += 30;
            if (GUI.Button(new Rect(10, y, 150, 25), "Test: 20 HP Damage"))
            {
                TakeDamage(20f);
            }
            y += 30;
            if (GUI.Button(new Rect(10, y, 150, 25), "Test: Repair 50 HP"))
            {
                RepairHull(50f);
            }
        }

        #endregion
    }
}
