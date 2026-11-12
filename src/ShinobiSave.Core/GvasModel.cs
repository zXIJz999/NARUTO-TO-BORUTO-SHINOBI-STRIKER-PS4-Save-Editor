namespace ShinobiSave.Core;

/// <summary>One tagged property (name + type + payload) from a UE4 property list.</summary>
public sealed class GvasProperty
{
    public string Name = "";
    public string Type = "";
    public int ArrayIndex;

    // Type-specific tag fields. Only the ones that apply to Type are set.
    public string? StructName;
    public byte[]? StructGuid;
    public string? InnerType;
    public string? KeyType;
    public string? ValueType;
    public string? EnumName;

    /// <summary>Optional per-property GUID; null when the tag's HasPropertyGuid byte is 0.</summary>
    public byte[]? PropertyGuid;

    /// <summary>
    /// Boxed primitive (int, float, bool, string, ...), or one of
    /// <see cref="GvasStruct"/>, <see cref="GvasArray"/>, <see cref="GvasMap"/>,
    /// <see cref="GvasSet"/>, <see cref="GvasRaw"/>.
    /// </summary>
    public object? Value;

    /// <summary>File offset of the tag and payload size as read. Informational only.</summary>
    public long Offset;
    public int SizeOnDisk;

    public override string ToString() => $"{Name} : {Type}";
}

/// <summary>A struct body: either a nested property list or the raw bytes of a native struct.</summary>
public sealed class GvasStruct
{
    public string StructName = "";
    public List<GvasProperty>? Properties;
    public byte[]? Native;
}

public sealed class GvasArray
{
    public string InnerType = "";
    public List<object?> Items = new();

    /// <summary>Set when a ByteProperty array is stored as a plain byte blob.</summary>
    public byte[]? Bytes;

    /// <summary>The inner tag that precedes the elements of a struct array.</summary>
    public GvasProperty? InnerTag;

    public int Count => Bytes?.Length ?? Items.Count;
}

public sealed class GvasMap
{
    public string KeyType = "";
    public string ValueType = "";
    public int RemovedCount;
    public List<KeyValuePair<object?, object?>> Entries = new();
}

public sealed class GvasSet
{
    public string InnerType = "";
    public int RemovedCount;
    public List<object?> Items = new();
}

/// <summary>Payload kept verbatim because its layout is not understood.</summary>
public sealed class GvasRaw
{
    public byte[] Data = Array.Empty<byte>();
    public string Reason = "";
}

/// <summary>An FString that must be written back as UTF-16 even though it is pure ASCII.</summary>
public sealed class GvasWideString
{
    public string Value = "";
    public override string ToString() => Value;
}

public sealed class GvasCustomVersion
{
    public byte[] Key = new byte[16];
    public int Version;
}

public sealed class GvasFile
{
    public int SaveGameVersion;
    public int PackageVersion;
    public ushort EngineMajor, EngineMinor, EnginePatch;
    public uint EngineBuild;
    public string? EngineBranch;
    public int CustomFormatVersion;
    public List<GvasCustomVersion> CustomVersions = new();
    public string? SaveGameClass;
    public List<GvasProperty> Properties = new();

    /// <summary>Bytes after the root property list (normally four zero bytes).</summary>
    public byte[] Trailer = Array.Empty<byte>();

    public static GvasFile Load(string path) => new GvasReader(File.ReadAllBytes(path)).ReadFile();

    public static GvasFile Load(byte[] data) => new GvasReader(data).ReadFile();

    public byte[] ToBytes() => GvasWriter.Write(this);

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());
}
