using System.Data;
using System.Drawing;
using DevExpress.LookAndFeel;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using DevExpress.XtraEditors.Repository;
using DevExpress.XtraGrid;
using DevExpress.XtraGrid.Views.Grid;
using ShinobiSave.Core;

namespace ShinobiSave.Editor;

/// <summary>Small factories so every page builds its controls the same way.</summary>
internal static class Ui
{
    public const string Author = "zXIJz";
    public const string Credit = "Made by " + Author;

    public const int Pad = 24;
    public const int ButtonHeight = 36;
    public const int GridRowHeight = 30;

    public static readonly Font TitleFont = new("Segoe UI", 16F, FontStyle.Bold);
    public static readonly Font HeadingFont = new("Segoe UI", 12F, FontStyle.Bold);
    public static readonly Font CreditFont = new("Segoe UI", 11F, FontStyle.Bold);
    public static readonly Font BigFont = new("Segoe UI", 11F);

    /// <summary>Skin colours, so nothing here brings its own palette.</summary>
    public static Color Accent => DXSkinColors.ForeColors.Question;
    public static Color Warning => DXSkinColors.ForeColors.Warning;
    public static Color Dim => DXSkinColors.ForeColors.DisabledText;

    public static PanelControl Panel(DockStyle dock, int size = 0)
    {
        var p = new PanelControl { Dock = dock, BorderStyle = BorderStyles.NoBorder };
        if (dock is DockStyle.Top or DockStyle.Bottom) p.Height = size;
        if (dock is DockStyle.Left or DockStyle.Right) p.Width = size;
        return p;
    }

    public static SimpleButton Button(string text, int x, int width, EventHandler onClick, int y = 12)
    {
        var b = new SimpleButton { Text = text, Location = new Point(x, y), Size = new Size(width, ButtonHeight) };
        b.Click += onClick;
        return b;
    }

    public static LabelControl Label(string text, int x, int y) => new() { Text = text, Location = new Point(x, y) };

    public static LabelControl Label(string text, int x, int y, Font font, Color? color = null)
    {
        var l = new LabelControl { Text = text, Location = new Point(x, y) };
        l.Appearance.Font = font;
        if (color != null) l.Appearance.ForeColor = color.Value;
        return l;
    }

    /// <summary>The credit line. Every page and the window frame carry one.</summary>
    public static LabelControl CreditLabel(int x, int y) => Label(Credit, x, y, CreditFont, Accent);

    /// <summary>A wrapped block of text with a fixed size.</summary>
    public static LabelControl TextBlock(string text, int x, int y, int width, int height, Font? font = null, Color? color = null)
    {
        var l = new LabelControl
        {
            Text = text,
            Location = new Point(x, y),
            AutoSizeMode = LabelAutoSizeMode.None,
            Size = new Size(width, height),
        };
        l.Appearance.TextOptions.WordWrap = DevExpress.Utils.WordWrap.Wrap;
        l.Appearance.TextOptions.VAlignment = DevExpress.Utils.VertAlignment.Top;
        if (font != null) l.Appearance.Font = font;
        if (color != null) l.Appearance.ForeColor = color.Value;
        return l;
    }

    public static LabelControl Note(string text, DockStyle dock = DockStyle.Top, int height = 40) => new()
    {
        Text = text,
        Dock = dock,
        AutoSizeMode = LabelAutoSizeMode.None,
        Height = height,
        Padding = new Padding(Pad, 10, Pad, 6),
        Appearance = { TextOptions = { WordWrap = DevExpress.Utils.WordWrap.Wrap } },
    };

    // ---- drop-downs that show names and store IDs ----------------------------

    public static void SetupLookup(RepositoryItemLookUpEdit p, object entries, string valueMember, bool showInfo)
    {
        p.DataSource = entries;
        p.ValueMember = valueMember;
        p.DisplayMember = "Name";
        p.Columns.Clear();
        // Fixed widths: the name gets the room, the rank/type column stays narrow.
        p.Columns.Add(new LookUpColumnInfo("Name", "Name", showInfo ? 430 : 340));
        if (showInfo) p.Columns.Add(new LookUpColumnInfo("Info", "", 90));
        p.ShowHeader = false;
        p.ShowFooter = false;
        p.DropDownRows = 16;
        p.NullText = "";
        // Typing in the box filters the list to names that contain the text.
        p.SearchMode = SearchMode.AutoSearch;
        p.PopupFilterMode = PopupFilterMode.Contains;
        p.BestFitMode = BestFitMode.None;
        p.PopupWidthMode = PopupWidthMode.ContentWidth;
        p.PopupFormMinSize = new Size(showInfo ? 580 : 380, 0);
    }

    public static LookUpEdit Lookup(List<NameEntry> entries, bool showInfo = false)
    {
        var e = new LookUpEdit();
        SetupLookup(e.Properties, entries, "Id", showInfo);
        return e;
    }

    /// <summary>Makes a grid column show names from <paramref name="entries"/> while storing their IDs.</summary>
    public static void LookupColumn(GridControl grid, GridView view, string field, List<NameEntry> entries, bool showInfo = false)
    {
        var col = view.Columns[field];
        if (col == null) return;
        var repo = new RepositoryItemLookUpEdit();
        SetupLookup(repo, entries, "Id", showInfo);
        grid.RepositoryItems.Add(repo);
        col.ColumnEdit = repo;
    }

    public static SpinEdit WholeNumber(int min = int.MinValue, int max = int.MaxValue)
    {
        var spin = new SpinEdit();
        spin.Properties.IsFloatValue = false;
        spin.Properties.Mask.EditMask = "N0";
        spin.Properties.Mask.UseMaskAsDisplayFormat = true;
        spin.Properties.MinValue = min;
        spin.Properties.MaxValue = max;
        return spin;
    }

    // ---- grids ------------------------------------------------------------------

    public static (GridControl grid, GridView view) Grid(object dataSource, bool allowAdd = false, bool find = true)
    {
        var grid = new GridControl { Dock = DockStyle.Fill, BindingContext = new BindingContext() };
        var view = new GridView(grid);
        grid.MainView = view;
        view.OptionsView.ShowGroupPanel = false;
        view.OptionsView.ShowAutoFilterRow = false;
        view.OptionsView.ColumnAutoWidth = true;     // columns share the width: no sideways scroll bar
        view.OptionsView.ShowIndicator = false;
        view.OptionsView.EnableAppearanceEvenRow = true;
        view.OptionsFind.AlwaysVisible = find;
        view.OptionsFind.FindNullPrompt = "Type here to search...";
        view.OptionsFind.ShowFindButton = false;
        view.OptionsFind.ShowClearButton = false;
        view.OptionsFind.ShowCloseButton = false;
        view.OptionsSelection.MultiSelect = true;
        view.OptionsBehavior.AutoPopulateColumns = true;
        view.OptionsCustomization.AllowGroup = false;
        view.OptionsMenu.EnableColumnMenu = false;
        view.RowHeight = GridRowHeight;
        view.ColumnPanelRowHeight = GridRowHeight + 4;
        if (allowAdd) view.OptionsView.NewItemRowPosition = NewItemRowPosition.Top;
        grid.DataSource = dataSource;
        grid.ForceInitialize();
        return (grid, view);
    }

    /// <summary>Sets a column's heading, relative width and position in one go.</summary>
    public static void Column(GridView view, string field, string caption, int width, int index, bool editable = true)
    {
        var col = view.Columns[field];
        if (col == null) return;
        col.Caption = caption;
        col.Width = width;
        col.VisibleIndex = index;
        col.OptionsColumn.AllowEdit = editable;
        col.OptionsColumn.AllowFocus = editable;
    }

    public static void Hide(GridView view, params string[] fields)
    {
        foreach (var f in fields)
            if (view.Columns[f] != null) view.Columns[f].Visible = false;
    }

    public static void ReadOnly(GridView view, params string[] fields)
    {
        foreach (var f in fields)
            if (view.Columns[f] != null) view.Columns[f].OptionsColumn.AllowEdit = false;
    }

    /// <summary>Data rows currently shown by the view (after find/filter), in display order.</summary>
    public static List<DataRow> ShownRows(GridView view)
    {
        var rows = new List<DataRow>(view.DataRowCount);
        for (int i = 0; i < view.DataRowCount; i++)
        {
            var r = view.GetDataRow(i);
            if (r != null) rows.Add(r);
        }
        return rows;
    }

    /// <summary>Text of a GVAS string value (string, wide string or null).</summary>
    public static string Text(object? v) => v switch
    {
        null => "",
        GvasWideString w => w.Value,
        _ => v.ToString() ?? "",
    };

    /// <summary>Like <see cref="Columns"/> but the columns share the width in the given proportions.</summary>
    public static Control WeightedColumns(double[] weights, params Control[] columns)
    {
        var page = Panel(DockStyle.Fill);
        foreach (var c in columns) { c.Dock = DockStyle.None; page.Controls.Add(c); }
        void Arrange()
        {
            double total = weights.Sum();
            int x = 0;
            for (int i = 0; i < columns.Length; i++)
            {
                int w = (int)(page.ClientSize.Width * weights[i] / total);
                columns[i].Bounds = new Rectangle(x, 0, Math.Max(200, w), Math.Max(100, page.ClientSize.Height));
                x += w;
            }
        }
        page.Resize += (_, _) => Arrange();
        Arrange();
        return page;
    }

    /// <summary>Lays out panels side by side, sharing the width equally up to a comfortable maximum.</summary>
    public static Control Columns(int maxColumnWidth, params Control[] columns)
    {
        var page = Panel(DockStyle.Fill);
        foreach (var c in columns) { c.Dock = DockStyle.None; page.Controls.Add(c); }
        void Arrange()
        {
            int w = Math.Min(maxColumnWidth, page.ClientSize.Width / columns.Length);
            for (int i = 0; i < columns.Length; i++)
                columns[i].Bounds = new Rectangle(i * w, 0, Math.Max(200, w), Math.Max(100, page.ClientSize.Height));
        }
        page.Resize += (_, _) => Arrange();
        Arrange();
        return page;
    }
}
