using System.Text;

namespace ShinobiSave.Dump;

/// <summary>
/// Reads a cooked UE 4.16 DataTable (.uasset + .uexp) into rows of flattened "field = value"
/// pairs. Only as much of the format as the game's tables need.
/// </summary>
public sealed class UAssetTable
{
    public List<(string row, List<KeyValuePair<string, string>> fields)> Rows = new();
    public string[] Names = Array.Empty<string>();

    private byte[] _d = Array.Empty<byte>();
    private int _p;

    public static UAssetTable Load(string uassetPath)
    {
        var t = new UAssetTable();
        byte[] head = File.ReadAllBytes(uassetPath);
        t.ReadNames(head);
        string uexp = Path.ChangeExtension(uassetPath, ".uexp");
        t._d = File.Exists(uexp) ? File.ReadAllBytes(uexp) : head;
        t._p = File.Exists(uexp) ? 0 : t._headerSize;
        t.ReadTable();
        return t;
    }

    private int _headerSize;

    private void ReadNames(byte[] h)
    {
        int p = 0;
        if (BitConverter.ToUInt32(h, 0) != 0x9E2A83C1) throw new Exception("not a uasset");
        p = 4;
        int legacy = BitConverter.ToInt32(h, p); p += 4;
        if (legacy != -4) p += 4;                 // legacy UE3 version
        p += 4 + 4;                               // UE4 version, licensee version
        int customCount = BitConverter.ToInt32(h, p); p += 4;
        p += customCount * 20;
        _headerSize = BitConverter.ToInt32(h, p); p += 4;
        int folderLen = BitConverter.ToInt32(h, p); p += 4;
        p += folderLen > 0 ? folderLen : -folderLen * 2;
        p += 4;                                   // package flags
        int nameCount = BitConverter.ToInt32(h, p); p += 4;
        int nameOffset = BitConverter.ToInt32(h, p);
        Names = new string[nameCount];
        p = nameOffset;
        for (int i = 0; i < nameCount; i++)
        {
            Names[i] = Str(h, ref p) ?? "";
            p += 4;                               // case-preserving + non-case hashes
        }
    }

    private void ReadTable()
    {
        // The DataTable object's own properties (RowStruct etc.), then the row map.
        SkipProps();
        _p += 4;
        int rows = I32();
        for (int i = 0; i < rows; i++)
        {
            // Standard tables write the row name first. The game's spreadsheet-imported tables do
            // not: the row starts straight at its first property (whose type name follows).
            int mark = _p;
            string row = FName();
            int typeIndex = BitConverter.ToInt32(_d, _p);
            bool nameless = typeIndex >= 0 && typeIndex < Names.Length && Names[typeIndex].EndsWith("Property", StringComparison.Ordinal);
            if (nameless) _p = mark;
            var fields = new List<KeyValuePair<string, string>>();
            ReadProps("", fields);
            if (nameless) row = fields.FirstOrDefault(f => f.Key == "IDName").Value ?? (fields.Count > 0 ? fields[0].Value : "");
            Rows.Add((row, fields));
        }
    }

    private void SkipProps() => ReadProps("", new List<KeyValuePair<string, string>>());

    private void ReadProps(string prefix, List<KeyValuePair<string, string>> o)
    {
        while (true)
        {
            string name = FName();
            if (name == "None") return;
            string type = FName();
            int size = I32();
            int arrayIndex = I32();
            string structName = "", inner = "", enumName = "";
            bool boolVal = false;
            switch (type)
            {
                case "StructProperty": structName = FName(); _p += 16; break;
                case "BoolProperty": boolVal = _d[_p++] != 0; break;
                case "ByteProperty": case "EnumProperty": enumName = FName(); break;
                case "ArrayProperty": case "SetProperty": inner = FName(); break;
                case "MapProperty": FName(); FName(); break;
            }
            if (_d[_p++] != 0) _p += 16;
            int end = _p + size;
            string key = prefix + name + (arrayIndex != 0 ? $"[{arrayIndex}]" : "");
            try
            {
                switch (type)
                {
                    case "BoolProperty": o.Add(new(key, boolVal ? "True" : "False")); break;
                    case "StructProperty": ReadStruct(key, structName, size, o); break;
                    case "ArrayProperty": ReadArray(key, inner, size, o); break;
                    case "ByteProperty":
                        o.Add(new(key, size == 1 ? _d[_p].ToString() : FName())); break;
                    default:
                        var v = ReadValue(type);
                        if (v != null) o.Add(new(key, v));
                        break;
                }
            }
            catch (Exception)
            {
                o.RemoveAll(f => f.Key.StartsWith(key + ".", StringComparison.Ordinal));
                o.Add(new(key, "<unparsed>"));
            }
            _p = end;
        }
    }

    private void ReadStruct(string key, string structName, int size, List<KeyValuePair<string, string>> o)
    {
        switch (structName)
        {
            case "Vector": case "Rotator":
                o.Add(new(key, $"{F32()},{F32()},{F32()}")); return;
            case "Vector2D": o.Add(new(key, $"{F32()},{F32()}")); return;
            case "LinearColor": case "Quat": o.Add(new(key, $"{F32()},{F32()},{F32()},{F32()}")); return;
            case "Color": o.Add(new(key, Convert.ToHexString(_d, _p, 4))); return;
            case "Guid": case "DateTime": case "Timespan": case "IntPoint": case "SoftObjectPath": case "StringAssetReference":
                if (structName == "StringAssetReference") { o.Add(new(key, Str(_d, ref _p) ?? "")); return; }
                o.Add(new(key, Convert.ToHexString(_d, _p, Math.Min(size, 16)))); return;
        }
        // The game's text reference: two FNames, text table and key within it.
        if (structName == "LocalizedText" && size == 16) { o.Add(new(key, $"{FName()}|{FName()}")); return; }
        ReadProps(key + ".", o);
    }

    private void ReadArray(string key, string inner, int size, List<KeyValuePair<string, string>> o)
    {
        int count = I32();
        if (inner == "StructProperty")
        {
            FName(); FName(); I32(); I32();
            string structName = FName(); _p += 16; _p++;
            for (int i = 0; i < count; i++) ReadStruct($"{key}[{i}]", structName, -1, o);
            return;
        }
        if (inner == "ByteProperty" && size - 4 == count) { o.Add(new(key, $"<{count} bytes>")); return; }
        for (int i = 0; i < count; i++)
        {
            var v = ReadValue(inner);
            if (v == null) return;
            o.Add(new($"{key}[{i}]", v));
        }
    }

    private string? ReadValue(string type)
    {
        switch (type)
        {
            case "IntProperty": return I32().ToString();
            case "UInt32Property": return ((uint)I32()).ToString();
            case "Int64Property": { long v = BitConverter.ToInt64(_d, _p); _p += 8; return v.ToString(); }
            case "FloatProperty": return F32().ToString();
            case "NameProperty": case "EnumProperty": case "ByteProperty": return FName();
            case "StrProperty": case "AssetObjectProperty": case "SoftObjectProperty": return Str(_d, ref _p) ?? "";
            case "ObjectProperty": return "obj:" + I32();
            case "TextProperty": return Text();
            default: return null;
        }
    }

    private string Text()
    {
        int flags = I32();
        sbyte history = (sbyte)_d[_p++];
        if (history == -1)
        {
            // No history: optionally a culture-invariant string.
            if (I32() != 0) return "T||" + (Str(_d, ref _p) ?? "");
            return "T||";
        }
        if (history == 0)
        {
            string ns = Str(_d, ref _p) ?? "", k = Str(_d, ref _p) ?? "", src = Str(_d, ref _p) ?? "";
            return $"T|{ns}:{k}|{src}";
        }
        return $"T|history{history}|";
    }

    private int I32() { int v = BitConverter.ToInt32(_d, _p); _p += 4; return v; }
    private float F32() { float v = BitConverter.ToSingle(_d, _p); _p += 4; return v; }

    private string FName()
    {
        int index = I32(), number = I32();
        string s = index >= 0 && index < Names.Length ? Names[index] : $"<name{index}>";
        return number > 0 ? $"{s}_{number - 1}" : s;
    }

    private static string? Str(byte[] d, ref int p)
    {
        int len = BitConverter.ToInt32(d, p); p += 4;
        if (len == 0) return null;
        if (len > 0) { var s = Encoding.UTF8.GetString(d, p, len - 1); p += len; return s; }
        var w = Encoding.Unicode.GetString(d, p, (-len - 1) * 2); p += -len * 2; return w;
    }

    public IEnumerable<string> ToLines()
    {
        foreach (var (row, fields) in Rows)
            yield return row + "\t" + string.Join("\t", fields.Select(f => $"{f.Key}={f.Value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')}"));
    }
}
