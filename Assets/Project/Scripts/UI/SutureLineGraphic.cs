using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SpaceSurvivor.UI
{
    /// <summary>
    /// SutureLineGraphic — disegno della sutura sul monitor medico (Rev BO-c · Q15-a).
    ///
    /// Una sola mesh (colori per vertice), MaskableGraphic → rispetta la mask stencil del
    /// SuturePanel (Image + Mask, Show Mask Graphic off, come DockingMinigame_Canvas).
    /// Disegna, dal basso verso l'alto:
    ///   1. ferita aperta (tratto spesso) dal punto cucito fino alla fine della linea;
    ///   2. tratto cucito (sottile + punti trasversali) dall'inizio fino a SewnFraction
    ///      (Q13-b: il progresso è la ferita che si chiude, niente barra);
    ///   3. tacche 50/75 sulla linea: colore "non raggiunta" finché la soglia non è
    ///      superata, poi "raggiunta" (one-shot, come i marker della base);
    ///   4. ago (disco pieno);
    ///   5. anello del reticolo (raggio = raggio di ingresso sul bersaglio).
    ///
    /// È una VISTA pura: nessuna logica di gioco. Lo stato lo scrive SutureInteraction; i
    /// colori arrivano dalla palette del minigame. Le coordinate sono locali al proprio
    /// RectTransform (stesso spazio in cui l'interazione genera la linea).
    /// Lo stile (spessori, dimensioni) è serializzato qui; i valori di gioco stanno in
    /// SutureTuning.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class SutureLineGraphic : MaskableGraphic
    {
        [Header("Ferita")]
        [Tooltip("Spessore (u) del tratto ancora aperto.")]
        [SerializeField, Min(0.5f)] private float openThickness = 10f;

        [Tooltip("Spessore (u) del tratto cucito e dei punti trasversali.")]
        [SerializeField, Min(0.5f)] private float sewnThickness = 4f;

        [Tooltip("Distanza (u) tra due punti di sutura lungo il tratto cucito.")]
        [SerializeField, Min(4f)] private float stitchSpacing = 20f;

        [Tooltip("Lunghezza (u) di ogni punto di sutura (trasversale alla linea).")]
        [SerializeField, Min(0f)] private float stitchLength = 14f;

        [Header("Tacche 50 / 75")]
        [SerializeField, Min(0f)] private float notchLength = 36f;
        [SerializeField, Min(0.5f)] private float notchThickness = 4f;

        [Header("Ago e reticolo")]
        [SerializeField, Min(1f)] private float markerRadius = 8f;
        [SerializeField, Min(0.5f)] private float reticleThickness = 3f;

        private const int CircleSegments = 32;
        private const int JointSegments = 10;

        // ── Stato (scritto dall'interazione) ─────────────────────────────────

        private readonly List<Vector2> _points = new List<Vector2>();
        private readonly List<float> _cumulative = new List<float>();
        private float _length;

        private float _sewnFraction;
        private bool _notch50Done;
        private bool _notch75Done;

        private bool _hasMarker;
        private Vector2 _marker;

        private bool _hasReticle;
        private Vector2 _reticle;
        private float _reticleRadius;
        private Color _reticleColor = Color.white;

        private Color _openColor = new Color(1f, 0.2f, 0f);
        private Color _sewnColor = new Color(0.2f, 1f, 0.4f);
        private Color _notchDefaultColor = new Color(1f, 1f, 1f, 0.45f);
        private Color _notchDoneColor = new Color(0.2f, 1f, 0.4f);
        private Color _markerColor = new Color(0.8f, 0.8f, 0.8f);

        // ── Ciclo di vita ────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            // Solo disegno: nessun raycast sul canvas medico.
            raycastTarget = false;
        }

#if UNITY_EDITOR
        protected override void Reset()
        {
            base.Reset();
            raycastTarget = false;
        }
#endif

        // ── API (SutureInteraction) ──────────────────────────────────────────

        /// <summary>Nuova linea (coordinate locali). Azzera cucito e tacche.</summary>
        public void SetLine(IReadOnlyList<Vector2> points)
        {
            _points.Clear();
            _cumulative.Clear();
            _length = 0f;

            if (points != null)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    if (i > 0) _length += Vector2.Distance(points[i - 1], points[i]);
                    _points.Add(points[i]);
                    _cumulative.Add(_length);
                }
            }

            _sewnFraction = 0f;
            _notch50Done = false;
            _notch75Done = false;
            SetVerticesDirty();
        }

        public void SetPalette(Color open, Color sewn, Color notchDefault, Color notchDone, Color marker)
        {
            _openColor = open;
            _sewnColor = sewn;
            _notchDefaultColor = notchDefault;
            _notchDoneColor = notchDone;
            _markerColor = marker;
            SetVerticesDirty();
        }

        /// <summary>Frazione cucita della linea (0–1), dall'inizio. Ridisegna solo se cambia.</summary>
        public void SetSewnFraction(float fraction)
        {
            fraction = Mathf.Clamp01(fraction);
            if (Mathf.Abs(fraction - _sewnFraction) < 0.0005f) return;
            _sewnFraction = fraction;
            SetVerticesDirty();
        }

        public void SetNotchesDone(bool reached50, bool reached75)
        {
            if (reached50 == _notch50Done && reached75 == _notch75Done) return;
            _notch50Done = reached50;
            _notch75Done = reached75;
            SetVerticesDirty();
        }

        public void SetMarker(Vector2 position)
        {
            if (_hasMarker && position == _marker) return;
            _hasMarker = true;
            _marker = position;
            SetVerticesDirty();
        }

        public void SetReticle(Vector2 position, float radius, Color ringColor)
        {
            if (_hasReticle && position == _reticle && Mathf.Approximately(radius, _reticleRadius)
                && ringColor == _reticleColor) return;
            _hasReticle = true;
            _reticle = position;
            _reticleRadius = radius;
            _reticleColor = ringColor;
            SetVerticesDirty();
        }

        // ── Mesh ─────────────────────────────────────────────────────────────

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            if (_points.Count >= 2 && _length > 0f)
            {
                float sewnLength = _sewnFraction * _length;

                // 1. Ferita aperta: da sewnLength alla fine.
                AddPolylineRange(vh, sewnLength, _length, openThickness, Tint(_openColor));

                // 2. Tratto cucito + punti di sutura.
                if (sewnLength > 0f)
                {
                    Color32 sewn = Tint(_sewnColor);
                    AddPolylineRange(vh, 0f, sewnLength, sewnThickness, sewn);

                    float half = stitchLength * 0.5f;
                    if (half > 0f)
                    {
                        for (float s = stitchSpacing * 0.5f; s <= sewnLength; s += stitchSpacing)
                        {
                            SampleAt(s, out Vector2 p, out Vector2 dir);
                            Vector2 n = new Vector2(-dir.y, dir.x);
                            AddSegment(vh, p - n * half, p + n * half, sewnThickness, sewn);
                        }
                    }
                }

                // 3. Tacche 50 / 75.
                AddNotch(vh, 0.50f, _notch50Done);
                AddNotch(vh, 0.75f, _notch75Done);
            }

            // 4. Ago.
            if (_hasMarker)
                AddDisc(vh, _marker, markerRadius, CircleSegments, Tint(_markerColor));

            // 5. Reticolo.
            if (_hasReticle && _reticleRadius > 0f)
                AddRing(vh, _reticle, _reticleRadius, reticleThickness, Tint(_reticleColor));
        }

        private void AddNotch(VertexHelper vh, float fraction, bool reached)
        {
            SampleAt(fraction * _length, out Vector2 p, out Vector2 dir);
            Vector2 n = new Vector2(-dir.y, dir.x);
            float half = notchLength * 0.5f;
            AddSegment(vh, p - n * half, p + n * half, notchThickness,
                       Tint(reached ? _notchDoneColor : _notchDefaultColor));
        }

        /// <summary>Tratto della polilinea tra due distanze d'arco, con giunti arrotondati.</summary>
        private void AddPolylineRange(VertexHelper vh, float from, float to, float thickness, Color32 c)
        {
            if (to - from <= 0.001f) return;

            for (int i = 1; i < _points.Count; i++)
            {
                float s0 = _cumulative[i - 1];
                float s1 = _cumulative[i];
                if (s1 <= from || s0 >= to || s1 - s0 <= 0f) continue;

                float a = Mathf.Max(s0, from);
                float b = Mathf.Min(s1, to);
                Vector2 p0 = Vector2.Lerp(_points[i - 1], _points[i], (a - s0) / (s1 - s0));
                Vector2 p1 = Vector2.Lerp(_points[i - 1], _points[i], (b - s0) / (s1 - s0));
                AddSegment(vh, p0, p1, thickness, c);

                // Giunto sul vertice finale del segmento, se interno al tratto.
                if (i < _points.Count - 1 && s1 > from && s1 < to)
                    AddDisc(vh, _points[i], thickness * 0.5f, JointSegments, c);
            }
        }

        /// <summary>Posizione e direzione (unitaria) alla distanza d'arco s.</summary>
        private void SampleAt(float s, out Vector2 position, out Vector2 direction)
        {
            s = Mathf.Clamp(s, 0f, _length);
            for (int i = 1; i < _points.Count; i++)
            {
                float s0 = _cumulative[i - 1];
                float s1 = _cumulative[i];
                if (s > s1 && i < _points.Count - 1) continue;

                Vector2 seg = _points[i] - _points[i - 1];
                float len = s1 - s0;
                direction = len > 0f ? seg / len : Vector2.right;
                position = len > 0f ? _points[i - 1] + seg * ((s - s0) / len) : _points[i - 1];
                return;
            }
            position = _points[0];
            direction = Vector2.right;
        }

        private Color32 Tint(Color c) => c * color;

        private static void AddSegment(VertexHelper vh, Vector2 p0, Vector2 p1, float thickness, Color32 c)
        {
            Vector2 d = p1 - p0;
            float len = d.magnitude;
            if (len <= 0.0001f) return;

            Vector2 n = new Vector2(-d.y, d.x) / len * (thickness * 0.5f);
            int start = vh.currentVertCount;
            vh.AddVert(p0 - n, c, Vector2.zero);
            vh.AddVert(p0 + n, c, Vector2.zero);
            vh.AddVert(p1 + n, c, Vector2.zero);
            vh.AddVert(p1 - n, c, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start + 2, start + 3, start);
        }

        private static void AddDisc(VertexHelper vh, Vector2 center, float radius, int segments, Color32 c)
        {
            if (radius <= 0f) return;

            int centerIndex = vh.currentVertCount;
            vh.AddVert(center, c, Vector2.zero);
            for (int i = 0; i <= segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                vh.AddVert(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, c, Vector2.zero);
            }
            for (int i = 0; i < segments; i++)
                vh.AddTriangle(centerIndex, centerIndex + 1 + i, centerIndex + 2 + i);
        }

        private static void AddRing(VertexHelper vh, Vector2 center, float radius, float thickness, Color32 c)
        {
            float inner = Mathf.Max(0f, radius - thickness * 0.5f);
            float outer = radius + thickness * 0.5f;

            int start = vh.currentVertCount;
            for (int i = 0; i <= CircleSegments; i++)
            {
                float a = i * Mathf.PI * 2f / CircleSegments;
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                vh.AddVert(center + dir * inner, c, Vector2.zero);
                vh.AddVert(center + dir * outer, c, Vector2.zero);
            }
            for (int i = 0; i < CircleSegments; i++)
            {
                int k = start + i * 2;
                vh.AddTriangle(k, k + 1, k + 3);
                vh.AddTriangle(k + 3, k + 2, k);
            }
        }
    }
}
