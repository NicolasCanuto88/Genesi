using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// MainMenuManager — Milestone 3, Blocco 1 (Frontend &amp; Identità).
/// Rev: aggiornato per corrispondere ESATTAMENTE alla gerarchia reale di
/// MainMenu.unity (estetica sci-fi con parentesi angolari, accent bar,
/// badge personaggio nome+ruolo+dot separati) — vedi guida wiring per il
/// percorso preciso di ogni campo nella Hierarchy.
///
/// State machine a 6 stati — logica invariata dalla versione precedente:
///   CharacterCreation → MainMenu → CharacterSelect → SessionType → LobbyHost → Join
///
/// CAMBIO RISPETTO ALLA VERSIONE PRECEDENTE: il badge personaggio nel
/// MainMenuPanel non è un singolo testo "Nome · Ruolo" ma tre elementi
/// separati (Name, Role, Dot) — Dot è un'Image colorata dinamicamente in
/// base al ruolo tramite RoleColors.Get() (fonte unica, condivisa con
/// CharacterEntryUI per coerenza tra badge personaggio e lista selezione).
///
/// REV BM — RUOLI: 5 bottoni ruolo (Pilota, Ingegnere, Scanner, Corpsman,
/// Quartermaster). "Medico" → "Corpsman" (rinomina M3 close; il campo
/// serializzato è rinominato con FormerlySerializedAs, nessun riferimento perso);
/// Quartermaster aggiunto (identità pronta, contenuto in Fase 3b). Le etichette
/// salvate nel profilo vengono da CrewRoles.ToDisplayName (fonte unica della
/// mappatura ruolo ↔ stringa, condivisa con PlayerCrewRole e RoleColors).
///
/// REV BX-b (Q120-a, Q127-a) — NAVIGAZIONE:
///   - Navigazione esplicita in Cambio personaggio e Creazione personaggio (vedi
///     CablaNavigazioneSelezione / CablaNavigazioneCreazione): con quella automatica "Conferma"
///     non era raggiungibile in Cambio personaggio. La scrollbar della lista non è navigabile.
///   - Cancel e "navigazione premuta" si leggono dalle azioni dell'InputSystemUIInputModule
///     dell'EventSystem (Cancel, Navigate, Submit), non più da Keyboard.current / Gamepad.current.
///   - Primo focus col gamepad sul primo bottone: in Creazione personaggio e Unisciti il campo di
///     testo resta il primo focus solo con tastiera e mouse (InputDeviceManager). Senza
///     InputDeviceManager nella scena si comporta come prima (campo di testo), con un avviso.
///   - Esc mentre si scrive in un campo chiude solo il campo, non il pannello (prima tornava al
///     menu principale perdendo il nome appena scritto).
///   - Gate BX-b: i campi di testo non si aprono passandoci sopra (shouldActivateOnSelect off): si
///     aprono con A / Invio o col click; il primo focus da tastiera apre comunque il campo. Il
///     ruolo scelto resta verde anche senza selezione (AggiornaCertColoriRuolo). Lobby con
///     navigazione esplicita (CablaNavigazioneLobby): da "Copia" giù si va a "Inizia la partita".
///   - DefaultExecutionOrder(100): Update gira dopo l'EventSystem. Così, quando il focus è perso
///     e si preme una direzione, il focus viene ripristinato DOPO che il modulo UI ha elaborato
///     la pressione: si vede la voce ripristinata, senza un passo in più (ordine deterministico,
///     prima dipendeva dall'ordine casuale degli Update).
///
/// REV BX-c (Q121-a / Q123-a) — SESSIONE:
///   - All'arrivo (Start) legge SessionFlow.ConsumeExitMessage(): al ritorno da una sessione
///     chiusa o persa il messaggio compare nel pannello principale (mainSessionMessageText) finché
///     non si passa a un altro pannello. Lo stato della sessione conclusa viene azzerato.
///   - Il codice della sessione va in SessionFlow (host all'avvio del server, client alla
///     connessione) perché il menu di pausa lo mostri: in partita RelayManager non esiste più.
///   - Il NetworkManager di questa scena è quello nuovo: PauseMenuController distrugge il vecchio
///     prima di caricare MainMenu.
///   - Q128-a: bottone EXIT GAME (mainBtnEsci) in fondo al pannello principale, esce subito
///     (SessionFlow.QuitGame; in Editor ferma il Play).
/// </summary>
[DefaultExecutionOrder(100)]
public class MainMenuManager : MonoBehaviour
{
    [Header("Debug")]
    [Tooltip("Log diagnostici verbosi (relay pronto, debug skip). Standard Rev BA — default off.")]
    [SerializeField] private bool logVerbose = false;
    private void LogV(string msg) { if (logVerbose) Debug.Log(msg); }

    private const string GAME_SCENE_NAME = "Game";

    // ── CANVAS ────────────────────────────────────────────────────────────────

    [Header("Canvas")]
    [SerializeField] private Canvas menuCanvas;

    // ── PANNELLI ──────────────────────────────────────────────────────────────

    [Header("Pannelli (uno solo attivo alla volta)")]
    [SerializeField] private GameObject characterCreationPanel;
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject characterSelectPanel;
    [SerializeField] private GameObject sessionTypePanel;
    [SerializeField] private GameObject lobbyHostPanel;
    [SerializeField] private GameObject joinPanel;

    // ── CHARACTER CREATION ────────────────────────────────────────────────────
    // Percorso: MainMenuCanvas/CharacterCreationPanel/ContentContainer/...

    [Header("Character Creation")]
    [Tooltip("ContentContainer/NameSection/CreationNameInput")]
    [SerializeField] private TMP_InputField creationNameInput;
    [Tooltip("ContentContainer/RoleContainer/Pilota")]
    [SerializeField] private Button creationBtnPilota;
    [Tooltip("ContentContainer/RoleContainer/Ingegnere")]
    [SerializeField] private Button creationBtnIngegnere;
    [Tooltip("ContentContainer/RoleContainer/Scanner")]
    [SerializeField] private Button creationBtnScanner;
    [Tooltip("ContentContainer/RoleContainer/Corpsman (ex Medico — Rev BM)")]
    [FormerlySerializedAs("creationBtnMedico")]
    [SerializeField] private Button creationBtnCorpsman;
    [Tooltip("ContentContainer/RoleContainer/Quartermaster (NUOVO — Rev BM)")]
    [SerializeField] private Button creationBtnQuartermaster;
    [Tooltip("ContentContainer/ErrorText")]
    [SerializeField] private TextMeshProUGUI creationErrorLabel;
    [Tooltip("ContentContainer/Apply")]
    [SerializeField] private Button creationBtnConferma;
    [Tooltip("Nuovo bottone da creare in Editor (vedi guida) — visibile solo se esiste già almeno un personaggio")]
    [SerializeField] private Button creationBtnIndietro;

    // ── MAIN MENU ─────────────────────────────────────────────────────────────
    // Percorso: MainMenuCanvas/MainMenuPanel/...

    [Header("Main Menu — badge personaggio (3 elementi separati, non un testo unico)")]
    [Tooltip("CharacterBadge/CharacterInfo/Background/Container/Name")]
    [SerializeField] private TextMeshProUGUI mainCharacterNameText;
    [Tooltip("CharacterBadge/CharacterInfo/Background/Container/Role")]
    [SerializeField] private TextMeshProUGUI mainCharacterRoleText;
    [Tooltip("CharacterBadge/CharacterInfo/Background/Container/Dot — colorata dinamicamente per ruolo")]
    [SerializeField] private Image mainCharacterDot;

    [Header("Main Menu — azioni")]
    [Tooltip("CharacterBadge/ChangeCharacter")]
    [SerializeField] private Button mainBtnCambiaPersonaggio;
    [Tooltip("NewGame")]
    [SerializeField] private Button mainBtnNuovaPartita;
    [Tooltip("LoadGame")]
    [SerializeField] private Button mainBtnCarica;
    [Tooltip("Join")]
    [SerializeField] private Button mainBtnUnisciti;
    [Tooltip("Options")]
    [SerializeField] private Button mainBtnOpzioni;
    [Tooltip("Credits")]
    [SerializeField] private Button mainBtnCrediti;
    [Tooltip("ExitGame — bottone in fondo al pannello (Rev BX-c · Q128-a): esce dal gioco senza " +
             "conferma. In Editor ferma il Play.")]
    [SerializeField] private Button mainBtnEsci;

    [Header("Main Menu — messaggio di sessione (Rev BX-c)")]
    [Tooltip("MainMenuPanel/ContentContainer/SessionMessage — testo mostrato al ritorno da una " +
             "sessione chiusa o persa (es. disconnessione). Vuoto e nascosto negli altri casi. Opzionale.")]
    [SerializeField] private TextMeshProUGUI mainSessionMessageText;

    // ── CHARACTER SELECT ──────────────────────────────────────────────────────
    // Percorso: MainMenuCanvas/CharacterSelectPanel/ContentContainer/...

    [Header("Character Select")]
    [Tooltip("CharacterContainer/CharacterList/Viewport/Content — NON il ScrollRect stesso")]
    [SerializeField] private Transform selectListContainer;
    [SerializeField] private GameObject characterEntryPrefab;
    [Tooltip("CharacterContainer/NewCharacter")]
    [SerializeField] private Button selectBtnNuovoPersonaggio;
    [Tooltip("ButtonContainer/Apply")]
    [SerializeField] private Button selectBtnConferma;
    [Tooltip("ButtonContainer/Back")]
    [SerializeField] private Button selectBtnIndietro;

    // ── SESSION TYPE ──────────────────────────────────────────────────────────
    // Percorso: MainMenuCanvas/SessionTypePanel/ContentContainer/...

    [Header("Session Type")]
    [Tooltip("CardMainContainer/CardContainer/Background/Open — card 'Aperta'")]
    [SerializeField] private Button sessionBtnAperta;
    [Tooltip("CardMainContainer/CardContainer (1)/Background/Open (1) — card 'Su invito'")]
    [SerializeField] private Button sessionBtnSuInvito;
    [Tooltip("Back (diretto sotto ContentContainer, non dentro le card)")]
    [SerializeField] private Button sessionBtnIndietro;

    // ── LOBBY HOST ────────────────────────────────────────────────────────────
    // Percorso: MainMenuCanvas/LobbyHostPanel/ContentContainer/...

    [Header("Lobby Host")]
    [Tooltip("BadgeSession/Text")]
    [SerializeField] private TextMeshProUGUI lobbySessionTypeBadge;
    [Tooltip("CardContainer (2)/Background/JoinCode — il codice vero e proprio (es. DJFMKQ)")]
    [SerializeField] private TextMeshProUGUI lobbyJoinCodeText;
    [Tooltip("CardContainer (2)/Background/CopyCode")]
    [SerializeField] private Button lobbyBtnCopiaCode;
    [Tooltip("⚠️ ATTENZIONE: nella scena questo GameObject si chiama 'JoinCode (1)' " +
             "ma è in realtà il conteggio giocatori, non un codice — testo originale " +
             "di placeholder 'Equipaggio a bordo: 1/5'")]
    [SerializeField] private TextMeshProUGUI lobbyPlayerCountText;
    [Tooltip("StartGame")]
    [SerializeField] private Button lobbyBtnInizia;
    [Tooltip("Back")]
    [SerializeField] private Button lobbyBtnAnnulla;

    // ── JOIN ──────────────────────────────────────────────────────────────────
    // Percorso: MainMenuCanvas/JoinPanel/ContentContainer/...

    [Header("Join")]
    [Tooltip("InputField (TMP)")]
    [SerializeField] private TMP_InputField joinCodeInput;
    [Tooltip("⚠️ ATTENZIONE: nella scena questo GameObject si chiama 'JoinCode (1)' " +
             "ma è in realtà il testo di stato connessione, non un codice")]
    [SerializeField] private TextMeshProUGUI joinStatusText;
    [Tooltip("ButtonContainer/StartGame — testo visualizzato 'connetti'")]
    [SerializeField] private Button joinBtnConferma;
    [Tooltip("ButtonContainer/Back")]
    [SerializeField] private Button joinBtnIndietro;

    // ── COLORI ────────────────────────────────────────────────────────────────

    [Header("Colori selezione (sfondo bottoni ruolo creazione)")]
    [SerializeField] private Color colorRuoloNormale = new Color(0.07f, 0.08f, 0.12f, 1f);
    [SerializeField] private Color colorRuoloSelezionato = new Color(0.04f, 0.11f, 0.16f, 1f);

    // ── RIFERIMENTI ───────────────────────────────────────────────────────────

    [Header("Riferimenti")]
    [SerializeField] private RelayManager relayManager;

    // ── NAVIGAZIONE CONTROLLER ────────────────────────────────────────────────

    [Header("Navigazione Controller / Tastiera")]
    [Tooltip("Imposta automaticamente il focus sull'elemento primario di ogni pannello " +
             "e gestisce il tasto Indietro (B / Esc). Disattivalo per tornare al comportamento solo-mouse.")]
    [SerializeField] private bool enableControllerNav = true;

    // ── STATE MACHINE ─────────────────────────────────────────────────────────

    private enum Stato { CharacterCreation, MainMenu, CharacterSelect, SessionType, LobbyHost, Join }
    private enum AzionePending { None, NuovaPartita, Unisciti }
    private enum TipoSessione { Aperta, SuInvito }

    private Stato _stato = Stato.CharacterCreation;
    private AzionePending _pendingAction = AzionePending.None;
    private TipoSessione _tipoSessione = TipoSessione.SuInvito;
    private bool _creatingFromSelect = false;
    private bool _isConnecting = false;
    private string _messaggioSessione = string.Empty;   // Rev BX-c: da SessionFlow al ritorno da una sessione
    private string _codiceInserito = string.Empty;      // Rev BX-c: codice usato per unirsi (per SessionFlow)
    private string _selectedCharId = "";
    private string _ruoloSelezionato = "";

    // Rev BM: etichette dalla fonte unica CrewRoles, nell'ordine dei bottoni
    // (Pilota, Ingegnere, Scanner, Corpsman, Quartermaster). È la stringa che
    // finisce nel profilo e che PlayerCrewRole converte in CrewRole.
    private static readonly string[] NomiRuoli = BuildNomiRuoli();
    private Button[] _creationRoleButtons;

    private static string[] BuildNomiRuoli()
    {
        var nomi = new string[CrewRoles.SelectableCount];
        for (int i = 0; i < nomi.Length; i++)
            nomi[i] = CrewRoles.ToDisplayName(CrewRoles.GetSelectable(i));
        return nomi;
    }

    // ── LIFECYCLE ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        // FIX (richiesta esplicita): in precedenza un clone ParrelSync
        // disattivava interamente il menu qui (vedi nota in AutoStartHost.cs
        // per la storia completa del meccanismo gemello rimosso insieme a
        // questo) — il clone passava direttamente alla connessione di rete
        // senza alcuna interazione utente. Ora il clone si comporta
        // ESATTAMENTE come qualunque altra istanza: passa dal menu, crea il
        // proprio personaggio separato, e si unisce manualmente inserendo
        // il join code generato dall'istanza host (stesso flusso "Unisciti"
        // già esistente per chiunque). Nessun trattamento speciale per
        // ClonesManager.IsClone() necessario qui.
        _creationRoleButtons = new[] {
            creationBtnPilota, creationBtnIngegnere,
            creationBtnScanner, creationBtnCorpsman,
            creationBtnQuartermaster
        };

        // Rev BM: bottoni e NomiRuoli sono accoppiati per indice → stessa lunghezza.
        if (_creationRoleButtons.Length != NomiRuoli.Length)
            Debug.LogError($"[MainMenuManager] Bottoni ruolo ({_creationRoleButtons.Length}) ≠ ruoli " +
                           $"selezionabili ({NomiRuoli.Length}). Allineare _creationRoleButtons a CrewRoles.");
        if (creationBtnQuartermaster == null)
            Debug.LogWarning("[MainMenuManager] creationBtnQuartermaster non assegnato: il ruolo Quartermaster " +
                             "non è selezionabile. Vedi guida setup Editor Rev BM.");

        // Rev BX-b (gate) — colori di transizione originali dei bottoni ruolo (vedi AggiornaCertColoriRuolo).
        _coloriOriginaliRuolo = new ColorBlock[_creationRoleButtons.Length];
        for (int i = 0; i < _creationRoleButtons.Length; i++)
            if (_creationRoleButtons[i] != null) _coloriOriginaliRuolo[i] = _creationRoleButtons[i].colors;

        // Rev BX-b (gate) — passare sopra un campo di testo con frecce o stick non lo apre: lo apre
        // A / Invio (o il click). Prima il campo si apriva da solo e catturava il gamepad finché non
        // si premeva B. Il primo focus da tastiera lo apre comunque (FocusPrimarioNextFrame).
        if (creationNameInput != null) creationNameInput.shouldActivateOnSelect = false;
        if (joinCodeInput != null) joinCodeInput.shouldActivateOnSelect = false;
    }

    private void Start()
    {
        WireButtons();

        NetworkManager.Singleton.OnServerStarted += OnServerStarted;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        RelayManager.OnServiceReady += OnRelayReady;

        // Rev BX-b — il primo focus dipende dal dispositivo (gamepad: niente campo di testo).
        if (enableControllerNav && InputDeviceManager.Instance == null)
            Debug.LogWarning("[MainMenuManager] InputDeviceManager assente in MainMenu: il primo focus " +
                             "resta sui campi di testo anche col gamepad. Vedi guida setup Editor Rev BX-b.");

        // Rev BX-c — ritorno da una sessione: messaggio (vuoto se l'uscita era voluta) e stato azzerato.
        _messaggioSessione = SessionFlow.ConsumeExitMessage();

        bool primoAccesso = !LocalCharacterProfile.Instance.HasAnyCharacter;
        TransitionTo(primoAccesso ? Stato.CharacterCreation : Stato.MainMenu);
    }

    private void OnDestroy()
    {
        RelayManager.OnServiceReady -= OnRelayReady;

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    // ── WIRING ────────────────────────────────────────────────────────────────

    private void WireButtons()
    {
        for (int i = 0; i < _creationRoleButtons.Length; i++)
        {
            int idx = i;
            if (_creationRoleButtons[idx] != null)
                _creationRoleButtons[idx].onClick.AddListener(() => OnRuoloSelezionato(NomiRuoli[idx]));
        }
        if (creationBtnConferma != null) creationBtnConferma.onClick.AddListener(OnCreationConferma);
        if (creationBtnIndietro != null) creationBtnIndietro.onClick.AddListener(OnCreationIndietro);

        if (mainBtnCambiaPersonaggio != null) mainBtnCambiaPersonaggio.onClick.AddListener(OnCambiaPersonaggio);
        if (mainBtnNuovaPartita != null) mainBtnNuovaPartita.onClick.AddListener(OnNuovaPartita);
        if (mainBtnUnisciti != null) mainBtnUnisciti.onClick.AddListener(OnUniscitiMainMenu);
        if (mainBtnCarica != null) mainBtnCarica.interactable = false; // Blocco 5
        if (mainBtnOpzioni != null) mainBtnOpzioni.interactable = false; // M4
        if (mainBtnCrediti != null) mainBtnCrediti.interactable = false; // M4
        if (mainBtnEsci != null) mainBtnEsci.onClick.AddListener(SessionFlow.QuitGame);   // Rev BX-c · Q128-a

        if (selectBtnNuovoPersonaggio != null) selectBtnNuovoPersonaggio.onClick.AddListener(OnNuovoPersonaggio);
        if (selectBtnConferma != null) selectBtnConferma.onClick.AddListener(OnSelectConferma);
        if (selectBtnIndietro != null) selectBtnIndietro.onClick.AddListener(() => TransitionTo(Stato.MainMenu));

        if (sessionBtnAperta != null) sessionBtnAperta.onClick.AddListener(() => OnTipoSessione(TipoSessione.Aperta));
        if (sessionBtnSuInvito != null) sessionBtnSuInvito.onClick.AddListener(() => OnTipoSessione(TipoSessione.SuInvito));
        if (sessionBtnIndietro != null) sessionBtnIndietro.onClick.AddListener(() => TransitionTo(Stato.MainMenu));

        if (lobbyBtnCopiaCode != null) lobbyBtnCopiaCode.onClick.AddListener(OnCopiaCode);
        if (lobbyBtnInizia != null) lobbyBtnInizia.onClick.AddListener(OnIniziaPartita);
        if (lobbyBtnAnnulla != null) lobbyBtnAnnulla.onClick.AddListener(OnAnnullaHost);

        if (joinBtnConferma != null) joinBtnConferma.onClick.AddListener(OnJoinConferma);
        if (joinBtnIndietro != null) joinBtnIndietro.onClick.AddListener(() => TransitionTo(Stato.MainMenu));
    }

    // ── STATE MACHINE ─────────────────────────────────────────────────────────

    private void TransitionTo(Stato nuovoStato)
    {
        _stato = nuovoStato;

        // Rev BX-c — il messaggio di sessione vale solo finché si resta nel pannello principale.
        if (nuovoStato != Stato.MainMenu) _messaggioSessione = string.Empty;

        characterCreationPanel?.SetActive(false);
        mainMenuPanel?.SetActive(false);
        characterSelectPanel?.SetActive(false);
        sessionTypePanel?.SetActive(false);
        lobbyHostPanel?.SetActive(false);
        joinPanel?.SetActive(false);

        switch (_stato)
        {
            case Stato.CharacterCreation: MostraCreazione(); break;
            case Stato.MainMenu: MostraMainMenu(); break;
            case Stato.CharacterSelect: MostraCharacterSelect(); break;
            case Stato.SessionType: MostraSessionType(); break;
            case Stato.LobbyHost: MostraLobbyHost(); break;
            case Stato.Join: MostraJoin(); break;
        }

        if (enableControllerNav) FocusPrimarioPannello();
    }

    // ── NAVIGAZIONE CONTROLLER / TASTIERA ─────────────────────────────────────
    // L'input è già cablato dall'InputSystemUIInputModule (DefaultInputActions:
    // Navigate/Submit/Cancel su gamepad + tastiera). Qui manca solo: dare un
    // focus iniziale a ogni pannello (senza, col controller nulla è selezionato
    // e lo stick non muove niente), gestire "Indietro" via Cancel, e ripristinare
    // il focus se va perso pur restando in un pannello.

    private GameObject _lastSelected;
    private Coroutine _focusRoutine;

    // Rev BX-b — un campo di testo aveva il focus di scrittura nel frame precedente (vedi HandleControllerNav).
    private bool _campoTestoAttivoPrima;

    // Rev BX-b — voci della lista personaggi create dall'ultima ricostruzione (ordine visivo).
    private readonly List<Selectable> _vociPersonaggi = new List<Selectable>();

    // Rev BX-b (gate) — colori di transizione originali dei bottoni ruolo, per indice.
    private ColorBlock[] _coloriOriginaliRuolo;

    // Rev BX-b — modulo UI dell'EventSystem corrente (azioni Cancel / Navigate / Submit).
    private EventSystem _moduleOwner;
    private InputSystemUIInputModule _uiModule;

    /// <summary>Rev BX-b — ultimo dispositivo usato è il gamepad (InputDeviceManager; assente = no).</summary>
    private static bool UsaGamepad()
        => InputDeviceManager.Instance != null && InputDeviceManager.Instance.IsGamepad;

    /// <summary>Selettore primario per lo stato corrente (rispetta interactable).</summary>
    private Selectable PrimarioPerStato()
    {
        switch (_stato)
        {
            case Stato.CharacterCreation:
                // Rev BX-b — col gamepad il primo focus va al primo bottone (ruolo Pilota): il
                // campo nome serve la tastiera, e un campo attivo cattura la navigazione.
                if (UsaGamepad() && creationBtnPilota != null) return creationBtnPilota;
                return creationNameInput != null ? (Selectable)creationNameInput : creationBtnPilota;
            case Stato.MainMenu:
                if (mainBtnNuovaPartita != null && mainBtnNuovaPartita.interactable) return mainBtnNuovaPartita;
                if (mainBtnUnisciti != null && mainBtnUnisciti.interactable) return mainBtnUnisciti;
                return mainBtnCambiaPersonaggio;
            case Stato.CharacterSelect:
                // Prima entry della lista se esiste, altrimenti "+ Nuovo personaggio".
                // Rev BX-b: dalla lista delle voci create, non dai figli (Destroy è differito).
                if (_vociPersonaggi.Count > 0 && _vociPersonaggi[0] != null) return _vociPersonaggi[0];
                return selectBtnNuovoPersonaggio != null ? selectBtnNuovoPersonaggio : selectBtnConferma;
            case Stato.SessionType:
                return sessionBtnAperta != null ? sessionBtnAperta : sessionBtnSuInvito;
            case Stato.LobbyHost:
                // Anche se ancora spento: vedi SelezionaPrimario.
                return lobbyBtnInizia != null ? lobbyBtnInizia : lobbyBtnAnnulla;
            case Stato.Join:
                // Rev BX-b — col gamepad il primo focus va al bottone Unisciti (vedi sopra).
                if (UsaGamepad() && joinBtnConferma != null) return joinBtnConferma;
                return joinCodeInput != null ? (Selectable)joinCodeInput : joinBtnConferma;
        }
        return null;
    }

    /// <summary>
    /// Imposta il focus sull'elemento primario del pannello, differito di un frame:
    /// un GameObject appena riattivato via SetActive non accetta la selezione nello
    /// stesso frame, e la lista personaggi viene popolata in modo asincrono.
    /// </summary>
    private void FocusPrimarioPannello()
    {
        if (!isActiveAndEnabled) return;
        if (_focusRoutine != null) StopCoroutine(_focusRoutine);
        _focusRoutine = StartCoroutine(FocusPrimarioNextFrame());
    }

    private IEnumerator FocusPrimarioNextFrame()
    {
        yield return null; // attende che SetActive/layout siano applicati
        var target = PrimarioPerStato();
        SelezionaPrimario(target);

        // Rev BX-b (gate) — con tastiera e mouse il campo di testo primario si apre subito, così si
        // scrive senza premere Invio. Col gamepad il primario è un bottone (PrimarioPerStato).
        if (target is TMP_InputField campo && campo.isActiveAndEnabled && campo.interactable && !UsaGamepad())
            campo.ActivateInputField();

        _focusRoutine = null;
    }

    /// <summary>
    /// Rev BX-b (gate) — seleziona il primario del pannello. In lobby "Inizia la partita" resta
    /// spento finché il server non parte (con Relay 1–2 secondi): lo si seleziona comunque, così il
    /// riquadro è già lì e il bottone si accende sotto la selezione. Spostarla su "Annulla" e poi
    /// riportarla su "Inizia" rischiava di far partire la partita a chi stava premendo A per annullare.
    /// Un bottone spento ignora A / Invio; giù porta ad "Annulla" (CablaNavigazioneLobby).
    /// </summary>
    private void SelezionaPrimario(Selectable target)
    {
        bool ancheSpento = _stato == Stato.LobbyHost && target != null && target == lobbyBtnInizia;
        SelezionaSelectable(target, ancheSpento);
    }

    private void SelezionaSelectable(Selectable s, bool ancheSpento = false)
    {
        if (EventSystem.current == null) return;
        var go = (s != null && s.isActiveAndEnabled && (s.interactable || ancheSpento)) ? s.gameObject : null;
        EventSystem.current.SetSelectedGameObject(null);
        EventSystem.current.SetSelectedGameObject(go);
        _lastSelected = go;
    }

    /// <summary>Gestisce Cancel (B / Esc) mappandolo al "Indietro" del pannello corrente.</summary>
    private void HandleCancel()
    {
        switch (_stato)
        {
            case Stato.CharacterCreation:
                if (creationBtnIndietro != null && creationBtnIndietro.gameObject.activeInHierarchy)
                    OnCreationIndietro();
                break;
            case Stato.CharacterSelect: TransitionTo(Stato.MainMenu); break;
            case Stato.SessionType: TransitionTo(Stato.MainMenu); break;
            case Stato.LobbyHost: OnAnnullaHost(); break;
            case Stato.Join: TransitionTo(Stato.MainMenu); break;
                // MainMenu: nessun "indietro" (è la radice del menu).
        }
    }

    /// <summary>
    /// Rev BX-b — modulo UI dell'EventSystem corrente, ricercato solo quando l'EventSystem cambia.
    /// Null se l'EventSystem non usa InputSystemUIInputModule.
    /// </summary>
    private InputSystemUIInputModule ModuloUI()
    {
        var es = EventSystem.current;
        if (es != _moduleOwner)
        {
            _moduleOwner = es;
            _uiModule = es != null ? es.GetComponent<InputSystemUIInputModule>() : null;
        }
        return _uiModule;
    }

    private static bool PremutoNelFrame(InputActionReference reference)
    {
        var action = reference != null ? reference.action : null;
        return action != null && action.WasPressedThisFrame();
    }

    /// <summary>Rev BX-b — azione Cancel del modulo UI (Esc / B), non più tasti letti a mano.</summary>
    private bool CancelPremuto()
    {
        var module = ModuloUI();
        return module != null && PremutoNelFrame(module.cancel);
    }

    /// <summary>Rev BX-b — azioni Navigate o Submit del modulo UI (frecce, WASD, stick, d-pad, Invio, A).</summary>
    private bool NavigazionePremuta()
    {
        var module = ModuloUI();
        return module != null && (PremutoNelFrame(module.move) || PremutoNelFrame(module.submit));
    }

    /// <summary>Rev BX-b — la selezione è un campo di testo con il focus di scrittura.</summary>
    private static bool CampoTestoAttivo(GameObject selected)
    {
        if (selected == null) return false;
        var field = selected.GetComponent<TMP_InputField>();
        return field != null && field.isFocused;
    }

    // ── NAVIGAZIONE ESPLICITA (Rev BX-b · Q127-a) ─────────────────────────────
    // La navigazione automatica di Unity sceglie il vicino per distanza e direzione. In Cambio
    // personaggio "Conferma" non era raggiungibile: da "Nuovo personaggio" giù andava a "Indietro"
    // (pari punteggio con Conferma), e da "Indietro" destra tornava a "Nuovo personaggio", più
    // vicino. In Creazione personaggio giù da Scanner e Corpsman saltava "Conferma". Qui la
    // navigazione dei due pannelli è esplicita, e così quella della lobby (gate BX-b); gli altri
    // pannelli funzionano con quella automatica.

    private static Navigation Esplicita(Selectable up, Selectable down, Selectable left, Selectable right)
        => new Navigation
        {
            mode = Navigation.Mode.Explicit,
            selectOnUp = up,
            selectOnDown = down,
            selectOnLeft = left,
            selectOnRight = right,
        };

    private static void Imposta(Selectable s, Selectable up, Selectable down, Selectable left, Selectable right)
    {
        if (s != null) s.navigation = Esplicita(up, down, left, right);
    }

    /// <summary>
    /// Cambio personaggio: voci dall'alto in basso → "+ Nuovo personaggio" → "Conferma";
    /// "Indietro" e "Conferma" affiancati. La scrollbar della lista non è navigabile.
    /// Limite noto: con molti personaggi la lista non scorre da sola verso la voce selezionata.
    /// </summary>
    private void CablaNavigazioneSelezione()
    {
        int n = _vociPersonaggi.Count;
        Selectable ultimaVoce = n > 0 ? _vociPersonaggi[n - 1] : null;

        for (int i = 0; i < n; i++)
        {
            Selectable su = i > 0 ? _vociPersonaggi[i - 1] : null;
            Selectable giu = i < n - 1 ? _vociPersonaggi[i + 1] : selectBtnNuovoPersonaggio;
            Imposta(_vociPersonaggi[i], su, giu, null, null);
        }

        Imposta(selectBtnNuovoPersonaggio, ultimaVoce, selectBtnConferma, null, null);
        Imposta(selectBtnConferma, selectBtnNuovoPersonaggio, null, selectBtnIndietro, null);
        Imposta(selectBtnIndietro, selectBtnNuovoPersonaggio, null, null, selectBtnConferma);

        // Scrollbar della lista: mai bersaglio della navigazione (prima si poteva finirci sopra).
        var scroll = selectListContainer != null ? selectListContainer.GetComponentInParent<ScrollRect>() : null;
        if (scroll != null)
        {
            if (scroll.verticalScrollbar != null)
                scroll.verticalScrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
            if (scroll.horizontalScrollbar != null)
                scroll.horizontalScrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
        }
    }

    /// <summary>
    /// Creazione personaggio: nome → prima riga ruoli (Pilota, Ingegnere, Scanner) → seconda riga
    /// (Corpsman, Quartermaster) → "Conferma" → "Indietro" (solo se visibile).
    /// </summary>
    private void CablaNavigazioneCreazione()
    {
        bool indietroVisibile = creationBtnIndietro != null && creationBtnIndietro.gameObject.activeSelf;
        Selectable indietro = indietroVisibile ? creationBtnIndietro : null;

        Imposta(creationNameInput, null, creationBtnPilota, null, null);

        Imposta(creationBtnPilota, creationNameInput, creationBtnCorpsman, null, creationBtnIngegnere);
        Imposta(creationBtnIngegnere, creationNameInput, creationBtnQuartermaster, creationBtnPilota, creationBtnScanner);
        Imposta(creationBtnScanner, creationNameInput, creationBtnConferma, creationBtnIngegnere, null);

        Imposta(creationBtnCorpsman, creationBtnPilota, creationBtnConferma, null, creationBtnQuartermaster);
        Imposta(creationBtnQuartermaster, creationBtnIngegnere, creationBtnConferma, creationBtnCorpsman, null);

        Imposta(creationBtnConferma, creationBtnCorpsman, indietro, null, null);
        Imposta(creationBtnIndietro, creationBtnConferma, null, null, null);
    }

    /// <summary>
    /// Rev BX-b (gate) — Lobby: "Copia" → "Inizia la partita" → "Annulla". Con la navigazione
    /// automatica giù da "Copia" andava ad "Annulla": Unity confronta i centri dei bottoni, e
    /// "Copia" sta molto a destra del centro di "Inizia", così "Annulla", più in basso, aveva un
    /// punteggio migliore (direzione più verticale a parità di scarto). La navigazione di Unity
    /// non controlla interactable: i bottoni ancora spenti (server non avviato) non sono bersagli.
    /// Da richiamare quando cambia interactable (MostraLobbyHost, OnServerStarted).
    /// </summary>
    private void CablaNavigazioneLobby()
    {
        Selectable copia = Interagibile(lobbyBtnCopiaCode);
        Selectable inizia = Interagibile(lobbyBtnInizia);
        Selectable annulla = Interagibile(lobbyBtnAnnulla);

        Imposta(lobbyBtnCopiaCode, null, inizia != null ? inizia : annulla, null, null);
        Imposta(lobbyBtnInizia, copia, annulla, null, null);
        Imposta(lobbyBtnAnnulla, inizia != null ? inizia : copia, null, null, null);
    }

    private static Selectable Interagibile(Selectable s)
        => s != null && s.interactable ? s : null;

    /// <summary>Rev BX-b (gate) — la selezione corrente è un elemento attivo e interagibile.</summary>
    private static bool SelezioneValida()
    {
        var go = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (go == null || !go.activeInHierarchy) return false;
        var s = go.GetComponent<Selectable>();
        return s != null && s.interactable;
    }

    /// <summary>
    /// Chiamato ogni frame da Update: gestisce Cancel e ripristina il focus se
    /// perso (es. dopo un click col mouse) non appena si usa la navigazione.
    /// </summary>
    private void HandleControllerNav()
    {
        var current = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;

        // Rev BX-b — Cancel mentre si scrive chiude solo il campo, il pannello non torna indietro.
        // Esc lo chiude il campo stesso (nel modulo UI, che gira prima di questo Update): qui il
        // campo risulta già chiuso, ma era attivo nel frame precedente. B del gamepad il campo
        // non lo riceve: lo si chiude qui.
        bool campoAttivoPrima = _campoTestoAttivoPrima;
        bool campoAttivoOra = CampoTestoAttivo(current);
        _campoTestoAttivoPrima = campoAttivoOra;

        if (CancelPremuto())
        {
            if (campoAttivoOra)
            {
                current.GetComponent<TMP_InputField>().DeactivateInputField();
                _campoTestoAttivoPrima = false;
            }
            else if (!campoAttivoPrima)
            {
                HandleCancel();
            }
            return;
        }

        if (current != null && current.activeInHierarchy)
        {
            _lastSelected = current; // memorizza l'ultimo focus valido
            return;
        }

        // Focus perso: riprendilo solo se l'utente sta usando stick/dpad/tastiera,
        // così durante l'uso a mouse non "rubiamo" il cursore.
        if (NavigazionePremuta())
        {
            var restore = (_lastSelected != null && _lastSelected.activeInHierarchy)
                ? _lastSelected.GetComponent<Selectable>() : null;
            if (restore != null && restore.interactable) SelezionaSelectable(restore);
            else SelezionaPrimario(PrimarioPerStato());
        }
    }

    // ── COLORE RUOLO: vedi classe statica RoleColors.cs (fonte unica, condivisa
    // con CharacterEntryUI per coerenza tra badge e lista personaggi) ─────────

    // ── CHARACTER CREATION ────────────────────────────────────────────────────

    private void MostraCreazione()
    {
        characterCreationPanel?.SetActive(true);
        if (creationErrorLabel != null) creationErrorLabel.gameObject.SetActive(false);
        _ruoloSelezionato = "";
        AggiornaCertColoriRuolo();

        var profile = LocalCharacterProfile.Instance;
        if (creationNameInput != null)
            creationNameInput.text = (profile.HasActiveCharacter && profile.CharacterName != "Senza nome")
                ? profile.CharacterName : "";

        // Il bottone Indietro ha senso solo se esiste già almeno un personaggio —
        // al primissimo avvio (nessun personaggio) non c'è nessun "menu principale"
        // a cui tornare: la creazione è obbligatoria per poter giocare.
        if (creationBtnIndietro != null)
            creationBtnIndietro.gameObject.SetActive(profile.HasAnyCharacter);

        CablaNavigazioneCreazione();   // Rev BX-b (Q127-a)
    }

    /// <summary>
    /// Annulla la creazione (anche se avviata da "+ Nuovo personaggio" dentro
    /// CharacterSelect) e torna sempre al Main Menu — non a CharacterSelect —
    /// per design esplicito: l'utente vuole un'uscita diretta, non un passo
    /// indietro nello stack di navigazione.
    /// </summary>
    private void OnCreationIndietro()
    {
        _creatingFromSelect = false;
        TransitionTo(Stato.MainMenu);
    }

    private void OnRuoloSelezionato(string ruolo)
    {
        _ruoloSelezionato = ruolo;
        AggiornaCertColoriRuolo();
    }

    /// <summary>
    /// Colora il bottone del ruolo scelto. Rev BX-b (gate): i bottoni ruolo hanno la transizione
    /// Color Tint, che moltiplica il colore dell'Image per il colore dello stato. Il colore "Normal"
    /// è quasi nero, quindi il verde del ruolo scelto spariva appena la selezione si spostava e si
    /// vedeva solo con il bottone selezionato (tinta quasi bianca). Ora il ruolo scelto ha Normal,
    /// Highlighted e Pressed bianchi: il verde resta visibile in ogni stato. Gli altri ruoli tornano
    /// ai colori originali della scena.
    /// </summary>
    private void AggiornaCertColoriRuolo()
    {
        for (int i = 0; i < _creationRoleButtons.Length; i++)
        {
            var bottone = _creationRoleButtons[i];
            if (bottone == null) continue;
            bool sel = NomiRuoli[i] == _ruoloSelezionato;

            var img = bottone.GetComponent<Image>();
            if (img != null) img.color = sel ? colorRuoloSelezionato : colorRuoloNormale;

            if (_coloriOriginaliRuolo != null && i < _coloriOriginaliRuolo.Length)
            {
                ColorBlock colori = _coloriOriginaliRuolo[i];
                if (sel)
                {
                    colori.normalColor = Color.white;
                    colori.highlightedColor = Color.white;
                    colori.pressedColor = Color.white;
                }
                bottone.colors = colori;
            }
        }
    }

    private void OnCreationConferma()
    {
        string nome = creationNameInput != null ? creationNameInput.text.Trim() : "";
        if (string.IsNullOrEmpty(nome)) { MostraErroreCreazione("Inserisci un nome per il personaggio."); return; }
        if (string.IsNullOrEmpty(_ruoloSelezionato)) { MostraErroreCreazione("Seleziona un ruolo per continuare."); return; }

        LocalCharacterProfile.Instance.CreateCharacter(nome, _ruoloSelezionato);

        if (_creatingFromSelect)
        {
            _creatingFromSelect = false;
            TransitionTo(Stato.CharacterSelect);
        }
        else
        {
            TransitionTo(Stato.MainMenu);
        }
    }

    private void MostraErroreCreazione(string msg)
    {
        if (creationErrorLabel == null) return;
        creationErrorLabel.text = msg;
        creationErrorLabel.gameObject.SetActive(true);
    }

    // ── MAIN MENU ─────────────────────────────────────────────────────────────

    private void MostraMainMenu()
    {
        mainMenuPanel?.SetActive(true);
        _isConnecting = false;
        _pendingAction = AzionePending.None;
        AggiornaBadgePersonaggio();
        AggiornaMessaggioSessione();
    }

    /// <summary>
    /// Rev BX-c — messaggio della sessione appena conclusa (SessionFlow), mostrato nel pannello
    /// principale finché non si passa a un altro pannello. Vuoto: testo nascosto.
    /// </summary>
    private void AggiornaMessaggioSessione()
    {
        if (mainSessionMessageText == null) return;
        bool mostra = !string.IsNullOrEmpty(_messaggioSessione);
        mainSessionMessageText.text = mostra ? _messaggioSessione : string.Empty;
        mainSessionMessageText.gameObject.SetActive(mostra);
    }

    private void AggiornaBadgePersonaggio()
    {
        var profile = LocalCharacterProfile.Instance;
        bool haPersonaggio = profile.HasActiveCharacter;

        if (mainCharacterNameText != null)
            mainCharacterNameText.text = haPersonaggio ? profile.CharacterName : "Nessun personaggio";

        if (mainCharacterRoleText != null)
            mainCharacterRoleText.text = haPersonaggio ? profile.Role : "—";

        if (mainCharacterDot != null)
            mainCharacterDot.color = haPersonaggio ? RoleColors.Get(profile.Role) : colorRuoloNormale;

        if (mainBtnNuovaPartita != null) mainBtnNuovaPartita.interactable = haPersonaggio;
        if (mainBtnUnisciti != null) mainBtnUnisciti.interactable = haPersonaggio;
    }

    private void OnCambiaPersonaggio()
    {
        _pendingAction = AzionePending.None;
        TransitionTo(Stato.CharacterSelect);
    }

    private void OnNuovaPartita()
    {
        _pendingAction = AzionePending.NuovaPartita;
        TransitionTo(LocalCharacterProfile.Instance.HasActiveCharacter
            ? Stato.SessionType
            : Stato.CharacterSelect);
    }

    private void OnUniscitiMainMenu()
    {
        _pendingAction = AzionePending.Unisciti;
        TransitionTo(LocalCharacterProfile.Instance.HasActiveCharacter
            ? Stato.Join
            : Stato.CharacterSelect);
    }

    // ── CHARACTER SELECT ──────────────────────────────────────────────────────

    private void MostraCharacterSelect()
    {
        characterSelectPanel?.SetActive(true);
        _selectedCharId = LocalCharacterProfile.Instance.CharacterId;
        RicostruisciListaPersonaggi();
    }

    private void RicostruisciListaPersonaggi()
    {
        if (selectListContainer == null || characterEntryPrefab == null) return;

        foreach (Transform child in selectListContainer) Destroy(child.gameObject);

        // Rev BX-b — voci nuove raccolte qui: le vecchie restano figlie fino a fine frame (Destroy differito).
        _vociPersonaggi.Clear();
        foreach (var data in LocalCharacterProfile.Instance.GetAllCharacters())
        {
            var go = Instantiate(characterEntryPrefab, selectListContainer);
            var entry = go.GetComponent<CharacterEntryUI>();
            entry?.Bind(data, data.characterId == _selectedCharId, OnPersonaggioCliccato);

            var voce = go.GetComponent<Selectable>();
            if (voce != null) _vociPersonaggi.Add(voce);
        }

        CablaNavigazioneSelezione();   // Rev BX-b (Q127-a)
    }

    private void OnPersonaggioCliccato(string characterId)
    {
        _selectedCharId = characterId;
        foreach (Transform child in selectListContainer)
        {
            var entry = child.GetComponent<CharacterEntryUI>();
            if (entry != null) entry.SetSelected(entry.CharacterId == characterId);
        }
    }

    private void OnNuovoPersonaggio()
    {
        _creatingFromSelect = true;
        TransitionTo(Stato.CharacterCreation);
    }

    private void OnSelectConferma()
    {
        if (!string.IsNullOrEmpty(_selectedCharId))
            LocalCharacterProfile.Instance.SelectCharacter(_selectedCharId);

        switch (_pendingAction)
        {
            case AzionePending.NuovaPartita: TransitionTo(Stato.SessionType); break;
            case AzionePending.Unisciti: TransitionTo(Stato.Join); break;
            default: TransitionTo(Stato.MainMenu); break;
        }
    }

    // ── SESSION TYPE ──────────────────────────────────────────────────────────

    private void MostraSessionType() => sessionTypePanel?.SetActive(true);

    private void OnTipoSessione(TipoSessione tipo)
    {
        _tipoSessione = tipo;
        TransitionTo(Stato.LobbyHost);
        AvviaHost();
    }

    // ── LOBBY HOST ────────────────────────────────────────────────────────────

    private void MostraLobbyHost()
    {
        lobbyHostPanel?.SetActive(true);
        if (lobbySessionTypeBadge != null)
            lobbySessionTypeBadge.text = _tipoSessione == TipoSessione.Aperta ? "APERTA" : "SU INVITO";
        if (lobbyJoinCodeText != null) lobbyJoinCodeText.text = "Avvio in corso...";
        if (lobbyBtnInizia != null) lobbyBtnInizia.interactable = false;
        if (lobbyBtnCopiaCode != null) lobbyBtnCopiaCode.interactable = false;
        CablaNavigazioneLobby();   // Rev BX-b (gate)
        AggiornaContatoreGiocatori();
        InvokeRepeating(nameof(AggiornaContatoreGiocatori), 0f, 1f);
    }

    private async void AvviaHost()
    {
        _isConnecting = true;
        try
        {
            if (relayManager != null && relayManager.IsServiceReady)
                await relayManager.StartHostAsync();
            else
                NetworkManager.Singleton.StartHost();
        }
        catch (Exception e)
        {
            Debug.LogError($"[MainMenuManager] Errore avvio host: {e.Message}");
            CancelInvoke(nameof(AggiornaContatoreGiocatori));
            TransitionTo(Stato.MainMenu);
        }
        _isConnecting = false;
    }

    private void OnCopiaCode()
    {
        if (lobbyJoinCodeText != null)
            GUIUtility.systemCopyBuffer = lobbyJoinCodeText.text;
    }

    private void OnIniziaPartita()
    {
        if (!NetworkManager.Singleton.IsServer) return;
        CancelInvoke(nameof(AggiornaContatoreGiocatori));
        if (menuCanvas != null) menuCanvas.gameObject.SetActive(false);
        NetworkManager.Singleton.SceneManager.LoadScene(GAME_SCENE_NAME, LoadSceneMode.Single);
    }

    private void OnAnnullaHost()
    {
        CancelInvoke(nameof(AggiornaContatoreGiocatori));
        NetworkManager.Singleton.Shutdown();
        TransitionTo(Stato.MainMenu);
    }

    private void AggiornaContatoreGiocatori()
    {
        if (lobbyPlayerCountText == null || NetworkManager.Singleton == null) return;
        int n = NetworkManager.Singleton.IsServer
            ? NetworkManager.Singleton.ConnectedClientsIds.Count : 0;
        lobbyPlayerCountText.text = $"Equipaggio a bordo: {n} / 5";
    }

    // ── JOIN ──────────────────────────────────────────────────────────────────

    private void MostraJoin()
    {
        joinPanel?.SetActive(true);
        if (joinStatusText != null) joinStatusText.text = "Inserisci il codice ricevuto dall'host.";
        if (joinBtnConferma != null) joinBtnConferma.interactable = true;
        if (joinBtnIndietro != null) joinBtnIndietro.interactable = true;
    }

    private async void OnJoinConferma()
    {
        if (_isConnecting) return;
        string codice = joinCodeInput != null ? joinCodeInput.text.Trim().ToUpper() : "";
        if (string.IsNullOrEmpty(codice))
        {
            if (joinStatusText != null) joinStatusText.text = "Inserisci il codice per continuare.";
            return;
        }

        _isConnecting = true;
        _codiceInserito = codice;   // Rev BX-c
        if (joinBtnConferma != null) joinBtnConferma.interactable = false;
        if (joinBtnIndietro != null) joinBtnIndietro.interactable = false;
        if (joinStatusText != null) joinStatusText.text = "Connessione in corso...";

        try
        {
            if (relayManager != null)
                await relayManager.StartClientAsync(codice);
            else
                throw new InvalidOperationException("RelayManager non trovato.");
        }
        catch (Exception e)
        {
            if (joinStatusText != null) joinStatusText.text = $"Errore: {e.Message}";
            if (joinBtnConferma != null) joinBtnConferma.interactable = true;
            if (joinBtnIndietro != null) joinBtnIndietro.interactable = true;
            _isConnecting = false;
        }
    }

    // ── NETWORK CALLBACKS ─────────────────────────────────────────────────────

    private void OnServerStarted()
    {
        string codice = relayManager != null && !string.IsNullOrEmpty(relayManager.LastJoinCode)
            ? relayManager.LastJoinCode : "(locale)";

        if (lobbyJoinCodeText != null) lobbyJoinCodeText.text = codice;
        if (lobbyBtnInizia != null) lobbyBtnInizia.interactable = true;
        if (lobbyBtnCopiaCode != null) lobbyBtnCopiaCode.interactable = true;

        // Rev BX-b (gate) — bottoni accesi: navigazione ricablata. Se la selezione è andata persa
        // (click sullo sfondo), torna su "Inizia"; altrimenti resta dov'è.
        CablaNavigazioneLobby();
        if (_stato == Stato.LobbyHost && enableControllerNav && _focusRoutine == null && !SelezioneValida())
            SelezionaPrimario(PrimarioPerStato());

        // Rev BX-c — codice per il menu di pausa (in partita RelayManager non c'è più).
        bool conRelay = relayManager != null && !string.IsNullOrEmpty(relayManager.LastJoinCode);
        SessionFlow.SetJoinCode(conRelay ? relayManager.LastJoinCode : "(local)");
    }

    private void OnClientConnected(ulong clientId)
    {
        if (_stato == Stato.LobbyHost) AggiornaContatoreGiocatori();

        bool èClientPuro = NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost;
        bool èClientLocale = clientId == NetworkManager.Singleton.LocalClientId;

        if (èClientPuro && èClientLocale && joinStatusText != null)
            joinStatusText.text = "Connesso. In attesa dell'host...";

        // Rev BX-c — codice per il menu di pausa: quello con cui ci si è uniti.
        if (èClientPuro && èClientLocale)
            SessionFlow.SetJoinCode(_codiceInserito);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (_stato == Stato.LobbyHost) AggiornaContatoreGiocatori();
    }

    private void OnRelayReady()
    {
        LogV("[MainMenuManager] Relay pronto — partite cross-internet disponibili.");
    }

    // ── CURSORE ───────────────────────────────────────────────────────────────

    private void Update()
    {
        if (menuCanvas != null && menuCanvas.gameObject.activeSelf)
        {
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (enableControllerNav) HandleControllerNav();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        DebugUpdate();
#endif
    }

    // ── DEBUG BYPASS ──────────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [Header("Debug")]
    [Tooltip("Salta il menu e avvia un host locale per test rapidi in Editor.")]
    [SerializeField] private bool debugSkipMenu = false;
    private bool _debugSkipDone = false;

    private void DebugUpdate()
    {
        if (!debugSkipMenu || _debugSkipDone) return;
        _debugSkipDone = true;

        if (!LocalCharacterProfile.Instance.HasAnyCharacter)
            LocalCharacterProfile.Instance.CreateCharacter("DEBUG", "Pilota");

        if (menuCanvas != null) menuCanvas.gameObject.SetActive(false);
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        NetworkManager.Singleton.StartHost();
        LogV("[MainMenuManager] debugSkipMenu — host locale avviato.");
    }
#endif
}