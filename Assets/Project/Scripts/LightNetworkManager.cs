using UnityEngine;
using Unity.Netcode;
using Unity.Collections;
using System.Collections.Generic;

/// <summary>
/// Gestisce la sincronizzazione dello stato Manual di tutte le ShipLight (NGO v2).
/// Un singolo NetworkObject invece di uno per ogni luce — più efficiente con N luci.
///
/// Le ShipLight si registrano in Start() e ricevono un indice.
/// Il server aggiorna netManualStates quando la dashboard toglia una luce.
/// Tutti i client leggono la NetworkList e aggiornano il rendering locale.
/// </summary>
public class LightNetworkManager : NetworkBehaviour
{
    public static LightNetworkManager Instance { get; private set; }
    public static event System.Action OnInstanceReady;

    // NetworkList: indice = posizione della luce, valore = stato manual (true = on)
    private NetworkList<bool> netManualStates;

    // Registro locale delle luci (solo per lookp rapido)
    private readonly List<ShipLight> registeredLights = new List<ShipLight>();

    private void Awake()
    {
        // NetworkList va inizializzato in Awake, non OnNetworkSpawn
        netManualStates = new NetworkList<bool>();
    }

    public override void OnNetworkSpawn()
    {
        if (Instance == null)
            Instance = this;
        else
        {
            Destroy(gameObject);
            return;
        }

        netManualStates.OnListChanged += OnManualStatesChanged;
        OnInstanceReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        netManualStates.OnListChanged -= OnManualStatesChanged;
        if (Instance == this) Instance = null;
    }

    // ===== Registrazione luci =====

    /// <summary>
    /// Registra una ShipLight e restituisce il suo indice nella NetworkList.
    /// Chiamato da ShipLight.Start() — solo il server aggiunge alla lista.
    /// </summary>
    public int RegisterLight(ShipLight light, bool initialManualState)
    {
        int index = registeredLights.Count;
        registeredLights.Add(light);

        if (IsServer)
            netManualStates.Add(initialManualState);

        return index;
    }

    public void UnregisterLight(int index)
    {
        // Non rimuoviamo dalla lista per non invalidare gli indici delle altre luci.
        // Segniamo come null nel registro locale.
        if (index >= 0 && index < registeredLights.Count)
            registeredLights[index] = null;
    }

    // ===== API pubblica =====

    /// <summary>
    /// Imposta lo stato manual di una luce. Chiamabile da qualsiasi client.
    /// </summary>
    public void SetManualState(int lightIndex, bool isOn)
    {
        if (IsServer)
            SetManualStateInternal(lightIndex, isOn);
        else
            SetManualStateRpc(lightIndex, isOn);
    }

    [Rpc(SendTo.Server)]
    private void SetManualStateRpc(int lightIndex, bool isOn)
    {
        SetManualStateInternal(lightIndex, isOn);
    }

    private void SetManualStateInternal(int lightIndex, bool isOn)
    {
        if (lightIndex < 0 || lightIndex >= netManualStates.Count) return;
        netManualStates[lightIndex] = isOn;
    }

    /// <summary>
    /// Legge lo stato manual corrente di una luce (safe da tutti i client).
    /// </summary>
    public bool GetManualState(int lightIndex)
    {
        if (lightIndex < 0 || lightIndex >= netManualStates.Count) return true;
        return netManualStates[lightIndex];
    }

    // ===== Callback NetworkList =====

    private void OnManualStatesChanged(NetworkListEvent<bool> changeEvent)
    {
        // Aggiorna la luce corrispondente all'indice modificato
        int index = changeEvent.Index;
        if (index >= 0 && index < registeredLights.Count)
        {
            ShipLight light = registeredLights[index];
            if (light != null)
                light.OnNetworkManualStateChanged(changeEvent.Value);
        }
    }
}
