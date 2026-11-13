using Codale.App.Controls;
using Codale.Core.Syntax;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace Codale.App.Views;

/// <summary>
/// Marks every occurrence of the search keywords in a TextBlock's plain Text, case
/// insensitively, on a background canvas beneath the selectable text. Matching rows
/// get a thin underline; syntax highlighters change only the foreground.
/// </summary>
/// <remarks>
/// Terms arrive as one newline-separated string and lines as a comma-separated list of
/// 0-based indexes, because x:Bind to an attached list property is awkward. Recomputes
/// when the terms, the lines or the text change.
/// </remarks>
public static class KeywordHighlight
{
    private static readonly Dictionary<(Color, bool), Brush> TokenBrushes = [];

    // TextHighlighter paints yellow unless told otherwise; token colouring must not paint at all.
    private static readonly Brush Transparent = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    public static readonly DependencyProperty TermsProperty = DependencyProperty.RegisterAttached(
        "Terms",
        typeof(string),
        typeof(KeywordHighlight),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty LinesProperty = DependencyProperty.RegisterAttached(
        "Lines",
        typeof(string),
        typeof(KeywordHighlight),
        new PropertyMetadata(null, OnChanged));

    /// <summary>The file the snippet came from; its extension picks the syntax colouring.</summary>
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path",
        typeof(string),
        typeof(KeywordHighlight),
        new PropertyMetadata(null, OnChanged));

    // Whether this TextBlock's Text is already watched, so the callback registers once.
    private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached(
        "Watched",
        typeof(bool),
        typeof(KeywordHighlight),
        new PropertyMetadata(false));

    public static string? GetTerms(TextBlock text) => (string?)text.GetValue(TermsProperty);

    public static void SetTerms(TextBlock text, string? value) => text.SetValue(TermsProperty, value);

    public static string? GetLines(TextBlock text) => (string?)text.GetValue(LinesProperty);

    public static void SetLines(TextBlock text, string? value) => text.SetValue(LinesProperty, value);

    public static string? GetPath(TextBlock text) => (string?)text.GetValue(PathProperty);

    public static void SetPath(TextBlock text, string? value) => text.SetValue(PathProperty, value);

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBlock text)
        {
            return;
        }

        // Text may be bound after the highlight properties; watch it once per TextBlock.
        if (!(bool)text.GetValue(WatchedProperty))
        {
            text.SetValue(WatchedProperty, true);
            text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (s, _) => Apply((TextBlock)s));
            text.Loaded += (_, _) =>
            {
                // The row band spans the card, which can resize while the code itself does not.
                if (text.Parent is Grid host && host.Children.OfType<Canvas>().FirstOrDefault() is { Tag: null } canvas)
                {
                    canvas.Tag = true;
                    canvas.SizeChanged += (_, _) => DrawBackground(text);
                }

                Apply(text);
            };
            text.SizeChanged += (_, _) => DrawBackground(text);
            text.ActualThemeChanged += (_, _) => Apply(text);
        }

        Apply(text);
    }

    private static void Apply(TextBlock text)
    {
        text.TextHighlighters.Clear();

        var content = text.Text;
        if (string.IsNullOrEmpty(content))
        {
            DrawBackground(text);
            return;
        }

        AddSyntax(text, content);
        DrawBackground(text);
        // Text changes of the same size may not raise SizeChanged. Re-read glyph
        // positions after the pending text layout has settled as well.
        text.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => DrawBackground(text));
    }

    // Background shapes are siblings below the text, so syntax foregrounds stay intact. The
    // canvas spans the whole row (gutter included); shapes are placed in its coordinates.
    private static void DrawBackground(TextBlock text)
    {
        if (text.Parent is not Grid host || host.Children.OfType<Canvas>().FirstOrDefault() is not { } canvas)
            return;
        canvas.Children.Clear();
        var wanted = (GetLines(text) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var line) ? line : -1).ToHashSet();
        EmphasizeGutter(host, text, wanted);
        var content = text.Text;
        if (string.IsNullOrEmpty(content) || text.ActualWidth <= 0 || text.ActualHeight <= 0 || canvas.ActualWidth <= 0)
            return;
        canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight) };
        var offset = text.TransformToVisual(canvas).TransformPoint(new Windows.Foundation.Point(0, 0));
        var light = text.ActualTheme == ElementTheme.Light;
        var matchBrush = new SolidColorBrush(SearchHighlightStyle.Match(light));
        var rowBrush = new SolidColorBrush(SearchHighlightStyle.Row(light));
        var edgeBrush = new SolidColorBrush(SearchHighlightStyle.RowEdge(light));
        var start = 0;
        var lineNo = 0;
        while (start <= content.Length)
        {
            var end = content.IndexOf('\n', start);
            if (end < 0) end = content.Length;
            if (wanted.Contains(lineNo))
            {
                var rect = CharacterRect(start);
                var top = offset.Y + rect.Y;
                Place(0, top, canvas.ActualWidth, rect.Height, rowBrush);
                Place(0, top, SearchHighlightStyle.RowEdgeWidth, rect.Height, edgeBrush);
            }
            if (end == content.Length) break;
            start = end + 1;
            lineNo++;
        }

        var claimed = new bool[content.Length];
        foreach (var term in (GetTerms(text) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(term => term.Length))
        {
            var wordStart = term.Length <= 5 && char.IsLetterOrDigit(term[0]);
            var index = 0;
            while ((index = content.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var midWord = wordStart && index > 0 && (char.IsLetterOrDigit(content[index - 1]) || content[index - 1] == '_');
                if (!midWord && !claimed.AsSpan(index, term.Length).Contains(true))
                {
                    Array.Fill(claimed, true, index, term.Length);
                    var first = CharacterRect(index);
                    var last = CharacterRect(index + term.Length, LogicalDirection.Backward);
                    Place(offset.X + first.X, offset.Y + first.Y, Math.Max(0, last.Right - first.X), first.Height, matchBrush, radius: 2);
                }
                index += term.Length;
            }
        }

        Windows.Foundation.Rect CharacterRect(int offset, LogicalDirection direction = LogicalDirection.Forward)
        {
            // Text is a single Run. Start within it to exclude inline boundary symbols.
            var origin = text.Inlines.OfType<Run>().FirstOrDefault()?.ContentStart ?? text.ContentStart;
            return origin.GetPositionAtOffset(offset, direction)?
                .GetCharacterRect(direction) ?? default;
        }

        void Place(double x, double y, double width, double height, Brush brush, double radius = 0)
        {
            var mark = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = width, Height = height, Fill = brush, RadiusX = radius, RadiusY = radius };
            Canvas.SetLeft(mark, x);
            Canvas.SetTop(mark, y);
            canvas.Children.Add(mark);
        }
    }

    /// <summary>The gutter's numbers for matched rows in the code's full-contrast colour; the rest stay dimmed.</summary>
    private static void EmphasizeGutter(Grid host, TextBlock code, HashSet<int> lines)
    {
        if (host.Children.OfType<Border>().FirstOrDefault()?.Child is not TextBlock gutter)
            return;
        gutter.TextHighlighters.Clear();
        var content = gutter.Text;
        if (string.IsNullOrEmpty(content) || lines.Count == 0)
            return;
        var highlighter = new TextHighlighter { Foreground = code.Foreground, Background = Transparent };
        var start = 0;
        var lineNo = 0;
        while (start < content.Length)
        {
            var end = content.IndexOf('\n', start);
            if (end < 0) end = content.Length;
            if (lines.Contains(lineNo) && end > start)
                highlighter.Ranges.Add(new TextRange { StartIndex = start, Length = end - start });
            start = end + 1;
            lineNo++;
        }
        if (highlighter.Ranges.Count > 0)
            gutter.TextHighlighters.Add(highlighter);
    }

    /// <summary>One foreground-only highlighter per token colour, from the same tokenizer the editor uses.</summary>
    private static void AddSyntax(TextBlock text, string content)
    {
        if (!Services.AppSettings.EditorHighlighting || GetPath(text) is not { Length: > 0 } path)
        {
            return;
        }

        var languageId = Services.SyntaxSelection.IdFor(path);
        if (languageId is null)
        {
            return;
        }

        var light = text.ActualTheme == ElementTheme.Light;
        var byKind = new Dictionary<CodeTokenKind, TextHighlighter>();
        var tokens = new List<CodeToken>();
        var state = new CodeTokenState();
        var start = 0;

        while (start <= content.Length)
        {
            var end = content.IndexOf('\n', start);
            if (end < 0)
            {
                end = content.Length;
            }

            var line = content[start..end].TrimEnd('\r');
            state = CodeTokenizer.TokenizeLine(line, languageId, state, tokens);
            foreach (var token in tokens)
            {
                if (token.Kind == CodeTokenKind.Plain || token.Length <= 0)
                {
                    continue;
                }

                if (!byKind.TryGetValue(token.Kind, out var highlighter))
                {
                    var colour = CodePalette.Get(token.Kind, light);
                    if (!TokenBrushes.TryGetValue((colour, light), out var brush))
                    {
                        brush = TokenBrushes[(colour, light)] = new SolidColorBrush(colour);
                    }

                    highlighter = byKind[token.Kind] = new TextHighlighter { Foreground = brush, Background = Transparent };
                }

                highlighter.Ranges.Add(new TextRange { StartIndex = start + token.Start, Length = token.Length });
            }

            start = end + 1;
        }

        foreach (var highlighter in byKind.Values)
        {
            text.TextHighlighters.Add(highlighter);
        }
    }

}
