using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// CharacterEntryUI — Milestone 3, Blocco 1.
///
/// Singola riga nella lista di selezione personaggio (CharacterSelectPanel).
/// Istanziata dinamicamente da MainMenuManager per ogni CharacterData in
/// LocalCharacterProfile.GetAllCharacters().
///
/// Pattern Bind(): stessa convenzione già usata da CrewCreditEntry e CrewHPEntry —
/// un metodo che riceve i dati e una callback, senza dipendere direttamente dal
/// sistema che la ospita.
///
/// ⚠️ EDITOR SETUP: questo script va su un Prefab (CharacterEntry.prefab) in
/// Assets/Project/Prefabs/UI/. Il prefab è un semplice Button con:
///   - Image (background) sul root
///   - TMP nameLabel e roleLabel come figli
///   - Button component sul root
/// MainMenuManager tiene il riferimento al prefab come SerializeField e lo
/// istanzia dentro un ScrollView → Content con VerticalLayoutGroup.
/// </summary>
public class CharacterEntryUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI nameLabel;
    [SerializeField] private TextMeshProUGUI roleLabel;
    [SerializeField] private Image           background;

    [Header("Colori")]
    [SerializeField] private Color colorNormale    = new Color(0.12f, 0.14f, 0.18f, 1f);
    [SerializeField] private Color colorSelezionato = new Color(0.18f, 0.72f, 0.36f, 1f);

    private string        _characterId;
    private Action<string> _onSelected;

    public string CharacterId => _characterId;

    /// <summary>
    /// Popola la riga con i dati del personaggio e registra la callback di selezione.
    /// Chiamato da MainMenuManager ogni volta che la lista viene ricreata.
    /// </summary>
    public void Bind(LocalCharacterProfile.CharacterData data, bool selected, Action<string> onSelected)
    {
        _characterId = data.characterId;
        _onSelected  = onSelected;

        if (nameLabel != null) nameLabel.text = data.characterName;
        if (roleLabel  != null) roleLabel.text  = data.role;

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
    }

    private void OnClicked() => _onSelected?.Invoke(_characterId);
}
