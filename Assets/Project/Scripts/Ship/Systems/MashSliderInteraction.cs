using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Sorgente dei parametri dell'interazione Mash + Slider. Implementata
    /// ESPLICITAMENTE da ProgressiveMinigame sui propri campi serializzati
    /// (Rev BN · Q1-c): i campi restano dove sono → zero migrazione di scena.
    /// I valori sono letti AL MOMENTO DELL'USO (tuning live in Inspector invariato).
    /// </summary>
    public interface IMashSliderSettings
    {
        InputActionReference MashAction { get; }
        InputActionReference[] SliderKeys { get; }
        GameObject SliderPanel { get; }
        RectTransform SliderIndicator { get; }
        TextMeshProUGUI SliderKeyLabel { get; }
        Image HitZoneImage { get; }
        float MashPointsPerPress { get; }
        float HitZoneFraction { get; }
        float NearZoneFraction { get; }
        float SliderSpeed { get; }
        float SliderMinInterval { get; }
        float SliderMaxInterval { get; }
    }

    /// <summary>
    /// MashSliderInteraction — interazione di DEFAULT dei ProgressiveMinigame
    /// (GDD §9.8 · estratta dalla base in Rev BN / QB, comportamento invariato).
    ///
    ///   - MASH: ogni pressione di mashAction → +mashPointsPerPress (bloccato mentre
    ///     lo slider è attivo).
    ///   - SLIDER BURST: ogni [min,max] s compare un indicatore che scorre 0→1; il
    ///     tasto mostrato va premuto al centro: CENTRATO +15 / QUASI +5 / MANCATO −20.
    ///     Se l'indicatore esce (>1) è un MANCATO.
    ///
    /// Codice trasposto 1:1 da ProgressiveMinigame pre-BN: stesso ordine delle
    /// chiamate, stesse chiamate RNG, stesse stringhe. Unica differenza di forma:
    /// punti e feedback passano da IMinigameHost.ApplyPoints (che esegue in blocco
    /// ×ruolo → clamp → status → soglie, lo stesso ordine del codice originale).
    /// La coroutine slider gira sul MonoBehaviour della base (Q2.1-a).
    /// </summary>
    public sealed class MashSliderInteraction : IMinigameInteraction
    {
        // Esiti slider — invariati da M2. Debito QC/D20: parametri via ScriptableObject.
        private const float SliderHitPoints = 15f;
        private const float SliderNearPoints = 5f;
        private const float SliderMissPoints = -20f;
        private const float MashFeedbackSeconds = 0.3f;
        private const float SliderFeedbackSeconds = 1.5f;

        private readonly IMashSliderSettings _settings;
        private IMinigameHost _host;

        private bool _sliderActive;
        private float _sliderIndicatorPos;   // 0–1
        private int _activeSliderIndex = -1;
        private Coroutine _sliderRoutine;

        public MashSliderInteraction(IMashSliderSettings settings)
        {
            _settings = settings;
        }

        // ── IMinigameInteraction ──────────────────────────────────────────────

        public string DebugLine =>
            $"Slider: {(_sliderActive ? $"ATTIVO [{_sliderIndicatorPos:F2}]" : "in attesa")}";

        public void Begin(IMinigameHost host)
        {
            _host = host;

            _sliderActive = false;
            _activeSliderIndex = -1;
            _sliderIndicatorPos = 0f;
            _sliderRoutine = null;   // Rev BL: la routine parte a fine grace (Enable), non qui.

            if (_settings.SliderPanel != null) _settings.SliderPanel.SetActive(false);
        }

        public void Enable()
        {
            // Rev BL — Q1-b: unico punto di abilitazione input (flip fine grace).
            // Ordine pre-BN preservato: prima il mash, poi la routine slider.
            EnableMashInput();
            _sliderRoutine = _host.StartHostedRoutine(SliderRoutine());
        }

        public void Tick(float deltaTime)
        {
            if (!_sliderActive) return;

            _sliderIndicatorPos += _settings.SliderSpeed * deltaTime;
            if (_sliderIndicatorPos > 1f)
                ResolveSlider(false, false);
            else
                UpdateSliderIndicatorPosition();
        }

        public void Disable()
        {
            DisableMashInput();
            DisableSliderInput();

            if (_sliderRoutine != null) _host?.StopHostedRoutine(_sliderRoutine);

            if (_settings.SliderPanel != null) _settings.SliderPanel.SetActive(false);

            _sliderActive = false;
            _activeSliderIndex = -1;
        }

        // ── Input ─────────────────────────────────────────────────────────────

        private void EnableMashInput()
        {
            var mashAction = _settings.MashAction;
            if (mashAction?.action == null) return;
            mashAction.action.Enable();
            mashAction.action.performed += OnMashPerformed;
        }

        private void DisableMashInput()
        {
            var mashAction = _settings.MashAction;
            if (mashAction?.action == null) return;
            mashAction.action.performed -= OnMashPerformed;
        }

        private void EnableSliderInput(int keyIndex)
        {
            var keys = _settings.SliderKeys;
            if (keys == null || keyIndex < 0
                || keyIndex >= keys.Length) return;

            var action = keys[keyIndex]?.action;
            if (action == null) return;
            action.Enable();
            action.performed += OnSliderKeyPerformed;
        }

        private void DisableSliderInput()
        {
            var keys = _settings.SliderKeys;
            if (keys == null || _activeSliderIndex < 0) return;
            var action = keys[_activeSliderIndex]?.action;
            if (action == null) return;
            action.performed -= OnSliderKeyPerformed;
        }

        private void OnMashPerformed(InputAction.CallbackContext ctx)
        {
            if (!_host.IsActive || _sliderActive) return;

            _host.ApplyPoints(_settings.MashPointsPerPress, "+1",
                              _host.Palette.Good, MashFeedbackSeconds);
        }

        private void OnSliderKeyPerformed(InputAction.CallbackContext ctx)
        {
            if (!_host.IsActive || !_sliderActive) return;

            float pos = _sliderIndicatorPos;
            bool hit = Mathf.Abs(pos - 0.5f) <= _settings.HitZoneFraction;
            bool near = Mathf.Abs(pos - 0.5f) <= _settings.NearZoneFraction;

            ResolveSlider(hit, near);
        }

        // ── Slider ────────────────────────────────────────────────────────────

        private IEnumerator SliderRoutine()
        {
            while (_host.IsActive)
            {
                float wait = UnityEngine.Random.Range(_settings.SliderMinInterval,
                                                      _settings.SliderMaxInterval);
                yield return new WaitForSeconds(wait);

                if (!_host.IsActive) yield break;
                var keys = _settings.SliderKeys;
                if (keys == null || keys.Length == 0) continue;

                int idx = UnityEngine.Random.Range(0, keys.Length);
                _activeSliderIndex = idx;
                _sliderIndicatorPos = 0f;
                _sliderActive = true;

                string keyName = GetActionDisplayName(idx);
                if (_settings.SliderKeyLabel != null) _settings.SliderKeyLabel.text = keyName;
                if (_settings.SliderPanel != null) _settings.SliderPanel.SetActive(true);

                EnableSliderInput(idx);

                yield return new WaitUntil(() => !_sliderActive);
            }
        }

        private void ResolveSlider(bool hit, bool near)
        {
            DisableSliderInput();
            _sliderActive = false;

            MinigamePalette palette = _host.Palette;
            float points;
            string msg;
            Color color;

            if (hit)
            {
                points = SliderHitPoints;
                msg = "CENTRATO! +15";
                color = palette.Good;
            }
            else if (near)
            {
                points = SliderNearPoints;
                msg = "QUASI +5";
                color = palette.Warning;
            }
            else
            {
                points = SliderMissPoints;
                msg = "MANCATO −20";
                color = palette.Critical;
            }

            // ×ruolo sui soli guadagni positivi → applicato dall'host (Rev U).
            // NB: può chiudere la sessione (soglia 100%) → Disable() rientrante:
            // le righe sotto restano innocue su UI già spenta (come pre-BN).
            _host.ApplyPoints(points, msg, color, SliderFeedbackSeconds);

            if (_settings.SliderPanel != null) _settings.SliderPanel.SetActive(false);
            _sliderIndicatorPos = 0f;
            _activeSliderIndex = -1;
        }

        private void UpdateSliderIndicatorPosition()
        {
            var sliderIndicator = _settings.SliderIndicator;
            var sliderPanel = _settings.SliderPanel;
            if (sliderIndicator == null || sliderPanel == null) return;

            var panelRect = (RectTransform)sliderPanel.transform;
            float halfPanel = panelRect.rect.width * 0.5f;
            float halfInd = sliderIndicator.rect.width * 0.5f;

            float minX = -halfPanel + halfInd;
            float maxX = halfPanel - halfInd;

            var pos = sliderIndicator.anchoredPosition;
            pos.x = Mathf.Lerp(minX, maxX, _sliderIndicatorPos);
            sliderIndicator.anchoredPosition = pos;

            MinigamePalette palette = _host.Palette;

            if (_settings.HitZoneImage != null)
                _settings.HitZoneImage.color = palette.Good;

            float dist = Mathf.Abs(_sliderIndicatorPos - 0.5f);
            if (sliderIndicator.TryGetComponent<Image>(out var img))
            {
                img.color = dist <= _settings.HitZoneFraction ? palette.Good
                          : dist <= _settings.NearZoneFraction ? palette.Warning
                          : Color.white;
            }
        }

        private string GetActionDisplayName(int index)
        {
            var keys = _settings.SliderKeys;
            if (keys == null || index < 0
                || index >= keys.Length) return "?";

            var action = keys[index]?.action;
            if (action == null) return "?";

            foreach (var binding in action.bindings)
            {
                if (!binding.isPartOfComposite)
                    return InputControlPath.ToHumanReadableString(
                        binding.effectivePath,
                        InputControlPath.HumanReadableStringOptions.OmitDevice);
            }
            return action.name.ToUpper();
        }
    }
}
