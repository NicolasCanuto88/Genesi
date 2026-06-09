using SpaceSurvivor.Ship;
using UnityEngine;

public class TestRepairable : MonoBehaviour, IRepairable
{
    public string GetSystemName() => "TEST SYSTEM";
    public ShipSystemState GetCurrentState() => ShipSystemState.DegradedHeavy;
    public float GetHealthPercent() => 0.3f;
    public bool IsRepairable() => true;
    public void ApplyRepair(float pct) => Debug.Log($"[Test] ApplyRepair {pct}%");

    public RepairThreshold[] GetRepairThresholds() => new[]
    {
        new RepairThreshold
        {
            progress  = 1.0f,
            materials = new[] { new RepairMaterialRequirement
                { itemType = ItemType.WireBundle, amount = 2 } }
        }
    };
}