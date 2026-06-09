using System;
using Unity.Netcode;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// InventorySystem — Milestone 2
    /// Gestisce le quantità di tutti i materiali a bordo.
    ///
    /// RESPONSABILITÀ:
    ///   - Traccia 10 tipi di item (6 Engineering + 4 Medical)
    ///   - Ogni quantità è una NetworkVariable (server authority, tutti leggono)
    ///   - Espone TryConsume / AddItem / GetQuantity / HasEnough
    ///   - Notifica l'UI via evento statico OnQuantityChanged
    ///
    /// API:
    ///   GetQuantity(ItemType)             → int  — tutti i client
    ///   HasEnough(ItemType, int)          → bool — tutti i client
    ///   TryConsume(ItemType, int)         → bool — SERVER ONLY
    ///   AddItem(ItemType, int)            → void — da qualsiasi client (RPC)
    ///
    /// CONSUMERS:
    ///   PropulsionSystem  → TryConsume(FuelCell, 1) ogni tick fuel
    ///   RepairMinigame    → HasEnough() per "AVVIA" · TryConsume() al completamento soglia
    ///   MedicalDashboardUI / InventoryDashboardUI → GetQuantity() + OnQuantityChanged
    ///
    /// ⚠️ DIPENDE DA: nessuno (standalone).
    /// </summary>
    public class InventorySystem : NetworkBehaviour
    {
        // ── Singleton & OnInstanceReady ──────────────────────────────────
        public static InventorySystem Instance { get; private set; }
        public static event Action OnInstanceReady;

        // ── NetworkVariables (una per ItemType) ──────────────────────────
        // Dichiarate individualmente: NGO non serializza array di NetworkVariable.
        // I nomi corrispondono esattamente all'enum ItemType per leggibilità.

        private readonly NetworkVariable<int> _qtyMechanicalPart =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _qtyWireBundle =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _qtyElectronicComponent =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _qtyHullPlate =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _qtyCoolantCanister =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _qtyFuelCell =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _qtyMedkitBase =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _qtyMedkitAdvanced =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _qtyO2EmergencyTank =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _qtyAntidote =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ── Catalog ──────────────────────────────────────────────────────
        [Header("Item Catalog (tutti e 10 gli ItemData)")]
        [Tooltip("Assegna un InventoryItemData per ogni ItemType. L'ordine non conta.")]
        [SerializeField] private InventoryItemData[] itemCatalog;

        // ── Quantità iniziali (test/debug) ───────────────────────────────
        [Header("Starting Quantities (solo Host/Server)")]
        [SerializeField] private int startMechanicalParts      = 10;
        [SerializeField] private int startWireBundles          = 8;
        [SerializeField] private int startElectronicComponents = 5;
        [SerializeField] private int startHullPlates           = 3;
        [SerializeField] private int startCoolantCanisters     = 4;
        [SerializeField] private int startFuelCells            = 20;
        [SerializeField] private int startMedkitBase           = 5;
        [SerializeField] private int startMedkitAdvanced       = 2;
        [SerializeField] private int startO2EmergencyTanks     = 3;
        [SerializeField] private int startAntidote             = 2;

        // ── Evento pubblico (tutti i client) ─────────────────────────────
        /// <summary>
        /// Fired su tutti i client quando una quantità cambia.
        /// Parametri: tipo item, nuova quantità.
        /// </summary>
        public static event Action<ItemType, int> OnQuantityChanged;

        // ── Lifecycle NGO ─────────────────────────────────────────────────
        public override void OnNetworkSpawn()
        {
            Instance = this;

            // Sottoscrivi con metodi nominati (non lambda) per poter
            // de-sottoscrivere correttamente in OnNetworkDespawn.
            _qtyMechanicalPart.OnValueChanged      += OnMechanicalPartChanged;
            _qtyWireBundle.OnValueChanged           += OnWireBundleChanged;
            _qtyElectronicComponent.OnValueChanged  += OnElectronicComponentChanged;
            _qtyHullPlate.OnValueChanged            += OnHullPlateChanged;
            _qtyCoolantCanister.OnValueChanged      += OnCoolantCanisterChanged;
            _qtyFuelCell.OnValueChanged             += OnFuelCellChanged;
            _qtyMedkitBase.OnValueChanged           += OnMedkitBaseChanged;
            _qtyMedkitAdvanced.OnValueChanged       += OnMedkitAdvancedChanged;
            _qtyO2EmergencyTank.OnValueChanged      += OnO2EmergencyTankChanged;
            _qtyAntidote.OnValueChanged             += OnAntidoteChanged;

            if (IsServer)
                InitStartingQuantities();

            OnInstanceReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            _qtyMechanicalPart.OnValueChanged      -= OnMechanicalPartChanged;
            _qtyWireBundle.OnValueChanged           -= OnWireBundleChanged;
            _qtyElectronicComponent.OnValueChanged  -= OnElectronicComponentChanged;
            _qtyHullPlate.OnValueChanged            -= OnHullPlateChanged;
            _qtyCoolantCanister.OnValueChanged      -= OnCoolantCanisterChanged;
            _qtyFuelCell.OnValueChanged             -= OnFuelCellChanged;
            _qtyMedkitBase.OnValueChanged           -= OnMedkitBaseChanged;
            _qtyMedkitAdvanced.OnValueChanged       -= OnMedkitAdvancedChanged;
            _qtyO2EmergencyTank.OnValueChanged      -= OnO2EmergencyTankChanged;
            _qtyAntidote.OnValueChanged             -= OnAntidoteChanged;

            if (Instance == this) Instance = null;
        }

        // ── Handler OnValueChanged ────────────────────────────────────────
        // Un metodo per item: necessario per la corretta de-sottoscrizione.

        private void OnMechanicalPartChanged(int _, int v)      => FireChanged(ItemType.MechanicalPart, v);
        private void OnWireBundleChanged(int _, int v)          => FireChanged(ItemType.WireBundle, v);
        private void OnElectronicComponentChanged(int _, int v) => FireChanged(ItemType.ElectronicComponent, v);
        private void OnHullPlateChanged(int _, int v)           => FireChanged(ItemType.HullPlate, v);
        private void OnCoolantCanisterChanged(int _, int v)     => FireChanged(ItemType.CoolantCanister, v);
        private void OnFuelCellChanged(int _, int v)            => FireChanged(ItemType.FuelCell, v);
        private void OnMedkitBaseChanged(int _, int v)          => FireChanged(ItemType.MedkitBase, v);
        private void OnMedkitAdvancedChanged(int _, int v)      => FireChanged(ItemType.MedkitAdvanced, v);
        private void OnO2EmergencyTankChanged(int _, int v)     => FireChanged(ItemType.O2EmergencyTank, v);
        private void OnAntidoteChanged(int _, int v)            => FireChanged(ItemType.Antidote, v);

        private static void FireChanged(ItemType type, int qty)
            => OnQuantityChanged?.Invoke(type, qty);

        // ── Inizializzazione ──────────────────────────────────────────────
        private void InitStartingQuantities()
        {
            _qtyMechanicalPart.Value      = Mathf.Max(0, startMechanicalParts);
            _qtyWireBundle.Value          = Mathf.Max(0, startWireBundles);
            _qtyElectronicComponent.Value = Mathf.Max(0, startElectronicComponents);
            _qtyHullPlate.Value           = Mathf.Max(0, startHullPlates);
            _qtyCoolantCanister.Value     = Mathf.Max(0, startCoolantCanisters);
            _qtyFuelCell.Value            = Mathf.Max(0, startFuelCells);
            _qtyMedkitBase.Value          = Mathf.Max(0, startMedkitBase);
            _qtyMedkitAdvanced.Value      = Mathf.Max(0, startMedkitAdvanced);
            _qtyO2EmergencyTank.Value     = Mathf.Max(0, startO2EmergencyTanks);
            _qtyAntidote.Value            = Mathf.Max(0, startAntidote);
        }

        // ── API pubblica ──────────────────────────────────────────────────

        /// <summary>Quantità corrente. Leggibile da qualsiasi client.</summary>
        public int GetQuantity(ItemType type) => NetVarFor(type).Value;

        /// <summary>True se la quantità è sufficiente. Leggibile da qualsiasi client.</summary>
        public bool HasEnough(ItemType type, int amount) => GetQuantity(type) >= amount;

        /// <summary>
        /// Consuma la quantità richiesta. SERVER ONLY.
        /// Restituisce true se il consumo è avvenuto, false se quantità insufficiente.
        /// I sistemi server-authority (PropulsionSystem, RepairMinigame) la chiamano direttamente.
        /// </summary>
        public bool TryConsume(ItemType type, int amount)
        {
            if (!IsServer)
            {
                Debug.LogWarning("[InventorySystem] TryConsume chiamato lato client — " +
                                 "chiedi al server via RPC dal tuo sistema.");
                return false;
            }

            if (amount <= 0) return true; // nessun consumo da fare

            var netVar = NetVarFor(type);
            if (netVar.Value < amount) return false;

            netVar.Value -= amount;
            return true;
        }

        /// <summary>
        /// Aggiunge item. Chiamabile da qualsiasi client — instrada via RPC se necessario.
        /// Usa principalmente in M3 per il loot dai POI.
        /// </summary>
        public void AddItem(ItemType type, int amount)
        {
            if (amount <= 0) return;
            if (IsServer) AddItemInternal(type, amount);
            else          AddItemServerRpc(type, amount);
        }

        [Rpc(SendTo.Server)]
        private void AddItemServerRpc(ItemType type, int amount) => AddItemInternal(type, amount);

        private void AddItemInternal(ItemType type, int amount)
        {
            var netVar = NetVarFor(type);
            netVar.Value = Mathf.Clamp(netVar.Value + amount, 0, GetMaxStack(type));
        }

        /// <summary>Capacità massima per questo item (da ScriptableObject, fallback 99).</summary>
        public int GetMaxStack(ItemType type)
        {
            if (itemCatalog != null)
                foreach (var data in itemCatalog)
                    if (data != null && data.itemType == type)
                        return data.maxStack;
            return 99;
        }

        /// <summary>Dati display dell'item (nome, icona, categoria). Null se non assegnato.</summary>
        public InventoryItemData GetItemData(ItemType type)
        {
            if (itemCatalog != null)
                foreach (var data in itemCatalog)
                    if (data != null && data.itemType == type)
                        return data;
            return null;
        }

        // ── Helper: NetworkVariable per ItemType ──────────────────────────
        private NetworkVariable<int> NetVarFor(ItemType type) => type switch
        {
            ItemType.MechanicalPart      => _qtyMechanicalPart,
            ItemType.WireBundle          => _qtyWireBundle,
            ItemType.ElectronicComponent => _qtyElectronicComponent,
            ItemType.HullPlate           => _qtyHullPlate,
            ItemType.CoolantCanister     => _qtyCoolantCanister,
            ItemType.FuelCell            => _qtyFuelCell,
            ItemType.MedkitBase          => _qtyMedkitBase,
            ItemType.MedkitAdvanced      => _qtyMedkitAdvanced,
            ItemType.O2EmergencyTank     => _qtyO2EmergencyTank,
            ItemType.Antidote            => _qtyAntidote,
            _                            => throw new ArgumentOutOfRangeException(
                                               nameof(type), type,
                                               "ItemType non gestito in InventorySystem")
        };

        // ── Debug GUI ─────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 200, 210, 340));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"[Inventory] {(IsServer ? "SERVER" : "CLIENT")}");

            for (int i = 0; i < (int)ItemType.COUNT; i++)
            {
                var t   = (ItemType)i;
                int qty = GetQuantity(t);
                int max = GetMaxStack(t);
                GUILayout.Label($"{t}: {qty}/{max}");
            }

            if (IsServer)
            {
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("+5 Fuel"))  AddItemInternal(ItemType.FuelCell, 5);
                if (GUILayout.Button("-1 Fuel"))  TryConsume(ItemType.FuelCell, 1);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("+3 Wire"))  AddItemInternal(ItemType.WireBundle, 3);
                if (GUILayout.Button("+2 Mech"))  AddItemInternal(ItemType.MechanicalPart, 2);
                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
#endif
    }
}
