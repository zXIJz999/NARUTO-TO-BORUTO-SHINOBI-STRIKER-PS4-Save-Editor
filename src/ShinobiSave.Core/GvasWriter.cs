using System.Text;

namespace ShinobiSave.Core;

public static class GvasWriter
{
    public static byte[] Write(GvasFile f)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(new[] { (byte)'G', (byte)'V', (byte)'A', (byte)'S' });
        w.Write(f.SaveGameVersion);
        w.Write(f.PackageVersion);
        w.Write(f.EngineMajor);
        w.Write(f.EngineMinor);
        w.Write(f.EnginePatch);
        w.Write(f.EngineBuild);
        WriteFString(w, f.EngineBranch);
        w.Write(f.CustomFormatVersion);
        w.Write(f.CustomVersions.Count);
        foreach (var cv in f.CustomVersions) { w.Write(cv.Key); w.Write(cv.Version); }
        WriteFString(w, f.SaveGameClass);
        WritePropertyList(w, f.Properties);
        w.Write(f.Trailer);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// null writes length 0; <see cref="GvasWideString"/> and any non-ASCII string write
    /// UTF-16 (negative length); everything else writes ANSI with a terminator.
    /// </summary>
    public static void WriteFString(BinaryWriter w, object? value)
    {
        if (value is null) { w.Write(0); return; }
        string s = value.ToString() ?? "";
        if (value is GvasWideString || !GvasReader.IsAscii(s))
        {
            w.Write(-(s.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(s));
            w.Write((ushort)0);
            return;
        }
        w.Write(s.Length + 1);
        w.Write(GvasReader.Latin1.GetBytes(s));
        w.Write((byte)0);
    }

    private static void WritePropertyList(BinaryWriter w, List<GvasProperty> props)
    {
        foreach (var p in props) WriteProperty(w, p);
        WriteFString(w, "None");
    }

    private static void WriteProperty(BinaryWriter w, GvasProperty p)
    {
        byte[] payload;
        if (p.Type == "BoolProperty") payload = Array.Empty<byte>();
        else
        {
            using var ms = new MemoryStream();
            using var pw = new BinaryWriter(ms);
            WritePayload(pw, p);
            pw.Flush();
            payload = ms.ToArray();
        }

        WriteFString(w, p.Name);
        WriteFString(w, p.Type);
        w.Write(payload.Length);
        w.Write(p.ArrayIndex);
        switch (p.Type)
        {
            case "StructProperty": WriteFString(w, p.StructName); w.Write(p.StructGuid ?? new byte[16]); break;
            case "BoolProperty": w.Write((byte)(p.Value is true ? 1 : 0)); break;
            case "ByteProperty":
            case "EnumProperty": WriteFString(w, p.EnumName); break;
            case "ArrayProperty":
            case "SetProperty": WriteFString(w, p.InnerType); break;
            case "MapProperty": WriteFString(w, p.KeyType); WriteFString(w, p.ValueType); break;
        }
        if (p.PropertyGuid != null) { w.Write((byte)1); w.Write(p.PropertyGuid); }
        else w.Write((byte)0);
        w.Write(payload);
    }

    private static void WritePayload(BinaryWriter w, GvasProperty p)
    {
        switch (p.Value)
        {
            case GvasRaw raw: w.Write(raw.Data); return;
            case GvasStruct s: WriteStructBody(w, s); return;
            case GvasArray a: WriteArray(w, a); return;
            case GvasMap m:
                w.Write(m.RemovedCount);
                w.Write(m.Entries.Count);
                foreach (var e in m.Entries) { WriteElement(w, m.KeyType, e.Key); WriteElement(w, m.ValueType, e.Value); }
                return;
            case GvasSet set:
                w.Write(set.RemovedCount);
                w.Write(set.Items.Count);
                foreach (var e in set.Items) WriteElement(w, set.InnerType, e);
                return;
        }
        if (p.Type == "ByteProperty" && p.EnumName is not (null or "None")) { WriteFString(w, p.Value); return; }
        WriteElement(w, p.Type, p.Value);
    }

    private static void WriteElement(BinaryWriter w, string type, object? v)
    {
        switch (type)
        {
            case "IntProperty": w.Write(Convert.ToInt32(v)); break;
            case "UInt32Property": w.Write(Convert.ToUInt32(v)); break;
            case "Int64Property": w.Write(Convert.ToInt64(v)); break;
            case "UInt64Property": w.Write(Convert.ToUInt64(v)); break;
            case "Int16Property": w.Write(Convert.ToInt16(v)); break;
            case "UInt16Property": w.Write(Convert.ToUInt16(v)); break;
            case "Int8Property": w.Write(Convert.ToSByte(v)); break;
            case "ByteProperty": w.Write(Convert.ToByte(v)); break;
            case "FloatProperty": w.Write(Convert.ToSingle(v)); break;
            case "DoubleProperty": w.Write(Convert.ToDouble(v)); break;
            case "BoolProperty": w.Write((byte)(v is true ? 1 : 0)); break;
            case "StrProperty":
            case "NameProperty":
            case "EnumProperty":
            case "ObjectProperty":
            case "SoftObjectProperty":
            case "AssetObjectProperty":
                WriteFString(w, v); break;
            case "StructProperty": WriteStructBody(w, (GvasStruct)v!); break;
            default: throw new GvasFormatException($"Cannot write property type '{type}'");
        }
    }

    private static void WriteStructBody(BinaryWriter w, GvasStruct s)
    {
        if (s.Native != null) { w.Write(s.Native); return; }
        WritePropertyList(w, s.Properties ?? new List<GvasProperty>());
    }

    private static void WriteArray(BinaryWriter w, GvasArray a)
    {
        w.Write(a.Count);
        if (a.InnerType == "StructProperty")
        {
            var tag = a.InnerTag ?? throw new GvasFormatException("Struct array without inner tag");
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            foreach (var it in a.Items) WriteStructBody(bw, (GvasStruct)it!);
            bw.Flush();
            WriteFString(w, tag.Name);
            WriteFString(w, "StructProperty");
            w.Write((int)ms.Length);
            w.Write(tag.ArrayIndex);
            WriteFString(w, tag.StructName);
            w.Write(tag.StructGuid ?? new byte[16]);
            if (tag.PropertyGuid != null) { w.Write((byte)1); w.Write(tag.PropertyGuid); }
            else w.Write((byte)0);
            w.Write(ms.GetBuffer(), 0, (int)ms.Length);
            return;
        }
        if (a.Bytes != null) { w.Write(a.Bytes); return; }
        if (a.InnerType == "ByteProperty") { foreach (var it in a.Items) WriteFString(w, it); return; }
        foreach (var it in a.Items) WriteElement(w, a.InnerType, it);
    }
}
