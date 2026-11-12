using System.Drawing;
using System.Globalization;
using DevExpress.XtraEditors;
using ShinobiSave.Core;

namespace ShinobiSave.Editor;

/// <summary>
/// A roomy form: one labelled editor per row, grouped under headings. Editors stretch with the
/// page, so the form never needs a sideways scroll bar.
/// </summary>
internal sealed class FormPage : XtraScrollableControl
{
    private const int LabelWidth = 190;
    private const int RowPitch = 42;
    private const int MaxEditorWidth = 440;
    private const int Gap = 8;

    private readonly SaveSchema _schema;
    private readonly GameNames _names;
    private readonly IdCatalog _catalog;
    private readonly Action _dirty;
    private readonly List<(Control editor, Control? trailing)> _rows = new();
    private readonly List<Control> _wide = new();
    private int _y = Ui.Pad;
    private bool _loading;

    public FormPage(SaveSchema schema, GameNames names, IdCatalog catalog, Action dirty)
    {
        _schema = schema;
        _names = names;
        _catalog = catalog;
        _dirty = dirty;
        Dock = DockStyle.Fill;
        Resize += (_, _) => Arrange();
    }

    /// <summary>Removes every row so the page can be filled again (used when the selection changes).</summary>
    public void Reset()
    {
        SuspendLayout();
        foreach (Control c in Controls.Cast<Control>().ToArray()) { Controls.Remove(c); c.Dispose(); }
        _rows.Clear();
        _wide.Clear();
        _y = Ui.Pad;
        ResumeLayout();
    }

    private void Arrange()
    {
        int x = Ui.Pad + LabelWidth;
        int room = ClientSize.Width - x - Ui.Pad - SystemInformation.VerticalScrollBarWidth;
        foreach (var (editor, trailing) in _rows)
        {
            int extra = trailing == null ? 0 : trailing.Width + Gap;
            editor.Width = Math.Max(120, Math.Min(MaxEditorWidth, room - extra));
            if (trailing != null) trailing.Left = editor.Right + Gap;
        }
        foreach (var c in _wide) c.Width = Math.Max(200, ClientSize.Width - Ui.Pad * 2 - SystemInformation.VerticalScrollBarWidth);
    }

    // ---- layout -----------------------------------------------------------------

    public void Heading(string title)
    {
        if (_y > Ui.Pad) _y += 14;
        Controls.Add(Ui.Label(title, Ui.Pad, _y, Ui.HeadingFont));
        _y += 40;
    }

    public void Note(string text, int lines = 2)
    {
        var block = Ui.TextBlock(text, Ui.Pad, _y, 400, lines * 20 + 4);
        Controls.Add(block);
        _wide.Add(block);
        _y += lines * 20 + 14;
        Arrange();
    }

    /// <summary>Adds a labelled editor, with an optional small control (button, colour swatch) after it.</summary>
    public T Row<T>(string label, T editor, Control? trailing = null, string? tip = null) where T : Control
    {
        var caption = Ui.Label(label, Ui.Pad, _y + 6);
        if (tip != null) caption.ToolTip = tip;
        editor.Location = new Point(Ui.Pad + LabelWidth, _y);
        editor.Height = 30;
        Controls.Add(caption);
        Controls.Add(editor);
        if (trailing != null)
        {
            trailing.Top = _y;
            trailing.Height = editor.Height;
            Controls.Add(trailing);
        }
        _rows.Add((editor, trailing));
        _y += RowPitch;
        Arrange();
        return editor;
    }

    // ---- editors bound to a save field ----------------------------------------------

    /// <summary>One field of a struct; the write rules live in Core (<see cref="FieldSlot"/>), shared with the web editor.</summary>
    private sealed class Slot
    {
        private readonly FormPage _page;
        private readonly FieldSlot _field;

        public Slot(FormPage page, string? structName, List<GvasProperty> props, string field)
        {
            _page = page;
            _field = new FieldSlot(page._schema, structName, props, field);
        }

        public FieldDef Def => _field.Def;
        public bool Stored => _field.Stored;
        public object? Value => _field.Value;

        public void Set(object? value)
        {
            if (_page._loading) return;
            if (_field.Set(value)) _page._dirty();
        }
    }

    public TextEdit TextBox(string label, string? structName, List<GvasProperty> props, string field, int maxLength = 0)
    {
        var slot = new Slot(this, structName, props, field);
        var edit = new TextEdit();
        if (maxLength > 0) edit.Properties.MaxLength = maxLength;
        _loading = true; edit.Text = Ui.Text(slot.Value); _loading = false;
        edit.EditValueChanged += (_, _) => slot.Set(edit.Text);
        return Row(label, edit);
    }

    public SpinEdit Number(string label, string? structName, List<GvasProperty> props, string field, int min = int.MinValue, int max = int.MaxValue)
    {
        var slot = new Slot(this, structName, props, field);
        bool isByte = slot.Def.Type == "ByteProperty";
        var spin = Ui.WholeNumber(isByte ? 0 : min, isByte ? 255 : max);
        _loading = true; spin.Value = slot.Value == null ? 0 : Convert.ToDecimal(slot.Value, CultureInfo.InvariantCulture); _loading = false;
        spin.EditValueChanged += (_, _) => slot.Set(isByte ? (byte)spin.Value : (int)spin.Value);
        return Row(label, spin);
    }

    public SpinEdit Decimal(string label, string? structName, List<GvasProperty> props, string field)
    {
        var slot = new Slot(this, structName, props, field);
        var spin = new SpinEdit();
        spin.Properties.IsFloatValue = true;
        spin.Properties.Increment = 0.05m;
        _loading = true; spin.Value = slot.Value == null ? 0 : Convert.ToDecimal(slot.Value, CultureInfo.InvariantCulture); _loading = false;
        spin.EditValueChanged += (_, _) => slot.Set((float)spin.Value);
        return Row(label, spin);
    }

    public ToggleSwitch Toggle(string label, string? structName, List<GvasProperty> props, string field)
    {
        var slot = new Slot(this, structName, props, field);
        var toggle = new ToggleSwitch();
        toggle.Properties.OnText = "Yes";
        toggle.Properties.OffText = "No";
        _loading = true; toggle.IsOn = slot.Value is true; _loading = false;
        toggle.Toggled += (_, _) => slot.Set(toggle.IsOn);
        return Row(label, toggle);
    }

    /// <summary>A drop-down of named choices. Type in it to search.</summary>
    public LookUpEdit Pick(string label, string? structName, List<GvasProperty> props, string field,
        IEnumerable<NameEntry> entries, bool withNone = false, bool showInfo = false, bool colour = false)
    {
        var slot = new Slot(this, structName, props, field);
        string current = Ui.Text(slot.Value);
        var choices = _names.Choices(entries, current, withNone);
        var edit = Ui.Lookup(choices, showInfo || colour);
        edit.Properties.NullText = "(game default)";
        _loading = true; edit.EditValue = slot.Stored ? current : null; _loading = false;

        PanelControl? swatch = null;
        DevExpress.XtraEditors.SimpleButton? sort = null;
        if (showInfo && choices.Any(c => c.Rank < 5))
        {
            // Cycle the list order: game order, A-Z, best rank first, worst rank first.
            string[] modes = { "Sort: Game order", "Sort: A-Z", "Sort: Best rank first", "Sort: Worst rank first" };
            int mode = 0;
            sort = new DevExpress.XtraEditors.DropDownButton { Text = "Sort", Width = 76 };
            var menu = new DevExpress.XtraBars.PopupMenu();
            void Apply(int chosen)
            {
                mode = chosen;
                IEnumerable<NameEntry> ordered = mode switch
                {
                    1 => choices.OrderBy(c => c.Id == GameNames.None ? 0 : 1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
                    2 => choices.OrderBy(c => c.Rank).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
                    3 => choices.OrderByDescending(c => c.Rank).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
                    _ => choices,
                };
                var value = edit.EditValue;
                _loading = true;
                edit.Properties.DataSource = ordered.ToList();
                edit.EditValue = value;
                _loading = false;
            }
            var xm = new DevExpress.Utils.Menu.DXPopupMenu();
            for (int m = 0; m < modes.Length; m++)
            {
                int chosen = m;
                xm.Items.Add(new DevExpress.Utils.Menu.DXMenuItem(modes[m].Replace("Sort: ", ""), (_, _) => Apply(chosen)));
            }
            ((DevExpress.XtraEditors.DropDownButton)sort).DropDownControl = xm;
        }
        if (colour)
        {
            swatch = new PanelControl { Width = 30, BorderStyle = DevExpress.XtraEditors.Controls.BorderStyles.Simple };
            void Paint() => swatch.Appearance.BackColor = ColourOf(edit.EditValue as string) ?? Color.Transparent;
            swatch.Appearance.Options.UseBackColor = true;
            swatch.LookAndFeel.UseDefaultLookAndFeel = false;
            swatch.LookAndFeel.Style = DevExpress.LookAndFeel.LookAndFeelStyle.Flat;
            Paint();
            edit.EditValueChanged += (_, _) => Paint();
        }
        edit.EditValueChanged += (_, _) => { if (edit.EditValue is string id) slot.Set(id); };
        return Row(label, edit, (Control?)swatch ?? sort);
    }

    /// <summary>A drop-down whose choices stand for whole numbers (item box slots).</summary>
    public LookUpEdit PickNumber(string label, string? structName, List<GvasProperty> props, string field, object entries, string valueMember)
    {
        var slot = new Slot(this, structName, props, field);
        var edit = new LookUpEdit();
        Ui.SetupLookup(edit.Properties, entries, valueMember, true);
        edit.Properties.NullText = "(game default)";
        _loading = true; edit.EditValue = slot.Value as int?; _loading = false;
        edit.EditValueChanged += (_, _) => { if (edit.EditValue is int n) slot.Set(n); };
        return Row(label, edit);
    }

    private Color? ColourOf(string? id)
    {
        try { return _names.Info(id)?["color"] is { } hex ? ColorTranslator.FromHtml(hex) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>A drop-down for an enum field (window mode, gender...).</summary>
    public LookUpEdit Choice(string label, string? structName, List<GvasProperty> props, string field)
    {
        var slot = new Slot(this, structName, props, field);
        var entries = _catalog.EnumValues(slot.Def.Detail).Select(v => new NameEntry { Id = v, Name = GameNames.Prettify(v) });
        return Pick(label, structName, props, field, entries);
    }

    /// <summary>Whole-number field stored as an 8-byte struct, or any other value edited as plain text.</summary>
    public TextEdit Raw(string label, string? structName, List<GvasProperty> props, string field)
    {
        var slot = new Slot(this, structName, props, field);
        var edit = new TextEdit();
        string Show(object? v) => v switch
        {
            GvasStruct { Native: { Length: 8 } n } when slot.Def.Detail == "IntPoint" => $"{BitConverter.ToInt32(n, 0)} x {BitConverter.ToInt32(n, 4)}",
            GvasStruct { Native: { Length: 8 } n } => BitConverter.ToInt64(n, 0).ToString(CultureInfo.InvariantCulture),
            _ => Ui.Text(v),
        };
        _loading = true; edit.Text = Show(slot.Value); _loading = false;
        edit.Enabled = slot.Stored || slot.Def.Type != "StructProperty";
        edit.EditValueChanged += (_, _) =>
        {
            string text = edit.Text.Trim();
            switch (slot.Def.Type)
            {
                case "UInt32Property": if (uint.TryParse(text, out var u)) slot.Set(u); break;
                case "Int64Property": if (long.TryParse(text, out var l)) slot.Set(l); break;
                case "StructProperty" when slot.Def.Detail == "IntPoint":
                    var parts = text.Split(new[] { 'x', 'X', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    {
                        var bytes = new byte[8];
                        BitConverter.TryWriteBytes(bytes.AsSpan(0), w);
                        BitConverter.TryWriteBytes(bytes.AsSpan(4), h);
                        slot.Set(new GvasStruct { StructName = "IntPoint", Native = bytes });
                    }
                    break;
                case "StructProperty":
                    if (long.TryParse(text, out var n)) slot.Set(new GvasStruct { StructName = slot.Def.Detail, Native = BitConverter.GetBytes(n) });
                    break;
                default: slot.Set(text); break;
            }
        };
        return Row(label, edit);
    }

    /// <summary>Adds a row for every simple field of a struct, with labels tidied from the field names.</summary>
    public void Auto(string? structName, List<GvasProperty> props, IReadOnlyDictionary<string, string>? labels = null, params string[] exclude)
    {
        var fields = _schema.Fields(structName).ToList();
        foreach (var p in props)
            if (fields.All(f => f.Name != p.Name))
                fields.Add(new FieldDef { Name = p.Name, Type = p.Type, Detail = p.EnumName ?? p.StructName ?? "" });

        foreach (var def in fields)
        {
            if (exclude.Contains(def.Name)) continue;
            string label = labels != null && labels.TryGetValue(def.Name, out var l) ? l : GameNames.Prettify(def.Name);
            switch (def.Type)
            {
                case "IntProperty":
                case "ByteProperty" when def.Detail is "" or "None":
                    Number(label, structName, props, def.Name); break;
                case "FloatProperty": Decimal(label, structName, props, def.Name); break;
                case "BoolProperty": Toggle(label, structName, props, def.Name); break;
                case "StrProperty": TextBox(label, structName, props, def.Name); break;
                case "EnumProperty":
                case "ByteProperty":
                    Choice(label, structName, props, def.Name); break;
                case "NameProperty":
                    string current = Ui.Text(props.FirstOrDefault(p => p.Name == def.Name)?.Value);
                    var like = _catalog.Like(current).Select(_names.Entry);
                    Pick(label, structName, props, def.Name, like, withNone: true); break;
                case "UInt32Property":
                case "Int64Property":
                case "StructProperty" when def.Detail is "NN_Int64" or "NN_ServerCharacterID" or "IntPoint":
                    Raw(label, structName, props, def.Name); break;
            }
        }
    }

    /// <summary>The fields of the struct property <paramref name="name"/>, or null when the save does not hold it.</summary>
    public static (string? structName, List<GvasProperty> props)? Struct(List<GvasProperty> parent, string name)
    {
        var prop = parent.FirstOrDefault(p => p.Name == name);
        return prop?.Value is GvasStruct { Properties: not null } st ? (prop.StructName, st.Properties) : null;
    }
}
