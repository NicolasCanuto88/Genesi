using UnityEngine;

/// <summary>
/// AutoStartHost — Rev Q (Blocco 2 M3, rewrite).
///
/// STORIA:
///   Rev O: avviava automaticamente Host o Client all'avvio della scena —
///   incompatibile con un flusso di menu reale, la sessione di rete
///   partiva prima ancora che il giocatore potesse inserire il proprio
///   nome.
///   Rev P: riscritto per avviare automaticamente SOLO il Client nei
///   clone ParrelSync, bypassando il loro menu (MainMenuManager si
///   auto-disattivava per i clone allo stesso scopo) — comodo per un test
///   rapido a un click, ma il clone non passava mai dal proprio menu:
///   nessuna creazione di un secondo personaggio separato, nessun
///   controllo reale del flusso che un secondo giocatore reale userebbe.
///
/// ORA (Rev Q): il clone ParrelSync è un'istanza del gioco IDENTICA a
///   qualunque altra — passa dal menu come tutti, crea il proprio
///   personaggio, e si unisce manualmente inserendo il join code generato
///   dall'istanza host (stesso flusso "Unisciti" già esistente). Questo
///   script non fa più nulla per nessuna istanza — sia in Editor che in
///   build, l'unico punto di ingresso alla rete è sempre MainMenuManager
///   tramite l'UI del menu.
///
/// Lo script resta come file vuoto, intenzionalmente, invece di essere
/// cancellato: il GameObject "AutoStartHost" in scena (se presente) può
/// restare con questo componente attaccato senza effetto, evitando di
/// dover toccare la scena per rimuoverlo. Può essere eliminato in modo
/// sicuro in qualunque momento, sia lo script che il GameObject.
///
/// ⚠️ REGOLA INVARIANTE: non aggiungere mai qui logica di avvio host/rete,
/// né per l'istanza principale né per i clone ParrelSync. Se serve un
/// percorso rapido per test senza passare dal menu, usare un eventuale
/// debug GUI dedicato (es. su MainMenuManager, dietro #if UNITY_EDITOR) —
/// mai un avvio automatico silenzioso in Start().
/// </summary>
public class AutoStartHost : MonoBehaviour
{
    private void Start()
    {
        // Intenzionalmente vuoto — vedi nota in testa al file.
    }
}