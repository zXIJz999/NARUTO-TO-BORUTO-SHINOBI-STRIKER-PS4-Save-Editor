using ShinobiSave.Core;

namespace ShinobiSave.Web;

/// <summary>
/// The open save and everything the pages share. All byte-level rules are in Core (EditOps,
/// FieldSlot, ShinobiSaveFile.Export), the same code the desktop editor uses.
/// </summary>
public sealed class EditorState
{
    public const string AppTitle = "Shinobi Striker Save Editor";
    public const string Author = "zXIJz";
    public const string Credit = "Made by " + Author;

    public SaveSchema Schema { get; private set; } = SaveSchema.FromSeed();
    public IdCatalog Catalog { get; private set; } = IdCatalog.FromSeed();
    public GameNames Names { get; } = GameNames.Load();

    public ShinobiSaveFile? Save { get; private set; }
    public byte[]? OriginalBytes { get; private set; }
    public string FileName { get; private set; } = "";
    public bool Dirty { get; private set; }
    public List<string> LogLines { get; } = new();
    public string Status { get; private set; } = "No save open";

    /// <summary>Raised after anything changes, so every page can redraw.</summary>
    public event Action? Changed;

    private readonly Dictionary<string, List<NameEntry>> _entries = new(StringComparer.Ordinal);

    public List<NameEntry> E(params string[] tables)
    {
        string key = string.Join("|", tables);
        if (!_entries.TryGetValue(key, out var list)) _entries[key] = list = Names.Entries(tables);
        return list;
    }

    public void Log(string message)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        Status = message;
        Changed?.Invoke();
    }

    public void MarkDirty()
    {
        if (Dirty) return;
        Dirty = true;
        Changed?.Invoke();
    }

    public void Open(byte[] data, string fileName)
    {
        // Fresh schema and catalogue per file, as the desktop editor harvests per open.
        var save = ShinobiSaveFile.Load(data, fileName);
        Schema = SaveSchema.FromSeed();
        Catalog = IdCatalog.FromSeed();
        Catalog.Harvest(save);
        Schema.Harvest(save.Gvas);
        Save = save;
        OriginalBytes = data;
        FileName = fileName;
        Dirty = false;
        Log($"Opened {fileName}" + (save.Items != null ? $" ({save.Items.Count(i => !i.IsEmpty)} items)" : ""));
        if (save.ItemBoxError != null) Log($"The item box could not be read and will be left untouched: {save.ItemBoxError}");
    }

    public void Undo()
    {
        if (OriginalBytes != null) Open(OriginalBytes, FileName);
    }

    /// <summary>The edited file's bytes (equip counts recounted, round-trip checked).</summary>
    public byte[] Export()
    {
        var bytes = Save!.Export();
        Dirty = false;
        Log($"Saved {FileName} ({bytes.Length:N0} bytes). Your browser downloads it; your original file is not changed.");
        return bytes;
    }

    /// <summary>A field of a struct, bound to the shared write rules.</summary>
    public FieldSlot Slot(string? structName, List<GvasProperty> props, string field) => new(Schema, structName, props, field);

    /// <summary>Sets a field and marks the save changed when it did change.</summary>
    public void Set(FieldSlot slot, object? value)
    {
        if (slot.Set(value)) MarkDirty();
    }

    public void Refresh() => Changed?.Invoke();

    // ---- in-page confirmation (no browser alert/confirm boxes) ----

    public string? Question { get; private set; }
    private Action? _onYes;

    public void Ask(string question, Action onYes)
    {
        Question = question;
        _onYes = onYes;
        Changed?.Invoke();
    }

    public void Answer(bool yes)
    {
        var act = yes ? _onYes : null;
        Question = null;
        _onYes = null;
        act?.Invoke();
        Changed?.Invoke();
    }

    public static (string? structName, List<GvasProperty> props)? Struct(List<GvasProperty> parent, string name)
    {
        var prop = parent.FirstOrDefault(p => p.Name == name);
        return prop?.Value is GvasStruct { Properties: not null } st ? (prop.StructName, st.Properties) : null;
    }
}
