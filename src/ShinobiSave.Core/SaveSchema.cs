using System.Reflection;

namespace ShinobiSave.Core;

public sealed class FieldDef
{
    public string Name = "";
    public string Type = "";
    /// <summary>Enum name for Byte/Enum properties, struct name for structs, inner type for arrays.</summary>
    public string Detail = "";

    public bool IsSimple => Type is "IntProperty" or "FloatProperty" or "BoolProperty" or "NameProperty"
        or "StrProperty" or "EnumProperty" or "ByteProperty" or "UInt32Property" or "Int64Property";
}

/// <summary>
/// Field lists per struct type, learned from real saves. UE4 leaves default-valued properties
/// out of a save, so a fresh save is missing fields that the editor still needs to offer.
/// </summary>
public sealed class SaveSchema
{
    private readonly Dictionary<string, List<FieldDef>> _structs = new(StringComparer.Ordinal);

    public static SaveSchema FromSeed()
    {
        var schema = new SaveSchema();
        var asm = typeof(SaveSchema).GetTypeInfo().Assembly;
        using var s = asm.GetManifestResourceStream("ShinobiSave.Core.schema.txt");
        if (s == null) return schema;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            // The seed file is already in game order, so append.
            if (!schema._structs.TryGetValue(parts[0], out var list)) schema._structs[parts[0]] = list = new List<FieldDef>();
            if (list.All(f => f.Name != parts[1]))
                list.Add(new FieldDef { Name = parts[1], Type = parts[2], Detail = parts.Length > 3 ? parts[3] : "" });
        }
        return schema;
    }

    public IEnumerable<string> ToLines() =>
        // Struct names sorted for a stable file; fields stay in game order within each struct.
        _structs.OrderBy(k => k.Key, StringComparer.Ordinal)
            .SelectMany(k => k.Value.Select(f => $"{k.Key}\t{f.Name}\t{f.Type}\t{f.Detail}"));

    public IReadOnlyList<FieldDef> Fields(string? structName) =>
        structName != null && _structs.TryGetValue(structName, out var list) ? list : Array.Empty<FieldDef>();

    public void Harvest(GvasFile file) => HarvestList("(root)", file.Properties);

    private void HarvestList(string structName, List<GvasProperty> props)
    {
        string? previous = null;
        foreach (var p in props)
        {
            AddField(structName, new FieldDef
            {
                Name = p.Name,
                Type = p.Type,
                Detail = p.EnumName ?? p.StructName ?? p.InnerType ?? "",
            }, previous);
            previous = p.Name;

            switch (p.Value)
            {
                case GvasStruct { Properties: not null } st:
                    HarvestList(p.StructName ?? "", st.Properties);
                    break;
                case GvasArray { InnerTag: not null } arr:
                    foreach (var it in arr.Items.OfType<GvasStruct>())
                        if (it.Properties != null) HarvestList(arr.InnerTag.StructName ?? "", it.Properties);
                    break;
            }
        }
    }

    private void AddField(string structName, FieldDef def, string? after = null)
    {
        if (structName.Length == 0) return;
        if (!_structs.TryGetValue(structName, out var list)) _structs[structName] = list = new List<FieldDef>();
        if (list.Any(f => f.Name == def.Name)) return;
        // Keep the order the game writes: place a new field right after the one that preceded it.
        int at = after == null ? -1 : list.FindIndex(f => f.Name == after);
        if (at >= 0) list.Insert(at + 1, def);
        else if (after == null && list.Count > 0) list.Insert(0, def);
        else list.Add(def);
    }

    /// <summary>Creates a property for a simple field with a neutral default value.</summary>
    public static GvasProperty Create(FieldDef def)
    {
        var p = new GvasProperty { Name = def.Name, Type = def.Type };
        switch (def.Type)
        {
            case "IntProperty": p.Value = 0; break;
            case "UInt32Property": p.Value = 0u; break;
            case "Int64Property": p.Value = 0L; break;
            case "FloatProperty": p.Value = 0f; break;
            case "BoolProperty": p.Value = false; break;
            case "NameProperty": p.Value = "None"; break;
            case "StrProperty": p.Value = ""; break;
            case "EnumProperty": p.EnumName = def.Detail; p.Value = ""; break;
            case "ByteProperty":
                p.EnumName = def.Detail.Length == 0 ? "None" : def.Detail;
                p.Value = p.EnumName == "None" ? (byte)0 : "";
                break;
            default: throw new GvasFormatException($"Cannot create a {def.Type} field");
        }
        return p;
    }

    /// <summary>Inserts <paramref name="prop"/> into <paramref name="props"/> at its schema position.</summary>
    public void InsertInOrder(string? structName, List<GvasProperty> props, GvasProperty prop)
    {
        var order = Fields(structName);
        int rank = IndexOf(order, prop.Name);
        int at = props.Count;
        if (rank >= 0)
        {
            for (int i = 0; i < props.Count; i++)
            {
                int r = IndexOf(order, props[i].Name);
                if (r > rank) { at = i; break; }
            }
        }
        props.Insert(at, prop);
    }

    private static int IndexOf(IReadOnlyList<FieldDef> list, string name)
    {
        for (int i = 0; i < list.Count; i++) if (list[i].Name == name) return i;
        return -1;
    }
}
