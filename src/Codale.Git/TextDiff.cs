using Codale.Core.Agents;

namespace Codale.Git;

/// <summary>
/// Diffs that do not come from git: two texts the agent swapped (an Edit's old and new
/// strings, a file before and after a session), a CLI's own structured patch, or the
/// bare unified-diff body a permission request carries. Every producer returns the same
/// <see cref="FileDiff"/> the git views already render, so one diff template serves all.
/// </summary>
public static class TextDiff
{
    /// <summary>Lines of unchanged text kept around each change in a numbered diff.</summary>
    public const int DefaultContext = 3;

    /// <summary>
    /// Past this many edits the texts share next to nothing worth aligning; the diff
    /// falls back to "all of it replaced" instead of spending memory on the trace.
    /// </summary>
    private const int MaxEditDistance = 4000;

    /// <summary>
    /// Line diff of two whole texts. Null on the old side reads as a new file, null on
    /// the new side as a deletion.
    /// </summary>
    /// <param name="numbered">
    /// False for snippets (an Edit's old/new strings): their position in the file is
    /// unknown, so the gutter stays blank and every line is shown rather than trimmed
    /// to context - there is no file around them to trim towards.
    /// </param>
    public static FileDiff Compute(
        string path,
        string? oldText,
        string? newText,
        bool numbered = true,
        int context = DefaultContext,
        string? header = null)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        var ops = Diff(a, b);

        var hunks = BuildHunks(ops, a, b, numbered ? context : int.MaxValue, numbered, header);

        return new FileDiff
        {
            Path = path,
            IsNew = oldText is null && newText is not null,
            IsDeleted = newText is null && oldText is not null,
            Hunks = hunks,
        };
    }

    /// <summary>A CLI's structured patch: hunks with real line numbers, kept as they came.</summary>
    public static FileDiff FromPatch(string path, IReadOnlyList<FilePatchHunk> patch, bool isNew = false)
    {
        var hunks = new List<DiffHunk>(patch.Count);

        foreach (var hunk in patch)
        {
            var oldLine = hunk.OldStart;
            var newLine = hunk.NewStart;
            var lines = new List<DiffLine>(hunk.Lines.Count);

            foreach (var raw in hunk.Lines)
            {
                var line = ReadPrefixedLine(raw, ref oldLine, ref newLine, numbered: true);
                if (line is not null)
                {
                    lines.Add(line);
                }
            }

            hunks.Add(new DiffHunk { Header = HunkHeader(lines), Lines = lines });
        }

        return new FileDiff { Path = path, IsNew = isNew, Hunks = hunks };
    }

    /// <summary>
    /// A unified-diff body with or without its file headers: a full <c>diff --git</c>
    /// block, a run of <c>@@</c> hunks, or bare +/- lines (producers send all three). Lines
    /// without a hunk header get no numbers - there is nothing to count them from.
    /// </summary>
    public static FileDiff FromUnified(string path, string? unified, bool isNew = false, bool isDeleted = false)
    {
        if (string.IsNullOrEmpty(unified))
        {
            return new FileDiff { Path = path, IsNew = isNew, IsDeleted = isDeleted };
        }

        if (unified.Contains("diff --git ", StringComparison.Ordinal) &&
            GitDiffParser.Parse(unified) is [var parsed, ..])
        {
            return parsed with { Path = path };
        }

        var hunks = new List<DiffHunk>();
        var lines = new List<DiffLine>();
        string header = "";
        var numbered = false;
        var oldLine = 0;
        var newLine = 0;

        void Flush()
        {
            if (lines.Count > 0)
            {
                hunks.Add(new DiffHunk { Header = header, Lines = lines.ToList() });
                lines.Clear();
            }
        }

        foreach (var raw in SplitLf(unified))
        {
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                header = raw;
                numbered = true;
                (oldLine, newLine) = GitDiffParser.ParseHunkHeader(raw);
                continue;
            }

            // File headers only precede the first hunk; inside one, "---" is a removed "--".
            if (hunks.Count == 0 && lines.Count == 0 &&
                (raw.StartsWith("--- ", StringComparison.Ordinal) || raw.StartsWith("+++ ", StringComparison.Ordinal)))
            {
                continue;
            }

            if (ReadPrefixedLine(raw, ref oldLine, ref newLine, numbered) is { } line)
            {
                lines.Add(line);
            }
        }

        Flush();

        // A trailing newline leaves one empty context row behind; it is not content.
        if (hunks is [.., var last] && last.Lines is [.., { Kind: DiffLineKind.Context, Text: "" }])
        {
            hunks[^1] = last with { Lines = last.Lines.Take(last.Lines.Count - 1).ToList() };
        }

        return new FileDiff { Path = path, IsNew = isNew, IsDeleted = isDeleted, Hunks = hunks };
    }

    /// <summary>Several edits to one file shown as one: their hunks in order.</summary>
    public static FileDiff Concat(string path, IEnumerable<FileDiff> diffs)
    {
        var list = diffs.ToList();
        return new FileDiff
        {
            Path = path,
            IsNew = list.Count > 0 && list[0].IsNew,
            IsDeleted = list.Count > 0 && list[^1].IsDeleted,
            Hunks = list.SelectMany(d => d.Hunks).ToList(),
        };
    }

    private static DiffLine? ReadPrefixedLine(string raw, ref int oldLine, ref int newLine, bool numbered)
    {
        if (raw.StartsWith('\\'))
        {
            // "\ No newline at end of file" describes the line above; it is not one.
            return null;
        }

        var prefix = raw.Length > 0 ? raw[0] : ' ';
        var text = raw.Length > 0 && prefix is '+' or '-' or ' ' ? raw[1..] : raw;

        switch (prefix)
        {
            case '+':
                return new DiffLine { Kind = DiffLineKind.Added, Text = text, NewNumber = numbered ? newLine++ : null };
            case '-':
                return new DiffLine { Kind = DiffLineKind.Removed, Text = text, OldNumber = numbered ? oldLine++ : null };
            default:
                return new DiffLine
                {
                    Kind = DiffLineKind.Context,
                    Text = text,
                    OldNumber = numbered ? oldLine++ : null,
                    NewNumber = numbered ? newLine++ : null,
                };
        }
    }

    /// <summary>
    /// Splits on LF only, dropping the CR of a CRLF pair. <c>ReplaceLineEndings</c> would
    /// also split on form feed, NEL and U+2028/U+2029, which are ordinary characters
    /// inside a source line and must not break a diff line in two.
    /// </summary>
    internal static string[] SplitLf(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i][..^1];
            }
        }

        return lines;
    }

    /// <summary>Lines of a text; a final newline ends the last line rather than opening an empty one.</summary>
    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = SplitLf(text);
        return lines is [.., ""] ? lines[..^1] : lines;
    }

    private enum Op
    {
        Equal,
        Delete,
        Insert,
    }

    /// <summary>One step of the edit script: indexes into the old and new line arrays (-1 when absent).</summary>
    private readonly record struct Step(Op Op, int A, int B);

    private static List<Step> Diff(string[] a, string[] b)
    {
        // Edits are local: trimming the shared head and tail first keeps the Myers
        // search to the part that actually changed.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix &&
               a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        var steps = new List<Step>(a.Length + b.Length);

        for (var i = 0; i < prefix; i++)
        {
            steps.Add(new Step(Op.Equal, i, i));
        }

        steps.AddRange(Myers(a, prefix, a.Length - suffix, b, prefix, b.Length - suffix));

        for (var i = 0; i < suffix; i++)
        {
            steps.Add(new Step(Op.Equal, a.Length - suffix + i, b.Length - suffix + i));
        }

        return steps;
    }

    /// <summary>
    /// Myers' O((N+M)D) shortest edit script over a[aLo..aHi) and b[bLo..bHi), with the
    /// trace kept for the backtrack. Deletions come before insertions at each change,
    /// which is the order a reader expects: what went, then what replaced it.
    /// </summary>
    private static List<Step> Myers(string[] a, int aLo, int aHi, string[] b, int bLo, int bHi)
    {
        var n = aHi - aLo;
        var m = bHi - bLo;
        var steps = new List<Step>();

        if (n == 0 || m == 0)
        {
            for (var i = 0; i < n; i++)
            {
                steps.Add(new Step(Op.Delete, aLo + i, -1));
            }

            for (var j = 0; j < m; j++)
            {
                steps.Add(new Step(Op.Insert, -1, bLo + j));
            }

            return steps;
        }

        var max = n + m;
        var offset = max;
        var v = new int[2 * max + 2];

        // The backtrack trace. A full copy of v per step was O(D·(N+M)) - two ~50k-line
        // files allocated gigabytes before the distance cap bailed out. Only k in
        // [-d, d] is written per step, so each step stores just those d+1 entries.
        var trace = new List<int[]>(64);
        var found = -1;

        for (var d = 0; d <= max && d <= MaxEditDistance; d++)
        {
            var vd = new int[d + 1];
            trace.Add(vd);

            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                var y = x - k;

                while (x < n && y < m && a[aLo + x] == b[bLo + y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                vd[(k + d) / 2] = x;

                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }

            if (found >= 0)
            {
                break;
            }
        }

        if (found < 0)
        {
            // Too different to align: show it as replaced wholesale.
            return Myers(a, aLo, aHi, b, bHi, bHi).Concat(Myers(a, aHi, aHi, b, bLo, bHi)).ToList();
        }

        var cx = n;
        var cy = m;

        for (var d = found; d >= 0; d--)
        {
            var vd = trace[d];
            var k = cx - cy;
            var prevK = k == -d || (k != d && vd[(k - 1 + d) / 2] < vd[(k + 1 + d) / 2]) ? k + 1 : k - 1;
            var prevX = vd[(prevK + d) / 2];
            var prevY = prevX - prevK;

            while (cx > prevX && cy > prevY)
            {
                steps.Add(new Step(Op.Equal, aLo + cx - 1, bLo + cy - 1));
                cx--;
                cy--;
            }

            if (d > 0)
            {
                if (cx == prevX)
                {
                    steps.Add(new Step(Op.Insert, -1, bLo + cy - 1));
                }
                else
                {
                    steps.Add(new Step(Op.Delete, aLo + cx - 1, -1));
                }

                cx = prevX;
                cy = prevY;
            }
        }

        steps.Reverse();
        return ReorderDeletesFirst(steps);
    }

    /// <summary>Within each run of changes, removed lines first, then the lines that replaced them.</summary>
    private static List<Step> ReorderDeletesFirst(List<Step> steps)
    {
        var result = new List<Step>(steps.Count);
        var i = 0;

        while (i < steps.Count)
        {
            if (steps[i].Op == Op.Equal)
            {
                result.Add(steps[i++]);
                continue;
            }

            var start = i;
            while (i < steps.Count && steps[i].Op != Op.Equal)
            {
                i++;
            }

            result.AddRange(steps.Skip(start).Take(i - start).Where(s => s.Op == Op.Delete));
            result.AddRange(steps.Skip(start).Take(i - start).Where(s => s.Op == Op.Insert));
        }

        return result;
    }

    private static List<DiffHunk> BuildHunks(
        List<Step> steps, string[] a, string[] b, int context, bool numbered, string? header)
    {
        var hunks = new List<DiffHunk>();
        var changes = steps
            .Select((step, index) => (step, index))
            .Where(s => s.step.Op != Op.Equal)
            .Select(s => s.index)
            .ToList();

        if (changes.Count == 0)
        {
            return hunks;
        }

        var groupStart = changes[0];
        var groupEnd = changes[0];

        void Emit(int firstChange, int lastChange)
        {
            var from = context == int.MaxValue ? 0 : Math.Max(0, firstChange - context);
            var to = context == int.MaxValue ? steps.Count : Math.Min(steps.Count, lastChange + context + 1);
            var lines = new List<DiffLine>(to - from);

            for (var i = from; i < to; i++)
            {
                var step = steps[i];
                lines.Add(step.Op switch
                {
                    Op.Delete => new DiffLine
                    {
                        Kind = DiffLineKind.Removed,
                        Text = a[step.A],
                        OldNumber = numbered ? step.A + 1 : null,
                    },
                    Op.Insert => new DiffLine
                    {
                        Kind = DiffLineKind.Added,
                        Text = b[step.B],
                        NewNumber = numbered ? step.B + 1 : null,
                    },
                    _ => new DiffLine
                    {
                        Kind = DiffLineKind.Context,
                        Text = a[step.A],
                        OldNumber = numbered ? step.A + 1 : null,
                        NewNumber = numbered ? step.B + 1 : null,
                    },
                });
            }

            hunks.Add(new DiffHunk { Header = header ?? (numbered ? HunkHeader(lines) : ""), Lines = lines });
        }

        foreach (var change in changes.Skip(1))
        {
            // Two changes whose context windows touch read better as one hunk.
            if (context != int.MaxValue && change - groupEnd > 2 * context + 1)
            {
                Emit(groupStart, groupEnd);
                groupStart = change;
            }

            groupEnd = change;
        }

        Emit(groupStart, groupEnd);
        return hunks;
    }

    /// <summary>"@@ -12,4 +12,6 @@" from a hunk's own numbered lines.</summary>
    private static string HunkHeader(IReadOnlyList<DiffLine> lines)
    {
        var oldStart = lines.FirstOrDefault(l => l.OldNumber is not null)?.OldNumber ?? 0;
        var newStart = lines.FirstOrDefault(l => l.NewNumber is not null)?.NewNumber ?? 0;
        var oldCount = lines.Count(l => l.Kind != DiffLineKind.Added);
        var newCount = lines.Count(l => l.Kind != DiffLineKind.Removed);
        return $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@";
    }
}
