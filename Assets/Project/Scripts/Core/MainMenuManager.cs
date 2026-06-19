using System;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

#if UNITY_EDITOR
using ParrelSync;
#endif

/// <summary>
/// MainMenuManager — Milestone 3, Blocco 1 (Frontend &amp; Identità).
///
/// State machine a 6 stati che orchestra l'intero flusso pre-partita:
///
///   CharacterCreation → creazione nuovo personaggio (primo accesso o nuovo)
///   MainMenu          → schermata principale con azioni disponibili
///   CharacterSelect   → selezione/cambio personaggio
///   SessionType       → tipo sessione (Aperta / Su invito) prima di hostare
///   LobbyHost         → attesa giocatori con join code, poi avvio partita
///   Join              → inserimento codice e connessione come client
///
/// FLUSSO PRIMO ACCESSO:
///   CharacterCreation → MainMenu
///
/// FLUSSO NUOVA PARTITA (personaggio già esistente):
///   MainMenu → SessionType → LobbyHost → "Inizia" → LoadScene("Game")
///
/// FLUSSO NUOVA PARTITA (nessun personaggio selezionato):
///   MainMenu → CharacterSelect → SessionType → LobbyHost → "Inizia"
///
/// FLUSSO UNISCITI:
///   MainMenu → Join → [connessione] → auto-load Game (NGO lo gestisce)
///
/// PARRELSYNC: il clone non vede nessun menu — AutoStartHost avvia il client
/// automaticamente, il canvas viene disabilitato in Awake.
///
/// CAMBIO SCENA: l'host chiama NetworkManager.Singleton.SceneManager.LoadScene
/// ("Game", Single) → NGO replica il cambio a tutti i client connessi. Tutti
/// i GameObject di MainMenu.unity vengono distrutti; NetworkManager sopravvive
/// (DontDestroyOnLoad gestito internamente da NGO).
///
/// NOTE SCENA: "Game" deve corrispondere ESATTAMENTE al nome del file
/// Game.unity in Build Settings (vedi guida Editor).
/// </summary>
public class MainMenuManager : MonoBehaviour
{
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

    [Header("Character Creation")]
    [SerializeField] private TMP_InputField creationNameInput;
    [SerializeField] private Button creationBtnPilota;
    [SerializeField] private Button creationBtnIngegnere;
    [SerializeField] private Button creationBtnScanner;
    [SerializeField] private Button creationBtnMedico;
    [SerializeField] private TextMeshProUGUI creationErrorLabel;
    [SerializeField] private Button creationBtnConferma;

    // ── MAIN MENU ─────────────────────────────────────────────────────────────

    [Header("Main Menu")]
    [SerializeField] private TextMeshProUGUI mainActiveCharacterText;
    [SerializeField] private Button mainBtnCambiaPersonaggio;
    [SerializeField] private Button mainBtnNuovaPartita;
    [SerializeField] private Button mainBtnCarica;
    [SerializeField] private Button mainBtnUnisciti;
    [SerializeField] private Button mainBtnOpzioni;
    [SerializeField] private Button mainBtnCrediti;

    // ── CHARACTER SELECT ──────────────────────────────────────────────────────

    [Header("Character Select")]
    [SerializeField] private Transform selectListContainer;
    [SerializeField] private GameObject characterEntryPrefab;
    [SerializeField] private Button selectBtnNuovoPersonaggio;
    [SerializeField] private Button selectBtnConferma;
    [SerializeField] private Button selectBtnIndietro;

    // ── SESSION TYPE ──────────────────────────────────────────────────────────

    [Header("Session Type")]
    [SerializeField] private Button sessionBtnAperta;
    [SerializeField] private Button sessionBtnSuInvito;
    [SerializeField] private Button sessionBtnIndietro;

    // ── LOBBY HOST ────────────────────────────────────────────────────────────

    [Header("Lobby Host")]
    [SerializeField] private TextMeshProUGUI lobbySessionTypeBadge;
    [SerializeField] private TextMeshProUGUI lobbyJoinCodeText;
    [SerializeField] private Button lobbyBtnCopiaCode;
    [SerializeField] private TextMeshProUGUI lobbyPlayerCountText;
    [SerializeField] private Button lobbyBtnInizia;
    [SerializeField] private Button lobbyBtnAnnulla;

    // ── JOIN ──────────────────────────────────────────────────────────────────

    [Header("Join")]
    [SerializeField] private TMP_InputField joinCodeInput;
    [SerializeField] private TextMeshProUGUI joinStatusText;
    [SerializeField] private Button joinBtnConferma;
    [SerializeField] private Button joinBtnIndietro;

    // ── COLORI ────────────────────────────────────────────────────────────────

    [Header("Colori ruolo")]
    [SerializeField] private Color colorRuoloNormale = new Color(0.12f, 0.14f, 0.18f, 1f);
    [SerializeField] private Color colorRuoloSelezionato = new Color(0.18f, 0.72f, 0.36f, 1f);

    // ── RIFERIMENTI ───────────────────────────────────────────────────────────

    [Header("Riferimenti")]
    [SerializeField] private RelayManager relayManager;

    // ── STATE MACHINE ─────────────────────────────────────────────────────────

    private enum Stato { CharacterCreation, MainMenu, CharacterSelect, SessionType, LobbyHost, Join }
    private enum AzionePending { None, NuovaPartita, Unisciti }
    private enum TipoSessione { Aperta, SuInvito }

    private Stato _stato = Stato.CharacterCreation;
    private AzionePending _pendingAction = AzionePending.None;
    private TipoSessione _tipoSessione = TipoSessione.SuInvito;
    private bool _creatingFromSelect = false;
    private bool _isConnecting = false;
    private string _selectedCharId = "";
    private string _ruoloSelezionato = "";

    private static readonly string[] NomiRuoli = { "Pilota", "Ingegnere", "Scanner", "Medico" };
    private Button[] _creationRoleButtons;

    // ── LIFECYCLE ─────────────────────────────────────────────────────────────

    private void Awake()
    {
#if UNITY_EDITOR
        if (ClonesManager.IsClone())
        {
            if (menuCanvas != null) menuCanvas.gameObject.SetActive(false);
            enabled = false;
            return;
        }
#endif
        _creationRoleButtons = new[] {
            creationBtnPilota, creationBtnIngegnere,
            creationBtnScanner, creationBtnMedico
        };
    }

    private void Start()
    {
#if UNITY_EDITOR
        if (!enabled) return;
#endif
        WireButtons();

        NetworkManager.Singleton.OnServerStarted += OnServerStarted;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        RelayManager.OnServiceReady += OnRelayReady;

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
        // Ruolo
        for (int i = 0; i < _creationRoleButtons.Length; i++)
        {
            int idx = i;
            if (_creationRoleButtons[idx] != null)
                _creationRoleButtons[idx].onClick.AddListener(() => OnRuoloSelezionato(NomiRuoli[idx]));
        }
        if (creationBtnConferma != null) creationBtnConferma.onClick.AddListener(OnCreationConferma);

        // Main Menu
        if (mainBtnCambiaPersonaggio != null) mainBtnCambiaPersonaggio.onClick.AddListener(OnCambiaPersonaggio);
        if (mainBtnNuovaPartita != null) mainBtnNuovaPartita.onClick.AddListener(OnNuovaPartita);
        if (mainBtnUnisciti != null) mainBtnUnisciti.onClick.AddListener(OnUniscitiMainMenu);
        if (mainBtnCarica != null) mainBtnCarica.interactable = false;
        if (mainBtnOpzioni != null) mainBtnOpzioni.interactable = false;
        if (mainBtnCrediti != null) mainBtnCrediti.interactable = false;

        // Character Select
        if (selectBtnNuovoPersonaggio != null) selectBtnNuovoPersonaggio.onClick.AddListener(OnNuovoPersonaggio);
        if (selectBtnConferma != null) selectBtnConferma.onClick.AddListener(OnSelectConferma);
        if (selectBtnIndietro != null) selectBtnIndietro.onClick.AddListener(() => TransitionTo(Stato.MainMenu));

        // Session Type
        if (sessionBtnAperta != null) sessionBtnAperta.onClick.AddListener(() => OnTipoSessione(TipoSessione.Aperta));
        if (sessionBtnSuInvito != null) sessionBtnSuInvito.onClick.AddListener(() => OnTipoSessione(TipoSessione.SuInvito));
        if (sessionBtnIndietro != null) sessionBtnIndietro.onClick.AddListener(() => TransitionTo(Stato.MainMenu));

        // Lobby Host
        if (lobbyBtnCopiaCode != null) lobbyBtnCopiaCode.onClick.AddListener(OnCopiaCode);
        if (lobbyBtnInizia != null) lobbyBtnInizia.onClick.AddListener(OnIniziaPartita);
        if (lobbyBtnAnnulla != null) lobbyBtnAnnulla.onClick.AddListener(OnAnnullaHost);

        // Join
        if (joinBtnConferma != null) joinBtnConferma.onClick.AddListener(OnJoinConferma);
        if (joinBtnIndietro != null) joinBtnIndietro.onClick.AddListener(() => TransitionTo(Stato.MainMenu));
    }

    // ── STATE MACHINE ─────────────────────────────────────────────────────────

    private void TransitionTo(Stato nuovoStato)
    {
        _stato = nuovoStato;

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
    }

    // ── CHARACTER CREATION ────────────────────────────────────────────────────

    private void MostraCreazione()
    {
        characterCreationPanel?.SetActive(true);
        if (creationErrorLabel != null) creationErrorLabel.gameObject.SetActive(false);
        _ruoloSelezionato = "";
        AggiornaCertColoriRuolo();

        // Precompila il nome se il personaggio attivo ne ha già uno
        var profile = LocalCharacterProfile.Instance;
        if (creationNameInput != null && profile.HasActiveCharacter
            && profile.CharacterName != "Senza nome")
            creationNameInput.text = profile.CharacterName;
        else if (creationNameInput != null)
            creationNameInput.text = "";
    }

    private void OnRuoloSelezionato(string ruolo)
    {
        _ruoloSelezionato = ruolo;
        AggiornaCertColoriRuolo();
    }

    private void AggiornaCertColoriRuolo()
    {
        for (int i = 0; i < _creationRoleButtons.Length; i++)
        {
            if (_creationRoleButtons[i] == null) continue;
            bool sel = NomiRuoli[i] == _ruoloSelezionato;
            var img = _creationRoleButtons[i].GetComponent<Image>();
            if (img != null) img.color = sel ? colorRuoloSelezionato : colorRuoloNormale;
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
        AggiornaPannelloMainMenu();
    }

    private void AggiornaPannelloMainMenu()
    {
        var profile = LocalCharacterProfile.Instance;
        bool haPersonaggio = profile.HasActiveCharacter;

        if (mainActiveCharacterText != null)
            mainActiveCharacterText.text = haPersonaggio
                ? $"{profile.CharacterName}  ·  {profile.Role}"
                : "Nessun personaggio — creane uno per iniziare.";

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

        foreach (var data in LocalCharacterProfile.Instance.GetAllCharacters())
        {
            var go = Instantiate(characterEntryPrefab, selectListContainer);
            var entry = go.GetComponent<CharacterEntryUI>();
            entry?.Bind(data, data.characterId == _selectedCharId, OnPersonaggioCliccato);
        }
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
        lobbyPlayerCountText.text = $"{n} / 5 giocatori";
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
    }

    private void OnClientConnected(ulong clientId)
    {
        if (_stato == Stato.LobbyHost) AggiornaContatoreGiocatori();

        // Client puro connesso: aspetta che l'host carichi la scena
        bool èClientPuro = NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost;
        bool èClientLocale = clientId == NetworkManager.Singleton.LocalClientId;

        if (èClientPuro && èClientLocale && joinStatusText != null)
            joinStatusText.text = "Connesso. In attesa dell'host...";
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (_stato == Stato.LobbyHost) AggiornaContatoreGiocatori();
    }

    private void OnRelayReady()
    {
        Debug.Log("[MainMenuManager] Relay pronto — partite cross-internet disponibili.");
    }

    // ── CURSORE ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Forza il cursore libero ogni frame finché il canvas è visibile.
    /// Protezione contro PlayerController.Awake() che può bloccare il cursore
    /// nel brevissimo intervallo prima che OnNetworkSpawn lo sblocchi.
    /// </summary>
    private void Update()
    {
        if (menuCanvas != null && menuCanvas.gameObject.activeSelf)
        {
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
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
        Debug.Log("[MainMenuManager] debugSkipMenu — host locale avviato.");
    }
#endif
}