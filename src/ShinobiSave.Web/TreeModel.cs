using System.Globalization;
using ShinobiSave.Core;

namespace ShinobiSave.Web;

/// <summary>
/// The whole save as a tree with editable scalar values: the same rules as the desktop
/// editor's Advanced tree (TreePage), without the WinForms tree control.
/// </summary>
public sealed class TreeNode
{
    public const int MaxChildren = 5000;

    public string Name = "";
    public string Type = "";
    public Func<string> Display = () => "";
    public Func<string, bool>? Set;
    public Func<List<TreeNode>>? Children;

    private List<TreeNode>? _children;
    public bool Expanded;
    public List<TreeNode> Kids => _children ??= Children?.Invoke() ?? new List<TreeNode>();

    public static List<TreeNode> Roots(GvasFile file) => file.Properties.Select(ForProperty).ToList();

    public static TreeNode ForProperty(GvasProperty p)
    {
        string type = p.Type switch
        {
            "StructProperty" => $"Struct<{p.StructName}>",
            "ArrayProperty" => $"Array<{(p.Value as GvasArray)?.InnerTag?.StructName ?? p.InnerType}>",
            "MapProperty" => $"Map<{p.KeyType},{p.ValueType}>",
            "SetProperty" => $"Set<{p.InnerType}>",
            "EnumProperty" or "ByteProperty" when p.EnumName is not (null or "None") => $"{p.Type}<{p.EnumName}>",
            _ => p.Type,
        };
        string name = p.ArrayIndex != 0 ? $"{p.Name}[{p.ArrayIndex}]" : p.Name;
        string elementType = p.Type == "ByteProperty" && p.EnumName is not (null or "None") ? "EnumProperty" : p.Type;
        return ForValue(name, type, elementType, () => p.Value, v => p.Value = v);
    }

    private static TreeNode ForValue(string name, string typeLabel, string elementType, Func<object?> get, Action<object?> set)
    {
        var node = new TreeNode { Name = name, Type = typeLabel };
        object? value = get();
        switch (value)
        {
            case GvasStruct { Properties: not null } st:
                node.Display = () => $"{st.Properties.Count} fields";
                node.Children = () => st.Properties.Select(ForProperty).ToList();
                break;
            case GvasStruct { Native: not null } st:
                node.Display = () => NativeText(st);
                node.Set = text => SetNative(st, text);
                break;
            case GvasArray { Bytes: not null } a:
                node.Display = () => $"{a.Bytes.Length} bytes" + (UeCompressed.LooksCompressed(a.Bytes) ? " (compressed item box, see the Items page)" : "");
                break;
            case GvasArray a:
                node.Display = () => $"{a.Items.Count} items";
                node.Children = () => a.Items.Take(MaxChildren).Select((_, i) => ForValue($"[{i}]", a.InnerTag != null ? $"Struct<{a.InnerTag.StructName}>" : a.InnerType,
                    a.InnerType, () => a.Items[i], v => a.Items[i] = v)).ToList();
                break;
            case GvasSet s:
                node.Display = () => $"{s.Items.Count} items";
                node.Children = () => s.Items.Take(MaxChildren).Select((_, i) => ForValue($"[{i}]", s.InnerType, s.InnerType, () => s.Items[i], v => s.Items[i] = v)).ToList();
                break;
            case GvasMap m:
                node.Display = () => $"{m.Entries.Count} entries";
                node.Children = () => m.Entries.Take(MaxChildren).Select((e, i) => ForValue(EditOps.Text(e.Key), m.ValueType, m.ValueType,
                    () => m.Entries[i].Value, v => m.Entries[i] = new KeyValuePair<object?, object?>(m.Entries[i].Key, v))).ToList();
                break;
            case GvasRaw raw:
                node.Display = () => $"{raw.Data.Length} raw bytes: {Convert.ToHexString(raw.Data, 0, Math.Min(24, raw.Data.Length))}";
                break;
            default:
                node.Display = () => Scalar(get());
                node.Set = text =>
                {
                    if (text == Scalar(get())) return false;
                    if (!TryParse(elementType, text, out var parsed)) return false;
                    set(parsed);
                    return true;
                };
                break;
        }
        return node;
    }

    private static string Scalar(object? v) => v switch
    {
        null => "",
        bool b => b ? "True" : "False",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => EditOps.Text(v),
    };

    private static bool TryParse(string type, string text, out object? value)
    {
        var inv = CultureInfo.InvariantCulture;
        value = null;
        switch (type)
        {
            case "IntProperty": if (int.TryParse(text, NumberStyles.Integer, inv, out var i)) { value = i; return true; } return false;
            case "UInt32Property": if (uint.TryParse(text, NumberStyles.Integer, inv, out var u)) { value = u; return true; } return false;
            case "Int64Property": if (long.TryParse(text, NumberStyles.Integer, inv, out var l)) { value = l; return true; } return false;
            case "UInt64Property": if (ulong.TryParse(text, NumberStyles.Integer, inv, out var ul)) { value = ul; return true; } return false;
            case "Int16Property": if (short.TryParse(text, NumberStyles.Integer, inv, out var s)) { value = s; return true; } return false;
            case "UInt16Property": if (ushort.TryParse(text, NumberStyles.Integer, inv, out var us)) { value = us; return true; } return false;
            case "Int8Property": if (sbyte.TryParse(text, NumberStyles.Integer, inv, out var sb)) { value = sb; return true; } return false;
            case "ByteProperty": if (byte.TryParse(text, NumberStyles.Integer, inv, out var b)) { value = b; return true; } return false;
            case "FloatProperty": if (float.TryParse(text, NumberStyles.Float, inv, out var f)) { value = f; return true; } return false;
            case "DoubleProperty": if (double.TryParse(text, NumberStyles.Float, inv, out var d)) { value = d; return true; } return false;
            case "BoolProperty":
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1") { value = true; return true; }
                if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0") { value = false; return true; }
                return false;
            case "StrProperty":
            case "NameProperty":
            case "EnumProperty":
            case "ObjectProperty":
            case "SoftObjectProperty":
            case "AssetObjectProperty":
                value = text; return true;
            default: return false;
        }
    }

    private static string NativeText(GvasStruct st)
    {
        var n = st.Native!;
        var inv = CultureInfo.InvariantCulture;
        string Floats(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => BitConverter.ToSingle(n, i * 4).ToString("R", inv)));
        return st.StructName switch
        {
            "Vector" or "Rotator" => Floats(3),
            "Vector2D" => Floats(2),
            "Quat" or "LinearColor" or "Vector4" => Floats(4),
            "IntPoint" => $"{BitConverter.ToInt32(n, 0)}, {BitConverter.ToInt32(n, 4)}",
            "IntVector" => $"{BitConverter.ToInt32(n, 0)}, {BitConverter.ToInt32(n, 4)}, {BitConverter.ToInt32(n, 8)}",
            "DateTime" or "Timespan" or "NN_Int64" or "NN_ServerCharacterID" => BitConverter.ToInt64(n, 0).ToString(inv),
            _ => Convert.ToHexString(n),
        };
    }

    private static bool SetNative(GvasStruct st, string text)
    {
        if (text == NativeText(st)) return false;
        var inv = CultureInfo.InvariantCulture;
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var n = st.Native!;
        byte[] result = new byte[n.Length];
        switch (st.StructName)
        {
            case "Vector" or "Rotator" or "Vector2D" or "Quat" or "LinearColor" or "Vector4":
                if (parts.Length != n.Length / 4) return false;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!float.TryParse(parts[i], NumberStyles.Float, inv, out var f)) return false;
                    BitConverter.TryWriteBytes(result.AsSpan(i * 4), f);
                }
                break;
            case "IntPoint" or "IntVector":
                if (parts.Length != n.Length / 4) return false;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!int.TryParse(parts[i], NumberStyles.Integer, inv, out var v)) return false;
                    BitConverter.TryWriteBytes(result.AsSpan(i * 4), v);
                }
                break;
            case "DateTime" or "Timespan" or "NN_Int64" or "NN_ServerCharacterID":
                if (!long.TryParse(text, NumberStyles.Integer, inv, out var l)) return false;
                BitConverter.TryWriteBytes(result, l);
                break;
            default:
                try
                {
                    var bytes = Convert.FromHexString(text);
                    if (bytes.Length != n.Length) return false;
                    result = bytes;
                }
                catch (FormatException) { return false; }
                break;
        }
        st.Native = result;
        return true;
    }
}
