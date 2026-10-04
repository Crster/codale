using System.Text;
using System.Text.RegularExpressions;

using Codale.Core.Helper;

namespace Codale.Search;

/// <summary>A run of lines in one file, 1-based and inclusive.</summary>
public sealed record LineSpan(int Start, int End);

/// <summary>
/// Finds the parts of one open file that relate to a search: an instant keyword pass
/// for immediate feedback, then the helper model reading the whole file and naming the
/// line ranges that actually matter.
/// </summary>
/// <remarks>
/// The model sees the file with line numbers and answers with ranges only - no prose -
/// as a short reply, so the cost is almost all prefill. Reading the whole file is what
/// finds code that does what the search describes without sharing its words. A file too
/// big for that, even with long lines clipped and blank ones dropped, is narrowed to the
/// blocks around the search's words for the model to choose among.
/// </remarks>
public static partial class FileFocus
{
    /// <summary>
    /// About 45k tokens: a 3,000-line class read whole in a few seconds. Narrowing a file
    /// to keyword blocks loses the parts that do what the search describes in other words.
    /// </summary>
    public const int MaxFileChars = 180_000;

    private const int MaxSpans = 8;
    private const int MaxSpanLines = 80;

    /// <summary>At most this many candidate blocks go to the model, best keyword coverage first.</summary>
    private const int MaxCandidates = 8;
    private const int MaxCandidateLines = 60;
    private const int MaxCandidateChars = 30_000;

    /// <summary>
    /// The candidate pick: the model chooses among keyword-hit blocks, a multiple-choice
    /// question a small model answers reliably - unlike naming line numbers into a whole file.
    /// </summary>
    public static ToolDefinition Tool { get; } = new()
    {
        Name = "highlight",
        Description = "Mark the candidate code blocks that match the search.",
        Parameters =
        [
            new ToolParameter
            {
                Name = "best",
                Description = "comma separated block numbers, best match first; empty if none match",
                Required = true,
            },
        ],
    };

    /// <summary>The whole-file fallback for a file whose search words appear nowhere.</summary>
    public static ToolDefinition RangeTool { get; } = new()
    {
        Name = "highlight",
        Description = "Mark the parts of the file that relate to the search.",
        Parameters =
        [
            new ToolParameter
            {
                Name = "ranges",
                Description = "comma separated start-end line ranges, most relevant first; empty if none",
                Required = true,
            },
        ],
    };

    private const string SystemPrompt =
        """
        You pick the code that matches a developer's search. The file's candidate regions
        are listed as blocks, each headed `## block N` with its code. best: the numbers of
        the blocks that contain what the developer would edit to carry out the search, most
        relevant first, comma separated. Judge by what the code does, not just shared words -
        a line that merely mentions one of the search's words is not the answer by itself.
        Leave it empty if no block matches.
        """;

    private const string RangeSystemPrompt =
        """
        You find the parts of one source file that match a developer's search. The search
        may be a name, a description of some behaviour or a question, loosely worded or
        misspelled: work out what they are looking for. The file is shown with line
        numbers. ranges: the line ranges that implement, define or directly answer it,
        most relevant first, as start-end, comma separated - usually 1 to 3. Each covers a
        whole function, method, block or section, not a single line. Judge by what the
        code does, not by shared words. Leave it empty if nothing in the file relates.
        """;

    /// <summary>A search in this file is about to start: get the model ready to read it.</summary>
    public static void Prewarm(ISearchModel model) => model.Prewarm(RangeSystemPrompt, [RangeTool]);

    /// <summary>True when the model can be shown the file whole (long lines clipped, blank ones dropped).</summary>
    internal static bool FitsWhole(IReadOnlyList<string> lines)
    {
        var size = 0;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                size += Math.Min(line.TrimEnd().Length, 120) + 8;
                if (size > MaxFileChars)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Lines containing the search's own words, grouped into spans. Instant and often
    /// good enough to look at while the model reads the file.
    /// </summary>
    public static IReadOnlyList<LineSpan> Instant(string query, IReadOnlyList<string> lines) =>
        Group(Hits(query, lines), gap: 2).Take(20).ToList();

    /// <summary>The search's own words, found exactly where they sit in the given lines (0-based line, column and length).</summary>
    public static IEnumerable<(int Line, int Col, int Length)> Occurrences(
        string query, IReadOnlyList<string> lines, int firstLine, int lastLine)
    {
        var words = CodeDiscovery.QuestionWords(query);
        lastLine = Math.Min(lastLine, lines.Count - 1);

        foreach (var word in words)
        {
            var wordStart = CodeDiscovery.NeedsWordStart(word);
            for (var n = Math.Max(0, firstLine); n <= lastLine; n++)
            {
                var line = lines[n];
                var index = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    if (!wordStart || index == 0 || !IsWordChar(line[index - 1]))
                    {
                        yield return (n, index, word.Length);
                    }

                    index = line.IndexOf(word, index + word.Length, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    /// <summary>1-based numbers of the lines containing any of the search's own words.</summary>
    private static List<int> Hits(string query, IReadOnlyList<string> lines)
    {
        var words = CodeDiscovery.QuestionWords(query);
        if (words.Count == 0)
        {
            return [];
        }

        // One delegate per word, built once for the whole scan. Fresh interpreted Regex
        // objects matched line by line over a 30k-line buffer stalled the UI thread for
        // tens of milliseconds before the highlights could paint.
        var matchers = new Func<string, bool>[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            matchers[i] = LiteralMatcher(words[i], CodeDiscovery.NeedsWordStart(words[i]));
        }

        var hits = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (var matcher in matchers)
            {
                if (matcher(lines[i]))
                {
                    hits.Add(i + 1);
                    break;
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// The same rule the regex encoded - a case-insensitive containment, with a word
    /// boundary required before short alphanumeric terms - as an IndexOf scan.
    /// </summary>
    private static Func<string, bool> LiteralMatcher(string word, bool wordStart) =>
        line =>
        {
            var index = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                if (!wordStart || index == 0 || !IsWordChar(line[index - 1]))
                {
                    return true;
                }

                index = line.IndexOf(word, index + word.Length, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        };

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// The candidate regions the model chooses among: each run of keyword hits widened to
    /// the blank-line-bounded block around it, so a block usually covers a whole function.
    /// At most <see cref="MaxCandidates"/> blocks, and only those fitting the context budget
    /// - a block the search never touches is dropped rather than shown truncated.
    /// </summary>
    public static IReadOnlyList<LineSpan> Candidates(string query, IReadOnlyList<string> lines)
    {
        var hits = Hits(query, lines);
        var blocks = new List<LineSpan>();

        foreach (var span in Group(hits, gap: 8))
        {
            var (start, end) = (span.Start, span.End);
            while (start > 1 && end - start < MaxCandidateLines - 1 && !string.IsNullOrWhiteSpace(lines[start - 2]))
            {
                start--;
            }

            while (end < lines.Count && end - start < MaxCandidateLines - 1 && !string.IsNullOrWhiteSpace(lines[end]))
            {
                end++;
            }

            if (blocks.FindIndex(b => start <= b.End + 1 && end >= b.Start - 1) is var i and >= 0)
            {
                blocks[i] = new LineSpan(Math.Min(blocks[i].Start, start), Math.Max(blocks[i].End, end));
            }
            else
            {
                blocks.Add(new LineSpan(start, end));
            }
        }

        // More distinct keyword hits inside a block say more about it than its length;
        // ties break toward the earlier block, which keeps the order stable.
        var scored = blocks
            .Select(b => (Block: b, Hits: hits.Count(h => h >= b.Start && h <= b.End)))
            .OrderByDescending(s => s.Hits)
            .ThenBy(s => s.Block.Start)
            .ToList();

        // Whole blocks only: what does not fit the context budget is dropped, not
        // truncated, because the model judges by what the code does.
        var kept = new List<LineSpan>();
        var budget = MaxCandidateChars;
        foreach (var (block, _) in scored)
        {
            if (kept.Count >= MaxCandidates)
            {
                break;
            }

            var size = block.End - block.Start + 1 +
                       Enumerable.Range(block.Start - 1, block.End - block.Start + 1).Sum(n => lines[n].Length);
            if (size > budget && kept.Count > 0)
            {
                break;
            }

            kept.Add(block);
            budget -= Math.Min(size, budget);
        }

        return kept;
    }

    /// <summary>
    /// The model's picks, or null when it could not answer (no model, error, timeout).
    /// A file that fits is read whole and the model names the ranges itself; a bigger
    /// one whose search words hit it is narrowed to the keyword-hit blocks (see
    /// <see cref="Candidates"/>) for the model to choose among.
    /// </summary>
    public static async Task<IReadOnlyList<LineSpan>?> AskAsync(
        ISearchModel model, string query, string fileName, IReadOnlyList<string> lines, CancellationToken ct)
    {
        IReadOnlyList<LineSpan> candidates = FitsWhole(lines) ? [] : Candidates(query, lines);

        ToolCall? call;
        try
        {
            call = candidates.Count > 0
                ? await model.NextCallAsync(
                    SystemPrompt, BuildCandidatePrompt(query, fileName, lines, candidates), [Tool], ct)
                    .ConfigureAwait(false)
                : await model.NextCallAsync(
                    RangeSystemPrompt, BuildPrompt(query, fileName, lines), [RangeTool], ct)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }

        if (call is null)
        {
            return null;
        }

        return candidates.Count > 0
            ? PickCandidates(call.GetString("best"), candidates)
            : ParseRanges(call.GetString("ranges"), lines.Count);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"(\d+)\s*(?:-|–|to|\.\.|:)\s*(\d+)|(\d+)")]
    private static partial Regex RangeNumbers();

    /// <summary>A line number from the model's reply; digits too long for an int clamp to the end of the file.</summary>
    private static int ParseLine(string digits) => int.TryParse(digits, out var n) ? n : int.MaxValue;

    /// <summary>Maps the model's "1, 3" onto the candidate blocks, best first, clamped and deduped.</summary>
    internal static IReadOnlyList<LineSpan> PickCandidates(string? text, IReadOnlyList<LineSpan> candidates)
    {
        var picks = new List<LineSpan>();

        foreach (Match match in Digits().Matches(text ?? ""))
        {
            // A reply of digits too long for an int is garbage, not a pick.
            if (!int.TryParse(match.Value, out var number))
            {
                continue;
            }

            var index = number - 1;
            if (index < 0 || index >= candidates.Count)
            {
                continue;
            }

            if (picks.All(p => p != candidates[index]))
            {
                picks.Add(candidates[index]);
            }
        }

        return picks;
    }

    internal static string BuildCandidatePrompt(
        string query, string fileName, IReadOnlyList<string> lines, IReadOnlyList<LineSpan> candidates)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Search: {query}");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine();

        foreach (var (i, block) in candidates.Index())
        {
            builder.AppendLine($"## block {i + 1}");
            for (var n = block.Start; n <= block.End; n++)
            {
                var text = lines[n - 1];
                builder.AppendLine(text.Length > 200 ? text[..200] : text);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    internal static string BuildPrompt(string query, string fileName, IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Search: {query}");
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine();

        foreach (var (number, text) in Condense(query, lines))
        {
            builder.Append(number).Append("| ").AppendLine(text);
        }

        return builder.ToString();
    }

    /// <summary>The numbered lines the model is shown: all of them when they fit.</summary>
    internal static IEnumerable<(int Number, string Text)> Condense(string query, IReadOnlyList<string> lines)
    {
        var all = lines.Select((text, i) => (Number: i + 1, Text: text)).ToList();
        if (Size(all) <= MaxFileChars)
        {
            return all;
        }

        // Pass one: clip long lines and drop blank ones; line numbers stay true.
        var clipped = all
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => (l.Number, Text: Clip(l.Text.TrimEnd())))
            .ToList();
        if (Size(clipped) <= MaxFileChars)
        {
            return clipped;
        }

        // Pass two: the question's neighbourhoods, plus declarations for the outline.
        var near = new HashSet<int>();
        foreach (var span in Instant(query, lines))
        {
            for (var n = Math.Max(1, span.Start - 15); n <= Math.Min(lines.Count, span.End + 15); n++)
            {
                near.Add(n);
            }
        }

        var kept = new List<(int Number, string Text)>();
        var size = 0;

        foreach (var line in clipped.Where(l => near.Contains(l.Number) || Declaration.IsMatch(l.Text)))
        {
            size += line.Text.Length + 8;
            if (size > MaxFileChars)
            {
                break;
            }

            kept.Add(line);
        }

        return kept;

        static int Size(List<(int Number, string Text)> set) => set.Sum(l => l.Text.Length + 8);

        static string Clip(string text) => text.Length > 120 ? text[..120] : text;
    }

    private static readonly Regex Declaration = new(
        @"^\s*(export\s+)?(public|private|protected|internal|static|async|abstract|override|sealed|partial|def|class|interface|struct|enum|record|function|fn|func|const|let|var|type|module|namespace|impl|#+\s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads "12-30, 45-52" (and the looser "12 to 30", "12..30", a bare "7") into
    /// clamped, merged spans in the model's order.
    /// </summary>
    internal static IReadOnlyList<LineSpan> ParseRanges(string? text, int lineCount)
    {
        var spans = new List<LineSpan>();

        foreach (Match match in RangeNumbers().Matches(text ?? ""))
        {
            int start, end;
            if (match.Groups[3].Success)
            {
                start = end = ParseLine(match.Groups[3].Value);
            }
            else
            {
                start = ParseLine(match.Groups[1].Value);
                end = ParseLine(match.Groups[2].Value);
            }

            if (end < start)
            {
                (start, end) = (end, start);
            }

            start = Math.Clamp(start, 1, Math.Max(1, lineCount));
            end = Math.Clamp(end, start, Math.Min(lineCount, start + MaxSpanLines - 1));

            if (spans.FindIndex(s => start <= s.End + 1 && end >= s.Start - 1) is var i and >= 0)
            {
                spans[i] = new LineSpan(Math.Min(spans[i].Start, start), Math.Max(spans[i].End, end));
            }
            else if (spans.Count < MaxSpans)
            {
                spans.Add(new LineSpan(start, end));
            }
        }

        return spans;
    }

    internal static IEnumerable<LineSpan> Group(IReadOnlyList<int> sortedLines, int gap)
    {
        if (sortedLines.Count == 0)
        {
            yield break;
        }

        var (start, end) = (sortedLines[0], sortedLines[0]);
        foreach (var line in sortedLines.Skip(1))
        {
            if (line - end <= gap + 1)
            {
                end = line;
                continue;
            }

            yield return new LineSpan(start, end);
            (start, end) = (line, line);
        }

        yield return new LineSpan(start, end);
    }
}
