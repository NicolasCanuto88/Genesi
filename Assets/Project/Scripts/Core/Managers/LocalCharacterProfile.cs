using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// LocalCharacterProfile — Milestone 3, Blocco 1 (rewrite multi-personaggio).
///
/// PRIMA (v0.9.7): un solo personaggio per dispositivo, SaveData piatto con
/// characterName/role/personalCredits. Il commento di classe stesso lo marcava
/// "STUB — un solo slot".
///
/// ORA: lista di personaggi (CharacterData[]) + activeCharacterId. Stessa API
/// pubblica (CharacterName, Role, PersonalCredits, AddPersonalCredits,
/// TrySpendPersonalCredits, SetCharacterInfo) — ora delega al personaggio attivo
/// invece che all'unico esistente. Nessuna modifica richiesta in EconomyManager,
/// ProfileTabUI o nel resto del codice che la usava.
///
/// MIGRAZIONE AUTOMATICA: se sul disco esiste il vecchio file formato v0.9.7
/// (oggetto piatto, nessun array "characters"), viene riconosciuto e convertito
/// in automatico al primo avvio — il personaggio precedente diventa il primo
/// elemento della nuova lista.
///
/// CharacterData è una classe pubblica (non privata) così CharacterEntryUI e
/// MainMenuManager possono referenziarla direttamente senza casting o DTO intermedi.
///
/// ⚠️ Stesso comportamento DontDestroyOnLoad di prima — posizionato in
/// MainMenu.unity (dal Blocco 1 in poi), persiste in Game.unity senza bisogno
/// di essere ricreato nella scena di gioco.
///
/// ⚠️ Dipende da: nessuno — è il livello più basso della catena di profilo.
/// </summary>
public class LocalCharacterProfile : MonoBehaviour
{
    private const string SAVE_FILE_NAME = "character_save.json";

    // ── SINGLETON ─────────────────────────────────────────────────────────────
    public static LocalCharacterProfile Instance { get; private set; }

    // ── EVENTI ────────────────────────────────────────────────────────────────

    /// <summary>Fired quando il saldo del personaggio attivo cambia (guadagno o spesa).</summary>
    public static event Action<int> OnPersonalCreditsChanged;

    /// <summary>Fired quando cambia il personaggio attivo (selezione, creazione, eliminazione).</summary>
    public static event Action OnActiveCharacterChanged;

    // ── TIPI DATI PUBBLICI ────────────────────────────────────────────────────

    [Serializable]
    public class CharacterData
    {
        public string characterId = "";
        public string characterName = "Senza nome";
        public string role = "Non assegnato";
        public int personalCredits = 0;
    }

    // ── SAVE DATA (nuovo formato) ─────────────────────────────────────────────

    [Serializable]
    private class SaveData
    {
        public List<CharacterData> characters = new List<CharacterData>();
        public string activeCharacterId = "";
    }

    // Formato legacy v0.9.7 (struttura piatta, un solo personaggio) — solo per migrazione.
    [Serializable]
    private class LegacySaveData
    {
        public string characterId;
        public string characterName;
        public string role;
        public int personalCredits;
    }

    // ── STATO INTERNO ─────────────────────────────────────────────────────────

    private SaveData _data;
    private CharacterData _activeCharacter;
    private string SavePath => Path.Combine(Application.persistentDataPath, SAVE_FILE_NAME);

    // ── API — LETTURA (STESSA API DI PRIMA, ora delega al personaggio attivo) ─

    public string CharacterId => _activeCharacter?.characterId ?? "";
    public string CharacterName => _activeCharacter?.characterName ?? "Senza nome";
    public string Role => _activeCharacter?.role ?? "Non assegnato";
    public int PersonalCredits => _activeCharacter?.personalCredits ?? 0;

    /// <summary>True se esiste almeno un personaggio creato.</summary>
    public bool HasAnyCharacter => _data?.characters?.Count > 0;

    /// <summary>True se c'è un personaggio attivo selezionato.</summary>
    public bool HasActiveCharacter => _activeCharacter != null;

    /// <summary>Lista read-only di tutti i personaggi creati su questo dispositivo.</summary>
    public IReadOnlyList<CharacterData> GetAllCharacters() =>
        _data?.characters ?? (IReadOnlyList<CharacterData>)Array.Empty<CharacterData>();

    // ── LIFECYCLE ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Load();
    }

    // ── PERSISTENZA ───────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(SavePath))
            {
                _data = new SaveData();
                return;
            }

            string json = File.ReadAllText(SavePath);
            _data = JsonUtility.FromJson<SaveData>(json);

            // ── Migrazione dal formato v0.9.7 (struttura piatta) ──────────────
            // Il formato legacy aveva "characterName" come campo radice, quello
            // nuovo ha un array "characters". Se la lista è vuota o null, proviamo
            // a leggere il file come legacy e convertiamo.
            bool needsMigration = _data == null
                               || _data.characters == null
                               || _data.characters.Count == 0;

            if (needsMigration)
            {
                var legacy = JsonUtility.FromJson<LegacySaveData>(json);

                if (legacy != null && !string.IsNullOrEmpty(legacy.characterName)
                    && legacy.characterName != "Senza nome")
                {
                    Debug.Log("[LocalCharacterProfile] Migrazione dal formato v0.9.7.");
                    _data = new SaveData();
                    var migrated = new CharacterData
                    {
                        characterId = string.IsNullOrEmpty(legacy.characterId)
                                          ? Guid.NewGuid().ToString()
                                          : legacy.characterId,
                        characterName = legacy.characterName,
                        role = legacy.role ?? "Non assegnato",
                        personalCredits = legacy.personalCredits
                    };
                    _data.characters.Add(migrated);
                    _data.activeCharacterId = migrated.characterId;
                    Save();
                }
                else
                {
                    _data = new SaveData();
                }
            }

            // ── Risolve personaggio attivo ────────────────────────────────────
            _activeCharacter = FindById(_data.activeCharacterId);

            // Fallback: se l'id salvato non corrisponde più a nessun personaggio
            // (es. personaggio cancellato tra sessioni), usa il primo della lista.
            if (_activeCharacter == null && _data.characters.Count > 0)
            {
                _activeCharacter = _data.characters[0];
                _data.activeCharacterId = _activeCharacter.characterId;
                Save();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[LocalCharacterProfile] Errore caricamento: {e.Message}. Reset.");
            _data = new SaveData();
            _activeCharacter = null;
        }
    }

    private void Save()
    {
        try { File.WriteAllText(SavePath, JsonUtility.ToJson(_data, true)); }
        catch (Exception e) { Debug.LogError($"[LocalCharacterProfile] Errore salvataggio: {e.Message}"); }
    }

    private CharacterData FindById(string id)
    {
        if (string.IsNullOrEmpty(id) || _data?.characters == null) return null;
        return _data.characters.Find(c => c.characterId == id);
    }

    // ── API — CRUD PERSONAGGI ─────────────────────────────────────────────────

    /// <summary>
    /// Crea un nuovo personaggio. Se makeActive è true (default) diventa subito quello corrente.
    /// Fires OnActiveCharacterChanged se viene attivato.
    /// </summary>
    public CharacterData CreateCharacter(string characterName, string role, bool makeActive = true)
    {
        var character = new CharacterData
        {
            characterId = Guid.NewGuid().ToString(),
            characterName = characterName,
            role = role,
            personalCredits = 0
        };

        _data.characters.Add(character);

        if (makeActive)
        {
            _activeCharacter = character;
            _data.activeCharacterId = character.characterId;
        }

        Save();

        if (makeActive)
        {
            OnActiveCharacterChanged?.Invoke();
            OnPersonalCreditsChanged?.Invoke(PersonalCredits);
        }

        return character;
    }

    /// <summary>
    /// Rende attivo il personaggio con l'id indicato.
    /// Fires OnActiveCharacterChanged e OnPersonalCreditsChanged.
    /// </summary>
    public void SelectCharacter(string characterId)
    {
        var character = FindById(characterId);
        if (character == null)
        {
            Debug.LogWarning($"[LocalCharacterProfile] SelectCharacter: id '{characterId}' non trovato.");
            return;
        }

        _activeCharacter = character;
        _data.activeCharacterId = characterId;
        Save();

        OnActiveCharacterChanged?.Invoke();
        OnPersonalCreditsChanged?.Invoke(PersonalCredits);
    }

    /// <summary>
    /// Elimina il personaggio con l'id indicato.
    /// Se era quello attivo, attiva il primo rimasto (o null se lista vuota).
    /// </summary>
    public void DeleteCharacter(string characterId)
    {
        var character = FindById(characterId);
        if (character == null) return;

        _data.characters.Remove(character);

        if (_data.activeCharacterId == characterId)
        {
            _activeCharacter = _data.characters.Count > 0 ? _data.characters[0] : null;
            _data.activeCharacterId = _activeCharacter?.characterId ?? "";
            OnActiveCharacterChanged?.Invoke();
            OnPersonalCreditsChanged?.Invoke(PersonalCredits);
        }

        Save();
    }

    // ── API — SCRITTURA (STESSA API DI PRIMA) ────────────────────────────────

    /// <summary>
    /// Aggiorna nome e ruolo del personaggio attivo. Usato da CharacterCreationPanel.
    /// Backward-compatible: stessa firma di prima.
    /// </summary>
    public void SetCharacterInfo(string characterName, string role)
    {
        if (_activeCharacter == null) return;
        _activeCharacter.characterName = characterName;
        _activeCharacter.role = role;
        Save();
        OnActiveCharacterChanged?.Invoke();
    }

    /// <summary>
    /// Aggiunge crediti al personaggio attivo e salva.
    /// Chiamato da EconomyManager.ReceiveFleetPaymentRpc. API invariata.
    /// </summary>
    public void AddPersonalCredits(int amount)
    {
        if (amount == 0 || _activeCharacter == null) return;
        _activeCharacter.personalCredits = Mathf.Max(0, _activeCharacter.personalCredits + amount);
        Save();
        OnPersonalCreditsChanged?.Invoke(PersonalCredits);
    }

    /// <summary>
    /// Spende crediti del personaggio attivo (shop, assicurazione — M3+).
    /// API invariata.
    /// </summary>
    public bool TrySpendPersonalCredits(int amount)
    {
        if (amount <= 0) return true;
        if (_activeCharacter == null || _activeCharacter.personalCredits < amount) return false;

        _activeCharacter.personalCredits -= amount;
        Save();
        OnPersonalCreditsChanged?.Invoke(PersonalCredits);
        return true;
    }

    // ── DEBUG GUI ─────────────────────────────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 550, 280, 90));
        GUILayout.BeginVertical("box");
        int count = _data?.characters?.Count ?? 0;
        GUILayout.Label($"[Profile] {CharacterName} ({Role}) | {count} personaggi");
        GUILayout.Label($"Personal Credits: {PersonalCredits} cr");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("+100")) AddPersonalCredits(100);
        if (GUILayout.Button("Reset"))
        {
            try { File.Delete(SavePath); } catch { }
            _data = new SaveData();
            _activeCharacter = null;
            OnActiveCharacterChanged?.Invoke();
            OnPersonalCreditsChanged?.Invoke(0);
        }
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
#endif
}