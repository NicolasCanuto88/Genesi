using UnityEngine;
using SpaceSurvivor.Ship;
using SpaceSurvivor.Ship.Systems;

/// <summary>
/// NavigationSystem — Milestone 3, Blocco 2.
/// Collega FTLDrive.OnJumpComplete a ZoneManager.SetZone(), come previsto
/// dal commento già presente in FTLDrive.cs ("OnJumpComplete → M3:
/// NavigationSystem cambia zona").
///
/// NON è un NetworkBehaviour, e non ha bisogno di esserlo:
///   FTLDrive.OnJumpComplete è un evento C# statico locale, invocato
///   ESCLUSIVAMENTE dall'istanza che esegue davvero la coroutine di salto
///   (JumpRoutine) — cioè solo lato server/host, perché
///   TryInitiateJumpInternal gira dietro "if (IsServer) ... else Rpc";
///   su un client puro la coroutine — e quindi l'evento — non esiste mai
///   localmente. Un MonoBehaviour semplice che si iscrive senza alcun
///   controllo IsServer è quindi già corretto: su host fa il suo lavoro,
///   su client puro l'evento semplicemente non scatta mai e lo script resta
///   inerte. ZoneManager.SetZone(), chiamato da qui, fa già la sua propria
///   verifica IsServer-o-Rpc internamente (vedi ZoneManager.cs).
///
/// SELEZIONE ZONA — placeholder dichiarato:
///   Il GDD non definisce ancora un algoritmo di selezione della
///   destinazione (dipende dal vero sistema di esplorazione/POI, Blocco 3).
///   Per ora: progressione ciclica Inner → Frontier → DeepVoid → Inner...
///   ad ogni salto completato, puramente per avere QUALCOSA di osservabile
///   e testabile (cambio EM, cambio disponibilità autopilota in DeepVoid,
///   ecc.) prima che esista un vero sistema di destinazioni.
///   ⚠️ Da sostituire quando esisterà un target di salto reale — dipende
///   da: Scanner/POI (Blocco 3).
///
/// COLLOCAZIONE IN SCENA:
///   Nessun vincolo — non è un NetworkObject, può stare ovunque in
///   Game.unity (es. come componente aggiuntivo sullo stesso GameObject di
///   ZoneManager, o come GameObject root-level a parte "NavigationSystem").
/// </summary>
public class NavigationSystem : MonoBehaviour
{
    private void OnEnable()
    {
        FTLDrive.OnJumpComplete += HandleJumpComplete;
    }

    private void OnDisable()
    {
        FTLDrive.OnJumpComplete -= HandleJumpComplete;
    }

    private void HandleJumpComplete()
    {
        if (ZoneManager.Instance == null)
        {
            Debug.LogWarning("[NavigationSystem] Salto FTL completato ma ZoneManager " +
                              "non è ancora pronto — zona non aggiornata.");
            return;
        }

        ZoneType next = GetNextZoneCyclic(ZoneManager.Instance.CurrentZone);
        ZoneManager.Instance.SetZone(next);

        Debug.Log($"[NavigationSystem] Salto FTL completato → nuova zona: {next} " +
                  "(progressione ciclica placeholder — dipende da: Scanner/POI, Blocco 3).");
    }

    private static ZoneType GetNextZoneCyclic(ZoneType current) => current switch
    {
        ZoneType.Inner    => ZoneType.Frontier,
        ZoneType.Frontier => ZoneType.DeepVoid,
        ZoneType.DeepVoid => ZoneType.Inner,
        _                 => ZoneType.Inner
    };
}
