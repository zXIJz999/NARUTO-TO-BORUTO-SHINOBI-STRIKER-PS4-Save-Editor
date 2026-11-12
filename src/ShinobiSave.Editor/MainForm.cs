using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using DevExpress.XtraGrid.Views.Base;
using DevExpress.XtraGrid.Views.Grid;
using DevExpress.XtraTab;
using ShinobiSave.Core;

namespace ShinobiSave.Editor;

internal sealed class MainForm : XtraForm
{
    private const string AppTitle = "Shinobi Striker Save Editor";
    private const string GameProcess = "NARUTO";
    private const string Reserved = "Reserved (unused)";

    private static readonly string GameSaveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "NARUTO TO BORUTO SHINOBI STRIKER");

    // Counters shown on the Player page; every counter is also on the Stats page.
    private static readonly string[] CommonCounters =
    {
        "ID_Counter_Money", "ID_Counter_SkillPoint", "ID_Counter_MasterPoint", "ID_Counter_PlayerExperience",
        "ID_Counter_PlayerExperienceAccumulation", "ID_Counter_TotalMoney", "ID_Counter_PlayTimeMinute",
    };

    /// <summary>Item kinds as stored in the item box (shared with the web editor).</summary>
    private static readonly (string type, string label, string prefix, string table)[] ItemKinds = EditOps.ItemKinds;

    /// <summary>"Add all I don't have" choice for DLC weapons and ninja tools only.</summary>
    private const string DlcChoice = "DLC weapons";

    private readonly SaveSchema _schema = SaveSchema.FromSeed();
    private readonly IdCatalog _catalog = IdCatalog.FromSeed();
    private readonly GameNames _names = GameNames.Load();
    private readonly Dictionary<string, List<NameEntry>> _entryCache = new(StringComparer.Ordinal);

    private readonly XtraTabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly LabelControl _status = new();
    private readonly MemoEdit _log = new();
    private readonly SimpleButton _saveBtn, _saveAsBtn, _reloadBtn;

    private ShinobiSaveFile? _save;
    private bool _dirty;
    private readonly List<MapTable> _maps = new();
    private readonly List<Action> _refreshers = new();

    public MainForm(string? startPath)
    {
        Text = $"{AppTitle} - {Ui.Credit}";
        Size = new Size(1400, 900);
        MinimumSize = new Size(1200, 720);
        StartPosition = FormStartPosition.CenterScreen;

        // Header: name of the tool, who made it, and the only buttons most people need.
        var header = Ui.Panel(DockStyle.Top, 84);
        header.Controls.Add(Ui.Label(AppTitle, Ui.Pad, 10, Ui.TitleFont));
        header.Controls.Add(Ui.CreditLabel(Ui.Pad + 2, 48));
        const int by = 24;
        header.Controls.Add(Ui.Button("Open my save", 400, 150, (_, _) => OpenGameSave(), by));
        header.Controls.Add(Ui.Button("Open a file...", 558, 130, (_, _) => OpenDialog(), by));
        _saveBtn = Ui.Button("Save", 712, 110, (_, _) => SaveTo(_save?.Path), by);
        _saveAsBtn = Ui.Button("Save a copy...", 830, 130, (_, _) => SaveAsDialog(), by);
        _reloadBtn = Ui.Button("Undo all changes", 968, 150, (_, _) => { if (_save != null && ConfirmDiscard()) LoadSave(_save.Path); }, by);
        header.Controls.AddRange(new Control[] { _saveBtn, _saveAsBtn, _reloadBtn });

        var footer = Ui.Panel(DockStyle.Bottom, 32);
        _status.Location = new Point(Ui.Pad, 8);
        _status.Text = "No save open";
        var footerCredit = Ui.Label($"{AppTitle}  -  {Ui.Credit}", 0, 8);
        footerCredit.Appearance.ForeColor = Ui.Accent;
        footer.Controls.Add(_status);
        footer.Controls.Add(footerCredit);
        void PlaceCredit() => footerCredit.Left = footer.ClientSize.Width - footerCredit.Width - Ui.Pad;
        footer.Resize += (_, _) => PlaceCredit();
        PlaceCredit();

        _log.Dock = DockStyle.Fill;
        _log.Properties.ReadOnly = true;

        _tabs.AppearancePage.Header.Font = Ui.BigFont;
        _tabs.AppearancePage.HeaderActive.Font = new Font(Ui.BigFont, FontStyle.Bold);
        Controls.Add(_tabs);
        Controls.Add(footer);
        Controls.Add(header);

        _tabs.SelectedPageChanged += (_, _) => { foreach (var r in _refreshers) r(); };
        FormClosing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };

        ShowPages();
        UpdateButtons();
        Log($"Ready. {_names.Count} item names loaded.");
        Shown += (_, _) =>
        {
            if (startPath != null) LoadSave(startPath);
            // Optional argument "--tab=<title>" opens on that tab (used for screenshots/testing).
            string? tab = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tab=", StringComparison.Ordinal))?[6..];
            var page = _tabs.TabPages.FirstOrDefault(p => p.Text.Equals(tab, StringComparison.OrdinalIgnoreCase));
            if (page != null) _tabs.SelectedTabPage = page;
        };
    }

    // ---- logging / state ----------------------------------------------------

    private void Log(string message)
    {
        _log.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _log.SelectionStart = _log.Text.Length;
        _log.ScrollToCaret();
        _status.Text = message;
    }

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle() =>
        Text = _save == null
            ? $"{AppTitle} - {Ui.Credit}"
            : $"{AppTitle} - {Ui.Credit} - {Path.GetFileName(_save.Path)}{(_dirty ? " (not saved yet)" : "")}";

    private void UpdateButtons() => _saveBtn.Enabled = _saveAsBtn.Enabled = _reloadBtn.Enabled = _save != null;

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        return XtraMessageBox.Show(this, "You have changes that are not saved. Throw them away?", AppTitle,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private List<NameEntry> E(params string[] tables)
    {
        string key = string.Join("|", tables);
        if (!_entryCache.TryGetValue(key, out var list)) _entryCache[key] = list = _names.Entries(tables);
        return list;
    }

    private FormPage Form() => new(_schema, _names, _catalog, MarkDirty);

    // ---- open / save ----------------------------------------------------------

    private void OpenDialog()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog
        {
            Filter = "Shinobi Striker saves (*.sav)|*.sav|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(GameSaveDir) ? GameSaveDir : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadSave(dlg.FileName);
    }

    private void OpenGameSave()
    {
        if (!ConfirmDiscard()) return;
        string path = Path.Combine(GameSaveDir, "Customize_v002.sav");
        if (!File.Exists(path))
        {
            Log($"No save found at {path}");
            XtraMessageBox.Show(this, $"No save was found in:\n{GameSaveDir}\n\nStart the game once so it makes a save, or use \"Open a file...\".",
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        LoadSave(path);
    }

    private void LoadSave(string path)
    {
        try
        {
            var save = ShinobiSaveFile.Load(path);
            _save = save;
            _catalog.Harvest(save);
            _schema.Harvest(save.Gvas);
            _dirty = false;
            ShowPages();
            Log($"Opened {path}" + (save.Items != null ? $" ({save.Items.Count(i => !i.IsEmpty)} items)" : ""));
            if (save.ItemBoxError != null) Log($"The item box could not be read and will be left untouched: {save.ItemBoxError}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GvasFormatException)
        {
            Log($"Could not open {path}: {ex.Message}");
            XtraMessageBox.Show(this, ex.Message, "Could not open the save", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateTitle();
        UpdateButtons();
    }

    private void SaveAsDialog()
    {
        if (_save == null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "Shinobi Striker saves (*.sav)|*.sav",
            FileName = Path.GetFileName(_save.Path),
            InitialDirectory = Path.GetDirectoryName(_save.Path) ?? "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) SaveTo(dlg.FileName);
    }

    private void SaveTo(string? path)
    {
        if (_save == null || path == null) return;
        if (Process.GetProcessesByName(GameProcess).Length > 0 &&
            XtraMessageBox.Show(this, "The game is running and will overwrite this file when it next saves.\nClose the game first. Save anyway?",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            Validate(); // push any editor still in edit mode into its data source
            foreach (var m in _maps) m.Commit();
            // Each item stores how many loadouts use it; keep that in step with the Loadouts page.
            if (_save.Items != null) _save.RecountEquipped();
            string? backup = _save.Save(path);
            _dirty = false;
            Log(backup != null ? $"Saved {path}. Old file kept as {Path.GetFileName(backup)}" : $"Saved {path}");
            foreach (var r in _refreshers) r();
            XtraMessageBox.Show(this,
                "Saved." + (backup != null ? $"\n\nA backup of the old file was kept next to it:\n{Path.GetFileName(backup)}" : ""),
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GvasFormatException)
        {
            Log($"Save failed: {ex.Message}");
            XtraMessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateTitle();
    }

    // ---- pages --------------------------------------------------------------

    private void ShowPages()
    {
        _tabs.BeginUpdate();
        _log.Parent = null;
        foreach (XtraTabPage page in _tabs.TabPages.ToArray()) { _tabs.TabPages.Remove(page); page.Dispose(); }
        _maps.Clear();
        _refreshers.Clear();

        if (_save == null)
        {
            AddPage(_tabs, "Start", BuildStartPage());
        }
        else if (_save.Kind == ShinobiSaveKind.Customize)
        {
            var counters = MapFor("m_gameCounters", new[] { "Group", "Name" }, k => { var c = _names.Counter(k); return new[] { c.group, c.label }; });
            var flags = MapFor("m_gameFlags", new[] { "Group", "Name", "What" }, k => { var f = _names.Flag(k); return new[] { f.group, f.label, f.what }; });
            AddPage(_tabs, "Player", BuildPlayerPage(counters));
            AddPage(_tabs, "Character", BuildCharacterPage());
            AddPage(_tabs, "Loadouts", BuildLoadoutsPage());
            AddPage(_tabs, "Items", BuildItemsPage());
            AddPage(_tabs, "Unlocks", BuildMapPage(flags, "On", true,
                "Everything the game marks as owned, unlocked or viewed. Pick a group, then switch things on or off."));
            AddPage(_tabs, "Stats", BuildMapPage(counters, "Amount", false,
                "Every number the game keeps count of: money, points, wins, master progress."));
            AddPage(_tabs, "Advanced", BuildAdvancedPage());
        }
        else
        {
            if (_save.Kind == ShinobiSaveKind.System) AddPage(_tabs, "Settings", BuildSystemPage());
            AddPage(_tabs, "Advanced", BuildAdvancedPage());
        }
        AddPage(_tabs, "About", BuildAboutPage());
        _tabs.EndUpdate();
        _tabs.SelectedTabPage = _tabs.TabPages[0];
    }

    private static void AddPage(XtraTabControl tabs, string title, Control content)
    {
        var page = new XtraTabPage { Text = title };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        tabs.TabPages.Add(page);
    }

    private MapTable? MapFor(string name, string[]? extraColumns = null, Func<string, string[]>? describe = null)
    {
        var map = _save?.Map(name);
        if (map == null) return null;
        var table = new MapTable(map, MarkDirty, extraColumns, describe);
        if (!table.Supported) return null;
        _maps.Add(table);
        return table;
    }

    private Control BuildStartPage()
    {
        var page = Ui.Panel(DockStyle.Fill);
        int x = Ui.Pad * 2, y = 36;
        page.Controls.Add(Ui.Label("Welcome", x, y, Ui.TitleFont)); y += 56;
        foreach (var step in new[]
                 {
                     "1.   Export your PS4 save and decrypt it with your save tool.",
                     "2.   Click  Open a file...  and pick Customize_v002.sav (or System_v002.sav).",
                     "3.   Change what you want on the pages that appear, then click  Save.",
                     "4.   Re-encrypt the save with your save tool and import it back to the PS4.",
                 })
        {
            page.Controls.Add(Ui.Label(step, x, y, Ui.BigFont));
            y += 34;
        }
        y += 16;
        page.Controls.Add(Ui.Button("Open my save", x, 200, (_, _) => OpenGameSave(), y));
        page.Controls.Add(Ui.Button("Open a file...", x + 212, 160, (_, _) => OpenDialog(), y));
        y += 70;
        page.Controls.Add(Ui.TextBlock(
            "Customize_v002.sav is your character. System_v002.sav is your game settings.\n" +
            "A backup of your old save is kept next to it every time you click Save.\n\n" +
            $"PC (Steam): Open my save opens {GameSaveDir}, but in testing the PC game kept the character on its servers, so changes did not show in game.",
            x, y, 900, 90));
        y += 100;
        page.Controls.Add(Ui.TextBlock(
            "Warning: this is an online game. Using an edited save is against the publisher's rules and can get your account banned. Use it at your own risk.",
            x, y, 900, 44, color: Ui.Warning));
        y += 56;
        page.Controls.Add(Ui.TextBlock(
            $"This editor is free. If someone sold it to you, or sold you an edited save, you were ripped off.  {Ui.Credit}.",
            x, y, 900, 44, Ui.BigFont, Ui.Accent));
        return page;
    }

    // ---- Player -----------------------------------------------------------------

    /// <summary>Longest player name the game accepts.</summary>
    private const int PlayerNameMax = 15;

    private Control BuildPlayerPage(MapTable? counters)
    {
        var root = _save!.Root;
        const string rootStruct = "NN_GameData_ForSave";

        var profile = Form();
        profile.Heading("Profile");
        if (FormPage.Struct(root, "m_playerBaseData") is { } b)
        {
            // The game takes at most 15 characters and no spaces ("zXIJz*Modded*Sa" is the longest).
            var name = profile.TextBox("Player name", b.structName, b.props, "m_playerName", PlayerNameMax);
            name.EditValueChanging += (_, e) =>
            {
                if (e.NewValue is string s && s.Contains(' ')) e.Cancel = true;
            };
            profile.Number("Level", b.structName, b.props, "m_playerRank", 0, 9999);
            profile.Pick("Village", b.structName, b.props, "m_IDCountry", E("DT_CountryInfo"));
            profile.Pick("Title", b.structName, b.props, "m_IDNickname", E("DT_Nickname"), withNone: true);
            profile.Pick("Master", b.structName, b.props, "m_IDMaster", E("DT_MasterData", "DT_DlcMasterAnnouncement"), withNone: true);
            profile.Pick("Ninja rank", b.structName, b.props, "m_IDShinobiRank", E("DT_ShinobiRank"));
            profile.Choice("Gender", b.structName, b.props, "m_sex");
        }
        else profile.Note("This save has no profile yet. Play the game once first.");
        profile.Heading("Ranked and extras");
        profile.Pick("Ranked grade", rootStruct, root, "m_latestMatchGrade", E("DT_MatchGrade"), withNone: true);
        profile.Number("Character remake tickets", rootStruct, root, "m_remakeTicketCount", 0, 9999);

        var money = Form();
        money.Heading("Money and points");
        if (counters == null) money.Note("This save has no counters.");
        else
        {
            foreach (var key in CommonCounters)
            {
                var row = counters.Find(key);
                if (row == null) continue;
                int value = (int)row["Value"];
                var range = _names.CounterRange(key);
                int max = Math.Max(value, range?.max ?? int.MaxValue);
                var spin = Ui.WholeNumber(Math.Min(value, 0), max);
                spin.Value = value;
                var maxBtn = new SimpleButton { Text = "Max", Width = 70 };
                maxBtn.Click += (_, _) => spin.Value = max;
                money.Row(_names.Counter(key).label, spin, maxBtn);

                bool refreshing = false;
                spin.EditValueChanged += (_, _) => { if (!refreshing && (int)row["Value"] != (int)spin.Value) row["Value"] = (int)spin.Value; };
                // The Stats page edits the same rows; pick its changes up when the user comes back here.
                _refreshers.Add(() =>
                {
                    if (row.RowState is DataRowState.Deleted or DataRowState.Detached) return;
                    int now = (int)row["Value"];
                    refreshing = true;
                    if (now > spin.Properties.MaxValue) spin.Properties.MaxValue = now;
                    spin.Value = now;
                    refreshing = false;
                });
            }
            money.Note("\"Max\" sets the highest amount the game allows. Master progress, wins and the rest are on the Stats page.");
        }
        return Ui.Columns(700, profile, money);
    }

    // ---- Character ------------------------------------------------------------------

    private Control BuildCharacterPage()
    {
        var page = Ui.Panel(DockStyle.Fill);
        var root = _save!.Root;
        var look = Form();
        var face = Form();

        var which = new RadioGroup { Location = new Point(Ui.Pad, 14), Size = new Size(460, 36) };
        which.Properties.Items.Add(new RadioGroupItem("m_customInfo", "Main character"));
        which.Properties.Items.Add(new RadioGroupItem("m_customInfoSub", "Second character"));
        which.Properties.Columns = 2;
        which.BorderStyle = BorderStyles.NoBorder;

        void Fill()
        {
            look.Reset();
            face.Reset();
            if (FormPage.Struct(root, which.EditValue as string ?? "m_customInfo") is not { } c)
            {
                look.Heading("Not in this save");
                look.Note("The game has not saved this character yet. Create it in the game first.");
                return;
            }
            var (s, p) = c;
            var colours = E("DT_PartsColors");
            look.Heading("Outfit and body");
            look.Pick("Top", s, p, "IDPartsJacket", E("DT_CustomJacket"), showInfo: true);
            look.Pick("Bottom", s, p, "IDPartsPants", E("DT_CustomPants"), showInfo: true);
            look.Pick("Accessory", s, p, "IDPartsAccessories", E("DT_CustomAccessories"), showInfo: true);
            look.Pick("Hairstyle", s, p, "IDPartsHair", E("DT_CustomHair"));
            look.Pick("Face paint", s, p, "IDPartsFacePaint", E("DT_CustomFacePaint"));
            look.Pick("Headband", s, p, "IDPartsForehead", E("DT_CustomForehead"));
            look.Pick("Body type", s, p, "IDPartsScale", E("DT_PartsScale"));
            look.Pick("Voice", s, p, "IDVoiceInfo", E("DT_VoiceInfo"));
            look.Choice("Gender", s, p, "Sex");

            face.Heading("Face");
            face.Pick("Eyes", s, p, "IDPartsEyes", E("DT_CustomEye"));
            face.Pick("Pupils", s, p, "IDPartsEyeBall", E("DT_CustomEyeBall"));
            face.Pick("Eyebrows", s, p, "IDPartsEyebrow", E("DT_CustomEyebrows"));
            face.Pick("Nose", s, p, "IDPartsNose", E("DT_CustomNose"));
            face.Pick("Mouth", s, p, "IDPartsMouth", E("DT_CustomMouth"));
            face.Heading("Colours");
            face.Pick("Skin colour", s, p, "IDPartsColorSkin", colours, colour: true);
            face.Pick("Hair colour", s, p, "IDPartsColorHair", colours, colour: true);
            face.Pick("Face paint colour", s, p, "IDPartsColorFacePaint", colours, colour: true);
            face.Pick("Headband colour", s, p, "IDPartsColorForehead", colours, colour: true);
            face.Pick("Eye colour", s, p, "IDPartsColorEyes", colours, colour: true);
        }
        which.EditValue = "m_customInfo";
        which.SelectedIndexChanged += (_, _) => Fill();
        Fill();

        var bar = Ui.Panel(DockStyle.Top, 60);
        bar.Controls.Add(which);
        bar.Controls.Add(Ui.Label("Tip: click a box and type to search, for example \"akatsuki\".", 520, 24));
        page.Controls.Add(Ui.Columns(700, look, face));
        page.Controls.Add(bar);
        return page;
    }

    // ---- Loadouts -------------------------------------------------------------------

    /// <summary>One choice in an "equipped item" drop-down: an item box slot and what is in it.</summary>
    private sealed class SlotEntry
    {
        public int Slot { get; init; }
        public string Name { get; init; } = "";
        public string Info { get; init; } = "";
    }

    private List<SlotEntry> SlotChoices(string prefix, int? current)
    {
        var list = new List<SlotEntry> { new() { Slot = -1, Name = "Nothing equipped" } };
        var items = _save?.Items;
        if (items != null)
            list.AddRange(items.Where(i => !i.IsEmpty && i.Id.StartsWith(prefix, StringComparison.Ordinal))
                .Select(i => new SlotEntry { Slot = i.Index, Name = _names.Display(i.Id), Info = $"Slot {i.Index}" })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
        if (current is { } slot && list.All(e => e.Slot != slot))
        {
            var item = items?.FirstOrDefault(i => i.Index == slot);
            list.Add(new SlotEntry { Slot = slot, Name = item == null || item.IsEmpty ? $"Empty slot {slot}" : _names.Display(item.Id), Info = $"Slot {slot}" });
        }
        return list;
    }

    private Control BuildLoadoutsPage()
    {
        var page = Ui.Panel(DockStyle.Fill);
        var loadouts = _save!.Loadouts.Where(l => l.Properties != null).ToList();
        if (loadouts.Count == 0)
        {
            page.Controls.Add(Ui.Note("This save has no loadouts yet. Play the game once first."));
            return page;
        }
        string structName = (_save.Prop("m_playerSettings")?.Value as GvasArray)?.InnerTag?.StructName ?? "NN_GameData_EfficencySetting";

        var list = new ListBoxControl { Dock = DockStyle.Left, Width = 250, ItemHeight = 38 };
        var skills = Form();
        var gear = Form();
        bool relabeling = false;

        string Title(int i)
        {
            string role = Ui.Text(loadouts[i].Properties!.FirstOrDefault(p => p.Name == "IDNameRole")?.Value);
            return $"  Loadout {i + 1}   -   {(role.Length == 0 ? "not set" : _names.Display(role))}";
        }
        void Relabel()
        {
            relabeling = true;
            int selected = list.SelectedIndex;
            list.Items.Clear();
            for (int i = 0; i < loadouts.Count; i++) list.Items.Add(Title(i));
            list.SelectedIndex = Math.Max(0, selected);
            relabeling = false;
        }

        // Jutsu lists start with the kind that belongs in the slot; the rest follow for anyone who wants them.
        List<NameEntry> JutsuFirst(string category)
        {
            string[] order = { category, "Normal", "FinishMove", "Kawarimi", "Summon" };
            string Word(string c) => c switch { "Normal" => "Ninjutsu", "FinishMove" => "Secret technique", "Kawarimi" => "Substitution", _ => "Summon" };
            return order.Distinct().SelectMany(c => _names.Jutsu(c).Select(e => new NameEntry { Id = e.Id, Name = e.Name, Info = Word(c) })).ToList();
        }
        var roles = E("DT_RoleData");
        var mainRoles = new[] { "ID_Role_Attack", "ID_Role_Shoot", "ID_Role_Defend", "ID_Role_Heal" };
        var roleChoices = roles.Where(r => mainRoles.Contains(r.Id)).Concat(roles.Where(r => !mainRoles.Contains(r.Id))).ToList();
        var normal = JutsuFirst("Normal");
        var finish = JutsuFirst("FinishMove");
        var kawarimi = JutsuFirst("Kawarimi");
        var summon = JutsuFirst("Summon");

        void Fill()
        {
            int i = list.SelectedIndex;
            if (relabeling || i < 0 || i >= loadouts.Count) return;
            var p = loadouts[i].Properties!;
            skills.Reset();
            gear.Reset();

            skills.Heading($"Loadout {i + 1}  (page {i / 4 + 1}, slot {i % 4 + 1})");
            var role = skills.Pick("Role", structName, p, "IDNameRole", roleChoices);
            role.EditValueChanged += (_, _) => Relabel();
            skills.Pick("Weapon", structName, p, "IDNameWeapon", E("DT_WeaponData"), showInfo: true);
            skills.Pick("Ninja tool", structName, p, "IDNameSubWeapon", E("DT_NinjaWeaponData"), showInfo: true);
            skills.Pick("Ninjutsu 1", structName, p, "IDNameJutsu_0", normal, showInfo: true);
            skills.Pick("Ninjutsu 2", structName, p, "IDNameJutsu_1", normal, showInfo: true);
            skills.Pick("Secret technique", structName, p, "IDNameJutsu_2", finish, showInfo: true);
            skills.Pick("Substitution", structName, p, "IDNameKawarimi", kawarimi, showInfo: true);
            skills.Pick("Summon", structName, p, "IDNameSummon", summon, withNone: true, showInfo: true);

            gear.Heading("Worn items (from your Items page)");
            foreach (var (field, label, prefix) in new[]
                     {
                         ("JacketItemIndex", "Top", "ID_CustomJacket_"), ("PantsItemIndex", "Bottom", "ID_CustomPants_"),
                         ("AccessoryItemIndex", "Accessory", "ID_CustomAccessory_"), ("WeaponItemIndex", "Weapon", "ID_Weapon_"),
                         ("SubWeaponItemIndex", "Ninja tool", "ID_CustomParts_"),
                     })
            {
                int? current = p.FirstOrDefault(x => x.Name == field)?.Value as int?;
                gear.PickNumber(label, structName, p, field, SlotChoices(prefix, current), nameof(SlotEntry.Slot));
            }
            gear.Note("These lists show the items you own. Add more on the Items page, then come back here.", 2);
        }

        Relabel();
        list.SelectedIndexChanged += (_, _) => Fill();
        Fill();
        _refreshers.Add(Fill); // the item box may have changed on the Items page

        page.Controls.Add(Ui.WeightedColumns(new[] { 1.45, 1.0 }, skills, gear));
        page.Controls.Add(list);
        return page;
    }

    // ---- Items ----------------------------------------------------------------------

    private Control BuildItemsPage()
    {
        var page = Ui.Panel(DockStyle.Fill);
        var items = _save!.Items;
        if (items == null)
        {
            page.Controls.Add(Ui.Note(_save.ItemBoxError != null
                ? $"The item box could not be read and is left untouched: {_save.ItemBoxError}"
                : "This save has no item box."));
            return page;
        }

        var list = new BindingList<InventoryItem>(items) { AllowNew = false, AllowRemove = false };
        var (grid, view) = Ui.Grid(list);
        view.OptionsView.ShowFilterPanelMode = ShowFilterPanelMode.Never;

        // Drop-down contents: everything the game knows, plus whatever this save already holds.
        var allItems = ItemKinds.SelectMany(k => E(k.table)).Where(e => !e.Id.EndsWith("_None", StringComparison.Ordinal) && !e.Id.EndsWith("_Never", StringComparison.Ordinal)).ToList();
        var itemChoices = _names.Choices(allItems, null, withNone: true);
        var skillChoices = _names.Choices(E("DT_SkillTable"), null, withNone: true);
        var typeChoices = ItemKinds.Select(k => new NameEntry { Id = k.type, Name = k.label }).ToList();
        typeChoices.Add(new NameEntry { Id = "Summon", Name = "Summon" });
        typeChoices.Add(new NameEntry { Id = "None", Name = "Empty" });
        void Know(List<NameEntry> choices, string value)
        {
            if (value.Length > 0 && choices.All(c => c.Id != value)) choices.Add(_names.Entry(value));
        }
        foreach (var it in items)
        {
            Know(itemChoices, it.Id); Know(typeChoices, it.Type);
            Know(skillChoices, it.Skill1); Know(skillChoices, it.Skill2); Know(skillChoices, it.Skill3);
        }

        Ui.Hide(view, nameof(InventoryItem.IsEmpty), nameof(InventoryItem.Flag), nameof(InventoryItem.Unknown1), nameof(InventoryItem.Unknown2));
        Ui.Column(view, nameof(InventoryItem.Index), "Slot", 60, 0, editable: false);
        Ui.Column(view, nameof(InventoryItem.Id), "Item", 330, 1);
        Ui.Column(view, nameof(InventoryItem.Type), "Kind", 110, 2);
        Ui.Column(view, nameof(InventoryItem.Skill1), "Skill 1", 190, 3);
        Ui.Column(view, nameof(InventoryItem.Skill2), "Skill 2", 190, 4);
        Ui.Column(view, nameof(InventoryItem.Skill3), "Skill 3", 190, 5);
        Ui.Column(view, nameof(InventoryItem.EquipCount), "Used by loadouts", 120, 6, editable: false);
        Ui.LookupColumn(grid, view, nameof(InventoryItem.Id), itemChoices, showInfo: true);
        Ui.LookupColumn(grid, view, nameof(InventoryItem.Type), typeChoices);
        foreach (var f in new[] { nameof(InventoryItem.Skill1), nameof(InventoryItem.Skill2), nameof(InventoryItem.Skill3) })
            Ui.LookupColumn(grid, view, f, skillChoices);
        view.CellValueChanged += (_, _) => MarkDirty();

        var bar = Ui.Panel(DockStyle.Top, 104);
        var newItem = Ui.Lookup(allItems, showInfo: true);
        newItem.Properties.NullText = "Pick an item to add (type to search)";
        newItem.Location = new Point(Ui.Pad, 14);
        newItem.Size = new Size(380, 32);

        var kind = new ComboBoxEdit { Location = new Point(560, 14), Size = new Size(190, 32) };
        kind.Properties.TextEditStyle = TextEditStyles.DisableTextEditor;
        kind.Properties.Items.Add("Everything");
        foreach (var k in ItemKinds) kind.Properties.Items.Add(k.label + "s");
        kind.Properties.Items.Add(DlcChoice);
        kind.SelectedIndex = 0;

        // Filters for the "Pick an item to add" list: rank and DLC (tagged in GameNames.IsDlc).
        var rankFilter = new ComboBoxEdit { Location = new Point(470, 60), Size = new Size(130, 32) };
        rankFilter.Properties.TextEditStyle = TextEditStyles.DisableTextEditor;
        rankFilter.Properties.Items.AddRange(new object[] { "Any rank", "Rank SS", "Rank S", "Rank A", "Rank B", "Rank C" });
        rankFilter.SelectedIndex = 0;
        var dlcOnly = new CheckEdit { Text = "DLC only", Location = new Point(612, 64), Width = 110 };
        void FilterAddList()
        {
            int rank = rankFilter.SelectedIndex - 1;
            newItem.Properties.DataSource = allItems
                .Where(e => rank < 0 || e.Rank == rank)
                .Where(e => !dlcOnly.Checked || _names.IsDlc(e.Id))
                .ToList();
            newItem.EditValue = null;
        }
        rankFilter.SelectedIndexChanged += (_, _) => FilterAddList();
        dlcOnly.CheckedChanged += (_, _) => FilterAddList();
        bar.Controls.Add(Ui.Label("Item list:", 400, 67));
        bar.Controls.Add(rankFilter);
        bar.Controls.Add(dlcOnly);

        var showEmpty = new CheckEdit { Text = "Show empty slots", Location = new Point(Ui.Pad, 64), Width = 180 };
        var count = Ui.Label("", 230, 67);
        void ShowCount() => count.Text = $"{items.Count(i => !i.IsEmpty)} of {items.Count} slots used";
        void ApplyFilter() => view.ActiveFilterString = showEmpty.Checked ? "" : "[Id] <> 'None' And [Id] <> ''";
        showEmpty.CheckedChanged += (_, _) => ApplyFilter();
        ApplyFilter();

        bool Put(string id) => EditOps.PutItem(items, _names, id);
        void AfterChange()
        {
            list.ResetBindings();
            MarkDirty();
            ShowCount();
        }

        bar.Controls.Add(newItem);
        bar.Controls.Add(Ui.Button("Add item", 412, 120, (_, _) =>
        {
            if (newItem.EditValue is not string id) return;
            if (!Put(id)) { Log("The item box is full. Remove something first."); return; }
            AfterChange();
            Log($"Added {_names.Display(id)}.");
        }));
        bar.Controls.Add(kind);
        bar.Controls.Add(Ui.Button("Add all I don't have", 758, 180, (_, _) =>
        {
            var wanted = kind.SelectedIndex <= 0 ? allItems
                : kind.Text == DlcChoice ? EditOps.DlcItems(_names)
                : E(ItemKinds[kind.SelectedIndex - 1].table);
            var owned = new HashSet<string>(items.Select(i => i.Id), StringComparer.Ordinal);
            var missing = wanted.Where(e => !owned.Contains(e.Id) && !e.Id.EndsWith("_None", StringComparison.Ordinal) && !e.Id.EndsWith("_Never", StringComparison.Ordinal)).ToList();
            if (missing.Count == 0) { Log("You already have all of those."); return; }
            if (XtraMessageBox.Show(this, $"Add {missing.Count} items to the item box?", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int added = 0;
            foreach (var e in missing) { if (!Put(e.Id)) break; added++; }
            AfterChange();
            Log(added == missing.Count ? $"Added {added} items." : $"Added {added} of {missing.Count} items, then the item box was full.");
        }));
        bar.Controls.Add(Ui.Button("Remove selected", 962, 150, (_, _) =>
        {
            var selected = view.GetSelectedRows().Select(h => view.GetRow(h)).OfType<InventoryItem>().Where(i => !i.IsEmpty).ToList();
            if (selected.Count == 0) return;
            if (XtraMessageBox.Show(this, $"Remove {selected.Count} item(s)? Loadouts wearing them will have nothing in that slot.", AppTitle,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var it in selected) it.Clear();
            AfterChange();
            Log($"Removed {selected.Count} item(s).");
        }));
        bar.Controls.Add(showEmpty);
        bar.Controls.Add(count);
        ShowCount();
        _refreshers.Add(() => { ShowCount(); grid.RefreshDataSource(); });

        page.Controls.Add(grid);
        page.Controls.Add(bar);
        return page;
    }

    // ---- Unlocks / Stats ----------------------------------------------------------------

    private Control BuildMapPage(MapTable? table, string valueCaption, bool onOff, string note)
    {
        var page = Ui.Panel(DockStyle.Fill);
        if (table == null)
        {
            page.Controls.Add(Ui.Note("Not in this save."));
            return page;
        }
        var (grid, view) = Ui.Grid(table.Table);
        view.OptionsView.ShowFilterPanelMode = ShowFilterPanelMode.Never;
        Ui.Hide(view, "_origin", "Key");
        Ui.Column(view, "Group", "Group", 170, 0, editable: false);
        Ui.Column(view, "Name", "Name", 520, 1, editable: false);
        if (onOff) Ui.Column(view, "What", "What it records", 150, 2, editable: false);
        Ui.Column(view, "Value", valueCaption, 140, 3);
        view.Columns["Group"].SortOrder = DevExpress.Data.ColumnSortOrder.Ascending;
        view.Columns["Key"].Caption = "Game ID";
        view.Columns["Key"].OptionsColumn.AllowEdit = false;

        const string everything = "Everything";
        var groups = table.Table.Rows.Cast<DataRow>().Select(r => (string)r["Group"]).Distinct()
            .Where(g => g != Reserved).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        var group = new ComboBoxEdit { Location = new Point(Ui.Pad + 60, 14), Size = new Size(230, 32) };
        group.Properties.TextEditStyle = TextEditStyles.DisableTextEditor;
        group.Properties.DropDownRows = 20;
        group.Properties.Items.Add(everything);
        group.Properties.Items.AddRange(groups.Cast<object>().ToArray());
        if (table.Table.Rows.Cast<DataRow>().Any(r => (string)r["Group"] == Reserved)) group.Properties.Items.Add(Reserved);
        void ApplyFilter()
        {
            string g = group.Text;
            view.ActiveFilterString = g == everything ? $"[Group] <> '{Reserved}'" : $"[Group] = '{g.Replace("'", "''")}'";
        }
        group.SelectedIndexChanged += (_, _) => ApplyFilter();
        group.SelectedIndex = 0;

        var bar = Ui.Panel(DockStyle.Top, 60);
        bar.Controls.Add(Ui.Label("Show", Ui.Pad, 22));
        bar.Controls.Add(group);
        void SetShown(object value, string words)
        {
            var rows = Ui.ShownRows(view);
            if (rows.Count == 0) return;
            if (XtraMessageBox.Show(this, $"{words} all {rows.Count} rows in the list right now?", AppTitle,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            view.BeginDataUpdate();
            foreach (var r in rows) r["Value"] = value;
            view.EndDataUpdate();
            Log($"{words}: {rows.Count} rows.");
        }
        int x = Ui.Pad + 310;
        if (onOff)
        {
            bar.Controls.Add(Ui.Button("Switch ON all shown", x, 180, (_, _) => SetShown(true, "Switch ON")));
            bar.Controls.Add(Ui.Button("Switch OFF all shown", x + 190, 180, (_, _) => SetShown(false, "Switch OFF")));
            x += 390;
        }
        var ids = new CheckEdit { Text = "Show game IDs", Location = new Point(x, 20), Width = 160 };
        ids.CheckedChanged += (_, _) =>
        {
            view.Columns["Key"].Visible = ids.Checked;
            if (ids.Checked) { view.Columns["Key"].VisibleIndex = 9; view.Columns["Key"].Width = 330; }
        };
        bar.Controls.Add(ids);

        page.Controls.Add(grid);
        page.Controls.Add(Ui.Note(note + " Use the search box to find something by name."));
        page.Controls.Add(bar);
        return page;
    }

    // ---- Settings (System_v002.sav) -----------------------------------------------------

    private static readonly Dictionary<string, string> SettingLabels = new(StringComparer.Ordinal)
    {
        ["m_brightness"] = "Brightness", ["m_windowMode"] = "Window mode", ["m_resolution"] = "Resolution",
        ["m_antiAlias"] = "Anti-aliasing", ["m_renderQuality"] = "Render quality", ["m_shadowQuality"] = "Shadow quality",
        ["m_vsync"] = "V-Sync", ["m_bloom"] = "Bloom", ["m_screenPercentage"] = "Resolution scale (%)",
        ["m_displayButtonInstractions"] = "Show button hints", ["PlayerDisplayName"] = "Player name display",
        ["VolumeBgm"] = "Music volume", ["VolumeSE"] = "Sound effects volume", ["VolumeVoice"] = "Voice volume",
        ["VolumeVoiceChat"] = "Voice chat volume", ["VoiceLanguage"] = "Voice language",
        ["bInitialized"] = "First-time setup done", ["LicenseAgreementsVersion"] = "Licence version accepted",
        ["PrivacyPolicyAgreementsVersion"] = "Privacy version accepted",
    };

    private Control BuildSystemPage()
    {
        var root = _save!.Root;
        var screen = Form();
        screen.Heading("Screen");
        if (FormPage.Struct(root, "DisplaySettings") is { } display)
        {
            if (FormPage.Struct(display.props, "ScreenSettings") is { } ss) screen.Auto(ss.structName, ss.props, SettingLabels);
            screen.Auto(display.structName, display.props, SettingLabels);
        }
        else screen.Note("The game has not saved any screen settings yet.");

        var other = Form();
        other.Heading("Sound");
        if (FormPage.Struct(root, "SoundSettings") is { } sound) other.Auto(sound.structName, sound.props, SettingLabels);
        else other.Note("The game has not saved any sound settings yet.");
        other.Heading("General");
        other.Auto("NN_GameSystemData_ForSave", root, SettingLabels);
        return Ui.Columns(700, screen, other);
    }

    // ---- Advanced -----------------------------------------------------------------------

    private Control BuildAdvancedPage()
    {
        var tabs = new XtraTabControl { Dock = DockStyle.Fill };
        var root = _save!.Root;

        var treePage = Ui.Panel(DockStyle.Fill);
        var tree = new TreePage(MarkDirty, Log);
        tree.Load(_save.Gvas);
        treePage.Controls.Add(tree.Control);
        treePage.Controls.Add(Ui.Note(
            "For people who know what they are doing: every field in the file. Edit the Value column. Changes made on other pages show here after you save and reopen."));
        AddPage(tabs, "Everything in the file", treePage);

        if (_save.Kind == ShinobiSaveKind.Customize)
        {
            foreach (var (name, title, valueCaption) in new[] { ("m_mapScrolls", "Scrolls", "Amount"), ("m_gameStringDatas", "Game text values", "Text") })
            {
                var table = MapFor(name);
                var p = Ui.Panel(DockStyle.Fill);
                if (table == null) p.Controls.Add(Ui.Note("Not in this save."));
                else
                {
                    var (grid, view) = Ui.Grid(table.Table, allowAdd: true, find: false);
                    Ui.Hide(view, "_origin");
                    Ui.Column(view, "Key", "Game ID", 400, 0);
                    Ui.Column(view, "Value", valueCaption, 300, 1);
                    p.Controls.Add(grid);
                }
                AddPage(tabs, title, p);
            }

            var rank = Form();
            rank.Heading("Ranked match");
            if (FormPage.Struct(root, "RankMatchData") is { } r) rank.Auto(r.structName, r.props);
            else rank.Note("Not in this save.");
            rank.Heading("Latest results");
            rank.Auto("NN_GameData_ForSave", root, new Dictionary<string, string>
            {
                ["m_latestNinjaRank"] = "Latest ninja rank", ["m_loadoutPage"] = "Selected loadout page", ["m_loadoutSlot"] = "Selected loadout slot",
                ["m_selectPlayerSettings"] = "Selected loadout", ["m_compressedDataVersion"] = "Item box version",
            }, "m_latestMatchGrade", "m_remakeTicketCount");
            var evt = Form();
            evt.Heading("Event match");
            if (FormPage.Struct(root, "EventMatchData") is { } ev) evt.Auto(ev.structName, ev.props);
            else evt.Note("Not in this save.");
            AddPage(tabs, "Matches", Ui.Columns(700, rank, evt));
        }

        AddPage(tabs, "Activity log", _log);
        return tabs;
    }

    // ---- About --------------------------------------------------------------------------

    private Control BuildAboutPage()
    {
        var page = Ui.Panel(DockStyle.Fill);
        int x = Ui.Pad * 2, y = 32;
        page.Controls.Add(Ui.Label(AppTitle, x, y, Ui.TitleFont)); y += 44;
        page.Controls.Add(Ui.Label(Ui.Credit, x, y, Ui.TitleFont, Ui.Accent)); y += 56;
        page.Controls.Add(Ui.TextBlock(
            "A free save editor for decrypted PS4 saves of NARUTO TO BORUTO: SHINOBI STRIKER.\n" +
            "It is free and always will be. If you paid for this editor or for an edited save, you were ripped off.",
            x, y, 900, 50, Ui.BigFont)); y += 70;

        page.Controls.Add(Ui.Label("Credits", x, y, Ui.HeadingFont)); y += 36;
        foreach (var (who, what, link) in new[]
                 {
                     (Ui.Author, "created the editor and mapped the save format", ""),
                     ("DevExpress WinForms", "the buttons, lists and tables", "https://www.devexpress.com/products/net/controls/winforms/"),
                     (".NET 8", "what the editor runs on", "https://dotnet.microsoft.com/"),
                     ("Unreal Engine 4", "the save file (GVAS) and pak formats the game uses", "https://www.unrealengine.com/"),
                     ("NARUTO TO BORUTO: SHINOBI STRIKER", "the game, by Soleil and Bandai Namco", "https://store.steampowered.com/app/633230/"),
                 })
        {
            page.Controls.Add(Ui.Label(who, x, y, new Font(Ui.BigFont, FontStyle.Bold), who == Ui.Author ? Ui.Accent : null));
            page.Controls.Add(Ui.Label(what, x + 330, y + 2));
            if (link.Length > 0)
            {
                var url = new HyperlinkLabelControl { Text = link, Location = new Point(x + 720, y + 2) };
                url.HyperlinkClick += (_, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Log($"Could not open {link}: {ex.Message}"); }
                };
                page.Controls.Add(url);
            }
            y += 34;
        }
        y += 22;
        page.Controls.Add(Ui.TextBlock(
            "Item, jutsu and title names are the game's own text. This is a fan-made tool. It is not made, approved or supported by Bandai Namco, Soleil or anyone who owns NARUTO or BORUTO.",
            x, y, 900, 44)); y += 56;
        page.Controls.Add(Ui.TextBlock(
            "Warning: this is an online game. Using an edited save is against the publisher's rules and can get your account banned. Use it at your own risk.",
            x, y, 900, 44, color: Ui.Warning));
        return page;
    }
}
