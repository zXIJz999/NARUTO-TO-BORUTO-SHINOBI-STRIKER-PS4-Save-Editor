namespace ShinobiSave.Core;

public enum ShinobiSaveKind { Unknown, Customize, System }

/// <summary>
/// A loaded Shinobi Striker save: the generic GVAS tree plus the decoded item box when the
/// file is the character save (Customize_v002.sav).
/// </summary>
public sealed class ShinobiSaveFile
{
    public const string ItemBoxProperty = "m_compressedDataV2";
    public const int SupportedItemBoxVersion = 512;

    public static readonly string[] LoadoutItemFields =
        { "JacketItemIndex", "PantsItemIndex", "AccessoryItemIndex", "WeaponItemIndex", "SubWeaponItemIndex" };

    public string Path { get; private set; } = "";
    public GvasFile Gvas { get; private set; } = new();
    public ShinobiSaveKind Kind { get; private set; }

    /// <summary>Root struct properties (CustomizeInfo or SystemData contents).</summary>
    public List<GvasProperty> Root { get; private set; } = new();

    /// <summary>Decoded item box, or null when absent or in an unsupported layout.</summary>
    public List<InventoryItem>? Items { get; private set; }

    /// <summary>Why <see cref="Items"/> is null, when the property exists but could not be decoded.</summary>
    public string? ItemBoxError { get; private set; }

    private byte[]? _itemStreamAtLoad;

    public static ShinobiSaveFile Load(string path) => Load(File.ReadAllBytes(path), path);

    /// <summary>Loads a save from memory; <paramref name="name"/> is kept as its path (used by the web editor).</summary>
    public static ShinobiSaveFile Load(byte[] data, string name)
    {
        var s = new ShinobiSaveFile { Path = name, Gvas = GvasFile.Load(data) };
        s.Kind = s.Gvas.SaveGameClass switch
        {
            "BP_SaveGame_C" => ShinobiSaveKind.Customize,
            "BP_SaveSystem_C" => ShinobiSaveKind.System,
            _ => ShinobiSaveKind.Unknown,
        };
        s.Root = s.Gvas.Properties.Count == 1 && s.Gvas.Properties[0].Value is GvasStruct { Properties: not null } st
            ? st.Properties
            : s.Gvas.Properties;
        s.LoadItems();
        return s;
    }

    private void LoadItems()
    {
        if (Prop(ItemBoxProperty)?.Value is not GvasArray { Bytes: not null } arr) return;
        try
        {
            var stream = InventoryCodec.Decompress(arr.Bytes);
            var items = InventoryCodec.Parse(stream);
            // Only trust the decoder if it reproduces the stream exactly.
            if (!InventoryCodec.Serialize(items).AsSpan().SequenceEqual(stream))
                throw new GvasFormatException("Item box did not re-serialise identically");
            Items = items;
            _itemStreamAtLoad = stream;
        }
        catch (Exception ex) when (ex is GvasFormatException or InvalidDataException or ArgumentException)
        {
            ItemBoxError = ex.Message;
        }
    }

    public GvasProperty? Prop(params string[] path) => Find(Root, path);

    public static GvasProperty? Find(List<GvasProperty> props, params string[] path)
    {
        GvasProperty? cur = null;
        var list = props;
        foreach (var name in path)
        {
            cur = list.FirstOrDefault(p => p.Name == name);
            if (cur == null) return null;
            list = (cur.Value as GvasStruct)?.Properties ?? new List<GvasProperty>();
        }
        return cur;
    }

    public GvasMap? Map(string name) => Prop(name)?.Value as GvasMap;

    public List<GvasStruct> Loadouts =>
        (Prop("m_playerSettings")?.Value as GvasArray)?.Items.OfType<GvasStruct>().ToList() ?? new List<GvasStruct>();

    /// <summary>
    /// Sets every item's equip count to the number of loadouts that reference its slot.
    /// Returns the number of items whose count changed.
    /// </summary>
    public int RecountEquipped()
    {
        if (Items == null) return 0;
        var counts = new int[Items.Count];
        foreach (var lo in Loadouts)
        {
            if (lo.Properties == null) continue;
            foreach (var field in LoadoutItemFields)
            {
                if (lo.Properties.FirstOrDefault(p => p.Name == field)?.Value is int idx && idx >= 0 && idx < counts.Length)
                    counts[idx]++;
            }
        }
        int changed = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].EquipCount == counts[i]) continue;
            Items[i].EquipCount = counts[i];
            changed++;
        }
        return changed;
    }

    /// <summary>Serialises the save. The item box is only recompressed when an item changed.</summary>
    public byte[] ToBytes()
    {
        if (Items != null && Prop(ItemBoxProperty)?.Value is GvasArray arr)
        {
            var stream = InventoryCodec.Serialize(Items);
            if (_itemStreamAtLoad == null || !stream.AsSpan().SequenceEqual(_itemStreamAtLoad))
            {
                arr.Bytes = InventoryCodec.Compress(stream);
                _itemStreamAtLoad = stream;
            }
        }
        return Gvas.ToBytes();
    }

    /// <summary>
    /// The bytes to write: equip counts brought in step with the loadouts, then serialised and
    /// checked to read back identically. Throws when the check fails.
    /// </summary>
    public byte[] Export()
    {
        // Each item stores how many loadouts use it; keep that in step with the loadouts.
        if (Items != null) RecountEquipped();
        var bytes = ToBytes();
        // Refuse to write something we cannot read back.
        var check = GvasFile.Load(bytes);
        if (!check.ToBytes().AsSpan().SequenceEqual(bytes))
            throw new GvasFormatException("Self-check failed: written data does not round-trip. Nothing was saved.");
        return bytes;
    }

    /// <summary>
    /// Writes the save to <paramref name="path"/>. If the file exists it is first copied to a
    /// timestamped .bak next to it. Returns the backup path, or null if none was made.
    /// </summary>
    public string? Save(string path)
    {
        var bytes = Export();
        string? backup = null;
        if (File.Exists(path))
        {
            backup = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(path, backup, false);
        }
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
        Path = path;
        return backup;
    }
}
