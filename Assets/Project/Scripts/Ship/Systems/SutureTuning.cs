using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// SutureTuning — parametri della sutura "segui l'ago" (Rev BO-c · Q4-a, valori Q14-a;
    /// Rev BO-d: preset più duro Q17-b, anello ridotto per il non-Corpsman Q18-c, tolleranza
    /// per dispositivo Q19-a).
    ///
    /// Tutte le distanze sono in UNITÀ CANVAS (quelle del RectTransform). Sul monitor della
    /// MedicalStation, da seduti, 1 u ≈ 2,05 px a 1080p (il canvas 900×470 riempie ~90%
    /// dello schermo).
    ///
    /// La linea si adatta all'area del SutureLineGraphic: larghezza = area − 2×marginX,
    /// ampiezza massima = altezza/2 − marginY. Così non esce mai dalla mask.
    ///
    /// Taratura (Rev BO-d · Q17-b, dopo il playtest BO-c "troppo facile"): 8 segmenti più
    /// alti, ago 130/175/220 u/s → ≈ 1,55 / 1,15 / 0,92 s per segmento; raggio 28/34 →
    /// finestra di reazione ≈ 0,22 / 0,16 / 0,13 s (BO-c: 0,35 / 0,25 / 0,19). La sensibilità
    /// del mouse è una mappatura mano → schermo e non dipende dalla geometria.
    ///
    /// RUOLO (Rev U · Q18-c): per chi non è Corpsman l'anello è più piccolo (raggi di ingresso
    /// e uscita × nonCorpsmanRadiusMultiplier). Si somma ai moltiplicatori di MedbayConfig
    /// (punti e decadimento): quelli toccano la velocità di cura, questo la mano.
    ///
    /// DISPOSITIVO (Q19-a): il mouse controlla la POSIZIONE (preciso), lo stick la VELOCITÀ
    /// (seguire un bersaglio mobile è molto più difficile). Raggi × mouseRadiusMultiplier o
    /// × stickRadiusMultiplier secondo il dispositivo attivo (InputDeviceManager, lo stesso
    /// dei prompt). Raggio finale = base × ruolo × dispositivo.
    ///
    /// Letto AL MOMENTO DELL'USO da SutureInteraction: il tuning live in Inspector vale anche
    /// a sessione aperta (la linea cambia solo alla sessione successiva).
    /// </summary>
    [CreateAssetMenu(menuName = "SpaceSurvivor/Suture Tuning", fileName = "SutureTuning")]
    public class SutureTuning : ScriptableObject
    {
        [Header("Linea (generata a ogni sessione · Q1-a)")]
        [Tooltip("Segmenti dello zig-zag. Gli estremi stanno a metà altezza, i vertici interni " +
                 "si alternano sopra e sotto.")]
        [SerializeField, Min(2)] private int segments = 8;

        [Tooltip("Margine orizzontale (u) tra i bordi dell'area e gli estremi della linea.")]
        [SerializeField, Min(0f)] private float marginX = 40f;

        [Tooltip("Margine verticale (u): l'ampiezza massima è altezza/2 − marginY.")]
        [SerializeField, Min(0f)] private float marginY = 40f;

        [Tooltip("Ampiezza minima di ogni vertice, come frazione dell'ampiezza massima (BO-d: 70–100%).")]
        [SerializeField, Range(0f, 1f)] private float amplitudeMinFraction = 0.7f;

        [Header("Ago (marker) — avanti e indietro")]
        [Tooltip("Velocità lungo la linea (u/s) per stadio: 0–50%, 50–75%, 75–100%. Lo stadio " +
                 "lo decide il minigame al superamento delle soglie.")]
        [SerializeField] private float[] markerSpeeds = { 130f, 175f, 220f };

        [Header("Reticolo (Q2-a · Look)")]
        [Tooltip("Mouse: unità canvas per pixel di delta.")]
        [SerializeField, Min(0f)] private float mouseSensitivity = 0.6f;

        [Tooltip("Stick: velocità del reticolo a piena corsa (u/s). Tenerla ≈ 2,3 × l'ago più veloce.")]
        [SerializeField, Min(0f)] private float stickSpeed = 500f;

        [Tooltip("Distanza reticolo–ago (u) sotto cui si entra \"sul bersaglio\". È anche il " +
                 "raggio dell'anello disegnato (operatore Corpsman).")]
        [SerializeField, Min(1f)] private float enterRadius = 28f;

        [Tooltip("Distanza (u) oltre cui si esce dal bersaglio (isteresi, Q3-a). Mai minore " +
                 "di enterRadius.")]
        [SerializeField, Min(1f)] private float exitRadius = 34f;

        [Header("Ruolo (Rev U · Q18-c)")]
        [Tooltip("Operatore NON Corpsman: raggi di ingresso e uscita (e anello disegnato) × questo " +
                 "valore. 1 = nessuna differenza. Si somma ai moltiplicatori di MedbayConfig.")]
        [SerializeField, Range(0.3f, 1f)] private float nonCorpsmanRadiusMultiplier = 0.75f;

        [Header("Dispositivo (Rev BO-d · Q19-a)")]
        [Tooltip("Mouse (controllo di posizione, preciso): raggi × questo valore.")]
        [SerializeField, Range(0.3f, 2f)] private float mouseRadiusMultiplier = 0.8f;

        [Tooltip("Stick (controllo di velocità, più difficile): raggi × questo valore.")]
        [SerializeField, Range(0.3f, 2f)] private float stickRadiusMultiplier = 1.2f;

        [Header("Punti (sempre via IMinigameHost.ApplyPoints)")]
        [Tooltip("Punti al secondo sul bersaglio. Il moltiplicatore di ruolo (Rev U) li scala.")]
        [SerializeField, Min(0f)] private float onTargetPointsPerSecond = 10f;

        [Tooltip("Punti persi al secondo fuori bersaglio (applicati come negativi: il ruolo non " +
                 "li attenua).")]
        [SerializeField, Min(0f)] private float offTargetLossPerSecond = 5f;

        [Header("Feedback")]
        [Tooltip("Durata (s) di \"ON THE LINE\" / \"OFF THE LINE\" prima che torni il prompt.")]
        [SerializeField, Min(0f)] private float feedbackSeconds = 1.2f;

        // ── Accesso (clamp difensivi) ────────────────────────────────────────

        public int Segments => Mathf.Max(2, segments);
        public float MarginX => Mathf.Max(0f, marginX);
        public float MarginY => Mathf.Max(0f, marginY);
        public float AmplitudeMinFraction => Mathf.Clamp01(amplitudeMinFraction);

        public float MouseSensitivity => Mathf.Max(0f, mouseSensitivity);
        public float StickSpeed => Mathf.Max(0f, stickSpeed);
        public float EnterRadius => Mathf.Max(1f, enterRadius);
        public float ExitRadius => Mathf.Max(EnterRadius, exitRadius);
        public float NonCorpsmanRadiusMultiplier => Mathf.Clamp(nonCorpsmanRadiusMultiplier, 0.3f, 1f);
        public float MouseRadiusMultiplier => Mathf.Clamp(mouseRadiusMultiplier, 0.3f, 2f);
        public float StickRadiusMultiplier => Mathf.Clamp(stickRadiusMultiplier, 0.3f, 2f);

        public float OnTargetPointsPerSecond => Mathf.Max(0f, onTargetPointsPerSecond);
        public float OffTargetLossPerSecond => Mathf.Max(0f, offTargetLossPerSecond);
        public float FeedbackSeconds => Mathf.Max(0f, feedbackSeconds);

        /// <summary>Numero di stadi di velocità configurati (almeno 1).</summary>
        public int SpeedStageCount => markerSpeeds != null && markerSpeeds.Length > 0 ? markerSpeeds.Length : 1;

        /// <summary>Velocità dell'ago per lo stadio indicato (clamp all'ultimo stadio disponibile).</summary>
        public float MarkerSpeed(int stage)
        {
            if (markerSpeeds == null || markerSpeeds.Length == 0) return 100f;
            int i = Mathf.Clamp(stage, 0, markerSpeeds.Length - 1);
            return Mathf.Max(0f, markerSpeeds[i]);
        }
    }
}