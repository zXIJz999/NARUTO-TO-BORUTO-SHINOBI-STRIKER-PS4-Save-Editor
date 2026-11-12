using System.Text;

namespace ShinobiSave.Core;

public sealed class GvasFormatException : Exception
{
    public GvasFormatException(string message) : base(message) { }
}

public sealed class GvasReader
{
    /// <summary>Native structs that are stored as fixed binary, not as a property list.</summary>
    public static readonly Dictionary<string, int> NativeStructSizes = new(StringComparer.Ordinal)
    {
        ["Vector"] = 12, ["Vector2D"] = 8, ["Vector4"] = 16, ["Rotator"] = 12, ["Quat"] = 16,
        ["LinearColor"] = 16, ["Color"] = 4, ["Guid"] = 16, ["DateTime"] = 8, ["Timespan"] = 8,
        ["IntPoint"] = 8, ["IntVector"] = 12,
        // Shinobi Striker's own 64-bit wrappers (a single little-endian int64).
        ["NN_Int64"] = 8, ["NN_ServerCharacterID"] = 8,
    };

    private readonly byte[] _d;
    private int _p;

    public GvasReader(byte[] data) { _d = data; }

    // ---- primitives -------------------------------------------------------

    private void Need(int n)
    {
        if (n < 0 || _p + n > _d.Length) throw new GvasFormatException($"Read of {n} bytes past end at 0x{_p:X}");
    }

    private byte U8() { Need(1); return _d[_p++]; }
    private short I16() { Need(2); var v = BitConverter.ToInt16(_d, _p); _p += 2; return v; }
    private ushort U16() { Need(2); var v = BitConverter.ToUInt16(_d, _p); _p += 2; return v; }
    private int I32() { Need(4); var v = BitConverter.ToInt32(_d, _p); _p += 4; return v; }
    private uint U32() { Need(4); var v = BitConverter.ToUInt32(_d, _p); _p += 4; return v; }
    private long I64() { Need(8); var v = BitConverter.ToInt64(_d, _p); _p += 8; return v; }
    private ulong U64() { Need(8); var v = BitConverter.ToUInt64(_d, _p); _p += 8; return v; }
    private float F32() { Need(4); var v = BitConverter.ToSingle(_d, _p); _p += 4; return v; }
    private double F64() { Need(8); var v = BitConverter.ToDouble(_d, _p); _p += 8; return v; }
    private byte[] Bytes(int n) { Need(n); var b = new byte[n]; Buffer.BlockCopy(_d, _p, b, 0, n); _p += n; return b; }

    /// <summary>
    /// Reads an FString. Returns null for length 0, a string for ANSI, and a
    /// <see cref="GvasWideString"/> only when a UTF-16 string is pure ASCII (so the
    /// writer can reproduce the original encoding).
    /// </summary>
    private object? FStringObj()
    {
        int len = I32();
        if (len == 0) return null;
        if (len > 0)
        {
            if (len > 0x100000) throw new GvasFormatException($"FString length {len} at 0x{_p - 4:X}");
            Need(len);
            var s = Latin1.GetString(_d, _p, len - 1);
            _p += len;
            return s;
        }
        int chars = -len;
        if (chars > 0x100000) throw new GvasFormatException($"FString length {len} at 0x{_p - 4:X}");
        Need(chars * 2);
        var w = Encoding.Unicode.GetString(_d, _p, (chars - 1) * 2);
        _p += chars * 2;
        return IsAscii(w) ? new GvasWideString { Value = w } : w;
    }

    private string? FString() => FStringObj()?.ToString();

    private string FStringReq() => FString() ?? "";

    internal static readonly Encoding Latin1 = Encoding.Latin1;

    internal static bool IsAscii(string s)
    {
        foreach (char c in s) if (c > 0x7F) return false;
        return true;
    }

    // ---- file -------------------------------------------------------------

    public GvasFile ReadFile()
    {
        if (_d.Length < 4 || _d[0] != (byte)'G' || _d[1] != (byte)'V' || _d[2] != (byte)'A' || _d[3] != (byte)'S')
            throw new GvasFormatException("Not a GVAS file (missing GVAS magic).");
        _p = 4;
        var f = new GvasFile
        {
            SaveGameVersion = I32(),
            PackageVersion = I32(),
            EngineMajor = U16(),
            EngineMinor = U16(),
            EnginePatch = U16(),
            EngineBuild = U32(),
        };
        f.EngineBranch = FString();
        f.CustomFormatVersion = I32();
        int n = I32();
        if (n < 0 || n > 4096) throw new GvasFormatException($"Custom version count {n}");
        for (int i = 0; i < n; i++)
            f.CustomVersions.Add(new GvasCustomVersion { Key = Bytes(16), Version = I32() });
        f.SaveGameClass = FString();
        f.Properties = ReadPropertyList();
        f.Trailer = Bytes(_d.Length - _p);
        return f;
    }

    private List<GvasProperty> ReadPropertyList()
    {
        var list = new List<GvasProperty>();
        while (true)
        {
            var p = ReadProperty();
            if (p == null) break;
            list.Add(p);
        }
        return list;
    }

    /// <summary>Reads one tagged property, or returns null at the "None" terminator.</summary>
    private GvasProperty? ReadProperty()
    {
        int start = _p;
        string name = FStringReq();
        if (name == "None") return null;
        if (name.Length == 0) throw new GvasFormatException($"Empty property name at 0x{start:X}");

        var p = new GvasProperty { Name = name, Offset = start };
        p.Type = FStringReq();
        int size = I32();
        p.ArrayIndex = I32();
        p.SizeOnDisk = size;

        bool boolVal = false;
        switch (p.Type)
        {
            case "StructProperty": p.StructName = FStringReq(); p.StructGuid = Bytes(16); break;
            case "BoolProperty": boolVal = U8() != 0; break;
            case "ByteProperty":
            case "EnumProperty": p.EnumName = FStringReq(); break;
            case "ArrayProperty":
            case "SetProperty": p.InnerType = FStringReq(); break;
            case "MapProperty": p.KeyType = FStringReq(); p.ValueType = FStringReq(); break;
        }
        if (U8() != 0) p.PropertyGuid = Bytes(16);

        if (p.Type == "BoolProperty")
        {
            p.Value = boolVal;
            if (size != 0) throw new GvasFormatException($"BoolProperty {name} has size {size}");
            return p;
        }

        int payload = _p;
        Need(size);
        try
        {
            p.Value = ReadPayload(p, size);
            if (_p != payload + size)
                throw new GvasFormatException($"{p.Type} {name}: consumed {_p - payload} of {size} bytes");
        }
        catch (Exception ex) when (ex is GvasFormatException or ArgumentException or IndexOutOfRangeException)
        {
            // Keep what we do not understand byte-for-byte so the file still round-trips.
            _p = payload;
            p.Value = new GvasRaw { Data = Bytes(size), Reason = ex.Message };
        }
        return p;
    }

    private object? ReadPayload(GvasProperty p, int size)
    {
        switch (p.Type)
        {
            case "StructProperty":
                return ReadStructBody(p.StructName!, size);
            case "ArrayProperty":
                return ReadArray(p.InnerType!, size);
            case "MapProperty":
                return ReadMap(p.KeyType!, p.ValueType!, size);
            case "SetProperty":
            {
                var s = new GvasSet { InnerType = p.InnerType!, RemovedCount = I32() };
                if (s.RemovedCount != 0) throw new GvasFormatException("Set with removed elements");
                int count = I32();
                for (int i = 0; i < count; i++) s.Items.Add(ReadElement(p.InnerType!, -1));
                return s;
            }
            case "ByteProperty":
                if (p.EnumName is null or "None") return U8();
                return FStringObj();
            case "TextProperty":
                return new GvasRaw { Data = Bytes(size), Reason = "FText" };
            default:
                return ReadElement(p.Type, size);
        }
    }

    /// <summary>Reads an untagged value of the given property type.</summary>
    private object? ReadElement(string type, int sizeHint)
    {
        switch (type)
        {
            case "IntProperty": return I32();
            case "UInt32Property": return U32();
            case "Int64Property": return I64();
            case "UInt64Property": return U64();
            case "Int16Property": return I16();
            case "UInt16Property": return U16();
            case "Int8Property": return (sbyte)U8();
            case "ByteProperty": return U8();
            case "FloatProperty": return F32();
            case "DoubleProperty": return F64();
            case "BoolProperty": return U8() != 0;
            case "StrProperty":
            case "NameProperty":
            case "EnumProperty":
            case "ObjectProperty":
            case "SoftObjectProperty":
            case "AssetObjectProperty":
                return FStringObj();
            case "StructProperty":
                return ReadStructBody("", sizeHint);
            default:
                throw new GvasFormatException($"Unsupported property type '{type}' at 0x{_p:X}");
        }
    }

    private GvasStruct ReadStructBody(string structName, int size)
    {
        var s = new GvasStruct { StructName = structName };
        if (NativeStructSizes.TryGetValue(structName, out int native))
        {
            if (size >= 0 && size != native)
                throw new GvasFormatException($"Native struct {structName} has size {size}, expected {native}");
            s.Native = Bytes(native);
            return s;
        }
        s.Properties = ReadPropertyList();
        return s;
    }

    private GvasArray ReadArray(string inner, int size)
    {
        int start = _p;
        var a = new GvasArray { InnerType = inner };
        int count = I32();
        if (count < 0) throw new GvasFormatException($"Array count {count}");

        if (inner == "StructProperty")
        {
            var tag = new GvasProperty { Offset = _p };
            tag.Name = FStringReq();
            tag.Type = FStringReq();
            if (tag.Type != "StructProperty") throw new GvasFormatException($"Struct array inner tag type '{tag.Type}'");
            int innerSize = I32();
            tag.ArrayIndex = I32();
            tag.SizeOnDisk = innerSize;
            tag.StructName = FStringReq();
            tag.StructGuid = Bytes(16);
            if (U8() != 0) tag.PropertyGuid = Bytes(16);
            a.InnerTag = tag;
            int body = _p;
            for (int i = 0; i < count; i++) a.Items.Add(ReadStructBody(tag.StructName, -1));
            if (_p != body + innerSize)
                throw new GvasFormatException($"Struct array body consumed {_p - body} of {innerSize} bytes");
            return a;
        }

        if (inner == "ByteProperty")
        {
            int remaining = size - (_p - start);
            if (remaining == count) { a.Bytes = Bytes(count); return a; }
            for (int i = 0; i < count; i++) a.Items.Add(FStringObj());
            return a;
        }

        a.Items.Capacity = Math.Min(count, 1 << 20);
        for (int i = 0; i < count; i++) a.Items.Add(ReadElement(inner, -1));
        return a;
    }

    private GvasMap ReadMap(string keyType, string valueType, int size)
    {
        var m = new GvasMap { KeyType = keyType, ValueType = valueType, RemovedCount = I32() };
        if (m.RemovedCount != 0) throw new GvasFormatException("Map with removed keys");
        int count = I32();
        if (count < 0) throw new GvasFormatException($"Map count {count}");
        for (int i = 0; i < count; i++)
        {
            var k = ReadElement(keyType, -1);
            var v = ReadElement(valueType, -1);
            m.Entries.Add(new KeyValuePair<object?, object?>(k, v));
        }
        return m;
    }
}
