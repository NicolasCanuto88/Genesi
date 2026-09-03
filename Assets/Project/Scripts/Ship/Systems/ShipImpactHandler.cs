using System;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ShipImpactHandler — Milestone 3 Fase 3 Blocco 3.2 (Sotto-step 3.2.a + 3.2.b).
    /// NetworkBehaviour singleton — GameObject dedicato figlio di Nave
    /// (tipicamente lo stesso di HullSystem, o comunque dentro la stessa
    /// scope network).
    ///
    /// RESPONSABILITÀ:
    ///   Orchestratore server-side degli eventi di IMPATTO sulla nave.
    ///
    ///   In 3.2.a: consuma DockingController.OnHardCollision, calcola il
    ///   danno secondo la formula soglia+quadratica concordata, chiama
    ///   HullSystem.TakeDamage().
    ///
    ///   In 3.2.b: dopo l'applicazione del danno, calcola il trasferimento
    ///   di momento cinetico dalla nave al POI colpito e chiama
    ///   PoiInstance.AddImpulse(). Il POI viene sbalzato lungo la direzione
    ///   radiale nave→POI, con deltaV inversamente proporzionale alla propria
    ///   Mass (PoiData.Mass agisce come "rapporto di massa" verso la
    ///   costante EffectiveShipMass — vedi Q3, Rev Z). La rotazione del POI
    ///   NON viene modificata: invariante Rev Z, l'asse di attracco deve
    ///   restare stabile perché il pilota possa riprovare l'ancoraggio dopo
    ///   un urto.
    ///
    ///   In 3.2.c aggiunge una seconda sottoscrizione a
    ///   PoiCollisionResolver.OnHardCollision (impatti in Manual/Coasting/
    ///   Autopilot). HandleHardCollision è agnostico rispetto alla sorgente:
    ///   zero modifiche al body, solo doppio publisher → singolo consumer.
    ///
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
    /// FORMULA TRASFERIMENTO MOMENTO (Blocco 3.2.b — Q3 = massa nave costante):
    ///     radialDir = (poi.LogicalPosition - ship.LogicalPosition).normalized
    ///     deltaV    = impactVelocity × EffectiveShipMass / poiData.Mass
    ///     poi.AddImpulse(radialDir × deltaV)
    ///
    ///   EffectiveShipMass è una costante interna = 1.0 (Q3): PoiData.Mass
    ///   è di fatto il "rapporto di massa nave/POI". Un relitto con Mass=100
    ///   (default) riceve deltaV = 1% della velocità di impatto: un urto a
    ///   3 u/s sbalza un relitto tipico di 0.03 u/s → con sleep threshold
    ///   0.05 u/s si ferma subito (impatto lieve → nessuno spostamento
    ///   percepibile). Un urto a 10 u/s sbalza 0.1 u/s → il POI si allontana
    ///   ~4 m in ~35s prima di fermarsi (feel corretto per collisione dura).
    ///   Per tunare visibilità dell'effetto: abbassare Mass del POI, NON
    ///   toccare EffectiveShipMass.
    ///
    ///   Nota: il trasferimento di momento e il danno sono DISACCOPPIATI.
    ///   Un frammento con ImpactDamageMultiplier=0.3 (asteroide vetroso)
    ///   ma Mass=10 fa poco danno alla nave ed è comunque scacciato via
    ///   con vigore. Un relitto con ImpactDamageMultiplier=2.0 (blindato)
    ///   ma Mass=500 (grosso) fa molto danno ed è quasi immobile.
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
    ///   - ShipMovement (Instance, LogicalPosition) — se assente al momento
    ///     dell'evento, il danno viene comunque applicato ma il trasferimento
    ///     di momento viene skippato con warning (fail-safe).
    ///   - PoiInstance.AddImpulse (aggiunto in 3.2.b.1)
    ///   - PoiData.ImpactDamageMultiplier (aggiunto in 3.2.a)
    ///   - PoiData.Mass (esistente, ora effettivamente usato)
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

        // ── Costanti fisiche (Blocco 3.2.b) ───────────────────────────────────

        /// <summary>
        /// Massa effettiva della nave usata nella formula di trasferimento
        /// momento. Costante 1.0 (Q3 confermato in workshop 3.2.b, Rev Z):
        /// PoiData.Mass diventa il "rapporto di massa nave/POI".
        ///
        /// Motivazione dell'unità arbitraria: un solo dial di tuning
        /// (PoiData.Mass per-asset), zero accoppiamento con sistemi upgrade
        /// non ancora implementati. Se in Milestone 4+ vorremo che Hull
        /// upgrade influenzi lo "sfondamento" della nave sui relitti, si
        /// promuoverà questa costante a property (o ShipMovement property)
        /// derivata dall'upgrade, senza refactor di nessun consumer.
        /// </summary>
        private const float EffectiveShipMass = 1.0f;

        /// <summary>
        /// Distanza minima nave↔POI (u logiche) sotto cui il trasferimento
        /// di momento viene skippato: la direzione radiale è degenere.
        /// Edge case teorico (overlap perfetto), il clamp posizionale del
        /// DockingController lo rende praticamente irraggiungibile — ma
        /// vale la spesa dell'if per prevenire NaN/Infinity in caso di
        /// bug o refactor futuri.
        /// </summary>
        private const float DegenerateRadialDistanceEpsilon = 1e-4f;

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
        [Tooltip("Log dettagliato di ogni impatto (calcolo formula danno + " +
                 "risultato). Utile in playtest per tuning; disattivare in " +
                 "build finale.")]
        [SerializeField] private bool logImpacts = true;

        [Tooltip("Log dettagliato del trasferimento di momento al POI colpito " +
                 "(direzione radiale + deltaV applicato). Utile in playtest " +
                 "3.2.b per verificare feel dell'urto e bilanciamento di " +
                 "PoiData.Mass; disattivare in build finale.")]
        [SerializeField] private bool logImpulses = true;

        // ── Stato server ──────────────────────────────────────────────────────
        private bool _subscribedToDocking = false;
        private bool _subscribedToResolver = false;

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
            TrySubscribeToResolver();
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                UnsubscribeFromDocking();
                UnsubscribeFromResolver();
            }

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

        // ── Subscription resiliente al PoiCollisionResolver (Blocco 3.2.c) ────
        // Simmetrica a TrySubscribeToDocking. Il resolver emette OnHardCollision
        // con la stessa signature (Action<float, PoiInstance>) e HandleHardCollision
        // è agnostico rispetto alla sorgente — un solo consumer, due publisher.
        private void TrySubscribeToResolver()
        {
            if (_subscribedToResolver) return;

            if (PoiCollisionResolver.Instance != null)
            {
                PoiCollisionResolver.Instance.OnHardCollision += HandleHardCollision;
                _subscribedToResolver = true;
                if (logImpacts)
                    Debug.Log("[ShipImpactHandler] Subscribed to PoiCollisionResolver.OnHardCollision.");
            }
            else
            {
                PoiCollisionResolver.OnInstanceReady += HandleResolverInstanceReady;
                if (logImpacts)
                    Debug.Log("[ShipImpactHandler] PoiCollisionResolver non pronto, in attesa di OnInstanceReady.");
            }
        }

        private void HandleResolverInstanceReady()
        {
            PoiCollisionResolver.OnInstanceReady -= HandleResolverInstanceReady;
            TrySubscribeToResolver();
        }

        private void UnsubscribeFromResolver()
        {
            PoiCollisionResolver.OnInstanceReady -= HandleResolverInstanceReady;
            if (_subscribedToResolver && PoiCollisionResolver.Instance != null)
            {
                PoiCollisionResolver.Instance.OnHardCollision -= HandleHardCollision;
            }
            _subscribedToResolver = false;
        }

        // ── Consumer di OnHardCollision (server-only) ─────────────────────────
        private void HandleHardCollision(float impactVelocity, PoiInstance poi)
        {
            if (!IsServer) return;

            // ── Validazione input ─────────────────────────────────────────────
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

            // ── Soglia ────────────────────────────────────────────────────────
            // Sotto ConfirmMaxVelocity nessun danno E nessun impulso
            // (invariante Rev X + Rev Z: un solo tuning globale, sotto soglia
            // il contatto è "allineamento fine", non "urto").
            // Se DockingController.Instance sparisse tra fire e handle (edge
            // case durante despawn) usiamo un fallback conservativo di 1.0.
            float threshold = DockingController.Instance != null
                ? DockingController.Instance.ConfirmMaxVelocity
                : 1.0f;

            if (impactVelocity < threshold)
            {
                if (logImpacts)
                {
                    Debug.Log($"[ShipImpactHandler] Impatto sotto soglia — no damage/impulse. " +
                              $"v={impactVelocity:F2} u/s, soglia={threshold:F2} u/s, " +
                              $"POI={poi.Data.DisplayName}");
                }
                return;
            }

            // ── Calcolo e applicazione danno ──────────────────────────────────
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
                // NB: se il danno è 0 per mult=0 (asteroide "carta velina"),
                // saltiamo anche il trasferimento di momento. La semantica
                // "urto irrilevante" vale su entrambi i canali.
                return;
            }

            var hull = HullSystem.Instance;
            if (hull == null)
            {
                Debug.LogWarning($"[ShipImpactHandler] HullSystem.Instance null — impatto perso! " +
                                 $"damage={damage:F1} HP, v={impactVelocity:F2} u/s, POI={poi.Data.DisplayName}");
                // Il danno è perso, ma tentiamo comunque il trasferimento di
                // momento: la reazione fisica del POI non dipende dallo stato
                // dello scafo (potresti sbattere una nave morta contro un
                // relitto — dovrebbe comunque spostarsi).
            }
            else
            {
                hull.TakeDamage(damage);

                if (logImpacts)
                {
                    Debug.LogWarning($"[ShipImpactHandler] IMPATTO → -{damage:F1} HP " +
                                     $"(v={impactVelocity:F2} u/s, k={hullDamagePerImpactSquared:F3}, " +
                                     $"mult={multiplier:F2}, POI={poi.Data.DisplayName})");
                }
            }

            // ── Trasferimento momento al POI colpito (Blocco 3.2.b) ───────────
            ApplyMomentumTransferToPoi(impactVelocity, poi);

            // ── Notifica consumer di feedback teatrale (Blocco 3.2.d) ─────────
            OnDamageInflicted?.Invoke(damage, impactVelocity, poi);

            // ── Feedback teatrale client-side (Blocco 3.2.d parte 2 — Rev AE) ─
            // QG-3 confermata: ClientRpc per canali impulsivi (shake camera +
            // audio one-shot). Il banner "MOTORI OFFLINE" NON passa da qui:
            // legge direttamente le NetworkVariable di PropulsionSystem
            // aggiornate dal TriggerEngineFailure sotto. Separazione pulita:
            // impulsivi via RPC, stato persistente via NV.
            ImpactSeverity severity = ImpactThresholdTable.Classify(impactVelocity);
            PlayImpactFeedbackClientRpc(severity, impactVelocity);

            // ── Avaria motori post-impatto (Blocco 3.2.d — Rev AC) ────────────
            // Q1=B confermata Rev AC: l'orchestrazione dell'avaria motori è
            // centralizzata in questo handler (canale unico delle conseguenze
            // di impatto). Il filtro Q6=B (stati applicabili: Manual/Coasting/
            // Autopilot) è interno a TriggerEngineFailure — chiamiamo sempre e
            // il metodo scarta l'invocazione quando fuori scope (es. impatti
            // in Docking dal DockingController). Ignoriamo il fire per
            // impatti sotto soglia e con damage=0: la sequenza early-return
            // sopra (righe 308-336) ha già interrotto il flusso in quei casi.
            var propulsion = PropulsionSystem.Instance;
            if (propulsion != null)
            {
                propulsion.TriggerEngineFailure();
            }
            else
            {
                Debug.LogWarning("[ShipImpactHandler] PropulsionSystem.Instance null — " +
                                 "avaria motori skippata.");
            }
        }

        // ── ClientRpc: feedback teatrale impulsivo (Rev AE) ──────────────────
        /// <summary>
        /// Fire-and-forget su tutti i client (server e non): triggera lo
        /// screen shake della camera locale e l'audio one-shot d'impatto,
        /// con severity classificata server-side (single source of truth
        /// in ImpactThresholdTable).
        ///
        /// SendTo.ClientsAndHost coerente col pattern esistente
        /// (DoubleDoorOpenAuto.PlaySoundClientRpc). Payload minimo:
        /// 1 byte severity + 4 byte velocity = 5 byte per impatto,
        /// bandwidth trascurabile.
        ///
        /// impactVelocity è passato per uso in log/diagnostica lato client
        /// (o eventuali futuri feedback velocity-dipendenti oltre le 3
        /// soglie discrete). CameraShaker e ImpactAudioController usano
        /// solo severity.
        /// </summary>
        [Rpc(SendTo.ClientsAndHost)]
        private void PlayImpactFeedbackClientRpc(ImpactSeverity severity, float impactVelocity)
        {
            // Shake della camera del player LOCALE (LocalInstance è null sui
            // client dove il player non è ancora spawnato o dove il componente
            // si è auto-disabilitato — safe navigation).
            CameraShaker.LocalInstance?.Trigger(severity);

            // Audio one-shot sulla nave locale (singleton per client).
            ImpactAudioController.Instance?.PlayImpact(severity);

            if (logImpacts)
            {
                Debug.Log($"[ShipImpactHandler] Client-side feedback: " +
                          $"{ImpactThresholdTable.DebugLabel(severity)} (v={impactVelocity:F2} u/s)");
            }
        }

        /// <summary>
        /// Calcola direzione radiale nave→POI e applica un impulso a
        /// PoiInstance secondo la formula di trasferimento momento
        /// (Q3 confermata Rev Z: EffectiveShipMass = 1.0).
        ///
        /// Fail-safe: se ShipMovement.Instance è assente o la distanza
        /// radiale è degenere (overlap perfetto), skippa l'impulso e
        /// logga warning. Il danno alla nave è già stato applicato
        /// separatamente — questa funzione può fallire senza compromettere
        /// il resto della catena.
        /// </summary>
        private void ApplyMomentumTransferToPoi(float impactVelocity, PoiInstance poi)
        {
            var shipMovement = ShipMovement.Instance;
            if (shipMovement == null)
            {
                Debug.LogWarning($"[ShipImpactHandler] ShipMovement.Instance null — " +
                                 $"trasferimento momento skippato (POI={poi.Data.DisplayName}).");
                return;
            }

            Vector3 shipToPoi = poi.LogicalPosition - shipMovement.LogicalPosition;
            float dist = shipToPoi.magnitude;

            if (dist < DegenerateRadialDistanceEpsilon)
            {
                Debug.LogWarning($"[ShipImpactHandler] Direzione radiale degenere " +
                                 $"(dist={dist:E2} u) — trasferimento momento skippato " +
                                 $"(POI={poi.Data.DisplayName}).");
                return;
            }

            Vector3 radialDir = shipToPoi / dist;

            float poiMass = poi.Data.Mass;
            // PoiData.Mass ha [Min(0.1f)] a livello di Inspector — divisione
            // sempre safe, ma teniamo un guard difensivo per non fidarci del
            // pattern nel caso PoiData venga modificato in futuro.
            if (poiMass <= 0f)
            {
                Debug.LogWarning($"[ShipImpactHandler] PoiData.Mass non positiva ({poiMass}) su " +
                                 $"{poi.Data.DisplayName} — trasferimento momento skippato.");
                return;
            }

            float deltaVMagnitude = impactVelocity * EffectiveShipMass / poiMass;
            Vector3 impulse = radialDir * deltaVMagnitude;

            poi.AddImpulse(impulse);

            if (logImpulses)
            {
                Debug.Log($"[ShipImpactHandler] IMPULSO → POI={poi.Data.DisplayName}, " +
                          $"deltaV={deltaVMagnitude:F3} u/s, dir=({radialDir.x:F2},{radialDir.y:F2},{radialDir.z:F2}), " +
                          $"poiMass={poiMass:F1}, shipMass={EffectiveShipMass:F1}, v={impactVelocity:F2} u/s");
            }
        }
    }
}