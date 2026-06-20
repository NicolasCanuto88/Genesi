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
    [Tooltip("ContentContainer/RoleContainer/Medico")]
    [SerializeField] private Button creationBtnMedico;
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
        AggiornaBadgePersonaggio();
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