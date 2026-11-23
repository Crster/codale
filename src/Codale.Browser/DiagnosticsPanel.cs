using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Codale.Browser;

/// <summary>
/// The app's shell colours (Codale.App/App.xaml, Themes/ChatPalette.xaml) flattened to opaque
/// values, since WinForms controls cannot blend alpha brushes over their parent.
/// </summary>
internal sealed record DiagnosticsPalette(
    Color Surface, Color Header, Color Hairline, Color Hover, Color Selected, Color Input, Color InputStroke,
    Color Text, Color TextSecondary, Color TextTertiary, Color Accent,
    Color Success, Color Failed, Color Warn, Color Running, Color Muted,
    Color Read, Color Edit, Color Write, Color Shell, Color Agent, Color Ask)
{
    private static Color C(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));

    public static readonly DiagnosticsPalette Dark = new(
        Surface: C(0x1B1C20), Header: C(0x17181B), Hairline: C(0x2C2D31), Hover: C(0x25262A), Selected: C(0x2D2E33),
        Input: C(0x232427), InputStroke: C(0x333437),
        Text: C(0xF3F3F4), TextSecondary: C(0xCBCBCC), TextTertiary: C(0x949496), Accent: C(0x818CF8),
        Success: C(0x34D399), Failed: C(0xFB7185), Warn: C(0xFBBF24), Running: C(0x38BDF8), Muted: C(0x94A3B8),
        Read: C(0x38BDF8), Edit: C(0xFBBF24), Write: C(0x34D399), Shell: C(0xA78BFA), Agent: C(0xFB923C), Ask: C(0x818CF8));

    public static readonly DiagnosticsPalette Light = new(
        Surface: C(0xFFFFFF), Header: C(0xF6F7F9), Hairline: C(0xE3E6EA), Hover: C(0xF5F5F5), Selected: C(0xEDEEF0),
        Input: C(0xFFFFFF), InputStroke: C(0xD9DCE1),
        Text: C(0x1B1B1B), TextSecondary: C(0x616161), TextTertiary: C(0x8D8D8D), Accent: C(0x4F46E5),
        Success: C(0x059669), Failed: C(0xE11D48), Warn: C(0xD97706), Running: C(0x0284C7), Muted: C(0x64748B),
        Read: C(0x0284C7), Edit: C(0xD97706), Write: C(0x059669), Shell: C(0x7C3AED), Agent: C(0xEA580C), Ask: C(0x4F46E5));

    /// <summary>A hue laid over the surface at the given strength, for row tints.</summary>
    public Color Tint(Color hue, double amount) => Color.FromArgb(
        (int)(Surface.R + (hue.R - Surface.R) * amount),
        (int)(Surface.G + (hue.G - Surface.G) * amount),
        (int)(Surface.B + (hue.B - Surface.B) * amount));
}

/// <summary>A flat, owner-drawn grid: hairline rows, quiet header, hover and selection like the app's lists.</summary>
internal sealed class DiagnosticsGrid : DataGridView
{
    public int HoverRow = -1;
    public string EmptyText = "";
    public Color EmptyColor = Color.Gray;

    public DiagnosticsGrid()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Fill; BorderStyle = BorderStyle.None; ReadOnly = true; AllowUserToAddRows = false;
        AllowUserToDeleteRows = false; AllowUserToResizeRows = false; RowHeadersVisible = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect; MultiSelect = false;
        AutoGenerateColumns = false; EnableHeadersVisualStyles = false; RowTemplate.Height = 26;
        ColumnHeadersHeight = 28; ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        CellBorderStyle = DataGridViewCellBorderStyle.None; ShowCellToolTips = true;
        Font = DiagnosticsPanel.UiFont;
    }

    /// <summary>
    /// DataGridView keeps a row selected on its own as soon as it has one; the selection is only
    /// drawn once the user has clicked or keyed into the grid, so new traffic never looks picked.
    /// </summary>
    public bool Picked;

    protected override void OnCellMouseDown(DataGridViewCellMouseEventArgs e) { Picked = true; base.OnCellMouseDown(e); }
    protected override void OnKeyDown(KeyEventArgs e) { Picked = true; base.OnKeyDown(e); }

    protected override void OnCellMouseEnter(DataGridViewCellEventArgs e)
    {
        base.OnCellMouseEnter(e);
        if (e.RowIndex == HoverRow) return;
        var old = HoverRow; HoverRow = e.RowIndex;
        Repaint(old); Repaint(HoverRow);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        var old = HoverRow; HoverRow = -1; Repaint(old);
    }

    private void Repaint(int row) { if (row >= 0 && row < Rows.Count) InvalidateRow(row); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (EmptyText.Length == 0 || Rows.GetRowCount(DataGridViewElementStates.Visible) > 0) return;
        var area = new Rectangle(0, ColumnHeadersHeight, ClientSize.Width, Math.Max(0, ClientSize.Height - ColumnHeadersHeight));
        TextRenderer.DrawText(e.Graphics, EmptyText, Font, area, EmptyColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>In-memory diagnostics for the current WebView. CDP event callbacks arrive on the UI thread.</summary>
internal sealed class DiagnosticsPanel : Panel
{
    private const int Limit = 500;
    private const int CellPadding = 10;
    private const TextFormatFlags CellText = TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    internal static readonly Font UiFont = new("Segoe UI", 9f);
    private static readonly Font LabelFont = new("Segoe UI Semibold", 7.5f);
    private static readonly Font PillFont = new("Segoe UI Semibold", 7.5f);
    private static readonly Font TabFont = new("Segoe UI Semibold", 9f);
    private static readonly Font IconFont = new("Segoe MDL2 Assets", 9f);
    private static readonly Font MonoFont = Mono(9f);

    private readonly DiagnosticsGrid _network = new() { EmptyText = "No requests yet. Load a page to see its traffic." };
    private readonly DiagnosticsGrid _console = new() { EmptyText = "No console messages." };
    private readonly SplitContainer _split = new() { Size = new Size(900, 310), Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, Panel2MinSize = 220, SplitterWidth = 4, SplitterDistance = 550 };
    private readonly Panel _topLine = new() { Dock = DockStyle.Top, Height = 1 };
    private readonly TableLayoutPanel _header = new() { Dock = DockStyle.Top, Height = 38, ColumnCount = 6, RowCount = 1, Padding = new Padding(12, 0, 6, 0), Margin = Padding.Empty };
    private readonly Label _title = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = LabelFont, Margin = Padding.Empty };
    private readonly Label _count = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = UiFont, Margin = new Padding(6, 0, 0, 0) };
    private readonly Panel _filterBox = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 6, 6) };
    private readonly Label _filterIcon = new() { Text = "", Font = IconFont, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Width = 28, Dock = DockStyle.Left };
    private readonly TextBox _filter = new() { BorderStyle = BorderStyle.None, Font = UiFont, PlaceholderText = "Filter" };
    private readonly Button _clear;
    private readonly Button _close;
    private readonly Panel _detailLine = new() { Dock = DockStyle.Left, Width = 1 };
    private readonly Panel _detailHeader = new() { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 6, 6, 6) };
    private readonly Button _requestTab;
    private readonly Button _responseTab;
    private readonly Button _detailClose;
    private readonly Panel _detailBody = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 0, 0) };
    private readonly TextBox _detail = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = MonoFont };
    private readonly Dictionary<string, Entry> _active = [];
    private CoreWebView2? _core;
    private string _mode = "requests";
    private bool _showResponse;
    private string _requestText = "", _responseText = "";
    private DiagnosticsPalette _p = DiagnosticsPalette.Light;
    private bool _dark;

    private sealed class Entry
    {
        public string Id = "", Url = "", Method = "", Type = "", Mime = "", Status = "", Error = "";
        public string RequestHeaders = "", ResponseHeaders = "", RequestBody = "", ResponseBody = "";
        public double Start, End;
        public long? Size;
        public bool Finished, BodyLoaded, IsRedirect;
        public DataGridViewRow? Row;
    }

    private sealed record ConsoleEntry(string Level, string Message, DateTime Time);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);

    public DiagnosticsPanel()
    {
        Dock = DockStyle.Bottom;
        Height = 310;
        Visible = false;

        _network.Columns.AddRange(Column("Status", 74), Column("Method", 70), Column("Path", 140, fill: true), Column("Type", 150), Column("Time", 78), Column("Size", 78));
        _console.Columns.AddRange(Column("Time", 112), Column("Level", 82), Column("Message", 150, fill: true));
        foreach (var grid in new[] { _network, _console })
        {
            grid.CellPainting += PaintCell;
            grid.SelectionChanged += (_, _) => grid.Invalidate();
        }

        _split.Panel2Collapsed = true;
        _network.SelectionChanged += async (_, _) => { if (!_split.Panel2Collapsed) await ShowSelectedAsync(); };
        _network.CellClick += async (_, e) =>
        {
            if (e.RowIndex < 0) return;
            _split.Panel2Collapsed = false;
            if (_split.Width > 500) _split.SplitterDistance = Math.Max(200, _split.Width - 380);
            await ShowSelectedAsync();
        };

        _clear = IconButton("", "Clear", (_, _) => Clear());
        _close = IconButton("", "Close panel", (_, _) => Visible = false);
        _header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        _header.Controls.Add(_title, 0, 0);
        _header.Controls.Add(_count, 1, 0);
        _header.Controls.Add(_filterBox, 3, 0);
        _header.Controls.Add(_clear, 4, 0);
        _header.Controls.Add(_close, 5, 0);

        _filterBox.Controls.Add(_filter);
        _filterBox.Controls.Add(_filterIcon);
        _filterBox.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, _filterBox.Width - 1, _filterBox.Height - 1);
            using var path = Rounded(bounds, 6);
            using var fill = new SolidBrush(_p.Input);
            using var stroke = new Pen(_p.InputStroke);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(stroke, path);
        };
        _filterBox.Resize += (_, _) => _filter.SetBounds(28, Math.Max(0, (_filterBox.ClientSize.Height - _filter.PreferredHeight) / 2), Math.Max(0, _filterBox.ClientSize.Width - 36), _filter.PreferredHeight);
        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { _filter.Clear(); e.SuppressKeyPress = true; } };

        _requestTab = TabButton("Request", false);
        _responseTab = TabButton("Response", true);
        _detailClose = IconButton("", "Close details", (_, _) => _split.Panel2Collapsed = true);
        _detailClose.Dock = DockStyle.Right;
        _detailClose.Width = 28;
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
        tabs.Controls.Add(_requestTab);
        tabs.Controls.Add(_responseTab);
        _detailHeader.Controls.Add(tabs);
        _detailHeader.Controls.Add(_detailClose);
        _detailBody.Controls.Add(_detail);
        _split.Panel2.Controls.Add(_detailBody);
        _split.Panel2.Controls.Add(_detailHeader);
        _split.Panel2.Controls.Add(_detailLine);
        _split.Panel1.Controls.Add(_network);

        Controls.Add(_split);
        Controls.Add(_console);
        Controls.Add(_header);
        Controls.Add(_topLine);
        _console.Visible = false;
        _split.Resize += (_, _) => { if (_split.Width > 500 && _split.SplitterDistance > _split.Width - 220) _split.SplitterDistance = _split.Width - 220; };

        foreach (var control in new Control[] { _network, _console, _detail }) control.HandleCreated += (_, _) => ThemeScrollbars(control);
        ApplyTheme(false);
        UpdateHeader();
    }

    private static Font Mono(float size)
    {
        using var families = new System.Drawing.Text.InstalledFontCollection();
        var name = families.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas";
        return new Font(name, size);
    }

    private static DataGridViewTextBoxColumn Column(string name, int width, bool fill = false) => new()
    {
        Name = name, HeaderText = name, Width = width, MinimumWidth = fill ? width : 40,
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    private Button IconButton(string glyph, string label, EventHandler action)
    {
        var button = new Button { Text = glyph, AccessibleName = label, Font = IconFont, FlatStyle = FlatStyle.Flat, Size = new Size(28, 28), Anchor = AnchorStyles.None, Margin = Padding.Empty, Cursor = Cursors.Hand, TabStop = true };
        button.FlatAppearance.BorderSize = 0;
        button.Click += action;
        new ToolTip().SetToolTip(button, label);
        return button;
    }

    private Button TabButton(string text, bool response)
    {
        var button = new Button { Text = text, Font = TabFont, FlatStyle = FlatStyle.Flat, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 26, MinimumSize = new Size(0, 26), Padding = new Padding(6, 0, 6, 0), Margin = new Padding(0, 0, 4, 0), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => { _showResponse = response; RenderDetail(); };
        return button;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        Listen(core, "Network.requestWillBeSent", Request);
        Listen(core, "Network.responseReceived", Response);
        Listen(core, "Network.loadingFinished", Finished);
        Listen(core, "Network.loadingFailed", Failed);
        Listen(core, "Runtime.consoleAPICalled", ConsoleMessage);
        Listen(core, "Runtime.exceptionThrown", ConsoleException);
    }

    private static void Listen(CoreWebView2 core, string name, Action<JsonElement> action)
    {
        core.GetDevToolsProtocolEventReceiver(name).DevToolsProtocolEventReceived += (_, e) =>
        {
            try { using var json = JsonDocument.Parse(e.ParameterObjectAsJson); action(json.RootElement); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException) { System.Diagnostics.Debug.WriteLine(ex); }
        };
    }

    public void Toggle(string mode) => Show(Visible && _mode == mode ? null : mode);

    /// <summary>Shows the console or requests view, or hides the panel when <paramref name="mode"/> is null.</summary>
    public void Show(string? mode)
    {
        if (mode is null) { Visible = false; return; }
        _mode = mode;
        _split.Visible = mode == "requests";
        _console.Visible = mode == "console";
        Visible = true;
        if (_split.Visible && _split.Width > 500) _split.SplitterDistance = Math.Max(200, _split.Width - 380);
        ApplyFilter();
    }

    private DiagnosticsGrid ActiveGrid => _mode == "console" ? _console : _network;

    private void UpdateHeader()
    {
        _title.Text = _mode == "console" ? "CONSOLE" : "REQUESTS";
        var grid = ActiveGrid;
        var total = grid.Rows.Count;
        var shown = grid.Rows.GetRowCount(DataGridViewElementStates.Visible);
        _count.Text = total == 0 ? "" : shown == total ? total.ToString() : $"{shown} of {total}";
    }

    private void Clear()
    {
        var grid = ActiveGrid;
        grid.Rows.Clear();
        grid.Picked = false;
        if (grid == _network)
        {
            _active.Clear();
            _split.Panel2Collapsed = true;
            _requestText = _responseText = "";
            RenderDetail();
        }
        UpdateHeader();
    }

    private bool Matches(DataGridViewRow row)
    {
        var filter = _filter.Text.Trim();
        if (filter.Length == 0) return true;
        var text = row.Tag switch
        {
            Entry entry => $"{entry.Status} {entry.Error} {entry.Method} {entry.Url} {entry.Mime} {entry.Type}",
            ConsoleEntry log => $"{log.Level} {log.Message}",
            _ => "",
        };
        return text.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        var grid = ActiveGrid;
        grid.CurrentCell = null;
        foreach (DataGridViewRow row in grid.Rows) row.Visible = Matches(row);
        UpdateHeader();
        grid.Invalidate();
    }

    private void Added(DiagnosticsGrid grid, DataGridViewRow row)
    {
        if (!Matches(row)) row.Visible = false;
        if (grid == ActiveGrid) UpdateHeader();
    }

    private static string Value(JsonElement obj, string key) => obj.TryGetProperty(key, out var p) && p.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? p.ToString() : "";
    private static double Number(JsonElement obj, string key) => obj.TryGetProperty(key, out var p) && p.TryGetDouble(out var n) ? n : 0;
    private static string Headers(JsonElement obj) => obj.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object ? string.Join(Environment.NewLine, h.EnumerateObject().Select(x => $"{x.Name}: {x.Value}")) : "(not available)";

    private void Request(JsonElement e)
    {
        var id = Value(e, "requestId");
        if (e.TryGetProperty("redirectResponse", out var redirect) && _active.TryGetValue(id, out var previous))
        {
            SetResponse(previous, redirect);
            previous.End = Number(e, "timestamp"); previous.Finished = true; previous.IsRedirect = true;
            UpdateRow(previous);
        }
        var request = e.GetProperty("request");
        var entry = new Entry { Id = id, Url = Value(request, "url"), Method = Value(request, "method"), Type = Value(e, "type"), Start = Number(e, "timestamp"), RequestHeaders = Headers(request), RequestBody = Value(request, "postData") };
        _active[id] = entry;
        var index = _network.Rows.Add();
        entry.Row = _network.Rows[index]; entry.Row.Tag = entry;
        UpdateRow(entry);
        if (_network.Rows.Count > Limit)
        {
            var oldest = _network.Rows[0].Tag as Entry;
            if (oldest is not null && _active.TryGetValue(oldest.Id, out var active) && ReferenceEquals(oldest, active)) _active.Remove(oldest.Id);
            _network.Rows.RemoveAt(0);
        }
        Added(_network, entry.Row);
    }

    private static void SetResponse(Entry entry, JsonElement response)
    {
        entry.Status = Value(response, "status"); entry.Mime = Value(response, "mimeType"); entry.ResponseHeaders = Headers(response);
    }
    private void Response(JsonElement e)
    {
        if (!_active.TryGetValue(Value(e, "requestId"), out var entry)) return;
        SetResponse(entry, e.GetProperty("response"));
        entry.Type = Value(e, "type"); UpdateRow(entry);
    }
    private void Finished(JsonElement e)
    {
        if (!_active.TryGetValue(Value(e, "requestId"), out var entry)) return;
        entry.End = Number(e, "timestamp"); entry.Size = (long)Number(e, "encodedDataLength"); entry.Finished = true; UpdateRow(entry);
        if (!_split.Panel2Collapsed && Selected() == entry) _ = ShowSelectedAsync();
    }
    private void Failed(JsonElement e)
    {
        if (!_active.TryGetValue(Value(e, "requestId"), out var entry)) return;
        entry.End = Number(e, "timestamp"); entry.Error = Value(e, "errorText"); entry.Finished = true; UpdateRow(entry);
        if (!_split.Panel2Collapsed && Selected() == entry) _ = ShowSelectedAsync();
    }
    private string DisplayUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var target) && Uri.TryCreate(_core?.Source, UriKind.Absolute, out var page) && target.Scheme == page.Scheme && target.Host == page.Host && target.Port == page.Port)
            return target.PathAndQuery;
        return url;
    }
    private void UpdateRow(Entry entry)
    {
        if (entry.Row is not { DataGridView: not null } row) return;
        row.SetValues(StatusText(entry), entry.Method, DisplayUrl(entry.Url), entry.Mime.Length > 0 ? entry.Mime : entry.Type, entry.Finished && entry.End >= entry.Start ? $"{(entry.End - entry.Start) * 1000:0} ms" : "…", entry.Size is { } bytes ? FormatSize(bytes) : "—");
        row.Cells[2].ToolTipText = entry.Url;
        row.Cells[0].ToolTipText = entry.Error;
    }
    private static string StatusText(Entry entry) => entry.Error.Length > 0 ? "Failed" : entry.Status.Length > 0 ? entry.Status : "Pending";
    private Color StatusColor(Entry entry)
    {
        if (entry.Error.Length > 0) return _p.Failed;
        if (!int.TryParse(entry.Status, out var status)) return _p.Running;
        return status >= 400 ? _p.Failed : status >= 300 ? _p.Warn : _p.Success;
    }
    private Color ResourceColor(string type, string mime)
    {
        var text = (type + " " + mime).ToLowerInvariant();
        if (text.Contains("javascript") || text.Contains("script")) return _p.Edit;
        if (text.Contains("css") || text.Contains("stylesheet")) return _p.Shell;
        if (text.Contains("image")) return _p.Write;
        if (text.Contains("json") || text.Contains("fetch") || text.Contains("xhr")) return _p.Read;
        if (text.Contains("font")) return _p.Agent;
        if (text.Contains("html") || text.Contains("document")) return _p.Ask;
        return _p.Muted;
    }
    private Color LevelColor(string level) => level switch
    {
        "error" or "assert" => _p.Failed,
        "warning" => _p.Warn,
        "info" => _p.Running,
        _ => _p.Muted,
    };
    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / 1048576d:0.0} MB";
    private Entry? Selected() => _network.SelectedRows.Count == 0 ? null : _network.SelectedRows[0].Tag as Entry;

    private void PaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (sender is not DiagnosticsGrid grid || e.Graphics is not { } g || e.ColumnIndex < 0) return;
        var bounds = e.CellBounds;
        var column = grid.Columns[e.ColumnIndex];
        var rightAligned = column.Name is "Time" or "Size" && grid == _network;
        using var hairline = new Pen(_p.Hairline);

        if (e.RowIndex < 0)
        {
            using var headerBack = new SolidBrush(_p.Surface);
            g.FillRectangle(headerBack, bounds);
            g.DrawLine(hairline, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            var label = Rectangle.FromLTRB(bounds.Left + CellPadding, bounds.Top, bounds.Right - CellPadding, bounds.Bottom - 1);
            TextRenderer.DrawText(g, column.HeaderText.ToUpperInvariant(), LabelFont, label, _p.TextTertiary, CellText | (rightAligned ? TextFormatFlags.Right : 0));
            e.Handled = true;
            return;
        }

        var row = grid.Rows[e.RowIndex];
        var log = row.Tag as ConsoleEntry;
        var selected = row.Selected && grid.Picked;
        var back = selected ? _p.Selected
            : e.RowIndex == grid.HoverRow ? _p.Hover
            : log is { Level: "error" or "assert" } ? _p.Tint(_p.Failed, _dark ? 0.08 : 0.05)
            : log is { Level: "warning" } ? _p.Tint(_p.Warn, _dark ? 0.08 : 0.06)
            : _p.Surface;
        using (var fill = new SolidBrush(back)) g.FillRectangle(fill, bounds);
        g.DrawLine(hairline, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        if (e.ColumnIndex == 0 && (selected || log is { Level: "error" or "assert" or "warning" }))
        {
            using var marker = new SolidBrush(selected ? _p.Accent : LevelColor(log!.Level));
            g.FillRectangle(marker, bounds.Left, bounds.Top, 2, bounds.Height - 1);
        }

        var content = Rectangle.FromLTRB(bounds.Left + CellPadding, bounds.Top, bounds.Right - CellPadding, bounds.Bottom - 1);
        var text = e.FormattedValue?.ToString() ?? "";
        if (row.Tag is Entry entry)
        {
            switch (column.Name)
            {
                case "Status": Pill(g, content, text, StatusColor(entry)); break;
                case "Method": TextRenderer.DrawText(g, text, MonoFont, content, _p.TextSecondary, CellText); break;
                case "Path": TextRenderer.DrawText(g, text, UiFont, content, entry.Error.Length > 0 ? _p.Failed : _p.Text, CellText); break;
                case "Type":
                    var hue = ResourceColor(entry.Type, entry.Mime);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var dot = new SolidBrush(hue)) g.FillEllipse(dot, content.Left, content.Top + content.Height / 2 - 3, 6, 6);
                    g.SmoothingMode = SmoothingMode.Default;
                    TextRenderer.DrawText(g, text, UiFont, Rectangle.FromLTRB(content.Left + 12, content.Top, content.Right, content.Bottom), _p.TextSecondary, CellText);
                    break;
                default: TextRenderer.DrawText(g, text, UiFont, content, _p.TextTertiary, CellText | (rightAligned ? TextFormatFlags.Right : 0)); break;
            }
        }
        else if (log is not null)
        {
            switch (column.Name)
            {
                case "Time": TextRenderer.DrawText(g, text, MonoFont, content, _p.TextTertiary, CellText); break;
                case "Level": Pill(g, content, text, LevelColor(log.Level)); break;
                default:
                    var color = log.Level is "error" or "assert" ? _p.Failed : log.Level == "warning" ? _p.Warn : _p.Text;
                    TextRenderer.DrawText(g, text, MonoFont, content, color, CellText);
                    break;
            }
        }
        e.Handled = true;
    }

    private void Pill(Graphics g, Rectangle content, string text, Color hue)
    {
        var size = TextRenderer.MeasureText(g, text, PillFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var pill = new Rectangle(content.Left, content.Top + (content.Height - 18) / 2, Math.Min(content.Width, size.Width + 14), 18);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Rounded(pill, 4))
        using (var tint = new SolidBrush(Color.FromArgb(_dark ? 40 : 28, hue)))
            g.FillPath(tint, path);
        g.SmoothingMode = SmoothingMode.Default;
        TextRenderer.DrawText(g, text, PillFont, pill, hue, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    private async Task ShowSelectedAsync()
    {
        var entry = Selected();
        if (entry is null) { _requestText = _responseText = ""; RenderDetail(); return; }
        var query = Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) && uri.Query.Length > 1
            ? string.Join(Environment.NewLine, uri.Query[1..].Split('&').Select(p => Uri.UnescapeDataString(p.Replace('+', ' ')))) : "(none)";
        _requestText = $"{entry.Method} {entry.Url}\r\n\r\nQUERY PARAMETERS\r\n{query}\r\n\r\nREQUEST HEADERS\r\n{entry.RequestHeaders}\r\n\r\nREQUEST BODY\r\n{(entry.RequestBody.Length > 0 ? entry.RequestBody : "(none or unavailable)")}";
        RenderDetail();
        if (entry.Finished && !entry.BodyLoaded && !entry.IsRedirect && entry.Error.Length == 0 && entry.Size <= 1024 * 1024 && IsText(entry.Mime) && _core is not null)
        {
            entry.BodyLoaded = true;
            try
            {
                var result = await _core.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId = entry.Id }));
                using var json = JsonDocument.Parse(result);
                var body = Value(json.RootElement, "body");
                if (json.RootElement.TryGetProperty("base64Encoded", out var encoded) && encoded.ValueKind == JsonValueKind.True) body = Encoding.UTF8.GetString(Convert.FromBase64String(body));
                entry.ResponseBody = body.Length > 100_000 ? body[..100_000] + "\r\n… (truncated)" : body;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or JsonException or InvalidOperationException or FormatException) { entry.ResponseBody = "(body unavailable)"; }
        }
        if (Selected() != entry) return;
        _responseText = $"STATUS\r\n{(entry.Error.Length > 0 ? entry.Error : entry.Status.Length > 0 ? entry.Status : "Pending")}\r\n\r\nRESPONSE HEADERS\r\n{entry.ResponseHeaders}\r\n\r\nRESPONSE BODY\r\n{(entry.ResponseBody.Length > 0 ? entry.ResponseBody : entry.Finished ? "(empty, binary, too large, or unavailable)" : "(pending)")}";
        RenderDetail();
    }
    private void RenderDetail()
    {
        var text = _showResponse ? _responseText : _requestText;
        if (_detail.Text != text) _detail.Text = text;
        foreach (var (tab, selected) in new[] { (_requestTab, !_showResponse), (_responseTab, _showResponse) })
        {
            tab.BackColor = selected ? _p.Selected : _p.Surface;
            tab.ForeColor = selected ? _p.Text : _p.TextTertiary;
            tab.FlatAppearance.MouseOverBackColor = selected ? _p.Selected : _p.Hover;
            tab.FlatAppearance.MouseDownBackColor = _p.Selected;
        }
    }
    private static bool IsText(string mime) => mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mime.Contains("json", StringComparison.OrdinalIgnoreCase) || mime.Contains("xml", StringComparison.OrdinalIgnoreCase) || mime.Contains("javascript", StringComparison.OrdinalIgnoreCase);

    private void ConsoleMessage(JsonElement e)
    {
        var level = Value(e, "type");
        var message = e.TryGetProperty("args", out var args) ? string.Join(" ", args.EnumerateArray().Select(a => a.TryGetProperty("value", out var v) ? v.ToString() : Value(a, "description"))) : "";
        AddConsole(level, message);
    }
    private void ConsoleException(JsonElement e)
    {
        var details = e.GetProperty("exceptionDetails");
        var message = details.TryGetProperty("exception", out var exception) ? Value(exception, "description") : Value(details, "text");
        AddConsole("error", message);
    }
    private void AddConsole(string level, string message)
    {
        var log = new ConsoleEntry(level, message, DateTime.Now);
        var label = level switch { "warning" => "Warn", "assert" => "Assert", "" => "Log", _ => char.ToUpperInvariant(level[0]) + level[1..] };
        var firstLine = message.ReplaceLineEndings(" ⏎ ");
        var index = _console.Rows.Add(log.Time.ToString("HH:mm:ss.fff"), label, firstLine);
        var row = _console.Rows[index];
        row.Tag = log;
        row.Cells[2].ToolTipText = message.Length > 2000 ? message[..2000] + "…" : message;
        if (_console.Rows.Count > Limit) _console.Rows.RemoveAt(0);
        Added(_console, row);
    }

    private void ThemeScrollbars(Control control)
    {
        if (!control.IsHandleCreated) return;
        var theme = _dark ? "DarkMode_Explorer" : "Explorer";
        SetWindowTheme(control.Handle, theme, null);
        foreach (Control child in control.Controls) if (child.IsHandleCreated) SetWindowTheme(child.Handle, theme, null);
    }

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        _p = dark ? DiagnosticsPalette.Dark : DiagnosticsPalette.Light;
        BackColor = _p.Surface;
        _topLine.BackColor = _detailLine.BackColor = _p.Hairline;
        _header.BackColor = _p.Header;
        _title.ForeColor = _p.TextTertiary;
        _count.ForeColor = _p.TextTertiary;
        _filterBox.BackColor = _p.Header;
        _filterIcon.BackColor = _filter.BackColor = _p.Input;
        _filterIcon.ForeColor = _p.TextTertiary;
        _filter.ForeColor = _p.Text;
        _filterBox.Invalidate();
        foreach (var button in new[] { _clear, _close, _detailClose })
        {
            var surface = button == _detailClose ? _p.Surface : _p.Header;
            button.BackColor = surface;
            button.ForeColor = _p.TextSecondary;
            button.FlatAppearance.MouseOverBackColor = _p.Selected;
            button.FlatAppearance.MouseDownBackColor = _p.Hairline;
        }
        foreach (var grid in new[] { _network, _console })
        {
            grid.BackgroundColor = _p.Surface; grid.GridColor = _p.Hairline;
            grid.DefaultCellStyle.BackColor = _p.Surface; grid.DefaultCellStyle.ForeColor = _p.Text;
            grid.DefaultCellStyle.SelectionBackColor = _p.Selected; grid.DefaultCellStyle.SelectionForeColor = _p.Text;
            grid.ColumnHeadersDefaultCellStyle.BackColor = _p.Surface; grid.ColumnHeadersDefaultCellStyle.ForeColor = _p.TextTertiary;
            grid.EmptyColor = _p.TextTertiary;
            grid.Invalidate();
        }
        _split.BackColor = _p.Surface;
        _split.Panel1.BackColor = _split.Panel2.BackColor = _p.Surface;
        _detailHeader.BackColor = _detailBody.BackColor = _p.Surface;
        _detail.BackColor = _p.Surface;
        _detail.ForeColor = _p.TextSecondary;
        foreach (var control in new Control[] { _network, _console, _detail }) ThemeScrollbars(control);
        RenderDetail();
    }
}
