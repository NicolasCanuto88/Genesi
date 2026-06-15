using UnityEngine;
using UnityEngine.InputSystem;

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
        
        panelTest.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        
    }

    public void exitPanelTest()
    {
        panelTest.SetActive(false);
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}
