using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// SutureTuning — parametri della sutura "segui l'ago" (Rev BO-c · Q4-a, valori Q14-a).
    ///
    /// Tutte le distanze sono in UNITÀ CANVAS (quelle del RectTransform). Sul monitor della
    /// MedicalStation, da seduti, 1 u ≈ 2,05 px a 1080p (il canvas 900×470 riempie ~90%
    /// dello schermo).
    ///
    /// La linea si adatta all'area del SutureLineGraphic: larghezza = area − 2×marginX,
    /// ampiezza massima = altezza/2 − marginY. Così non esce mai dalla mask.
    ///
    /// Criterio di taratura (Q14-a): ritmo (tempo per segmento ≈ 1,9 / 1,4 / 1,1 s) e
    /// finestra di reazione (raggio ÷ velocità ≈ 0,35 → 0,19 s) uguali ai valori ratificati
    /// sul canvas del letto; cambia solo la dimensione. La sensibilità del mouse è una
    /// mappatura mano → schermo e non dipende dalla geometria.
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
        [SerializeField, Min(2)] private int segments = 6;

        [Tooltip("Margine orizzontale (u) tra i bordi dell'area e gli estremi della linea.")]
        [SerializeField, Min(0f)] private float marginX = 40f;

        [Tooltip("Margine verticale (u): l'ampiezza massima è altezza/2 − marginY.")]
        [SerializeField, Min(0f)] private float marginY = 40f;

        [Tooltip("Ampiezza minima di ogni vertice, come frazione dell'ampiezza massima (Q1-a: 50–100%).")]
        [SerializeField, Range(0f, 1f)] private float amplitudeMinFraction = 0.5f;

        [Header("Ago (marker) — avanti e indietro")]
        [Tooltip("Velocità lungo la linea (u/s) per stadio: 0–50%, 50–75%, 75–100%. Lo stadio " +
                 "lo decide il minigame al superamento delle soglie.")]
        [SerializeField] private float[] markerSpeeds = { 100f, 140f, 180f };

        [Header("Reticolo (Q2-a · Look)")]
        [Tooltip("Mouse: unità canvas per pixel di delta.")]
        [SerializeField, Min(0f)] private float mouseSensitivity = 0.6f;

        [Tooltip("Stick: velocità del reticolo a piena corsa (u/s).")]
        [SerializeField, Min(0f)] private float stickSpeed = 410f;

        [Tooltip("Distanza reticolo–ago (u) sotto cui si entra \"sul bersaglio\". È anche il " +
                 "raggio dell'anello disegnato.")]
        [SerializeField, Min(1f)] private float enterRadius = 35f;

        [Tooltip("Distanza (u) oltre cui si esce dal bersaglio (isteresi, Q3-a). Mai minore " +
                 "di enterRadius.")]
        [SerializeField, Min(1f)] private float exitRadius = 43f;

        [Header("Punti (sempre via IMinigameHost.ApplyPoints)")]
        [Tooltip("Punti al secondo sul bersaglio. Il moltiplicatore di ruolo (Rev U) li scala.")]
        [SerializeField, Min(0f)] private float onTargetPointsPerSecond = 12f;

        [Tooltip("Punti persi al secondo fuori bersaglio (applicati come negativi: il ruolo non " +
                 "li attenua).")]
        [SerializeField, Min(0f)] private float offTargetLossPerSecond = 4f;

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
