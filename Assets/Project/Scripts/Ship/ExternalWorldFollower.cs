using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ExternalWorldFollower — Milestone 3, Blocco 3 (Rev S → Rev T → Rev T.1).
    ///
    /// Componente da attaccare a QUALUNQUE GameObject del "mondo esterno"
    /// (asteroidi, relitti, stazioni, starfield distante) che deve dare
    /// l'illusione di scorrere rispetto alla nave.
    ///
    /// MODELLO (Rev T.1) — FORMULA CHIUSA SU STATO LOGICO REPLICATO:
    ///   Ogni oggetto vive in una posizione LOGICA costante nello "spazio
    ///   logico" (frame globale in cui gli asteroidi sono fermi e la nave si
    ///   muove). La posizione visiva in worldspace Unity si ottiene ogni
    ///   frame come:
    ///
    ///     P_visual = pivot + Inverse(shipLogicalRot) * (P_logical - shipLogicalPos)
    ///
    ///   dove:
    ///     - pivot                = shipReference.position (o Vector3.zero)
    ///     - shipLogicalRot / Pos = ShipMovement.LogicalRotation / .LogicalPosition
    ///                              (NetworkVariable server-authoritative)
    ///     - P_logical            = costante seedata via uno dei due
    ///                              percorsi di inizializzazione (vedi USO
    ///                              TIPICO sotto)
    ///
    ///   L'orientamento dell'oggetto è mantenuto coerente:
    ///     T_visual_rotation = Inverse(shipLogicalRot) * T_initial_logical_rotation
    ///
    /// PERCHÉ NON PIÙ IL DELTA FRAME-PER-FRAME (Rev T originale):
    ///   La versione Rev T calcolava deltaRotation = current * Inverse(previous)
    ///   e componeva transform.rotation e transform.position ricorsivamente ogni
    ///   frame. Funziona ma:
    ///     - Client e server integravano indipendentemente CurrentSpeed × dt con
    ///       campionamenti diversi (FixedUpdate vs Update), causando divergenza
    ///       della posizione visiva rispetto a LogicalPosition sul server.
    ///     - La divergenza è invisibile finché tutto il mondo esterno usa lo
    ///       stesso integrale locale, ma DIVENTA VISIBILE non appena si
    ///       aggiungono oggetti (POI, Fase 2b) che calcolano la loro posizione
    ///       da LogicalPosition direttamente: gli asteroidi sarebbero in un
    ///       posto, i POI in un altro.
    ///
    ///   La formula chiusa risolve tutti e due i problemi:
    ///     - Una sola fonte di verità (LogicalPosition + LogicalRotation)
    ///     - Nessuna composizione ricorsiva → nessun drift accumulativo
    ///     - Nessuna normalizzazione periodica necessaria
    ///     - I POI useranno la STESSA formula → coerenza per costruzione
    ///
    /// SCELTA DI RETE: MonoBehaviour puro, NON NetworkBehaviour. Il movimento è
    /// simulazione client-side deterministica dallo stato GIÀ replicato di
    /// ShipMovement. Ogni client applica la stessa formula agli stessi valori
    /// replicati → tutti vedono la stessa posizione senza traffico di rete
    /// aggiuntivo. Costo di rete indipendente dal numero di oggetti esterni.
    ///
    /// USO TIPICO — DUE PERCORSI DI INIZIALIZZAZIONE:
    ///
    ///   PERCORSO A — SCENE-PLACED (default, tipico per starfield, decorativi):
    ///     1. Piazza il GameObject in Editor alla posizione worldspace
    ///        desiderata. La sua P_logical viene derivata automaticamente in
    ///        OnEnable dalla worldspace di Editor, assumendo lo stato di
    ///        riferimento della nave (LogicalPosition = zero, LogicalRotation
    ///        = identity — cioè lo stato SEMPRE presente al momento della
    ///        piazzatura in Editor).
    ///     2. Assegnare shipReference al Transform di "Nave" (opzionale —
    ///        default Vector3.zero, funziona finché "Nave" è all'origine).
    ///     3. Play → l'oggetto scorre e ruota in senso inverso rispetto allo
    ///        stato logico della nave.
    ///
    ///   PERCORSO B — SPAWN DINAMICO (POI/meteoriti in Fase 2b+):
    ///     Il chiamante (tipicamente un PoiInstance NetworkBehaviour) chiama
    ///     SetLogicalOverride(logicalPos, logicalRot) subito dopo lo spawn,
    ///     passando i valori dei propri NetworkVariable server-authoritative.
    ///     Questo bypassa la derivazione da Editor — necessario perché al
    ///     momento dello spawn la nave potrebbe già essere in movimento
    ///     nello spazio logico, e derivare P_logical da transform.position
    ///     darebbe risultati divergenti su client con timing di connessione
    ///     diverso.
    ///
    ///     Il PoiInstance è responsabile di:
    ///       1. Esporre NetworkVariable<Vector3> LogicalPosition (deciso dal
    ///          server allo spawn: es. ship.LogicalPosition + offset).
    ///       2. Esporre NetworkVariable<Quaternion> LogicalRotation (idem).
    ///       3. In OnNetworkSpawn (dopo che i NetVar sono sincronizzati),
    ///          chiamare externalWorldFollower.SetLogicalOverride(...) sul
    ///          proprio GameObject PoiVisual.
    ///
    ///     Da lì il rendering è identico al percorso A — stessa formula
    ///     chiusa, stessa coerenza per costruzione tra client.
    ///
    /// DIPENDE DA: ShipMovement (Instance + OnInstanceReady + LogicalRotation
    ///             + LogicalPosition)
    /// </summary>
    public class ExternalWorldFollower : MonoBehaviour
    {
        [Header("Configurazione pivot")]
        [Tooltip("Transform attorno a cui ruotare quando la nave gira. " +
                 "Tipicamente il GameObject 'Nave'. Se null, usa Vector3.zero " +
                 "(corretto finché 'Nave' è piazzata all'origine).")]
        [SerializeField] private Transform shipReference;

        [Header("Toggle di test (Blocco 3)")]
        [Tooltip("Se true, la posizione dell'oggetto risponde a LogicalPosition. " +
                 "Utile disattivare per testare solo la rotazione, o per " +
                 "congelare l'oggetto in una posizione fissa.")]
        [SerializeField] private bool applyTranslation = true;

        [Tooltip("Se true, la posizione e l'orientamento rispondono a " +
                 "LogicalRotation. Utile disattivare per testare solo la " +
                 "traslazione.")]
        [SerializeField] private bool applyRotation = true;

        [Header("Debug")]
        [Tooltip("Se true, stampa un log al primo bind con ShipMovement.Instance " +
                 "e mostra un OnGUI con posizione/stato correnti. Lasciare OFF " +
                 "in produzione.")]
        [SerializeField] private bool verboseLogging = false;

        // ── Stato interno ────────────────────────────────────────────────────
        // P_logical costante nello "spazio logico". Inizializzata via uno di
        // due percorsi (vedi header):
        //   A) InitializeFromScenePlacement() — deriva da worldspace di Editor
        //   B) SetLogicalOverride(pos, rot) — passata esplicitamente dal
        //      chiamante (tipicamente PoiInstance con NetVar)
        //
        // Perché il percorso A NON legge ship.LogicalPosition/Rotation:
        //   Se un client si connette in ritardo, ShipMovement.OnNetworkSpawn
        //   gli sincronizza i NetVar al valore CORRENTE del server (che
        //   potrebbe non essere più zero/identity). Se calcolassimo
        //   _pointLogicalPosition da quei valori, ogni client la calcolerebbe
        //   diversa in base a QUANDO si è connesso → traiettorie divergenti
        //   con offset proporzionale al ritardo di connessione.
        //
        //   Assumendo invece lo stato di riferimento (zero, identity) — che è
        //   SEMPRE lo stato al momento della piazzatura in Editor —
        //   _pointLogicalPosition è deterministica dalla scena, uguale su
        //   ogni client per costruzione, indipendente dal timing.
        //
        // Perché il percorso B esiste:
        //   Per oggetti istanziati a runtime la worldspace di Editor non
        //   esiste, e la worldspace corrente al momento dello spawn dipende
        //   dallo stato logico corrente della nave (cioè: quando il client
        //   si connette). Serve una fonte di verità replicata — che il
        //   NetworkBehaviour proprietario fornisce esplicitamente.
        private Vector3 _pointLogicalPosition;
        private Quaternion _pointLogicalRotation;

        private bool _initialized;

        // ── Lifecycle ────────────────────────────────────────────────────────

        private void OnEnable()
        {
            // Se qualcuno ha già chiamato SetLogicalOverride prima di questo
            // OnEnable (raro ma possibile con ordini di inizializzazione
            // custom), rispettiamo l'override — non sovrascriviamo.
            if (_initialized) return;

            // ShipMovement potrebbe non essere ancora spawnato (specialmente
            // ai primi frame della scena, o su un client che entra in una
            // sessione già in corso). Se non c'è, ci sottoscriviamo a
            // OnInstanceReady e inizializziamo quando arriva.
            if (ShipMovement.Instance != null)
            {
                InitializeFromScenePlacement();
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

            // Se nel frattempo qualcuno ha chiamato SetLogicalOverride,
            // rispettalo — non sovrascrivere con il percorso scene-placed.
            if (_initialized) return;

            InitializeFromScenePlacement();
        }

        /// <summary>
        /// PERCORSO A — inizializzazione per oggetti scene-placed.
        ///
        /// Deriva P_logical dalla posizione worldspace in Editor, assumendo
        /// lo stato di riferimento della nave (LogicalPosition = zero,
        /// LogicalRotation = identity) — cioè lo stato in cui la nave è
        /// SEMPRE al momento della piazzatura in Editor.
        ///
        /// Formula inversa semplificata sotto l'assunzione dello stato di
        /// riferimento:
        ///   P_logical = (P_visual - pivot)     [rotazione identity, offset zero]
        ///   R_logical = R_visual
        ///
        /// NON usare per oggetti istanziati a runtime — usare invece
        /// SetLogicalOverride passando i valori dei NetworkVariable
        /// server-authoritative.
        /// </summary>
        private void InitializeFromScenePlacement()
        {
            Vector3 pivot = shipReference != null ? shipReference.position : Vector3.zero;

            _pointLogicalPosition = transform.position - pivot;
            _pointLogicalRotation = transform.rotation;

            _initialized = true;

            if (verboseLogging)
            {
                Debug.Log($"[ExternalWorldFollower] {name}: bind SCENE-PLACED. " +
                          $"P_logical=({_pointLogicalPosition.x:F1}, {_pointLogicalPosition.y:F1}, {_pointLogicalPosition.z:F1}) · " +
                          $"pivot=({pivot.x:F1}, {pivot.y:F1}, {pivot.z:F1})");
            }
        }

        /// <summary>
        /// PERCORSO B — inizializzazione per oggetti istanziati a runtime
        /// con posizione logica già server-authoritative (POI, meteoriti,
        /// relitti in Fase 2b+).
        ///
        /// Il chiamante (tipicamente un NetworkBehaviour) DEVE aver ricevuto
        /// i valori tramite NetworkVariable prima di chiamare questo metodo,
        /// così che tutti i client ricevano gli STESSI valori — è la loro
        /// natura server-authoritative che garantisce la coerenza tra client
        /// indipendentemente dal timing di connessione (a differenza del
        /// percorso scene-placed, che deriva P_logical dalla worldspace di
        /// Editor).
        ///
        /// Chiamabile in qualunque momento — sovrascrive un'eventuale
        /// inizializzazione precedente e si disiscrive da OnInstanceReady
        /// se era in attesa. Da chiamare tipicamente in OnNetworkSpawn del
        /// NetworkBehaviour proprietario, DOPO che i suoi NetworkVariable
        /// sono sincronizzati.
        /// </summary>
        /// <param name="logicalPosition">Posizione dell'oggetto nello spazio
        /// logico (frame globale). Costante per la vita dell'oggetto.</param>
        /// <param name="logicalRotation">Orientamento dell'oggetto nello
        /// spazio logico. Costante per la vita dell'oggetto (per oggetti
        /// che non ruotano su sé stessi — un asteroide che ruota su sé
        /// stesso applicherà una rotazione locale AGGIUNTIVA sopra questa
        /// baseline nel proprio Update).</param>
        public void SetLogicalOverride(Vector3 logicalPosition, Quaternion logicalRotation)
        {
            // Cancella eventuale attesa del percorso scene-placed — non ci
            // serve più, abbiamo i valori diretti.
            ShipMovement.OnInstanceReady -= HandleInstanceReady;

            _pointLogicalPosition = logicalPosition;
            _pointLogicalRotation = logicalRotation;
            _initialized = true;

            if (verboseLogging)
            {
                Debug.Log($"[ExternalWorldFollower] {name}: bind DYNAMIC OVERRIDE. " +
                          $"P_logical=({_pointLogicalPosition.x:F1}, {_pointLogicalPosition.y:F1}, {_pointLogicalPosition.z:F1})");
            }
        }

        // ── Update ───────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_initialized) return;

            var ship = ShipMovement.Instance;
            if (ship == null) return; // difensivo: potrebbe essere despawnato

            // Stato logico corrente della nave (con fallback ai valori di
            // riferimento zero/identity quando i toggle di test disabilitano
            // una componente — così l'oggetto resta congelato alla sua
            // posizione worldspace di Editor invece di andare in un limbo).
            Vector3 shipLogicalPos = applyTranslation
                ? ship.LogicalPosition
                : Vector3.zero;

            Quaternion shipLogicalRot = applyRotation
                ? ship.LogicalRotation
                : Quaternion.identity;

            Quaternion shipLogicalRotInverse = Quaternion.Inverse(shipLogicalRot);

            Vector3 pivot = shipReference != null ? shipReference.position : Vector3.zero;

            // Formula chiusa: nessuna composizione ricorsiva, nessun drift.
            // Se lo stato logico non cambia (nave ferma), il risultato è
            // identico ogni frame → oggetto stabile senza calcoli di soglia.
            transform.position = pivot + shipLogicalRotInverse * (_pointLogicalPosition - shipLogicalPos);
            transform.rotation = shipLogicalRotInverse * _pointLogicalRotation;
        }

        // ── Debug GUI ────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!verboseLogging) return;
            if (!_initialized || ShipMovement.Instance == null) return;

            var ship = ShipMovement.Instance;
            Vector3 shipLogPos = ship.LogicalPosition;
            Vector3 euler = ship.LogicalRotation.eulerAngles;

            GUILayout.BeginArea(new Rect(360, Screen.height - 120, 360, 110));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[ExtWorldFollower] {name}");
            GUILayout.Label($"P_logical: ({_pointLogicalPosition.x:F0}, {_pointLogicalPosition.y:F0}, {_pointLogicalPosition.z:F0})");
            GUILayout.Label($"P_visual: ({transform.position.x:F1}, {transform.position.y:F1}, {transform.position.z:F1})");
            GUILayout.Label($"Ship LogPos: ({shipLogPos.x:F0}, {shipLogPos.y:F0}, {shipLogPos.z:F0})");
            GUILayout.Label($"Ship Rot: yaw {NormalizeAngleDisplay(euler.y):F1}° · pitch {NormalizeAngleDisplay(euler.x):F1}°");
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