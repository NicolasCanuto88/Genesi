using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SpaceSurvivor.Ship;

/// <summary>
/// RepairEntry — Milestone 2
/// Componente da aggiungere a ogni riga della Sezione B (Repair) su
/// ShipSystemsDashboardUI — Monitor 2.
///
/// Pattern identico a CrewHPEntry: una entry per sistema IRepairable
/// (PropulsionSystem, FTLDrive), aggiornata via Refresh() chiamato
/// dal pannello padre durante il polling (0.2s).
///
/// RESPONSABILITÀ:
///   - Mostra nome sistema, stato (ONLINE/DEGRADED/OFFLINE), HP%
///   - Se IsRepairable() == false (sistema ONLINE ≥75%):
///       materialsText → messaggio neutro
///       button → "ONLINE", disabilitato
///   - Se IsRepairable() == true (DEGRADED/OFFLINE):
///       materialsText → TOTALE materiali richiesti per una sessione
///       completa (somma di tutte le soglie 50+75+100 — vedi
///       GetTotalRepairMaterials)
///       button → "AVVIA" se HasMaterialsForFullRepair(), altrimenti
///                "MATERIALI INSUFFICIENTI"
///   - Click su AVVIA → invoca callback onAvviaClicked(target).
///     Il pannello padre mostra "Recati al pannello [SISTEMA]".
///
/// GATE CUMULATIVO (Rev P):
///   "Materiali richiesti" e "disponibilità" sono calcolati tramite
///   IRepairableExtensions (GetTotalRepairMaterials / HasMaterialsForFullRepair)
///   — le STESSE estensioni usate da RepairPanel.CanInteract(). Single source
///   of truth: se questa entry mostra "AVVIA" attivo, il pannello fisico
///   corrispondente è garantito interagibile (e viceversa).
///
/// NOTA: nessun consumo materiali avviene qui. Il consumo reale è gestito
/// da RepairMinigame → RepairPanel.ApplyRepairThresholdRpc() (server authority),
/// soglia per soglia, durante il minigame.
///
/// DIPENDE DA: InventorySystem ✅ (solo lettura GetQuantity/HasEnough)
/// </summary>
public class RepairEntry : MonoBehaviour
{
    [Header("Riferimenti UI")]
    [SerializeField] private TextMeshProUGUI systemNameText;
    [SerializeField] private TextMeshProUGUI stateBadge;
    [SerializeField] private SciFiSegmentedBar healthBar;
    [SerializeField] private TextMeshProUGUI healthText;
    [SerializeField] private TextMeshProUGUI materialsText;
    [SerializeField] private Button avviaButton;
    [SerializeField] private TextMeshProUGUI avviaButtonLabel;

    [Header("Colori Stato")]
    [SerializeField] private Color colorOnline = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorDegraded = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);
    [SerializeField] private Color colorOffline = new Color(0.5f, 0.5f, 0.5f);

    [Header("Colore materiali insufficienti")]
    [Tooltip("Colore hex usato nel rich-text per materiali mancanti. Es. #FF3300")]
    [SerializeField] private string colorMissingHex = "#FF3300";

    [Header("Testi")]
    [Tooltip("Mostrato in materialsText quando il sistema è ONLINE (IsRepairable() == false).")]
    [SerializeField] private string textSystemOperational = "Sistema operativo — nessuna riparazione necessaria";

    [Tooltip("Prefisso mostrato prima dell'elenco materiali totali richiesti.")]
    [SerializeField] private string textTotalRequiredPrefix = "Riparazione completa richiede: ";

    private IRepairable _target;
    private Action<IRepairable> _onAvvia;

    // =========================================================================
    // API PUBBLICA
    // =========================================================================

    /// <summary>
    /// Associa questa entry a un sistema IRepairable e registra il callback
    /// del pulsante AVVIA. Chiamato una volta durante l'inizializzazione
    /// del dashboard (Start / InitWithXxxSystem).
    /// </summary>
    public void Bind(IRepairable target, Action<IRepairable> onAvviaClicked)
    {
        _target = target;
        _onAvvia = onAvviaClicked;

        if (avviaButton != null)
        {
            avviaButton.onClick.RemoveAllListeners();
            avviaButton.onClick.AddListener(HandleAvviaClicked);
        }

        Refresh();
    }

    /// <summary>
    /// Aggiorna stato, HP%, materiali e interactable del pulsante.
    /// Chiamato dal pannello padre durante il polling (InvokeRepeating 0.2s).
    /// </summary>
    public void Refresh()
    {
        if (_target == null)
        {
            gameObject.SetActive(false);
            return;
        }

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);

        if (systemNameText != null)
            systemNameText.text = _target.GetSystemName().ToUpperInvariant();

        ShipSystemState state = _target.GetCurrentState();
        float percent = _target.GetHealthPercent();

        if (healthBar != null) healthBar.SetValue(percent);
        if (healthText != null) healthText.text = $"{percent * 100f:F0}%";

        UpdateStateBadge(state);
        UpdateMaterialsAndButton();
    }

    // =========================================================================
    // STATO
    // =========================================================================

    private void UpdateStateBadge(ShipSystemState state)
    {
        if (stateBadge == null) return;

        switch (state)
        {
            case ShipSystemState.Online:
                stateBadge.text = "ONLINE";
                stateBadge.color = colorOnline;
                break;
            case ShipSystemState.DegradedLight:
                stateBadge.text = "DEGRADATO";
                stateBadge.color = colorDegraded;
                break;
            case ShipSystemState.DegradedHeavy:
                stateBadge.text = "DEGRADATO GRAVE";
                stateBadge.color = colorCritical;
                break;
            case ShipSystemState.Offline:
                stateBadge.text = "OFFLINE";
                stateBadge.color = colorCritical;
                break;
        }
    }

    // =========================================================================
    // MATERIALI + PULSANTE AVVIA
    // =========================================================================

    private void UpdateMaterialsAndButton()
    {
        bool repairable = _target.IsRepairable();

        // ── Sistema ONLINE (≥75% HP) — nessuna riparazione necessaria ──────
        if (!repairable)
        {
            if (materialsText != null)
                materialsText.text = textSystemOperational;

            if (avviaButton != null)
                avviaButton.interactable = false;

            if (avviaButtonLabel != null)
                avviaButtonLabel.text = "ONLINE";

            return;
        }

        // ── Sistema DEGRADED/OFFLINE — mostra TOTALE cumulativo ────────────
        // Stessa estensione usata da RepairPanel.CanInteract() → coerenza garantita.
        RepairMaterialRequirement[] totals = _target.GetTotalRepairMaterials();
        bool materialsAvailable = _target.HasMaterialsForFullRepair();

        if (totals.Length > 0)
        {
            var parts = new List<string>(totals.Length);

            foreach (var req in totals)
            {
                int have = InventorySystem.Instance != null
                    ? InventorySystem.Instance.GetQuantity(req.itemType)
                    : 0;

                bool enough = have >= req.amount;
                string color = enough ? "white" : colorMissingHex;
                parts.Add($"<color={color}>{req.amount}× {ItemDisplayName(req.itemType)}</color> ({have})");
            }

            if (materialsText != null)
                materialsText.text = textTotalRequiredPrefix + string.Join("   ", parts);
        }
        else if (materialsText != null)
        {
            materialsText.text = "Nessun materiale richiesto";
        }

        if (avviaButton != null)
            avviaButton.interactable = materialsAvailable;

        if (avviaButtonLabel != null)
            avviaButtonLabel.text = materialsAvailable ? "AVVIA" : "MATERIALI INSUFFICIENTI";
    }

    private void HandleAvviaClicked()
    {
        if (_target == null) return;
        _onAvvia?.Invoke(_target);
    }

    // =========================================================================
    // HELPER
    // =========================================================================

    /// <summary>
    /// Nome leggibile per ItemType. Solo presentazione UI —
    /// i valori numerici (amount, soglie) vengono sempre da ScriptableObject.
    /// </summary>
    private static string ItemDisplayName(ItemType type) => type switch
    {
        ItemType.MechanicalPart => "Parte Meccanica",
        ItemType.WireBundle => "Fascio di Cavi",
        ItemType.ElectronicComponent => "Componente Elettronico",
        ItemType.HullPlate => "Piastra dello Scafo",
        ItemType.CoolantCanister => "Tanica Refrigerante",
        ItemType.FuelCell => "Cella di Carburante",
        ItemType.MedkitBase => "Medikit Base",
        ItemType.MedkitAdvanced => "Medikit Avanzato",
        ItemType.O2EmergencyTank => "Tanica O₂ Emergenza",
        ItemType.Antidote => "Antidoto",
        _ => type.ToString()
    };
}