using System.Collections;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// AsteroidSpawner — Milestone 3, Blocco 2 (versione minima, "ugly but
    /// functional" come da filosofia dichiarata per il Blocco 3 — applicata
    /// qui per lo stesso motivo: nessun asset 3D di asteroidi esiste ancora
    /// nel progetto, verificato (zero risultati per "asteroid" in Assets/).
    ///
    /// Questo script implementa SOLO la logica di rischio/danno richiesta da
    /// ZoneEvent.AsteroidField. Gli asteroidi VISIVI (mesh, drift reale nello
    /// spazio, VFX d'impatto) restano Track Parallelo — vedi GDD §12 — fino a
    /// quando non sarà disponibile un asset. Quando arriveranno (Blocco 3),
    /// si muoveranno secondo lo stesso principio "mondo esterno inverso"
    /// documentato in ShipMovement.cs — questo script non ne dipende, è
    /// puramente logico (rischio/danno), indipendente da qualunque
    /// rappresentazione visiva.
    ///
    /// RESPONSABILITÀ:
    ///   Mentre ZoneManager.ActiveEvent == AsteroidField, rischio periodico
    ///   di impatto se la nave è in navigazione attiva (MANUAL o, in difesa
    ///   di profondità, AUTOPILOT — anche se ZoneManager disabilita già
    ///   l'autopilota in AsteroidField, vedi UpdateAutopilotAvailability).
    ///   Nessun rischio in COASTING/ANCHORED.
    ///
    ///   Il danno passa per ShieldSystem.AbsorbDamage() quando disponibile —
    ///   rispetta GDD §9.6 ("gli scudi sono un sistema unico per tutti i
    ///   contesti: combattimento, radiazioni, asteroidi"). Se ShieldSystem
    ///   non è in scena, va diretto a HullSystem.TakeDamage(). NON usa
    ///   HullSystem.NotifyDamagePassthrough() direttamente: quel metodo è
    ///   l'entry point interno che ShieldSystem stesso usa per il danno
    ///   residuo già filtrato — chiamarlo qui bypasserebbe l'assorbimento
    ///   scudi.
    ///
    /// NetworkBehaviour root-level, gira interamente server-side; non
    /// possiede stato che i client debbano leggere direttamente (il danno
    /// si manifesta già attraverso HullSystem/ShieldSystem, entrambi già
    /// replicati).
    ///
    /// DIPENDE DA:
    ///   ZoneManager (ZoneEvent.AsteroidField) ✅ · PropulsionSystem
    ///   (NavigationState) ✅ · ShieldSystem/HullSystem ✅
    ///   Asteroidi visivi reali (mesh, drift, VFX impatto) — Track Parallelo,
    ///   nessun asset esiste ancora nel progetto.
    /// </summary>
    public class AsteroidSpawner : NetworkBehaviour
    {
        [Header("Rischio impatto (solo durante ZoneEvent.AsteroidField)")]
        [Tooltip("Intervallo medio fra un controllo rischio e il successivo.")]
        [SerializeField] private float checkInterval = 4f;

        [Tooltip("Probabilità di impatto ad ogni controllo, in volo MANUAL.")]
        [SerializeField, Range(0f, 1f)] private float manualImpactChance = 0.08f;

        [Tooltip("Probabilità di impatto ad ogni controllo, in AUTOPILOT. " +
                 "Difesa in profondità: ZoneManager disabilita già l'autopilota " +
                 "in AsteroidField, ma copre l'istante esatto in cui l'evento " +
                 "scatta prima che SetAutopilotAvailable(false) sia applicato.")]
        [SerializeField, Range(0f, 1f)] private float autopilotImpactChance = 0.20f;

        [Header("Danno per impatto")]
        [SerializeField] private float minDamage = 15f;
        [SerializeField] private float maxDamage = 45f;

        private Coroutine _riskRoutine;

        // =========================================================================
        // LIFECYCLE NGO
        // =========================================================================

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            if (ZoneManager.Instance != null)
                SubscribeToZoneManager();
            else
                ZoneManager.OnInstanceReady += SubscribeToZoneManager;
        }

        public override void OnNetworkDespawn()
        {
            ZoneManager.OnInstanceReady -= SubscribeToZoneManager;

            if (ZoneManager.Instance != null)
                ZoneManager.Instance.OnZoneChanged.RemoveListener(HandleZoneChanged);

            StopRiskRoutine();
        }

        private void SubscribeToZoneManager()
        {
            ZoneManager.OnInstanceReady -= SubscribeToZoneManager;
            if (ZoneManager.Instance == null) return;

            ZoneManager.Instance.OnZoneChanged.AddListener(HandleZoneChanged);

            // Stato iniziale, nel caso l'evento sia già attivo allo spawn
            // (es. riconnessione a metà sessione).
            HandleZoneChanged(ZoneManager.Instance.CurrentZone, ZoneManager.Instance.ActiveEvent);
        }

        // =========================================================================
        // ZONE EVENT → RISK ROUTINE
        // =========================================================================

        private void HandleZoneChanged(ZoneType zone, ZoneEvent evt)
        {
            if (!IsServer) return;

            if (evt == ZoneEvent.AsteroidField)
                StartRiskRoutine();
            else
                StopRiskRoutine();
        }

        private void StartRiskRoutine()
        {
            if (_riskRoutine != null) return;
            _riskRoutine = StartCoroutine(RiskRoutine());
            Debug.Log("[AsteroidSpawner] Campo asteroidi attivo — rischio impatto avviato.");
        }

        private void StopRiskRoutine()
        {
            if (_riskRoutine == null) return;
            StopCoroutine(_riskRoutine);
            _riskRoutine = null;
        }

        private IEnumerator RiskRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(checkInterval);

                var ps = PropulsionSystem.Instance;
                if (ps == null) continue;

                float chance = ps.CurrentNavState switch
                {
                    NavigationState.Manual    => manualImpactChance,
                    NavigationState.Autopilot => autopilotImpactChance,
                    _                          => 0f // Coasting/Anchored: nessun rischio
                };

                if (chance <= 0f) continue;
                if (Random.value > chance) continue;

                float damage = Random.Range(minDamage, maxDamage);
                ApplyImpactDamage(damage);
            }
        }

        private void ApplyImpactDamage(float damage)
        {
            if (ShieldSystem.Instance != null)
                ShieldSystem.Instance.AbsorbDamage(damage);
            else
                HullSystem.Instance?.TakeDamage(damage);

            Debug.LogWarning($"[AsteroidSpawner] Impatto asteroide — {damage:F0} danno in ingresso.");
        }
    }
}
