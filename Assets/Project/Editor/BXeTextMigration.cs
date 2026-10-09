using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// BXeTextMigration — Rev BX-e (Q129-a). Strumento Editor una tantum: porta in inglese i testi fissi
/// di scene, prefab e asset (gli script sono tradotti a parte). Tabella da
/// claude/INVENTARIO_TESTI_BX-e.md, percorsi rigenerati sulla hierarchy dopo BX-d.
///
/// MENU: Tools ▸ Genesi ▸ BX-e Testi in inglese
///   1 · Verifica — apre scene e prefab, confronta e scrive il report. Non modifica nulla.
///   2 · Applica  — come Verifica, poi scrive i testi nuovi e salva scene, prefab e asset.
///
/// SICUREZZA: ogni voce scrive solo se il valore attuale è esattamente il testo italiano atteso.
///   - già in inglese → "già fatto", nessuna modifica (lo strumento si può rilanciare);
///   - valore diverso (cambiato a mano) → saltato e segnalato con il valore trovato;
///   - oggetto non trovato o percorso ambiguo (più fratelli con lo stesso nome) → saltato e segnalato.
/// Un segmento "Nome#n" indica l'n-esimo fratello con quel nome (da 0); nessuna voce lo usa oggi.
///
/// Le scene aperte vengono salvate (se l'utente lo conferma) prima di iniziare e riaperte alla fine.
/// Log in italiano come il resto dei log del progetto. Dopo il commit BX-e lo strumento può restare
/// (è idempotente) o essere eliminato.
/// </summary>
public static class BXeTextMigration
{
    private const string MenuRoot = "Tools/Genesi/BX-e Testi in inglese/";
    private const string MainMenuScene = "Assets/Project/Scenes/MainMenu.unity";
    private const string GameScene = "Assets/Project/Scenes/Game.unity";
    private const string TmpProperty = "m_text";

    // Prefissi dei percorsi.
    private const string MM = "MainMenuCanvas/";
    private const string ESS = "Nave/MeshHolder/Level B/Cabina di pilotaggio/Interior/ESSENTIAL/";
    private const string ENG = ESS + "ENGINEERING/Desktop_01 (1)/Monitor1/";
    private const string SSD = ENG + "ShipSystemsDashboardUI/MainContainer/Content/";
    private const string SCN = ESS + "SCANNER/Desktop_01 (1)/Monitor1/ScannerMonitor_Canvas/";
    private const string PIL = ESS + "PILOT/PilotStation/Monitor/DockingMinigame_Canvas/MainContainer/Mask/";
    private const string MED = "Nave/MeshHolder/Level C/Medibay/Interior/Medibay/MedicalStation/Monitor/";

    /// <summary>Una modifica: oggetto (percorso), componente, proprietà serializzata, testo atteso, testo nuovo.</summary>
    private sealed class Change
    {
        public readonly string Path;       // percorso dell'oggetto; vuoto per un asset
        public readonly string Component;  // null = testo TextMeshPro; altrimenti nome del componente
        public readonly string Property;   // nome della proprietà serializzata
        public readonly string Old;
        public readonly string New;

        public Change(string path, string component, string property, string oldValue, string newValue)
        {
            Path = path; Component = component; Property = property; Old = oldValue; New = newValue;
        }

        public string Label => string.IsNullOrEmpty(Path) ? Property
            : Component == null ? Path : Path + " [" + Component + "." + Property + "]";
    }

    private static Change T(string path, string oldText, string newText)
        => new Change(path, null, TmpProperty, oldText, newText);

    private static Change F(string path, string component, string property, string oldValue, string newValue)
        => new Change(path, component, property, oldValue, newValue);

    private static Change A(string property, string oldValue, string newValue)
        => new Change(string.Empty, null, property, oldValue, newValue);

    // ── TABELLE ───────────────────────────────────────────────────────────────

    private static readonly Change[] MainMenuChanges =
    {
        T(MM + "MainMenuPanel/ContentContainer/NewGame/Text (TMP)",
          "Nuova partita", "New game"),
        T(MM + "MainMenuPanel/ContentContainer/LoadGame/Text (TMP)",
          "Carica", "Load game"),
        T(MM + "MainMenuPanel/ContentContainer/Join/Text (TMP)",
          "Unisciti a partita", "Join game"),
        T(MM + "MainMenuPanel/ContentContainer/Options/Text (TMP)",
          "Impostazioni", "Settings"),
        T(MM + "MainMenuPanel/ContentContainer/Credits/Text (TMP)",
          "Crediti", "Credits"),
        T(MM + "MainMenuPanel/ContentContainer/CharacterBadge/ChangeCharacter/Text (TMP)",
          "Cambia personaggio", "Change character"),
        T(MM + "CharacterCreationPanel/ContentContainer/HeaderSection/Title",
          "REGISTRAZIONE", "ENLISTMENT"),
        T(MM + "CharacterCreationPanel/ContentContainer/HeaderSection/Subtitle",
          "Nuovo membro equipaggio", "New crew member"),
        T(MM + "CharacterCreationPanel/ContentContainer/NameSection/NameLabel",
          "IDENTIFICATIVO", "CALLSIGN"),
        T(MM + "CharacterCreationPanel/ContentContainer/NameSection/CreationNameInput/Text Area/Placeholder",
          "Il tuo nome", "Your name"),
        T(MM + "CharacterCreationPanel/ContentContainer/ErrorText",
          "Scegli il ruolo\n", "Choose a role"),   // a capo finale in scena (refuso): tolto
        T(MM + "CharacterCreationPanel/ContentContainer/RoleContainer/Scanner/Container/Sottotitolo",
          "Sensori - Analisi", "SENSORS - ANALYSIS"),
        T(MM + "CharacterCreationPanel/ContentContainer/RoleContainer/Quartermaster/Container/Sottotitolo",
          "ARMERIA - GADGET", "ARMORY - GADGETS"),
        T(MM + "CharacterCreationPanel/ContentContainer/Apply/Text (TMP)",
          "Conferma", "Confirm"),
        T(MM + "CharacterSelectPanel/ContentContainer/CharacterContainer/NewCharacter/Text (TMP)",
          "+ Nuovo personaggio", "+ New character"),
        T(MM + "CharacterSelectPanel/ContentContainer/ButtonContainer/Apply/Text (TMP)",
          "Conferma", "Confirm"),
        T(MM + "CharacterSelectPanel/ContentContainer/ButtonContainer/Back/Text (TMP)",
          "INDIETRO", "BACK"),
        T(MM + "SessionTypePanel/ContentContainer/HeaderContainer/GameType",
          "Modalità sessione", "Session mode"),
        T(MM + "SessionTypePanel/ContentContainer/HeaderContainer/Descrizione",
          "Come vuoi che gli alti si uniscano?", "How should others join?"),
        T(MM + "SessionTypePanel/ContentContainer/CardMainContainer/CardContainer/Background/Open/Text (TMP)",
          "APERTA", "OPEN"),
        T(MM + "SessionTypePanel/ContentContainer/CardMainContainer/CardContainer/Background/Open/Text (TMP) (1)",
          "Chiunque può unirsi alla partita", "Anyone can join"),
        T(MM + "SessionTypePanel/ContentContainer/CardMainContainer/CardContainer (1)/Background/Open (1)/Text (TMP)",
          "Su Invito", "INVITE ONLY"),
        T(MM + "SessionTypePanel/ContentContainer/CardMainContainer/CardContainer (1)/Background/Open (1)/Text (TMP) (1)",
          "Solo con codice", "Code required"),
        T(MM + "SessionTypePanel/ContentContainer/Back/Text (TMP)",
          "Indietro\n", "Back"),   // a capo finale in scena (refuso): tolto
        T(MM + "LobbyHostPanel/ContentContainer/Description",
          "Condividi il codice operativo", "Share the session code"),
        T(MM + "LobbyHostPanel/ContentContainer/BadgeSession/Text",
          "Su invito", "Invite only"),
        T(MM + "LobbyHostPanel/ContentContainer/CardContainer/Background/CopyCode/Text (TMP)",
          "Copia", "Copy"),
        T(MM + "LobbyHostPanel/ContentContainer/WaitTeam",
          "In Attesa Equipaggio", "Waiting for crew"),
        T(MM + "LobbyHostPanel/ContentContainer/MemberCount",
          "Equipaggio a bordo: 1/5", "Crew aboard: 1/5"),
        T(MM + "LobbyHostPanel/ContentContainer/StartGame/Text (TMP)",
          "INIZIA LA PARTITA", "START GAME"),
        T(MM + "LobbyHostPanel/ContentContainer/Back/Text (TMP)",
          "Annulla", "Cancel"),
        T(MM + "JoinPanel/ContentContainer/WaitTeam",
          "Unisciti", "Join"),
        T(MM + "JoinPanel/ContentContainer/Description",
          "Inserisci il codice operativo", "Enter the session code"),
        T(MM + "JoinPanel/ContentContainer/InputField (TMP)/Text Area/Placeholder",
          "Codice operativo", "Session code"),
        T(MM + "JoinPanel/ContentContainer/Status",
          "In attesa di connessione...", "Waiting for connection..."),
        T(MM + "JoinPanel/ContentContainer/ButtonContainer/Join/Text (TMP)",
          "connetti", "Connect"),
        T(MM + "JoinPanel/ContentContainer/ButtonContainer/Back/Text (TMP)",
          "Indietro", "Back")
    };

    private static readonly Change[] GameChanges =
    {
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Diagnostics/Section_A_SubsystemStatus/Label_SectionA_Title",
          "STATO SOTTOSISTEMI", "SUBSYSTEM STATUS"),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Diagnostics/Section_A_SubsystemStatus/LifeSupport_Row/Text_O2Auton",
          "Autonomia: ∞", "Time left: ∞"),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Repair/Section_B_Repair/Label_SectionTitle",
          "RIPARAZIONI", "REPAIRS"),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Repair/Section_B_Repair/RepairStatusMessage",
          "RECATI AL PANNELLO...", "GO TO PANEL..."),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Repair/Section_B_Repair/RepairRow_Propulsion/Row_PropHeader/Label_PropSystemName",
          "\"PROPULSIONE", "PROPULSION"),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Repair/Section_B_Repair/RepairRow_Propulsion/Row_PropHeader/Badge_PropState",
          "DEGRADATO", "DEGRADED"),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Repair/Section_B_Repair/RepairRow_Propulsion/Button_PropAvvia/Text (TMP)",
          "MATERIALI INSUFFICIENTI", "NOT ENOUGH MATERIALS"),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Repair/Section_B_Repair/RepairRow_Propulsion/Label_Materials",
          "Soglia 50% — Richiede: 2× Componente Elettronico (1)   1× Tanica Refrigerante (2)", "Threshold 50% — Requires: 2× Electronic Component (1)   1× Coolant Canister (2)"),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Repair/Section_B_Repair/RepairRow_FTL/Row_PropHeader/Badge_PropState",
          "DEGRADATO", "DEGRADED"),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Repair/Section_B_Repair/RepairRow_FTL/Button_PropAvvia/Text (TMP)",
          "MATERIALI INSUFFICIENTI", "NOT ENOUGH MATERIALS"),
        T(ENG + "ShipSystemsDashboardUI/MainContainer/Content/Column_Repair/Section_B_Repair/RepairRow_FTL/Label_Materials",
          "Soglia 50% — Richiede: 2× Componente Elettronico (1)   1× Tanica Refrigerante (2)", "Threshold 50% — Requires: 2× Electronic Component (1)   1× Coolant Canister (2)"),
        T(ENG + "PowerManagementDashboardUI/BackgroundPanel/MainContainer/Content/LeftColumn/PowerStatusPanel/BannerEngineFailure/Panel/Label",
          "MOTORI OFFLINE — Ripristino: 0%", "ENGINES OFFLINE — RESTORING: 0%"),
        T(SCN + "BackgroundPanel/ListContent/EmptyState",
          "NESSUN CONTATTO", "NO CONTACTS"),
        T(SCN + "BackgroundPanel/ListContent/Content/LockAndDetail/Detail",
          "NESSUN CONTATTO", "NO CONTACTS"),
        T(SCN + "LockMinigame/StatusText",
          "PREMI E PER RIPARARE", "PRESS E TO REPAIR"),
        T(PIL + "Speed",
          "VELOCITÀ: 0.0 u/s", "SPEED: 0.0 u/s"),
        T(PIL + "Distance",
          "DISTANZA: 0.0 m", "DISTANCE: 0.0 m"),
        T(PIL + "ConfirmPrompt/ConfirmText",
          "Premi Space per ATTRACCARE", "PRESS SPACE / A TO DOCK"),
        T(MED + "MedBayCanvas/Panel_Background/Content/O2/O2Row/o2Stats/o2AutonText",
          "Autonomia: ∞", "Time left: ∞"),
        T(MED + "MedBayCanvas/Panel_Background/Content/O2/O2Row/Row_Header/o2StatusBadge",
          "NORMALE", "NORMAL"),
        T(MED + "MedicalMinigameCanvas/Panel_Background/StatusText",
          "PREMI E PER RIPARARE", "PRESS E TO REPAIR"),
        T(ESS + "PropulsionSystemPanel/P_Ship_Interior_Module_01/Ship_Interior_Module_01/RepairMinigameCanvas/Panel_Background/StatusText",
          "PREMI E PER RIPARARE", "PRESS E TO REPAIR"),
        T(ESS + "FTLDrivePanel/P_Ship_Interior_Module_01/Ship_Interior_Module_01/RepairMinigameCanvas/Panel_Background/StatusText",
          "PREMI E PER RIPARARE", "PRESS E TO REPAIR"),
        T("Nave/MeshHolder/Level C/Server_01/RepairMinigameCanvas (1)/Panel_Background/StatusText",
          "PREMI E PER RIPARARE", "PRESS E TO REPAIR"),

        // Campi serializzati (non TextMeshPro).
        F(ENG + "WreckDashboardUI/PowerReadout (1)", "PowerReadout", "prefix", "W DISPONIBILE: ", "W AVAILABLE: "),
        F(ENG + "ShipSystemsDashboardUI/PowerReadout", "PowerReadout", "prefix", "W DISPONIBILE: ", "W AVAILABLE: "),
        F(ENG + "PowerManagementDashboardUI/BackgroundPanel/PowerReadout", "PowerReadout", "prefix", "W DISPONIBILE: ", "W AVAILABLE: "),
        F(ENG + "InventorySystemDashboardUI/PowerReadout (1)", "PowerReadout", "prefix", "W DISPONIBILE: ", "W AVAILABLE: "),
        F(SSD + "Column_Repair/Section_B_Repair/RepairRow_FTL", "RepairEntry", "textSystemOperational",
          "Sistema operativo — nessuna riparazione necessaria", "System operational — no repair needed"),
        F(SSD + "Column_Repair/Section_B_Repair/RepairRow_FTL", "RepairEntry", "textTotalRequiredPrefix",
          "Riparazione completa richiede: ", "Full repair requires: "),
        F(SSD + "Column_Repair/Section_B_Repair/RepairRow_Propulsion", "RepairEntry", "textSystemOperational",
          "Sistema operativo — nessuna riparazione necessaria", "System operational — no repair needed"),
        F(SSD + "Column_Repair/Section_B_Repair/RepairRow_Propulsion", "RepairEntry", "textTotalRequiredPrefix",
          "Riparazione completa richiede: ", "Full repair requires: "),
    };

    // Prefab: percorsi relativi alla radice del prefab.
    private const string PlayerPrefab = "Assets/Project/Prefabs/Player/Player.prefab";
    private static readonly Change[] PlayerPrefabChanges =
    {
        T("Camera/TabletHoldPoint/Holo_Base/TabletCanvas/PanelBackground/TabSwitcher/Tab_Nave/ListMember",
          "Lista Equipaggio", "Crew list"),
    };

    private const string CrewCreditEntryPrefab = "Assets/Project/Prefabs/UI/CrewCreditEntry.prefab";
    private static readonly Change[] CrewCreditEntryChanges =
    {
        T("SendCreditsButton/Text (TMP)", "Invia", "Send"),
    };

    // Asset ScriptableObject.
    private const string WreckPoiAsset = "Assets/Project/Scripts/POI/PoiData_Wreck.asset";
    private static readonly Change[] WreckPoiChanges =
    {
        A("displayName", "Relitto abbandonato", "Abandoned wreck"),
        A("composition", "Lega di titanio, scafo militare", "Titanium alloy, military hull"),
        A("stubBlueprintInfo", "Blueprint di Prova", "Test blueprint"),
        A("stubShipSystemsInfo", "Stub Ship di Prova", "Test systems"),
        A("stubLayoutInfo", "Layout di Prova", "Test layout"),
    };

    // ── MENU ──────────────────────────────────────────────────────────────────

    [MenuItem(MenuRoot + "1 · Verifica (non modifica nulla)")]
    private static void Verify() => Run(false);

    [MenuItem(MenuRoot + "2 · Applica")]
    private static void Apply() => Run(true);

    private enum Outcome { Applied, WouldApply, AlreadyDone, Different, Missing }

    private sealed class Report
    {
        public readonly bool Write;
        public int Applied, AlreadyDone, Problems;
        public readonly StringBuilder Problem = new StringBuilder();
        public readonly StringBuilder Detail = new StringBuilder();

        public Report(bool write) { Write = write; }

        public void Add(string container, Change change, Outcome outcome, string found = null)
        {
            switch (outcome)
            {
                case Outcome.Applied:
                case Outcome.WouldApply:
                    Applied++;
                    Detail.AppendLine($"  {(Write ? "scritto" : "da scrivere")} · {container} · {change.Label} → \"{change.New}\"");
                    break;
                case Outcome.AlreadyDone:
                    AlreadyDone++;
                    break;
                case Outcome.Different:
                    Problems++;
                    Problem.AppendLine($"  DIVERSO · {container} · {change.Label}: trovato \"{Visible(found)}\", atteso \"{Visible(change.Old)}\" (saltato)");
                    break;
                case Outcome.Missing:
                    Problems++;
                    Problem.AppendLine($"  NON TROVATO · {container} · {change.Label}: {found} (saltato)");
                    break;
            }
        }
    }

    /// <summary>Rende visibili a capo e tabulazioni nel report (un a capo finale non si vede in console).</summary>
    private static string Visible(string value)
        => value == null ? "(null)" : value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static void Run(bool write)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[BX-e] Esci dal Play mode prima di usare lo strumento.");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
        var report = new Report(write);

        try
        {
            ProcessScene(MainMenuScene, MainMenuChanges, report);
            ProcessScene(GameScene, GameChanges, report);
            ProcessPrefab(PlayerPrefab, PlayerPrefabChanges, report);
            ProcessPrefab(CrewCreditEntryPrefab, CrewCreditEntryChanges, report);
            ProcessAsset(WreckPoiAsset, WreckPoiChanges, report);
            if (write) AssetDatabase.SaveAssets();
        }
        finally
        {
            RestoreScenes(setup);
        }

        string head = $"[BX-e] {(write ? "Applica" : "Verifica")}: {report.Applied} {(write ? "scritti" : "da scrivere")}, " +
                      $"{report.AlreadyDone} già in inglese, {report.Problems} da controllare.";
        Debug.Log(head + "\n" + report.Detail);
        if (report.Problems > 0)
            Debug.LogWarning("[BX-e] Voci saltate (nessuna modifica):\n" + report.Problem);
    }

    /// <summary>
    /// Riapre le scene che erano aperte prima. Una scena mai salvata (senza percorso) non si può
    /// riaprire e viene saltata; se manca la scena attiva, diventa attiva la prima rimasta.
    /// </summary>
    private static void RestoreScenes(SceneSetup[] setup)
    {
        if (setup == null) return;
        var valid = new List<SceneSetup>();
        foreach (SceneSetup entry in setup)
            if (!string.IsNullOrEmpty(entry.path)) valid.Add(entry);
        if (valid.Count == 0) return;

        bool anyActive = false;
        foreach (SceneSetup entry in valid) anyActive |= entry.isActive;
        if (!anyActive) valid[0].isActive = true;

        EditorSceneManager.RestoreSceneManagerSetup(valid.ToArray());
    }

    // ── SCENE ─────────────────────────────────────────────────────────────────

    private static void ProcessScene(string scenePath, Change[] changes, Report report)
    {
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        var roots = new List<Transform>();
        foreach (GameObject go in scene.GetRootGameObjects()) roots.Add(go.transform);

        bool changed = false;
        foreach (Change change in changes)
            changed |= ProcessChange(scenePath, roots, change, report);

        if (changed && report.Write)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
    }

    // ── PREFAB ────────────────────────────────────────────────────────────────

    private static void ProcessPrefab(string prefabPath, Change[] changes, Report report)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        if (root == null)
        {
            foreach (Change change in changes) report.Add(prefabPath, change, Outcome.Missing, "prefab non trovato");
            return;
        }

        try
        {
            var children = new List<Transform>();
            foreach (Transform child in root.transform) children.Add(child);

            bool changed = false;
            foreach (Change change in changes)
                changed |= ProcessChange(prefabPath, children, change, report);

            if (changed && report.Write) PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ── ASSET ─────────────────────────────────────────────────────────────────

    private static void ProcessAsset(string assetPath, Change[] changes, Report report)
    {
        Object asset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
        if (asset == null)
        {
            foreach (Change change in changes) report.Add(assetPath, change, Outcome.Missing, "asset non trovato");
            return;
        }

        var so = new SerializedObject(asset);
        bool changed = false;
        foreach (Change change in changes)
            changed |= ProcessProperty(assetPath, so, change, report);

        if (changed && report.Write)
        {
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }
    }

    // ── VOCE SINGOLA ──────────────────────────────────────────────────────────

    /// <summary>Trova oggetto e componente della voce e la elabora. True se ha scritto qualcosa.</summary>
    private static bool ProcessChange(string container, List<Transform> roots, Change change, Report report)
    {
        Transform target = FindByPath(roots, change.Path, out string error);
        if (target == null)
        {
            report.Add(container, change, Outcome.Missing, error);
            return false;
        }

        Component component = change.Component == null
            ? target.GetComponent<TMP_Text>()
            : target.GetComponent(change.Component);
        if (component == null)
        {
            report.Add(container, change, Outcome.Missing,
                $"componente {(change.Component ?? "TextMeshPro")} assente");
            return false;
        }

        var so = new SerializedObject(component);
        bool changed = ProcessProperty(container, so, change, report);
        if (changed && report.Write)
        {
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(component);
            if (PrefabUtility.IsPartOfPrefabInstance(component))
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }
        return changed;
    }

    /// <summary>Confronta e (in Applica) scrive una proprietà stringa. True se va scritta.</summary>
    private static bool ProcessProperty(string container, SerializedObject so, Change change, Report report)
    {
        SerializedProperty property = so.FindProperty(change.Property);
        if (property == null || property.propertyType != SerializedPropertyType.String)
        {
            report.Add(container, change, Outcome.Missing, $"proprietà {change.Property} assente");
            return false;
        }

        string current = property.stringValue;
        if (current == change.New)
        {
            report.Add(container, change, Outcome.AlreadyDone);
            return false;
        }
        if (current != change.Old)
        {
            report.Add(container, change, Outcome.Different, current);
            return false;
        }

        if (report.Write) property.stringValue = change.New;
        report.Add(container, change, report.Write ? Outcome.Applied : Outcome.WouldApply);
        return true;
    }

    // ── PERCORSI ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Segue il percorso "A/B/C" a partire dagli oggetti indicati, inattivi compresi. "Nome#n" sceglie
    /// l'n-esimo fratello con quel nome (da 0). Senza "#n", più fratelli con lo stesso nome sono un
    /// errore: meglio saltare la voce che scrivere sull'oggetto sbagliato.
    /// </summary>
    private static Transform FindByPath(List<Transform> roots, string path, out string error)
    {
        error = null;
        string[] segments = path.Split('/');
        List<Transform> candidates = roots;
        Transform current = null;

        for (int s = 0; s < segments.Length; s++)
        {
            ParseSegment(segments[s], out string name, out int index);

            Transform match = null;
            int seen = 0;
            foreach (Transform candidate in candidates)
            {
                if (candidate.name != name) continue;
                if (index < 0)
                {
                    if (match != null)
                    {
                        error = $"più oggetti \"{name}\" allo stesso livello (percorso ambiguo)";
                        return null;
                    }
                    match = candidate;
                }
                else if (seen++ == index)
                {
                    match = candidate;
                    break;
                }
            }

            if (match == null)
            {
                error = $"\"{segments[s]}\" non trovato" + (current != null ? $" sotto \"{current.name}\"" : " alla radice");
                return null;
            }

            current = match;
            candidates = new List<Transform>();
            foreach (Transform child in current) candidates.Add(child);
        }
        return current;
    }

    private static void ParseSegment(string segment, out string name, out int index)
    {
        name = segment;
        index = -1;
        int hash = segment.LastIndexOf('#');
        if (hash > 0 && int.TryParse(segment.Substring(hash + 1), out int parsed) && parsed >= 0)
        {
            name = segment.Substring(0, hash);
            index = parsed;
        }
    }
}