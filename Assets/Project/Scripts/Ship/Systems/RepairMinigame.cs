using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// RepairMinigame — Milestone 2
    /// Minigame di riparazione in volo. World Space UI su pannello fisico nave.
    ///
    /// MECCANICA (GDD §9.8):
    ///   Componente 1 — Mashing:
    ///     Premi [RepairMash] ripetutamente → +1 pt per pressione.
    ///     La barra decade automaticamente (velocità dipende da ShipSystemState).
    ///     Obiettivo: compensare il decay e fare progresso netto.
    ///
    ///   Componente 2 — Slider burst (ogni 8–14 sec):
    ///     Un indicatore si muove da sinistra a destra.
    ///     Sopra: uno dei [repairSliderKeys] scelto a caso.
    ///     Premi quel tasto quando l'indicatore è nella zona centrale.
    ///     Centrato (±10%) → +15pt · Quasi (±25%) → +5pt · Mancato → −20pt
    ///
    ///   Soglie (50% / 75% / 100%):
    ///     Al superamento soglia: RepairPanel.ApplyRepairThresholdRpc(pct) → server.
    ///     Il server consuma materiali (InventorySystem) e applica la riparazione.
    ///     Regola invariante GDD: materiali consumati SOLO al superamento soglia,
    ///     mai all'avvio, mai in caso di interruzione.
    ///
    /// MULTIPLAYER (Rev M — fix):
    ///   CrossThreshold() non chiama più InventorySystem.TryConsume() o
    ///   IRepairable.ApplyRepair() direttamente (erano server-only, fallivano
    ///   silenziosamente sul client). Ora chiama RepairPanel.ApplyRepairThresholdRpc()
    ///   che esegue entrambe le operazioni sul server con server authority.
    ///
    /// INPUT (New Input System — zero Keyboard.current):
    ///   [SerializeField] mashAction       → mappato a E / South gamepad
    ///   [SerializeField] repairSliderKeys → 4–6 InputActionReference casuali
    ///
    /// SETUP IN SCENA:
    ///   Canvas World Space (scala 0.001) figlio del RepairPanel.
    ///   Nessun Mask + Viewport — Content diretto su Panel_Background.
    ///   Assegna mashAction e repairSliderKeys dall'InputActions asset.
    ///   Assegna repairPanel (stesso GameObject o padre — sempre presente).
    /// </summary>
    public class RepairMinigame : MonoBehaviour
    {
        // ── UI References ─────────────────────────────────────────────────────

        [Header("Progress Bar")]
        [SerializeField] private Image progressBarFill;
        [SerializeField] private TextMeshProUGUI progressText;
        [SerializeField] private TextMeshProUGUI systemNameText;
        [SerializeField] private TextMeshProUGUI statusText;

        [Header("Threshold Markers")]
        [Tooltip("Marker visivo a 50% sulla barra (Image — posizionalo manualmente in Canvas). " +
                 "Cambia colore a colorGood quando la soglia viene superata, per segnalare " +
                 "\"questo guadagno è già al sicuro\" anche se la barra decade sotto questo punto dopo.")]
        [SerializeField] private Image marker50;
        [Tooltip("Marker visivo a 75% sulla barra. Stesso comportamento di marker50.")]
        [SerializeField] private Image marker75;

        [Header("Slider Event")]
        [SerializeField] private GameObject sliderPanel;
        [SerializeField] private RectTransform sliderIndicator;
        [SerializeField] private TextMeshProUGUI sliderKeyLabel;
        [SerializeField] private Image hitZoneImage;

        [Header("Root Canvas")]
        [SerializeField] private GameObject rootCanvas;

        // ── Input ─────────────────────────────────────────────────────────────

        [Header("Input — New Input System")]
        [Tooltip("Azione mappata a E / South button. Aggiungi 'RepairMash' al tuo InputActions.")]
        [SerializeField] private InputActionReference mashAction;

        [Tooltip("4–6 azioni casuali per lo slider. Aggiungi 'RepairKey_0…5' al tuo InputActions.")]
        [SerializeField] private InputActionReference[] repairSliderKeys;

        // ── Network — riferimento al RepairPanel per RPC ──────────────────────

        [Header("Network")]
        [Tooltip("RepairPanel su questo stesso GameObject (o padre). " +
                 "Usato per ApplyRepairThresholdRpc — obbligatorio per multiplayer.")]
        [SerializeField] private RepairPanel repairPanel;

        // ── Parametri minigame ────────────────────────────────────────────────

        [Header("Parametri Minigame")]
        [Tooltip("Punti aggiunti per ogni pressione di RepairMash.")]
        [SerializeField] private float mashPointsPerPress = 1f;

        [Tooltip("Zona di successo slider (±fraction della larghezza). 0.10 = ±10%.")]
        [SerializeField] private float hitZoneFraction = 0.10f;

        [Tooltip("Zona 'quasi' slider. 0.25 = ±25%.")]
        [SerializeField] private float nearZoneFraction = 0.25f;

        [Tooltip("Velocità dell'indicatore slider (da 0 a 1 in secondi).")]
        [SerializeField] private float sliderSpeed = 0.4f;

        [SerializeField] private float sliderMinInterval = 8f;
        [SerializeField] private float sliderMaxInterval = 14f;

        // ── Timer minigame ────────────────────────────────────────────────────

        [Header("Timer")]
        [SerializeField] private TextMeshProUGUI timerText;
        [SerializeField] private float timeLimitDegradedLight = 120f;
        [SerializeField] private float timeLimitDegradedHeavy = 90f;
        [SerializeField] private float timeLimitOffline = 60f;

        private float _timeRemaining;

        // ── Vantaggio iniziale ────────────────────────────────────────────────

        [Header("Grace Period")]
        [Tooltip("Secondi di attesa prima che il decay inizi. Evita 0% accidentali all'apertura.")]
        [SerializeField] private float startGraceDuration = 2.5f;

        private float _graceTimer;
        private bool _inGracePeriod;

        // ── Colori feedback ───────────────────────────────────────────────────

        [Header("Colori")]
        [SerializeField] private Color colorGood = new Color(0.2f, 1f, 0.4f);
        [SerializeField] private Color colorWarning = new Color(1f, 0.67f, 0f);
        [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);
        [SerializeField] private Color colorNeutral = new Color(0.8f, 0.8f, 0.8f);

        [Tooltip("Colore dei marker50/marker75 PRIMA che la soglia venga raggiunta.")]
        [SerializeField] private Color colorMarkerDefault = new Color(1f, 1f, 1f, 0.45f);

        // ── Stato runtime ─────────────────────────────────────────────────────

        private IRepairable _target;
        private float _progress;          // 0–100
        private float _decayRate;
        private bool _isActive;
        private bool _sliderActive;
        private float _sliderIndicatorPos; // 0–1
        private int _activeSliderIndex = -1;
        private float _statusTimer;

        // Soglie già superate in questa sessione (per non inviare RPC duplicati)
        private bool _threshold50Crossed;
        private bool _threshold75Crossed;
        private bool _threshold100Crossed;

        private Action _onComplete;
        private Action _onInterrupted;

        private Coroutine _sliderRoutine;
        private Coroutine _statusRoutine;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            if (rootCanvas != null) rootCanvas.SetActive(false);
            if (sliderPanel != null) sliderPanel.SetActive(false);

            // Auto-cerca RepairPanel se non assegnato in Inspector
            if (repairPanel == null)
                repairPanel = GetComponentInParent<RepairPanel>();

            if (repairPanel == null)
                Debug.LogWarning("[RepairMinigame] RepairPanel non trovato — " +
                                 "le riparazioni non saranno sincronizzate in multiplayer.");
        }

        private void Update()
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
                    SetStatus("PREMI E PER RIPARARE", colorNeutral);
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

        // ── API pubblica ──────────────────────────────────────────────────────

        /// <summary>
        /// Apre il minigame per il sistema specificato.
        /// panel: il RepairPanel che coordina l'RPC server-side.
        /// </summary>
        public void Open(IRepairable target, RepairPanel panel,
                         Action onComplete, Action onInterrupted)
        {
            if (target == null || !target.IsRepairable()) return;

            _target = target;
            _onComplete = onComplete;
            _onInterrupted = onInterrupted;

            // Usa il panel passato esplicitamente (priorità su quello serializzato)
            if (panel != null) repairPanel = panel;

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

            // Calcola decay dal sistema
            _decayRate = _target.GetCurrentState().GetBarDecayRate();
            _graceTimer = startGraceDuration;
            _inGracePeriod = true;
            _timeRemaining = GetTimeLimit(_target.GetCurrentState());

            // UI
            if (rootCanvas != null) rootCanvas.SetActive(true);
            if (sliderPanel != null) sliderPanel.SetActive(false);

            if (systemNameText != null)
                systemNameText.text = target.GetSystemName().ToUpper();

            SetStatus("PREMI E PER RIPARARE", colorNeutral);

            // Input
            EnableMashInput();

            // Slider routine — _isActive prima di StartCoroutine (regola invariante)
            _isActive = true;
            _sliderRoutine = StartCoroutine(SliderRoutine());
        }

        /// <summary>Interrompe il minigame senza consumare materiali.</summary>
        public void Interrupt()
        {
            if (!_isActive) return;
            CloseInternal();
            _onInterrupted?.Invoke();
        }

        // ── Input Handlers ────────────────────────────────────────────────────

        private void EnableMashInput()
        {
            if (mashAction?.action == null) return;
            mashAction.action.Enable();
            mashAction.action.performed += OnMashPerformed;
        }

        private void DisableMashInput()
        {
            if (mashAction?.action == null) return;
            mashAction.action.performed -= OnMashPerformed;
        }

        private void EnableSliderInput(int keyIndex)
        {
            if (repairSliderKeys == null || keyIndex < 0
                || keyIndex >= repairSliderKeys.Length) return;

            var action = repairSliderKeys[keyIndex]?.action;
            if (action == null) return;
            action.Enable();
            action.performed += OnSliderKeyPerformed;
        }

        private void DisableSliderInput()
        {
            if (repairSliderKeys == null || _activeSliderIndex < 0) return;
            var action = repairSliderKeys[_activeSliderIndex]?.action;
            if (action == null) return;
            action.performed -= OnSliderKeyPerformed;
        }

        private void OnMashPerformed(InputAction.CallbackContext ctx)
        {
            if (!_isActive || _sliderActive) return;

            _progress = Mathf.Min(_progress + mashPointsPerPress, 100f);
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

            _progress = Mathf.Clamp(_progress + points, 0f, 100f);
            SetStatus(msg, color, 1.5f);
            CheckThresholds();

            if (sliderPanel != null) sliderPanel.SetActive(false);
            _sliderIndicatorPos = 0f;
            _activeSliderIndex = -1;
        }

        private void OnTimerExpired()
        {
            // Riparazione parziale: mantiene le soglie già raggiunte
            // I materiali già consumati alle soglie precedenti NON vengono rimborsati
            SetStatus("TEMPO SCADUTO", colorCritical);
            CloseInternal();
            _onInterrupted?.Invoke();
        }

        // ── Soglie ────────────────────────────────────────────────────────────

        private void CheckThresholds()
        {
            if (!_threshold50Crossed && _progress >= 50f) CrossThreshold(50f, ref _threshold50Crossed);
            if (!_threshold75Crossed && _progress >= 75f) CrossThreshold(75f, ref _threshold75Crossed);
            if (!_threshold100Crossed && _progress >= 100f) CrossThreshold(100f, ref _threshold100Crossed);
        }

        /// <summary>
        /// Superamento soglia: invia RPC al server.
        ///
        /// Il server (RepairPanel.ApplyRepairThresholdRpc) esegue:
        ///   1. InventorySystem.TryConsume() — server authority
        ///   2. IRepairable.ApplyRepair()   — server authority
        ///
        /// RepairMinigame gestisce SOLO la UI locale (progress bar, status text,
        /// callback onComplete). Nessun accesso diretto a sistemi server-only.
        /// </summary>
        private void CrossThreshold(float pct, ref bool flag)
        {
            flag = true;

            // Evidenzia il marker corrispondente — feedback "guadagno al sicuro".
            // La barra può ancora decadere sotto questo punto: il marker verde
            // ricorda che l'HP/materiali di questa soglia sono già stati applicati
            // permanentemente (server-side), indipendentemente da cosa fa la barra dopo.
            if (Mathf.Approximately(pct, 50f) && marker50 != null)
                marker50.color = colorGood;
            else if (Mathf.Approximately(pct, 75f) && marker75 != null)
                marker75.color = colorGood;

            // Delega consumo materiali + riparazione al server via RPC
            if (repairPanel != null)
            {
                repairPanel.ApplyRepairThresholdRpc(pct);
            }
            else
            {
                Debug.LogError("[RepairMinigame] repairPanel null — RPC non inviato. " +
                               "Assegna RepairPanel in Inspector o sulla stessa gerarchia.");
            }

            // Completamento totale → chiudi minigame localmente
            if (pct >= 100f)
            {
                SetStatus("SISTEMA RIPARATO!", colorGood);
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

        private void SetStatus(string msg, Color color, float duration = 0f)
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
                statusText.text = _isActive ? "PREMI E PER RIPARARE" : "";
                statusText.color = colorNeutral;
            }
        }

        private Color GetBarColor(float normalized)
        {
            if (normalized >= 0.75f) return colorGood;
            if (normalized >= 0.50f) return colorWarning;
            return colorCritical;
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        private void CloseInternal()
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

        private string GetActionDisplayName(int index)
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

        private float GetTimeLimit(ShipSystemState state) => state switch
        {
            ShipSystemState.DegradedLight => timeLimitDegradedLight,
            ShipSystemState.DegradedHeavy => timeLimitDegradedHeavy,
            ShipSystemState.Offline => timeLimitOffline,
            _ => 120f
        };

        // ── Debug GUI ─────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!_isActive) return;

            GUILayout.BeginArea(new Rect(Screen.width - 220, 10, 210, 140));
            GUILayout.BeginVertical("box");
            GUILayout.Label("[RepairMinigame]");
            GUILayout.Label($"Progresso: {_progress:F1}/100");
            GUILayout.Label($"Decay: {_decayRate:F1} pt/s");
            GUILayout.Label($"Slider: {(_sliderActive ? $"ATTIVO [{_sliderIndicatorPos:F2}]" : "in attesa")}");
            GUILayout.Label($"Soglie: 50={_threshold50Crossed} 75={_threshold75Crossed} 100={_threshold100Crossed}");
            GUILayout.Label($"Panel RPC: {(repairPanel != null ? "OK" : "MANCANTE ⚠")}");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}