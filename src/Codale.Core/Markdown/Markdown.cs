namespace Codale.Core.Markdown;

/// <summary>
/// The block shapes a chat transcript actually meets: plan text, replies, and step
/// summaries are headings, paragraphs, lists, fenced code, quotes, and rules. The
/// parser below is deliberately a subset of CommonMark tuned to that corpus - it
/// never throws, and anything it does not understand degrades to literal text
/// rather than swallowing the words.
/// </summary>
public abstract record MarkdownBlock
{
    /// <summary>An ATX heading (# .. ######).</summary>
    public sealed record Heading(int Level, IReadOnlyList<MarkdownInline> Content) : MarkdownBlock;

    /// <summary>One or more lines of prose, soft breaks folded to spaces.</summary>
    public sealed record Paragraph(IReadOnlyList<MarkdownInline> Content) : MarkdownBlock;

    /// <summary>
    /// A list row. <see cref="Number"/> is zero for bullets, otherwise the literal
    /// ordinal the author typed. <see cref="Depth"/> nests every two spaces of indent.
    /// </summary>
    public sealed record ListItem(int Depth, int Number, IReadOnlyList<MarkdownInline> Content) : MarkdownBlock;

    /// <summary>A fenced block kept verbatim, line breaks intact.</summary>
    public sealed record CodeBlock(string? Language, string Text) : MarkdownBlock;

    /// <summary>A quoted region; the markers are stripped and the inside re-parsed.</summary>
    public sealed record Quote(IReadOnlyList<MarkdownBlock> Content) : MarkdownBlock;

    /// <summary>A horizontal rule (---, ***, ___).</summary>
    public sealed record ThematicBreak : MarkdownBlock;

    /// <summary>
    /// A GFM pipe table. <see cref="Rows"/> excludes the header; every row is padded
    /// or trimmed to the header's width so the renderer can lay out a plain grid.
    /// </summary>
    public sealed record Table(
        IReadOnlyList<TableAlignment> Alignments,
        IReadOnlyList<IReadOnlyList<MarkdownInline>> Header,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>> Rows) : MarkdownBlock;
}

public enum TableAlignment
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>The inline shapes inside a block: styled runs, code spans, links, and images.</summary>
public abstract record MarkdownInline
{
    public sealed record Text(string Value) : MarkdownInline;

    /// <summary>Bold when <see cref="Strong"/> (**x**), italic otherwise (*x*).</summary>
    public sealed record Emphasis(IReadOnlyList<MarkdownInline> Content, bool Strong) : MarkdownInline;

    public sealed record CodeSpan(string Value) : MarkdownInline;

    public sealed record Strike(IReadOnlyList<MarkdownInline> Content) : MarkdownInline;

    /// <summary>A [label](url) link.</summary>
    public sealed record Link(IReadOnlyList<MarkdownInline> Content, string Url) : MarkdownInline;

    /// <summary>
    /// An ![alt](url) image. The view resolves local paths and data URIs; remote URLs stay
    /// links, so agent-authored text cannot make the transcript phone home.
    /// </summary>
    public sealed record Image(IReadOnlyList<MarkdownInline> Content, string Url) : MarkdownInline;
}

/// <summary>
/// Hand-rolled rather than a package: the app vendors its surface (xterm.js, ripgrep)
/// and the chat only needs the subset above, so the parser stays small, testable,
/// and free to degrade. No raw HTML is ever produced, so agent output cannot smuggle
/// markup into the transcript.
/// </summary>
public static class MarkdownParser
{
    private const int MaxEmphasisDepth = 8;

    /// <summary>
    /// Quotes nested deeper than this stay literal text: each level recurses, and a stack
    /// overflow cannot be caught, so a document of ">>>>..." must not be able to cause one.
    /// </summary>
    public const int MaxQuoteDepth = 16;

    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return [];
        }

        try
        {
            var lines = markdown
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Replace("\t", "  ")
                .Split('\n');

            return ParseBlocks(lines, 0, lines.Length, 0);
        }
        catch (Exception)
        {
            // A pathological document must still read: fall back to the raw text.
            return [new MarkdownBlock.Paragraph([new MarkdownInline.Text(markdown)])];
        }
    }

    private static IReadOnlyList<MarkdownBlock> ParseBlocks(string[] lines, int start, int end, int quoteDepth)
    {
        var blocks = new List<MarkdownBlock>();
        var i = start;

        while (i < end)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            var text = IndentAtMost(line, 3);

            if (Fence(text) is { } fence)
            {
                var (code, next) = ReadFence(lines, i, end, fence);
                blocks.Add(code);
                i = next;
                continue;
            }

            if (HeadingLevel(text) is { } level)
            {
                var content = text.TrimStart()[level..].Trim();
                blocks.Add(new MarkdownBlock.Heading(level, ParseInlines(StripClosingHashes(content))));
                i++;
                continue;
            }

            if (IsThematicBreak(text))
            {
                blocks.Add(new MarkdownBlock.ThematicBreak());
                i++;
                continue;
            }

            if (quoteDepth < MaxQuoteDepth && IsQuoteLine(text))
            {
                var (quote, next) = ReadQuote(lines, i, end, quoteDepth);
                blocks.Add(quote);
                i = next;
                continue;
            }

            if (TableStart(lines, i, end) is { } alignments)
            {
                var (table, next) = ReadTable(lines, i, end, alignments);
                blocks.Add(table);
                i = next;
                continue;
            }

            if (ListItem(lines[i], out _, out _, out _) is not null)
            {
                var (items, next) = ReadList(lines, i, end);
                blocks.AddRange(items);
                i = next;
                continue;
            }

            var (paragraph, after) = ReadParagraph(lines, i, end, quoteDepth);
            if (paragraph is not null)
            {
                blocks.Add(paragraph);
            }

            i = after;
        }

        return blocks;
    }

    /// <summary>A fenced code block; an unterminated fence swallows to the end of its region
    /// rather than bleeding monospace over the rest of the document.</summary>
    private static (MarkdownBlock Block, int Next) ReadFence(
        string[] lines, int i, int end, (char Character, string? Language) fence)
    {
        var body = new List<string>();
        i++;

        while (i < end)
        {
            var closes = ClosingFence(IndentAtMost(lines[i], 3), fence.Character);
            i++;

            if (closes)
            {
                break;
            }

            body.Add(lines[i - 1].TrimEnd());
        }

        return (new MarkdownBlock.CodeBlock(fence.Language, string.Join("\n", body)), i);
    }

    private static bool IsQuoteLine(string line) => IndentAtMost(line, 3).TrimStart().StartsWith('>');

    /// <summary>A run of ">" lines, parsed again as blocks one level deeper.</summary>
    private static (MarkdownBlock Block, int Next) ReadQuote(string[] lines, int i, int end, int quoteDepth)
    {
        var quoted = new List<string>();

        while (i < end && IsQuoteLine(lines[i]))
        {
            var inner = IndentAtMost(lines[i], 3).TrimStart()[1..];
            quoted.Add(inner.StartsWith(' ') ? inner[1..] : inner);
            i++;
        }

        return (new MarkdownBlock.Quote(ParseBlocks([.. quoted], 0, quoted.Count, quoteDepth + 1)), i);
    }

    /// <summary>Consecutive text lines joined into one paragraph, ended by a blank line or the start of another block.</summary>
    private static (MarkdownBlock? Block, int Next) ReadParagraph(string[] lines, int i, int end, int quoteDepth)
    {
        var paragraph = new List<string>();

        while (i < end && !string.IsNullOrWhiteSpace(lines[i]))
        {
            if (paragraph.Count > 0 && StartsOtherBlock(lines, i, end, quoteDepth))
            {
                break;
            }

            paragraph.Add(lines[i].Trim());
            i++;
        }

        MarkdownBlock? block = paragraph.Count > 0
            ? new MarkdownBlock.Paragraph(ParseInlines(string.Join(" ", paragraph)))
            : null;
        return (block, i);
    }

    private static bool StartsOtherBlock(string[] lines, int i, int end, int quoteDepth)
    {
        var candidate = IndentAtMost(lines[i], 3);

        return Fence(candidate) is not null ||
               HeadingLevel(candidate) is not null ||
               IsThematicBreak(candidate) ||
               (quoteDepth < MaxQuoteDepth && IsQuoteLine(candidate)) ||
               TableStart(lines, i, end) is not null ||
               ListItem(lines[i], out _, out _, out _) is not null;
    }

    /// <summary>
    /// A list is a run of marker lines plus their indented continuations, flattened
    /// to <see cref="MarkdownBlock.ListItem"/> rows whose depth the renderer indents.
    /// A blank line only ends the list when the next real line cannot continue it.
    /// </summary>
    private static (List<MarkdownBlock> Items, int Next) ReadList(string[] lines, int i, int end)
    {
        var items = new List<(int Depth, int Number, List<string> Lines)>();

        while (i < end)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                var look = i + 1;

                while (look < end && string.IsNullOrWhiteSpace(lines[look]))
                {
                    look++;
                }

                // Only a marker or a deeper-indented line continues past the blank.
                if (look < end &&
                    (ListItem(lines[look], out _, out _, out _) is not null ||
                     lines[look].TrimEnd().Length - lines[look].TrimStart().Length >= 2))
                {
                    i = look;
                    continue;
                }

                break;
            }

            var indent = line.Length - line.TrimStart().Length;

            // A blank inside a loose list reads as a gap, not a boundary: the renderer
            // spaces rows uniformly, so nothing needs to remember that one was skipped.
            if (ListItem(line, out var depth, out var number, out var first) is not null)
            {
                items.Add((depth, number, [first]));
                i++;
                continue;
            }

            if (indent >= 2 && items.Count > 0)
            {
                items[^1].Lines.Add(line.Trim());
                i++;
                continue;
            }

            break;
        }

        var rows = items
            .Select(item => new MarkdownBlock.ListItem(
                Math.Min(item.Depth, 4),
                item.Number,
                ParseInlines(string.Join(" ", item.Lines))))
            .ToList<MarkdownBlock>();

        return (rows, i);
    }

    /// <summary>
    /// A table opens on a line holding a pipe followed by a delimiter row
    /// (|---|:---:|) with the same number of cells. Returns the column alignments,
    /// or null when line <paramref name="i"/> does not start a table.
    /// </summary>
    private static List<TableAlignment>? TableStart(string[] lines, int i, int end)
    {
        if (i + 1 >= end || !lines[i].Contains('|'))
        {
            return null;
        }

        // A delimiter row is always dashes (colons optional, pipes optional for a
        // single column); skipping the split for everything else keeps paragraph
        // scanning cheap - this runs on the line after every paragraph line while
        // a reply streams.
        var next = lines[i + 1];
        if (!next.Contains('-'))
        {
            return null;
        }

        var delimiter = SplitRow(next);
        var alignments = new List<TableAlignment>();

        foreach (var cell in delimiter)
        {
            var spec = cell.Trim();
            var left = spec.StartsWith(':');
            var right = spec.EndsWith(':');
            var dashes = spec.Trim(':');

            if (dashes.Length == 0 || dashes.Any(c => c != '-'))
            {
                return null;
            }

            alignments.Add((left, right) switch
            {
                (true, true) => TableAlignment.Center,
                (true, false) => TableAlignment.Left,
                (false, true) => TableAlignment.Right,
                _ => TableAlignment.None,
            });
        }

        return alignments.Count > 0 && SplitRow(lines[i]).Count == alignments.Count
            ? alignments
            : null;
    }

    private static (MarkdownBlock Table, int Next) ReadTable(string[] lines, int i, int end, List<TableAlignment> alignments)
    {
        var width = alignments.Count;

        IReadOnlyList<IReadOnlyList<MarkdownInline>> Cells(string line)
        {
            var cells = SplitRow(line);
            return Enumerable.Range(0, width)
                .Select(c => c < cells.Count ? ParseInlines(cells[c].Trim()) : [])
                .ToList();
        }

        var header = Cells(lines[i]);
        var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownInline>>>();
        i += 2;

        // The body runs until a blank line or a line with no pipe at all.
        while (i < end && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].Contains('|'))
        {
            rows.Add(Cells(lines[i]));
            i++;
        }

        return (new MarkdownBlock.Table(alignments, header, rows), i);
    }

    /// <summary>
    /// Splits a row on its unescaped pipes outside code spans, dropping the optional
    /// leading and trailing pipe. Escaped pipes become literal ones.
    /// </summary>
    private static List<string> SplitRow(string line)
    {
        var text = line.Trim();
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var tick = 0;

        for (var k = 0; k < text.Length; k++)
        {
            var c = text[k];

            if (c == '\\' && k + 1 < text.Length && text[k + 1] == '|')
            {
                cell.Append('|');
                k++;
                continue;
            }

            if (c == '`')
            {
                var run = RunOf(text, k, '`');
                tick = tick == 0 ? run : tick == run ? 0 : tick;
                cell.Append(text, k, run);
                k += run - 1;
                continue;
            }

            if (c == '|' && tick == 0)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                continue;
            }

            cell.Append(c);
        }

        cells.Add(cell.ToString());

        if (text.StartsWith('|'))
        {
            cells.RemoveAt(0);
        }

        if (text.Length > 1 && text.EndsWith('|') && !text.EndsWith("\\|") && cells.Count > 0 && cells[^1].Length == 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    /// <summary>
    /// The marker ("- ", "* ", "+ ", "12. ", "12) ") and the text after it, or null
    /// when the line does not open a row. An empty result string is a bare marker.
    /// </summary>
    private static string? ListItem(string line, out int depth, out int number, out string content)
    {
        depth = Math.Min((line.Length - line.TrimStart().Length) / 2, 4);
        number = 0;
        content = "";
        var text = line.TrimStart();

        if (text.Length >= 2 && text[0] is '-' or '*' or '+' && (text[1] == ' ' || text[1] == '\t'))
        {
            content = text[2..].Trim();
            return text[2..].Trim();
        }

        var digits = 0;

        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits < 10 && digits + 1 < text.Length && text[digits] is '.' or ')' && text[digits + 1] == ' ')
        {
            number = int.Parse(text[..digits]);
            content = text[(digits + 2)..].Trim();
            return content;
        }

        return null;
    }

    private static int? HeadingLevel(string line)
    {
        var text = line.TrimStart();
        var hashes = 0;

        while (hashes < text.Length && text[hashes] == '#')
        {
            hashes++;
        }

        // "#tag" is a paragraph; a heading needs the separating space (or nothing after).
        return hashes is >= 1 and <= 6 && (hashes == text.Length || text[hashes] == ' ')
            ? hashes
            : null;
    }

    private static string StripClosingHashes(string content)
    {
        var end = content.Length;

        while (end > 0 && content[end - 1] == '#')
        {
            end--;
        }

        // "## Fix C#" keeps its hash only because a space separates them (CommonMark's
        // closing sequence); "## Fix ##" loses the pair.
        return end < content.Length && end > 0 && content[end - 1] == ' '
            ? content[..end].TrimEnd()
            : content;
    }

    /// <summary>The fence opener: the fence character, its length, and the info string.</summary>
    private static (char Character, string? Language)? Fence(string line)
    {
        var text = line.TrimStart();
        var open = text.Length > 0 ? text[0] : '\0';

        if (open is not '`' and not '~')
        {
            return null;
        }

        var run = 0;

        while (run < text.Length && text[run] == open)
        {
            run++;
        }

        if (run < 3)
        {
            return null;
        }

        var info = text[run..].Trim();

        // A backtick fence's info string cannot itself hold backticks (CommonMark);
        // treating that as prose avoids swallowing half a document on a stray ```.
        if (open == '`' && info.Contains('`'))
        {
            return null;
        }

        return (open, info.Length > 0 ? info : null);
    }

    private static bool ClosingFence(string line, char character)
    {
        var text = line.TrimStart();
        var run = 0;

        while (run < text.Length && text[run] == character)
        {
            run++;
        }

        return run >= 3 && run == text.Length;
    }

    private static bool IsThematicBreak(string line)
    {
        // Index loop: this runs per candidate line while paragraphs scan, and the LINQ
        // form allocated a char[] plus delegates per line.
        var mark = default(char);
        var count = 0;
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c is not ('-' or '*' or '_') || (count > 0 && c != mark))
            {
                return false;
            }

            mark = c;
            count++;
        }

        return count >= 3;
    }

    private static string IndentAtMost(string line, int max) =>
        line.Length - line.TrimStart(' ').Length <= max ? line.TrimStart(' ') : line;

    // ---------------------------------------------------------------- inlines

    private static IReadOnlyList<MarkdownInline> ParseInlines(string text, int depth = 0)
    {
        if (depth > MaxEmphasisDepth)
        {
            return [new MarkdownInline.Text(text)];
        }

        var inlines = new List<MarkdownInline>();
        var literal = new System.Text.StringBuilder();
        var i = 0;

        void Flush()
        {
            if (literal.Length > 0)
            {
                inlines.Add(new MarkdownInline.Text(literal.ToString()));
                literal.Clear();
            }
        }

        while (i < text.Length)
        {
            var c = text[i];

            switch (c)
            {
                case '\\' when i + 1 < text.Length && IsPunctuation(text[i + 1]):
                    literal.Append(text[i + 1]);
                    i += 2;
                    break;

                case '`':
                {
                    var run = RunOf(text, i, '`');

                    if (FindRun(text, i + run, '`', run) is { } close)
                    {
                        var span = text[(i + run)..close];
                        Flush();
                        inlines.Add(new MarkdownInline.CodeSpan(NormaliseCodeSpan(span)));
                        i = close + run;
                    }
                    else
                    {
                        literal.Append(text.Substring(i, run));
                        i += run;
                    }

                    break;
                }

                case '*' or '_' when EmphasisClose(text, i, c, out var strong, out var close):
                {
                    var opener = strong ? 2 : 1;
                    Flush();
                    inlines.Add(new MarkdownInline.Emphasis(
                        ParseInlines(text[(i + opener)..close], depth + 1),
                        strong));
                    i = close + opener;
                    break;
                }

                case '~' when i + 1 < text.Length && text[i + 1] == '~'
                    && FindRun(text, i + 2, '~', 2) is { } strikeClose:
                    Flush();
                    inlines.Add(new MarkdownInline.Strike(ParseInlines(text[(i + 2)..strikeClose], depth + 1)));
                    i = strikeClose + 2;
                    break;

                case '[' when ParseLink(text, i) is { } link:
                    Flush();
                    inlines.Add(new MarkdownInline.Link(ParseInlines(link.Label, depth + 1), link.Url));
                    i = link.Next;
                    break;

                case '!' when i + 1 < text.Length && text[i + 1] == '[' && ParseLink(text, i + 1) is { } image:
                    Flush();
                    inlines.Add(new MarkdownInline.Image(ParseInlines(image.Label, depth + 1), image.Url));
                    i = image.Next;
                    break;

                case '&' when Entity(text, i) is { } decoded:
                    literal.Append(decoded.Value);
                    i = decoded.Next;
                    break;

                default:
                    literal.Append(c);
                    i++;
                    break;
            }
        }

        Flush();
        return inlines;
    }

    /// <summary>
    /// Whether an emphasis opener at <paramref name="start"/> finds its closer, and
    /// where. Underscore obeys the intraword rule so file_name and __init__ survive;
    /// a doubled opener wins over a single one, so **bold** parses before *italic*.
    /// </summary>
    private static bool EmphasisClose(string text, int start, char marker, out bool strong, out int close)
    {
        strong = false;
        close = -1;

        var doubled = start + 1 < text.Length && text[start + 1] == marker;

        if (marker == '_' && !CanOpenUnderscore(text, start))
        {
            return false;
        }

        var opener = doubled ? 2 : 1;

        if (start + opener >= text.Length || text[start + opener] == ' ')
        {
            return false;
        }

        var scan = start + opener;

        while ((close = text.IndexOf(marker, scan)) >= 0)
        {
            var run = RunOf(text, close, marker);

            // A closer hugging the opener (***x***'s inner pair) would make an empty
            // span; skip it and let the next run close for real.
            if (close > start + opener && text[close - 1] != ' ' && (!doubled || run >= 2))
            {
                strong = doubled;
                return true;
            }

            scan = close + run;
        }

        close = -1;
        return false;
    }

    private static bool CanOpenUnderscore(string text, int start)
    {
        var before = start > 0 ? text[start - 1] : ' ';
        return !char.IsAsciiLetterOrDigit(before);
    }

    private static (string Label, string Url, int Next)? ParseLink(string text, int start)
    {
        var labelEnd = text.IndexOf(']', start + 1);

        if (labelEnd < 0 ||
            labelEnd + 1 >= text.Length ||
            text[labelEnd + 1] != '(' ||
            text.IndexOf('(', start + 1, labelEnd - start - 1) >= 0)
        {
            return null;
        }

        var urlEnd = text.IndexOf(')', labelEnd + 2);

        if (urlEnd < 0)
        {
            return null;
        }

        var label = text[(start + 1)..labelEnd];
        var url = text[(labelEnd + 2)..urlEnd].Trim();

        if (url.Length == 0 || url.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return (label, url, urlEnd + 1);
    }

    /// <summary>Named and numeric entities; an unknown spelling keeps its ampersand.</summary>
    private static (string Value, int Next)? Entity(string text, int start)
    {
        if (start + 1 >= text.Length)
        {
            return null;
        }

        // The count must stay inside the string or IndexOf throws.
        var remaining = text.Length - start - 1;
        var semicolon = text.IndexOf(';', start + 1, Math.Min(12, remaining));

        if (semicolon < 0)
        {
            return null;
        }

        var name = text[(start + 1)..semicolon];
        var value = name switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => "\u00A0",
            _ => null,
        };

        if (value is not null)
        {
            return (value, semicolon + 1);
        }

        if (name.StartsWith("#x", StringComparison.Ordinal) &&
            int.TryParse(name[2..], System.Globalization.NumberStyles.AllowHexSpecifier, null, out var hex))
        {
            return (CodePoint(hex), semicolon + 1);
        }

        if (name.StartsWith('#') &&
            int.TryParse(name[1..], System.Globalization.NumberStyles.None, null, out var num))
        {
            return (CodePoint(num), semicolon + 1);
        }

        return null;
    }

    /// <summary>
    /// A numeric entity's character. NUL, lone surrogates and anything past U+10FFFF are not
    /// text (and a lone surrogate would poison whatever encodes the transcript later), so they
    /// read as the replacement character, as CommonMark specifies; astral code points become a pair.
    /// </summary>
    private static string CodePoint(int value) =>
        value is <= 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF)
            ? "\uFFFD"
            : char.ConvertFromUtf32(value);

    private static string NormaliseCodeSpan(string span)
    {
        // One leading and trailing space around a code span are removed when both
        // exist (CommonMark), so "` a b `" reads "a b".
        return span.Length >= 2 && span[0] == ' ' && span[^1] == ' ' ? span[1..^1] : span;
    }

    private static int RunOf(string text, int start, char c)
    {
        var run = 0;

        while (start + run < text.Length && text[start + run] == c)
        {
            run++;
        }

        return run;
    }

    private static int? FindRun(string text, int from, char c, int length)
    {
        var scan = from;

        while (true)
        {
            var at = text.IndexOf(c, scan);

            if (at < 0)
            {
                return null;
            }

            if (RunOf(text, at, c) == length)
            {
                return at;
            }

            scan = at + 1;
        }
    }

    private static bool IsPunctuation(char c) =>
        c is '"' or '\'' or '*' or '_' or '`' or '[' or ']' or '(' or ')' or
            '~' or '!' or '&' or '<' or '>' or '#' or '-' or '+' or '.' or ',' or
            ':' or ';' or '?' or '/' or '\\' or '|' or '{' or '}' or '$' or '%' or
            '@' or '^' or '=';
}
