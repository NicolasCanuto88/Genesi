using System;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    // ── Tipi di supporto ──────────────────────────────────────────────────────

    /// <summary>Stato operativo di un subsystem nave.</summary>
    public enum ShipSystemState
    {
        Online,          // 75–100% HP
        DegradedLight,   // 50–74%
        DegradedHeavy,   // 25–49%
        Offline          // 0–24%
    }

    /// <summary>Quantità di un materiale richiesta per una soglia di riparazione.</summary>
    [Serializable]
    public struct RepairMaterialRequirement
    {
        public ItemType itemType;
        public int      amount;
    }

    /// <summary>
    /// Materiali consumati al completamento di una soglia di riparazione.
    /// La regola invariante: materiali consumati SOLO al superamento della soglia,
    /// mai all'avvio e mai in caso di interruzione.
    /// </summary>
    [Serializable]
    public struct RepairThreshold
    {
        [Range(0f, 1f)]
        public float progress;                       // es. 0.5 = 50%
        public RepairMaterialRequirement[] materials; // costo a questa soglia
    }

    // ── Interfaccia ───────────────────────────────────────────────────────────

    /// <summary>
    /// Implementa questa interfaccia su ogni sistema nave riparabile in volo.
    /// Viene letta da RepairMinigame per sapere quanto è danneggiato,
    /// quali materiali servono e come applicare la riparazione.
    ///
    /// Sistemi che devono implementarla in M2:
    ///   PropulsionSystem · FTLDrive · LifeSupportConsumer · ShieldSystem
    ///
    /// ⚠️ I valori numerici (HP, materiali) devono venire da ScriptableObject.
    /// </summary>
    public interface IRepairable
    {
        /// <summary>Nome visualizzato nel RepairMinigame e sul Monitor 2.</summary>
        string GetSystemName();

        /// <summary>Stato attuale del sistema.</summary>
        ShipSystemState GetCurrentState();

        /// <summary>Salute normalizzata 0–1. Usata per calcolare il barDecayRate.</summary>
        float GetHealthPercent();

        /// <summary>
        /// Soglie di riparazione con i materiali da consumare a ciascuna.
        /// RepairMinigame chiama InventorySystem.TryConsume() quando la barra
        /// attraversa ogni soglia.
        /// </summary>
        RepairThreshold[] GetRepairThresholds();

        /// <summary>
        /// Applica la riparazione parziale o totale.
        /// Chiamato da RepairMinigame al superamento di ogni soglia.
        /// progressPercent: 0–100 (es. 50, 75, 100).
        /// </summary>
        void ApplyRepair(float progressPercent);

        /// <summary>True se il sistema è riparabile (DEGRADED o OFFLINE).</summary>
        bool IsRepairable();
    }

    // ── Helper statici ────────────────────────────────────────────────────────

    public static class ShipSystemStateExtensions
    {
        /// <summary>Velocità di decadimento della barra RepairMinigame per stato.</summary>
        public static float GetBarDecayRate(this ShipSystemState state) => state switch
        {
            ShipSystemState.DegradedLight => 1.0f,  // ~2 E/sec per stare fermi
            ShipSystemState.DegradedHeavy => 3.5f,  // ~5 E/sec
            ShipSystemState.Offline       => 5.0f,  // ~7 E/sec
            _                             => 0f
        };

        public static bool IsRepairable(this ShipSystemState state)
            => state != ShipSystemState.Online;
    }
}
