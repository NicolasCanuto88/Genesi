using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// ProgressiveMinigame — Framework Minigame Progressivo (D24 · Rev BB)
    ///
    /// Classe base astratta che generalizza il minigame a barra di progresso con
    /// soglie (50/75/100%), estratta da RepairMinigame (M2). Ingegneria e Medicina
    /// (Corpsman) ne derivano SENZA duplicare logica:
    ///   - RepairMinigameEngineering    : ProgressiveMinigame  (riparazione subsystem)
    ///   - StabilizationMinigameMedical : ProgressiveMinigame  (stabilizzazione — placeholder)
    ///
    /// COSA VIVE QUI (condiviso):
    ///   - Accumulo progresso 0-100, decay, soglie one-shot.
    ///     L'EFFETTO di soglia è delegato al derivato (vedi ApplyThresholdEffect):
    ///     l'invariante materiali "solo al superamento soglia, mai all'avvio né su
    ///     interruzione" è onorata qui, perché l'effetto scatta UNA volta in CrossThreshold.
    ///   - Interazione di DEFAULT: Mashing + Slider burst (GDD §9.8). Scelta QA:
    ///     resta nella base come default overridabile; un derivato con interazione
    ///     diversa potrà sostituirla, ma finché non serve è condivisa.
    ///   - Lifecycle (grace, timer, open/interrupt/close), UI barra/slider, input.
    ///   - Hook modificatore di ruolo (clausola malus trasversale, Rev U) — default
    ///     IDENTITÀ (1.0): i tipi esistenti restano invariati; i ruoli lo cablano
    ///     nella Fase ruoli.
    ///
    /// COSA DELEGA AL DERIVATO (astratto):
    ///   - GetTargetDisplayName()  → etichetta header (nome sistema / paziente)
    ///   - GetInitialDecayRate()   → velocità decay iniziale (dominio-specifica)
    ///   - GetTimeLimitSeconds()   → limite di tempo iniziale (dominio-specifico)
    ///   - ApplyThresholdEffect()  → effetto server-authority a ogni soglia
    ///                               (Ingegneria → RepairPanel RPC; Medicina → TBD)
    ///
    /// NB: MonoBehaviour ASTRATTO — non si aggiunge mai direttamente a un GameObject;
    /// si aggiunge il concreto (RepairMinigameEngineering / StabilizationMinigameMedical).
    /// I campi [SerializeField] della base sono serializzati sul componente concreto.
    /// </summary>
    public abstract class ProgressiveMinigame : MonoBehaviour
    {
        // ── UI References (interazione condivisa) ─────────────────────────────

        [Header("Progress Bar")]
        [SerializeField] protected Image progressBarFill;
        [SerializeField] protected TextMeshProUGUI progressText;
        [SerializeField] protected TextMeshProUGUI systemNameText;
        [SerializeField] protected TextMeshProUGUI statusText;

        [Header("Threshold Markers")]
        [Tooltip("Marker visivo a 50% sulla barra. Cambia colore a colorGood quando la soglia " +
                 "viene superata, per segnalare \"questo guadagno è già al sicuro\".")]
        [SerializeField] protected Image marker50;
        [Tooltip("Marker visivo a 75% sulla barra. Stesso comportamento di marker50.")]
        [SerializeField] protected Image marker75;

        [Header("Slider Event")]
        [SerializeField] protected GameObject sliderPanel;
        [SerializeField] protected RectTransform sliderIndicator;
        [SerializeField] protected TextMeshProUGUI sliderKeyLabel;
        [SerializeField] protected Image hitZoneImage;

        [Header("Root Canvas")]
        [SerializeField] protected GameObject rootCanvas;

        // ── Input ─────────────────────────────────────────────────────────────

        [Header("Input — New Input System")]
        [Tooltip("Azione mappata a E / South button. Aggiungi 'RepairMash' al tuo InputActions.")]
        [SerializeField] protected InputActionReference mashAction;

        [Tooltip("4–6 azioni casuali per lo slider. Aggiungi 'RepairKey_0…5' al tuo InputActions.")]
        [SerializeField] protected InputActionReference[] repairSliderKeys;

        // ── Parametri minigame ──────────────────────────────────────────────────

        [Header("Parametri Minigame")]
        [Tooltip("Punti aggiunti per ogni pressione di mash.")]
        [SerializeField] protected float mashPointsPerPress = 1f;

        [Tooltip("Zona di successo slider (±fraction della larghezza). 0.10 = ±10%.")]
        [SerializeField] protected float hitZoneFraction = 0.10f;

        [Tooltip("Zona 'quasi' slider. 0.25 = ±25%.")]
        [SerializeField] protected float nearZoneFraction = 0.25f;

        [Tooltip("Velocità dell'indicatore slider (da 0 a 1 in secondi).")]
        [SerializeField] protected float sliderSpeed = 0.4f;

        [SerializeField] protected float sliderMinInterval = 8f;
        [SerializeField] protected float sliderMaxInterval = 14f;

        // ── Timer ───────────────────────────────────────────────────────────────

        [Header("Timer")]
        [SerializeField] protected TextMeshProUGUI timerText;

        protected float _timeRemaining;

        // ── Grace Period ──────────────────────────────────────────────────────

        [Header("Grace Period")]
        [Tooltip("Secondi di attesa prima che il decay inizi. Evita 0% accidentali all'apertura.")]
        [SerializeField] protected float startGraceDuration = 2.5f;

        protected float _graceTimer;
        protected bool _inGracePeriod;

        // ── Colori feedback ─────────────────────────────────────────────────────

        [Header("Colori")]
        [SerializeField] protected Color colorGood = new Color(0.2f, 1f, 0.4f);
        [SerializeField] protected Color colorWarning = new Color(1f, 0.67f, 0f);
        [SerializeField] protected Color colorCritical = new Color(1f, 0.2f, 0f);
        [SerializeField] protected Color colorNeutral = new Color(0.8f, 0.8f, 0.8f);

        [Tooltip("Colore dei marker50/marker75 PRIMA che la soglia venga raggiunta.")]
        [SerializeField] protected Color colorMarkerDefault = new Color(1f, 1f, 1f, 0.45f);

        // ── Debug (standard Rev BA) ───────────────────────────────────────────

        [Header("Debug")]
        [Tooltip("Overlay OnGUI di diagnostica (solo Editor/Development Build). Standard Rev BA — default off.")]
        [SerializeField] protected bool showDebugUI = false;

        [Tooltip("Log verbosi non critici (standard Rev BA — default off). I problemi reali restano sempre attivi.")]
        [SerializeField] protected bool logVerbose = false;

        // ── Stato runtime ───────────────────────────────────────────────────────

        protected float _progress;          // 0–100
        protected float _decayRate;
        protected bool _isActive;
        protected bool _sliderActive;
        protected float _sliderIndicatorPos; // 0–1
        protected int _activeSliderIndex = -1;

        // Soglie già superate in questa sessione (per non applicare effetti duplicati)
        protected bool _threshold50Crossed;
        protected bool _threshold75Crossed;
        protected bool _threshold100Crossed;

        protected Action _onComplete;
        protected Action _onInterrupted;

        protected Coroutine _sliderRoutine;
        protected Coroutine _statusRoutine;

        // ── Testi status (overridabili — default engineering-flavored) ──────────

        protected virtual string PromptText => "PREMI E PER RIPARARE";
        protected virtual string CompleteText => "SISTEMA RIPARATO!";

        // ── Hook astratti (dominio) ─────────────────────────────────────────────

        /// <summary>Etichetta header (nome sistema / paziente). ToUpper è applicato dalla base.</summary>
        protected abstract string GetTargetDisplayName();

        /// <summary>Velocità di decay iniziale della barra (PRIMA del modificatore di ruolo).</summary>
        protected abstract float GetInitialDecayRate();

        /// <summary>Limite di tempo iniziale in secondi (PRIMA del modificatore di ruolo).</summary>
        protected abstract float GetTimeLimitSeconds();

        /// <summary>
        /// Effetto server-authority a ogni soglia superata (50/75/100).
        /// Ingegneria → RepairPanel.ApplyRepairThresholdRpc(pct).
        /// L'invariante materiali è onorata QUI: si applica SOLO su chiamata da CrossThreshold.
        /// </summary>
        protected abstract void ApplyThresholdEffect(float pct);

        // ── Hook modificatore di ruolo (clausola malus trasversale · Rev U) ─────

        /// <summary>Moltiplicatore ruolo sul decay. Default 1 (identità). Override nella Fase ruoli.</summary>
        protected virtual float GetRoleDecayMultiplier() => 1f;

        /// <summary>Moltiplicatore ruolo sui punti guadagnati (mash + slider positivi). Default 1 (identità).</summary>
        protected virtual float GetRolePointsMultiplier() => 1f;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected virtual void Awake()
        {
            if (rootCanvas != null) rootCanvas.SetActive(false);
            if (sliderPanel != null) sliderPanel.SetActive(false);
        }

        protected virtual void Update()
        {
            if (!_isActive) return;

            // Timer countdown
            _timeRemaining -= Time.deltaTime;
            if (_timeRemaining <= 0f)
            {
                OnTimerExpired();
                return;
            }

            // Aggiorna UI timer
            if (timerText != null)
            {
                int mins = Mathf.FloorToInt(_timeRemaining / 60f);
                int secs = Mathf.FloorToInt(_timeRemaining % 60f);
                timerText.text = $"{mins:D2}:{secs:D2}";
                timerText.color = _timeRemaining < 20f ? colorCritical
                                : _timeRemaining < 45f ? colorWarning
                                : colorNeutral;
            }

            // Grace period — decay sospeso, countdown visivo
            if (_inGracePeriod)
            {
                _graceTimer -= Time.deltaTime;
                if (_graceTimer <= 0f)
                {
                    _inGracePeriod = false;
                    SetStatus(PromptText, colorNeutral);
                }
                else
                {
                    if (statusText != null)
                    {
                        statusText.text = $"INIZIA TRA {Mathf.CeilToInt(_graceTimer)}...";
                        statusText.color = colorWarning;
                    }
                    UpdateUI();
                    return;
                }
            }

            // Decay normale — solo dopo il grace period
            _progress -= _decayRate * Time.deltaTime;
            _progress = Mathf.Clamp(_progress, 0f, 100f);

            // Slider indicator movement
            if (_sliderActive)
            {
                _sliderIndicatorPos += sliderSpeed * Time.deltaTime;
                if (_sliderIndicatorPos > 1f)
                    ResolveSlider(false, false);
                else
                    UpdateSliderIndicatorPosition();
            }

            UpdateUI();
        }

        // ── API protetta — avvio sessione (template method) ─────────────────────

        /// <summary>
        /// Sequenza di apertura condivisa. Il derivato imposta PRIMA il proprio
        /// target/coordinatore, poi chiama questo. Le grandezze dominio-specifiche
        /// arrivano dagli hook (GetTargetDisplayName / GetInitialDecayRate /
        /// GetTimeLimitSeconds).
        /// </summary>
        protected void BeginSession(Action onComplete, Action onInterrupted)
        {
            _onComplete = onComplete;
            _onInterrupted = onInterrupted;

            // Reset stato
            _progress = 0f;
            _threshold50Crossed = false;
            _threshold75Crossed = false;
            _threshold100Crossed = false;
            _sliderActive = false;
            _activeSliderIndex = -1;
            _sliderIndicatorPos = 0f;

            // Reset marker — nuova sessione, nessuna soglia ancora "al sicuro"
            if (marker50 != null) marker50.color = colorMarkerDefault;
            if (marker75 != null) marker75.color = colorMarkerDefault;

            // Decay + tempo iniziale (con modificatore di ruolo, default identità)
            _decayRate = GetInitialDecayRate() * GetRoleDecayMultiplier();
            _graceTimer = startGraceDuration;
            _inGracePeriod = true;
            _timeRemaining = GetTimeLimitSeconds();

            // UI
            if (rootCanvas != null) rootCanvas.SetActive(true);
            if (sliderPanel != null) sliderPanel.SetActive(false);

            if (systemNameText != null)
                systemNameText.text = GetTargetDisplayName().ToUpper();

            SetStatus(PromptText, colorNeutral);

            // Input
            EnableMashInput();

            // Slider routine — _isActive prima di StartCoroutine (regola invariante)
            _isActive = true;
            _sliderRoutine = StartCoroutine(SliderRoutine());
        }

        /// <summary>Interrompe il minigame senza applicare effetti (nessun consumo materiali).</summary>
        public void Interrupt()
        {
            if (!_isActive) return;
            CloseInternal();
            _onInterrupted?.Invoke();
        }

        // ── Input Handlers ──────────────────────────────────────────────────────

        protected void EnableMashInput()
        {
            if (mashAction?.action == null) return;
            mashAction.action.Enable();
            mashAction.action.performed += OnMashPerformed;
        }

        protected void DisableMashInput()
        {
            if (mashAction?.action == null) return;
            mashAction.action.performed -= OnMashPerformed;
        }

        protected void EnableSliderInput(int keyIndex)
        {
            if (repairSliderKeys == null || keyIndex < 0
                || keyIndex >= repairSliderKeys.Length) return;

            var action = repairSliderKeys[keyIndex]?.action;
            if (action == null) return;
            action.Enable();
            action.performed += OnSliderKeyPerformed;
        }

        protected void DisableSliderInput()
        {
            if (repairSliderKeys == null || _activeSliderIndex < 0) return;
            var action = repairSliderKeys[_activeSliderIndex]?.action;
            if (action == null) return;
            action.performed -= OnSliderKeyPerformed;
        }

        private void OnMashPerformed(InputAction.CallbackContext ctx)
        {
            if (!_isActive || _sliderActive) return;

            _progress = Mathf.Min(_progress + mashPointsPerPress * GetRolePointsMultiplier(), 100f);
            SetStatus("+1", colorGood, 0.3f);
            CheckThresholds();
        }

        private void OnSliderKeyPerformed(InputAction.CallbackContext ctx)
        {
            if (!_isActive || !_sliderActive) return;

            float pos = _sliderIndicatorPos;
            bool hit = Mathf.Abs(pos - 0.5f) <= hitZoneFraction;
            bool near = Mathf.Abs(pos - 0.5f) <= nearZoneFraction;

            ResolveSlider(hit, near);
        }

        // ── Slider Coroutine ──────────────────────────────────────────────────

        private IEnumerator SliderRoutine()
        {
            while (_isActive)
            {
                float wait = UnityEngine.Random.Range(sliderMinInterval, sliderMaxInterval);
                yield return new WaitForSeconds(wait);

                if (!_isActive) yield break;
                if (repairSliderKeys == null || repairSliderKeys.Length == 0) continue;

                int idx = UnityEngine.Random.Range(0, repairSliderKeys.Length);
                _activeSliderIndex = idx;
                _sliderIndicatorPos = 0f;
                _sliderActive = true;

                string keyName = GetActionDisplayName(idx);
                if (sliderKeyLabel != null) sliderKeyLabel.text = keyName;
                if (sliderPanel != null) sliderPanel.SetActive(true);

                EnableSliderInput(idx);

                yield return new WaitUntil(() => !_sliderActive);
            }
        }

        private void ResolveSlider(bool hit, bool near)
        {
            DisableSliderInput();
            _sliderActive = false;

            float points;
            string msg;
            Color color;

            if (hit)
            {
                points = 15f;
                msg = "CENTRATO! +15";
                color = colorGood;
            }
            else if (near)
            {
                points = 5f;
                msg = "QUASI +5";
                color = colorWarning;
            }
            else
            {
                points = -20f;
                msg = "MANCATO −20";
                color = colorCritical;
            }

            // Il modificatore di ruolo si applica solo ai guadagni positivi.
            if (points > 0f) points *= GetRolePointsMultiplier();

            _progress = Mathf.Clamp(_progress + points, 0f, 100f);
            SetStatus(msg, color, 1.5f);
            CheckThresholds();

            if (sliderPanel != null) sliderPanel.SetActive(false);
            _sliderIndicatorPos = 0f;
            _activeSliderIndex = -1;
        }

        private void OnTimerExpired()
        {
            // Riparazione parziale: mantiene le soglie già raggiunte.
            // I materiali già consumati alle soglie precedenti NON vengono rimborsati.
            SetStatus("TEMPO SCADUTO", colorCritical);
            CloseInternal();
            _onInterrupted?.Invoke();
        }

        // ── Soglie ──────────────────────────────────────────────────────────────

        protected void CheckThresholds()
        {
            if (!_threshold50Crossed && _progress >= 50f) CrossThreshold(50f, ref _threshold50Crossed);
            if (!_threshold75Crossed && _progress >= 75f) CrossThreshold(75f, ref _threshold75Crossed);
            if (!_threshold100Crossed && _progress >= 100f) CrossThreshold(100f, ref _threshold100Crossed);
        }

        /// <summary>
        /// Superamento soglia: colora il marker (feedback "guadagno al sicuro"),
        /// delega l'effetto server-authority al derivato (ApplyThresholdEffect),
        /// e al 100% chiude il minigame localmente.
        ///
        /// INVARIANTE: l'effetto scatta UNA volta a soglia — mai all'avvio, mai su
        /// interruzione. I flag _thresholdNNCrossed impediscono i duplicati.
        /// </summary>
        private void CrossThreshold(float pct, ref bool flag)
        {
            flag = true;

            if (Mathf.Approximately(pct, 50f) && marker50 != null)
                marker50.color = colorGood;
            else if (Mathf.Approximately(pct, 75f) && marker75 != null)
                marker75.color = colorGood;

            ApplyThresholdEffect(pct);

            if (pct >= 100f)
            {
                SetStatus(CompleteText, colorGood);
                CloseInternal();
                _onComplete?.Invoke();
                return;
            }

            SetStatus($"SOGLIA {pct:F0}% RAGGIUNTA", colorGood, 2f);
        }

        // ── UI ────────────────────────────────────────────────────────────────

        private void UpdateUI()
        {
            float normalized = _progress / 100f;

            if (progressBarFill != null)
            {
                progressBarFill.fillAmount = normalized;
                progressBarFill.color = GetBarColor(normalized);
            }

            if (progressText != null)
                progressText.text = $"{_progress:F0}/100";
        }

        private void UpdateSliderIndicatorPosition()
        {
            if (sliderIndicator == null || sliderPanel == null) return;

            var panelRect = (RectTransform)sliderPanel.transform;
            float halfPanel = panelRect.rect.width * 0.5f;
            float halfInd = sliderIndicator.rect.width * 0.5f;

            float minX = -halfPanel + halfInd;
            float maxX = halfPanel - halfInd;

            var pos = sliderIndicator.anchoredPosition;
            pos.x = Mathf.Lerp(minX, maxX, _sliderIndicatorPos);
            sliderIndicator.anchoredPosition = pos;

            if (hitZoneImage != null)
                hitZoneImage.color = colorGood;

            float dist = Mathf.Abs(_sliderIndicatorPos - 0.5f);
            if (sliderIndicator.TryGetComponent<Image>(out var img))
            {
                img.color = dist <= hitZoneFraction ? colorGood
                          : dist <= nearZoneFraction ? colorWarning
                          : Color.white;
            }
        }

        protected void SetStatus(string msg, Color color, float duration = 0f)
        {
            if (statusText == null) return;
            statusText.text = msg;
            statusText.color = color;

            if (_statusRoutine != null) StopCoroutine(_statusRoutine);
            if (duration > 0f)
                _statusRoutine = StartCoroutine(ClearStatusAfter(duration));
        }

        private IEnumerator ClearStatusAfter(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (statusText != null)
            {
                statusText.text = _isActive ? PromptText : "";
                statusText.color = colorNeutral;
            }
        }

        private Color GetBarColor(float normalized)
        {
            if (normalized >= 0.75f) return colorGood;
            if (normalized >= 0.50f) return colorWarning;
            return colorCritical;
        }

        // ── Cleanup ─────────────────────────────────────────────────────────────

        protected void CloseInternal()
        {
            _isActive = false;

            DisableMashInput();
            DisableSliderInput();

            if (_sliderRoutine != null) StopCoroutine(_sliderRoutine);
            if (_statusRoutine != null) StopCoroutine(_statusRoutine);

            if (rootCanvas != null) rootCanvas.SetActive(false);
            if (sliderPanel != null) sliderPanel.SetActive(false);

            _sliderActive = false;
            _activeSliderIndex = -1;
        }

        // ── Helper ────────────────────────────────────────────────────────────

        protected string GetActionDisplayName(int index)
        {
            if (repairSliderKeys == null || index < 0
                || index >= repairSliderKeys.Length) return "?";

            var action = repairSliderKeys[index]?.action;
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

        // ── Debug helpers (standard Rev BA — protected per condivisione derivati) ──

        protected void LogV(string msg)      { if (logVerbose) Debug.Log(msg); }
        protected void LogVWarn(string msg)  { if (logVerbose) Debug.LogWarning(msg); }
        protected void LogVError(string msg) { if (logVerbose) Debug.LogError(msg); }

        // ── Debug GUI ─────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showDebugUI) return;
            if (!_isActive) return;

            GUILayout.BeginArea(new Rect(Screen.width - 220, 10, 210, 160));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[{GetType().Name}]");
            GUILayout.Label($"Progresso: {_progress:F1}/100");
            GUILayout.Label($"Decay: {_decayRate:F1} pt/s");
            GUILayout.Label($"Slider: {(_sliderActive ? $"ATTIVO [{_sliderIndicatorPos:F2}]" : "in attesa")}");
            GUILayout.Label($"Soglie: 50={_threshold50Crossed} 75={_threshold75Crossed} 100={_threshold100Crossed}");
            OnDebugGUIExtra();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        /// <summary>Righe di debug aggiuntive dominio-specifiche (es. stato del coordinatore server).</summary>
        protected virtual void OnDebugGUIExtra() { }
#endif
    }
}
