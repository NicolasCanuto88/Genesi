using UnityEngine;
using Unity.Netcode;

#if UNITY_EDITOR
using ParrelSync;
#endif

/// <summary>
/// AutoStartHost — Rev O (Blocco 1 M3, rewrite).
///
/// PRIMA: avviava automaticamente Host (istanza principale Editor/build)
/// o Client (clone ParrelSync) all'avvio della scena. Incompatibile con
/// un flusso di menu reale — tutta la sessione di rete partiva prima ancora
/// che il giocatore potesse inserire il proprio nome.
///
/// ORA: avvia automaticamente SOLO il Client per i clone ParrelSync, così il
/// workflow di test interno in Editor (Host window + clone window) resta
/// identico a prima — il clone si connette automaticamente all'host appena
/// la partita viene creata dal menu. L'istanza principale non tocca la rete:
/// MainMenuManager gestisce Host e Join tramite l'UI del menu.
///
/// In build: questo script non fa assolutamente nulla. MainMenuManager è
/// l'unico punto di ingresso alla rete sia in Editor (istanza principale)
/// sia in build.
///
/// ⚠️ REGOLA INVARIANTE: non aggiungere mai qui logica di avvio host/rete.
/// Se serve un percorso rapido per test senza menu, usare il debug GUI di
/// MainMenuManager (che può esporre un bypass #if UNITY_EDITOR) — mai
/// tornare ad auto-avviare l'host qui.
/// </summary>
public class AutoStartHost : MonoBehaviour
{
    private void Start()
    {
#if UNITY_EDITOR
        if (!ClonesManager.IsClone()) return; // istanza principale: niente

        // Clone ParrelSync: avvia il client automaticamente per il test interno.
        // L'host viene creato dall'istanza principale tramite il menu (MainMenuManager).
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsListening) return;

        NetworkManager.Singleton.StartClient();
        Debug.Log("[AutoStartHost] Clone ParrelSync → StartClient automatico");
#endif
        // In build: niente. MainMenuManager gestisce tutto.
    }
}