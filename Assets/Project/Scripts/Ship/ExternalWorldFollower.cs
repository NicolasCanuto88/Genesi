using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ExternalWorldFollower — Milestone 3, Blocco 3 (prima implementazione).
    ///
    /// Componente da attaccare a QUALUNQUE GameObject del "mondo esterno"
    /// (asteroidi, relitti, stazioni, starfield distante) che deve dare
    /// l'illusione di scorrere rispetto alla nave. Legge lo stato LOGICO
    /// di movimento da ShipMovement.Instance e applica il DELTA INVERSO
    /// alla propria Transform ogni frame:
    ///   - traslazione: -LogicalForward × CurrentSpeed × Time.deltaTime
    ///   - rotazione: -deltaYaw attorno a shipReference (o Vector3.zero)
    ///
    /// La nave (e i player al suo interno) resta ferma. Sono gli oggetti
    /// esterni che si muovono — il risultato visuale è indistinguibile
    /// dal "movimento reale della nave", e per costruzione elimina i
    /// problemi di precisione a coordinate molto grandi (nave e player
    /// non si allontanano mai dall'origine del mondo).
    ///
    /// SCELTA DI RETE: MonoBehaviour puro, NON NetworkBehaviour. Il
    /// movimento è simulazione client-side deterministica dallo stato
    /// GIÀ replicato di ShipMovement (LogicalYawDegrees è NetworkVariable,
    /// CurrentSpeed è letto da PropulsionSystem che è a sua volta
    /// server-authoritative). Ogni client applica lo stesso identico
    /// delta agli stessi valori replicati → tutti vedono la stessa
    /// posizione senza traffico di rete aggiuntivo. Vantaggio: il
    /// costo di rete NON scala col numero di oggetti esterni (100
    /// asteroidi = 1 asteroide, dal punto di vista della banda). Un
    /// eventuale NetworkTransform per asteroide sarebbe stato invece
    /// costoso e non necessario, dato che questi oggetti non hanno
    /// interazione fisica non-deterministica coi player nella fase di
    /// "scorrimento" (avvicinamento e saccheggio saranno gestiti da un
    /// sistema separato, con la nave ancorata e il mondo fermo).
    ///
    /// USO TIPICO:
    ///   1. Attaccare a un GameObject "ExternalWorldRoot" che contiene
    ///      tutti gli oggetti di sfondo (starfield, asteroidi di test,
    ///      relitti visibili in lontananza)
    ///   2. Assegnare shipReference al Transform di "Nave" (opzionale —
    ///      default Vector3.zero, funziona finché "Nave" è all'origine)
    ///   3. Play → il mondo scorre quando il pilota accelera in MANUAL/
    ///      AUTOPILOT/COASTING, ruota quando il pilota sterza in MANUAL
    ///
    /// TEST MINIMO (Blocco 3 fase 1):
    ///   - 1 asteroide di prova a ~50m davanti alla nave
    ///   - Un solo ExternalWorldFollower su quel GameObject
    ///   - Pilota entra in postazione, MANUAL, accelera → l'asteroide
    ///     scorre verso di lui e alla fine gli passa vicino/dietro
    ///
    /// DIPENDE DA: ShipMovement (Instance + OnInstanceReady)
    /// </summary>
    public class ExternalWorldFollower : MonoBehaviour
    {
        [Header("Configurazione pivot")]
        [Tooltip("Transform attorno a cui ruotare quando la nave gira. " +
                 "Tipicamente il GameObject 'Nave'. Se null, usa Vector3.zero " +
                 "(corretto finché 'Nave' è piazzata all'origine).")]
        [SerializeField] private Transform shipReference;

        [Header("Toggle di test (Blocco 3)")]
        [Tooltip("Se true, applica la traslazione inversa. Utile disattivare " +
                 "per testare solo la rotazione, o per congelare il mondo " +
                 "in fase di setup scena.")]
        [SerializeField] private bool applyTranslation = true;

        [Tooltip("Se true, applica la rotazione inversa attorno a shipReference. " +
                 "Utile disattivare per testare solo la traslazione.")]
        [SerializeField] private bool applyRotation = true;

        [Header("Debug")]
        [Tooltip("Se true, stampa un log al primo bind con ShipMovement.Instance " +
                 "e mostra un OnGUI con posizione/delta correnti. Lasciare OFF " +
                 "in produzione.")]
        [SerializeField] private bool verboseLogging = false;

        // ── Stato interno ────────────────────────────────────────────────────
        private float _previousYaw;
        private bool _initialized;

        // ── Lifecycle ────────────────────────────────────────────────────────

        private void OnEnable()
        {
            // ShipMovement potrebbe non essere ancora spawnato (specialmente
            // ai primi frame della scena, o su un client che entra in una
            // sessione già in corso). Se non c'è, ci sottoscriviamo a
            // OnInstanceReady e inizializziamo quando arriva.
            if (ShipMovement.Instance != null)
            {
                Initialize();
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
        }

        private void HandleInstanceReady()
        {
            ShipMovement.OnInstanceReady -= HandleInstanceReady;
            Initialize();
        }

        private void Initialize()
        {
            // Cattura il yaw corrente come punto di partenza — evita un
            // "salto" al primo frame se la nave stava già ruotando prima
            // che questo componente si abilitasse (es. hot-reload in Editor).
            _previousYaw = ShipMovement.Instance.LogicalYawDegrees;
            _initialized = true;

            if (verboseLogging)
                Debug.Log($"[ExternalWorldFollower] {name}: bind con ShipMovement completato. " +
                          $"Yaw iniziale: {_previousYaw:F1}°");
        }

        // ── Update ───────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_initialized) return;

            var ship = ShipMovement.Instance;
            if (ship == null) return; // difensivo: potrebbe essere despawnato

            // ── Rotazione inversa ───────────────────────────────────────────
            // Delta yaw dal frame precedente. Se il pilota ha ruotato la nave
            // di +deltaYaw, il mondo deve ruotare di -deltaYaw attorno alla
            // nave — così un asteroide davanti resta "visivamente davanti"
            // rispetto alla nuova direzione della nave. La rotazione modifica
            // sia la posizione che l'orientamento dell'oggetto (RotateAround).
            if (applyRotation)
            {
                float currentYaw = ship.LogicalYawDegrees;
                float deltaYaw = currentYaw - _previousYaw;

                if (Mathf.Abs(deltaYaw) > 0.0001f)
                {
                    Vector3 pivot = shipReference != null
                        ? shipReference.position
                        : Vector3.zero;

                    transform.RotateAround(pivot, Vector3.up, -deltaYaw);
                }

                _previousYaw = currentYaw;
            }

            // ── Traslazione inversa ─────────────────────────────────────────
            // Se la nave si sta muovendo (COASTING/AUTOPILOT/MANUAL con
            // velocità > 0), il mondo trasla in direzione opposta a
            // LogicalForward × CurrentSpeed × Time.deltaTime. Sotto una
            // soglia minima evitiamo aggiornamenti inutili quando la nave
            // è ferma o pressoché ferma (evita jitter numerico).
            if (applyTranslation && ship.CurrentSpeed > 0.01f)
            {
                Vector3 shipVelocity = ship.LogicalForward * ship.CurrentSpeed;
                transform.position -= shipVelocity * Time.deltaTime;
            }
        }

        // ── Debug GUI ────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!verboseLogging) return;
            if (!_initialized || ShipMovement.Instance == null) return;

            var ship = ShipMovement.Instance;
            GUILayout.BeginArea(new Rect(340, Screen.height - 90, 320, 80));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[ExtWorldFollower] {name}");
            GUILayout.Label($"Pos: {transform.position.x:F1}, {transform.position.z:F1}");
            GUILayout.Label($"ShipSpeed: {ship.CurrentSpeed:F1} m/s · Yaw: {ship.LogicalYawDegrees:F1}°");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}
