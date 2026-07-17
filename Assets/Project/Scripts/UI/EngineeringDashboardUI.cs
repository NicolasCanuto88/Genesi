using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Engineering Dashboard UI — Power management interface.
/// Aggiornamento M1B: Slider standard sostituiti con SciFiSegmentedBar.
/// Aggiornamento NGO: PowerManager.Instance cercato via OnInstanceReady.
/// </summary>
public class EngineeringDashboardUI : MonoBehaviour
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
        powerManager = PowerManager.Instance;

        // Se la dashboard era già aperta quando PowerManager è diventato disponibile,
        // refresha la lista ora che abbiamo il riferimento
        if (isOpen)
            RefreshLightsList();
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
        SetInitialSelection();
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
                powerStatusText.text = "✅ OPERATIONAL";
                powerStatusText.color = Color.green;
            }
        }

        // — Pannello blackout —
        if (blackoutPanel != null)
        {
            bool shouldShowBlackout =
                powerManager.IsInBlackout && powerManager.IsBlackoutManualResetNeeded;
            bool wasShowingBlackout = blackoutPanel.activeSelf;

            blackoutPanel.SetActive(shouldShowBlackout);

            // Se il BlackoutPanel è appena scomparso e la selezione era sul
            // restorePowerButton (ormai disattivato con il padre), riporta la
            // selezione sulla prima luce, altrimenti EventSystem la perderebbe
            // e la navigazione a tasti smetterebbe di rispondere.
            if (wasShowingBlackout && !shouldShowBlackout)
                RestoreSelectionAfterBlackoutEnds();
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

    // ── Selezione iniziale (navigazione a tasti direzionali) ─────────────────

    /// <summary>
    /// Sceglie il primo Selectable su cui posizionare la selezione EventSystem
    /// all'apertura del pannello. In blackout: il pulsante Restore, punto
    /// naturale di partenza in situazione di emergenza. Altrimenti: la prima
    /// luce della lista.
    ///
    /// Perché una coroutine e non l'assegnazione diretta: i prefab delle luci
    /// vengono Instantiate() nello stesso frame in cui viene chiamato Open(),
    /// e Unity può non aver ancora finalizzato l'inizializzazione dei
    /// componenti Selectable/Toggle nello stesso frame. Rimandare al frame
    /// successivo (yield return null) garantisce che i Selectable siano
    /// operativi e possano ricevere la selezione — è il pattern standard per
    /// "seleziona qualcosa che ho appena istanziato". La versione precedente,
    /// che assegnava direttamente subito dopo RefreshLightsList(), funzionava
    /// nel ramo blackout (Restore preesiste ed è sempre pronto) ma falliva nel
    /// ramo luci (la prima luce era appena istanziata), lasciando l'EventSystem
    /// senza selezione — sintomo osservato: "nessuna evidenziazione" con le
    /// frecce, perché senza Selected non c'è punto di partenza da cui muoversi.
    /// </summary>
    private void SetInitialSelection()
    {
        StartCoroutine(SetInitialSelectionNextFrame());
    }

    private IEnumerator SetInitialSelectionNextFrame()
    {
        // Aspetta un frame: Instantiate() istanzia il GameObject nello stesso
        // frame ma alcuni componenti finalizzano solo dopo. Un yield è
        // sufficiente — non serve WaitForEndOfFrame né più frame.
        yield return null;

        if (EventSystem.current == null)
        {
            Debug.LogWarning("[EngineeringDashboard] SetInitialSelection: EventSystem.current è null. " +
                             "Verifica di aver aggiunto un EventSystem con InputSystemUIInputModule in Game.unity.");
            yield break;
        }

        GameObject initial = null;
        string reason = "";

        if (blackoutPanel != null && blackoutPanel.activeInHierarchy
            && restorePowerButton != null && restorePowerButton.interactable)
        {
            initial = restorePowerButton.gameObject;
            reason = "blackout attivo → restorePowerButton";
        }
        else if (lightsListParent != null && lightsListParent.childCount > 0)
        {
            // Cerca il primo Selectable nella lista luci (di solito è il Toggle
            // del primo LightControlEntry). GetComponentInChildren risale la
            // gerarchia del prefab e trova il Toggle anche se è nidificato.
            var firstEntry = lightsListParent.GetChild(0);
            var firstSelectable = firstEntry.GetComponentInChildren<Selectable>();

            if (firstSelectable != null && firstSelectable.interactable)
            {
                initial = firstSelectable.gameObject;
                reason = $"nessun blackout → prima luce ({firstEntry.name})";
            }
            else
            {
                Debug.LogWarning("[EngineeringDashboard] SetInitialSelection: la prima riga luce " +
                                 $"({firstEntry.name}) non ha un Selectable interactable. " +
                                 "Verifica che il prefab LightControlEntry abbia un Toggle " +
                                 "con Interactable=ON.");
            }
        }
        else
        {
            Debug.LogWarning("[EngineeringDashboard] SetInitialSelection: nessun candidato disponibile. " +
                             $"blackoutPanel={(blackoutPanel != null ? blackoutPanel.activeInHierarchy.ToString() : "null")}, " +
                             $"lightsListParent={(lightsListParent != null ? lightsListParent.childCount.ToString() : "null")} figli.");
        }

        if (initial != null)
        {
            EventSystem.current.SetSelectedGameObject(initial);
            Debug.Log($"[EngineeringDashboard] Selezione iniziale impostata: {initial.name} ({reason}).");
        }
    }

    /// <summary>
    /// Chiamato da UpdateUI quando il BlackoutPanel passa da attivo a non
    /// attivo: la selezione era probabilmente sul restorePowerButton,
    /// che è appena stato disattivato con il suo genitore. Riporta la
    /// selezione sulla prima luce così l'utente può continuare a navigare.
    /// </summary>
    private void RestoreSelectionAfterBlackoutEnds()
    {
        if (EventSystem.current == null) return;

        var currentSelected = EventSystem.current.currentSelectedGameObject;
        bool wasOnRestore = restorePowerButton != null
            && currentSelected == restorePowerButton.gameObject;

        if (!wasOnRestore) return; // La selezione era altrove, non ci interessa

        if (lightsListParent != null && lightsListParent.childCount > 0)
            EventSystem.current.SetSelectedGameObject(lightsListParent.GetChild(0).gameObject);
    }

    // ── Handler pulsanti ─────────────────────────────────────────────────────

    private void OnLightToggled(ShipLight light, bool isOn)
    {
        if (light != null)
        {
            light.SetManualState(isOn);
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