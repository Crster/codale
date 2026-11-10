namespace Codale.Core.Features;

/// <summary>
/// Cuts a file into the units file search finds and context quotes. Cuts fall on symbol boundaries (a method with
/// its doc comment stays whole; small neighbours share a chunk), and a unit too big for one chunk is split into
/// overlapping windows. Each chunk also names the symbols it belongs to, including the enclosing type that its
/// own lines never mention, so a search for "SmartExperience RankAsync" finds the method's body.
/// </summary>
public static class FileChunker
{
    public const int Version = 3;
    public const int MaxLines = 40;
    public const int MaxChars = 1500;
    private const int OverlapLines = 5;
    /// <summary>Doc comments and attributes above a declaration travel with it, up to this many lines.</summary>
    private const int MaxLeading = 8;
    /// <summary>Declarations nested deeper than this are locals and lambdas, not units worth a cut.</summary>
    private const int MaxBoundaryIndent = 12;
    private const int MaxSymbols = 8;

    /// <summary>Files past this size are not indexed: generated, minified or data files crowd out the code.</summary>
    public const int MaxFileBytes = 1_000_000;
    /// <summary>Hand-written source averages well under this per line; bundles and minified files far over it.</summary>
    private const int MaxAverageLineChars = 300;

    private readonly record struct Segment(int Start, int End, IReadOnlyList<string> Symbols);

    public static IReadOnlyList<(int StartLine, string Text, string Symbols)> Chunk(string content, string? path = null)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var chunks = new List<(int, string, string)>();
        var current = new List<Segment>();

        void Flush()
        {
            if (current.Count == 0) return;
            Emit(lines, current[0].Start, current[^1].End, current.SelectMany(s => s.Symbols).Distinct(StringComparer.Ordinal).Take(MaxSymbols), chunks);
            current.Clear();
        }

        foreach (var segment in Segments(lines, path))
        {
            var size = Size(lines, segment.Start, segment.End);
            if (size.Lines == 0) continue;
            if (size.Lines > MaxLines || size.Chars > MaxChars)
            {
                // One unit too big for a chunk: its own overlapping windows, each still named after it.
                Flush();
                Windows(lines, segment, chunks);
                continue;
            }

            if (current.Count > 0)
            {
                var combined = Size(lines, current[0].Start, segment.End);
                if (combined.Lines > MaxLines || combined.Chars > MaxChars) Flush();
            }
            current.Add(segment);
        }

        Flush();
        return chunks;
    }

    /// <summary>Line ranges of the file's units, each with its symbol path (enclosing types, then its own name).</summary>
    private static List<Segment> Segments(string[] lines, string? path)
    {
        var markdown = path is not null && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));
        var starts = new List<(int Line, IReadOnlyList<string> Symbols)>();
        var containers = new Stack<(int Indent, string Name)>();
        var previous = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            string? name = null;
            var isContainer = false;
            if (markdown)
            {
                if (lines[i].StartsWith('#') && lines[i].TrimStart('#') is { Length: > 0 } heading && heading[0] == ' ' && lines[i].Length - heading.Length <= 4)
                    name = heading.Trim();
            }
            else if (SymbolScanner.Match(lines[i]) is { } declaration && declaration.IsBoundary)
            {
                name = declaration.Name;
                isContainer = declaration.IsContainer;
            }

            if (name is null) continue;
            var indent = SymbolScanner.Indent(lines[i]);
            if (indent > MaxBoundaryIndent) continue;
            while (containers.Count > 0 && containers.Peek().Indent >= indent) containers.Pop();
            var symbols = containers.Reverse().Select(c => c.Name).Append(name).ToList();
            if (isContainer) containers.Push((indent, name));

            // The comment block and attributes above a declaration are part of it.
            var start = i;
            while (start > previous + 1 && i - start < MaxLeading && IsLeading(lines[start - 1])) start--;
            starts.Add((start, symbols));
            previous = i;
        }

        var segments = new List<Segment>();
        if (starts.Count == 0 || starts[0].Line > 0) segments.Add(new Segment(0, starts.Count == 0 ? lines.Length : starts[0].Line, []));
        for (var k = 0; k < starts.Count; k++)
            segments.Add(new Segment(starts[k].Line, k + 1 < starts.Count ? starts[k + 1].Line : lines.Length, starts[k].Symbols));
        return segments;
    }

    private static bool IsLeading(string line)
    {
        var text = line.TrimStart();
        return text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal) ||
               text.StartsWith('*') || text.StartsWith('[') || text.StartsWith('@') || text.StartsWith("#[", StringComparison.Ordinal);
    }

    private static (int Lines, int Chars) Size(string[] lines, int start, int end)
    {
        int first = start, last = end;
        while (first < last && string.IsNullOrWhiteSpace(lines[first])) first++;
        while (last > first && string.IsNullOrWhiteSpace(lines[last - 1])) last--;
        var chars = 0;
        for (var i = first; i < last; i++) chars += lines[i].Length + 1;
        return (last - first, chars);
    }

    private static void Windows(string[] lines, Segment segment, List<(int, string, string)> chunks)
    {
        var start = segment.Start;
        while (start < segment.End)
        {
            var end = start;
            var chars = 0;
            while (end < segment.End && end - start < MaxLines && (chars + lines[end].Length < MaxChars || end == start))
            {
                chars += lines[end].Length + 1;
                end++;
            }

            Emit(lines, start, end, segment.Symbols, chunks);
            if (end >= segment.End) break;
            start = Math.Max(end - OverlapLines, start + 1);
        }
    }

    private static void Emit(string[] lines, int start, int end, IEnumerable<string> symbols, List<(int, string, string)> chunks)
    {
        var first = start;
        var last = end;
        while (first < last && string.IsNullOrWhiteSpace(lines[first])) first++;
        while (last > first && string.IsNullOrWhiteSpace(lines[last - 1])) last--;
        var text = string.Join('\n', lines[first..last]);
        if (text.Length > 0) chunks.Add((first + 1, text, string.Join(' ', symbols)));
    }

    /// <summary>True for content worth indexing: text, not too large, not minified.</summary>
    public static bool IsIndexable(string content)
    {
        if (content.Length is 0 or > MaxFileBytes || content.Contains('\0')) return false;
        // Only a big file can be a bundle: a few long lines in a small one are just long lines.
        if (content.Length < 20_000) return true;
        var lineCount = 1;
        foreach (var c in content) if (c == '\n') lineCount++;
        return content.Length / lineCount <= MaxAverageLineChars;
    }
}
