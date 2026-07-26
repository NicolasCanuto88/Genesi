using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiSpawner — Milestone 3, Blocco 3, Sottofase 2b.
    ///
    /// NetworkBehaviour server-only che spawna PoiInstance come "eventi
    /// casuali" durante il viaggio. Non è un sistema di missioni — è la
    /// componente che rende il viaggio non-vuoto: ogni tanto, mentre la
    /// nave si muove, qualcosa compare all'orizzonte logico.
    ///
    /// MODELLO A ROLL PERIODICO:
    ///   - Ogni checkIntervalSeconds, il server tira un dado
    ///   - Se il roll passa (spawnProbability) E la nave sta viaggiando E
    ///     non abbiamo raggiunto maxActivePoi → spawna un POI scelto dalla
    ///     lista pesata
    ///   - Nessuna schedulazione temporale, nessuna difficoltà crescente,
    ///     nessuna zona-specifica. Volutamente semplice.
    ///
    /// COSA CONTA COME "VIAGGIARE":
    ///   NavigationState in {Autopilot, Manual} → sempre sì
    ///   NavigationState == Coasting E CurrentSpeed >= minSpeedForRoll → sì
    ///   NavigationState in {Anchored, FTL_Charging, FTL_Jumping, FTL_Cooldown} → no
    ///
    ///   Motivazione: gli "eventi casuali" hanno senso solo quando la nave sta
    ///   effettivamente andando da qualche parte. In Coasting a velocità
    ///   piena la nave sta viaggiando per inerzia — vale come viaggio. In
    ///   Coasting quasi ferma no. In Anchored, per definizione, la nave è
    ///   ferma vicino a un altro POI: non ha senso spawnarne altri.
    ///
    /// SPAWN LOGICO (Opzione A):
    ///   poi.LogicalPosition = ship.LogicalPosition
    ///                       + Quaternion.Euler(pitchRand, yawRand, 0)
    ///                         * Vector3.forward
    ///                         * distanzaRand;
    ///
    ///   pitchRand ∈ [-spawnPitchRangeDeg, +spawnPitchRangeDeg]
    ///   yawRand ∈ [0, 360]
    ///   distanzaRand ∈ [spawnDistanceMin, spawnDistanceMax]
    ///
    ///   Il POI appare "davanti alla nave" nel frame logico ma con dispersione
    ///   sferica limitata dal pitch range. Il frame logico e il frame worldspace
    ///   sono già mappati correttamente dal Follower (Rev T.2): "davanti nel
    ///   frame logico" (Vector3.forward) coincide con "davanti al pilota" in
    ///   worldspace grazie alla rotazione Y=180° della Nave.
    ///
    /// CAP DEI POI ATTIVI:
    ///   Se PoiRegistry.Count >= maxActivePoi, il roll viene saltato. Nessun
    ///   cleanup automatico in 2b — un POI resta finché non viene despawnato
    ///   esplicitamente (che in 2b non avviene mai). Il cleanup "POI troppo
    ///   lontano dietro la nave → despawn server" è debito registrato per
    ///   Fase 3+.
    ///
    /// SETUP EDITOR:
    ///   1. Aggiungere questo componente a un GameObject in Game.unity (tipico:
    ///      un GameObject vuoto "Managers/PoiSpawner").
    ///   2. Aggiungere NetworkObject sullo stesso GameObject.
    ///   3. Registrare il NetworkObject nel NetworkPrefabList (necessario se
    ///      instanziato dinamicamente — non necessario se scene-placed in
    ///      Game.unity, come raccomandato).
    ///   4. Popolare spawnableTypes con almeno un PoiData asset (in 2b:
    ///      PoiData_Wreck con weight = 1).
    ///
    /// DIPENDE DA:
    ///   - ShipMovement.Instance (per LogicalPosition)
    ///   - PropulsionSystem.Instance (per NavigationState + CurrentSpeed)
    ///   - PoiRegistry (per Count e per iterare, sebbene qui usiamo solo Count)
    ///   - Ogni PoiData.VisualPrefab deve avere un NetworkObject registrato
    ///     nel NetworkPrefabList
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class PoiSpawner : NetworkBehaviour
    {
        [System.Serializable]
        public class WeightedPoi
        {
            [Tooltip("Il PoiData che verrà spawnato. Il prefab da instanziare " +
                     "è il visualPrefab di questo PoiData — ma non è il visual, " +
                     "è il prefab del PoiInstance root (vedi note sotto).")]
            public PoiData data;

            [Tooltip("Peso relativo nella selezione random. Più alto = più " +
                     "probabile. Valori tipici: 1-10.")]
            [Min(0.01f)]
            public float weight = 1f;

            [Tooltip("Prefab del PoiInstance root da instanziare (con " +
                     "NetworkObject + PoiInstance + child Visual con " +
                     "ExternalWorldFollower). NON è il visualPrefab di PoiData " +
                     "— questo è la struttura completa del PoiInstance, mentre " +
                     "visualPrefab (nel PoiData) è solo il child Visual. In 2b " +
                     "manteniamo questa separazione: PoiSpawner istanzia il " +
                     "root, che a sua volta ha già il child Visual configurato " +
                     "nel prefab. Il campo visualPrefab in PoiData è " +
                     "\"dormiente\" finché non serve un pattern più flessibile " +
                     "(es. root generico + visual iniettato a runtime).")]
            public GameObject poiInstancePrefab;
        }

        [Header("Roll periodico")]
        [Tooltip("Intervallo tra un check e il successivo (secondi). Il roll " +
                 "avviene ad ogni check, indipendentemente dal fatto che il " +
                 "precedente abbia spawnato qualcosa o meno.")]
        [Min(1f)]
        [SerializeField] private float checkIntervalSeconds = 30f;

        [Tooltip("Probabilità che il roll produca uno spawn, per singolo check. " +
                 "Con checkInterval=30s e prob=0.2, ci si aspetta uno spawn " +
                 "ogni ~150s di viaggio effettivo (tempo medio geometrico).")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnProbability = 0.20f;

        [Tooltip("Numero massimo di POI simultaneamente attivi nella sessione. " +
                 "Se raggiunto, i roll saltano lo spawn (contatore basato su " +
                 "PoiRegistry.Count).")]
        [Min(1)]
        [SerializeField] private int maxActivePoi = 5;

        [Header("Filtri stato nave")]
        [Tooltip("Sotto questa velocità, uno stato Coasting NON conta come " +
                 "\"viaggio\" — il roll viene saltato. Non impatta gli stati " +
                 "Autopilot/Manual (dove il roll è sempre ammesso a " +
                 "prescindere dalla velocità corrente).")]
        [Min(0f)]
        [SerializeField] private float minSpeedForRoll = 5f;

        [Header("Categorie POI spawnabili")]
        [Tooltip("Lista dei tipi di POI che questo spawner può generare, con " +
                 "peso relativo. In 2b: una sola entry con PoiData_Wreck.")]
        [SerializeField] private List<WeightedPoi> spawnableTypes = new List<WeightedPoi>();

        [Header("Debug")]
        [Tooltip("Log dettagliati dei roll (successo/fallimento, motivo di " +
                 "skip). Lasciare OFF in produzione.")]
        [SerializeField] private bool verboseLogging = false;

        // Timer per il prossimo check.
        private float _timeUntilNextCheck;

        // ── Lifecycle NGO ────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (!IsServer)
            {
                // Su client il componente non fa nulla — potremmo anche
                // disabilitarlo, ma lasciarlo abilitato con guardia è meno
                // fragile (se dopo l'host migration il ruolo cambia).
                enabled = false;
                return;
            }

            _timeUntilNextCheck = checkIntervalSeconds;

            if (verboseLogging)
            {
                Debug.Log($"[PoiSpawner] Server active. Check every " +
                          $"{checkIntervalSeconds}s, prob {spawnProbability:P0}, " +
                          $"cap {maxActivePoi}, {spawnableTypes.Count} types.");
            }
        }

        // ── Update loop server-only ──────────────────────────────────────────

        private void Update()
        {
            if (!IsServer) return;

            _timeUntilNextCheck -= Time.deltaTime;
            if (_timeUntilNextCheck > 0f) return;

            _timeUntilNextCheck = checkIntervalSeconds;

            TryRoll();
        }

        private void TryRoll()
        {
            // Filtro 1: la nave sta viaggiando?
            if (!IsShipTraveling(out string skipReason))
            {
                if (verboseLogging)
                    Debug.Log($"[PoiSpawner] Roll skipped: {skipReason}");
                return;
            }

            // Filtro 2: cap raggiunto?
            if (PoiRegistry.Count >= maxActivePoi)
            {
                if (verboseLogging)
                    Debug.Log($"[PoiSpawner] Roll skipped: cap raggiunto " +
                              $"({PoiRegistry.Count}/{maxActivePoi}).");
                return;
            }

            // Filtro 3: nessuna categoria configurata?
            if (spawnableTypes.Count == 0)
            {
                if (verboseLogging)
                    Debug.LogWarning("[PoiSpawner] Roll skipped: spawnableTypes vuoto.");
                return;
            }

            // Il dado.
            float roll = Random.value; // [0, 1]
            if (roll > spawnProbability)
            {
                if (verboseLogging)
                    Debug.Log($"[PoiSpawner] Roll failed: {roll:F2} > {spawnProbability:F2}.");
                return;
            }

            // Passato tutti i filtri → seleziona categoria e spawna.
            WeightedPoi chosen = PickWeighted();
            if (chosen == null || chosen.data == null || chosen.poiInstancePrefab == null)
            {
                Debug.LogWarning("[PoiSpawner] Selezione fallita: entry non valida " +
                                 "(data o poiInstancePrefab null).");
                return;
            }

            SpawnPoi(chosen);
        }

        // ── Filtri stato nave ────────────────────────────────────────────────

        private bool IsShipTraveling(out string skipReason)
        {
            var prop = PropulsionSystem.Instance;
            if (prop == null)
            {
                skipReason = "PropulsionSystem.Instance == null";
                return false;
            }

            var state = prop.CurrentNavState;

            switch (state)
            {
                case NavigationState.Autopilot:
                case NavigationState.Manual:
                    skipReason = null;
                    return true;

                case NavigationState.Coasting:
                    if (prop.CurrentSpeed >= minSpeedForRoll)
                    {
                        skipReason = null;
                        return true;
                    }
                    skipReason = $"Coasting a {prop.CurrentSpeed:F1} m/s < " +
                                 $"{minSpeedForRoll:F1} m/s";
                    return false;

                case NavigationState.Anchored:
                    skipReason = "Anchored";
                    return false;

                default:
                    // Copre FTL_Charging, FTL_Jumping, FTL_Cooldown, ecc.
                    // se in futuro venissero aggiunti stati alla enum.
                    skipReason = $"stato non idoneo: {state}";
                    return false;
            }
        }

        // ── Selezione categoria ──────────────────────────────────────────────

        private WeightedPoi PickWeighted()
        {
            float totalWeight = 0f;
            foreach (var entry in spawnableTypes)
            {
                if (entry != null) totalWeight += Mathf.Max(0.01f, entry.weight);
            }

            if (totalWeight <= 0f) return null;

            float roll = Random.value * totalWeight;
            float accum = 0f;

            foreach (var entry in spawnableTypes)
            {
                if (entry == null) continue;
                accum += Mathf.Max(0.01f, entry.weight);
                if (roll <= accum) return entry;
            }

            // Fallback numerico (arrotondamenti float): ultima entry valida.
            for (int i = spawnableTypes.Count - 1; i >= 0; i--)
                if (spawnableTypes[i] != null) return spawnableTypes[i];

            return null;
        }

        // ── Spawn ────────────────────────────────────────────────────────────

        private void SpawnPoi(WeightedPoi entry)
        {
            var ship = ShipMovement.Instance;
            if (ship == null)
            {
                Debug.LogWarning("[PoiSpawner] ShipMovement.Instance == null, abort spawn.");
                return;
            }

            // Posizione logica: Opzione A confermata in review architetturale.
            //   base = ship.LogicalPosition
            //   dispersione: Quaternion.Euler(pitchRand, yawRand, 0) * Vector3.forward
            //   distanza: random tra min e max
            //
            // Uso Vector3.forward invece di ship.LogicalForward: sono
            // equivalenti finché ship.LogicalRotation == identity, e per
            // il gameplay la differenza è invisibile (yaw casuale 0-360°
            // annulla comunque l'orientamento base). Vector3.forward è più
            // semplice da leggere.
            PoiData data = entry.data;
            float distanza = Random.Range(data.SpawnDistanceMin, data.SpawnDistanceMax);
            float yawRand = Random.Range(0f, 360f);
            float pitchRand = Random.Range(-data.SpawnPitchRangeDeg, data.SpawnPitchRangeDeg);

            Vector3 offset = Quaternion.Euler(pitchRand, yawRand, 0f) * Vector3.forward * distanza;
            Vector3 logicalPos = ship.LogicalPosition + offset;

            // Orientamento del POI su se stesso: totalmente random. Rende
            // ogni spawn visivamente distinto senza dover configurare
            // nulla nel PoiData.
            Quaternion logicalRot = Random.rotationUniform;

            // Instantiate. La posizione worldspace iniziale è irrilevante —
            // verrà scavalcata dalla formula chiusa del Follower non appena
            // InitializeLogicalPose scatena il TryApplyLogicalToVisual sul
            // PoiInstance. Uso Vector3.zero solo per non lasciare il prefab
            // sotto camera per un frame.
            GameObject go = Instantiate(entry.poiInstancePrefab, Vector3.zero, Quaternion.identity);

            var netObj = go.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Debug.LogError($"[PoiSpawner] Prefab {entry.poiInstancePrefab.name} " +
                               $"senza NetworkObject. Destroy.");
                Destroy(go);
                return;
            }

            netObj.Spawn(destroyWithScene: true);

            // Popola le NetworkVariable server-side. PoiInstance.OnNetworkSpawn
            // ha già iscritto OnValueChanged → il set qui triggera il
            // TryApplyLogicalToVisual sul server nel prossimo tick, e la
            // sync arriva ai client subito dopo il pacchetto di Spawn.
            var poi = go.GetComponent<PoiInstance>();
            if (poi == null)
            {
                Debug.LogError($"[PoiSpawner] Prefab {entry.poiInstancePrefab.name} " +
                               $"senza PoiInstance. Despawn.");
                netObj.Despawn(destroy: true);
                return;
            }

            poi.InitializeLogicalPose(logicalPos, logicalRot);

            if (verboseLogging)
            {
                Debug.Log($"[PoiSpawner] Spawn {data.DisplayName} @ " +
                          $"logicalPos=({logicalPos.x:F0}, {logicalPos.y:F0}, {logicalPos.z:F0}) " +
                          $"· dist={distanza:F0}m · yaw={yawRand:F0}° pitch={pitchRand:F0}° " +
                          $"· active={PoiRegistry.Count}/{maxActivePoi}");
            }
        }
    }
}
