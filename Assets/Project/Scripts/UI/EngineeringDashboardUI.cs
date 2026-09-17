using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

/// <summary>
/// Engineering Dashboard UI — Power management interface.
/// Aggiornamento M1B: Slider standard sostituiti con SciFiSegmentedBar.
/// Aggiornamento NGO: PowerManager.Instance cercato via OnInstanceReady.
/// Aggiornamento post-playtest Blocco 2: implementa IDashboardPanel così
/// MonitorSwitcher chiama Open() ogni volta che questo monitor diventa
/// visibile — necessario per re-impostare la selezione EventSystem quando
/// si torna a Monitor 1 dopo essere passati per Monitor 2 o 3. Prima
/// dell'implementazione dell'interfaccia, MonitorSwitcher trovava null
/// via GetComponent&lt;IDashboardPanel&gt;() e non richiamava mai Open()
/// ai cambi di monitor: la selezione, lasciata su un CanvasGroup ormai
/// non-interattivo, rimaneva "morta" al ritorno su Monitor 1.
/// </summary>
public class EngineeringDashboardUI : MonoBehaviour, IDashboardPanel
{
    [Header("Power Status Display")]
    [SerializeField] private TextMeshProUGUI powerGenerationText;
    [SerializeField] private TextMeshProUGUI powerConsumptionText;
    [SerializeField] private TextMeshProUGUI powerReserveText;
    [SerializeField] private TextMeshProUGUI powerStatusText;

    [SerializeField] private SciFiSegmentedBar powerGenerationBar;
    [SerializeField] private SciFiSegmentedBar powerReserveBar;

    [Header("Blackout Recovery")]
    [SerializeField] private GameObject blackoutPanel;
    [SerializeField] private Button restorePowerButton;
    [SerializeField] private TextMeshProUGUI restorePowerStatusText;

    [Header("Manual Lights Control")]
    [SerializeField] private Transform lightsListParent;
    [SerializeField] private GameObject lightControlPrefab;

    // ── D13, Rev AE — Banner "MOTORI OFFLINE" durante avaria post-impatto ──
    // Il controller polla PropulsionSystem.IsInEngineFailure/Ratio e gestisce
    // autonomamente show/hide + fade + pulse + label. Nessuna logica qui: solo
    // il riferimento inspector per fare in modo che l'istanza appartenga a
    // questo canvas HUD (una per HUD).
    [Header("Engine Failure Banner (D13)")]
    [Tooltip("Riferimento all'istanza di EngineFailureBannerController figlia " +
             "di questo Canvas. Il controller è self-managed: si attiva/disattiva " +
             "da solo in base a PropulsionSystem.IsInEngineFailure.")]
    [SerializeField] private EngineFailureBannerController engineFailureBanner;

    [Header("Debug")]
    [Tooltip("Se true, stampa log informativi di flusso (selezione EventSystem, " +
             "refresh lista luci, toggle luci, ripristini della rete di sicurezza). " +
             "I LogWarning restano sempre attivi. Lasciare OFF in produzione.")]
    [SerializeField] private bool logVerbose = false;

    private PowerManager powerManager;
    private List<LightControlEntry> lightControls = new List<LightControlEntry>();
    private bool isOpen = false;

    private class LightControlEntry
    {
        public ShipLight light;
        public GameObject uiElement;
        public Toggle toggle;
        public TextMeshProUGUI label;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (restorePowerButton != null)
            restorePowerButton.onClick.AddListener(OnRestorePowerClicked);
    }

    private void Start()
    {
        if (PowerManager.Instance != null)
            InitWithPowerManager();
        else
            PowerManager.OnInstanceReady += InitWithPowerManager;
    }

    private void InitWithPowerManager()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;

        // Se powerManager era già stato assegnato (perché Open() è stato
        // chiamato prima e ha trovato PowerManager.Instance già disponibile,
        // caso normale mid-game), NON rifare RefreshLightsList: la lista
        // è già stata popolata correttamente da Open, e ripopolarla qui
        // significherebbe distruggere le luci appena istanziate + ricrearle,
        // sprecando lavoro e producendo un lampeggio visivo. La rete di
        // sicurezza in Update (DashboardSelection.EnsureSafety) recuperava la
        // selezione ma il doppio Refresh restava rumore sotto il tappeto.
        if (powerManager != null) return;

        powerManager = PowerManager.Instance;

        // Solo se siamo arrivati qui perché Open() aveva trovato
        // PowerManager.Instance == null (race condition all'inizio della
        // sessione), ripopoliamo ora. In quel caso Open aveva stampato
        // "powerManager è null — lista vuota" e non aveva istanziato nulla,
        // quindi non ci sono luci "zombie" da distruggere.
        if (isOpen)
        {
            RefreshLightsList();
            DashboardSelection.SetInitial(this, ChooseInitialSelection, logVerbose);
        }
    }

    private void OnDestroy()
    {
        PowerManager.OnInstanceReady -= InitWithPowerManager;
        CancelInvoke(nameof(UpdateUI));
    }

    public void Open()
    {
        isOpen = true;

        // Open() è sempre chiamato dopo Start Host — PowerManager.Instance è già disponibile
        if (powerManager == null)
            powerManager = PowerManager.Instance;

        RefreshLightsList();
        UpdateUI();
        InvokeRepeating(nameof(UpdateUI), 0f, 0.1f);

        // Imposta la selezione iniziale per la navigazione a tasti direzionali.
        // Va fatto DOPO RefreshLightsList (le luci devono esistere per essere
        // selezionabili) e DOPO UpdateUI (che decide se il BlackoutPanel è
        // attivo o meno). Vedi commento su SetInitialSelection.
        DashboardSelection.SetInitial(this, ChooseInitialSelection, logVerbose);
    }

    public void Close()
    {
        isOpen = false;
        CancelInvoke(nameof(UpdateUI));
    }

    // ── Aggiornamento UI ─────────────────────────────────────────────────────

    private void UpdateUI()
    {
        if (powerManager == null) return;

        // — Generazione —
        if (powerGenerationText != null)
            powerGenerationText.text =
                $"{powerManager.CurrentPowerGeneration:F0}W / {powerManager.MaxPowerOutput:F0}W";

        if (powerGenerationBar != null)
            powerGenerationBar.SetValue(
                powerManager.CurrentPowerGeneration / powerManager.MaxPowerOutput);

        // — Consumo —
        if (powerConsumptionText != null)
        {
            float percent = (powerManager.CurrentPowerGeneration > 0)
                ? (powerManager.CurrentPowerConsumption / powerManager.CurrentPowerGeneration) * 100f
                : 0f;
            powerConsumptionText.text =
                $"{powerManager.CurrentPowerConsumption:F0}W ({percent:F0}%)";
        }

        // — Riserva —
        if (powerReserveText != null)
            powerReserveText.text =
                $"Reserve: {powerManager.PowerReservePercentage * 100f:F0}%";

        if (powerReserveBar != null)
            powerReserveBar.SetValue(powerManager.PowerReservePercentage);

        // — Stato testuale —
        if (powerStatusText != null)
        {
            if (powerManager.IsInBlackout)
            {
                powerStatusText.text = "⚠️⚠️⚠️ BLACKOUT ⚠️⚠️⚠️";
                powerStatusText.color = Color.red;
            }
            else if (powerManager.IsInCriticalState)
            {
                powerStatusText.text = "⚠️ CRITICAL POWER";
                powerStatusText.color = Color.yellow;
            }
            else
            {
                powerStatusText.text = "[OK] OPERATIONAL";
                powerStatusText.color = Color.green;
            }
        }

        // — Pannello blackout —
        // La transizione della selezione quando il blackout finisce (dal pulsante
        // Restore, che si disattiva, alla prima luce) e ora coperta dalla rete di
        // sicurezza condivisa: DashboardSelection.EnsureSafety in Update() rileva la
        // selezione non piu valida e la ripristina sul candidato di
        // ChooseInitialSelection (che, senza blackout, e la prima luce).
        if (blackoutPanel != null)
        {
            bool shouldShowBlackout =
                powerManager.IsInBlackout && powerManager.IsBlackoutManualResetNeeded;
            blackoutPanel.SetActive(shouldShowBlackout);
        }

        // — Pulsante restore —
        if (restorePowerButton != null && restorePowerStatusText != null)
        {
            bool canRestore = powerManager.CanRestorePower(out string reason);
            restorePowerButton.interactable = canRestore;
            restorePowerStatusText.text = reason;
            restorePowerStatusText.color = canRestore ? Color.green : Color.red;
        }

        // — Toggle luci —
        foreach (var entry in lightControls)
        {
            if (entry.light == null || entry.toggle == null) continue;

            entry.toggle.SetIsOnWithoutNotify(entry.light.GetManualState());

            if (entry.label != null)
            {
                entry.label.color = entry.light.GetCurrentState() switch
                {
                    ShipLight.LightState.Normal => Color.white,
                    ShipLight.LightState.Emergency => Color.red,
                    ShipLight.LightState.Off => Color.grey,
                    _ => Color.white
                };
            }
        }
    }

    // ── Lista luci ───────────────────────────────────────────────────────────

    private void RefreshLightsList()
    {
        // Distruggi le voci esistenti
        foreach (var entry in lightControls)
        {
            if (entry.uiElement != null)
                Destroy(entry.uiElement);
        }
        lightControls.Clear();

        if (powerManager == null)
        {
            Debug.LogWarning("[EngineeringDashboard] RefreshLightsList: powerManager è null — lista vuota");
            return;
        }

        if (lightsListParent == null || lightControlPrefab == null)
        {
            Debug.LogWarning("[EngineeringDashboard] RefreshLightsList: lightsListParent o lightControlPrefab non assegnati");
            return;
        }

        List<ShipLight> manualLights = powerManager.GetManualLights();

        if (logVerbose)
            Debug.Log($"[EngineeringDashboard] RefreshLightsList: trovate {manualLights.Count} luci Manual");

        foreach (var light in manualLights)
        {
            GameObject entryGO = Instantiate(lightControlPrefab, lightsListParent);

            var entry = new LightControlEntry
            {
                light = light,
                uiElement = entryGO,
                toggle = entryGO.GetComponentInChildren<Toggle>(),
                label = entryGO.GetComponentInChildren<TextMeshProUGUI>()
            };

            if (entry.label != null)
                entry.label.text = $"{light.gameObject.name} - {light.PowerConsumption:F0}W";

            if (entry.toggle != null)
            {
                entry.toggle.isOn = light.GetManualState();

                ShipLight capturedLight = light;
                entry.toggle.onValueChanged.AddListener(
                    (bool isOn) => OnLightToggled(capturedLight, isOn));
            }

            lightControls.Add(entry);
        }
    }

    // ── Selezione iniziale (pattern condiviso DashboardSelection) ─────────────

    // La meccanica (coroutine next-frame, rete di sicurezza, guardie CanvasGroup)
    // vive in DashboardSelection; qui resta solo la SCELTA del candidato.

    private void Update()
    {
        if (isOpen) DashboardSelection.EnsureSafety(this, ChooseInitialSelection, logVerbose);
    }

    /// <summary>
    /// Candidato per la selezione: in blackout il pulsante Restore, altrimenti la
    /// prima luce interactable. Se nessuno dei due, null.
    /// </summary>
    private GameObject ChooseInitialSelection()
    {
        if (blackoutPanel != null && blackoutPanel.activeInHierarchy
            && restorePowerButton != null && restorePowerButton.interactable)
            return restorePowerButton.gameObject;

        if (lightsListParent != null)
        {
            for (int i = 0; i < lightsListParent.childCount; i++)
            {
                var sel = lightsListParent.GetChild(i).GetComponentInChildren<Selectable>();
                if (sel != null && sel.interactable && sel.gameObject.activeInHierarchy)
                    return sel.gameObject;
            }
        }
        return null;
    }

    // ── Handler pulsanti ─────────────────────────────────────────────────────

    private void OnLightToggled(ShipLight light, bool isOn)
    {
        if (light != null)
        {
            light.SetManualState(isOn);
            if (logVerbose)
                Debug.Log($"[EngineeringDashboard] {light.gameObject.name} → {(isOn ? "ON" : "OFF")}");
        }
    }

    private void OnRestorePowerClicked()
    {
        if (powerManager == null) return;

        bool success = powerManager.TryManualPowerRestore();
        Debug.Log(success
            ? "[EngineeringDashboard] Power restored successfully!"
            : "[EngineeringDashboard] Failed to restore power");
    }
}