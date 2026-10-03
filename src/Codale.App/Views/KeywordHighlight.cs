using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace Codale.App.Views;

/// <summary>
/// Marks every occurrence of the search keywords in a TextBlock's plain Text, case
/// insensitively, with TextHighlighters - so the text stays one run, selectable and
/// cheap, instead of being split into styled inlines. Whole matched lines can be given
/// a fainter band underneath, so the area that matched reads at a glance.
/// </summary>
/// <remarks>
/// Terms arrive as one newline-separated string and lines as a comma-separated list of
/// 0-based indexes, because x:Bind to an attached list property is awkward. Recomputes
/// when the terms, the lines or the text change.
/// </remarks>
public static class KeywordHighlight
{
    // An amber that reads on the dark card and the light one; text keeps its colour.
    private static readonly Brush MarkBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xE5, 0xB2, 0x3C));

    // The same amber, faint enough that the keyword marks still stand out on top of it.
    private static readonly Brush LineBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xE5, 0xB2, 0x3C));

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
        }

        Apply(text);
    }

    private static void Apply(TextBlock text)
    {
        text.TextHighlighters.Clear();

        var content = text.Text;
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        // The band goes first so the keyword marks paint over it.
        if (GetLines(text) is { Length: > 0 } lines && LineBand(content, lines) is { } band)
        {
            text.TextHighlighters.Add(band);
        }

        if (GetTerms(text) is not { Length: > 0 } terms)
        {
            return;
        }

        var highlighter = new TextHighlighter { Background = MarkBrush };

        // Longest first, so "RetryPolicy" wins over "Retry" where both match.
        foreach (var term in terms.Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(t => t.Length))
        {
            // Short words must start a word, as the search matched them: "run" is not in "truncate".
            var wordStart = term.Length <= 5 && char.IsLetterOrDigit(term[0]);

            var index = 0;
            while ((index = content.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var overlaps = highlighter.Ranges.Any(r => index < r.StartIndex + r.Length && r.StartIndex < index + term.Length);
                var midWord = wordStart && index > 0 && (char.IsLetterOrDigit(content[index - 1]) || content[index - 1] == '_');
                if (!overlaps && !midWord)
                {
                    highlighter.Ranges.Add(new TextRange { StartIndex = index, Length = term.Length });
                }

                index += term.Length;
            }
        }

        if (highlighter.Ranges.Count > 0)
        {
            text.TextHighlighters.Add(highlighter);
        }
    }

    private static TextHighlighter? LineBand(string content, string lines)
    {
        var wanted = lines
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n >= 0)
            .ToHashSet();

        if (wanted.Count == 0)
        {
            return null;
        }

        var band = new TextHighlighter { Background = LineBrush };
        var (line, start) = (0, 0);

        while (start <= content.Length)
        {
            var end = content.IndexOf('\n', start);
            if (end < 0)
            {
                end = content.Length;
            }

            if (wanted.Contains(line) && end > start)
            {
                band.Ranges.Add(new TextRange { StartIndex = start, Length = end - start });
            }

            (line, start) = (line + 1, end + 1);
        }

        return band.Ranges.Count > 0 ? band : null;
    }
}
