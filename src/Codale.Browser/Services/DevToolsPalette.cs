using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using Windows.UI;
using Windows.UI.Text;

namespace Codale.Browser;

/// <summary>
/// The colours of the Network and Console panel, in a light and a dark set. The window sets <see cref="IsDark"/> from
/// its theme and rebuilds the panel rows when the theme changes, so every brush is read for the current theme.
/// </summary>
public static class DevToolsPalette
{
    private static readonly Dictionary<(uint, bool), SolidColorBrush> Brushes = [];

    public static bool IsDark { get; set; } = true;

    public static SolidColorBrush Brush(uint dark, uint light)
    {
        var argb = IsDark ? dark : light;
        if (!Brushes.TryGetValue((argb, IsDark), out var brush))
        {
            brush = new SolidColorBrush(FromArgb(argb));
            Brushes[(argb, IsDark)] = brush;
        }

        return brush;
    }

    /// <summary>A colour from a full <c>0xAARRGGBB</c> value (the alpha is always given, so 0x00000000 is transparent).</summary>
    public static Color FromArgb(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static Color ColorOf(uint dark, uint light) => FromArgb(IsDark ? dark : light);

    public static SolidColorBrush Transparent => Brush(0x00000000, 0x00000000);

    // ---- Network ---------------------------------------------------------------------

    /// <summary>The colour of an HTTP verb: GET blue, POST green, PUT orange, PATCH purple, DELETE red, the rest grey.</summary>
    public static SolidColorBrush Method(string method) => method.ToUpperInvariant() switch
    {
        "GET" => Brush(0xFF60A5FA, 0xFF2563EB),
        "POST" => Brush(0xFF4ADE80, 0xFF15803D),
        "PUT" => Brush(0xFFFB923C, 0xFFC2410C),
        "PATCH" => Brush(0xFFC084FC, 0xFF7E22CE),
        "DELETE" => Brush(0xFFF87171, 0xFFB91C1C),
        _ => Brush(0xFF94A3B8, 0xFF64748B),
    };

    /// <summary>The colour of a status: 2xx green, 3xx cyan, 4xx orange, 5xx and failures red, pending grey.</summary>
    public static SolidColorBrush Status(int? status, bool failed) => status switch
    {
        _ when failed && status is null or < 400 => Brush(0xFFF87171, 0xFFB91C1C),
        null => Brush(0xFF94A3B8, 0xFF64748B),
        < 200 => Brush(0xFF94A3B8, 0xFF64748B),
        < 300 => Brush(0xFF4ADE80, 0xFF15803D),
        < 400 => Brush(0xFF22D3EE, 0xFF0E7490),
        < 500 => Brush(0xFFFB923C, 0xFFC2410C),
        _ => Brush(0xFFF87171, 0xFFB91C1C),
    };

    /// <summary>The row tint: a faint red for failed and 4xx/5xx requests, nothing otherwise.</summary>
    public static SolidColorBrush RequestRow(int? status, bool failed) =>
        failed || status >= 500 ? Brush(0x2EF87171, 0x18DC2626)
        : status >= 400 ? Brush(0x1FFB923C, 0x14EA580C)
        : Transparent;

    // ---- Console ---------------------------------------------------------------------

    public static SolidColorBrush ConsoleText(ConsoleLevel level) => level switch
    {
        ConsoleLevel.Error => Brush(0xFFFF8A80, 0xFFC5221F),
        ConsoleLevel.Warning => Brush(0xFFFDD663, 0xFF7A4F01),
        ConsoleLevel.Info => Brush(0xFF8AB4F8, 0xFF1A56C4),
        ConsoleLevel.Debug => Brush(0xFF9AA0A6, 0xFF5F6368),
        _ => Brush(0xFFE3E3E3, 0xFF1F1F1F),
    };

    public static SolidColorBrush ConsoleIcon(ConsoleLevel level) => level switch
    {
        ConsoleLevel.Error => Brush(0xFFF28B82, 0xFFD93025),
        ConsoleLevel.Warning => Brush(0xFFFDD663, 0xFFE37400),
        ConsoleLevel.Info => Brush(0xFF8AB4F8, 0xFF1A73E8),
        _ => Brush(0xFF9AA0A6, 0xFF80868B),
    };

    public static SolidColorBrush ConsoleRow(ConsoleLevel level) => level switch
    {
        ConsoleLevel.Error => Brush(0x2EF28B82, 0xFFFDEDED),
        ConsoleLevel.Warning => Brush(0x24FDD663, 0xFFFEF7E0),
        _ => Transparent,
    };

    public static SolidColorBrush ConsoleDivider(ConsoleLevel level) => level switch
    {
        ConsoleLevel.Error => Brush(0x55F28B82, 0xFFF9D2D0),
        ConsoleLevel.Warning => Brush(0x4DFDD663, 0xFFF6E3A6),
        _ => Brush(0x1FFFFFFF, 0x14000000),
    };

    // ---- ANSI ------------------------------------------------------------------------

    // The 16 terminal colours: black, red, green, yellow, blue, magenta, cyan, white, then the bright set.
    private static readonly uint[] AnsiDark =
    [
        0xFF6E7681, 0xFFFF7B72, 0xFF3FB950, 0xFFD29922, 0xFF58A6FF, 0xFFBC8CFF, 0xFF39C5CF, 0xFFD0D7DE,
        0xFF8B949E, 0xFFFFA198, 0xFF56D364, 0xFFE3B341, 0xFF79C0FF, 0xFFD2A8FF, 0xFF56D4DD, 0xFFFFFFFF,
    ];

    private static readonly uint[] AnsiLight =
    [
        0xFF24292F, 0xFFCF222E, 0xFF116329, 0xFF7D4E00, 0xFF0969DA, 0xFF8250DF, 0xFF1B7C83, 0xFF6E7781,
        0xFF57606A, 0xFFA40E26, 0xFF1A7F37, 0xFF633C01, 0xFF218BFF, 0xFFA475F9, 0xFF3192AA, 0xFF8C959F,
    ];

    public static Color Ansi(int index) => FromArgb((IsDark ? AnsiDark : AnsiLight)[Math.Clamp(index, 0, 15)]);

    // ---- JSON ------------------------------------------------------------------------

    public static TextStyle JsonKey => new(ColorOf(0xFF9CDCFE, 0xFF0451A5));
    public static TextStyle JsonString => new(ColorOf(0xFFCE9178, 0xFFA31515));
    public static TextStyle JsonNumber => new(ColorOf(0xFFB5CEA8, 0xFF098658));
    public static TextStyle JsonLiteral => new(ColorOf(0xFF569CD6, 0xFF0000FF));
    public static TextStyle Muted => new(ColorOf(0xFF8B949E, 0xFF6E7781));
}

/// <summary>
/// Fills a TextBlock from styled <see cref="TextSegment"/> runs. Set <c>local:RichText.Segments</c> in a template, or
/// call <see cref="Fill"/> from code. Backgrounds are drawn with text highlighters, since a Run has no background.
/// </summary>
public static class RichText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(object), typeof(RichText), new PropertyMetadata(null, OnSegmentsChanged));

    public static object? GetSegments(DependencyObject target) => target.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject target, object? value) => target.SetValue(SegmentsProperty, value);

    private static void OnSegmentsChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is TextBlock block) Fill(block, e.NewValue as IReadOnlyList<TextSegment> ?? []);
    }

    public static void Fill(TextBlock block, IReadOnlyList<TextSegment> segments)
    {
        block.Inlines.Clear();
        block.TextHighlighters.Clear();
        var offset = 0;
        foreach (var segment in segments)
        {
            if (segment.Text.Length == 0) continue;
            var style = segment.Style;
            var run = new Run { Text = segment.Text };
            var foreground = style.AnsiForeground is { } fgIndex ? DevToolsPalette.Ansi(fgIndex) : style.Foreground;
            if (foreground is { } fg) run.Foreground = new SolidColorBrush(fg);
            if (style.Bold) run.FontWeight = new FontWeight(700);
            if (style.Italic) run.FontStyle = FontStyle.Italic;
            if (style.Underline) run.TextDecorations = TextDecorations.Underline;
            block.Inlines.Add(run);

            var background = style.AnsiBackground is { } bgIndex ? DevToolsPalette.Ansi(bgIndex) : style.Background;
            if (background is { } bg)
            {
                var highlighter = new TextHighlighter { Background = new SolidColorBrush(bg) };
                // A highlighter repaints the text too; keep the run's colour (or the block's) rather than the selection colour.
                highlighter.Foreground = foreground is { } hfg ? new SolidColorBrush(hfg) : block.Foreground;
                highlighter.Ranges.Add(new TextRange { StartIndex = offset, Length = segment.Text.Length });
                block.TextHighlighters.Add(highlighter);
            }

            offset += segment.Text.Length;
        }
    }
}
