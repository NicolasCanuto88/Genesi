using System.Collections;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// PauseMenuController — Rev BX-c (Q121-a, Q122-a, Q123-a, Q124-a). Menu di pausa della scena Game.
///
/// IL GIOCO NON SI FERMA (come in Lethal Company): si bloccano solo i comandi del giocatore locale.
/// O₂, sistemi e compagni continuano. Time.timeScale non viene toccato.
///
/// APERTURA: azione "Pause" della mappa UI (Esc / Start), abilitata da questo componente sul
/// PlayerInput del giocatore locale. Si apre solo da giocatore libero:
///   - vivo con PlayerController attivo (a piedi o sulla scala: non seduto, non a un pannello,
///     non sul letto, tablet chiuso), oppure a terra / in attesa del clone;
///   - nessuna interazione continua (rianimazione), nessun canale del kit, nessuna mira della
///     granata, nessuna pressione del drone o dei gadget del Quartermaster.
/// Seduti, col tablet o in un minigame, Esc / B chiudono prima quel contesto (Cancel): questo
/// componente non tocca nessuno degli 8 consumatori di Cancel. Esc è legato sia a Pause sia a
/// UI/Cancel: da libero nessun consumatore di Cancel è attivo, quindi agisce solo la pausa.
/// Caso da osservare: a terra mentre si è seduti a Pilota, Ingegneria o Scanner, Esc apre la
/// pausa e chiude anche la postazione.
///
/// MENTRE È APERTO: mappa Player spenta (niente movimento, visuale, interazione, tablet, gadget;
/// lo spegnimento annulla le azioni in corso, quindi Move torna a zero), cursore libero.
/// Esc / Start / B: dal pannello di conferma torna al pannello principale, altrimenti chiude.
///
/// CONTENUTO (Q121-a):
///   - RESUME;
///   - LEAVE SESSION (client, senza conferma) o END SESSION (host, con conferma: termina per tutti);
///   - QUIT TO DESKTOP (con conferma; per l'host termina anche la sessione);
///   - codice della sessione (SessionFlow.JoinCode) per far rientrare chi cade.
///
/// USCITA (Q123-a): Leave, End Session e disconnessione subita riportano al menu principale.
///   1. SessionFlow.BeginLeave(messaggio) — vuoto se l'uscita è voluta;
///   2. NetworkManager.Shutdown() — l'host disconnette prima i client (NGO, fino a 5 s);
///   3. attesa di IsListening == false (con timeout);
///   4. distruzione del NetworkManager persistente: MainMenu ne contiene uno nuovo, e due
///      NetworkManager in DontDestroyOnLoad non devono convivere;
///   5. caricamento di MainMenu, che mostra il messaggio (MainMenuManager).
/// Disconnessione subita: OnClientStopped / OnServerStopped senza un'uscita in corso.
///
/// UI: costruita in Editor (guida Rev BX-c), stile del menu principale. Visibilità via CanvasGroup
/// sulla radice (mai SetActive su questo GameObject); i pannelli figli via SetActive.
/// Testi in inglese.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class PauseMenuController : MonoBehaviour
{
    [Header("Pannelli (figli di questo canvas)")]
    [Tooltip("Pannello principale: Resume, Leave/End Session, Quit, codice sessione.")]
    [SerializeField] private GameObject mainPanel;
    [Tooltip("Pannello di conferma per End Session e Quit to Desktop.")]
    [SerializeField] private GameObject confirmPanel;

    [Header("Pannello principale")]
    [SerializeField] private Button resumeButton;
    [SerializeField] private Button leaveButton;
    [Tooltip("Testo del button Leave: LEAVE SESSION (client) o END SESSION (host), scritto dallo script.")]
    [SerializeField] private TMP_Text leaveButtonLabel;
    [SerializeField] private Button quitButton;
    [Tooltip("Testo con il codice della sessione.")]
    [SerializeField] private TMP_Text sessionCodeText;
    [Tooltip("Testo di stato (\"Leaving the session...\"). Opzionale.")]
    [SerializeField] private TMP_Text statusText;

    [Header("Pannello di conferma")]
    [SerializeField] private TMP_Text confirmText;
    [SerializeField] private Button confirmYesButton;
    [SerializeField] private Button confirmNoButton;

    [Header("Input (azioni del PlayerInput locale)")]
    [Tooltip("Azione che apre e chiude la pausa (mappa UI: Esc / Start).")]
    [SerializeField] private string pauseActionName = "UI/Pause";
    [Tooltip("Mappa spenta mentre la pausa è aperta.")]
    [SerializeField] private string playerMapName = "Player";

    [Header("Uscita")]
    [Tooltip("Attesa massima della chiusura di rete prima di caricare il menu principale (s). " +
             "L'host aspetta fino a 5 s che i client si disconnettano.")]
    [SerializeField] private float leaveTimeoutSeconds = 7f;

    [Header("Debug")]
    [Tooltip("Log verbosi non critici - standard Rev BA - default off.")]
    [SerializeField] private bool logVerbose = false;

    // ── Testi (inglese, localizzazione prevista) ───────────────────────────────
    private const string TextLeaveSession = "LEAVE SESSION";
    private const string TextEndSession = "END SESSION";
    private const string TextSessionCode = "SESSION CODE  {0}";
    private const string TextNoCode = "—";
    private const string TextLeaving = "Leaving the session...";
    private const string TextConfirmEnd = "End the session for the whole crew?";
    private const string TextConfirmQuitHost = "Quit to desktop? The session will end for the whole crew.";
    private const string TextConfirmQuit = "Quit to desktop?";
    private const string TextDisconnected = "Disconnected from the session: the host left or the connection was lost.";
    private const string TextNetworkError = "The session stopped because of a network error.";

    private enum Pending { None, EndSession, Quit }

    private CanvasGroup rootGroup;
    private bool isOpen;
    private bool leaving;
    private Pending pending = Pending.None;

    // Giocatore locale (risolto da PlayerHealthSystem.LocalInstance).
    private GameObject localPlayer;
    private PlayerInput localInput;
    private PlayerController localController;
    private PlayerHealthSystem localHealth;
    private TabletStation localTablet;
    private InteractionSystem localInteraction;
    private PlayerMedKit localMedKit;
    private PlayerThrower localThrower;
    private PlayerNanomedicDrone localDrone;
    private PlayerQuartermasterGadgets localGadgets;

    private InputAction pauseAction;
    private InputActionMap playerMap;
    private bool playerMapDisabledByPause;

    // Modulo UI dell'EventSystem (azione Cancel per tornare indietro / chiudere).
    private EventSystem moduleOwner;
    private InputSystemUIInputModule uiModule;

    private NetworkManager subscribedManager;

    /// <summary>True mentre il menu di pausa è aperto.</summary>
    public bool IsOpen => isOpen;

    private void LogV(string msg) { if (logVerbose) Debug.Log("[PauseMenu] " + msg); }

    // ── Ciclo di vita ──────────────────────────────────────────────────────────

    private void Awake()
    {
        rootGroup = GetComponent<CanvasGroup>();
        SetRootVisible(false);
        if (mainPanel != null) mainPanel.SetActive(true);
        if (confirmPanel != null) confirmPanel.SetActive(false);

        if (resumeButton != null) resumeButton.onClick.AddListener(Close);
        if (leaveButton != null) leaveButton.onClick.AddListener(OnLeaveClicked);
        if (quitButton != null) quitButton.onClick.AddListener(OnQuitClicked);
        if (confirmYesButton != null) confirmYesButton.onClick.AddListener(OnConfirmYes);
        if (confirmNoButton != null) confirmNoButton.onClick.AddListener(Back);
    }

    private void Start()
    {
        subscribedManager = NetworkManager.Singleton;
        if (subscribedManager != null)
        {
            subscribedManager.OnClientStopped += HandleClientStopped;
            subscribedManager.OnServerStopped += HandleServerStopped;
        }
        else
        {
            Debug.LogWarning("[PauseMenu] NetworkManager assente: uscita al menu principale non disponibile.");
        }
    }

    private void OnDestroy()
    {
        if (subscribedManager != null)
        {
            subscribedManager.OnClientStopped -= HandleClientStopped;
            subscribedManager.OnServerStopped -= HandleServerStopped;
        }

        // L'asset delle azioni sopravvive alla scena: non lasciare la mappa Player spenta né la
        // pausa accesa per la sessione successiva.
        RestorePlayerMap();
        if (pauseAction != null) pauseAction.Disable();
    }

    private void Update()
    {
        ResolveLocalPlayer();

        if (isOpen)
        {
            KeepCursorFree();
            if (leaving) return;

            // Esc è legato sia a Pause sia a UI/Cancel: un solo passo indietro per pressione.
            if (PressedThisFrame(pauseAction) || PressedThisFrame(CancelAction()))
            {
                Back();
                return;
            }

            DashboardSelection.EnsureSafety(this, ChooseSelection, logVerbose);
            return;
        }

        if (PressedThisFrame(pauseAction) && CanOpen())
            Open();
    }

    // ── Apertura / chiusura ────────────────────────────────────────────────────

    /// <summary>
    /// Si apre solo da giocatore libero (vedi commento di classe). A terra o in attesa del clone
    /// PlayerController è spento dal freeze Downed: lì la pausa si apre comunque.
    /// </summary>
    private bool CanOpen()
    {
        if (leaving || SessionFlow.IsLeaving) return false;
        if (localPlayer == null || localInput == null) return false;

        if (localTablet != null && localTablet.IsBusy) return false;
        if (localInteraction != null && localInteraction.IsInteracting) return false;
        if (localMedKit != null && localMedKit.IsChanneling) return false;
        if (localThrower != null && localThrower.IsAiming) return false;
        if (localDrone != null && localDrone.IsDeployHeld) return false;
        if (localGadgets != null && localGadgets.IsPressing) return false;

        bool alive = localHealth == null || localHealth.IsAlive;
        if (alive && (localController == null || !localController.enabled)) return false;

        return true;
    }

    private void Open()
    {
        isOpen = true;
        pending = Pending.None;

        SetRootVisible(true);
        ShowMainPanel();
        if (statusText != null) statusText.text = string.Empty;
        SetButtonsInteractable(true);

        if (playerMap != null && playerMap.enabled)
        {
            playerMap.Disable();
            playerMapDisabledByPause = true;
        }

        KeepCursorFree();
        LogV("Aperto.");
    }

    /// <summary>Chiude la pausa e restituisce i comandi (button Resume, Esc / Start / B).</summary>
    public void Close()
    {
        if (!isOpen || leaving) return;

        isOpen = false;
        pending = Pending.None;

        SetRootVisible(false);
        ClearSelectionIfOurs();
        RestorePlayerMap();

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        LogV("Chiuso.");
    }

    /// <summary>Indietro: dalla conferma al pannello principale, altrimenti chiude.</summary>
    private void Back()
    {
        if (leaving) return;

        if (pending != Pending.None)
        {
            pending = Pending.None;
            ShowMainPanel();
            return;
        }

        Close();
    }

    private void ShowMainPanel()
    {
        if (confirmPanel != null) confirmPanel.SetActive(false);
        if (mainPanel != null) mainPanel.SetActive(true);

        if (leaveButtonLabel != null)
            leaveButtonLabel.text = IsHostSession() ? TextEndSession : TextLeaveSession;

        if (sessionCodeText != null)
        {
            string code = string.IsNullOrEmpty(SessionFlow.JoinCode) ? TextNoCode : SessionFlow.JoinCode;
            sessionCodeText.text = string.Format(TextSessionCode, code);
        }

        SelectNow(resumeButton);
    }

    private void ShowConfirm(Pending what, string message)
    {
        pending = what;
        if (confirmText != null) confirmText.text = message;
        if (mainPanel != null) mainPanel.SetActive(false);
        if (confirmPanel != null) confirmPanel.SetActive(true);

        // Azioni distruttive: il focus parte su "annulla".
        SelectNow(confirmNoButton);
    }

    // ── Button ─────────────────────────────────────────────────────────────────

    private void OnLeaveClicked()
    {
        if (leaving) return;

        if (IsHostSession())
            ShowConfirm(Pending.EndSession, TextConfirmEnd);
        else
            StartLeave(string.Empty);
    }

    private void OnQuitClicked()
    {
        if (leaving) return;
        ShowConfirm(Pending.Quit, IsHostSession() ? TextConfirmQuitHost : TextConfirmQuit);
    }

    private void OnConfirmYes()
    {
        if (leaving) return;

        switch (pending)
        {
            case Pending.EndSession: StartLeave(string.Empty); break;
            case Pending.Quit: QuitToDesktop(); break;
        }
    }

    private void QuitToDesktop()
    {
        leaving = true;
        SetButtonsInteractable(false);
        LogV("Quit to desktop.");

        // Uscita unica con EXIT GAME del menu principale (Q128-a): in Editor ferma il Play,
        // in build Application.Quit (NGO chiude la rete in OnApplicationQuit).
        SessionFlow.QuitGame();
    }

    // ── Uscita al menu principale (Q123-a) ─────────────────────────────────────

    private void StartLeave(string message)
    {
        if (leaving) return;
        leaving = true;
        SessionFlow.BeginLeave(message);

        if (isOpen)
        {
            if (pending != Pending.None) { pending = Pending.None; ShowMainPanel(); }
            if (statusText != null) statusText.text = TextLeaving;
            SetButtonsInteractable(false);
        }

        LogV("Uscita verso il menu principale" + (string.IsNullOrEmpty(message) ? "." : ": " + message));
        StartCoroutine(LeaveRoutine());
    }

    private IEnumerator LeaveRoutine()
    {
        // Un frame di distanza: StartLeave può arrivare da dentro un callback di NGO.
        yield return null;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsServer || nm.IsClient) && !nm.ShutdownInProgress)
            nm.Shutdown();

        float deadline = Time.realtimeSinceStartup + leaveTimeoutSeconds;
        while (nm != null && nm.IsListening && Time.realtimeSinceStartup < deadline)
            yield return null;

        if (nm != null && nm.IsListening)
            Debug.LogWarning("[PauseMenu] La rete non si è chiusa entro il timeout: si torna al menu comunque.");

        // MainMenu contiene il proprio NetworkManager: quello persistente va distrutto prima.
        if (nm != null) Destroy(nm.gameObject);

        SceneManager.LoadScene(SessionFlow.MainMenuSceneName);
    }

    /// <summary>Client fermato (anche l'host, che è anche client). Se non stiamo uscendo, è subita.</summary>
    private void HandleClientStopped(bool wasHost)
    {
        if (leaving || SessionFlow.IsLeaving) return;
        StartLeave(wasHost ? TextNetworkError : TextDisconnected);
    }

    /// <summary>Server fermato senza un'uscita in corso: errore del transport.</summary>
    private void HandleServerStopped(bool wasClient)
    {
        if (leaving || SessionFlow.IsLeaving) return;
        StartLeave(TextNetworkError);
    }

    // ── Giocatore locale e input ───────────────────────────────────────────────

    /// <summary>
    /// Risolve i componenti del giocatore locale quando cambia (spawn in Game). Abilita l'azione
    /// Pause sul suo PlayerInput: la mappa UI non è abilitata dal PlayerInput, e l'EventSystem
    /// abilita solo le azioni che usa (Navigate, Submit, Cancel, ...).
    /// </summary>
    private void ResolveLocalPlayer()
    {
        PlayerHealthSystem health = PlayerHealthSystem.LocalInstance;
        GameObject player = health != null ? health.gameObject : null;
        if (player == localPlayer) return;

        // Cambio di giocatore (o despawn): restituisci la mappa al vecchio, se l'avevamo spenta.
        RestorePlayerMap();

        localPlayer = player;
        localHealth = health;
        localInput = player != null ? player.GetComponent<PlayerInput>() : null;
        localController = player != null ? player.GetComponent<PlayerController>() : null;
        localTablet = player != null ? player.GetComponent<TabletStation>() : null;
        localInteraction = player != null ? player.GetComponent<InteractionSystem>() : null;
        localMedKit = player != null ? player.GetComponent<PlayerMedKit>() : null;
        localThrower = player != null ? player.GetComponent<PlayerThrower>() : null;
        localDrone = player != null ? player.GetComponent<PlayerNanomedicDrone>() : null;
        localGadgets = player != null ? player.GetComponent<PlayerQuartermasterGadgets>() : null;

        InputActionAsset actions = localInput != null ? localInput.actions : null;
        pauseAction = actions != null ? actions.FindAction(pauseActionName, throwIfNotFound: false) : null;
        playerMap = actions != null ? actions.FindActionMap(playerMapName, throwIfNotFound: false) : null;

        if (localInput != null && pauseAction == null)
            Debug.LogWarning($"[PauseMenu] Azione '{pauseActionName}' non trovata: aggiungila all'asset delle azioni " +
                             "(guida setup Editor Rev BX-c).");

        if (pauseAction != null) pauseAction.Enable();

        // Despawn a pausa aperta: la pausa resta (uscita o clone in arrivo), i comandi no.
        if (isOpen && playerMap != null && playerMap.enabled)
        {
            playerMap.Disable();
            playerMapDisabledByPause = true;
        }
    }

    private void RestorePlayerMap()
    {
        if (playerMapDisabledByPause && playerMap != null)
            playerMap.Enable();
        playerMapDisabledByPause = false;
    }

    private InputAction CancelAction()
    {
        EventSystem es = EventSystem.current;
        if (es != moduleOwner)
        {
            moduleOwner = es;
            uiModule = es != null ? es.GetComponent<InputSystemUIInputModule>() : null;
        }
        return uiModule != null && uiModule.cancel != null ? uiModule.cancel.action : null;
    }

    private static bool PressedThisFrame(InputAction action)
        => action != null && action.WasPressedThisFrame();

    private static bool IsHostSession()
        => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    // ── UI ─────────────────────────────────────────────────────────────────────

    private void SetRootVisible(bool visible)
    {
        if (rootGroup == null) return;
        rootGroup.alpha = visible ? 1f : 0f;
        rootGroup.interactable = visible;
        rootGroup.blocksRaycasts = visible;
    }

    private void SetButtonsInteractable(bool interactable)
    {
        if (resumeButton != null) resumeButton.interactable = interactable;
        if (leaveButton != null) leaveButton.interactable = interactable;
        if (quitButton != null) quitButton.interactable = interactable;
        if (confirmYesButton != null) confirmYesButton.interactable = interactable;
        if (confirmNoButton != null) confirmNoButton.interactable = interactable;
    }

    private static void KeepCursorFree()
    {
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible) Cursor.visible = true;
    }

    /// <summary>Seleziona subito un button (rete di sicurezza: EnsureSafety in Update).</summary>
    private static void SelectNow(Selectable target)
    {
        EventSystem es = EventSystem.current;
        if (es == null) return;
        es.SetSelectedGameObject(target != null && target.isActiveAndEnabled ? target.gameObject : null);
    }

    /// <summary>Candidato di selezione: annulla nella conferma, Resume nel pannello principale.</summary>
    private GameObject ChooseSelection()
    {
        if (pending != Pending.None && confirmNoButton != null && confirmNoButton.isActiveAndEnabled)
            return confirmNoButton.gameObject;
        if (resumeButton != null && resumeButton.isActiveAndEnabled && resumeButton.interactable)
            return resumeButton.gameObject;
        return DashboardSelection.FirstInteractableSelectable(transform);
    }

    private void ClearSelectionIfOurs()
    {
        EventSystem es = EventSystem.current;
        if (es == null || es.currentSelectedGameObject == null) return;
        if (es.currentSelectedGameObject.transform.IsChildOf(transform))
            es.SetSelectedGameObject(null);
    }
}
