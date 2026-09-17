using UnityEngine;
using UnityEngine.UI;
using TMPro;
using SpaceSurvivor.Ship;

/// <summary>
/// WreckDashboardUI — sezione "Relitto" della dashboard Ingegneria (Rev BG - Stage B).
/// Nuovo IDashboardPanel raggiunto da un button dell'hub (ShowSection). Sostituisce i
/// self-test: da qui l'Ingegnere alimenta il relitto e opera il pump.
///
/// Controlli:
///   - Ombelicale ON/OFF -> WreckUmbilical.RequestUmbilicalServerRpc
///   - Pump AVVIA/FERMA Harvest -> WreckOxygenPump.SetPumpModeServerRpc
///   - Supply: mostrato ma DISABILITATO (inerte finche EVA/boarding non esiste)
/// Letture (polling 0.2s): stato ombelicale, residuo relitto, stato pump.
///
/// Selezione/cyan via DashboardSelection (pattern condiviso Stage A). Il button HOME
/// della sezione e cablato in scena su DashboardHubController.ShowHub().
/// </summary>
public class WreckDashboardUI : MonoBehaviour, IDashboardPanel
{
    [Header("Ombelicale")]
    [SerializeField] private Button umbilicalButton;
    [SerializeField] private TextMeshProUGUI umbilicalButtonLabel;
    [SerializeField] private TextMeshProUGUI umbilicalStatusText;

    [Header("Pump")]
    [SerializeField] private Button harvestButton;
    [SerializeField] private TextMeshProUGUI harvestButtonLabel;
    [SerializeField] private TextMeshProUGUI pumpStatusText;
    [Tooltip("Mostrato ma disabilitato: Supply e inerte finche non esiste EVA/boarding.")]
    [SerializeField] private Button supplyButton;

    [Header("Letture")]
    [SerializeField] private TextMeshProUGUI reserveText;

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;

    private bool isOpen;
    private bool _wired;

    // ── IDashboardPanel ────────────────────────────────────────────────────────

    public void Open()
    {
        isOpen = true;
        EnsureWired();
        if (supplyButton != null) supplyButton.interactable = false;

        UpdateReadouts();
        InvokeRepeating(nameof(UpdateReadouts), 0f, 0.2f);

        DashboardSelection.SetInitial(this, ChooseInitialSelection, logVerbose);
    }

    public void Close()
    {
        isOpen = false;
        CancelInvoke(nameof(UpdateReadouts));
    }

    // ── Selezione EventSystem (cyan) — pattern condiviso DashboardSelection ──

    private void Update()
    {
        if (isOpen) DashboardSelection.EnsureSafety(this, ChooseInitialSelection, logVerbose);
    }

    private GameObject ChooseInitialSelection()
        => DashboardSelection.FirstInteractableSelectable(transform);

    // ── Wiring button ──────────────────────────────────────────────────────────

    private void EnsureWired()
    {
        if (_wired) return;
        if (umbilicalButton != null) umbilicalButton.onClick.AddListener(OnUmbilicalClicked);
        if (harvestButton != null) harvestButton.onClick.AddListener(OnHarvestClicked);
        _wired = true;
    }

    private void OnDestroy()
    {
        if (umbilicalButton != null) umbilicalButton.onClick.RemoveListener(OnUmbilicalClicked);
        if (harvestButton != null) harvestButton.onClick.RemoveListener(OnHarvestClicked);
    }

    private void OnUmbilicalClicked()
    {
        var u = WreckUmbilical.Instance;
        if (u == null) return;
        u.RequestUmbilicalServerRpc(!u.IsRequested);
    }

    private void OnHarvestClicked()
    {
        var p = WreckOxygenPump.Instance;
        if (p == null) return;
        var next = (p.Mode == WreckPumpMode.Harvest) ? WreckPumpMode.Off : WreckPumpMode.Harvest;
        p.SetPumpModeServerRpc(next);
    }

    // ── Letture (polling) ──────────────────────────────────────────────────────

    private void UpdateReadouts()
    {
        var u = WreckUmbilical.Instance;
        var p = WreckOxygenPump.Instance;
        bool powered = u != null && u.IsPowered;

        // Ombelicale
        if (umbilicalStatusText != null)
        {
            string s;
            if (u == null) s = "OMBELICALE: N/D";
            else if (!u.IsRequested) s = "OMBELICALE: SPENTO";
            else if (u.IsPowered) s = "OMBELICALE: ATTIVO";
            else s = "OMBELICALE: IN ATTESA"; // richiesto ma nessun relitto ancorato o sheddato
            umbilicalStatusText.text = s;
        }
        if (umbilicalButtonLabel != null)
            umbilicalButtonLabel.text = (u != null && u.IsRequested) ? "SPEGNI OMBELICALE" : "ACCENDI OMBELICALE";

        // Residuo relitto
        if (reserveText != null)
        {
            var r = (u != null) ? u.CurrentReserve : null;
            reserveText.text = (r != null)
                ? $"RESIDUO RELITTO: {r.Residual:F0} / {r.InitialResidual:F0}"
                : "RESIDUO RELITTO: nessun relitto";
        }

        // Pump
        if (harvestButton != null) harvestButton.interactable = powered; // Q0-a: serve ombelicale attivo
        if (harvestButtonLabel != null)
            harvestButtonLabel.text = (p != null && p.Mode == WreckPumpMode.Harvest) ? "FERMA HARVEST" : "AVVIA HARVEST";

        if (pumpStatusText != null)
        {
            string s;
            if (p == null) s = "PUMP: N/D";
            else if (p.Mode == WreckPumpMode.Harvest) s = powered ? "PUMP: HARVEST attivo" : "PUMP: HARVEST (in attesa alimentazione)";
            else if (p.Mode == WreckPumpMode.Supply) s = "PUMP: SUPPLY (non disponibile)";
            else s = "PUMP: OFF";
            pumpStatusText.text = s;
        }

        if (supplyButton != null) supplyButton.interactable = false; // inerte, sempre
    }
}
