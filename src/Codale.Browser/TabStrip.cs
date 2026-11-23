using System.Drawing.Drawing2D;

namespace Codale.Browser;

/// <summary>
/// The row of tabs above the toolbar. A tab an agent opened carries its session's colour - a bar
/// along the top, a tint and a dot - so each agent's page is told apart at a glance; the user's own
/// tabs stay plain. The selected tab takes the toolbar's colour, so it reads as joined to it.
/// </summary>
internal sealed class TabStrip : Control
{
    private const int Gap = 8;
    private const int Top_ = 0;
    private static readonly Font TitleFont = new("Segoe UI", 9f);
    private static readonly Font GlyphFont = new("Segoe MDL2 Assets", 7.5f);
    private static readonly Font PlusFont = new("Segoe MDL2 Assets", 9f);
    private const TextFormatFlags TitleFlags = TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private readonly ToolTip _tooltip = new() { InitialDelay = 600 };
    private IReadOnlyList<BrowserTab> _tabs = [];
    private BrowserTab? _active;
    private int _hover = -1;
    private bool _hoverClose, _hoverPlus;
    private bool _dark;
    private Color _bar, _back, _selected, _hoverBack, _text, _muted, _line, _closeHover;

    public event Action<BrowserTab>? Selected;
    public event Action<BrowserTab>? Closed;
    public event Action? NewTab;

    /// <summary>A left press on the empty part of the strip (not a tab or the + button): the window can be dragged from here.</summary>
    public event Action<MouseEventArgs>? EmptyAreaMouseDown;

    public TabStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 34;
        ApplyTheme(false);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<BrowserTab> Tabs { get => _tabs; set { _tabs = value; Invalidate(); } }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public BrowserTab? Active { get => _active; set { _active = value; Invalidate(); } }

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        // The empty part of the strip takes the toolbar's colour so the top reads as one bar; only inactive tabs keep their own shade.
        _bar = dark ? Color.FromArgb(31, 34, 40) : Color.White;
        _back = dark ? Color.FromArgb(23, 24, 27) : Color.FromArgb(233, 235, 238);
        _selected = dark ? Color.FromArgb(31, 34, 40) : Color.White;
        _hoverBack = dark ? Color.FromArgb(38, 41, 47) : Color.FromArgb(244, 245, 247);
        _text = dark ? Color.FromArgb(235, 238, 242) : Color.FromArgb(32, 36, 42);
        _muted = dark ? Color.FromArgb(150, 156, 166) : Color.FromArgb(98, 106, 117);
        _line = dark ? Color.FromArgb(48, 51, 58) : Color.FromArgb(210, 214, 220);
        _closeHover = dark ? Color.FromArgb(62, 67, 76) : Color.FromArgb(222, 225, 230);
        BackColor = _bar;
        Invalidate();
    }

    private int TabWidth => _tabs.Count == 0 ? 0 : Math.Clamp((Width - 2 * Gap - 40) / _tabs.Count, 72, 230);

    private Rectangle TabRect(int index) => new(Gap + index * TabWidth, Top_, TabWidth, Height - Top_);

    private static Rectangle CloseRect(Rectangle tab) => new(tab.Right - 23, tab.Top + (tab.Height - 16) / 2, 16, 16);

    private Rectangle PlusRect => new(Gap + _tabs.Count * TabWidth + 6, Top_ + (Height - Top_ - 24) / 2, 24, 24);

    private static GraphicsPath TopRounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_bar);
        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var r = TabRect(i);
            var active = tab == _active;
            var hovered = i == _hover;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = active ? _selected : hovered ? _hoverBack : _back;
            // Inactive tabs keep their shade; the rounded top corners show the bar behind them.
            using (var path = TopRounded(r, 6))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (tab.AgentColor is { } hue)
                {
                    using var tint = new SolidBrush(Color.FromArgb(active ? (_dark ? 26 : 18) : (_dark ? 40 : 34), hue));
                    g.FillPath(tint, path);
                }
            }

            if (tab.AgentColor is { } bar)
            {
                using var pen = new SolidBrush(bar);
                using var top = TopRounded(new Rectangle(r.X + 1, r.Y, r.Width - 2, 6), 3);
                g.SetClip(new Rectangle(r.X, r.Y, r.Width, 3));
                g.FillPath(pen, top);
                g.ResetClip();
            }

            var textLeft = r.X + 12;
            if (tab.AgentColor is { } dot)
            {
                using var brush = new SolidBrush(dot);
                g.FillEllipse(brush, r.X + 12, r.Y + (r.Height - 8) / 2, 8, 8);
                textLeft += 15;
            }

            g.SmoothingMode = SmoothingMode.Default;
            var close = CloseRect(r);
            var showClose = active || hovered;
            var title = Rectangle.FromLTRB(textLeft, r.Top, (showClose ? close.Left : r.Right) - 6, r.Bottom);
            TextRenderer.DrawText(g, tab.Title, TitleFont, title, active ? _text : _muted, TitleFlags);

            if (showClose)
            {
                if (hovered && _hoverClose)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using var back = new SolidBrush(_closeHover);
                    g.FillEllipse(back, close);
                    g.SmoothingMode = SmoothingMode.Default;
                }

                TextRenderer.DrawText(g, "", GlyphFont, close, _muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // A divider between two plain neighbours; selected, hovered and coloured tabs stand on their own.
            var next = i + 1 < _tabs.Count ? _tabs[i + 1] : null;
            if (!active && !hovered && next is not null && next != _active && i + 1 != _hover)
            {
                using var line = new Pen(_line);
                g.DrawLine(line, r.Right - 1, r.Top + 9, r.Right - 1, r.Bottom - 9);
            }
        }

        var plus = PlusRect;
        if (_hoverPlus)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var back = new SolidBrush(_hoverBack);
            using var path = new GraphicsPath();
            path.AddEllipse(plus);
            g.FillPath(back, path);
            g.SmoothingMode = SmoothingMode.Default;
        }

        TextRenderer.DrawText(g, "", PlusFont, plus, _muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private int HitTab(Point p)
    {
        for (var i = 0; i < _tabs.Count; i++) if (TabRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = HitTab(e.Location);
        var hoverClose = hover >= 0 && CloseRect(TabRect(hover)).Contains(e.Location);
        var hoverPlus = PlusRect.Contains(e.Location);
        if (hover == _hover && hoverClose == _hoverClose && hoverPlus == _hoverPlus) return;
        if (hover != _hover)
        {
            var tab = hover >= 0 ? _tabs[hover] : null;
            _tooltip.SetToolTip(this, tab is null ? null : tab.IsAgent ? $"{tab.AgentLabel}\n{tab.Title}" : tab.Title);
        }

        _hover = hover; _hoverClose = hoverClose; _hoverPlus = hoverPlus;
        Cursor = hover >= 0 || hoverPlus ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1; _hoverClose = _hoverPlus = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HitTab(e.Location) < 0 && !PlusRect.Contains(e.Location)) EmptyAreaMouseDown?.Invoke(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && PlusRect.Contains(e.Location)) { NewTab?.Invoke(); return; }
        var index = HitTab(e.Location);
        if (index < 0) return;
        var tab = _tabs[index];
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && CloseRect(TabRect(index)).Contains(e.Location))) Closed?.Invoke(tab);
        else if (e.Button == MouseButtons.Left) Selected?.Invoke(tab);
    }
}
