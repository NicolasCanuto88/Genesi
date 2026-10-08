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
/// TASTI DEL KIT (Rev BU-d · Q91-c): una riga per tasto, con il tasto del dispositivo in uso
/// accanto ai pezzi che usa. I tasti vengono da InputDeviceManager.FormatPrompt (segnaposto
/// {medkit} {antidote} {drug} {cycledrug} {grenade} {drone}); al cambio di dispositivo
/// (OnDeviceChanged) la riga si riscrive anche a tablet aperto. Il riquadro InventoryStubLabel
/// nel Player prefab è alto 180 px per contenere le righe (guida Editor di Rev BU-d).
///
/// GADGET DEL QUARTERMASTER (Rev BV-b · Q91-c): per il Quartermaster la riga del ruolo aggiunge il
/// tasto e i tempi dello Scudo Personale al tier corrente ("Quartermaster — [F] Personal Shield ·
/// 7 s, recharge 40 s"). Si riscrive al cambio di dispositivo.
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
        if (InputDeviceManager.Instance != null)
            InputDeviceManager.Instance.OnDeviceChanged += OnDeviceChanged;   // Rev BU-d
    }

    public void Close()
    {
        LocalCharacterProfile.OnPersonalCreditsChanged -= OnCreditsChanged;
        PlayerHealthSystem.OnLocalHealthChanged -= OnHealthChanged;
        PlayerMedKit.OnLocalKitChanged -= RefreshMedKit;
        if (InputDeviceManager.Instance != null)
            InputDeviceManager.Instance.OnDeviceChanged -= OnDeviceChanged;   // Rev BU-d
    }

    private void RefreshStaticInfo()
    {
        var profile = LocalCharacterProfile.Instance;

        if (nameLabel != null)
            nameLabel.text = profile != null ? profile.CharacterName : "—";

        RefreshRole();

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

    /// <summary>
    /// Rev BQ — contenuto del kit medico personale (conteggio / capienza per tipo).
    /// Rev BR-b — seconda riga con le droghe e la droga selezionata (CycleDrug).
    /// Rev BS-b — la bomba curativa nella prima riga (Q65-a).
    /// Rev BU-b — il Nanomedic Drone in coda alla prima riga. Rev BU-c — l'Antidote Injector accanto
    /// all'antidoto.
    /// Rev BU-d (Q91-c) — una riga per tasto, con il tasto del dispositivo in uso. Esempio a tastiera:
    ///   Medical kit
    ///   [H] Medkit 1/2 · Advanced 0/1
    ///   [J] Antidote 1/2 · Injector 0/1
    ///   [G] Grenade 1/1 · [V] Drone 0/1 (tap: you, hold: crewmate)
    ///   Drugs — Adrenaline 1/2 · Hazmat 0/1 · Combat Stim 0/1
    ///   [K] use: Adrenaline · [L] next drug
    /// </summary>
    private void RefreshMedKit()
    {
        if (inventoryStubLabel == null) return;

        PlayerMedKit kit = PlayerMedKit.LocalInstance;
        if (kit == null)
        {
            inventoryStubLabel.text = "—"; // kit locale non ancora spawnato
            return;
        }

        string selected = kit.HasSelectedDrug ? PlayerMedKit.DrugLabel(kit.SelectedDrug) : "—";

        // Segnaposto dei tasti tra graffe: li risolve FormatPrompt (stringhe normali, non interpolate).
        string template =
            "Medical kit" +
            "\n[{medkit}] Medkit " + Slot(kit, ItemType.MedkitBase) +
            " · Advanced " + Slot(kit, ItemType.MedkitAdvanced) +
            "\n[{antidote}] Antidote " + Slot(kit, ItemType.Antidote) +
            " · Injector " + Slot(kit, ItemType.AntidoteInjector) +
            "\n[{grenade}] Grenade " + Slot(kit, ItemType.HealingGrenade) +
            " · [{drone}] Drone " + Slot(kit, ItemType.NanomedicDrone) + " (tap: you, hold: crewmate)" +
            "\nDrugs — Adrenaline " + Slot(kit, ItemType.Adrenaline) +
            " · Hazmat " + Slot(kit, ItemType.HazmatInjection) +
            " · Combat Stim " + Slot(kit, ItemType.CombatStim) +
            "\n[{drug}] use: " + selected + " · [{cycledrug}] next drug";

        inventoryStubLabel.text = InputDeviceManager.Instance != null
            ? InputDeviceManager.Instance.FormatPrompt(template)
            : template;
    }

    /// <summary>Rev BU-d — "conteggio/capienza" di un pezzo del kit.</summary>
    private static string Slot(PlayerMedKit kit, ItemType type) => $"{kit.GetCount(type)}/{kit.GetCap(type)}";

    /// <summary>
    /// Rev BV-b — riga del ruolo. Per il Quartermaster (ruolo replicato) aggiunge tasto e tempi dello
    /// Scudo Personale al tier corrente; per gli altri ruoli resta il nome del ruolo.
    /// </summary>
    private void RefreshRole()
    {
        if (roleLabel == null) return;

        LocalCharacterProfile profile = LocalCharacterProfile.Instance;
        string role = profile != null ? profile.Role : "—";

        PlayerQuartermasterGadgets gadgets = PlayerQuartermasterGadgets.LocalInstance;
        if (gadgets != null && gadgets.IsQuartermaster &&
            gadgets.TryGetPersonalShieldStats(out float duration, out float cooldown))
        {
            string template = role + " — [{shield}] Personal Shield · " + Mathf.RoundToInt(duration) + " s, recharge " +
                              Mathf.RoundToInt(cooldown) + " s";
            roleLabel.text = InputDeviceManager.Instance != null
                ? InputDeviceManager.Instance.FormatPrompt(template)
                : template;
            return;
        }

        roleLabel.text = role;
    }

    /// <summary>
    /// Rev BU-d — cambio tastiera/gamepad a tablet aperto: si riscrivono i tasti (Rev BV-b: anche quelli
    /// della riga del ruolo).
    /// </summary>
    private void OnDeviceChanged(InputDeviceManager.ActiveDevice device)
    {
        RefreshMedKit();
        RefreshRole();
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