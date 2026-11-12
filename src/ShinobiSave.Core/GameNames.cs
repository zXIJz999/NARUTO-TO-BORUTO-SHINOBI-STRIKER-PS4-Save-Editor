using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ShinobiSave.Core;

/// <summary>One choice in a drop-down: the game ID that is stored and the text that is shown.</summary>
public sealed class NameEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Short extra fact shown in a second column (rarity, type, colour code).</summary>
    public string Info { get; init; } = "";
    /// <summary>0 = best (SS) ... 4 = C, 5 = no rank. Used to sort drop-downs by rank.</summary>
    public int Rank { get; init; } = 5;
    public override string ToString() => Name;
}

/// <summary>What the game's data tables say about one ID.</summary>
public sealed class GameItemInfo
{
    public string Id = "", Name = "", Table = "", Rarity = "";
    public string[] Skills = Array.Empty<string>();
    public Dictionary<string, string> Extra = new(StringComparer.Ordinal);
    public string? this[string key] => Extra.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// Turns game IDs (ID_CustomJacket_M_16) into the names the game shows (Hidden Cloud: Top).
/// Built from the embedded names.tsv; anything not in it gets a tidied-up version of its ID.
/// </summary>
public sealed class GameNames
{
    public const string None = "None";

    private readonly Dictionary<string, GameItemInfo> _info = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GameItemInfo>> _byTable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _display = new(StringComparer.Ordinal);

    public int Count => _info.Count;

    public static GameNames Load()
    {
        var g = new GameNames();
        var asm = typeof(GameNames).GetTypeInfo().Assembly;
        using var s = asm.GetManifestResourceStream("ShinobiSave.Core.names.tsv");
        if (s == null) return g;
        using var r = new StreamReader(s, Encoding.UTF8);
        while (r.ReadLine() is { } line)
        {
            var p = line.Split('\t');
            if (p.Length < 3 || p[0].Length == 0) continue;
            var info = new GameItemInfo
            {
                Id = p[0], Name = p[1], Table = p[2], Rarity = p.Length > 3 ? p[3] : "",
                Skills = p.Length > 6 ? new[] { p[4], p[5], p[6] } : Array.Empty<string>(),
            };
            if (p.Length > 7)
                foreach (var kv in p[7].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0 && !info.Extra.ContainsKey(kv[..eq])) info.Extra[kv[..eq]] = kv[(eq + 1)..];
                }
            g._info[info.Id] = info;
            if (!g._byTable.TryGetValue(info.Table, out var list)) g._byTable[info.Table] = list = new List<GameItemInfo>();
            list.Add(info);
        }
        g.BuildDisplayNames();
        return g;
    }

    public GameItemInfo? Info(string? id) => id != null && _info.TryGetValue(id, out var i) ? i : null;

    /// <summary>The name to show for an ID. Never empty and unique within the ID's table.</summary>
    public string Display(string? id)
    {
        if (string.IsNullOrEmpty(id) || id == None) return "None";
        return _display.TryGetValue(id, out var s) ? s : Prettify(id);
    }

    /// <summary>Every ID of the given data tables as drop-down entries, in game order.</summary>
    public List<NameEntry> Entries(params string[] tables)
    {
        var result = new List<NameEntry>();
        foreach (var t in tables)
            if (_byTable.TryGetValue(t, out var list))
                foreach (var i in list) result.Add(Entry(i.Id));
        return result;
    }

    public NameEntry Entry(string id)
    {
        var i = Info(id);
        int rank = i?.Rarity switch { "SS" => 0, "S" => 1, "A" => 2, "B" => 3, "C" => 4, _ => 5 };
        return new NameEntry { Id = id, Name = Display(id), Info = i == null ? "" : InfoText(i), Rank = rank };
    }

    /// <summary>
    /// Entries for a drop-down, guaranteed to contain <paramref name="current"/> (a value the
    /// save already holds, even one the tables do not know) and optionally a "None" choice.
    /// </summary>
    public List<NameEntry> Choices(IEnumerable<NameEntry> entries, string? current, bool withNone = false)
    {
        var list = new List<NameEntry>();
        if (withNone) list.Add(new NameEntry { Id = None, Name = "None" });
        list.AddRange(entries.Where(e => !withNone || e.Id != None));
        if (!string.IsNullOrEmpty(current) && list.All(e => e.Id != current)) list.Add(Entry(current));
        return list;
    }

    public List<NameEntry> Jutsu(string category) =>
        Entries("DT_NinjutsuData").Where(e => Info(e.Id)?["category"]?.EndsWith(category, StringComparison.Ordinal) == true).ToList();

    private static string InfoText(GameItemInfo i)
    {
        if (i.Table == "DT_PartsColors") return i["color"] ?? "";
        if (i.Table == "DT_NinjutsuData") return i["type"] ?? "";
        if (i.Rarity.Length > 0 && i.Rarity != None) return "Rank " + i.Rarity;
        return "";
    }

    // ---- display names ------------------------------------------------------

    private void BuildDisplayNames()
    {
        foreach (var (table, list) in _byTable)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var i in list) names[i.Id] = BaseName(i);

            // Male and female versions of an outfit share a name: say which is which.
            foreach (var group in list.GroupBy(i => names[i.Id]).Where(g => g.Count() > 1).ToList())
                foreach (var i in group)
                    if (i["sex"] is "Male" or "Female") names[i.Id] = $"{names[i.Id]} ({i["sex"]})";

            // Anything still sharing a name gets a number.
            foreach (var group in list.GroupBy(i => names[i.Id]).Where(g => g.Count() > 1).ToList())
            {
                int n = 0;
                foreach (var i in group) if (++n > 1) names[i.Id] = $"{names[i.Id]} #{n}";
            }
            foreach (var kv in names) _display[kv.Key] = kv.Value;
        }
    }

    private static string BaseName(GameItemInfo i)
    {
        if (i.Table == "DT_PartsColors") return ColourLabel(i);
        string name = i.Name.Replace(" : ", ": ").Trim();
        if (name.Length == 0 || name == "-") return i.Id.EndsWith("_None", StringComparison.Ordinal) ? "None" : Prettify(i.Id);
        return name;
    }

    private static string ColourLabel(GameItemInfo i)
    {
        string plain = Prettify(i.Id);
        return i["color"] is { Length: 7 } hex ? $"{ColourWord(hex)} - {plain}" : plain;
    }

    private static readonly (string name, int r, int g, int b)[] ColourWords =
    {
        ("Black", 0, 0, 0), ("White", 255, 255, 255), ("Grey", 128, 128, 128), ("Light grey", 200, 200, 200), ("Dark grey", 64, 64, 64),
        ("Red", 220, 30, 30), ("Dark red", 120, 15, 15), ("Pink", 245, 150, 190), ("Hot pink", 240, 60, 150),
        ("Orange", 245, 140, 30), ("Yellow", 245, 225, 50), ("Gold", 205, 165, 60), ("Cream", 245, 230, 190),
        ("Brown", 120, 75, 40), ("Dark brown", 65, 40, 25), ("Tan", 200, 160, 115), ("Peach", 240, 195, 160),
        ("Green", 50, 170, 60), ("Dark green", 20, 85, 35), ("Lime", 160, 225, 60), ("Olive", 120, 125, 50),
        ("Teal", 30, 140, 140), ("Cyan", 70, 215, 230), ("Sky blue", 130, 190, 240), ("Blue", 40, 80, 215), ("Navy", 20, 30, 95),
        ("Purple", 130, 50, 180), ("Dark purple", 65, 25, 95), ("Lavender", 190, 160, 235), ("Magenta", 205, 50, 205),
    };

    /// <summary>Nearest plain colour word for a #RRGGBB value.</summary>
    public static string ColourWord(string hex)
    {
        if (hex.Length != 7 || !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return "Colour";
        int r = rgb >> 16 & 255, g = rgb >> 8 & 255, b = rgb & 255;
        string best = "Colour";
        long bestDistance = long.MaxValue;
        foreach (var c in ColourWords)
        {
            long d = (long)(r - c.r) * (r - c.r) * 3 + (long)(g - c.g) * (g - c.g) * 4 + (long)(b - c.b) * (b - c.b) * 2;
            if (d < bestDistance) { bestDistance = d; best = c.name; }
        }
        return best;
    }

    // ---- tidy names for things the game has no text for -------------------------

    private static readonly (string from, string to)[] Words =
    {
        ("PVP", "PvP"), ("Dom", "Base Battle"), ("Team Death Match", "Combat Battle"), ("Destroy", "Barrier Battle"),
        ("Battle Royal", "Survival"), ("Kiled", "Deaths"), ("Njt", "Jutsu"), ("NJT", "Jutsu"), ("Exp", "XP"),
    };

    /// <summary>ID_PartsColor_Hair_54 -> "Hair 54", m_playerName -> "Player name".</summary>
    public static string Prettify(string id)
    {
        string s = id;
        int sep = s.IndexOf("::", StringComparison.Ordinal);
        if (sep >= 0)
        {
            string enumName = s[..sep];
            s = s[(sep + 2)..];
            if (s.StartsWith(enumName + "_", StringComparison.Ordinal)) s = s[(enumName.Length + 1)..];
            s = Regex.Replace(s, "^ENN[A-Za-z]*_", "");
        }
        s = Regex.Replace(s, "^(ID_|m_|b(?=[A-Z]))", "");
        s = Regex.Replace(s, "^(Parts_?Color|PartsColor|Custom(?=[A-Z])|Counter|Flag|CustomParts)_?", "");
        s = s.Replace('_', ' ');
        s = Regex.Replace(s, "(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        foreach (var (from, to) in Words) s = Regex.Replace(s, $@"\b{from}\b", to);
        return s.Length == 0 ? id : char.ToUpperInvariant(s[0]) + s[1..];
    }

    // ---- counters -----------------------------------------------------------

    private static readonly Dictionary<string, string> CounterLabels = new(StringComparer.Ordinal)
    {
        ["ID_Counter_Money"] = "Ryo (money)",
        ["ID_Counter_TotalMoney"] = "Total ryo earned",
        ["ID_Counter_PlayerExperience"] = "Experience (XP)",
        ["ID_Counter_PlayerExperienceAccumulation"] = "Total XP earned",
        ["ID_Counter_SkillPoint"] = "Skill points",
        ["ID_Counter_MasterPoint"] = "Master points",
        ["ID_Counter_PlayTimeMinute"] = "Play time (minutes)",
        ["ID_Counter_Win_PVP"] = "PvP wins",
        ["ID_Counter_Play_PVP"] = "PvP matches played",
        ["ID_Counter_PVP_Kill"] = "PvP kills",
        ["ID_Counter_PVP_Dead"] = "PvP deaths",
        ["ID_Counter_UseSpecial"] = "Secret techniques used",
        ["ID_Counter_CheckScroll"] = "Scrolls appraised",
    };

    public (string group, string label) Counter(string key)
    {
        if (CounterLabels.TryGetValue(key, out var fixedLabel))
            return (key.Contains("PVP", StringComparison.Ordinal) ? "PvP" : "Main", fixedLabel);
        string rest = key.StartsWith("ID_Counter_", StringComparison.Ordinal) ? key["ID_Counter_".Length..] : key;
        if (Regex.IsMatch(rest, @"^R\d+$")) return ("Reserved (unused)", "Reserved " + rest[1..]);
        if (rest.StartsWith("MasterPoint_", StringComparison.Ordinal)) return ("Masters", "Master XP: " + Master(rest[12..]));
        if (rest.StartsWith("MasterRank_", StringComparison.Ordinal)) return ("Masters", "Master rank: " + Master(rest[11..]));
        var mission = Regex.Match(rest, "^Mission_([A-Z])_Clear$");
        if (mission.Success) return ("Missions", $"{mission.Groups[1].Value}-rank missions cleared");
        if (rest.StartsWith("Scenario_", StringComparison.Ordinal)) return ("Story", "Story step: " + Prettify(rest[9..]));
        if (rest.StartsWith("ExpScale_", StringComparison.Ordinal) || rest == "ExpBase") return ("XP rates", "XP rate: " + Prettify(rest.Replace("ExpScale_", "")));
        if (rest.StartsWith("Win_PVP_", StringComparison.Ordinal)) return ("PvP", "PvP wins: " + Prettify(rest[8..]));
        if (rest.StartsWith("Play_PVP_Role", StringComparison.Ordinal)) return ("PvP", "PvP matches as " + Role(rest[13..]));
        if (rest.StartsWith("Play_PVP_", StringComparison.Ordinal)) return ("PvP", "PvP matches: " + Prettify(rest[9..]));
        if (rest.StartsWith("Play_Role", StringComparison.Ordinal)) return ("Missions", "Missions played as " + Role(rest[9..]));
        return ("Other", Prettify(key));
    }

    private string Master(string shortName) => _display.TryGetValue("ID_Master_" + shortName, out var s) ? s : Prettify(shortName);
    private string Role(string shortName) => _display.TryGetValue("ID_Role_" + shortName, out var s) ? s + " type" : Prettify(shortName);

    public (int min, int max)? CounterRange(string key)
    {
        var i = Info(key);
        if (i == null || !int.TryParse(i["min"], out int min) || !int.TryParse(i["max"], out int max) || max <= min) return null;
        return (min, max);
    }

    // ---- flags --------------------------------------------------------------

    private static readonly (string prefix, string group)[] FlagGroups =
    {
        ("CustomJacket_", "Tops"), ("CustomPants_", "Bottoms"), ("CustomAccessory_", "Accessories"), ("CustomFacePaint_", "Face paint"),
        ("CustomHair_", "Hairstyles"), ("CustomForehead_", "Headbands"), ("CustomParts_", "Ninja tools"), ("Weapon_", "Weapons"),
        ("NJT_", "Jutsu"), ("Nickname_", "Titles"), ("Master_", "Masters"), ("Role_", "Roles"), ("ML_", "Missions"), ("ShopItem_", "Shop items"),
    };

    private static readonly (string prefix, string what)[] FlagKinds =
    {
        ("Acquisition_", "Owned"), ("Open_", "Unlocked"), ("Check_", "Viewed"), ("Clear_", "Cleared"),
    };

    /// <summary>Splits an unlock flag into its group (Tops, Jutsu...), what it records and the thing it is about.</summary>
    public (string group, string what, string label) Flag(string key)
    {
        string rest = key.StartsWith("ID_Flag_", StringComparison.Ordinal) ? key["ID_Flag_".Length..] : key;
        if (Regex.IsMatch(rest, @"^R\d+$")) return ("Reserved (unused)", "", "Reserved " + rest[1..]);

        string what = "";
        foreach (var (prefix, w) in FlagKinds)
            if (rest.StartsWith(prefix, StringComparison.Ordinal)) { what = w; rest = rest[prefix.Length..]; break; }

        foreach (var (prefix, group) in FlagGroups)
        {
            if (!rest.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string id = "ID_" + rest;
            return (group, what, _display.TryGetValue(id, out var name) ? name : Prettify(rest[prefix.Length..]));
        }
        if (rest.StartsWith("BASIC_", StringComparison.Ordinal)) return ("Master lessons", what, "Lesson: " + Prettify(rest[6..]));
        if (rest.StartsWith("DLC_", StringComparison.Ordinal)) return ("DLC", what, Prettify(rest[4..]));
        return ("Other", what, Prettify(rest));
    }
}
