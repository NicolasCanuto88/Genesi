using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ScriptableObject per un singolo tipo di materiale.
    /// Crea un asset per ogni ItemType (es. "InvItem_FuelCell").
    /// </summary>
    [CreateAssetMenu(
        fileName = "InvItem_",
        menuName  = "SpaceSurvivor/Inventory/Item Data")]
    public class InventoryItemData : ScriptableObject
    {
        [Header("Identificazione")]
        public ItemType     itemType;
        public ItemCategory category;
        public string       displayName;

        [Header("Capacità")]
        [Tooltip("Quantità massima trasportabile (Cargo T1 = 99 per item).")]
        public int maxStack = 99;

        [Header("UI")]
        [Tooltip("Icona usata in HUD e monitor. Opzionale in M2.")]
        public Sprite icon;

        [TextArea(2, 4)]
        public string description;
    }
}
