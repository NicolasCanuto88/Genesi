using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ExternalWorldFollower — Milestone 3, Blocco 3 (Rev S → Rev T).
    ///
    /// Componente da attaccare a QUALUNQUE GameObject del "mondo esterno"
    /// (asteroidi, relitti, stazioni, starfield distante) che deve dare
    /// l'illusione di scorrere rispetto alla nave. Legge lo stato LOGICO
    /// di movimento da ShipMovement.Instance e applica il DELTA INVERSO
    /// alla propria Transform ogni frame:
    ///   - traslazione: -LogicalForward × CurrentSpeed × Time.deltaTime
    ///   - rotazione: Inverse(deltaRotation) applicato rispetto a
    ///     shipReference (o Vector3.zero se non assegnato)
    ///
    /// La nave (e i player al suo interno) resta ferma. Sono gli oggetti
    /// esterni che si muovono — il risultato visuale è indistinguibile
    /// dal "movimento reale della nave", e per costruzione elimina i
    /// problemi di precisione a coordinate molto grandi (nave e player
    /// non si allontanano mai dall'origine del mondo).
    ///
    /// AGGIORNAMENTO Rev T (dopo estensione ShipMovement a Quaternion):
    ///   Era: delta yaw scalare (float) → RotateAround(pivot, up, -deltaYaw).
    ///   Ora: delta Quaternion → Inverse applicato a mano a posizione
    ///        relativa al pivot e a orientamento. Supporta pilotaggio 3D
    ///        (yaw + pitch) senza cambiare l'interfaccia esterna del
    ///        componente. Normalizzazione periodica del transform.rotation
    ///        per prevenire drift accumulativo su tempi lunghi.
    ///
    /// SCELTA DI RETE: MonoBehaviour puro, NON NetworkBehaviour. Il
    /// movimento è simulazione client-side deterministica dallo stato
    /// GIÀ replicato di ShipMovement (LogicalRotation e LogicalPosition
    /// sono NetworkVariable; CurrentSpeed è letto da PropulsionSystem che
    /// è a sua volta server-authoritative con _netCurrentSpeed). Ogni
    /// client applica lo stesso identico delta agli stessi valori
    /// replicati → tutti vedono la stessa posizione senza traffico di
    /// rete aggiuntivo. Il costo di rete NON scala col numero di oggetti
    /// esterni (100 asteroidi = 1 asteroide, dal punto di vista della
    /// banda).
    ///
    /// USO TIPICO:
    ///   1. Attaccare a un GameObject "ExternalWorldRoot" che contiene
    ///      tutti gli oggetti di sfondo (starfield, asteroidi di test,
    ///      relitti visibili in lontananza)
    ///   2. Assegnare shipReference al Transform di "Nave" (opzionale —
    ///      default Vector3.zero, funziona finché "Nave" è all'origine)
    ///   3. Play → il mondo scorre quando il pilota accelera in MANUAL/
    ///      AUTOPILOT/COASTING, ruota su yaw+pitch quando sterza in MANUAL
    ///
    /// DIPENDE DA: ShipMovement (Instance + OnInstanceReady + LogicalRotation
    ///             + LogicalForward + CurrentSpeed)
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
        // Rev T: era _previousYaw (float). Ora è il quaternion completo del
        // frame precedente, così il delta cattura yaw+pitch insieme.
        private Quaternion _previousRotation;
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
            // Cattura la rotazione corrente come punto di partenza — evita un
            // "salto" al primo frame se la nave stava già ruotando prima
            // che questo componente si abilitasse (es. hot-reload in Editor).
            _previousRotation = ShipMovement.Instance.LogicalRotation;
            _initialized = true;

            if (verboseLogging)
            {
                Vector3 euler = _previousRotation.eulerAngles;
                Debug.Log($"[ExternalWorldFollower] {name}: bind con ShipMovement completato. " +
                          $"Rotazione iniziale: yaw {euler.y:F1}° · pitch {euler.x:F1}°");
            }
        }

        // ── Update ───────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_initialized) return;

            var ship = ShipMovement.Instance;
            if (ship == null) return; // difensivo: potrebbe essere despawnato

            // ── Rotazione inversa ───────────────────────────────────────────
            // Delta quaternion dal frame precedente. Se la nave è ruotata
            // di deltaRotation, il mondo deve subire Inverse(deltaRotation)
            // rispetto al pivot — sia in posizione (ruota il vettore
            // pivot→oggetto) sia in orientamento (l'oggetto stesso ruota
            // in senso opposto).
            //
            // Nota su drift: deltaRotation è ricalcolato da zero ogni frame
            // (non composto ricorsivamente in locale). L'unica composizione
            // ricorsiva è transform.rotation ← inverseDelta * transform.rotation,
            // che su tempi lunghi può accumulare errore numerico. Normalizziamo
            // il risultato per blindarci.
            if (applyRotation)
            {
                Quaternion currentRotation = ship.LogicalRotation;
                Quaternion deltaRotation = currentRotation * Quaternion.Inverse(_previousRotation);

                // Ignora delta praticamente nulli (nave ferma o quasi) —
                // evita computazione inutile e micro-jitter numerico.
                if (Quaternion.Angle(deltaRotation, Quaternion.identity) > 0.001f)
                {
                    Quaternion inverseDelta = Quaternion.Inverse(deltaRotation);
                    Vector3 pivot = shipReference != null
                        ? shipReference.position
                        : Vector3.zero;

                    // Ruota il vettore pivot→oggetto per la nuova posizione
                    Vector3 offsetFromPivot = transform.position - pivot;
                    transform.position = pivot + inverseDelta * offsetFromPivot;

                    // Ruota l'orientamento dell'oggetto per lo stesso delta,
                    // poi normalizza per prevenire drift accumulativo.
                    transform.rotation = (inverseDelta * transform.rotation).normalized;
                }

                _previousRotation = currentRotation;
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
            Vector3 euler = ship.LogicalRotation.eulerAngles;

            GUILayout.BeginArea(new Rect(360, Screen.height - 100, 340, 90));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[ExtWorldFollower] {name}");
            GUILayout.Label($"Pos: {transform.position.x:F1}, {transform.position.y:F1}, {transform.position.z:F1}");
            GUILayout.Label($"ShipSpeed: {ship.CurrentSpeed:F1} m/s");
            GUILayout.Label($"ShipRot: yaw {euler.y:F1}° · pitch {NormalizeAngleDisplay(euler.x):F1}°");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private static float NormalizeAngleDisplay(float angleDeg)
        {
            angleDeg %= 360f;
            if (angleDeg > 180f) angleDeg -= 360f;
            return angleDeg;
        }
#endif
    }
}