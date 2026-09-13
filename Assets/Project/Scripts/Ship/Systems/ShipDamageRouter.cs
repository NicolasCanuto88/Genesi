using System;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Rev BF — Fase 2a (Danni Differenziati), Stage B.
    /// Autorità server-side per l'instradamento del danno da impatto ai subsystem.
    ///
    /// FLUSSO (confermato QB1-a, modalità SELEZIONE):
    ///   grossDamage
    ///     → ShieldSystem.AbsorbAndReturnResidual (scudo-first, se presente)
    ///     → residuo
    ///        → hull-floor  (SEMPRE allo scafo, quota da DamageDistributionTable)
    ///        → subsystem   (UN subsystem selezionato a pesi, se abilitato)
    ///
    /// CONSUMATO DA (entrambi server-side):
    ///   - ShipImpactHandler.HandleHardCollision (impatti POI)
    ///   - AsteroidSpawner.ApplyImpactDamage      (hazard asteroidi)
    /// Entrambi degradano con grazia allo Stage A (scudo-first → hull) se
    /// Instance è null: la pipeline resta valida anche senza router in scena.
    ///
    /// NetworkBehaviour SENZA NetworkVariable proprie: tutto lo stato persistente
    /// vive nei sink (Hull/Shield/Propulsion/FTL). Il router è NetworkBehaviour
    /// solo per IsServer pulito e per il pattern singleton della casa
    /// (Instance + OnInstanceReady). La selezione RNG gira SOLO server-side → i
    /// client vedono il risultato via le NetworkVariable dei sink, coerente su
    /// tutte le istanze.
    ///
    /// EDITOR SETUP (prescrittivo):
    ///   1. Aggiungere il componente ShipDamageRouter allo STESSO GameObject di
    ///      ShipImpactHandler / HullSystem (già dentro la scope network, già con
    ///      NetworkObject → nessun setup network aggiuntivo).
    ///   2. Create → SpaceSurvivor → Damage Distribution Table: creare l'asset,
    ///      compilare le righe Light/Medium/Hard.
    ///   3. Assegnare l'asset al campo 'Table' dell'Inspector di ShipDamageRouter.
    ///   4. Verifica: a router presente + SO assegnata, un impatto Medium/Hard
    ///      deve degradare Propulsion o FTL (non solo lo scafo).
    /// </summary>
    public class ShipDamageRouter : NetworkBehaviour
    {
        // ── Singleton (server-only usage) ─────────────────────────────────────
        public static ShipDamageRouter Instance { get; private set; }
        public static event Action OnInstanceReady;

        [Header("Tuning")]
        [Tooltip("Tabella di distribuzione del danno post-scudo " +
                 "(hull-floor + pesi selezione per severità). Se assente, il " +
                 "residuo va interamente allo scafo (fallback sicuro).")]
        [SerializeField] private DamageDistributionTable table;

        [Header("Debug")]
        [Tooltip("Log diagnostici verbosi dell'instradamento (split hull/subsystem).")]
        [SerializeField] private bool logVerbose = false;

        // ── NGO Lifecycle ─────────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[ShipDamageRouter] Istanza duplicata rilevata — distruggo il componente.");
                // Destroy(this) NON Destroy(gameObject): il router condivide il GO
                // con ShipImpactHandler/HullSystem — distruggere il GO li ucciderebbe.
                Destroy(this);
                return;
            }
            Instance = this;
            OnInstanceReady?.Invoke();

            if (IsServer && table == null)
            {
                Debug.LogError("[ShipDamageRouter] Nessuna DamageDistributionTable assegnata — " +
                               "il residuo post-scudo andrà interamente allo scafo (fallback sicuro).");
            }
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // ── API pubblica (server-only) ────────────────────────────────────────
        /// <summary>
        /// Instrada un impatto: scudo-first, poi hull-floor + selezione subsystem.
        /// Server-only. grossDamage è garantito &gt; 0 dai chiamanti (early-return
        /// a monte), ma il guard interno resta per robustezza.
        /// </summary>
        public void RouteImpactDamage(float grossDamage, ImpactSeverity severity)
        {
            if (!IsServer) return;
            if (grossDamage <= 0f) return;

            // ── 1. Scudo-first ────────────────────────────────────────────────
            float residual;
            var shield = ShieldSystem.Instance;
            if (shield != null)
                residual = shield.AbsorbAndReturnResidual(grossDamage);
            else
                residual = grossDamage;

            if (residual <= 0f)
            {
                if (logVerbose)
                    Debug.Log($"[ShipDamageRouter] Scudo ha assorbito tutto ({grossDamage:F1} HP) — nessun residuo.");
                return;
            }

            // ── 2. Split del residuo: hull-floor + subsystem ──────────────────
            DamageDistributionTable.SeverityRow row = table != null
                ? table.GetRow(severity)
                : new DamageDistributionTable.SeverityRow { hullFloorFraction = 1f, enableSelection = false };

            float floorFraction = Mathf.Clamp01(row.hullFloorFraction);
            float hullPart = residual * floorFraction;
            float subsystemPart = residual - hullPart;

            // ── 3. Selezione subsystem (RNG server-side) ──────────────────────
            ShipSubsystem selected = ShipSubsystem.Hull;
            if (row.enableSelection && subsystemPart > 0f)
                selected = SelectSubsystem(row.weights);

            // Se la selezione ricade su Hull (o nessun bersaglio valido), la quota
            // subsystem si fonde nello scafo: nessun danno perso.
            if (selected == ShipSubsystem.Hull)
            {
                hullPart += subsystemPart;
                subsystemPart = 0f;
            }

            // ── 4. Applicazione ai sink ───────────────────────────────────────
            if (hullPart > 0f)
                HullSystem.NotifyDamagePassthrough(hullPart);

            if (subsystemPart > 0f)
                ApplyToSubsystem(selected, subsystemPart);

            if (logVerbose)
            {
                Debug.Log($"[ShipDamageRouter] {severity}: grezzo={grossDamage:F1}, residuo={residual:F1} " +
                          $"→ hull={hullPart:F1}, {selected}={subsystemPart:F1}");
            }
        }

        // ── Selezione pesata ──────────────────────────────────────────────────
        /// <summary>
        /// Selezione pesata fra i subsystem della riga. Ignora Hull, i pesi ≤ 0 e
        /// i bersagli il cui sistema non è presente in scena (Instance null).
        /// Ritorna Hull se nessun bersaglio valido → il chiamante fonde nello scafo.
        /// </summary>
        private ShipSubsystem SelectSubsystem(DamageDistributionTable.SubsystemWeight[] weights)
        {
            if (weights == null || weights.Length == 0)
                return ShipSubsystem.Hull;

            float total = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i].target == ShipSubsystem.Hull) continue;
                if (weights[i].weight <= 0f) continue;
                if (!IsSubsystemPresent(weights[i].target)) continue;
                total += weights[i].weight;
            }

            if (total <= 0f)
                return ShipSubsystem.Hull;

            float roll = UnityEngine.Random.value * total;
            float acc = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i].target == ShipSubsystem.Hull) continue;
                if (weights[i].weight <= 0f) continue;
                if (!IsSubsystemPresent(weights[i].target)) continue;

                acc += weights[i].weight;
                if (roll <= acc)
                    return weights[i].target;
            }

            return ShipSubsystem.Hull; // fallback numerico (non dovrebbe accadere)
        }

        private static bool IsSubsystemPresent(ShipSubsystem s)
        {
            switch (s)
            {
                case ShipSubsystem.Propulsion: return PropulsionSystem.Instance != null;
                case ShipSubsystem.FTL:        return FTLDrive.Instance != null;
                case ShipSubsystem.Hull:       return HullSystem.Instance != null;
                default:                       return false;
            }
        }

        // ── Applicazione ai sink concreti ─────────────────────────────────────
        private void ApplyToSubsystem(ShipSubsystem target, float amount)
        {
            switch (target)
            {
                case ShipSubsystem.Propulsion:
                    if (PropulsionSystem.Instance != null)
                        PropulsionSystem.Instance.ApplyDamage(amount);
                    else
                        HullSystem.NotifyDamagePassthrough(amount); // fail-safe: non perdere danno
                    break;

                case ShipSubsystem.FTL:
                    if (FTLDrive.Instance != null)
                        FTLDrive.Instance.ApplyDamage(amount);
                    else
                        HullSystem.NotifyDamagePassthrough(amount);
                    break;

                default: // Hull o inatteso
                    HullSystem.NotifyDamagePassthrough(amount);
                    break;
            }
        }
    }
}
