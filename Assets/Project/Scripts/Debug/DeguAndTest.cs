using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// DeguAndTest — pannello di test aperto dall'azione "Debug" (PlayerController.OnDebug).
///
/// Rev BT-a (Q74-a): l'azione "Debug" non ha più binding (prima era lo spazio) e OnDebug gira
/// solo in Editor e Development Build. In scena (Game.unity, GameObject "DebugAndTest") il campo
/// panelTest non è assegnato: panel() e exitPanelTest() ora lo controllano invece di lanciare una
/// NullReferenceException. Guardia commentata invece di rimozione (convenzione di progetto).
/// </summary>
public class DeguAndTest : MonoBehaviour
{
    [SerializeField] GameObject panelTest;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {

    }

    public void panel()
    {
        // Rev BT-a: nessun pannello assegnato → niente da aprire (e il cursore resta bloccato).
        if (panelTest == null)
        {
            Debug.LogWarning("[DeguAndTest] panelTest non assegnato: pannello di test non disponibile.");
            return;
        }

        panelTest.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

    }

    public void exitPanelTest()
    {
        if (panelTest == null) return;   // Rev BT-a

        panelTest.SetActive(false);
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}