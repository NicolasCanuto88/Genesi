using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// DashboardSelection — pattern condiviso di selezione EventSystem per i pannelli
/// dashboard (Rev BG - Stage A).
///
/// Estratto da EngineeringDashboardUI, dove viveva la logica "quando il pannello e
/// visibile, c'e sempre qualcosa selezionato" che fa apparire il riquadro cyan
/// - MenuSelectionHighlight segue EventSystem.currentSelectedGameObject. Finche era
/// locale a un solo pannello, il cyan appariva solo li; centralizzandola qui ogni
/// pannello - luci, ship systems, inventory, hub e la futura sezione Relitto - la
/// ottiene chiamando due metodi.
///
/// USO in un IDashboardPanel:
///   Open()   -> DashboardSelection.SetInitial(this, ChooseInitialSelection, logVerbose);
///   Update() -> if (isOpen) DashboardSelection.EnsureSafety(this, ChooseInitialSelection, logVerbose);
///   piu un metodo GameObject ChooseInitialSelection() con la logica di dominio:
///   quale Selectable selezionare per primo - per una sezione di sola lettura,
///   tipicamente il button Home.
///
/// La meccanica - coroutine next-frame, guardie CanvasGroup/attivita, riparazione -
/// e qui; la SCELTA del candidato resta nel pannello perche e dominio-specifica.
/// </summary>
public static class DashboardSelection
{
    /// <summary>
    /// Imposta la selezione iniziale il frame successivo. Da chiamare in Open().
    /// La coroutine e ospitata su 'host', quindi richiede host attivo e abilitato.
    /// </summary>
    public static void SetInitial(MonoBehaviour host, Func<GameObject> chooser, bool logVerbose = false)
    {
        // Guardia: StartCoroutine su un MonoBehaviour disattivato lancia un errore.
        // Succede quando il paging chiama Open() su un pannello il cui GameObject e
        // ancora disattivato al caricamento scena. Non e un problema funzionale:
        // quando il pannello diventa visibile la rete di sicurezza in Update lo copre.
        if (host == null || !host.isActiveAndEnabled) return;
        host.StartCoroutine(SetInitialRoutine(chooser, logVerbose));
    }

    private static IEnumerator SetInitialRoutine(Func<GameObject> chooser, bool logVerbose)
    {
        // I Selectable appena istanziati - es. le voci luci - possono non essere
        // ancora operativi nello stesso frame. Un frame di attesa lo risolve.
        yield return null;

        if (EventSystem.current == null)
        {
            Debug.LogWarning("[DashboardSelection] EventSystem.current e null. " +
                             "Verifica un EventSystem con InputSystemUIInputModule in scena.");
            yield break;
        }

        GameObject initial = chooser != null ? chooser() : null;
        if (initial != null)
        {
            EventSystem.current.SetSelectedGameObject(initial);
            if (logVerbose) Debug.Log("[DashboardSelection] Selezione iniziale: " + initial.name + ".");
        }
        else if (logVerbose)
        {
            Debug.Log("[DashboardSelection] Nessun candidato per la selezione iniziale.");
        }
    }

    /// <summary>
    /// Rete di sicurezza riparativa: se il pannello e visibile ma non c'e una
    /// selezione valida sotto di esso, ripristina la selezione su un candidato.
    /// Da chiamare ogni frame da Update() quando il pannello e aperto. Copre
    /// cambio pagina e ritorno, blackout che scompare, elementi ricreati.
    /// </summary>
    public static void EnsureSafety(MonoBehaviour host, Func<GameObject> chooser, bool logVerbose = false)
    {
        if (host == null || EventSystem.current == null) return;

        // Se il CanvasGroup padre e invisibile o non-interattivo, siamo su un'altra
        // pagina: la selezione non ci riguarda, non toccare.
        var cg = host.GetComponentInParent<CanvasGroup>();
        if (cg != null && (cg.alpha < 0.5f || !cg.interactable)) return;

        var currentSel = EventSystem.current.currentSelectedGameObject;
        if (currentSel != null && currentSel.activeInHierarchy)
        {
            var selectable = currentSel.GetComponent<Selectable>();
            if (selectable != null && selectable.interactable
                && currentSel.transform.IsChildOf(host.transform))
                return; // selezione valida e sotto il nostro pannello: ok
        }

        GameObject candidate = chooser != null ? chooser() : null;
        if (candidate != null)
        {
            EventSystem.current.SetSelectedGameObject(candidate);
            if (logVerbose) Debug.Log("[DashboardSelection] Selezione ripristinata: " + candidate.name + ".");
        }
    }

    /// <summary>
    /// Trova il primo Selectable interactable e attivo sotto 'root'. Chooser di
    /// default per una sezione senza una scelta di dominio particolare: nella
    /// dashboard con hub cade tipicamente sul button Home o sul primo controllo.
    /// Alloca solo quando invocato - e SetInitial/EnsureSafety lo invocano solo
    /// quando serve impostare/ripristinare la selezione, non a regime.
    /// </summary>
    public static GameObject FirstInteractableSelectable(Transform root)
    {
        if (root == null) return null;
        var selectables = root.GetComponentsInChildren<Selectable>(includeInactive: false);
        for (int i = 0; i < selectables.Length; i++)
        {
            var s = selectables[i];
            if (s != null && s.interactable && s.gameObject.activeInHierarchy)
                return s.gameObject;
        }
        return null;
    }
}
