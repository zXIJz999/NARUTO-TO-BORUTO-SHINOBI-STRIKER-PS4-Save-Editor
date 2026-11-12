using System.Reflection;

namespace ShinobiSave.Core;

/// <summary>
/// Known game IDs grouped by prefix (ID_NJT, ID_Skill, ID_CustomJacket, ...), used to fill
/// drop-downs. Built from an embedded seed list plus whatever the loaded save mentions.
/// </summary>
public sealed class IdCatalog
{
    private static readonly string[] FlagPrefixes =
        { "ID_Flag_Acquisition_", "ID_Flag_Open_", "ID_Flag_Check_" };

    private readonly SortedSet<string> _ids = new(StringComparer.Ordinal);

    public int Count => _ids.Count;

    public static IdCatalog FromSeed()
    {
        var c = new IdCatalog();
        var asm = typeof(IdCatalog).GetTypeInfo().Assembly;
        using var s = asm.GetManifestResourceStream("ShinobiSave.Core.ids.txt");
        if (s == null) return c;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
            if (line.Length > 0) c._ids.Add(line);
        return c;
    }

    private readonly Dictionary<string, SortedSet<string>> _enums = new(StringComparer.Ordinal);

    /// <summary>Values seen for an enum type, in full "Enum::Value" form.</summary>
    public List<string> EnumValues(string? enumName) =>
        enumName != null && _enums.TryGetValue(enumName, out var set) ? set.ToList() : new List<string>();

    public IEnumerable<string> AllEnumValues => _enums.Values.SelectMany(v => v);

    public void Add(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        int sep = id.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0)
        {
            string name = id[..sep];
            if (!_enums.TryGetValue(name, out var set)) _enums[name] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(id);
            return;
        }
        if (!id.StartsWith("ID_", StringComparison.Ordinal)) return;
        _ids.Add(id);
        // Unlock flags name the thing they unlock: ID_Flag_Acquisition_CustomJacket_M_16 -> ID_CustomJacket_M_16.
        foreach (var prefix in FlagPrefixes)
        {
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rest = id[prefix.Length..];
            if (rest.StartsWith("Custom", StringComparison.Ordinal) || rest.StartsWith("Weapon_", StringComparison.Ordinal)
                || rest.StartsWith("NJT_", StringComparison.Ordinal) || rest.StartsWith("Nickname_", StringComparison.Ordinal)
                || rest.StartsWith("Master_", StringComparison.Ordinal) || rest.StartsWith("Role_", StringComparison.Ordinal))
                _ids.Add("ID_" + rest);
        }
    }

    /// <summary>Adds every ID-looking string found in the save.</summary>
    public void Harvest(ShinobiSaveFile save)
    {
        Walk(save.Gvas.Properties);
        if (save.Items == null) return;
        foreach (var it in save.Items)
        {
            Add(it.Id); Add(it.Skill1); Add(it.Skill2); Add(it.Skill3);
            Add(InventoryItem.TypePrefix + it.Type);
        }
    }

    private void Walk(List<GvasProperty> props)
    {
        foreach (var p in props) WalkValue(p.Value);
    }

    private void WalkValue(object? v)
    {
        switch (v)
        {
            case string s: Add(s); break;
            case GvasWideString w: Add(w.Value); break;
            case GvasStruct st when st.Properties != null: Walk(st.Properties); break;
            case GvasArray a: foreach (var it in a.Items) WalkValue(it); break;
            case GvasSet set: foreach (var it in set.Items) WalkValue(it); break;
            case GvasMap m:
                foreach (var e in m.Entries) { WalkValue(e.Key); WalkValue(e.Value); }
                break;
        }
    }

    public IEnumerable<string> All => _ids;

    /// <summary>IDs that start with any of the given prefixes, sorted.</summary>
    public List<string> WithPrefix(params string[] prefixes) =>
        _ids.Where(id => prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal))).ToList();

    /// <summary>
    /// Suggestions for a field holding <paramref name="current"/>: every known ID that shares
    /// its "ID_Word" family (ID_CustomJacket_F_19_1 -> ID_CustomJacket_*).
    /// </summary>
    public List<string> Like(string? current)
    {
        string? family = Family(current);
        return family == null ? new List<string>() : WithPrefix(family);
    }

    public static string? Family(string? id)
    {
        if (id == null || !id.StartsWith("ID_", StringComparison.Ordinal)) return null;
        int end = id.IndexOf('_', 3);
        return end < 0 ? id : id[..(end + 1)];
    }
}
