using System.Collections.Generic;
using SpaceSurvivor.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Sorgente dei riferimenti della sutura. Implementata ESPLICITAMENTE da
    /// StabilizationMinigameMedical (stesso schema di IMashSliderSettings, Rev BN).
    /// Letta AL MOMENTO DELL'USO: l'azione Look cambia a ogni sessione (arriva dal
    /// PlayerInput del giocatore seduto alla console).
    /// </summary>
    public interface ISutureSettings
    {
        SutureTuning Tuning { get; }
        SutureLineGraphic Graphic { get; }
        InputAction LookAction { get; }
        /// <summary>Colore delle tacche 50/75 non ancora raggiunte (colorMarkerDefault della base).</summary>
        Color NotchDefaultColor { get; }
        /// <summary>
        /// Rev BO-d · Q18-c — true se l'operatore è Corpsman (fissato dal minigame all'apertura
        /// della sessione). Se false l'anello si riduce (SutureTuning.NonCorpsmanRadiusMultiplier).
        /// </summary>
        bool OperatorIsCorpsman { get; }
    }

    /// <summary>
    /// SutureInteraction — "segui l'ago" (Rev BO-c · archetipo 4, tracking continuo).
    /// Primo uso reale del seam QB: creata da StabilizationMinigameMedical.CreateInteraction().
    ///
    ///   - LINEA (Q1-a): zig-zag generato a ogni sessione dentro l'area del
    ///     SutureLineGraphic (margini e ampiezza da SutureTuning). Estremi a metà altezza.
    ///   - AGO: scorre avanti e indietro lungo la linea (ping-pong continuo, niente salti),
    ///     alla velocità dello stadio corrente. Lo stadio lo decide il minigame
    ///     (SetSpeedStage) al superamento delle soglie 50/75.
    ///   - RETICOLO (Q2-a, Q6-a): azione Look del giocatore locale, solo ReadValue nel Tick
    ///     (nessun handler, nessun Enable/Disable dell'azione condivisa → protocollo
    ///     AF/AG intatto). Mouse = delta × sensibilità; stick = valore × velocità × dt;
    ///     dispositivo da activeControl.device (come PilotStation). Confinato all'area.
    ///   - PUNTI: +onTarget/s sul bersaglio, −offTarget/s fuori, SOLO via
    ///     IMinigameHost.ApplyPoints (×ruolo sui soli guadagni, clamp, soglie).
    ///   - FEEDBACK (Q3-a): isteresi enter/exit; testo solo ai cambi di stato.
    ///   - RUOLO (Rev BO-d · Q18-c): operatore non Corpsman → raggi di ingresso/uscita e
    ///     anello × NonCorpsmanRadiusMultiplier (la "mano" meno esperta ha meno tolleranza).
    ///   - DISPOSITIVO (Rev BO-d · Q19-a): raggi × moltiplicatore mouse o stick. Il dispositivo
    ///     per la TOLLERANZA viene da InputDeviceManager (appiccicoso, lo stesso dei prompt:
    ///     l'anello non sfarfalla quando il mouse sta fermo). Il MOVIMENTO usa invece il
    ///     controllo del frame (delta del mouse o velocità dello stick).
    ///   - Q5-a: a inizio sessione ago e reticolo sono sovrapposti all'inizio della linea e
    ///     restano fermi durante il grace (Tick non gira).
    ///
    /// CONTRATTO QB: Begin (reset + nuova linea, nessun input) → Enable (fine grace) →
    /// Tick (post-grace) → Disable (idempotente, anche RIENTRANTE dentro ApplyPoints
    /// quando la soglia 100% chiude la sessione: il Tick esce subito dopo).
    /// </summary>
    public sealed class SutureInteraction : IMinigameInteraction
    {
        private const string OnLineText = "ON THE LINE";
        private const string OffLineText = "OFF THE LINE";

        // Area di ripiego se il graphic manca (dimensioni del SuturePanel, Q14-a).
        private static readonly Rect FallbackArea = new Rect(-340f, -150f, 680f, 300f);

        private readonly ISutureSettings _settings;
        private IMinigameHost _host;
        private SutureTuning _fallbackTuning;
        private bool _warnedGraphic;
        private bool _warnedLook;

        private readonly List<Vector2> _points = new List<Vector2>();
        private readonly List<float> _cumulative = new List<float>();
        private float _length;
        private Rect _area;

        private float _travel;           // distanza percorsa dall'ago (ping-pong su _length)
        private Vector2 _markerPos;
        private Vector2 _reticlePos;

        private bool _enabled;
        private bool _onTarget;
        private int _stage;
        private bool _reached50;
        private bool _reached75;

        private float _lastDistance;
        private bool _lastFromMouse;

        public SutureInteraction(ISutureSettings settings)
        {
            _settings = settings;
        }

        // ── API per il minigame (riferimento tipizzato, IMinigameInteraction non la vede) ──

        /// <summary>Stadio di velocità dell'ago (0 = 0–50%, 1 = 50–75%, 2 = 75–100%). Solo un campo: sicuro in rientranza.</summary>
        public void SetSpeedStage(int stage)
        {
            _stage = Mathf.Max(0, stage);
        }

        /// <summary>Soglia superata: la tacca corrispondente diventa "raggiunta" (one-shot).</summary>
        public void MarkThresholdReached(float pct)
        {
            if (Mathf.Approximately(pct, 50f)) _reached50 = true;
            else if (Mathf.Approximately(pct, 75f)) _reached75 = true;
            else return;

            SutureLineGraphic g = _settings.Graphic;
            if (g != null) g.SetNotchesDone(_reached50, _reached75);
        }

        /// <summary>Q13-b — progresso corrente (0–1): la ferita è cucita fino a qui.</summary>
        public void SetProgress(float normalized)
        {
            SutureLineGraphic g = _settings.Graphic;
            if (g != null) g.SetSewnFraction(normalized);
        }

        // ── IMinigameInteraction ─────────────────────────────────────────────

        public string DebugLine =>
            $"Sutura: stadio {_stage + 1} · ago {(_length > 0f ? Mathf.PingPong(_travel, _length) / _length : 0f):F2}" +
            $" · dist {_lastDistance:F0}u / r {EnterRadius(Tuning):F0}u · {(_onTarget ? "ON" : "OFF")}" +
            $" · {(UsingGamepad() ? "pad" : "mouse")}";

        public void Begin(IMinigameHost host)
        {
            _host = host;
            _enabled = false;
            _stage = 0;
            _reached50 = false;
            _reached75 = false;
            _travel = 0f;
            _lastDistance = 0f;
            // Ripiego del dispositivo se InputDeviceManager manca: si parte dal mouse.
            _lastFromMouse = InputDeviceManager.Instance == null || !InputDeviceManager.Instance.IsGamepad;

            GenerateLine(Tuning);

            // Q5-a — ago e reticolo sovrapposti all'inizio della linea: si parte sul bersaglio.
            _markerPos = _points.Count > 0 ? _points[0] : _area.center;
            _reticlePos = _markerPos;
            _onTarget = true;

            SutureLineGraphic g = _settings.Graphic;
            if (g == null)
            {
                if (!_warnedGraphic)
                {
                    Debug.LogWarning("[SutureInteraction] SutureLineGraphic non assegnato: la sutura " +
                                     "funziona ma non si vede.");
                    _warnedGraphic = true;
                }
                return;
            }

            MinigamePalette palette = _host.Palette;
            g.SetLine(_points);
            g.SetPalette(palette.Critical, palette.Good, _settings.NotchDefaultColor, palette.Good, palette.Neutral);
            g.SetNotchesDone(false, false);
            g.SetSewnFraction(0f);
            PushDynamic(g, palette);
        }

        public void Enable()
        {
            _enabled = true;

            if (_settings.LookAction == null && !_warnedLook)
            {
                Debug.LogWarning("[SutureInteraction] Azione Look assente: il reticolo resta fermo.");
                _warnedLook = true;
            }
        }

        public void Tick(float deltaTime)
        {
            if (!_enabled || _host == null || !_host.IsActive) return;

            SutureTuning t = Tuning;

            // 1. Ago.
            if (_length > 0f)
            {
                _travel += t.MarkerSpeed(_stage) * deltaTime;
                _markerPos = PointAt(Mathf.PingPong(_travel, _length));
            }

            // 2. Reticolo.
            MoveReticle(t, deltaTime);

            // 3. Bersaglio con isteresi.
            float distance = Vector2.Distance(_reticlePos, _markerPos);
            _lastDistance = distance;
            bool wasOnTarget = _onTarget;
            if (_onTarget) { if (distance > ExitRadius(t)) _onTarget = false; }
            else if (distance <= EnterRadius(t)) _onTarget = true;

            // 4. Punti — unico canale verso il progresso. Testo solo ai cambi di stato.
            MinigamePalette palette = _host.Palette;
            float rate = _onTarget ? t.OnTargetPointsPerSecond : -t.OffTargetLossPerSecond;
            string feedback = null;
            Color feedbackColor = palette.Neutral;
            if (_onTarget != wasOnTarget)
            {
                feedback = _onTarget ? OnLineText : OffLineText;
                feedbackColor = _onTarget ? palette.Good : palette.Critical;
            }

            _host.ApplyPoints(rate * deltaTime, feedback, feedbackColor, t.FeedbackSeconds);

            // Rientranza: al 100% la base chiude la sessione DENTRO ApplyPoints e chiama
            // Disable(). Qui non si tocca più nulla (UI già spenta).
            if (!_enabled) return;

            // 5. Vista.
            SutureLineGraphic g = _settings.Graphic;
            if (g != null) PushDynamic(g, palette);
        }

        public void Disable()
        {
            // Nessun handler e nessuna routine da sganciare: solo lo stop del Tick.
            // Idempotente, sicuro anche se Enable non è mai stato chiamato.
            _enabled = false;
        }

        // ── Interni ──────────────────────────────────────────────────────────

        private SutureTuning Tuning
        {
            get
            {
                SutureTuning t = _settings.Tuning;
                if (t != null) return t;

                if (_fallbackTuning == null)
                {
                    Debug.LogWarning("[SutureInteraction] SutureTuning non assegnato: uso i valori di default.");
                    _fallbackTuning = ScriptableObject.CreateInstance<SutureTuning>();
                }
                return _fallbackTuning;
            }
        }

        /// <summary>Q18-c — moltiplicatore di ruolo sui raggi (1 per il Corpsman).</summary>
        private float RoleRadiusMultiplier(SutureTuning t) =>
            _settings.OperatorIsCorpsman ? 1f : t.NonCorpsmanRadiusMultiplier;

        /// <summary>
        /// Q19-a — dispositivo per la tolleranza. Fonte: InputDeviceManager (appiccicoso).
        /// Ripiego: ultimo dispositivo che ha dato input non nullo alla sutura.
        /// </summary>
        private bool UsingGamepad()
        {
            InputDeviceManager idm = InputDeviceManager.Instance;
            return idm != null ? idm.IsGamepad : !_lastFromMouse;
        }

        private float DeviceRadiusMultiplier(SutureTuning t) =>
            UsingGamepad() ? t.StickRadiusMultiplier : t.MouseRadiusMultiplier;

        /// <summary>Raggio finale = base × ruolo × dispositivo.</summary>
        private float EnterRadius(SutureTuning t) =>
            t.EnterRadius * RoleRadiusMultiplier(t) * DeviceRadiusMultiplier(t);

        private float ExitRadius(SutureTuning t) =>
            t.ExitRadius * RoleRadiusMultiplier(t) * DeviceRadiusMultiplier(t);

        private void GenerateLine(SutureTuning t)
        {
            SutureLineGraphic g = _settings.Graphic;
            _area = g != null ? g.rectTransform.rect : FallbackArea;
            if (_area.width <= 0f || _area.height <= 0f) _area = FallbackArea;

            float left = _area.xMin + t.MarginX;
            float right = _area.xMax - t.MarginX;
            if (right <= left)
            {
                left = _area.xMin;
                right = _area.xMax;
            }

            float amplitude = Mathf.Max(0f, _area.height * 0.5f - t.MarginY);
            float centerY = _area.center.y;
            int segments = t.Segments;
            float sign = Random.value < 0.5f ? 1f : -1f;   // primo picco sopra o sotto

            _points.Clear();
            _cumulative.Clear();
            _length = 0f;

            for (int i = 0; i <= segments; i++)
            {
                float x = Mathf.Lerp(left, right, i / (float)segments);
                float y = centerY;
                if (i > 0 && i < segments)
                {
                    float a = amplitude * Random.Range(t.AmplitudeMinFraction, 1f);
                    y = centerY + sign * a;
                    sign = -sign;
                }

                Vector2 p = new Vector2(x, y);
                if (i > 0) _length += Vector2.Distance(_points[i - 1], p);
                _points.Add(p);
                _cumulative.Add(_length);
            }
        }

        private Vector2 PointAt(float s)
        {
            if (_points.Count == 0) return _area.center;
            if (s <= 0f) return _points[0];

            for (int i = 1; i < _points.Count; i++)
            {
                float s1 = _cumulative[i];
                if (s > s1) continue;

                float s0 = _cumulative[i - 1];
                float len = s1 - s0;
                return len > 0f
                    ? Vector2.Lerp(_points[i - 1], _points[i], (s - s0) / len)
                    : _points[i];
            }
            return _points[_points.Count - 1];
        }

        private void MoveReticle(SutureTuning t, float deltaTime)
        {
            InputAction look = _settings.LookAction;
            if (look == null) return;

            Vector2 value = look.ReadValue<Vector2>();
            bool fromMouse = look.activeControl?.device is Mouse;
            // Appiccicoso: si aggiorna solo con input reale (a mouse fermo activeControl è null).
            if (value.sqrMagnitude > 0f) _lastFromMouse = fromMouse;

            // Mouse: delta in pixel del frame. Stick: velocità (−1..1) × u/s × dt.
            Vector2 delta = fromMouse
                ? value * t.MouseSensitivity
                : value * (t.StickSpeed * deltaTime);

            _reticlePos += delta;
            _reticlePos.x = Mathf.Clamp(_reticlePos.x, _area.xMin, _area.xMax);
            _reticlePos.y = Mathf.Clamp(_reticlePos.y, _area.yMin, _area.yMax);
        }

        private void PushDynamic(SutureLineGraphic g, MinigamePalette palette)
        {
            g.SetMarker(_markerPos);
            g.SetReticle(_reticlePos, EnterRadius(Tuning), _onTarget ? palette.Good : palette.Critical);
        }
    }
}