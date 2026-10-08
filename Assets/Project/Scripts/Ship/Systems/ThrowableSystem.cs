using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ThrowableSystem — voli, detonazioni e aree dei lanciabili (Rev BS-a, workshop bomba
    /// curativa + framework di lancio Q51–Q62, tutte come raccomandate).
    ///
    /// PATTERN: singleton di scena Instance + OnInstanceReady, come MedbaySystem e
    /// InventorySystem (GameObject radice in Game.unity con NetworkObject). Vive nella scena, non
    /// sul giocatore: un volo o un'area sopravvivono se chi lancia muore o si disconnette.
    ///
    /// RETE (Q52-a) — nessun NetworkObject per bomba, nessun Rigidbody:
    ///   - SERVER: PlayerThrower chiama ServerLaunch. Il server avanza ogni volo a ogni frame con
    ///     ThrowBallistics (parabola + sweep contro tutto, chi lancia escluso) e decide contatti,
    ///     rimbalzi e detonazione. Per i corpi dei giocatori conta la loro posizione sul server
    ///     (NetworkTransform owner-authoritative: circa mezzo RTT di ritardo).
    ///   - RPC verso tutti (ClientsAndHost), affidabili e in ordine: Launch al lancio, Bounce a
    ///     ogni rimbalzo, Detonate alla detonazione. Ognuna porta l'indice del catalogo.
    ///   - CLIENT: animano una copia SOLO visiva sulla stessa parabola, a partire dalla
    ///     ricezione (stesso ritardo per tutti gli eventi, quindi tempi coerenti). La copia usa lo
    ///     stesso sweep solo per non attraversare le pareti: al contatto si ferma e aspetta il
    ///     server, che ha l'ultima parola (posizione del rimbalzo o della detonazione).
    ///
    /// AREA (Q55-b): alla detonazione, se il lanciabile ha una durata, il server registra un'area
    /// (centro, raggio, scadenza, chi lancia, moltiplicatore di effetto) e i client ne mostrano la
    /// visuale per la stessa durata, senza altri messaggi. L'effetto è uno switch su
    /// ThrowEffectKind:
    ///   - None (Rev BS-a): solo la visuale.
    ///   - HealingField (Rev BS-b · Q55-b · Q56-a · Q63-a): a ogni tick (primo alla detonazione)
    ///     cura ogni giocatore VIVO con una parte del corpo nel raggio (segmento del
    ///     CharacterController) e in linea di vista dal centro verso il petto o verso il punto del
    ///     corpo più vicino (basta uno dei due). Pareti e porte bloccano, i corpi no. Chi lancia è
    ///     compreso. Cura = HP/s × tick × moltiplicatore di ruolo preso al lancio, via
    ///     PlayerHealthSystem.ApplyHeal (solo Alive, HP max dinamico): non rialza chi è a terra e non
    ///     cura gli stati. A fine campo OnServerAreaFinished porta il riepilogo a chi ha lanciato
    ///     (Q64-a).
    ///   - BubbleShield (Rev BV-c · Q96-a · Q105-a): a ogni tick (primo alla detonazione) chi ha una
    ///     parte del corpo nel raggio e in linea di vista dal centro (stessa regola del campo), VIVO O
    ///     A TERRA (non in attesa del clone), riceve lo stato Shielded per ShieldLingerSeconds, via
    ///     PlayerStatusEffects.ApplyEffectForSeconds. Uscito dalla bolla, lo scudo finisce entro quel
    ///     tempo (anche dopo la fine della bolla). Lo stato para solo i proiettili (Combat).
    ///     OnServerAreaFinished riporta quanti giocatori diversi la bolla ha coperto.
    ///
    /// DURATA DELL'AREA (Rev BV-c): ServerLaunch accetta una durata esplicita (la bolla dura quanto
    /// dice il tier del Quartermaster); senza, vale AreaDurationSeconds dello SO. La durata viaggia
    /// con la detonazione, così i client mostrano la visuale per lo stesso tempo.
    ///
    /// NAVE STATICA (Q53-a): tutto nello spazio Unity, cioè nel riferimento della nave.
    ///
    /// CATALOGO: ogni ThrowableData lanciato deve stare qui; l'ordine non va cambiato a partita
    /// in corso (l'indice viaggia nelle RPC). Fuori catalogo → errore a log e nessun lancio.
    ///
    /// LATE JOIN: chi entra durante un volo o un'area non li vede (durate di pochi secondi).
    /// </summary>
    public class ThrowableSystem : NetworkBehaviour
    {
        /// <summary>Nessun giocatore colpito.</summary>
        public const ulong NoClient = ulong.MaxValue;

        /// <summary>Secondi oltre la durata massima del volo dopo cui un client scarta una copia visiva orfana.</summary>
        private const float ClientOrphanGraceSeconds = 2f;

        /// <summary>Detonazioni ricordate per l'overlay di debug.</summary>
        private const int DebugRecordCount = 4;

        [Header("Catalogo dei lanciabili")]
        [Tooltip("Tutti i ThrowableData che si possono lanciare. Le RPC usano l'indice in questa lista: " +
                 "non riordinarla a partita in corso. Massimo 255 voci.")]
        [SerializeField] private ThrowableData[] catalog = Array.Empty<ThrowableData>();

        [Header("Debug")]
        [Tooltip("Overlay OnGUI con voli, aree e ultime detonazioni (solo Editor/Development Build, solo " +
                 "server). Standard Rev BA — default off.")]
        [SerializeField] private bool showDebugUI = false;

        [Tooltip("Log verboso di lanci, rimbalzi e detonazioni. Standard Rev BA — default off.")]
        [SerializeField] private bool logVerbose = false;

        [Tooltip("Gizmo in Scene view: percorso dell'ultimo volo sul server e aree attive. Standard Rev BA — " +
                 "default off.")]
        [SerializeField] private bool drawDebugGizmos = false;

        public static ThrowableSystem Instance { get; private set; }

        /// <summary>Fired dopo OnNetworkSpawn — i dipendenti si sottoscrivono se Instance è null al loro Start.</summary>
        public static event Action OnInstanceReady;

        /// <summary>
        /// Fired su OGNI client (host compreso) quando arriva una detonazione: chi ha lanciato, chi
        /// è stato colpito (NoClient se nessun giocatore) e il lanciabile (null se fuori catalogo).
        /// </summary>
        public static event Action<ulong, ulong, ThrowableData> OnClientDetonated;

        /// <summary>
        /// Rev BS-b (Q64-a) — fired SOLO sul server quando un'area con effetto finisce: chi ha
        /// lanciato, il lanciabile, gli HP curati in totale e quanti giocatori diversi ne hanno avuti.
        /// </summary>
        public static event Action<ulong, ThrowableData, float, int> OnServerAreaFinished;

        // ── Stato server ──────────────────────────────────────────────────────

        private sealed class ServerFlight
        {
            public ushort Id;
            public byte DataIndex;
            public ThrowableData Data;
            public ulong Thrower;
            public float EffectScale;
            public float AreaDuration;   // Rev BV-c: durata dell'area alla detonazione (secondi, 0 = nessuna)
            public ThrowFlightState State;
        }

        private sealed class ServerArea
        {
            public ThrowableData Data;
            public Vector3 Center;
            public float EndTime;
            public ulong Thrower;
            public float EffectScale;
            public float NextTickTime;                                    // Rev BS-b
            public float TotalHealed;                                     // Rev BS-b
            // Rev BS-b: giocatori distinti raggiunti dall'effetto (curati; Rev BV-c: o coperti dalla bolla)
            public readonly HashSet<ulong> Healed = new HashSet<ulong>();
        }

        private readonly List<ServerFlight> serverFlights = new List<ServerFlight>();
        private readonly List<ServerArea> serverAreas = new List<ServerArea>();
        private ushort nextFlightId;

        // ── Stato client (visuale) ────────────────────────────────────────────

        private sealed class ClientFlight
        {
            public ThrowableData Data;
            public ulong Thrower;
            public ThrowFlightState State;
            public GameObject Visual;
            public bool Frozen;   // contatto locale: in attesa dell'evento del server
            public float Age;
        }

        private sealed class ClientArea
        {
            public GameObject Visual;
            public float Elapsed;
            public float Duration;
            public float FadeFraction;
            public Renderer[] Renderers;
            public Color[] BaseColors;
        }

        private readonly Dictionary<ushort, ClientFlight> clientFlights = new Dictionary<ushort, ClientFlight>();
        private readonly List<ushort> clientFlightsToRemove = new List<ushort>();
        private readonly List<ClientArea> clientAreas = new List<ClientArea>();
        private MaterialPropertyBlock propertyBlock;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private readonly RaycastHit[] serverHits = new RaycastHit[16];
        private readonly RaycastHit[] lineOfSightHits = new RaycastHit[16];   // Rev BS-b
        private readonly RaycastHit[] clientHits = new RaycastHit[16];

        // ── Debug (server) ────────────────────────────────────────────────────

        private struct DetonationRecord
        {
            public ushort Id;
            public string Name;
            public ulong Thrower;
            public ulong Hit;
            public Vector3 Point;
            public float FlightTime;
            public int Bounces;
        }

        private readonly List<DetonationRecord> debugRecords = new List<DetonationRecord>();
        private readonly List<Vector3> debugLastPath = new List<Vector3>();
        private ushort debugLastPathId;

        // ── API pubblica ──────────────────────────────────────────────────────

        /// <summary>Indice del lanciabile nel catalogo, −1 se assente.</summary>
        public int IndexOf(ThrowableData data)
        {
            if (data == null || catalog == null) return -1;
            for (int i = 0; i < catalog.Length && i < 256; i++)
                if (catalog[i] == data) return i;
            return -1;
        }

        /// <summary>Voli attivi sul server (overlay, test).</summary>
        public int ServerFlightCount => serverFlights.Count;

        /// <summary>Aree attive sul server (overlay, test).</summary>
        public int ServerAreaCount => serverAreas.Count;

        // ── Lifecycle NGO ─────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (Instance != null && Instance != this)
                Debug.LogWarning("[ThrowableSystem] Instance già esistente. Deve esserci un solo " +
                                 "ThrowableSystem in scena.");

            Instance = this;
            propertyBlock = new MaterialPropertyBlock();

            if (IsServer) ValidateCatalog();

            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            serverFlights.Clear();
            serverAreas.Clear();

            foreach (ClientFlight flight in clientFlights.Values)
                DestroyVisual(flight.Visual);
            clientFlights.Clear();

            for (int i = 0; i < clientAreas.Count; i++)
                DestroyVisual(clientAreas[i].Visual);
            clientAreas.Clear();

            if (Instance == this) Instance = null;
        }

        private void ValidateCatalog()
        {
            if (catalog == null || catalog.Length == 0)
            {
                Debug.LogWarning("[ThrowableSystem] Catalogo vuoto: nessun lanciabile si potrà lanciare. " +
                                 "Assegnare gli asset ThrowableData (guida Editor di Rev BS-a).");
                return;
            }
            if (catalog.Length > 256)
                Debug.LogError("[ThrowableSystem] Catalogo oltre 256 voci: le RPC usano un byte. Le voci oltre la " +
                               "256ª non si possono lanciare.");

            for (int i = 0; i < catalog.Length; i++)
            {
                if (catalog[i] == null)
                {
                    Debug.LogWarning($"[ThrowableSystem] Voce {i} del catalogo vuota.");
                    continue;
                }
                for (int j = 0; j < i; j++)
                {
                    if (catalog[j] == catalog[i])
                        Debug.LogWarning($"[ThrowableSystem] {catalog[i].name} compare due volte nel catalogo " +
                                         $"(voci {j} e {i}): si usa la prima.");
                }
            }
        }

        // ── API server ────────────────────────────────────────────────────────

        /// <summary>
        /// Lancia un oggetto. SERVER ONLY. origin e lookDirection vengono dalla camera di chi lancia
        /// (il client li manda nella sua RPC: co-op, nessun anti-cheat). effectScale è il
        /// moltiplicatore di effetto deciso dal chiamante (ruolo), registrato con l'area.
        /// Ritorna false se il lanciabile non è nel catalogo o il sistema non è pronto.
        /// L'area dura AreaDurationSeconds dello SO.
        /// </summary>
        public bool ServerLaunch(ThrowableData data, Vector3 origin, Vector3 lookDirection, ulong throwerClientId,
                                 float effectScale)
            => ServerLaunch(data, origin, lookDirection, throwerClientId, effectScale,
                            data != null ? data.AreaDurationSeconds : 0f);

        /// <summary>
        /// Rev BV-c — come sopra, con la durata dell'area decisa dal chiamante (Bubble Shield: dal tier
        /// del Quartermaster). areaDurationSeconds ≤ 0 = nessuna area.
        /// </summary>
        public bool ServerLaunch(ThrowableData data, Vector3 origin, Vector3 lookDirection, ulong throwerClientId,
                                 float effectScale, float areaDurationSeconds)
        {
            if (!IsServer || !IsSpawned)
            {
                Debug.LogWarning("[ThrowableSystem] ServerLaunch chiamato fuori dal server o prima dello spawn.");
                return false;
            }

            int index = IndexOf(data);
            if (index < 0)
            {
                Debug.LogError($"[ThrowableSystem] {(data != null ? data.name : "null")} non è nel catalogo di " +
                               "ThrowableSystem: lancio annullato. Aggiungerlo al catalogo in Game.unity.");
                return false;
            }

            Vector3 velocity = ThrowBallistics.LaunchDirection(lookDirection, data.AimPitchOffsetDegrees) *
                               data.LaunchSpeed;

            var flight = new ServerFlight
            {
                Id = nextFlightId++,
                DataIndex = (byte)index,
                Data = data,
                Thrower = throwerClientId,
                EffectScale = Mathf.Max(0f, effectScale),
                AreaDuration = Mathf.Max(0f, areaDurationSeconds),
                State = ThrowBallistics.Begin(origin, velocity)
            };
            serverFlights.Add(flight);

            debugLastPathId = flight.Id;
            debugLastPath.Clear();
            debugLastPath.Add(origin);

            LaunchClientRpc(flight.Id, flight.DataIndex, throwerClientId, origin, velocity);

            LogV($"Lancio #{flight.Id}: {data.name} da client {throwerClientId}, velocità {velocity.magnitude:F1} m/s, " +
                 $"effetto ×{flight.EffectScale:F2}.");
            return true;
        }

        // ── Tick ──────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!IsSpawned) return;

            float dt = Time.deltaTime;
            if (IsServer)
            {
                ServerTickFlights(dt);
                ServerTickAreas();
            }
            if (IsClient)
            {
                ClientTickFlights(dt);
                ClientTickAreas(dt);
            }
        }

        private void ServerTickFlights(float dt)
        {
            for (int i = serverFlights.Count - 1; i >= 0; i--)
            {
                ServerFlight flight = serverFlights[i];
                ThrowableData data = flight.Data;
                if (data == null)
                {
                    serverFlights.RemoveAt(i);
                    continue;
                }

                Transform ignoreRoot = ResolvePlayerRoot(flight.Thrower);
                ThrowStepResult result = ThrowBallistics.Advance(ref flight.State, dt, data.Gravity,
                                                                 data.ProjectileRadius, data.MaxFlightSeconds,
                                                                 data.CollisionMask, ignoreRoot, serverHits,
                                                                 out ThrowContact contact);

                if (result == ThrowStepResult.Flying)
                {
                    RecordPath(flight, flight.State.Position(data.Gravity));
                    continue;
                }

                if (result == ThrowStepResult.Contact)
                {
                    RecordPath(flight, contact.Center);
                    if (ThrowBallistics.TryBounce(ref flight.State, contact, data.MaxBounces, data.Restitution,
                                                  data.MinBounceSpeed))
                    {
                        BounceClientRpc(flight.Id, flight.State.SegmentOrigin, flight.State.SegmentVelocity);
                        LogV($"Rimbalzo #{flight.Id} ({flight.State.Bounces}/{data.MaxBounces}) su " +
                             $"{(contact.Collider != null ? contact.Collider.name : "?")}.");
                        continue;
                    }

                    serverFlights.RemoveAt(i);
                    ServerDetonate(flight, contact.Center, ResolveHitClient(contact.Collider),
                                   contact.Collider != null ? contact.Collider.name : "?");
                    continue;
                }

                // Expired: detona in aria dove si trova.
                Vector3 point = flight.State.Position(data.Gravity);
                RecordPath(flight, point);
                serverFlights.RemoveAt(i);
                ServerDetonate(flight, point, NoClient, "volo scaduto");
            }
        }

        private void ServerDetonate(ServerFlight flight, Vector3 point, ulong hitClient, string hitLabel)
        {
            ThrowableData data = flight.Data;

            if (flight.AreaDuration > 0f)
            {
                serverAreas.Add(new ServerArea
                {
                    Data = data,
                    Center = point,
                    EndTime = Time.time + flight.AreaDuration,   // Rev BV-c: durata decisa al lancio
                    Thrower = flight.Thrower,
                    EffectScale = flight.EffectScale,
                    NextTickTime = Time.time   // Rev BS-b: primo tick alla detonazione
                });
            }

            DetonateClientRpc(flight.Id, flight.DataIndex, flight.Thrower, point, hitClient, flight.AreaDuration);

            debugRecords.Insert(0, new DetonationRecord
            {
                Id = flight.Id,
                Name = data.name,
                Thrower = flight.Thrower,
                Hit = hitClient,
                Point = point,
                FlightTime = flight.State.TotalTime,
                Bounces = flight.State.Bounces
            });
            if (debugRecords.Count > DebugRecordCount) debugRecords.RemoveAt(debugRecords.Count - 1);

            LogV($"Detonazione #{flight.Id}: {data.name} a ({point.x:F2}, {point.y:F2}, {point.z:F2}) dopo " +
                 $"{flight.State.TotalTime:F2} s · {hitLabel}" +
                 (hitClient != NoClient ? $" (client {hitClient})" : string.Empty) + ".");
        }

        /// <summary>
        /// Aree attive: tick dell'effetto e scadenza. I tick dovuti si recuperano anche dopo un frame
        /// lungo, mai oltre la fine dell'area. A fine area, riepilogo per chi ha lanciato.
        /// </summary>
        private void ServerTickAreas()
        {
            float now = Time.time;
            for (int i = serverAreas.Count - 1; i >= 0; i--)
            {
                ServerArea area = serverAreas[i];
                ThrowableData data = area.Data;
                if (data == null)
                {
                    serverAreas.RemoveAt(i);
                    continue;
                }

                if (data.EffectKind == ThrowEffectKind.HealingField)
                {
                    while (area.NextTickTime <= now && area.NextTickTime < area.EndTime)
                    {
                        ServerHealingTick(area);
                        area.NextTickTime += data.HealTickSeconds;
                    }
                }
                else if (data.EffectKind == ThrowEffectKind.BubbleShield)   // Rev BV-c
                {
                    while (area.NextTickTime <= now && area.NextTickTime < area.EndTime)
                    {
                        ServerShieldTick(area);
                        area.NextTickTime += data.ShieldTickSeconds;
                    }
                }

                if (now < area.EndTime) continue;

                serverAreas.RemoveAt(i);
                if (data.EffectKind != ThrowEffectKind.None)
                {
                    LogV($"Area {data.name} di client {area.Thrower} finita: {area.Healed.Count} giocatori " +
                         $"raggiunti, +{area.TotalHealed:F1} HP.");
                    OnServerAreaFinished?.Invoke(area.Thrower, data, area.TotalHealed, area.Healed.Count);
                }
            }
        }

        /// <summary>
        /// Rev BS-b — un tick del campo curativo. SERVER ONLY. Ogni giocatore vivo nel campo e in linea
        /// di vista riceve HP/s × tick × moltiplicatore di ruolo di chi ha lanciato.
        /// </summary>
        private void ServerHealingTick(ServerArea area)
        {
            ThrowableData data = area.Data;
            float amount = data.HealPerSecond * data.HealTickSeconds * area.EffectScale;
            if (amount <= 0f || NetworkManager == null) return;

            IReadOnlyList<ulong> clients = NetworkManager.ConnectedClientsIds;
            for (int c = 0; c < clients.Count; c++)
            {
                ulong clientId = clients[c];
                if (!PlayerHealthSystem.TryGetByClientId(clientId, out PlayerHealthSystem player) || player == null)
                    continue;
                if (!player.IsAlive) continue;
                if (!IsInsideField(area.Center, data.AreaRadius, data.CollisionMask, player)) continue;

                float healed = player.ApplyHeal(amount);
                if (healed <= 0f) continue;

                area.TotalHealed += healed;
                area.Healed.Add(clientId);
            }
        }

        /// <summary>
        /// Rev BV-c (Q96-a · Q105-a) — un tick della Bubble Shield. SERVER ONLY. Ogni giocatore vivo o a
        /// terra nella bolla e in linea di vista riceve lo stato Shielded per ShieldLingerSeconds
        /// (riapplicare rinnova senza accorciare: lo scudo personale più lungo resta intatto).
        /// </summary>
        private void ServerShieldTick(ServerArea area)
        {
            ThrowableData data = area.Data;
            if (NetworkManager == null) return;

            IReadOnlyList<ulong> clients = NetworkManager.ConnectedClientsIds;
            for (int c = 0; c < clients.Count; c++)
            {
                ulong clientId = clients[c];
                if (!PlayerHealthSystem.TryGetByClientId(clientId, out PlayerHealthSystem player) || player == null)
                    continue;
                if (!player.IsAlive && !player.IsDowned) continue;   // Q105-a: non chi aspetta il clone
                if (!IsInsideField(area.Center, data.AreaRadius, data.CollisionMask, player)) continue;
                if (!PlayerStatusEffects.TryGetByClientId(clientId, out PlayerStatusEffects effects) ||
                    effects == null)
                    continue;

                effects.ApplyEffectForSeconds(StatusEffectType.Shielded, data.ShieldLingerSeconds);
                area.Healed.Add(clientId);
            }
        }

        /// <summary>
        /// Rev BS-b (Q63-a) — il giocatore è nel campo: una parte del corpo (capsula del
        /// CharacterController) entro il raggio dal centro, e in linea di vista dal centro verso il
        /// petto o verso il punto del corpo più vicino. Pareti e porte bloccano, i corpi no.
        /// </summary>
        private bool IsInsideField(Vector3 center, float radius, int mask, PlayerHealthSystem player)
        {
            Transform body = player.transform;
            CharacterController capsule = player.GetComponent<CharacterController>();

            Vector3 bottom;
            Vector3 top;
            float bodyRadius;
            if (capsule != null)
            {
                Vector3 axisCenter = body.TransformPoint(capsule.center);
                float halfAxis = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
                bottom = axisCenter - Vector3.up * halfAxis;
                top = axisCenter + Vector3.up * halfAxis;
                bodyRadius = capsule.radius;
            }
            else
            {
                // Ripiego senza CharacterController: corpo verticale standard dai piedi.
                bottom = body.position + Vector3.up * 0.3f;
                top = body.position + Vector3.up * 1.5f;
                bodyRadius = 0.3f;
            }

            Vector3 nearest = ClosestPointOnSegment(center, bottom, top);
            if (Vector3.Distance(center, nearest) > radius + bodyRadius) return false;

            return IsLineClear(center, top, mask) || IsLineClear(center, nearest, mask);
        }

        /// <summary>Rev BS-b — nessun collider che non sia un corpo di giocatore tra a e b (trigger ignorati).</summary>
        private bool IsLineClear(Vector3 from, Vector3 to, int mask)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 1e-4f) return true;

            int count = Physics.RaycastNonAlloc(new Ray(from, delta / distance), lineOfSightHits, distance, mask,
                                                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider collider = lineOfSightHits[i].collider;
                if (collider == null) continue;
                if (collider.GetComponentInParent<PlayerHealthSystem>() != null) continue;   // i corpi non bloccano
                return false;
            }
            return true;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            if (lengthSquared < 1e-8f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSquared);
            return a + ab * t;
        }

        private static Transform ResolvePlayerRoot(ulong clientId)
        {
            if (clientId == NoClient) return null;
            return PlayerHealthSystem.TryGetByClientId(clientId, out PlayerHealthSystem player) && player != null
                ? player.transform
                : null;
        }

        private static ulong ResolveHitClient(Collider collider)
        {
            if (collider == null) return NoClient;
            PlayerHealthSystem player = collider.GetComponentInParent<PlayerHealthSystem>();
            return player != null ? player.OwnerClientId : NoClient;
        }

        private void RecordPath(ServerFlight flight, Vector3 point)
        {
            if (!drawDebugGizmos || flight.Id != debugLastPathId) return;
            if (debugLastPath.Count < 512) debugLastPath.Add(point);
        }

        // ── RPC server → tutti ────────────────────────────────────────────────

        [Rpc(SendTo.ClientsAndHost)]
        private void LaunchClientRpc(ushort flightId, byte dataIndex, ulong thrower, Vector3 origin, Vector3 velocity)
        {
            ThrowableData data = DataAt(dataIndex);
            if (data == null) return;

            if (clientFlights.TryGetValue(flightId, out ClientFlight stale))
                DestroyVisual(stale.Visual);

            Quaternion rotation = velocity.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(velocity) : Quaternion.identity;
            clientFlights[flightId] = new ClientFlight
            {
                Data = data,
                Thrower = thrower,
                State = ThrowBallistics.Begin(origin, velocity),
                Visual = SpawnVisual(data.ProjectileVisualPrefab, origin, rotation),
                Frozen = false,
                Age = 0f
            };
        }

        [Rpc(SendTo.ClientsAndHost)]
        private void BounceClientRpc(ushort flightId, Vector3 origin, Vector3 velocity)
        {
            if (!clientFlights.TryGetValue(flightId, out ClientFlight flight)) return;

            flight.State.SegmentOrigin = origin;
            flight.State.SegmentVelocity = velocity;
            flight.State.SegmentTime = 0f;
            flight.State.Bounces++;
            flight.Frozen = false;
            PlaceVisual(flight.Visual, origin, velocity);
        }

        [Rpc(SendTo.ClientsAndHost)]
        private void DetonateClientRpc(ushort flightId, byte dataIndex, ulong thrower, Vector3 point, ulong hitClient,
                                       float areaDuration)
        {
            if (clientFlights.TryGetValue(flightId, out ClientFlight flight))
            {
                DestroyVisual(flight.Visual);
                clientFlights.Remove(flightId);
            }

            ThrowableData data = DataAt(dataIndex);
            if (data != null && areaDuration > 0f && data.AreaVisualPrefab != null)
                SpawnArea(data, point, areaDuration);   // Rev BV-c: durata decisa al lancio

            OnClientDetonated?.Invoke(thrower, hitClient, data);
        }

        // ── Visuale client ────────────────────────────────────────────────────

        private void ClientTickFlights(float dt)
        {
            if (clientFlights.Count == 0) return;

            clientFlightsToRemove.Clear();
            foreach (KeyValuePair<ushort, ClientFlight> entry in clientFlights)
            {
                ClientFlight flight = entry.Value;
                ThrowableData data = flight.Data;
                flight.Age += dt;

                // Copia orfana (evento perso per despawn o disconnessione): si scarta.
                if (data == null || flight.Age > data.MaxFlightSeconds + ClientOrphanGraceSeconds)
                {
                    DestroyVisual(flight.Visual);
                    clientFlightsToRemove.Add(entry.Key);
                    continue;
                }
                if (flight.Frozen) continue;

                Transform ignoreRoot = ResolvePlayerRoot(flight.Thrower);
                ThrowStepResult result = ThrowBallistics.Advance(ref flight.State, dt, data.Gravity,
                                                                 data.ProjectileRadius, data.MaxFlightSeconds,
                                                                 data.CollisionMask, ignoreRoot, clientHits,
                                                                 out ThrowContact contact);
                if (result == ThrowStepResult.Contact)
                {
                    // Contatto locale: la copia si ferma qui; decide il server (Bounce o Detonate).
                    flight.Frozen = true;
                    PlaceVisual(flight.Visual, contact.Center, contact.Velocity);
                    continue;
                }

                PlaceVisual(flight.Visual, flight.State.Position(data.Gravity), flight.State.Velocity(data.Gravity));
                if (result == ThrowStepResult.Expired) flight.Frozen = true;
            }

            for (int i = 0; i < clientFlightsToRemove.Count; i++)
                clientFlights.Remove(clientFlightsToRemove[i]);
        }

        private void SpawnArea(ThrowableData data, Vector3 point, float duration)
        {
            GameObject visual = SpawnVisual(data.AreaVisualPrefab, point, Quaternion.identity);
            if (visual == null) return;

            // Il prefab è una sfera di diametro 1: scala al diametro dell'area.
            visual.transform.localScale = Vector3.one * (data.AreaRadius * 2f);

            Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
            var baseColors = new Color[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                Material shared = renderers[i] != null ? renderers[i].sharedMaterial : null;
                baseColors[i] = shared != null && shared.HasProperty(BaseColorId)
                    ? shared.GetColor(BaseColorId)
                    : Color.white;
            }

            clientAreas.Add(new ClientArea
            {
                Visual = visual,
                Elapsed = 0f,
                Duration = duration,
                FadeFraction = data.AreaFadeFraction,
                Renderers = renderers,
                BaseColors = baseColors
            });
        }

        private void ClientTickAreas(float dt)
        {
            for (int i = clientAreas.Count - 1; i >= 0; i--)
            {
                ClientArea area = clientAreas[i];
                area.Elapsed += dt;
                if (area.Visual == null || area.Elapsed >= area.Duration)
                {
                    DestroyVisual(area.Visual);
                    clientAreas.RemoveAt(i);
                    continue;
                }

                // Sfumatura nell'ultima frazione della durata (MaterialPropertyBlock su _BaseColor).
                if (area.FadeFraction <= 0f) continue;
                float progress = area.Elapsed / area.Duration;
                float fadeStart = 1f - area.FadeFraction;
                if (progress <= fadeStart) continue;

                float alphaScale = Mathf.Clamp01((1f - progress) / area.FadeFraction);
                for (int r = 0; r < area.Renderers.Length; r++)
                {
                    Renderer renderer = area.Renderers[r];
                    if (renderer == null) continue;
                    Color color = area.BaseColors[r];
                    color.a *= alphaScale;
                    renderer.GetPropertyBlock(propertyBlock);
                    propertyBlock.SetColor(BaseColorId, color);
                    renderer.SetPropertyBlock(propertyBlock);
                }
            }
        }

        private GameObject SpawnVisual(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (prefab == null) return null;
            GameObject instance = Instantiate(prefab, position, rotation, transform);
            DisableColliders(instance);
            return instance;
        }

        /// <summary>
        /// Visuale senza collider: un collider sulla bomba o sull'area fermerebbe gli sweep (e
        /// l'arco di mira). Il prefab non dovrebbe averne; qui per sicurezza.
        /// </summary>
        public static void DisableColliders(GameObject instance)
        {
            if (instance == null) return;
            Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                colliders[i].enabled = false;
        }

        private static void PlaceVisual(GameObject visual, Vector3 position, Vector3 velocity)
        {
            if (visual == null) return;
            visual.transform.position = position;
            if (velocity.sqrMagnitude > 1e-6f)
                visual.transform.rotation = Quaternion.LookRotation(velocity);
        }

        private static void DestroyVisual(GameObject visual)
        {
            if (visual != null) Destroy(visual);
        }

        private ThrowableData DataAt(byte index)
        {
            if (catalog != null && index < catalog.Length && catalog[index] != null)
                return catalog[index];

            Debug.LogError($"[ThrowableSystem] Indice di catalogo {index} senza ThrowableData su questo client: " +
                           "il catalogo deve essere identico su tutte le istanze.");
            return null;
        }

        private void LogV(string message)
        {
            if (logVerbose) Debug.Log($"[ThrowableSystem] {message}");
        }

        // ── Debug ─────────────────────────────────────────────────────────────

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showDebugUI) return;
            if (!IsServer || !IsSpawned) return;

            GUILayout.BeginArea(new Rect(730, Screen.height - 150, 420, 140));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[ThrowableSystem] voli {serverFlights.Count} · aree {serverAreas.Count} · " +
                            $"catalogo {(catalog != null ? catalog.Length : 0)}");
            if (debugRecords.Count == 0)
                GUILayout.Label("Nessuna detonazione.");
            for (int i = 0; i < debugRecords.Count; i++)
            {
                DetonationRecord record = debugRecords[i];
                string hit = record.Hit != NoClient ? $"colpito client {record.Hit}" : "nessun giocatore";
                GUILayout.Label($"#{record.Id} {record.Name} · da {record.Thrower} · {hit} · " +
                                $"({record.Point.x:F1}, {record.Point.y:F1}, {record.Point.z:F1}) · " +
                                $"{record.FlightTime:F2} s · rimb. {record.Bounces}");
            }
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!drawDebugGizmos) return;

            Gizmos.color = Color.yellow;
            for (int i = 1; i < debugLastPath.Count; i++)
                Gizmos.DrawLine(debugLastPath[i - 1], debugLastPath[i]);

            Gizmos.color = Color.green;
            for (int i = 0; i < serverAreas.Count; i++)
            {
                ServerArea area = serverAreas[i];
                if (area.Data != null) Gizmos.DrawWireSphere(area.Center, area.Data.AreaRadius);
            }
        }
#endif
    }
}