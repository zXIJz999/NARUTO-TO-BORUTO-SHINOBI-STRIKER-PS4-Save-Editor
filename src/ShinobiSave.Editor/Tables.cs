using System.Data;
using ShinobiSave.Core;

namespace ShinobiSave.Editor;

/// <summary>
/// Edits a GVAS map (name -> int / bool / string) through a DataTable. Nothing is written back
/// until <see cref="Commit"/>, and an untouched map is never rebuilt.
/// </summary>
internal sealed class MapTable
{
    private const string OriginColumn = "_origin";

    private readonly GvasMap _map;
    private readonly List<KeyValuePair<object?, object?>> _original;
    private bool _modified;

    public DataTable Table { get; } = new();
    public bool Supported { get; }

    /// <param name="extraColumns">Read-only text columns shown next to each key (friendly name, group...).</param>
    /// <param name="describe">Fills <paramref name="extraColumns"/> for a key.</param>
    public MapTable(GvasMap map, Action dirty, string[]? extraColumns = null, Func<string, string[]>? describe = null)
    {
        _map = map;
        _original = map.Entries.ToList();

        Type? valueType = map.ValueType switch
        {
            "IntProperty" => typeof(int),
            "BoolProperty" => typeof(bool),
            "StrProperty" or "NameProperty" => typeof(string),
            _ => null,
        };
        Supported = valueType != null && map.KeyType is "NameProperty" or "StrProperty";
        if (!Supported) return;

        Table.Columns.Add("Key", typeof(string));
        var valueCol = Table.Columns.Add("Value", valueType!);
        if (valueType == typeof(int)) valueCol.DefaultValue = 0;
        if (valueType == typeof(bool)) valueCol.DefaultValue = false;
        if (valueType == typeof(string)) valueCol.DefaultValue = "";
        Table.Columns.Add(OriginColumn, typeof(int)).DefaultValue = -1;
        foreach (var c in extraColumns ?? Array.Empty<string>()) Table.Columns.Add(c, typeof(string)).DefaultValue = "";

        Table.BeginLoadData();
        for (int i = 0; i < _original.Count; i++)
        {
            object value = valueType == typeof(string) ? Ui.Text(_original[i].Value) : _original[i].Value ?? valueCol.DefaultValue;
            string key = Ui.Text(_original[i].Key);
            var row = Table.Rows.Add(key, value, i);
            if (extraColumns == null || describe == null) continue;
            var extra = describe(key);
            for (int c = 0; c < extraColumns.Length && c < extra.Length; c++) row[extraColumns[c]] = extra[c];
        }
        Table.EndLoadData();
        Table.AcceptChanges();

        void Touch() { _modified = true; dirty(); }
        Table.ColumnChanged += (_, _) => Touch();
        Table.RowDeleted += (_, _) => Touch();
        Table.TableNewRow += (_, _) => Touch();
    }

    public DataRow? Find(string key) =>
        Supported ? Table.Rows.Cast<DataRow>().FirstOrDefault(r => r.RowState != DataRowState.Deleted && (string)r["Key"] == key) : null;

    public void Commit()
    {
        if (!Supported || !_modified) return;
        var entries = new List<KeyValuePair<object?, object?>>(Table.Rows.Count);
        foreach (DataRow row in Table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            string key = row["Key"] as string ?? "";
            if (key.Length == 0) continue;
            int origin = row[OriginColumn] is int o ? o : -1;
            object? keyObj = key, valueObj = row["Value"] is DBNull ? null : row["Value"];

            if (origin >= 0 && origin < _original.Count)
            {
                // Reuse the original objects for unchanged text so null/UTF-16 strings stay as they were.
                var orig = _original[origin];
                if (Ui.Text(orig.Key) == key) keyObj = orig.Key;
                if (valueObj is string s && Ui.Text(orig.Value) == s) valueObj = orig.Value;
            }
            else if (valueObj is string { Length: 0 } && _map.ValueType == "StrProperty")
            {
                valueObj = null; // the game stores empty strings as null
            }
            valueObj ??= _map.ValueType switch { "IntProperty" => 0, "BoolProperty" => false, _ => null };
            entries.Add(new KeyValuePair<object?, object?>(keyObj, valueObj));
        }
        _map.Entries = entries;
        _modified = false;
    }
}

/// <summary>
/// Edits an array of structs as a table: one row per element, one column per simple field.
/// Edits are written straight through to the underlying properties.
/// </summary>
internal sealed class StructArrayTable
{
    public DataTable Table { get; } = new();

    public StructArrayTable(GvasArray array, SaveSchema schema, Action dirty)
    {
        string structName = array.InnerTag?.StructName ?? "";
        var structs = array.Items.OfType<GvasStruct>().Where(s => s.Properties != null).ToList();

        var fields = schema.Fields(structName).Where(f => f.IsSimple).ToList();
        foreach (var s in structs)
            foreach (var p in s.Properties!)
                if (fields.All(f => f.Name != p.Name) && IsSimpleValue(p))
                    fields.Add(new FieldDef { Name = p.Name, Type = p.Type, Detail = p.EnumName ?? "" });

        Table.Columns.Add("#", typeof(int)).ReadOnly = true;
        foreach (var f in fields) Table.Columns.Add(f.Name, ClrType(f));

        Table.BeginLoadData();
        for (int i = 0; i < structs.Count; i++)
        {
            var row = Table.NewRow();
            row["#"] = i;
            foreach (var f in fields)
            {
                var p = structs[i].Properties!.FirstOrDefault(x => x.Name == f.Name);
                if (p == null) continue; // left as DBNull: not stored, game default
                row[f.Name] = ClrType(f) == typeof(string) ? Ui.Text(p.Value) : p.Value ?? DBNull.Value;
            }
            Table.Rows.Add(row);
        }
        Table.EndLoadData();
        Table.AcceptChanges();

        Table.ColumnChanged += (_, e) =>
        {
            if (e.Column == null || e.Column.ColumnName == "#") return;
            int index = (int)e.Row["#"];
            if (index < 0 || index >= structs.Count) return;
            var props = structs[index].Properties!;
            var def = fields.First(f => f.Name == e.Column.ColumnName);
            var prop = props.FirstOrDefault(p => p.Name == def.Name);
            object? value = e.ProposedValue is DBNull ? null : e.ProposedValue;

            if (value == null)
            {
                // Clearing a cell removes the field so the game falls back to its default.
                if (prop != null) { props.Remove(prop); dirty(); }
                return;
            }
            if (prop == null)
            {
                prop = SaveSchema.Create(def);
                schema.InsertInOrder(structName, props, prop);
            }
            else if (value is string s && s == Ui.Text(prop.Value)) return;

            prop.Value = def.Type == "ByteProperty" && value is int b ? (byte)b : value;
            dirty();
        };
    }

    private static bool IsSimpleValue(GvasProperty p) => p.Value is int or float or bool or string or GvasWideString or byte or null
        && p.Type is "IntProperty" or "FloatProperty" or "BoolProperty" or "NameProperty" or "StrProperty" or "EnumProperty" or "ByteProperty";

    private static Type ClrType(FieldDef f) => f.Type switch
    {
        "IntProperty" => typeof(int),
        "UInt32Property" => typeof(uint),
        "Int64Property" => typeof(long),
        "FloatProperty" => typeof(float),
        "BoolProperty" => typeof(bool),
        "ByteProperty" when f.Detail is "" or "None" => typeof(int),
        _ => typeof(string),
    };
}
