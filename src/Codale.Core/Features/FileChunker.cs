namespace Codale.Core.Features;

/// <summary>Cuts a file into overlapping stretches of lines, the unit file search finds and context quotes.</summary>
public static class FileChunker
{
    public const int MaxLines = 40;
    public const int MaxChars = 1500;
    private const int OverlapLines = 5;

    /// <summary>Files past this size are not indexed: generated, minified or data files crowd out the code.</summary>
    public const int MaxFileBytes = 200_000;

    public static IReadOnlyList<(int StartLine, string Text)> Chunk(string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var chunks = new List<(int, string)>();
        var start = 0;

        while (start < lines.Length)
        {
            var end = start;
            var chars = 0;
            while (end < lines.Length && end - start < MaxLines && (chars + lines[end].Length < MaxChars || end == start))
            {
                chars += lines[end].Length + 1;
                end++;
            }

            var text = string.Join('\n', lines[start..end]).Trim();
            if (text.Length > 0)
            {
                chunks.Add((start + 1, text));
            }

            if (end >= lines.Length)
            {
                break;
            }

            start = Math.Max(end - OverlapLines, start + 1);
        }

        return chunks;
    }

    /// <summary>True for content worth indexing: text, not too large.</summary>
    public static bool IsIndexable(string content) =>
        content.Length is > 0 and <= MaxFileBytes && !content.Contains('\0');
}
