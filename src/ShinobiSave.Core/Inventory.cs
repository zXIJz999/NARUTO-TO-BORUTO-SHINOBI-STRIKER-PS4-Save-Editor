using System.Text;

namespace ShinobiSave.Core;

/// <summary>
/// One slot of the item box stored in m_compressedDataV2.
/// On-disk layout per slot: FString id, int32 slot index, int32 equip count, FString item type,
/// int32 unknown, int32 unknown, FString skill x3, byte flag.
/// </summary>
public sealed class InventoryItem
{
    public const string EmptyId = "None";
    public const string EmptyType = "ENN_ItemType::ENN_ItemType_None";
    public const string TypePrefix = "ENN_ItemType::ENN_ItemType_";

    private FStr _id, _type, _s1, _s2, _s3;

    /// <summary>Slot number. Loadouts refer to items by this index, so it is not editable.</summary>
    public int Index { get; internal set; }

    public string Id { get => _id.Text; set => _id = _id.With(value); }

    /// <summary>Item type without the enum prefix: Weapon, Jacket, Pants, Accessory, Facepaint, NinjaWeapon, Summon, None.</summary>
    public string Type
    {
        get => _type.Text.StartsWith(TypePrefix, StringComparison.Ordinal) ? _type.Text[TypePrefix.Length..] : _type.Text;
        set => _type = _type.With(value.Contains("::") ? value : TypePrefix + value);
    }

    /// <summary>How many loadouts use this item (16 loadouts, so 16 means "equipped everywhere").</summary>
    public int EquipCount { get; set; }

    public string Skill1 { get => _s1.Text; set => _s1 = _s1.With(value); }
    public string Skill2 { get => _s2.Text; set => _s2 = _s2.With(value); }
    public string Skill3 { get => _s3.Text; set => _s3 = _s3.With(value); }

    /// <summary>Meaning not confirmed. Set on most items that carry skills.</summary>
    public bool Flag { get; set; }

    /// <summary>Always 0 in the saves examined.</summary>
    public int Unknown1 { get; set; }

    /// <summary>0 in all but one slot of the saves examined.</summary>
    public int Unknown2 { get; set; }

    public bool IsEmpty => Id == EmptyId || Id.Length == 0;

    public void Clear()
    {
        Id = EmptyId; Type = "None"; EquipCount = 0; Unknown1 = 0; Unknown2 = 0; Flag = false;
        Skill1 = Skill2 = Skill3 = EmptyId;
    }

    internal static InventoryItem Read(byte[] d, ref int p)
    {
        var it = new InventoryItem();
        it._id = FStr.Read(d, ref p);
        it.Index = I32(d, ref p);
        it.EquipCount = I32(d, ref p);
        it._type = FStr.Read(d, ref p);
        it.Unknown1 = I32(d, ref p);
        it.Unknown2 = I32(d, ref p);
        it._s1 = FStr.Read(d, ref p);
        it._s2 = FStr.Read(d, ref p);
        it._s3 = FStr.Read(d, ref p);
        if (p >= d.Length) throw new GvasFormatException("Item record truncated");
        it.Flag = d[p++] != 0;
        return it;
    }

    internal void Write(BinaryWriter w)
    {
        _id.Write(w);
        w.Write(Index);
        w.Write(EquipCount);
        _type.Write(w);
        w.Write(Unknown1);
        w.Write(Unknown2);
        _s1.Write(w); _s2.Write(w); _s3.Write(w);
        w.Write((byte)(Flag ? 1 : 0));
    }

    private static int I32(byte[] d, ref int p)
    {
        if (p + 4 > d.Length) throw new GvasFormatException("Item record truncated");
        int v = BitConverter.ToInt32(d, p); p += 4; return v;
    }

    /// <summary>FString that remembers whether it was null or UTF-16 so untouched values round-trip.</summary>
    private readonly struct FStr
    {
        private readonly string? _value;
        private readonly bool _wide;

        private FStr(string? value, bool wide) { _value = value; _wide = wide; }

        public string Text => _value ?? "";

        public FStr With(string? text) => text == Text ? this : new FStr(text ?? "", false);

        public static FStr Read(byte[] d, ref int p)
        {
            int len = I32(d, ref p);
            if (len == 0) return new FStr(null, false);
            if (len > 0)
            {
                if (len > 0x10000 || p + len > d.Length) throw new GvasFormatException($"Item string length {len}");
                var s = Encoding.Latin1.GetString(d, p, len - 1); p += len;
                return new FStr(s, false);
            }
            int chars = -len;
            if (chars > 0x10000 || p + chars * 2 > d.Length) throw new GvasFormatException($"Item string length {len}");
            var w = Encoding.Unicode.GetString(d, p, (chars - 1) * 2); p += chars * 2;
            return new FStr(w, true);
        }

        public void Write(BinaryWriter w)
        {
            if (_value == null) { w.Write(0); return; }
            GvasWriter.WriteFString(w, _wide ? new GvasWideString { Value = _value } : _value);
        }
    }
}

/// <summary>Reads and writes the compressed item box (m_compressedDataV2, data version 512).</summary>
public static class InventoryCodec
{
    public const int BlockSize = 0x20000;

    /// <summary>Decompresses every block in the property and joins them into one stream.</summary>
    public static byte[] Decompress(byte[] blob)
    {
        using var ms = new MemoryStream();
        int offset = 0;
        while (offset < blob.Length)
        {
            if (!UeCompressed.LooksCompressed(blob, offset))
                throw new GvasFormatException($"Item box: unexpected data at 0x{offset:X}");
            ms.Write(UeCompressed.DecompressBlock(blob, ref offset, out _));
        }
        return ms.ToArray();
    }

    public static List<InventoryItem> Parse(byte[] stream)
    {
        if (stream.Length < 8) throw new GvasFormatException("Item box stream too short");
        int length = BitConverter.ToInt32(stream, 0);
        int count = BitConverter.ToInt32(stream, 4);
        if (length != stream.Length - 4) throw new GvasFormatException($"Item box length field {length} does not match stream size {stream.Length}");
        if (count < 0 || count > 1_000_000) throw new GvasFormatException($"Item box count {count}");
        var list = new List<InventoryItem>(count);
        int p = 8;
        for (int i = 0; i < count; i++) list.Add(InventoryItem.Read(stream, ref p));
        if (p != stream.Length) throw new GvasFormatException($"Item box has {stream.Length - p} unread bytes");
        return list;
    }

    public static byte[] Serialize(List<InventoryItem> items)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0);
        w.Write(items.Count);
        foreach (var it in items) it.Write(w);
        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), bytes.Length - 4);
        return bytes;
    }

    /// <summary>Splits the stream into 128 KB pieces and compresses each as its own block, as the game does.</summary>
    public static byte[] Compress(byte[] stream)
    {
        using var ms = new MemoryStream();
        for (int o = 0; o < stream.Length; o += BlockSize)
        {
            int n = Math.Min(BlockSize, stream.Length - o);
            ms.Write(UeCompressed.CompressBlock(stream.AsSpan(o, n).ToArray(), BlockSize));
        }
        return ms.ToArray();
    }
}
