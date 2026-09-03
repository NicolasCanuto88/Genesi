using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System.Collections.Generic;
using System.Collections;

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
    [SerializeField] private bool verboseLogging = false;

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
        // sicurezza in Update (EnsureSelectionSafety) recuperava la
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
            SetInitialSelection();
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
                powerStatusText.text = "[OK] OPERATIONAL";
                powerStatusText.color = Color.green;
            }
        }

        // — Pannello blackout —
        if (blackoutPanel != null)
        {
            bool shouldShowBlackout =
                powerManager.IsInBlackout && powerManager.IsBlackoutManualResetNeeded;
            bool wasShowingBlackout = blackoutPanel.activeSelf;

            // IMPORTANTE: la selezione EventSystem deve essere ispezionata
            // PRIMA di disattivare il BlackoutPanel. Se la disattivassimo
            // prima, Unity nello stesso frame azzererebbe automaticamente
            // currentSelectedGameObject (perché il GameObject selezionato
            // — restorePowerButton — sarebbe appena diventato inattivo),
            // e il controllo "era sul Restore?" fallirebbe sempre.
            // Sintomo: dopo aver premuto Restore la navigazione a tasti
            // muore, nessuna voce viene evidenziata.
            bool selectionWasOnRestore = wasShowingBlackout && !shouldShowBlackout
                && EventSystem.current != null
                && restorePowerButton != null
                && EventSystem.current.currentSelectedGameObject == restorePowerButton.gameObject;

            blackoutPanel.SetActive(shouldShowBlackout);

            if (selectionWasOnRestore)
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

        if (verboseLogging)
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
    /// luce della lista. Se non c'è né l'uno né l'altra (caso estremo:
    /// nessuna luce Manual e nessun blackout), non tocca la selezione — Unity
    /// gestisce il caso "nessun Selected" senza errori.
    /// </summary>
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
    /// "seleziona qualcosa che ho appena istanziato".
    ///
    /// Anche se questo primo tentativo fallisce per qualche motivo (Selectable
    /// non ancora interactable, EventSystem in stato transitorio dopo il
    /// SetActive del pannello parent), la rete di sicurezza in Update()
    /// riprova ad ogni frame finché il pannello è visibile — vedi
    /// EnsureSelectionSafety(). L'invariante è "quando il Monitor 1 è
    /// visibile, c'è sempre qualcosa selezionato".
    /// </summary>
    private void SetInitialSelection()
    {
        // Guardia contro chiamate quando il GameObject è disattivato: succede
        // quando MonitorSwitcher.Start() esegue ShowMonitor(defaultMonitorIndex,
        // instant: true) al caricamento della scena, ma EngineeringStation.Awake
        // ha nel frattempo disattivato il pannello (dashboardUI.gameObject.
        // SetActive(false)). StartCoroutine su MonoBehaviour disattivato produce
        // l'errore "Coroutine couldn't be started because the game object is
        // inactive!". Non è un problema funzionale — quando l'utente entra
        // davvero in station, il pannello viene riattivato, Open() viene
        // richiamata, e la coroutine parte correttamente da lì. Ma senza questa
        // guardia si registra un errore rosso in Console al caricamento scena.
        if (!isActiveAndEnabled) return;

        StartCoroutine(SetInitialSelectionNextFrame());
    }

    private IEnumerator SetInitialSelectionNextFrame()
    {
        yield return null;

        if (EventSystem.current == null)
        {
            Debug.LogWarning("[EngineeringDashboard] SetInitialSelection: EventSystem.current è null. " +
                             "Verifica di aver aggiunto un EventSystem con InputSystemUIInputModule in Game.unity.");
            yield break;
        }

        GameObject initial = ChooseInitialSelection(out string reason);

        if (initial != null)
        {
            EventSystem.current.SetSelectedGameObject(initial);
            if (verboseLogging)
                Debug.Log($"[EngineeringDashboard] Selezione iniziale impostata: {initial.name} ({reason}).");
        }
        else
        {
            Debug.LogWarning($"[EngineeringDashboard] SetInitialSelection: nessun candidato disponibile ({reason}).");
        }
    }

    /// <summary>
    /// Restituisce il GameObject candidato per la selezione iniziale, o null
    /// se non ce n'è uno valido in questo momento. Metodo puro — non tocca
    /// nulla. Usato sia da SetInitialSelectionNextFrame che da
    /// EnsureSelectionSafety.
    /// </summary>
    private GameObject ChooseInitialSelection(out string reason)
    {
        if (blackoutPanel != null && blackoutPanel.activeInHierarchy
            && restorePowerButton != null && restorePowerButton.interactable)
        {
            reason = "blackout attivo → restorePowerButton";
            return restorePowerButton.gameObject;
        }

        if (lightsListParent != null && lightsListParent.childCount > 0)
        {
            for (int i = 0; i < lightsListParent.childCount; i++)
            {
                var entry = lightsListParent.GetChild(i);
                var sel = entry.GetComponentInChildren<Selectable>();
                if (sel != null && sel.interactable && sel.gameObject.activeInHierarchy)
                {
                    reason = $"nessun blackout → prima luce interactable ({entry.name})";
                    return sel.gameObject;
                }
            }
            reason = "lista luci presente ma nessuna con Selectable interactable";
            return null;
        }

        reason = $"blackoutPanel={(blackoutPanel != null ? blackoutPanel.activeInHierarchy.ToString() : "null")}, lightsListParent childCount={(lightsListParent != null ? lightsListParent.childCount.ToString() : "null")}";
        return null;
    }

    /// <summary>
    /// Rete di sicurezza per la selezione EventSystem: chiamata ogni frame
    /// dall'Update() finché isOpen. Se il pannello è visibile ma non c'è
    /// alcun Selectable selezionato (o quello che era selezionato è ormai
    /// inattivo/non-interactable), riporta la selezione su un candidato
    /// valido. Copre tutti i casi in cui il primo tentativo di
    /// SetInitialSelection fallisce per timing/state, e più in generale
    /// tutti i casi in cui la selezione va persa (blackout che scompare,
    /// cambio monitor e ritorno, luci ricreate).
    ///
    /// L'approccio è "riparativo" invece di "prescrittivo": non serve
    /// enumerare tutti i casi in cui la selezione può andare persa —
    /// basta rilevare l'assenza e ripristinare. Costo: un
    /// EventSystem.currentSelectedGameObject e al più un GetComponentInChildren
    /// per frame quando isOpen, trascurabile.
    /// </summary>
    private void EnsureSelectionSafety()
    {
        if (!isOpen) return;
        if (EventSystem.current == null) return;

        // Il pannello è effettivamente visibile? Se il CanvasGroup padre ha
        // alpha=0 (siamo su un altro monitor), non toccare — l'utente sta
        // interagendo con quel monitor, la selezione non ci riguarda.
        var cg = GetComponentInParent<CanvasGroup>();
        if (cg != null && (cg.alpha < 0.5f || !cg.interactable)) return;

        var currentSel = EventSystem.current.currentSelectedGameObject;

        // Se c'è già una selezione valida (attiva e interactable) sul nostro
        // pannello, non toccare.
        if (currentSel != null && currentSel.activeInHierarchy)
        {
            var currentSelectable = currentSel.GetComponent<Selectable>();
            if (currentSelectable != null && currentSelectable.interactable)
            {
                // Verifica che sia sotto il nostro pannello — se è altrove
                // (Restore appena disattivato, es), la sostituiamo.
                if (currentSel.transform.IsChildOf(this.transform))
                    return;
            }
        }

        // Nessuna selezione valida: ripristina
        GameObject candidate = ChooseInitialSelection(out string reason);
        if (candidate != null)
        {
            EventSystem.current.SetSelectedGameObject(candidate);
            if (verboseLogging)
                Debug.Log($"[EngineeringDashboard] EnsureSelectionSafety: selezione ripristinata su {candidate.name} ({reason}).");
        }
    }

    private void Update()
    {
        EnsureSelectionSafety();
    }

    /// <summary>
    /// Chiamato da UpdateUI SOLO dopo aver verificato che la selezione era
    /// sul restorePowerButton al momento in cui il BlackoutPanel è passato
    /// da attivo a non attivo. La verifica DEVE essere fatta dal chiamante
    /// prima di disattivare il pannello, altrimenti Unity avrà già azzerato
    /// currentSelectedGameObject e il controllo sarebbe stato inefficace.
    ///
    /// Rimanda la selezione di un frame (stesso pattern di
    /// SetInitialSelection): dopo un SetActive(false) sul pannello parent,
    /// EventSystem può essere in uno stato transitorio per il resto del
    /// frame, e riassegnare la selezione nello stesso frame a volte viene
    /// ignorato silenziosamente. Un yield return null lo risolve.
    /// </summary>
    private void RestoreSelectionAfterBlackoutEnds()
    {
        StartCoroutine(RestoreSelectionAfterBlackoutEndsNextFrame());
    }

    private IEnumerator RestoreSelectionAfterBlackoutEndsNextFrame()
    {
        yield return null;

        if (EventSystem.current == null) yield break;
        if (lightsListParent == null || lightsListParent.childCount == 0) yield break;

        var firstEntry = lightsListParent.GetChild(0);
        var firstSelectable = firstEntry.GetComponentInChildren<Selectable>();

        if (firstSelectable != null && firstSelectable.interactable)
        {
            EventSystem.current.SetSelectedGameObject(firstSelectable.gameObject);
            if (verboseLogging)
                Debug.Log($"[EngineeringDashboard] Selezione trasferita dal Restore alla prima luce ({firstEntry.name}) dopo fine blackout.");
        }
    }

    // ── Handler pulsanti ─────────────────────────────────────────────────────

    private void OnLightToggled(ShipLight light, bool isOn)
    {
        if (light != null)
        {
            light.SetManualState(isOn);
            if (verboseLogging)
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