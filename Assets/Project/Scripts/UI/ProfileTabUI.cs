using SpaceSurvivor.Ship;
using TMPro;
using UnityEngine;

/// <summary>
/// ProfileTabUI — Milestone 2. Tab "Profilo" del Tablet.
///
/// DATI REALI: nome/ruolo locale, crediti personali (LocalCharacterProfile),
/// HP (PlayerHealthSystem — NUOVO, agganciato in questa sessione: prima era stub).
///
/// HP: legge PlayerHealthSystem.LocalInstance — l'istanza del client locale
/// (individuata via IsOwner, vedi PlayerHealthSystem.cs). Aggiornamento sia
/// "a freddo" all'apertura (RefreshStaticInfo) sia in tempo reale tramite
/// l'evento statico OnLocalHealthChanged — stesso identico pattern già usato
/// per i crediti personali (LocalCharacterProfile.OnPersonalCreditsChanged).
///
/// KIT MEDICO (Rev BQ · Q30-a): la riga che era lo stub dell'inventario personale
/// mostra il contenuto del kit medico personale (PlayerMedKit.LocalInstance), aggiornato
/// all'apertura e in tempo reale via l'evento statico OnLocalKitChanged. Il kit NON è
/// l'inventario personale dei collezionabili (zaino + stiva, non ancora progettato):
/// quando esisterà, questa riga ne diventerà una parte. Testo di gioco in inglese.
///
/// DATI ANCORA STUB (da agganciare quando i sistemi corrispondenti esisteranno):
///   - Skill tree → dipende da: Progressione Personaggio, GDD §10 (da completare)
///
/// ⚠️ Se PlayerHealthSystem.LocalInstance è null al momento di Open() (Player
/// locale non ancora spawnato in rete, o componente non ancora aggiunto al
/// Player prefab in Editor — vedi nota in PlayerHealthSystem.cs), il campo HP
/// mostra "—": nessun retry automatico in M2, perché in pratica il Tablet si
/// apre sempre DOPO che il proprio Player esiste in scena (non si può aprire
/// il proprio tablet prima di esistere).
/// </summary>
public class ProfileTabUI : MonoBehaviour, IDashboardPanel
{
    [Header("Dati personaggio (reali)")]
    [SerializeField] private TextMeshProUGUI nameLabel;
    [SerializeField] private TextMeshProUGUI roleLabel;
    [SerializeField] private TextMeshProUGUI personalCreditsLabel;

    [Header("HP — reale da questa sessione (PlayerHealthSystem)")]
    [Tooltip("Stesso campo già presente nell'Inspector (ex 'hpStubLabel') — non rinominato " +
             "per non perdere il riferimento UI già assegnato sul prefab del Tablet.")]
    [SerializeField] private TextMeshProUGUI hpStubLabel;

    [Header("Kit medico personale (Rev BQ)")]
    [Tooltip("Stesso campo già presente nell'Inspector (ex stub dell'inventario personale) — non " +
             "rinominato per non perdere il riferimento UI già assegnato sul prefab del Tablet.")]
    [SerializeField] private TextMeshProUGUI inventoryStubLabel;

    [Header("Placeholder — finché i sistemi corrispondenti non esistono")]
    [SerializeField] private TextMeshProUGUI skillStubLabel;

    [Header("Status Colors (HP) — default già sensati, nessuna modifica Inspector richiesta")]
    [SerializeField] private Color colorHealthy = new Color(0.2f, 1f, 0.4f);
    [SerializeField] private Color colorWarning = new Color(1f, 0.67f, 0f);
    [SerializeField] private Color colorCritical = new Color(1f, 0.2f, 0f);

    public void Open()
    {
        RefreshStaticInfo();
        LocalCharacterProfile.OnPersonalCreditsChanged += OnCreditsChanged;
        PlayerHealthSystem.OnLocalHealthChanged += OnHealthChanged;
        PlayerMedKit.OnLocalKitChanged += RefreshMedKit;
    }

    public void Close()
    {
        LocalCharacterProfile.OnPersonalCreditsChanged -= OnCreditsChanged;
        PlayerHealthSystem.OnLocalHealthChanged -= OnHealthChanged;
        PlayerMedKit.OnLocalKitChanged -= RefreshMedKit;
    }

    private void RefreshStaticInfo()
    {
        var profile = LocalCharacterProfile.Instance;

        if (nameLabel != null)
            nameLabel.text = profile != null ? profile.CharacterName : "—";

        if (roleLabel != null)
            roleLabel.text = profile != null ? profile.Role : "—";

        OnCreditsChanged(profile != null ? profile.PersonalCredits : 0);

        var health = PlayerHealthSystem.LocalInstance;
        if (health != null)
            OnHealthChanged(health.CurrentHP, health.MaxHP);
        else if (hpStubLabel != null)
            hpStubLabel.text = "—"; // PlayerHealthSystem locale non ancora spawnato

        RefreshMedKit();

        if (skillStubLabel != null)
            skillStubLabel.text = "Skill tree — da definire (GDD §10)";
    }

    /// <summary>Rev BQ — contenuto del kit medico personale (conteggio / capienza per tipo).</summary>
    private void RefreshMedKit()
    {
        if (inventoryStubLabel == null) return;

        PlayerMedKit kit = PlayerMedKit.LocalInstance;
        if (kit == null)
        {
            inventoryStubLabel.text = "—"; // kit locale non ancora spawnato
            return;
        }

        inventoryStubLabel.text =
            $"Medical kit — Medkit {kit.GetCount(ItemType.MedkitBase)}/{kit.GetCap(ItemType.MedkitBase)}" +
            $" · Advanced {kit.GetCount(ItemType.MedkitAdvanced)}/{kit.GetCap(ItemType.MedkitAdvanced)}" +
            $" · Antidote {kit.GetCount(ItemType.Antidote)}/{kit.GetCap(ItemType.Antidote)}";
    }

    private void OnCreditsChanged(int newAmount)
    {
        if (personalCreditsLabel != null)
            personalCreditsLabel.text = $"{newAmount} cr";
    }

    private void OnHealthChanged(float current, float max)
    {
        if (hpStubLabel == null) return;

        hpStubLabel.text = $"{current:F0} / {max:F0} HP";

        float percent = max > 0f ? current / max : 0f;
        hpStubLabel.color = percent < 0.20f ? colorCritical
                           : percent < 0.40f ? colorWarning
                           : colorHealthy;
    }
}