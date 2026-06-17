using TMPro;
using UnityEngine;

/// <summary>
/// ProfileTabUI — Milestone 2. Tab "Profilo" del Tablet.
///
/// DATI REALI: nome/ruolo locale, crediti personali (da LocalCharacterProfile —
/// quindi persistenti e indipendenti dalla sessione corrente).
///
/// DATI STUB (da agganciare quando i sistemi corrispondenti esisteranno):
///   - HP                   → dipende da: PlayerHealthSystem (M3, non implementato)
///   - Inventario personale → dipende da: sistema di inventario personale per-player
///                             (concettualmente diverso da InventorySystem, che è
///                             lo stock CONDIVISO di materiali di riparazione della
///                             nave — Monitor 3. Non ancora progettato.)
///   - Skill tree           → dipende da: Progressione Personaggio, GDD §10 (da completare)
/// </summary>
public class ProfileTabUI : MonoBehaviour, IDashboardPanel
{
    [Header("Dati personaggio (reali)")]
    [SerializeField] private TextMeshProUGUI nameLabel;
    [SerializeField] private TextMeshProUGUI roleLabel;
    [SerializeField] private TextMeshProUGUI personalCreditsLabel;

    [Header("Placeholder — finché i sistemi corrispondenti non esistono")]
    [SerializeField] private TextMeshProUGUI hpStubLabel;
    [SerializeField] private TextMeshProUGUI inventoryStubLabel;
    [SerializeField] private TextMeshProUGUI skillStubLabel;

    public void Open()
    {
        RefreshStaticInfo();
        LocalCharacterProfile.OnPersonalCreditsChanged += OnCreditsChanged;
    }

    public void Close()
    {
        LocalCharacterProfile.OnPersonalCreditsChanged -= OnCreditsChanged;
    }

    private void RefreshStaticInfo()
    {
        var profile = LocalCharacterProfile.Instance;

        if (nameLabel != null)
            nameLabel.text = profile != null ? profile.CharacterName : "—";

        if (roleLabel != null)
            roleLabel.text = profile != null ? profile.Role : "—";

        OnCreditsChanged(profile != null ? profile.PersonalCredits : 0);

        if (hpStubLabel != null)
            hpStubLabel.text = "— (in arrivo: PlayerHealthSystem, M3)";

        if (inventoryStubLabel != null)
            inventoryStubLabel.text = "Inventario personale — sistema non ancora implementato";

        if (skillStubLabel != null)
            skillStubLabel.text = "Skill tree — da definire (GDD §10)";
    }

    private void OnCreditsChanged(int newAmount)
    {
        if (personalCreditsLabel != null)
            personalCreditsLabel.text = $"{newAmount} cr";
    }
}
