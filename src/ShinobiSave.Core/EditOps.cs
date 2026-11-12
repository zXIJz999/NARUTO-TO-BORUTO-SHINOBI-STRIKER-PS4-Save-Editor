namespace ShinobiSave.Core;

/// <summary>
/// Every rule that decides what bytes an edit produces. The desktop editor and the web editor
/// both call these, so the same edit gives the same file in either.
/// </summary>
public static class EditOps
{
    /// <summary>Longest player name the game accepts ("zXIJz*Modded*Sa"). Spaces are not allowed.</summary>
    public const int PlayerNameMax = 15;

    /// <summary>Applies the game's player-name rules: no spaces, at most 15 characters.</summary>
    public static string CleanPlayerName(string name)
    {
        var s = name.Replace(" ", "");
        return s.Length > PlayerNameMax ? s[..PlayerNameMax] : s;
    }

    /// <summary>Item kinds as stored in the item box, with the words shown for them.</summary>
    public static readonly (string type, string label, string prefix, string table)[] ItemKinds =
    {
        ("Jacket", "Top", "ID_CustomJacket_", "DT_CustomJacket"),
        ("Pants", "Bottom", "ID_CustomPants_", "DT_CustomPants"),
        ("Accessory", "Accessory", "ID_CustomAccessory_", "DT_CustomAccessories"),
        ("Facepaint", "Face paint", "ID_CustomFacePaint_", "DT_CustomFacePaint"),
        ("Weapon", "Weapon", "ID_Weapon_", "DT_WeaponData"),
        ("NinjaWeapon", "Ninja tool", "ID_CustomParts_", "DT_NinjaWeaponData"),
    };

    /// <summary>IDs the game uses as placeholders; never offered as items to add.</summary>
    public static bool IsPlaceholder(string id) =>
        id.EndsWith("_None", StringComparison.Ordinal) || id.EndsWith("_Never", StringComparison.Ordinal);

    /// <summary>Every item the game knows, for the item box drop-downs.</summary>
    public static List<NameEntry> AllItems(GameNames names) =>
        ItemKinds.SelectMany(k => names.Entries(k.table)).Where(e => !IsPlaceholder(e.Id)).ToList();

    /// <summary>
    /// Puts an item into the first empty item box slot. Clothes come with the skills the game
    /// gives them by default. Returns false when the box is full.
    /// </summary>
    public static bool PutItem(List<InventoryItem> items, GameNames names, string id)
    {
        var slot = items.FirstOrDefault(i => i.IsEmpty);
        if (slot == null) return false;
        var info = names.Info(id);
        slot.Clear();
        slot.Id = id;
        slot.Type = ItemKinds.FirstOrDefault(k => id.StartsWith(k.prefix, StringComparison.Ordinal)).type ?? "None";
        if (info != null && info.Skills.Length == 3 && info.Skills[0].StartsWith("ID_Skill_", StringComparison.Ordinal))
        {
            slot.Skill1 = info.Skills[0]; slot.Skill2 = info.Skills[1]; slot.Skill3 = info.Skills[2];
            slot.Flag = true;
        }
        return true;
    }

    /// <summary>DLC weapons and ninja tools (the item box kinds the tables mark as DLC).</summary>
    public static List<NameEntry> DlcItems(GameNames names) => AllItems(names).Where(e => names.IsDlc(e.Id)).ToList();

    /// <summary>Items of <paramref name="wanted"/> that the item box does not hold yet.</summary>
    public static List<NameEntry> MissingItems(List<InventoryItem> items, IEnumerable<NameEntry> wanted)
    {
        var owned = new HashSet<string>(items.Select(i => i.Id), StringComparer.Ordinal);
        return wanted.Where(e => !owned.Contains(e.Id) && !IsPlaceholder(e.Id)).ToList();
    }

    /// <summary>
    /// Changes the value of one map entry, keeping the original key object and, when the text
    /// is unchanged, the original string object so its null/UTF-16 form survives.
    /// Returns true when something changed.
    /// </summary>
    public static bool SetMapValue(GvasMap map, int index, object? value)
    {
        var e = map.Entries[index];
        if (value is string s)
        {
            if (Text(e.Value) == s) return false;
            if (s.Length == 0 && map.ValueType == "StrProperty") value = null; // the game stores empty strings as null
        }
        else if (Equals(value, e.Value)) return false;
        value ??= map.ValueType switch { "IntProperty" => 0, "BoolProperty" => false, _ => null };
        map.Entries[index] = new KeyValuePair<object?, object?>(e.Key, value);
        return true;
    }

    /// <summary>Text of a GVAS string value (string, wide string or null).</summary>
    public static string Text(object? v) => v switch
    {
        null => "",
        GvasWideString w => w.Value,
        _ => v.ToString() ?? "",
    };
}

/// <summary>
/// Reads and writes one field of a struct, creating it in the save the first time it is set
/// (native structs can be edited but are never created).
/// </summary>
public sealed class FieldSlot
{
    private readonly SaveSchema _schema;
    private readonly string? _structName;
    private readonly List<GvasProperty> _props;
    private GvasProperty? _prop;
    public readonly FieldDef Def;

    public FieldSlot(SaveSchema schema, string? structName, List<GvasProperty> props, string field)
    {
        _schema = schema;
        _structName = structName;
        _props = props;
        _prop = props.FirstOrDefault(p => p.Name == field);
        Def = schema.Fields(structName).FirstOrDefault(f => f.Name == field)
              ?? new FieldDef { Name = field, Type = _prop?.Type ?? "NameProperty", Detail = _prop?.EnumName ?? _prop?.StructName ?? "" };
    }

    public bool Stored => _prop != null;
    public object? Value => _prop?.Value;

    /// <summary>Sets the value. Returns true when the save changed.</summary>
    public bool Set(object? value)
    {
        if (value == null) return false;
        if (_prop == null)
        {
            if (Def.Type == "StructProperty") return false;
            _prop = SaveSchema.Create(Def);
            _schema.InsertInOrder(_structName, _props, _prop);
        }
        // Leave text alone unless it really changed, so its original encoding survives.
        else if (value is string s && s == EditOps.Text(_prop.Value)) return false;
        else if (Equals(value, _prop.Value)) return false;
        _prop.Value = value;
        return true;
    }
}
