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
    /// soglie (50/75/100%), estratta da RepairMinigame (M2). I minigame di tutti i
    /// ruoli ne derivano SENZA duplicare logica:
    ///   - RepairMinigameEngineering        (riparazione subsystem · Ingegnere)
    ///   - StabilizationMinigameEngineering (Path H hold-against-decay · Ingegnere)
    ///   - LockMinigameScanner              (aggancio POI · Scanner)
    ///   - StabilizationMinigameMedical     (stabilizzazione paziente · Corpsman — placeholder)
    ///
    /// COSA VIVE QUI (condiviso):
    ///   - Accumulo progresso 0-100, decay, soglie one-shot.
    ///     L'EFFETTO di soglia è delegato al derivato (vedi ApplyThresholdEffect):
    ///     l'invariante materiali "solo al superamento soglia, mai all'avvio né su
    ///     interruzione" è onorata qui, perché l'effetto scatta UNA volta in CrossThreshold.
    ///   - Lifecycle (grace, timer, open/interrupt/close), UI-shell (barra, marker,
    ///     timer, status).
    ///   - Servizi all'interazione (IMinigameHost, implementato esplicitamente):
    ///     ApplyPoints è l'UNICO canale verso il progresso e ne centralizza gli
    ///     invarianti (×ruolo sui guadagni positivi, clamp, feedback prima delle
    ///     soglie, niente punti a sessione inattiva o in grace).
    ///   - Hook modificatore di ruolo (clausola malus trasversale, Rev U) — default
    ///     IDENTITÀ (1.0): i tipi esistenti restano invariati; i ruoli lo cablano
    ///     nella Fase ruoli.
    ///
    /// INTERAZIONE — seam QB (Rev BN · strategy iniettabile):
    ///   Il modello d'interazione (cosa fa il giocatore) vive dietro
    ///   IMinigameInteraction. Default = MashSliderInteraction (Mashing + Slider
    ///   burst, GDD §9.8), costruita sui campi "Slider Event" / "Input" / "Parametri
    ///   Minigame" di QUESTA classe (esposti via IMashSliderSettings). Un derivato
    ///   con meccanica propria fa override di CreateInteraction(). Scelta Q1-c: i
    ///   campi restano qui → zero migrazione dei riferimenti serializzati (debito
    ///   cosmetico: sui derivati con interazione propria restano visibili, inutilizzati).
    ///   NB (Rev BL): l'input si abilita SOLO a fine grace (IMinigameInteraction.Enable,
    ///   punto singolo in Update) → nessun progresso accumulabile durante "INIZIA TRA n…".
    ///
    /// COSA DELEGA AL DERIVATO (astratto):
    ///   - GetTargetDisplayName()  → etichetta header (nome sistema / paziente)
    ///   - GetInitialDecayRate()   → velocità decay iniziale (dominio-specifica)
    ///   - GetTimeLimitSeconds()   → limite di tempo iniziale (dominio-specifico)
    ///   - ApplyThresholdEffect()  → effetto server-authority a ogni soglia
    ///                               (Ingegneria → Repair/StabilizationPanel; Scanner →
    ///                               lock POI; Medicina → BO)
    ///
    /// NB: MonoBehaviour ASTRATTO — non si aggiunge mai direttamente a un GameObject;
    /// si aggiunge il concreto (uno dei derivati elencati sopra).
    /// I campi [SerializeField] della base sono serializzati sul componente concreto.
    /// </summary>
    public abstract class ProgressiveMinigame : MonoBehaviour, IMinigameHost, IMashSliderSettings
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

        [Header("Slider Event (interazione default Mash+Slider)")]
        [SerializeField] protected GameObject sliderPanel;
        [SerializeField] protected RectTransform sliderIndicator;
        [SerializeField] protected TextMeshProUGUI sliderKeyLabel;
        [SerializeField] protected Image hitZoneImage;

        [Header("Root Canvas")]
        [SerializeField] protected GameObject rootCanvas;

        // ── Input ─────────────────────────────────────────────────────────────

        [Header("Input — New Input System (interazione default Mash+Slider)")]
        [Tooltip("Azione mappata a E / South button. Aggiungi 'RepairMash' al tuo InputActions.")]
        [SerializeField] protected InputActionReference mashAction;

        [Tooltip("4–6 azioni casuali per lo slider. Aggiungi 'RepairKey_0…5' al tuo InputActions.")]
        [SerializeField] protected InputActionReference[] repairSliderKeys;

        // ── Parametri minigame ──────────────────────────────────────────────────

        [Header("Parametri Minigame (interazione default Mash+Slider)")]
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

        // Interazione (seam QB) — creata in modo lazy alla prima BeginSession, NON in
        // Awake: LockMinigameScanner può avere il GO spento a riposo (Awake tardivo).
        private IMinigameInteraction _interaction;

        // Soglie già superate in questa sessione (per non applicare effetti duplicati)
        protected bool _threshold50Crossed;
        protected bool _threshold75Crossed;
        protected bool _threshold100Crossed;

        protected Action _onComplete;
        protected Action _onInterrupted;

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

        /// <summary>
        /// Moltiplicatore ruolo sui punti guadagnati. Applicato da ApplyPoints ai SOLI
        /// guadagni positivi riportati da qualsiasi interazione. Default 1 (identità).
        /// </summary>
        protected virtual float GetRolePointsMultiplier() => 1f;

        // ── Hook win-mode (Rev BI · Path H "hold-against-decay") ────────────────
        // Estensioni MINIME e opt-in: i default riproducono ESATTAMENTE il modello
        // "repair" (sali a 100% = successo · timer scaduto = fallimento · start a 0 ·
        // nessun floor). Il derivato Stabilizzazione li override; RepairMinigame-
        // Engineering NON tocca nulla → gate di regressione D24 intatto.

        /// <summary>Progresso iniziale della barra (0–100). Default 0 (repair).</summary>
        protected virtual float GetStartProgress() => 0f;

        /// <summary>
        /// Se true (default), raggiungere il 100% conclude la sessione con successo.
        /// La Stabilizzazione lo mette a false: il 100% è solo buffer massimo, la
        /// vittoria è sopravvivere fino allo scadere del timer.
        /// </summary>
        protected virtual bool CompletesAtHundred => true;

        /// <summary>
        /// Se true, la barra che tocca il floor (GetFloorProgress) termina la
        /// sessione via OnFloorBreached. Default false → il repair non ha floor.
        /// </summary>
        protected virtual bool FailsAtFloor => false;

        /// <summary>Soglia-floor (0–100) sotto cui la sessione fallisce, se FailsAtFloor. Default 0.</summary>
        protected virtual float GetFloorProgress() => 0f;

        /// <summary>
        /// Chiamato quando la barra sfonda il floor (solo se FailsAtFloor).
        /// Default: chiude come interruzione (nessun effetto, nessun consumo).
        /// Il derivato può aggiungere feedback prima di chiamare base.
        /// </summary>
        protected virtual void OnFloorBreached()
        {
            CloseInternal();
            _onInterrupted?.Invoke();
        }

        // ── Hook interazione (seam QB · Rev BN) ─────────────────────────────────

        /// <summary>
        /// Crea il modello d'interazione del minigame. Default: Mash + Slider sui campi
        /// serializzati di questa classe (comportamento pre-BN). Chiamato UNA volta, alla
        /// prima BeginSession (dopo che il derivato ha impostato il proprio target).
        /// Override per un archetipo diverso (es. BO: "sutura" del Corpsman); il derivato
        /// può tenere un riferimento tipizzato all'istanza per pilotarne le fasi.
        /// </summary>
        protected virtual IMinigameInteraction CreateInteraction() => new MashSliderInteraction(this);

        private IMinigameInteraction GetOrCreateInteraction()
        {
            if (_interaction != null) return _interaction;

            _interaction = CreateInteraction();
            if (_interaction == null)
            {
                // Guard esplicito: un override che ritorna null è un errore di
                // programmazione, ma non deve lasciare il minigame senza input.
                Debug.LogError($"[{GetType().Name}] CreateInteraction() ha restituito null — " +
                               "uso MashSliderInteraction di default.");
                _interaction = new MashSliderInteraction(this);
            }
            return _interaction;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        protected virtual void Awake()
        {
            if (rootCanvas != null) rootCanvas.SetActive(false);
            // sliderPanel appartiene all'interazione di default, ma l'interazione nasce
            // lazy in BeginSession: lo stato a riposo va garantito qui (come pre-BN).
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

                    // Rev BL — Q1-b: input abilitato SOLO ora (fine grace).
                    // _isActive è già true (BeginSession) → invariante
                    // "_isActive prima di StartCoroutine" rispettata. Il flip
                    // avviene una volta sola: al frame successivo il ramo
                    // _inGracePeriod è già saltato.
                    // QB (Rev BN): l'abilitazione è dell'interazione (default:
                    // mash, poi routine slider — stesso ordine pre-BN).
                    _interaction?.Enable();
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

            // Path H (hold-against-decay) — sfondare il floor termina la sessione.
            // Opt-in: FailsAtFloor default false → il repair (che ammette 0% fino
            // allo scadere del timer) resta invariato.
            if (FailsAtFloor && _progress <= GetFloorProgress())
            {
                OnFloorBreached();
                return;
            }

            // QB (Rev BN) — tick dell'interazione. Stessa posizione del movimento
            // slider pre-BN: dopo decay e floor, prima dell'aggiornamento barra.
            _interaction?.Tick(Time.deltaTime);

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
            _progress = Mathf.Clamp(GetStartProgress(), 0f, 100f);
            _threshold50Crossed = false;
            _threshold75Crossed = false;
            _threshold100Crossed = false;

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

            // QB (Rev BN) — l'interazione resetta stato e UI propri (default: slider
            // spento, indice/posizione azzerati). Nessun input, nessuna routine (Rev BL).
            GetOrCreateInteraction().Begin(this);

            if (systemNameText != null)
                systemNameText.text = GetTargetDisplayName().ToUpper();

            SetStatus(PromptText, colorNeutral);

            // Rev BL — Q1-b (gate input in grace): l'interazione NON viene abilitata
            // qui. Durante il grace period nessun input è cablato e nessuno slider si
            // muove → impossibile accumulare progresso prima di "INIZIA". L'input si
            // abilita al flip _inGracePeriod→false in Update (punto singolo).
            // _isActive resta l'ultima assegnazione: nulla gira in Update prima che
            // il setup sia completo. (Con nessuno StartCoroutine qui, cade anche il
            // vincolo "GO attivo per StartCoroutine in BeginSession".)
            _isActive = true;
        }

        /// <summary>Interrompe il minigame senza applicare effetti (nessun consumo materiali).</summary>
        public void Interrupt()
        {
            if (!_isActive) return;
            CloseInternal();
            _onInterrupted?.Invoke();
        }

        // ── IMinigameHost (servizi all'interazione · seam QB) ───────────────────

        bool IMinigameHost.IsActive => _isActive;

        MinigamePalette IMinigameHost.Palette =>
            new MinigamePalette(colorGood, colorWarning, colorCritical, colorNeutral);

        void IMinigameHost.ApplyPoints(float rawPoints, string feedback,
                                       Color feedbackColor, float feedbackDuration)
        {
            // Invariante strutturale (Rev BL): nessun punto a sessione inattiva o in
            // grace. Con l'interazione di default è un guard mai attraversato (input
            // cablato solo post-grace, handler già gated su IsActive); protegge le
            // interazioni future.
            if (!_isActive || _inGracePeriod) return;

            // Rev U: il modificatore di ruolo si applica solo ai guadagni positivi.
            float points = rawPoints > 0f ? rawPoints * GetRolePointsMultiplier() : rawPoints;
            _progress = Mathf.Clamp(_progress + points, 0f, 100f);

            // Ordine pre-BN: feedback PRIMA delle soglie → "SOGLIA n%" / CompleteText
            // prevalgono sul feedback dell'azione.
            if (feedback != null) SetStatus(feedback, feedbackColor, feedbackDuration);
            CheckThresholds();
        }

        Coroutine IMinigameHost.StartHostedRoutine(IEnumerator routine) => StartCoroutine(routine);

        void IMinigameHost.StopHostedRoutine(Coroutine routine)
        {
            if (routine != null) StopCoroutine(routine);
        }

        // ── IMashSliderSettings (campi dell'interazione default · Q1-c) ────────

        InputActionReference IMashSliderSettings.MashAction => mashAction;
        InputActionReference[] IMashSliderSettings.SliderKeys => repairSliderKeys;
        GameObject IMashSliderSettings.SliderPanel => sliderPanel;
        RectTransform IMashSliderSettings.SliderIndicator => sliderIndicator;
        TextMeshProUGUI IMashSliderSettings.SliderKeyLabel => sliderKeyLabel;
        Image IMashSliderSettings.HitZoneImage => hitZoneImage;
        float IMashSliderSettings.MashPointsPerPress => mashPointsPerPress;
        float IMashSliderSettings.HitZoneFraction => hitZoneFraction;
        float IMashSliderSettings.NearZoneFraction => nearZoneFraction;
        float IMashSliderSettings.SliderSpeed => sliderSpeed;
        float IMashSliderSettings.SliderMinInterval => sliderMinInterval;
        float IMashSliderSettings.SliderMaxInterval => sliderMaxInterval;

        // ── Esiti ───────────────────────────────────────────────────────────────

        protected virtual void OnTimerExpired()
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

            if (pct >= 100f && CompletesAtHundred)
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

            // QB (Rev BN) — l'interazione sgancia input, ferma le proprie routine e
            // spegne la propria UI. Chiamata PRIMA di spegnere rootCanvas: su
            // Stabilizzazione Ing. e Aggancio rootCanvas è il GO stesso del
            // componente → StopCoroutine avviene a GO ancora attivo (come pre-BN).
            _interaction?.Disable();

            if (_statusRoutine != null) StopCoroutine(_statusRoutine);

            if (rootCanvas != null) rootCanvas.SetActive(false);
        }

        // ── Debug helpers (standard Rev BA — protected per condivisione derivati) ──

        protected void LogV(string msg) { if (logVerbose) Debug.Log(msg); }
        protected void LogVWarn(string msg) { if (logVerbose) Debug.LogWarning(msg); }
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
            GUILayout.Label(_interaction != null ? _interaction.DebugLine : "Interazione: —");
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