using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ShipSkyboxRotator — Milestone 3, Blocco 3, Fase 2, Sottofase 2c (v2).
    ///
    /// Passa ogni frame una matrice di rotazione allo shader
    /// SpaceSurvivor/SkyboxRotated6Sided via Shader.SetGlobalMatrix, così che
    /// lo skybox 6-sided ruoti in senso inverso a ship.LogicalRotation dando
    /// al pilota il feedback visuale del moto rotazionale della nave.
    ///
    /// MODELLO — CASO DEGENERE DI EXTERNAL WORLD FOLLOWER:
    ///   ExternalWorldFollower applica ogni frame:
    ///     P_visual = pivot + Inverse(shipRot) * (P_logical - shipPos)
    ///     R_visual = Inverse(shipRot) * R_initial_logical
    ///
    ///   La skybox è infinitamente lontana (parallasse infinito → nessuna
    ///   posizione, solo orientamento) e non ha un R_initial_logical proprio
    ///   (per convenzione identity). Formula degenere:
    ///
    ///     R_skybox = Inverse(shipLogicalRotation)
    ///
    /// PERCHÉ NON USA UN TRANSFORM (differenza dalle versioni v1 con sfera):
    ///   Lo skybox in Unity non è un GameObject in scena — è un Material
    ///   assegnato a Lighting Settings → Environment → Skybox Material,
    ///   disegnato dal renderer come 6 quad ai bordi del frustum. La
    ///   rotazione si applica passando una matrice globale che lo shader
    ///   applica al vertex object-space. Nessuna sfera, nessuna gerarchia,
    ///   nessun layer separato.
    ///
    /// SCELTA DI RETE: MonoBehaviour puro, NON NetworkBehaviour. Coerente
    /// con ExternalWorldFollower — legge stato replicato già esistente di
    /// ShipMovement (LogicalRotation è NetworkVariable server-authoritative).
    /// Ogni client applica la stessa formula agli stessi valori replicati →
    /// tutti vedono la stessa rotazione senza traffico di rete aggiuntivo.
    ///
    /// PIAZZAMENTO COMPONENTE:
    ///   Attaccare a un qualunque GameObject persistente della scena. Il
    ///   candidato naturale è "Nave" (già esistente, statico all'origine),
    ///   ma qualunque GameObject non-distruttibile va bene — il componente
    ///   non ha dipendenze sul suo Transform.
    ///
    /// SETUP EDITOR RICHIESTO:
    ///   1. Material "SkyboxRotatedMat" con shader
    ///      SpaceSurvivor/SkyboxRotated6Sided, 6 texture assegnate.
    ///   2. Lighting Settings → Environment → Skybox Material → SkyboxRotatedMat.
    ///   3. Lighting Settings → Environment → Source → Skybox (default).
    ///   4. MainCamera → Clear Flags → Skybox (default).
    ///   5. Questo componente su un GameObject qualsiasi persistente.
    ///
    /// DIPENDE DA: ShipMovement (Instance + OnInstanceReady + LogicalRotation)
    /// </summary>
    public class ShipSkyboxRotator : MonoBehaviour
    {
        [Header("Toggle di test")]
        [Tooltip("Se true, lo skybox ruota in senso inverso a " +
                 "ship.LogicalRotation. Se false, resta fermo (utile per " +
                 "verificare l'orientamento di riferimento).")]
        [SerializeField] private bool applyRotation = true;

        [Header("Debug")]
        [Tooltip("Se true, stampa un log al primo bind con ShipMovement.Instance " +
                 "e mostra un OnGUI con la rotazione corrente. Lasciare OFF in " +
                 "produzione.")]
        [SerializeField] private bool verboseLogging = false;

        // Property ID cached per performance (evita string lookup ogni frame).
        // Deve corrispondere ESATTAMENTE al nome della variabile nello shader
        // (SpaceSurvivor/SkyboxRotated6Sided).
        private static readonly int SkyboxRotationMatrixID =
            Shader.PropertyToID("_SkyboxRotationMatrix");

        private bool _initialized;

        // ── Lifecycle ────────────────────────────────────────────────────────

        private void OnEnable()
        {
            // Reset a identity all'avvio. Necessario perché in Unity una
            // matrice globale non settata vale zero-matrix (non identity),
            // che ridurrebbe positionOS a zero → skybox nera al primo frame
            // prima che HandleInstanceReady venga chiamato.
            Shader.SetGlobalMatrix(SkyboxRotationMatrixID, Matrix4x4.identity);

            if (_initialized) return;

            if (ShipMovement.Instance != null)
            {
                HandleInstanceReady();
            }
            else
            {
                ShipMovement.OnInstanceReady += HandleInstanceReady;
            }
        }

        private void OnDisable()
        {
            ShipMovement.OnInstanceReady -= HandleInstanceReady;
            _initialized = false;

            // Reset a identity quando disabilitato — evita che lo skybox
            // resti "congelato" in una rotazione se il rotator viene tolto
            // o disabilitato dinamicamente.
            Shader.SetGlobalMatrix(SkyboxRotationMatrixID, Matrix4x4.identity);
        }

        private void HandleInstanceReady()
        {
            ShipMovement.OnInstanceReady -= HandleInstanceReady;
            _initialized = true;

            if (verboseLogging)
            {
                string mode = applyRotation ? "attiva" : "congelata (test)";
                Debug.Log($"[ShipSkyboxRotator] bind OK con " +
                          $"ShipMovement.Instance. Modalità {mode}.");
            }
        }

        // ── Update ───────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_initialized) return;

            var ship = ShipMovement.Instance;
            if (ship == null) return; // difensivo: potrebbe essere despawnato

            // Toggle di test: se applyRotation è false, lo skybox resta
            // nella rotazione di riferimento (identity) invece di ruotare.
            Quaternion rot = applyRotation
                ? Quaternion.Inverse(ship.LogicalRotation)
                : Quaternion.identity;

            // Formula chiusa (caso degenere del Follower):
            //   R_skybox = Inverse(shipLogicalRotation)
            // Passata come matrice 4x4 allo shader; il vertex shader userà
            // solo la componente 3x3 di rotazione (mul((float3x3)matrix, pos)).
            Shader.SetGlobalMatrix(SkyboxRotationMatrixID, Matrix4x4.Rotate(rot));
        }

        // ── Debug GUI ────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!verboseLogging) return;
            if (!_initialized || ShipMovement.Instance == null) return;

            var ship = ShipMovement.Instance;
            Vector3 shipEuler = ship.LogicalRotation.eulerAngles;
            string applyLabel = applyRotation ? "ON" : "OFF (frozen)";

            GUILayout.BeginArea(new Rect(360, Screen.height - 200, 360, 100));
            GUILayout.BeginVertical("box");
            GUILayout.Label("[ShipSkyboxRotator]");
            GUILayout.Label($"Ship Rot: yaw {NormalizeAngleDisplay(shipEuler.y):F1}° · " +
                            $"pitch {NormalizeAngleDisplay(shipEuler.x):F1}°");
            GUILayout.Label($"Apply: {applyLabel}");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private static float NormalizeAngleDisplay(float angleDeg)
        {
            angleDeg %= 360f;
            if (angleDeg > 180f) angleDeg -= 360f;
            else if (angleDeg < -180f) angleDeg += 360f;
            return angleDeg;
        }
#endif
    }
}