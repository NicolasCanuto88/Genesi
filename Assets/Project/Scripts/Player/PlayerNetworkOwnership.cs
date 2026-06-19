using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// PlayerNetworkOwnership — Rev O (aggiornato Blocco 1 M3).
///
/// Gestisce la visibilità e il controllo del Player locale vs remoto,
/// con consapevolezza della scena in cui ci si trova.
///
/// PROBLEMA RISOLTO (doppio):
///
/// 1. COMPONENTI DISATTIVATE IN GAME
///    OnNetworkSpawn scatta UNA sola volta — quando il Player spawna in
///    MainMenu. Se l'oggetto sopravvive al cambio scena, non viene mai
///    rieabilitato. Fix: quando in MainMenu ci sottoscriviamo a
///    SceneManager.sceneLoaded; quando Game.unity carica, riabilitiamo
///    Camera/Audio/Input/Controller per il proprietario locale.
///
/// 2. CURSORE CHE SI BLOCCA IN MAINMENU
///    PlayerController.Awake() può bloccare il cursore prima che
///    OnNetworkSpawn lo sblocchi. Gestito in MainMenuManager.Update()
///    (non qui), che forza CursorLockMode.None finché il canvas è attivo.
///
/// LOGICA SCENA:
///
///   Spawn in MainMenu → disabilita tutto (anche IsOwner) + unlock cursor
///                      → sottoscrive sceneLoaded per rieabilitare in Game
///
///   Spawn in Game + IsOwner  → non tocca nulla (tutto già attivo)
///   Spawn in Game + !IsOwner → disabilita Camera/Audio/Input/Controller
///
/// La costante GAME_SCENE_NAME deve corrispondere esattamente al nome del
/// file Game.unity in Build Settings (uguale a MainMenuManager).
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class PlayerNetworkOwnership : NetworkBehaviour
{
    private const string GAME_SCENE_NAME = "Game";

    private PlayerController playerController;
    private PlayerInput playerInput;
    private Camera playerCamera;
    private AudioListener audioListener;

    private void Awake()
    {
        playerController = GetComponent<PlayerController>();
        playerInput = GetComponent<PlayerInput>();
        playerCamera = GetComponentInChildren<Camera>();
        if (playerCamera != null)
            audioListener = playerCamera.GetComponent<AudioListener>();
    }

    public override void OnNetworkSpawn()
    {
        bool inGame = SceneManager.GetActiveScene().name == GAME_SCENE_NAME;

        if (!inGame)
        {
            // Spawn in MainMenu: disabilita tutto indipendentemente dall'ownership.
            // Il cursore viene sbloccato qui come prima misura; MainMenuManager.Update()
            // lo mantiene libero ogni frame come protezione aggiuntiva.
            DisabilitaComponenti();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Ascolta il caricamento di Game per rieabilitare i componenti del
            // giocatore locale quando la partita davvero inizia.
            if (IsOwner)
                SceneManager.sceneLoaded += OnSceneLoaded;

            return;
        }

        // Spawn in Game.unity — comportamento normale.
        if (IsOwner) return; // il proprio Player resta intatto

        DisabilitaComponenti();
    }

    public override void OnNetworkDespawn()
    {
        // Pulizia: deregistra sempre per evitare memory leak se il Player
        // viene despawnato prima che Game.unity carichi.
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != GAME_SCENE_NAME) return;

        // Ci siamo: Game.unity è caricata. Deregistra subito (fired una volta sola).
        SceneManager.sceneLoaded -= OnSceneLoaded;

        if (!IsOwner) return;

        // Riabilita tutti i componenti per il giocatore locale.
        if (playerController != null) playerController.enabled = true;
        if (playerInput != null) playerInput.enabled = true;
        if (playerCamera != null) playerCamera.enabled = true;
        if (audioListener != null) audioListener.enabled = true;
    }

    private void DisabilitaComponenti()
    {
        if (playerController != null) playerController.enabled = false;
        if (playerInput != null) playerInput.enabled = false;
        if (playerCamera != null) playerCamera.enabled = false;
        if (audioListener != null) audioListener.enabled = false;
    }
}