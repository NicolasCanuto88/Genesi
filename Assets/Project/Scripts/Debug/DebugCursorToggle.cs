using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine.InputSystem;
#endif

namespace SpaceSurvivor.DebugTools
{
    /// <summary>
    /// DebugCursorToggle — tooling di DEBUG/TEST (solo Editor/Development Build).
    ///
    /// Libera il cursore del sistema operativo per poter cliccare gli overlay
    /// OnGUI dei sistemi (es. i bottoni "-20 HP" / "-50 HP" di PropulsionSystem e
    /// FTLDrive, o "+5 Fuel" di InventorySystem) e poi lo riblocca.
    ///
    /// PERCHÉ REGGE: PlayerController.HandleLook si limita a NON ruotare la camera
    /// quando il cursore è sbloccato (non lo ri-blocca ogni frame), quindi lo
    /// sblocco persiste e la visuale resta ferma mentre clicchi i bottoni.
    ///
    /// NOTA ARCHITETTURALE: usa Keyboard.current, ammesso QUI perché è codice
    /// dev-only sotto #if — non è uno script di gameplay e non entra mai in una
    /// build di release. Per purezza stretta, sostituibile con un InputActionReference.
    ///
    /// USO: aggiungi questo componente a un GameObject qualsiasi nella scena di
    /// test. Premi F1 per liberare/ribloccare il cursore.
    /// </summary>
    public class DebugCursorToggle : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Tooltip("Tasto che libera/riblocca il cursore.")]
        [SerializeField] private Key toggleKey = Key.F1;

        [Tooltip("Se true, all'avvio parte con cursore libero.")]
        [SerializeField] private bool startFree = false;

        private bool _free;

        private void Start()
        {
            _free = startFree;
            ApplyCursorState();
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb[toggleKey].wasPressedThisFrame)
            {
                _free = !_free;
                ApplyCursorState();
            }
        }

        private void ApplyCursorState()
        {
            Cursor.lockState = _free ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = _free;
        }

        // Overlay minimale che ricorda lo stato del toggle.
        private void OnGUI()
        {
            GUI.Label(new Rect(10, 10, 320, 20),
                _free ? $"[DEBUG] Cursore LIBERO — {toggleKey} per ribloccare"
                      : $"[DEBUG] Cursore bloccato — {toggleKey} per liberare");
        }
#endif
    }
}
