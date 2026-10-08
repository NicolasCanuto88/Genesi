using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using SpaceSurvivor.Ship;

/// <summary>
/// MedicalDashboardUI — Milestone 2 · Rev BW-a
/// Monitor unico della Medical Station. Tre sezioni (GDD §9.4).
///
/// SEZIONE A — Stato equipaggio:
///   HP real-time per ogni membro connesso, da PlayerHealthSystem.TryGetByClientId, enumerando
///   NetworkManager.ConnectedClientsIds (stesso pattern di ShipTabUI.RefreshCrewList) su slot fissi
///   (crewEntries). Etichetta (Rev BW-a · Q110-a): "You · ruolo" per il client locale, il ruolo per
///   gli altri (CrewRoles.ToDisplayName, in italiano: debito di localizzazione già noto); senza
///   ruolo "You" / "Crew id".
///
/// SEZIONE B — O₂ &amp; Life Support:
///   Dati reali da OxygenSystem: livello, bilancio, autonomia stimata, badge di stato.
///
/// SEZIONE C — Scorte mediche della STIVA (Rev BW-a · Q107-a):
///   Griglia generata a runtime da UNA cella modello in scena (supplyCellTemplate, spenta, figlia di
///   supplyGrid): una cella per voce di supplyOrder, con un TextMeshProUGUI ("voce  n/max") e una
///   SciFiSegmentedBar trovati tra i figli. Il massimo viene da InventorySystem.GetMaxStack: dal BW-a
///   ogni voce medica ha il suo InventoryItemData (Q108-a: 5 per le sei voci nuove).
///   Aggiornamento a eventi da InventorySystem.OnQuantityChanged (callback delle NetworkVariable,
///   quindi su tutti i client). Il monitor mostra la stiva della nave, non il kit personale
///   (PlayerMedKit): cala quando qualcuno si rifornisce all'armadietto medico.
///
/// REV BW-a — PERCHÉ IL MONITOR ERA FERMO: i riferimenti serializzati puntavano a un layout spento
///   in scena (Panel_Background/Scroll View); quello visibile (Panel_Background/Content) non era
///   collegato e mostrava i testi segnaposto dell'Editor. Fermi quindi equipaggio, O₂ e scorte, da
///   almeno fine agosto. La correzione è in Editor (ricollegamento); la griglia generata riduce i
///   riferimenti della sezione C da otto a due.
///
/// VISIBILITÀ (Rev BW-a · Q109-a): MedicalStation spegne il monitor in Awake, come
///   EngineeringStation, e lo accende solo a chi si siede. Start gira quindi alla prima seduta:
///   iscrizioni e generazione delle celle sono idempotenti.
///
/// Testi in inglese (Rev BW-a · Q110-a). Pattern Open()/Close() via IDashboardPanel — chiamato da
/// MedicalStation.
/// </summary>
public class MedicalDashboardUI : MonoBehaviour, IDashboardPanel
{
    // ── SEZIONE A — Equipaggio ────────────────────────────────────────────────

    [Header("Sezione A — Crew HP (reale, PlayerHealthSystem)")]
    [Tooltip("Un elemento per ogni slot crew (massimo 5), dal layout VISIBILE " +
             "(Panel_Background/Content/Crew). Gli slot non usati si spengono da soli.")]
    [SerializeField] private CrewHPEntry[] crewEntries;

    // ── SEZIONE B — O₂ & Life Support ────────────────────────────────────────

    [Header("Sezione B — O₂ & Life Support")]
    [SerializeField] private SciFiSegmentedBar o2Bar;
    [SerializeField] private TextMeshProUGUI o2LevelText;
    [SerializeField] private TextMeshProUGUI o2RateText;
    [SerializeField] private TextMeshProUGUI o2AutonText;
    [SerializeField] private TextMeshProUGUI o2StatusBadge;
    [SerializeField] private TextMeshProUGUI lifeSupportBadge;

    // ── SEZIONE C — Scorte mediche (Rev BW-a · Q107-a) ────────────────────────

    [Header("Sezione C — Medical Supplies (Rev BW-a, griglia generata)")]
    [Tooltip("Contenitore della griglia (GridLayoutGroup: 2 colonne, riempimento verticale). Le celle " +
             "si generano qui dentro, una per voce di Supply Order.")]
    [SerializeField] private RectTransform supplyGrid;

    [Tooltip("Cella modello, SPENTA, figlia di Supply Grid: un TextMeshProUGUI e una SciFiSegmentedBar " +
             "tra i figli. Viene clonata, mai mostrata.")]
    [SerializeField] private GameObject supplyCellTemplate;

    [Tooltip("Voci della stiva nell'ordine della griglia: prima colonna dall'alto, poi la seconda. " +
             "Solo voci mediche (da MedkitBase in poi); le altre vengono ignorate.")]
    [SerializeField]
    private ItemType[] supplyOrder =
    {
        ItemType.MedkitBase,
        ItemType.MedkitAdvanced,
        ItemType.Antidote,
        ItemType.AntidoteInjector,
        ItemType.O2EmergencyTank,
        ItemType.Adrenaline,
        ItemType.HazmatInjection,
        ItemType.CombatStim,
        ItemType.HealingGrenade,
        ItemType.NanomedicDrone
    };

    // Rev BW-a (Q107-a): le quattro righe fisse sono sostituite dalla griglia generata. Campi tenuti
    // come guardia commentata; i valori ancora serializzati in scena spariscono al primo salvataggio.
    // [SerializeField] private SciFiSegmentedBar medkitBasicBar;
    // [SerializeField] private SciFiSegmentedBar medkitAdvancedBar;
    // [SerializeField] private SciFiSegmentedBar o2TankBar;
    // [SerializeField] private SciFiSegmentedBar antidoteBar;
    // [SerializeField] private TextMeshProUGUI medkitBasicText;
    // [SerializeField] private TextMeshProUGUI medkitAdvancedText;
    // [SerializeField] private TextMeshProUGUI o2TankText;
    // [SerializeField] private TextMeshProUGUI antidoteText;

    // ── COLORI ────────────────────────────────────────────────────────────────

    [Header("Status Colors")]
    [SerializeField] private Color colorOnline = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorWarning = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);
    [SerializeField] private Color colorOffline = new Color(0.5f, 0.5f, 0.5f);

    // ── RIFERIMENTI SISTEMI ───────────────────────────────────────────────────

    private OxygenSystem oxygenSystem;
    private InventorySystem inventorySystem;
    private bool inventorySubscribed;

    // ── CELLE GENERATE (Rev BW-a) ─────────────────────────────────────────────

    private struct SupplyCell
    {
        public ItemType Type;
        public TextMeshProUGUI Label;
        public SciFiSegmentedBar Bar;
    }

    private readonly List<SupplyCell> supplyCells = new List<SupplyCell>();
    private bool supplyCellsBuilt;

    // ── LIFECYCLE ─────────────────────────────────────────────────────────────

    private void Start()
    {
        EnsureSupplyCells();

        // OxygenSystem
        if (OxygenSystem.Instance != null)
            oxygenSystem = OxygenSystem.Instance;
        else
            OxygenSystem.OnInstanceReady += OnOxygenReady;

        // InventorySystem
        if (InventorySystem.Instance != null)
            ConnectInventory();
        else
            InventorySystem.OnInstanceReady += OnInventoryReady;

        UpdateCrewSection();
        UpdateMedicalSupplies();
    }

    private void OnDestroy()
    {
        OxygenSystem.OnInstanceReady -= OnOxygenReady;
        InventorySystem.OnInstanceReady -= OnInventoryReady;
        InventorySystem.OnQuantityChanged -= OnInventoryQuantityChanged;
        inventorySubscribed = false;
        CancelInvoke(nameof(UpdateUI));
    }

    private void OnOxygenReady()
    {
        OxygenSystem.OnInstanceReady -= OnOxygenReady;
        oxygenSystem = OxygenSystem.Instance;
    }

    private void OnInventoryReady()
    {
        InventorySystem.OnInstanceReady -= OnInventoryReady;
        ConnectInventory();
    }

    /// <summary>
    /// Rev BW-a — idempotente: Start (alla prima seduta) e Open possono chiamarla entrambi senza
    /// iscriversi due volte all'evento.
    /// </summary>
    private void ConnectInventory()
    {
        inventorySystem = InventorySystem.Instance;

        if (!inventorySubscribed)
        {
            InventorySystem.OnQuantityChanged += OnInventoryQuantityChanged;
            inventorySubscribed = true;
        }

        UpdateMedicalSupplies();
    }

    private void OnInventoryQuantityChanged(ItemType type, int _)
    {
        // Solo voci mediche, e solo le celle di quella voce.
        if (type < ItemType.MedkitBase || type >= ItemType.COUNT) return;

        EnsureSupplyCells();
        for (int i = 0; i < supplyCells.Count; i++)
        {
            if (supplyCells[i].Type == type)
                UpdateSupplyCell(supplyCells[i]);
        }
    }

    // ── OPEN / CLOSE ──────────────────────────────────────────────────────────

    public void Open()
    {
        EnsureSupplyCells();

        if (oxygenSystem == null && OxygenSystem.Instance != null)
            oxygenSystem = OxygenSystem.Instance;

        if (InventorySystem.Instance != null && (inventorySystem == null || !inventorySubscribed))
            ConnectInventory();

        UpdateUI();
        UpdateMedicalSupplies();

        // Rev BW-a: niente doppio ciclo se Open arriva due volte senza Close.
        CancelInvoke(nameof(UpdateUI));
        InvokeRepeating(nameof(UpdateUI), 0f, 0.2f);
    }

    public void Close()
    {
        CancelInvoke(nameof(UpdateUI));
    }

    // ── UPDATE UI ─────────────────────────────────────────────────────────────

    private void UpdateUI()
    {
        UpdateO2Section();
        UpdateCrewSection();
    }

    // ── SEZIONE B — O₂ ───────────────────────────────────────────────────────

    private void UpdateO2Section()
    {
        if (oxygenSystem == null) return;

        float level = oxygenSystem.O2Level;
        float percent = oxygenSystem.O2Percentage;
        float netRate = oxygenSystem.NetRatePerMinute;
        float genRate = oxygenSystem.GenerationRatePerMinute;

        if (o2Bar != null) o2Bar.SetValue(percent);

        if (o2LevelText != null)
            o2LevelText.text = $"{level:F1}%";

        if (o2RateText != null)
        {
            string sign = netRate >= 0f ? "+" : "";
            o2RateText.text = $"{sign}{netRate:F1} / min";
            o2RateText.color = netRate >= 0f ? colorOnline : colorCritical;
        }

        if (o2AutonText != null)
            o2AutonText.text = ComputeAutonomy(level, netRate);

        UpdateO2Badge(level, percent);
        UpdateLifeSupportBadge(genRate);
    }

    private void UpdateO2Badge(float level, float percent)
    {
        if (o2StatusBadge == null) return;

        if (oxygenSystem.IsAlarmActive || percent < 0.20f)
            SetBadge(o2StatusBadge, "CRITICAL", colorCritical);
        else if (percent < 0.50f)
            SetBadge(o2StatusBadge, "LOW", colorWarning);
        else
            SetBadge(o2StatusBadge, "NORMAL", colorOnline);
    }

    private void UpdateLifeSupportBadge(float genRate)
    {
        if (lifeSupportBadge == null) return;

        if (genRate <= 0f)
            SetBadge(lifeSupportBadge, "OFFLINE", colorOffline);
        else if (genRate < 2.0f)
            SetBadge(lifeSupportBadge, "DEGRADED", colorWarning);
        else
            SetBadge(lifeSupportBadge, "ONLINE", colorOnline);
    }

    /// <summary>
    /// Rev BW-a: con bilancio non negativo "—" invece di "∞" (il simbolo non è nell'atlante
    /// principale e finiva nell'atlante di fallback).
    /// </summary>
    private string ComputeAutonomy(float currentLevel, float netRatePerMinute)
    {
        if (netRatePerMinute >= 0f) return "Time left: —";
        float minutes = currentLevel / Mathf.Abs(netRatePerMinute);
        if (minutes > 999f) return "Time left: —";
        int mins = Mathf.FloorToInt(minutes);
        int secs = Mathf.FloorToInt((minutes - mins) * 60f);
        return $"Time left: {mins:D2}:{secs:D2}";
    }

    // ── SEZIONE A — CREW (reale, PlayerHealthSystem) ──────────────────────────

    /// <summary>
    /// Popola crewEntries[] con l'HP reale di ogni client connesso, su slot fissi (l'array è già
    /// dimensionato in Inspector). Slot in eccesso spenti.
    /// </summary>
    private void UpdateCrewSection()
    {
        if (crewEntries == null || crewEntries.Length == 0) return;

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            // Nessuna sessione di rete attiva (es. test in Editor senza Host/Client
            // avviato) — fallback minimale per non lasciare il pannello vuoto.
            SetCrewOfflineFallback();
            return;
        }

        var connectedIds = NetworkManager.Singleton.ConnectedClientsIds;
        int count = Mathf.Min(connectedIds.Count, crewEntries.Length);

        for (int i = 0; i < count; i++)
        {
            if (crewEntries[i] == null) continue;

            ulong clientId = connectedIds[i];
            bool isLocalPlayer = clientId == NetworkManager.Singleton.LocalClientId;
            string label = CrewLabel(clientId, isLocalPlayer);

            if (PlayerHealthSystem.TryGetByClientId(clientId, out var health))
            {
                crewEntries[i].SetData(label, health.CurrentHP, health.MaxHP, colorOnline);
            }
            else
            {
                // PlayerHealthSystem non ancora spawnato per questo client (breve finestra
                // all'avvio sessione). Placeholder HP piena, non un dato reale.
                crewEntries[i].SetData($"{label} (waiting...)", 100f, 100f, colorOffline);
            }

            crewEntries[i].gameObject.SetActive(true);
        }

        for (int i = count; i < crewEntries.Length; i++)
            crewEntries[i]?.gameObject.SetActive(false);
    }

    /// <summary>
    /// Rev BW-a (Q110-a): "You · ruolo" per il client locale, il ruolo per gli altri. Senza ruolo
    /// dichiarato: "You" / "Crew id".
    /// </summary>
    private static string CrewLabel(ulong clientId, bool isLocalPlayer)
    {
        CrewRole role = PlayerCrewRole.GetRole(clientId);
        string roleName = role != CrewRole.None ? CrewRoles.ToDisplayName(role) : null;

        if (isLocalPlayer)
            return roleName != null ? $"You · {roleName}" : "You";

        return roleName ?? $"Crew {clientId}";
    }

    private void SetCrewOfflineFallback()
    {
        for (int i = 0; i < crewEntries.Length; i++)
        {
            if (crewEntries[i] == null) continue;

            if (i == 0)
            {
                crewEntries[i].SetData("No active session", 100f, 100f, colorOffline);
                crewEntries[i].gameObject.SetActive(true);
            }
            else
            {
                crewEntries[i].gameObject.SetActive(false);
            }
        }
    }

    // ── SEZIONE C — MEDICAL SUPPLIES (Rev BW-a) ──────────────────────────────

    /// <summary>
    /// Genera le celle dalla cella modello, una volta sola. Il modello resta spento: GridLayoutGroup
    /// ignora i figli spenti, quindi non occupa posto nella griglia.
    /// </summary>
    private void EnsureSupplyCells()
    {
        if (supplyCellsBuilt) return;
        supplyCellsBuilt = true;

        if (supplyGrid == null || supplyCellTemplate == null)
        {
            Debug.LogWarning("[MedicalDashboardUI] Supply Grid o Supply Cell Template non assegnati: " +
                             "la sezione delle scorte resta vuota.", this);
            return;
        }

        supplyCellTemplate.SetActive(false);

        if (supplyOrder == null) return;

        foreach (ItemType type in supplyOrder)
        {
            if (type < ItemType.MedkitBase || type >= ItemType.COUNT) continue;

            GameObject cell = Instantiate(supplyCellTemplate, supplyGrid, false);
            cell.name = $"Supply_{type}";
            cell.SetActive(true);

            supplyCells.Add(new SupplyCell
            {
                Type = type,
                Label = cell.GetComponentInChildren<TextMeshProUGUI>(true),
                Bar = cell.GetComponentInChildren<SciFiSegmentedBar>(true)
            });
        }

        // La griglia è già attiva: ricalcolo immediato, così il primo frame non mostra celle sovrapposte.
        LayoutRebuilder.ForceRebuildLayoutImmediate(supplyGrid);
    }

    private void UpdateMedicalSupplies()
    {
        EnsureSupplyCells();

        for (int i = 0; i < supplyCells.Count; i++)
            UpdateSupplyCell(supplyCells[i]);
    }

    private void UpdateSupplyCell(SupplyCell cell)
    {
        string itemName = SupplyLabel(cell.Type);

        if (inventorySystem == null)
        {
            // InventorySystem non ancora pronto: nessun numero inventato.
            if (cell.Bar != null) cell.Bar.SetValue(0f);
            if (cell.Label != null)
            {
                cell.Label.text = $"{itemName}  —";
                cell.Label.color = colorOffline;
            }
            return;
        }

        SetSupplyEntry(cell.Bar, cell.Label, itemName,
            inventorySystem.GetQuantity(cell.Type),
            inventorySystem.GetMaxStack(cell.Type));
    }

    private void SetSupplyEntry(SciFiSegmentedBar bar, TextMeshProUGUI label,
                                 string name, int current, int max)
    {
        if (bar != null)
            bar.SetValue(max > 0 ? (float)current / max : 0f);

        if (label != null)
        {
            label.text = $"{name}  {current}/{max}";
            label.color = current == 0
                ? colorOffline
                : current < max * 0.2f
                    ? colorCritical
                    : current < max * 0.5f
                        ? colorWarning
                        : colorOnline;
        }
    }

    /// <summary>Rev BW-a — nomi in inglese, coerenti con i testi del kit (PlayerMedKit).</summary>
    private static string SupplyLabel(ItemType type)
    {
        switch (type)
        {
            case ItemType.MedkitBase: return "Medkit";
            case ItemType.MedkitAdvanced: return "Advanced Medkit";
            case ItemType.O2EmergencyTank: return "O2 Tank";
            case ItemType.Antidote: return "Antidote";
            case ItemType.Adrenaline: return "Adrenaline";
            case ItemType.HazmatInjection: return "Hazmat";
            case ItemType.CombatStim: return "Combat Stim";
            case ItemType.HealingGrenade: return "Healing Grenade";
            case ItemType.NanomedicDrone: return "Nanomedic Drone";
            case ItemType.AntidoteInjector: return "Antidote Injector";
            default: return type.ToString();
        }
    }

    // ── UTILITY ───────────────────────────────────────────────────────────────

    private void SetBadge(TextMeshProUGUI badge, string text, Color color)
    {
        if (badge == null) return;
        badge.text = text;
        badge.color = color;
    }
}