namespace Codale.Git;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

public sealed record DiffLine
{
    public required DiffLineKind Kind { get; init; }
    public required string Text { get; init; }

    /// <summary>Line number in the original file, or null for an added line.</summary>
    public int? OldNumber { get; init; }

    /// <summary>Line number in the new file, or null for a removed line.</summary>
    public int? NewNumber { get; init; }

    /// <summary>The numbers for the diff gutter; empty rather than blank-null so the column stays put.</summary>
    public string OldText => OldNumber?.ToString() ?? "";

    public string NewText => NewNumber?.ToString() ?? "";

    /// <summary>One gutter number for a single-column diff: the new line's, or the removed line's old one.</summary>
    public string GutterText => (NewNumber ?? OldNumber)?.ToString() ?? "";

    /// <summary>The unified-diff mark for the line, for a sign column beside the text.</summary>
    public string Sign => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        _ => "",
    };
}

public sealed record DiffHunk
{
    public required string Header { get; init; }
    public IReadOnlyList<DiffLine> Lines { get; init; } = [];
}

public sealed record FileDiff
{
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public bool IsBinary { get; init; }
    public bool IsNew { get; init; }
    public bool IsDeleted { get; init; }

    private readonly IReadOnlyList<DiffHunk> _hunks = [];

    public IReadOnlyList<DiffHunk> Hunks
    {
        get => _hunks;
        init
        {
            _hunks = value;
            Added = value.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));
            Removed = value.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));
        }
    }

    /// <summary>Computed once when the hunks are set: the diff tab sums these across all files per refresh, and a lazy cache field would make record equality depend on whether it had been read.</summary>
    public int Added { get; private set; }

    public int Removed { get; private set; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Stat => IsBinary ? "binary" : $"+{Added} −{Removed}";

    /// <summary>The folder the file lives in, repo-relative with forward slashes; empty at the root.</summary>
    public string Directory
    {
        get
        {
            var slash = Path.Replace('\\', '/').LastIndexOf('/');
            return slash <= 0 ? "" : Path[..slash].Replace('\\', '/');
        }
    }

    /// <summary>git's one-letter status for the file: A, D, R or M.</summary>
    public string ChangeLetter =>
        IsNew ? "A" : IsDeleted ? "D" : OldPath is not null && OldPath != Path ? "R" : "M";

    /// <summary>The halves of <see cref="Stat"/>, for colouring separately; empty for binaries.</summary>
    public string AddedText => IsBinary ? "" : $"+{Added}";

    public string RemovedText => IsBinary ? "binary" : $"−{Removed}";
}

/// <summary>
/// Parses git's unified diff output into a structure the review panel can render.
/// </summary>
/// <remarks>
/// Parsing the text rather than using a diff library because git has already done the
/// hard part - rename detection, binary detection, whitespace rules and the user's own
/// diff configuration all apply, and the output is stable and well specified.
/// </remarks>
public static class GitDiffParser
{
    public static IReadOnlyList<FileDiff> Parse(string unifiedDiff)
    {
        var files = new List<FileDiff>();

        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return files;
        }

        string? path = null;
        string? oldPath = null;
        var isBinary = false;
        var isNew = false;
        var isDeleted = false;
        var hunks = new List<DiffHunk>();

        string? hunkHeader = null;
        var hunkLines = new List<DiffLine>();
        var oldLine = 0;
        var newLine = 0;

        void FlushHunk()
        {
            if (hunkHeader is not null)
            {
                hunks.Add(new DiffHunk { Header = hunkHeader, Lines = hunkLines.ToList() });
                hunkLines.Clear();
                hunkHeader = null;
            }
        }

        void FlushFile()
        {
            FlushHunk();

            if (path is not null)
            {
                files.Add(new FileDiff
                {
                    Path = path,
                    OldPath = oldPath == path ? null : oldPath,
                    IsBinary = isBinary,
                    IsNew = isNew,
                    IsDeleted = isDeleted,
                    Hunks = hunks.ToList(),
                });
            }

            hunks.Clear();
            path = null;
            oldPath = null;
            isBinary = false;
            isNew = false;
            isDeleted = false;
        }

        foreach (var raw in TextDiff.SplitLf(unifiedDiff))
        {
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                (oldPath, path) = ParseDiffHeader(raw);
                continue;
            }

            if (path is null)
            {
                continue;
            }

            if (raw.StartsWith("new file mode", StringComparison.Ordinal))
            {
                isNew = true;
            }
            else if (raw.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                isDeleted = true;
            }
            else if (raw.StartsWith("Binary files ", StringComparison.Ordinal) ||
                     raw.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                isBinary = true;
            }
            else if (raw.StartsWith("rename from ", StringComparison.Ordinal))
            {
                oldPath = raw["rename from ".Length..];
            }
            else if (raw.StartsWith("rename to ", StringComparison.Ordinal))
            {
                path = raw["rename to ".Length..];
            }
            else if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushHunk();
                hunkHeader = raw;
                (oldLine, newLine) = ParseHunkHeader(raw);
            }
            else if (hunkHeader is not null)
            {
                if (raw.StartsWith('+'))
                {
                    hunkLines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Added,
                        Text = raw[1..],
                        NewNumber = newLine++,
                    });
                }
                else if (raw.StartsWith('-'))
                {
                    hunkLines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Removed,
                        Text = raw[1..],
                        OldNumber = oldLine++,
                    });
                }
                else if (raw.StartsWith(' '))
                {
                    hunkLines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Context,
                        Text = raw[1..],
                        OldNumber = oldLine++,
                        NewNumber = newLine++,
                    });
                }

                // "\ No newline at end of file" and anything else is not a content line.
            }
        }

        FlushFile();
        return files;
    }

    /// <summary>
    /// Pulls the two paths out of <c>diff --git a/x b/y</c>. Paths may contain spaces,
    /// so the a/ and b/ prefixes are used as anchors rather than splitting on space.
    /// </summary>
    private static (string? Old, string? New) ParseDiffHeader(string line)
    {
        var body = line["diff --git ".Length..];

        var separator = body.IndexOf(" b/", StringComparison.Ordinal);
        if (separator < 0 || !body.StartsWith("a/", StringComparison.Ordinal))
        {
            return (null, body.Trim());
        }

        var oldPath = body[2..separator];
        var newPath = body[(separator + 3)..];
        return (oldPath, newPath);
    }

    /// <summary>Reads the starting line numbers out of <c>@@ -a,b +c,d @@</c>.</summary>
    internal static (int Old, int New) ParseHunkHeader(string header)
    {
        var oldStart = 0;
        var newStart = 0;

        var minus = header.IndexOf('-');
        var plus = header.IndexOf('+');

        if (minus >= 0)
        {
            oldStart = ReadNumber(header, minus + 1);
        }

        if (plus >= 0)
        {
            newStart = ReadNumber(header, plus + 1);
        }

        return (oldStart, newStart);

        static int ReadNumber(string text, int start)
        {
            var end = start;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            return end > start && int.TryParse(text[start..end], out var value) ? value : 0;
        }
    }
}
