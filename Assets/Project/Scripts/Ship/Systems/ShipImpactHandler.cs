using System;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ShipImpactHandler — Milestone 3 Fase 3 Blocco 3.2 (Sotto-step 3.2.a).
    /// NetworkBehaviour singleton — GameObject dedicato figlio di Nave
    /// (tipicamente lo stesso di HullSystem, o comunque dentro la stessa
    /// scope network).
    ///
    /// RESPONSABILITÀ:
    ///   Orchestratore server-side degli eventi di IMPATTO sulla nave.
    ///   In 3.2.a: consuma DockingController.OnHardCollision, calcola il
    ///   danno secondo la formula soglia+quadratica concordata, chiama
    ///   HullSystem.TakeDamage().
    ///
    ///   In 3.2.b si estenderà per gestire la reazione fisica del POI
    ///   colpito (trasferimento di momento a PoiInstance.LogicalVelocity).
    ///   In 3.2.d si estenderà per fire di eventi verso feedback teatrale
    ///   (screen shake, luci sfarfallanti, audio d'impatto).
    ///
    ///   HullSystem resta agnostico rispetto alla causa del danno:
    ///   collisione, hazard ambientale, EMP, tempeste future — tutti
    ///   causano solo TakeDamage(x). Il "perché" è responsabilità del
    ///   sistema chiamante (qui: ShipImpactHandler per gli impatti).
    ///
    /// FORMULA DANNO (Blocco 3.2.a — δ soglia + quadratica):
    ///     if (impactVelocity &lt; ConfirmMaxVelocity)  damage = 0;
    ///     else                                       damage =
    ///         impactVelocity² × hullDamagePerImpactSquared
    ///                        × poiData.ImpactDamageMultiplier
    ///
    ///   Soglia: coincide con DockingController.ConfirmMaxVelocity —
    ///   invariante Rev X: un solo tuning globale gestisce sia "posso
    ///   attraccare" che "quanto è troppo forte". Sotto soglia i
    ///   micro-contatti di allineamento fine non danneggiano.
    ///
    ///   Sopra soglia la quadratica è narrativamente coerente ("una nave
    ///   grossa che sbatte forte fa molto danno") ed è ammorbidita/durita
    ///   per-POI dal moltiplicatore ImpactDamageMultiplier (asteroide
    ///   vetroso &lt; 1, relitto blindato &gt; 1).
    ///
    /// EVENTO PUBBLICO:
    ///   OnDamageInflicted(damage, impactVelocity, poi) — fire server-side
    ///   solo quando è stato effettivamente applicato danno (&gt; 0).
    ///   Consumer futuro in 3.2.d per feedback teatrale (screen shake +
    ///   luci + audio, la cui intensità scala col danno reale, non con la
    ///   velocità raw).
    ///
    /// DIPENDE DA:
    ///   - DockingController (Instance, OnHardCollision, ConfirmMaxVelocity)
    ///   - HullSystem (Instance, TakeDamage) — se assente al momento
    ///     dell'evento, il danno viene loggato ma non applicato (fail-safe).
    ///   - PoiData.ImpactDamageMultiplier (aggiunto in 3.2.a)
    ///
    /// EDITOR SETUP:
    ///   Componente su un GameObject figlio di Nave (o sullo stesso GO di
    ///   HullSystem). Nessun altro requisito: si aggancia via singleton
    ///   pattern con OnInstanceReady fallback per race condition di
    ///   ordine di spawn.
    /// </summary>
    public class ShipImpactHandler : NetworkBehaviour
    {
        // ── Singleton (server-only usage) ─────────────────────────────────────
        public static ShipImpactHandler Instance { get; private set; }

        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Formula danno da impatto (Blocco 3.2.a)")]
        [Tooltip("Coefficiente globale della componente quadratica del danno. " +
                 "Formula: damage = impactVelocity² × k × poiData.ImpactDamageMultiplier. " +
                 "Default 1.0 = punto di partenza per playtest.\n\n" +
                 "Reference (con moltiplicatore POI = 1.0 e MaxHP T1 = 500):\n" +
                 "  v = 1.0 u/s (soglia)    → 1 HP\n" +
                 "  v = 5.0 u/s (medio)     → 25 HP  (5% MaxHP)\n" +
                 "  v = 8.0 u/s (max RCS)   → 64 HP  (~13% MaxHP)\n" +
                 "  v = 15  u/s (Coasting)  → 225 HP (~45% MaxHP)")]
        [Min(0f)]
        [SerializeField] private float hullDamagePerImpactSquared = 1.0f;

        [Header("Debug")]
        [Tooltip("Log dettagliato di ogni impatto (calcolo formula + risultato). " +
                 "Utile in playtest per tuning; disattivare in build finale.")]
        [SerializeField] private bool logImpacts = true;

        // ── Stato server ──────────────────────────────────────────────────────
        private bool _subscribedToDocking = false;

        // ── Eventi pubblici ───────────────────────────────────────────────────
        /// <summary>
        /// Fire server-side quando un impatto ha effettivamente applicato
        /// danno alla nave (&gt; 0). Parametri:
        /// (damage HP inflitto, impactVelocity u/s, PoiInstance colpito).
        /// Consumer futuro in Blocco 3.2.d (feedback teatrale).
        /// </summary>
        public event Action<float, float, PoiInstance> OnDamageInflicted;

        // ── NGO Lifecycle ─────────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[ShipImpactHandler] Istanza duplicata rilevata — distruggo.");
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // Solo il server ha bisogno di reagire agli impatti: HullSystem
            // è server-authoritative sul danno, il consumer stesso vive
            // server-side.
            if (!IsServer) return;

            TrySubscribeToDocking();
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
                UnsubscribeFromDocking();

            if (Instance == this) Instance = null;
        }

        // ── Subscription resiliente a race condition di spawn order ───────────
        private void TrySubscribeToDocking()
        {
            if (_subscribedToDocking) return;

            if (DockingController.Instance != null)
            {
                DockingController.Instance.OnHardCollision += HandleHardCollision;
                _subscribedToDocking = true;
                if (logImpacts)
                    Debug.Log("[ShipImpactHandler] Subscribed to DockingController.OnHardCollision.");
            }
            else
            {
                // DockingController non ancora spawnato: aspetta OnInstanceReady.
                DockingController.OnInstanceReady += HandleDockingInstanceReady;
                if (logImpacts)
                    Debug.Log("[ShipImpactHandler] DockingController non pronto, in attesa di OnInstanceReady.");
            }
        }

        private void HandleDockingInstanceReady()
        {
            DockingController.OnInstanceReady -= HandleDockingInstanceReady;
            TrySubscribeToDocking();
        }

        private void UnsubscribeFromDocking()
        {
            DockingController.OnInstanceReady -= HandleDockingInstanceReady;
            if (_subscribedToDocking && DockingController.Instance != null)
            {
                DockingController.Instance.OnHardCollision -= HandleHardCollision;
            }
            _subscribedToDocking = false;
        }

        // ── Consumer di OnHardCollision (server-only) ─────────────────────────
        private void HandleHardCollision(float impactVelocity, PoiInstance poi)
        {
            if (!IsServer) return;

            // Validazione input
            if (poi == null)
            {
                Debug.LogWarning("[ShipImpactHandler] HardCollision con PoiInstance null — ignoro.");
                return;
            }
            if (poi.Data == null)
            {
                Debug.LogWarning($"[ShipImpactHandler] HardCollision su POI senza Data (id={poi.NetworkObjectId}) — ignoro.");
                return;
            }

            // Soglia: sotto ConfirmMaxVelocity nessun danno (invariante Rev X).
            // Se DockingController.Instance sparisse tra fire e handle (edge
            // case durante despawn) usiamo un fallback conservativo di 1.0.
            float threshold = DockingController.Instance != null
                ? DockingController.Instance.ConfirmMaxVelocity
                : 1.0f;

            if (impactVelocity < threshold)
            {
                if (logImpacts)
                {
                    Debug.Log($"[ShipImpactHandler] Impatto sotto soglia — no damage. " +
                              $"v={impactVelocity:F2} u/s, soglia={threshold:F2} u/s, " +
                              $"POI={poi.Data.DisplayName}");
                }
                return;
            }

            // Formula danno: v² × k × moltiplicatore per-POI.
            float multiplier = poi.Data.ImpactDamageMultiplier;
            float damage = impactVelocity * impactVelocity
                         * hullDamagePerImpactSquared
                         * multiplier;

            if (damage <= 0f)
            {
                if (logImpacts)
                {
                    Debug.Log($"[ShipImpactHandler] Damage calcolato = 0 " +
                              $"(k={hullDamagePerImpactSquared:F3}, mult={multiplier:F2}) — skip.");
                }
                return;
            }

            // Applicazione danno via HullSystem (server-authoritative).
            var hull = HullSystem.Instance;
            if (hull == null)
            {
                Debug.LogWarning($"[ShipImpactHandler] HullSystem.Instance null — impatto perso! " +
                                 $"damage={damage:F1} HP, v={impactVelocity:F2} u/s, POI={poi.Data.DisplayName}");
                return;
            }

            hull.TakeDamage(damage);

            if (logImpacts)
            {
                Debug.LogWarning($"[ShipImpactHandler] IMPATTO → -{damage:F1} HP " +
                                 $"(v={impactVelocity:F2} u/s, k={hullDamagePerImpactSquared:F3}, " +
                                 $"mult={multiplier:F2}, POI={poi.Data.DisplayName})");
            }

            // Notifica consumer di feedback teatrale (Blocco 3.2.d, futuro).
            OnDamageInflicted?.Invoke(damage, impactVelocity, poi);
        }
    }
}
