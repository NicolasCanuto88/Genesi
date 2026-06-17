using System;
using System.IO;
using UnityEngine;

/// <summary>
/// LocalCharacterProfile — Milestone 2.
///
/// Dati del personaggio del GIOCATORE, persistenti SUL SUO DISPOSITIVO,
/// indipendenti da qualsiasi lobby/sessione (GDD §2B "Personal Account").
///
/// REGOLA DI DESIGN (decisa dall'utente in questa sessione):
///   I crediti personali non sono mai proprietà del server di UNA sessione —
///   sono proprietà del personaggio. Un host può solo CHIEDERE di aggiungerne
///   (tramite EconomyManager.RequestTransferToPlayerRpc → ReceiveFleetPaymentRpc),
///   ma non può mai leggerli o sottrarli direttamente da remoto.
///
///   Conseguenza pratica: un giocatore può unirsi alla sessione di un host
///   qualsiasi, ricevere un pagamento, lasciare quella sessione e ritrovare
///   lo stesso saldo entrando nella sessione di un host diverso — perché il
///   saldo non vive mai sul server di nessuno dei due, vive qui.
///
/// PERSISTENZA: file JSON in Application.persistentDataPath. Scelta minima per
/// sbloccare la feature subito; sostituibile in futuro con Steam Cloud o un
/// backend dedicato senza toccare il resto del codice (stesso debito tecnico
/// già segnalato in Rev M per il Fleet Account: "tecnologia da scegliere in M3").
///
/// ⚠️ STUB — un solo "slot" personaggio per dispositivo. La selezione/creazione
/// di personaggi multipli con ruoli diversi (GDD §2B) non è ancora implementata:
/// dipende da un futuro sistema di character creation (GDD §10, da completare).
/// Quando esisterà, questo componente dovrà diventare "il profilo CARICATO",
/// non più l'unico esistente — l'API pubblica (CharacterName/Role/PersonalCredits/
/// AddPersonalCredits/TrySpendPersonalCredits) può restare la stessa.
///
/// ⚠️ Da posizionare su un GameObject persistente in scena (es. lo stesso
/// "Bootstrap"/Managers che contiene InputDeviceManager) — non è un NetworkBehaviour,
/// non va spawnato in rete, esiste indipendentemente da qualunque sessione NGO.
/// </summary>
public class LocalCharacterProfile : MonoBehaviour
{
    private const string SAVE_FILE_NAME = "character_save.json";

    public static LocalCharacterProfile Instance { get; private set; }

    /// <summary>Fired localmente ogni volta che il saldo personale cambia (guadagno o spesa).</summary>
    public static event Action<int> OnPersonalCreditsChanged;

    [Serializable]
    private class SaveData
    {
        public string characterId;
        public string characterName = "Senza nome";
        public string role = "Non assegnato";
        public int personalCredits = 0;
    }

    private SaveData data;

    public string CharacterId     => data.characterId;
    public string CharacterName   => data.characterName;
    public string Role            => data.role;
    public int    PersonalCredits => data.personalCredits;

    private string SavePath => Path.Combine(Application.persistentDataPath, SAVE_FILE_NAME);

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(SavePath))
            {
                string json = File.ReadAllText(SavePath);
                data = JsonUtility.FromJson<SaveData>(json);
                if (data == null) data = CreateNew();
            }
            else
            {
                data = CreateNew();
                Save();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[LocalCharacterProfile] Errore caricamento salvataggio: {e.Message}. Creo un profilo nuovo.");
            data = CreateNew();
        }
    }

    private SaveData CreateNew()
    {
        return new SaveData
        {
            characterId = Guid.NewGuid().ToString(),
            characterName = "Senza nome",
            role = "Non assegnato",
            personalCredits = 0
        };
    }

    private void Save()
    {
        try
        {
            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(SavePath, json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[LocalCharacterProfile] Errore salvataggio: {e.Message}");
        }
    }

    /// <summary>
    /// Aggiunge crediti personali e salva immediatamente su disco.
    /// Chiamato da EconomyManager.ReceiveFleetPaymentRpc quando l'host trasferisce
    /// crediti dal Fleet Account, oppure (M3+) da vendite personali / shop in stazione.
    /// </summary>
    public void AddPersonalCredits(int amount)
    {
        if (amount == 0) return;

        data.personalCredits = Mathf.Max(0, data.personalCredits + amount);
        Save();
        OnPersonalCreditsChanged?.Invoke(data.personalCredits);
    }

    /// <summary>
    /// Spende crediti personali (shop in stazione, assicurazione — vedi GDD §7, M3+).
    /// Nessun server coinvolto: sono soldi del giocatore, non della sessione.
    /// </summary>
    public bool TrySpendPersonalCredits(int amount)
    {
        if (amount <= 0) return true;
        if (data.personalCredits < amount) return false;

        data.personalCredits -= amount;
        Save();
        OnPersonalCreditsChanged?.Invoke(data.personalCredits);
        return true;
    }

    /// <summary>Da richiamare quando esisterà la UI di character creation/selection (GDD §10).</summary>
    public void SetCharacterInfo(string characterName, string role)
    {
        data.characterName = characterName;
        data.role = role;
        Save();
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 550, 260, 70));
        GUILayout.BeginVertical("box");
        GUILayout.Label($"[LocalCharacterProfile] {CharacterName} ({Role})");
        GUILayout.Label($"Personal Credits: {PersonalCredits} cr");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("+100 (debug)")) AddPersonalCredits(100);
        if (GUILayout.Button("Reset save")) { File.Delete(SavePath); data = CreateNew(); Save(); OnPersonalCreditsChanged?.Invoke(data.personalCredits); }
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}
