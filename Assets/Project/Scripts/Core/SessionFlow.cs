using UnityEngine;

/// <summary>
/// SessionFlow — Rev BX-c (Q121-a / Q123-a / Q128-a). Stato della sessione che deve sopravvivere al
/// cambio di scena tra Game e MainMenu. Classe statica senza MonoBehaviour: niente oggetti in scena.
///
/// QuitGame: uscita dal gioco, unica per EXIT GAME del menu principale e QUIT TO DESKTOP della
/// pausa. In Editor ferma il Play; in build Application.Quit (NGO chiude la rete in
/// OnApplicationQuit: per l'host disconnette i client).
///
/// CONTENUTO:
///   - JoinCode: codice della sessione corrente (Relay), mostrato nel menu di pausa per far
///     rientrare chi cade. L'host lo imposta all'avvio (MainMenuManager.OnServerStarted), il client
///     quando si connette con il codice inserito. "(local)" senza Relay.
///   - IsLeaving: true dall'inizio dell'uscita verso il menu principale fino al menu stesso.
///     Distingue un'uscita voluta (Leave / End Session) da una disconnessione subita.
///   - Messaggio di uscita: impostato da chi esce (vuoto se l'uscita è voluta), letto una volta da
///     MainMenuManager all'arrivo nel menu (ConsumeExitMessage), che azzera anche il resto.
///
/// La sequenza di uscita (Shutdown, attesa, distruzione del NetworkManager, caricamento di
/// MainMenu) vive in PauseMenuController, che ha un MonoBehaviour per la coroutine.
/// </summary>
public static class SessionFlow
{
    /// <summary>Nome della scena del menu principale in Build Settings (come GAME_SCENE_NAME altrove).</summary>
    public const string MainMenuSceneName = "MainMenu";

    /// <summary>Codice della sessione corrente; vuoto se non ce n'è.</summary>
    public static string JoinCode { get; private set; } = string.Empty;

    /// <summary>True dall'inizio dell'uscita verso il menu principale.</summary>
    public static bool IsLeaving { get; private set; }

    private static string s_ExitMessage = string.Empty;

    /// <summary>Codice della sessione (host all'avvio, client alla connessione).</summary>
    public static void SetJoinCode(string code) => JoinCode = code ?? string.Empty;

    /// <summary>
    /// Inizio dell'uscita. message: testo da mostrare nel menu principale (vuoto o null se l'uscita
    /// è voluta). Idempotente: la prima chiamata vince, così una disconnessione arrivata durante
    /// un'uscita voluta non sovrascrive nulla.
    /// </summary>
    public static void BeginLeave(string message)
    {
        if (IsLeaving) return;
        IsLeaving = true;
        s_ExitMessage = message ?? string.Empty;
    }

    /// <summary>
    /// Letto all'arrivo nel menu principale: restituisce il messaggio di uscita (vuoto se nessuno)
    /// e azzera lo stato della sessione conclusa.
    /// </summary>
    public static string ConsumeExitMessage()
    {
        string message = s_ExitMessage;
        s_ExitMessage = string.Empty;
        IsLeaving = false;
        JoinCode = string.Empty;
        return message;
    }

    /// <summary>
    /// Esce dal gioco (Q128-a). Segna l'uscita come voluta, così una disconnessione arrivata durante
    /// la chiusura non avvia anche il ritorno al menu principale.
    /// </summary>
    public static void QuitGame()
    {
        BeginLeave(string.Empty);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
