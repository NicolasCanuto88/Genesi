using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Base class for all ship upgrade data (ScriptableObjects)
    /// Contains common upgrade properties (tier, cost, description, etc.)
    /// </summary>
    public abstract class UpgradeData : ScriptableObject
    {
        [Header("Upgrade Info")]
        [SerializeField] protected int tier = 1;
        [SerializeField] protected string upgradeName = "Upgrade";
        [SerializeField, TextArea(3, 6)] protected string description = "Upgrade description";

        [Header("Costs")]
        [SerializeField] protected int purchaseCost = 0; // Credits to buy from shop
        [SerializeField] protected int installCost = 0; // Credits for installation materials
        [SerializeField] protected float installTimeHours = 0f; // In-game time to install

        [Header("Requirements")]
        [SerializeField] protected int requiredEngineerTier = 1; // Engineer skill level needed
        [SerializeField] protected bool requiresQuest = false; // Locked behind quest?
        [SerializeField] protected string questID = ""; // Which quest unlocks this

        // Properties
        public int Tier => tier;
        public string UpgradeName => upgradeName;
        public string Description => description;
        public int PurchaseCost => purchaseCost;
        public int InstallCost => installCost;
        public int TotalCost => purchaseCost + installCost;
        public float InstallTimeHours => installTimeHours;
        public int RequiredEngineerTier => requiredEngineerTier;
        public bool RequiresQuest => requiresQuest;
        public string QuestID => questID;

        /// <summary>
        /// Check if player meets requirements to install this upgrade
        /// </summary>
        public virtual bool CanInstall(int engineerTier, bool questCompleted)
        {
            // Check engineer skill level
            if (engineerTier < requiredEngineerTier)
            {
                Debug.LogWarning($"[{upgradeName}] Requires Engineer Tier {requiredEngineerTier} (current: {engineerTier})");
                return false;
            }

            // Check quest requirement
            if (requiresQuest && !questCompleted)
            {
                Debug.LogWarning($"[{upgradeName}] Requires quest: {questID}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Get formatted description for UI
        /// </summary>
        public virtual string GetFormattedDescription()
        {
            string text = $"<b>{upgradeName}</b> (Tier {tier})\n\n";
            text += $"{description}\n\n";
            text += $"<b>Cost:</b> {TotalCost:N0} cr";
            
            if (installTimeHours > 0)
            {
                text += $"\n<b>Install Time:</b> {installTimeHours:F1} hours";
            }

            if (requiredEngineerTier > 1)
            {
                text += $"\n<b>Requires:</b> Engineer Tier {requiredEngineerTier}";
            }

            if (requiresQuest)
            {
                text += $"\n<b>Quest Required:</b> {questID}";
            }

            return text;
        }
    }
}
