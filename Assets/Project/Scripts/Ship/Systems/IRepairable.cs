using System;
using System.Collections.Generic;
using System.Linq;
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
        public int amount;
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
        /// progressPercent è un target ASSOLUTO (0–100) espresso come
        /// percentuale di maxHealth: targetHP = maxHealth * (progressPercent/100),
        /// HP = max(HP attuale, targetHP).
        ///
        /// ⚠️ RepairPanel passa un valore già "aggiustato" rispetto all'HP di
        /// inizio sessione (vedi IRepairableExtensions) — questo metodo NON
        /// cambia semantica, riceve semplicemente un progressPercent diverso
        /// da quello del minigame.
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
            ShipSystemState.Offline => 5.0f,  // ~7 E/sec
            _ => 0f
        };

        public static bool IsRepairable(this ShipSystemState state)
            => state != ShipSystemState.Online;
    }

    /// <summary>
    /// Helper condivisi tra RepairPanel (gate fisico) e RepairEntry (Sezione B Monitor 2).
    /// Single source of truth: pannello fisico e Monitor 2 sono sempre coerenti.
    ///
    /// MODELLO "SOGLIE RELATIVE ALLA SESSIONE" (Rev P):
    ///   Ogni soglia (50/75/100 del minigame) dà un guadagno HP proporzionale
    ///   al deficit corrente — non un target assoluto. Esempio: HP=60/100
    ///   (deficit 40) → soglia 50% = +20 (→80) · 75% = +30 (→90) · 100% = +40 (→100).
    ///   Risultato: nessuna soglia è mai "già superata", nessun materiale sprecato.
    ///
    ///   Conseguenza diretta: in QUALSIASI sessione che arriva al 100% del
    ///   minigame, TUTTE le soglie vengono attraversate e TUTTI i loro
    ///   materiali vengono consumati. Il gate quindi richiede la SOMMA dei
    ///   materiali di tutte le soglie — non solo della prima.
    /// </summary>
    public static class IRepairableExtensions
    {
        /// <summary>
        /// Somma i materiali richiesti su TUTTE le soglie di riparazione
        /// (stesso ItemType aggregato). Rappresenta il costo TOTALE di una
        /// sessione di riparazione completata al 100%.
        /// </summary>
        public static RepairMaterialRequirement[] GetTotalRepairMaterials(this IRepairable repairable)
        {
            var thresholds = repairable.GetRepairThresholds();
            if (thresholds == null || thresholds.Length == 0)
                return Array.Empty<RepairMaterialRequirement>();

            var totals = new Dictionary<ItemType, int>();

            foreach (var threshold in thresholds)
            {
                if (threshold.materials == null) continue;

                foreach (var req in threshold.materials)
                {
                    totals.TryGetValue(req.itemType, out int existing);
                    totals[req.itemType] = existing + req.amount;
                }
            }

            return totals
                .Select(kvp => new RepairMaterialRequirement { itemType = kvp.Key, amount = kvp.Value })
                .ToArray();
        }

        /// <summary>
        /// True se l'inventario contiene, per ogni ItemType, almeno la somma
        /// richiesta su TUTTE le soglie (controllo READ-ONLY, nessun consumo).
        ///
        /// Questo è il GATE all'ingresso: se false, RepairPanel.CanInteract()
        /// ritorna false — il pannello fisico non è interagibile finché non
        /// si hanno tutti i materiali per una sessione completa.
        ///
        /// True se il sistema non ha soglie configurate (nessun requisito).
        /// False se InventorySystem.Instance non è pronto — gate chiuso per sicurezza.
        /// </summary>
        public static bool HasMaterialsForFullRepair(this IRepairable repairable)
        {
            var totals = repairable.GetTotalRepairMaterials();
            if (totals.Length == 0) return true;

            if (InventorySystem.Instance == null) return false;

            foreach (var req in totals)
            {
                if (!InventorySystem.Instance.HasEnough(req.itemType, req.amount))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Restituisce la soglia con il progress più basso (es. 0.5 = 50%).
        /// Usata da RepairPanel per identificare l'inizio di una sessione
        /// di riparazione (vedi ApplyRepairThresholdRpc).
        /// Null se nessuna soglia configurata.
        /// </summary>
        public static RepairThreshold? GetFirstThreshold(this IRepairable repairable)
        {
            var thresholds = repairable.GetRepairThresholds();
            if (thresholds == null || thresholds.Length == 0) return null;
            return thresholds.OrderBy(t => t.progress).First();
        }
    }
}