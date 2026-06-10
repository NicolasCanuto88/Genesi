using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using SpaceSurvivor.Ship;                  // ← AGGIUNTO: PropulsionSystem
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
    /// Authority: server. Propaga a ElectricalDegradationManager, ShieldSystem
    /// e PropulsionSystem (disponibilità autopilota).
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

        /// <summary>Cambia zona. Azzera automaticamente l'evento attivo.</summary>
        public void SetZone(ZoneType zone)
        {
            if (IsServer) SetZoneInternal(zone);
            else SetZoneServerRpc(zone);
        }

        /// <summary>Attiva un evento ambientale. NON attiva protezioni automaticamente.</summary>
        public void TriggerEvent(ZoneEvent evt)
        {
            if (IsServer) SetEventInternal(evt);
            else SetEventServerRpc(evt);
        }

        /// <summary>Termina l'evento attivo.</summary>
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
            _netEvent.Value = ZoneEvent.None;
        }

        private void SetEventInternal(ZoneEvent evt)
        {
            _netEvent.Value = evt;
        }

        /// <summary>
        /// Propaga lo stato ambientale corrente ai sistemi dipendenti.
        /// Aggiorna SOLO parametri passivi — non attiva mai protezioni.
        /// </summary>
        private void ApplyCurrentState()
        {
            var em = ResolveEMIntensity(CurrentZone, ActiveEvent);
            var ctx = ResolveZoneContext(ActiveEvent);

            if (IsServer)
            {
                // Moltiplicatore EM → degrado elettrico (effetto ambientale passivo)
                if (ElectricalDegradationManager.Instance != null)
                    ElectricalDegradationManager.Instance.SetEMIntensity(em);
                else
                    Debug.LogWarning("[ZoneManager] ElectricalDegradationManager non trovato.");

                // Cost tier scudi → solo il costo W se già attivi (non attiva gli scudi)
                if (ShieldSystem.Instance != null)
                    ShieldSystem.Instance.SetZoneContext(ctx);
                else
                    Debug.LogWarning("[ZoneManager] ShieldSystem non trovato.");

                // Disponibilità autopilota → disabilitato in AsteroidField   ← AGGIUNTO
                UpdateAutopilotAvailability(ActiveEvent);
            }

            OnZoneChanged?.Invoke(CurrentZone, ActiveEvent);

            Debug.Log($"[ZoneManager] Zona:{CurrentZone} Evento:{ActiveEvent} → EM:{em} Ctx:{ctx}");
        }

        // ── Autopilota (AGGIUNTO) ─────────────────────────────────────────

        /// <summary>
        /// Comunica a PropulsionSystem se il pilota automatico è disponibile.
        /// Durante AsteroidField il campo richiede guida manuale.
        ///
        /// Regola invariante ZoneManager: aggiorna un parametro passivo,
        /// NON forza lo stato di navigazione. È il Pilota a decidere.
        /// </summary>
        private void UpdateAutopilotAvailability(ZoneEvent evt)
        {
            bool available = evt != ZoneEvent.AsteroidField;

            if (PropulsionSystem.Instance != null)
                PropulsionSystem.Instance.SetAutopilotAvailable(available);
            // Se PropulsionSystem non è ancora in scena: nessun warning —
            // verrà aggiornato alla prossima variazione di evento.
        }

        // ── Mapping GDD §9.7 ─────────────────────────────────────────────

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
                ZoneEvent.RadiationStorm => EMIntensity.Strong,
                ZoneEvent.SolarStorm => EMIntensity.Strong,
                ZoneEvent.EMAnomaly => EMIntensity.Extreme,
                ZoneEvent.AsteroidField => EMIntensity.None,
                _ => EMIntensity.None
            };

            return (EMIntensity)Mathf.Max((int)baseline, (int)eventEM);
        }

        public static ZoneContext ResolveZoneContext(ZoneEvent evt)
        {
            return evt switch
            {
                ZoneEvent.RadiationStorm => ZoneContext.RadiationStorm,
                ZoneEvent.AsteroidField => ZoneContext.AsteroidStorm,
                _ => ZoneContext.Normal
            };
        }

        // ── Debug GUI ─────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            var em = ResolveEMIntensity(CurrentZone, ActiveEvent);
            var ctx = ResolveZoneContext(ActiveEvent);

            GUILayout.BeginArea(new Rect(Screen.width - 235, 400, 225, 310));
            GUILayout.BeginVertical("box");

            GUILayout.Label($"[ZoneManager] {(IsServer ? "SERVER" : "CLIENT")}");
            GUILayout.Label($"Zona:     {CurrentZone}");
            GUILayout.Label($"Evento:   {ActiveEvent}");
            GUILayout.Label($"EM:       {em}");
            GUILayout.Label($"Ctx:      {ctx}");
            GUILayout.Label($"Autopilot:{(ActiveEvent != ZoneEvent.AsteroidField ? "OK" : "DISABLED")}");

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