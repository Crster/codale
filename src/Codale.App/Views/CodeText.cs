using Codale.App.Controls;
using Codale.Core.Syntax;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace Codale.App.Views;

/// <summary>
/// Attached properties that turn any TextBlock into coloured source text: bind
/// Code (the raw text) and Syntax ("shell" tokenised, "text" plain), and the
/// inlines are rebuilt from that. TextBlock is sealed in WinUI, so a subclass
/// cannot carry the behaviour, and RichTextBlock.Blocks cannot be bound from a
/// data template - attached properties are the one seam that works. Tokens
/// without a palette entry keep the TextBlock's own foreground, so output text
/// follows the theme.
/// </summary>
public static class CodeText
{
    private static readonly IReadOnlyDictionary<ShellTokenKind, Brush> Palette =
        new Dictionary<ShellTokenKind, Brush>
        {
            // A One Dark-ish set, picked medium so it holds on the card in both themes.
            [ShellTokenKind.Command] = Solid(0x61, 0xAF, 0xEF),
            [ShellTokenKind.Keyword] = Solid(0xC6, 0x78, 0xDD),
            [ShellTokenKind.String] = Solid(0x98, 0xC3, 0x79),
            [ShellTokenKind.Variable] = Solid(0xE0, 0x6C, 0x75),
            [ShellTokenKind.Flag] = Solid(0xD1, 0x9A, 0x66),
            [ShellTokenKind.Number] = Solid(0xD1, 0x9A, 0x66),
            [ShellTokenKind.Operator] = Solid(0x56, 0xB6, 0xC2),
            [ShellTokenKind.Comment] = Solid(0x7F, 0x8C, 0x9B),
        };

    private const string LanguagePrefix = "lang:";

    /// <summary>Longest block, in characters, the grammar will colour; past it the text stays plain so a huge paste cannot stall the UI.</summary>
    private const int MaxGrammarChars = 100_000;

    private static readonly Dictionary<CodeTokenKind, Brush> GrammarBrushes = [];

    /// <summary>The syntax value that colours a block with the grammar for <paramref name="languageId"/>.</summary>
    public static string ForLanguage(string languageId) => LanguagePrefix + languageId;

    private static Brush Solid(byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(255, r, g, b));

    public static readonly DependencyProperty CodeProperty = DependencyProperty.RegisterAttached(
        "Code",
        typeof(string),
        typeof(CodeText),
        new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty SyntaxProperty = DependencyProperty.RegisterAttached(
        "Syntax",
        typeof(string),
        typeof(CodeText),
        new PropertyMetadata("text", OnTextChanged));

    /// <summary>The raw text to render.</summary>
    public static string? GetCode(TextBlock text) => (string?)text.GetValue(CodeProperty);

    public static void SetCode(TextBlock text, string? value) => text.SetValue(CodeProperty, value);

    /// <summary>"shell" runs the tokenizer; anything else renders plain.</summary>
    public static string GetSyntax(TextBlock text) => (string)text.GetValue(SyntaxProperty);

    public static void SetSyntax(TextBlock text, string value) => text.SetValue(SyntaxProperty, value);

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBlock text)
        {
            return;
        }

        text.Inlines.Clear();

        if (GetCode(text) is not { Length: > 0 } code)
        {
            return;
        }

        code = Normalize(code);

        var syntax = GetSyntax(text);
        if (syntax.StartsWith(LanguagePrefix, StringComparison.Ordinal)
            && code.Length <= MaxGrammarChars
            && AddGrammarRuns(text, code, syntax[LanguagePrefix.Length..]))
        {
            return;
        }

        var spans = syntax == "shell"
            ? ShellHighlighter.Tokenize(code)
            : new List<(string Text, ShellTokenKind Kind)> { (code, ShellTokenKind.Plain) };

        foreach (var (span, kind) in spans)
        {
            // A Run does not break lines on its own: each newline is a LineBreak inline.
            var lines = span.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    text.Inlines.Add(new LineBreak());
                }

                if (lines[i].Length == 0)
                {
                    continue;
                }

                var run = new Run { Text = lines[i] };

                if (Palette.TryGetValue(kind, out var brush))
                {
                    run.Foreground = brush;
                }

                text.Inlines.Add(run);
            }
        }
    }

    /// <summary>
    /// Colours <paramref name="code"/> line by line with the grammar for <paramref name="languageId"/>.
    /// Returns false (with the inlines left empty) when the language has no usable grammar, so the
    /// caller can fall back to plain or shell colouring.
    /// </summary>
    private static bool AddGrammarRuns(TextBlock text, string code, string languageId)
    {
        var tokenizer = SyntaxService.Tokenizer;
        if (!tokenizer.Supports(languageId))
        {
            return false;
        }

        var runs = new List<Inline>();
        var tokens = new List<CodeToken>();
        object? state = null;
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                runs.Add(new LineBreak());
            }

            var line = lines[i];
            if (!tokenizer.TryTokenizeLine(languageId, line, state, tokens, out state))
            {
                // The grammar gave up mid-block; keep the rest readable rather than half-coloured.
                tokens.Clear();
            }

            var at = 0;
            foreach (var token in tokens)
            {
                if (token.Start > at)
                {
                    runs.Add(new Run { Text = line[at..token.Start] });
                }

                runs.Add(new Run { Text = line.Substring(token.Start, token.Length), Foreground = BrushFor(token.Kind) });
                at = token.Start + token.Length;
            }

            if (at < line.Length)
            {
                runs.Add(new Run { Text = line[at..] });
            }
        }

        foreach (var run in runs)
        {
            text.Inlines.Add(run);
        }

        return true;
    }

    private static Brush BrushFor(CodeTokenKind kind)
    {
        if (!GrammarBrushes.TryGetValue(kind, out var brush))
        {
            brush = new SolidColorBrush(CodePalette.Get(kind));
            GrammarBrushes[kind] = brush;
        }

        return brush;
    }

    private const int TabWidth = 4;

    /// <summary>
    /// Terminal text made displayable: ANSI escapes dropped, CR/CRLF as LF, tabs padded to
    /// the next tab stop, other control characters shown as a middle dot.
    /// </summary>
    private static string Normalize(string code)
    {
        code = System.Text.RegularExpressions.Regex.Replace(code, @"\u001b\[[0-9;?]*[ -/]*[@-~]", "");
        code = code.Replace("\r\n", "\n").Replace('\r', '\n');

        var sb = new System.Text.StringBuilder(code.Length);
        var column = 0;

        foreach (var c in code)
        {
            switch (c)
            {
                case '\n':
                    sb.Append(c);
                    column = 0;
                    break;
                case '\t':
                    var pad = TabWidth - (column % TabWidth);
                    sb.Append(' ', pad);
                    column += pad;
                    break;
                case ' ' or ' ' or ' ':
                    sb.Append(' ');
                    column++;
                    break;
                default:
                    sb.Append(char.IsControl(c) ? '·' : c);
                    column++;
                    break;
            }
        }

        return sb.ToString();
    }
}
