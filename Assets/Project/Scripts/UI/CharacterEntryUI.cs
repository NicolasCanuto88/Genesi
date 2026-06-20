using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// CharacterEntryUI — Milestone 3, Blocco 1 (aggiornato).
///
/// Singola riga nella lista di selezione personaggio (CharacterSelectPanel).
/// Istanziata dinamicamente da MainMenuManager per ogni CharacterData in
/// LocalCharacterProfile.GetAllCharacters().
///
/// AGGIORNAMENTO: il prefab reale (CharacterEntry.prefab) ha due elementi
/// che la versione precedente di questo script non pilotava — leftStripe
/// (Image, rimaneva sempre bianca) e creditsLabel (TMP, rimaneva fermo al
/// testo placeholder "999999999" inserito in Editor). Aggiunti entrambi i
/// campi, popolati in Bind(). Aggiunto anche checkmarkObject — stesso bug:
/// era presente nel prefab (testo "V") ma mai nascosto/mostrato in base
/// alla selezione.
///
/// Il colore dello stripe usa RoleColors.Get() — stessa fonte usata dal
/// badge personaggio in MainMenuManager, per coerenza visiva tra le due UI.
///
/// Pattern Bind(): un metodo che riceve i dati e una callback, senza
/// dipendere direttamente dal sistema che la ospita — stessa convenzione
/// di CrewCreditEntry e CrewHPEntry.
/// </summary>
public class CharacterEntryUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI nameLabel;
    [SerializeField] private TextMeshProUGUI roleLabel;
    [SerializeField] private Image background;

    [Header("Nuovi campi — Border/Background/...")]
    [Tooltip("Border/Background/LeftStripe — colorata dinamicamente in base al ruolo")]
    [SerializeField] private Image leftStripe;
    [Tooltip("Border/Background/Credits — testo crediti personali del personaggio")]
    [SerializeField] private TextMeshProUGUI creditsLabel;
    [Tooltip("Border/Background/Checkmark — visibile solo quando la riga è selezionata")]
    [SerializeField] private GameObject checkmarkObject;

    [Header("Colori sfondo riga (selezione)")]
    [SerializeField] private Color colorNormale = new Color(0.12f, 0.14f, 0.18f, 1f);
    [SerializeField] private Color colorSelezionato = new Color(0.18f, 0.72f, 0.36f, 1f);

    private string _characterId;
    private Action<string> _onSelected;

    public string CharacterId => _characterId;

    /// <summary>
    /// Popola la riga con i dati del personaggio e registra la callback di selezione.
    /// Chiamato da MainMenuManager ogni volta che la lista viene ricreata.
    /// </summary>
    public void Bind(LocalCharacterProfile.CharacterData data, bool selected, Action<string> onSelected)
    {
        _characterId = data.characterId;
        _onSelected = onSelected;

        if (nameLabel != null) nameLabel.text = data.characterName;
        if (roleLabel != null) roleLabel.text = data.role;

        if (leftStripe != null)
            leftStripe.color = RoleColors.Get(data.role);

        if (creditsLabel != null)
            creditsLabel.text = $"{data.personalCredits} cr";

        SetSelected(selected);

        var btn = GetComponent<Button>();
        if (btn != null)
        {
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(OnClicked);
        }
    }

    public void SetSelected(bool selected)
    {
        if (background != null)
            background.color = selected ? colorSelezionato : colorNormale;

        if (checkmarkObject != null)
            checkmarkObject.SetActive(selected);
    }

    private void OnClicked() => _onSelected?.Invoke(_characterId);
}