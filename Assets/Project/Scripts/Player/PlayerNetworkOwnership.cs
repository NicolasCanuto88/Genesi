using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// PlayerNetworkOwnership — Milestone 2.
/// Disabilita le componenti locali (camera, audio listener, controller, input)
/// su ogni istanza spawnata del Player che NON appartiene al client locale.
///
/// CONTESTO: prima di questa sessione il Player non era mai stato un
/// NetworkObject spawnato da NGO — viveva come singolo GameObject piazzato a
/// mano in scena (Game.unity), copia indipendente per ogni istanza Unity, mai
/// sincronizzata. Configurando Player come Player Prefab di NetworkManager
/// (richiesto da PlayerHealthSystem), NGO spawna ORA un'istanza per ogni
/// client connesso SU TUTTI I CLIENT — quindi ogni macchina vede sia il
/// proprio Player sia quello degli altri.
///
/// PROBLEMA: PlayerController, PlayerInput, Camera e AudioListener non hanno
/// alcuna nozione di ownership di rete. Senza questo script, ogni istanza
/// spawnata (anche quelle remote) avrebbe: PlayerInput che ascolta lo stesso
/// dispositivo locale (quindi WASD muoverebbe TUTTI i Player presenti nella
/// scena, non solo il proprio); una Camera attiva in competizione con quella
/// del proprio Player; un secondo AudioListener (Unity stampa il warning
/// "There are 2 audio listeners in the scene").
///
/// SOLUZIONE: su OnNetworkSpawn, se !IsOwner, disabilita Camera, AudioListener,
/// PlayerController e PlayerInput su QUESTA istanza. L'istanza posseduta dal
/// client locale (IsOwner == true) non viene toccata — comportamento identico
/// a oggi su singolo giocatore.
///
/// ⚠️ Cosa NON risolve questo script: la posizione/rotazione del Player
/// remoto non è sincronizzata in rete (nessun NetworkTransform ancora) — un
/// Player remoto resta visivamente fermo nel punto di spawn. Sincronizzare il
/// movimento è lavoro di M3 ("Sessione multiplayer reale (2+ client)", già in
/// roadmap) — qui ci si limita a evitare i problemi di camera/input/audio
/// duplicati, senza introdurre alcun sync di trasformazione.
///
/// ⚠️ TabletStation/InteractionSystem/FootstepController non vengono
/// disabilitati esplicitamente qui: TabletStation dipende dal SendMessages di
/// PlayerInput, che viene già disabilitato sopra — quindi smette comunque di
/// reagire all'input su un'istanza remota senza bisogno di toccarla.
/// InteractionSystem e FootstepController restano tecnicamente attivi ma
/// inerti (nessun movimento da rilevare, eventuale raycast dalla camera
/// disabilitata ma comunque valida come Transform) — non causano errori,
/// semplicemente non producono alcun effetto visibile.
///
/// ⚠️ Setup Editor: aggiungere questo componente sullo stesso GameObject
/// radice di Player.prefab (dove vivono già PlayerController, PlayerInput,
/// NetworkObject, PlayerHealthSystem). Nessun campo da assegnare in
/// Inspector — Camera e AudioListener vengono trovati automaticamente in
/// Awake, stesso pattern già usato da TabletStation per playerCamera.
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class PlayerNetworkOwnership : NetworkBehaviour
{
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
        if (IsOwner) return; // Il proprio Player resta intatto — nessun cambiamento per il single-player.

        if (playerController != null) playerController.enabled = false;
        if (playerInput != null) playerInput.enabled = false;
        if (playerCamera != null) playerCamera.enabled = false;
        if (audioListener != null) audioListener.enabled = false;
    }
}
