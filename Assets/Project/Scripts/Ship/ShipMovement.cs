using System;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ShipMovement — Milestone 3, Blocco 2 (RISCRITTO dopo ampia
    /// sperimentazione — vedi SESSION_HANDOFF per la cronologia completa di
    /// tentativi falliti: nave che trasla/ruota davvero nel mondo, con
    /// player agganciati ad essa via parenting/delta-tracking, si è
    /// rivelata un terreno di bug profondi — CharacterController che non
    /// segue piattaforme in movimento è un limite DOCUMENTATO di Unity
    /// stesso, non risolvibile con workaround lato player).
    ///
    /// DECISIONE ARCHITETTURALE FINALE: "Nave" non si muove MAI
    /// fisicamente. Resta esattamente dov'è piazzata in Editor, per
    /// sempre. Questo script traccia SOLO lo stato LOGICO del movimento:
    ///   - Eredita NavigationState/CurrentSpeed da PropulsionSystem
    ///     (nessuna duplicazione di stato, solo lettura)
    ///   - Accumula un orientamento LOGICO (yaw) per il volo MANUAL,
    ///     sincronizzato in rete via NetworkVariable
    ///   - Espone questi dati per: HUD, calcoli di distanza/ETA, e in
    ///     futuro (Blocco 3+) per muovere in senso INVERSO il mondo
    ///     esterno (asteroidi, relitti, stazioni) attorno alla nave ferma —
    ///     pattern comune nei giochi spaziali, ha anche il vantaggio di
    ///     evitare problemi di precisione a coordinate molto grandi (la
    ///     nave/il player restano sempre vicino all'origine del mondo).
    ///
    /// USO PREVISTO PER IL MONDO ESTERNO (Blocco 3+):
    ///   Un futuro "ExternalWorldFollower" (o nome simile), posto sul
    ///   GameObject radice che conterrà asteroidi/relitti/stazioni visivi,
    ///   leggerà LogicalForward × CurrentSpeed (velocità) e
    ///   LogicalYawDegrees (rotazione) per traslare/ruotare quel gruppo in
    ///   senso INVERSO rispetto a questi valori — dando l'illusione che la
    ///   nave (ferma) si muova rispetto al mondo (che in realtà si muove
    ///   lui). Nessun codice di quel sistema esiste ancora: questo script
    ///   si limita a esporre i dati necessari in modo pulito.
    ///
    /// NESSUN Rigidbody, NESSUN NetworkTransform: a differenza dei
    /// tentativi precedenti, questo script non sposta alcuna Transform —
    /// è puro stato (NetworkVariable), niente altro.
    ///
    /// DIPENDE DA: PropulsionSystem ✅ (CurrentNavState, CurrentSpeed)
    /// </summary>
    public class ShipMovement : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static ShipMovement Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── Steering manuale (logico) ────────────────────────────────────────
        [Header("Steering Manuale (logico — non muove 'Nave')")]
        [Tooltip("Gradi/secondo di yaw logico a input di sterzata massimo (±1). " +
                 "Usato in futuro per ruotare il mondo esterno in senso inverso " +
                 "(Blocco 3+) — non ha alcun effetto visibile finché quel " +
                 "sistema non esiste.")]
        [SerializeField] private float manualYawSpeedDegPerSec = 25f;

        // ── Stato di rete ─────────────────────────────────────────────────────
        private readonly NetworkVariable<float> _logicalYawDegrees = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Stato server-only (non serve replicarlo: solo il server lo consuma in FixedUpdate)
        private float _manualSteerInput; // [-1, 1]

        /// <summary>Yaw logico cumulativo, gradi — repliacato a tutti i client.</summary>
        public float LogicalYawDegrees => _logicalYawDegrees.Value;

        /// <summary>
        /// Direzione di marcia logica corrente, in coordinate mondo (yaw
        /// applicato a Vector3.forward). NON la direzione verso cui punta
        /// "Nave" (che non ruota mai) — è puramente concettuale, pronta per
        /// il futuro sistema di mondo esterno inverso.
        /// </summary>
        public Vector3 LogicalForward => Quaternion.Euler(0f, _logicalYawDegrees.Value, 0f) * Vector3.forward;

        /// <summary>Velocità logica attuale (m/s) — da PropulsionSystem, nessuna duplicazione.</summary>
        public float CurrentSpeed =>
            PropulsionSystem.Instance != null ? PropulsionSystem.Instance.CurrentSpeed : 0f;

        public NavigationState CurrentNavState =>
            PropulsionSystem.Instance != null ? PropulsionSystem.Instance.CurrentNavState : NavigationState.Anchored;

        // =========================================================================
        // LIFECYCLE NGO
        // =========================================================================

        public override void OnNetworkSpawn()
        {
            Instance = this;
            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // =========================================================================
        // AGGIORNAMENTO STATO LOGICO (solo server)
        // =========================================================================

        private void FixedUpdate()
        {
            if (!IsServer) return;
            if (CurrentNavState != NavigationState.Manual) return;
            if (Mathf.Approximately(_manualSteerInput, 0f)) return;

            float delta = _manualSteerInput * manualYawSpeedDegPerSec * Time.fixedDeltaTime;
            _logicalYawDegrees.Value += delta;
        }

        // =========================================================================
        // API PUBBLICA
        // =========================================================================

        /// <summary>
        /// Chiamato da PilotStation, una volta per frame, mentre il Pilota è
        /// seduto e NavigationState == Manual. steerX atteso in [-1, 1] (X
        /// dell'azione Look — mouse/stick). Aggiorna solo lo stato LOGICO
        /// (LogicalYawDegrees) — non muove né ruota "Nave".
        /// </summary>
        public void SetManualSteerInput(float steerX)
        {
            float clamped = Mathf.Clamp(steerX, -1f, 1f);

            if (IsServer) _manualSteerInput = clamped;
            else          SetManualSteerInputRpc(clamped);
        }

        [Rpc(SendTo.Server)]
        private void SetManualSteerInputRpc(float steerX) => _manualSteerInput = steerX;

        // =========================================================================
        // DEBUG GUI
        // =========================================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, Screen.height - 90, 320, 80));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[ShipMovement] {(IsServer ? "SRV" : "CLT")} (stato logico — 'Nave' non si muove)");
            GUILayout.Label($"NavState: {CurrentNavState} · Speed: {CurrentSpeed:F1} m/s");
            GUILayout.Label($"Yaw logico: {LogicalYawDegrees:F1}°");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}
