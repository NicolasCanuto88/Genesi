using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using static ElectricalDegradationManager;
using static SpaceSurvivor.Ship.ShieldSystem;

namespace SpaceSurvivor.Ship.Systems
{
    // ─── Enums ────────────────────────────────────────────────────────────
    // ZoneType e ZoneEvent vengono definiti qui; li usano anche PilotHUD e UI future.

    public enum ZoneType
    {
        Inner,      // EM None  — zona sicura
        Frontier,   // EM Weak  — rischio moderato
        DeepVoid    // EM Moderate baseline — alta pressione
    }

    public enum ZoneEvent
    {
        None,
        RadiationStorm,   // → ShieldContext RadiationStorm (87W) + EM Strong ×1.45
        AsteroidField,    // → ShieldContext AsteroidStorm (60W) + EM invariata
        SolarStorm,       // → EM Strong ×1.45, nessun cambio ShieldContext
        EMAnomaly         // → EM Extreme ×1.80 (rara), nessun cambio ShieldContext
    }

    // ─── ZoneManager ──────────────────────────────────────────────────────

    /// <summary>
    /// Gestisce zona spaziale corrente ed eventi ambientali.
    /// Authority: server. Propaga a ElectricalDegradationManager e ShieldSystem.
    /// Milestone 2.
    /// </summary>
    public class ZoneManager : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────
        public static ZoneManager Instance { get; private set; }
        public static event System.Action OnInstanceReady;

        // ── Network Variables ─────────────────────────────────────────────
        private readonly NetworkVariable<ZoneType> _netZone =
            new(ZoneType.Inner,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<ZoneEvent> _netEvent =
            new(ZoneEvent.None,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        // ── Unity Events (UI, Audio — tutti i client) ─────────────────────
        [System.Serializable]
        public class ZoneChangedEvent : UnityEvent<ZoneType, ZoneEvent> { }

        [Header("Events")]
        public ZoneChangedEvent OnZoneChanged;

        // ── Proprietà pubbliche ───────────────────────────────────────────
        public ZoneType CurrentZone => _netZone.Value;
        public ZoneEvent ActiveEvent => _netEvent.Value;

        // ── Lifecycle NGO ─────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            Instance = this;

            _netZone.OnValueChanged += (_, _) => ApplyCurrentState();
            _netEvent.OnValueChanged += (_, _) => ApplyCurrentState();

            // Push stato iniziale (tutti i client lo applicano per UI/audio)
            ApplyCurrentState();

            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            _netZone.OnValueChanged -= (_, _) => ApplyCurrentState();
            _netEvent.OnValueChanged -= (_, _) => ApplyCurrentState();
            if (Instance == this) Instance = null;
        }

        // ── API pubblica ──────────────────────────────────────────────────

        /// <summary>
        /// Cambia zona. Azzera automaticamente l'evento attivo.
        /// Chiamabile da qualsiasi client.
        /// </summary>
        public void SetZone(ZoneType zone)
        {
            if (IsServer) SetZoneInternal(zone);
            else SetZoneServerRpc(zone);
        }

        /// <summary>
        /// Attiva un evento ambientale (RadiationStorm, AsteroidField…).
        /// Chiamabile da qualsiasi client.
        /// </summary>
        public void TriggerEvent(ZoneEvent evt)
        {
            if (IsServer) SetEventInternal(evt);
            else SetEventServerRpc(evt);
        }

        /// <summary>Termina l'evento attivo. Chiamabile da qualsiasi client.</summary>
        public void ClearEvent() => TriggerEvent(ZoneEvent.None);

        // ── RPCs ──────────────────────────────────────────────────────────
        [Rpc(SendTo.Server)]
        private void SetZoneServerRpc(ZoneType zone) => SetZoneInternal(zone);

        [Rpc(SendTo.Server)]
        private void SetEventServerRpc(ZoneEvent evt) => SetEventInternal(evt);

        // ── Server internals ──────────────────────────────────────────────
        private void SetZoneInternal(ZoneType zone)
        {
            _netZone.Value = zone;
            _netEvent.Value = ZoneEvent.None; // reset evento al cambio zona
        }

        private void SetEventInternal(ZoneEvent evt)
        {
            _netEvent.Value = evt;
        }

        /// <summary>
        /// Propaga lo stato corrente a tutti i sistemi dipendenti.
        /// Push downstream → solo server (evita doppi RPC).
        /// Unity Event → tutti i client (UI, audio).
        /// </summary>
        private void ApplyCurrentState()
        {
            var em = ResolveEMIntensity(CurrentZone, ActiveEvent);
            var ctx = ResolveZoneContext(ActiveEvent);

            if (IsServer)
            {
                if (ElectricalDegradationManager.Instance != null)
                    ElectricalDegradationManager.Instance.SetEMIntensity(em);
                else
                    Debug.LogWarning("[ZoneManager] ElectricalDegradationManager non trovato — verrà applicato al prossimo cambio zona.");

                if (ShieldSystem.Instance != null)
                    ShieldSystem.Instance.SetZoneContext(ctx);
                else
                    Debug.LogWarning("[ZoneManager] ShieldSystem non trovato — verrà applicato al prossimo cambio zona.");
            }

            // UI e audio su tutti i client
            OnZoneChanged?.Invoke(CurrentZone, ActiveEvent);

            Debug.Log($"[ZoneManager] Zona:{CurrentZone} Evento:{ActiveEvent} → EM:{em} Ctx:{ctx}");
        }

        // ── Mapping GDD §9.7 ─────────────────────────────────────────────

        /// <summary>
        /// Zona + evento → EMIntensity.
        /// L'evento sovrascrive la baseline della zona se più alto (Mathf.Max sugli int).
        /// </summary>
        public static EMIntensity ResolveEMIntensity(ZoneType zone, ZoneEvent evt)
        {
            EMIntensity baseline = zone switch
            {
                ZoneType.Inner => EMIntensity.None,
                ZoneType.Frontier => EMIntensity.Weak,
                ZoneType.DeepVoid => EMIntensity.Moderate,
                _ => EMIntensity.None
            };

            EMIntensity eventEM = evt switch
            {
                ZoneEvent.RadiationStorm => EMIntensity.Strong,   // ×1.45
                ZoneEvent.SolarStorm => EMIntensity.Strong,   // ×1.45
                ZoneEvent.EMAnomaly => EMIntensity.Extreme,  // ×1.80
                ZoneEvent.AsteroidField => EMIntensity.None,     // hazard fisico, no EM
                _ => EMIntensity.None
            };

            // Se l'evento porta meno EM della zona baseline, vince la baseline
            return (EMIntensity)Mathf.Max((int)baseline, (int)eventEM);
        }

        /// <summary>
        /// Evento attivo → ZoneContext per ShieldSystem (determina consumo W).
        /// Combat context è gestito da M3 (CombatSystem), non da ZoneManager.
        /// </summary>
        public static ZoneContext ResolveZoneContext(ZoneEvent evt)
        {
            return evt switch
            {
                ZoneEvent.RadiationStorm => ZoneContext.RadiationStorm, // 87W
                ZoneEvent.AsteroidField => ZoneContext.AsteroidStorm,  // 60W
                _ => ZoneContext.Normal           // 25W
            };
        }

        // ── Debug GUI ─────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            var em = ResolveEMIntensity(CurrentZone, ActiveEvent);
            var ctx = ResolveZoneContext(ActiveEvent);

            GUILayout.BeginArea(new Rect(Screen.width - 235, 400, 225, 300));
            GUILayout.BeginVertical("box");

            GUILayout.Label($"[ZoneManager] {(IsServer ? "SERVER" : "CLIENT")}");
            GUILayout.Label($"Zona:   {CurrentZone}");
            GUILayout.Label($"Evento: {ActiveEvent}");
            GUILayout.Label($"EM:     {em}");
            GUILayout.Label($"Ctx:    {ctx}");

            GUILayout.Space(4);
            GUILayout.Label("─ Zona ─");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Inner")) SetZone(ZoneType.Inner);
            if (GUILayout.Button("Frontier")) SetZone(ZoneType.Frontier);
            if (GUILayout.Button("DeepVoid")) SetZone(ZoneType.DeepVoid);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label("─ Evento ─");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("RadStorm")) TriggerEvent(ZoneEvent.RadiationStorm);
            if (GUILayout.Button("Asteroids")) TriggerEvent(ZoneEvent.AsteroidField);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("SolarStorm")) TriggerEvent(ZoneEvent.SolarStorm);
            if (GUILayout.Button("EM Anomaly")) TriggerEvent(ZoneEvent.EMAnomaly);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Clear Event")) ClearEvent();

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}