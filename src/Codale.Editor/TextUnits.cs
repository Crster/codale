using System.Buffers;
using System.Globalization;
using System.Text;

namespace Codale.Editor;

/// <summary>
/// Unicode-aware column arithmetic. The caret never lands inside a grapheme cluster (a surrogate
/// pair, an emoji ZWJ sequence, a base letter with its combining marks), so it can never drift off
/// the glyph it is drawn against.
/// </summary>
public static class TextUnits
{
    /// <summary>
    /// True when every character is printable ASCII: one character is one cell, one cluster, and
    /// one monospace advance, so columns map to pixels by multiplication.
    /// </summary>
    public static bool IsSimple(ReadOnlySpan<char> text) =>
        text.IndexOfAnyExceptInRange(' ', '~') < 0;

    /// <summary>The column after the grapheme cluster starting at <paramref name="col"/>.</summary>
    public static int NextBoundary(string line, int col)
    {
        if (col >= line.Length)
        {
            return line.Length;
        }

        var c = line[col];
        if (c < 0x300 && (col + 1 >= line.Length || line[col + 1] < 0x300))
        {
            // Fast path: Latin text followed by a non-combining character.
            return col + 1;
        }

        return col + Math.Max(1, StringInfo.GetNextTextElementLength(line.AsSpan(col)));
    }

    /// <summary>The start of the grapheme cluster that ends at <paramref name="col"/>.</summary>
    public static int PreviousBoundary(string line, int col)
    {
        if (col <= 0)
        {
            return 0;
        }

        col = Math.Min(col, line.Length);

        // Below U+0300 nothing combines with what came before (no marks, joiners or surrogates),
        // so the previous character starts its own cluster.
        if (line[col - 1] < 0x300)
        {
            return col - 1;
        }

        // Walk clusters from a safe point before the caret: a cluster is rarely longer than a
        // few dozen code units, so start a little way back at a non-continuation character.
        var from = Math.Max(0, col - 64);
        while (from > 0 && !IsSafeStart(line, from))
        {
            from--;
        }

        var previous = from;
        var at = from;
        while (at < col)
        {
            previous = at;
            at += Math.Max(1, StringInfo.GetNextTextElementLength(line.AsSpan(at)));
        }

        return previous;
    }

    /// <summary>Snaps a column onto the nearest cluster boundary at or before it.</summary>
    public static int Snap(string line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);
        if (col == 0 || col == line.Length)
        {
            return col;
        }

        var c = line[col];
        if (c < 0x300 && !char.IsLowSurrogate(c) && line[col - 1] < 0x300)
        {
            return col;
        }

        var prev = PreviousBoundary(line, col);
        return NextBoundary(line, prev) == col ? col : prev;
    }

    private static bool IsJoiner(char c) => c is '‍' or '️' or '︎';

    private static bool IsSafeStart(string line, int at)
    {
        var c = line[at];
        return c < 0x300 && !IsJoiner(c) && (at == 0 || line[at - 1] != '‍');
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The word (letters, digits, underscore) touching <paramref name="col"/>; empty range when none.</summary>
    public static (int Start, int End) WordAt(string line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);
        var start = col;
        while (start > 0 && IsWordChar(line[start - 1]))
        {
            start--;
        }

        var end = col;
        while (end < line.Length && IsWordChar(line[end]))
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>
    /// Word or symbol run for a double click: letters/digits/underscore group together, other
    /// non-space characters group together, whitespace groups together.
    /// </summary>
    public static (int Start, int End) RunAt(string line, int col)
    {
        if (line.Length == 0)
        {
            return (0, 0);
        }

        col = Math.Clamp(col, 0, line.Length - 1);
        var kind = Kind(line[col]);
        var start = col;
        while (start > 0 && Kind(line[start - 1]) == kind)
        {
            start--;
        }

        var end = col;
        while (end < line.Length && Kind(line[end]) == kind)
        {
            end++;
        }

        return (start, NextBoundaryOrSame(line, end));

        static int Kind(char c) => IsWordChar(c) ? 0 : char.IsWhiteSpace(c) ? 1 : 2;
    }

    private static int NextBoundaryOrSame(string line, int col) => Snap(line, col) == col ? col : NextBoundary(line, col);

    /// <summary>Ctrl+Right: past any non-word characters, then past the next word.</summary>
    public static int NextWordStop(string line, int col)
    {
        while (col < line.Length && char.IsWhiteSpace(line[col]))
        {
            col++;
        }

        if (col < line.Length && !IsWordChar(line[col]))
        {
            while (col < line.Length && !IsWordChar(line[col]) && !char.IsWhiteSpace(line[col]))
            {
                col = NextBoundary(line, col);
            }

            return col;
        }

        while (col < line.Length && IsWordChar(line[col]))
        {
            col++;
        }

        return col;
    }

    /// <summary>Ctrl+Left: back over whitespace, then to the start of the word or symbol run.</summary>
    public static int PreviousWordStop(string line, int col)
    {
        while (col > 0 && char.IsWhiteSpace(line[col - 1]))
        {
            col--;
        }

        if (col > 0 && !IsWordChar(line[col - 1]))
        {
            while (col > 0 && !IsWordChar(line[col - 1]) && !char.IsWhiteSpace(line[col - 1]))
            {
                col = PreviousBoundary(line, col);
            }

            return col;
        }

        while (col > 0 && IsWordChar(line[col - 1]))
        {
            col--;
        }

        return col;
    }

    /// <summary>
    /// Monospace cells a cluster occupies: 0 for controls, 2 for wide East Asian text and emoji,
    /// otherwise 1. Used to fold wrapped lines; drawing positions come from the real text layout.
    /// </summary>
    public static int CellWidth(ReadOnlySpan<char> cluster)
    {
        if (cluster.IsEmpty)
        {
            return 0;
        }

        if (Rune.DecodeFromUtf16(cluster, out var rune, out _) != OperationStatus.Done)
        {
            return 1;
        }

        var value = rune.Value;
        if (value < 0x20)
        {
            return 0;
        }

        if (value < 0x1100)
        {
            return cluster.Length > 1 && cluster.Contains('️') ? 2 : 1;
        }

        return IsWide(value) || cluster.Length > 2 || cluster.Contains('️') ? 2 : 1;
    }

    private static bool IsWide(int cp) =>
        cp is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE4F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F900 and <= 0x1F9FF
            or >= 0x1F680 and <= 0x1F6FF
            or >= 0x1FA70 and <= 0x1FAFF
            or >= 0x2600 and <= 0x27BF
            or >= 0x20000 and <= 0x3FFFD;

    /// <summary>
    /// Splits a line into wrapped rows of at most <paramref name="wrapCells"/> cells, breaking
    /// only between clusters. Fills <paramref name="rowStarts"/> with each row's first column
    /// (the first entry is always 0) and returns the row count.
    /// </summary>
    public static int Wrap(string line, int wrapCells, int tabWidth, List<int>? rowStarts)
    {
        rowStarts?.Clear();
        rowStarts?.Add(0);
        if (wrapCells <= 0 || wrapCells == int.MaxValue)
        {
            return 1;
        }

        if (IsSimple(line))
        {
            var rows = line.Length == 0 ? 1 : (line.Length + wrapCells - 1) / wrapCells;
            if (rowStarts is not null)
            {
                for (var r = 1; r < rows; r++)
                {
                    rowStarts.Add(r * wrapCells);
                }
            }

            return rows;
        }

        var count = 1;
        var cells = 0;
        var col = 0;
        while (col < line.Length)
        {
            var next = NextBoundary(line, col);
            var width = line[col] == '\t' ? tabWidth - (cells % tabWidth) : CellWidth(line.AsSpan(col, next - col));
            if (cells > 0 && cells + width > wrapCells)
            {
                count++;
                rowStarts?.Add(col);
                cells = 0;
                width = line[col] == '\t' ? tabWidth : width;
            }

            cells += width;
            col = next;
        }

        return count;
    }

    /// <summary>Cells of a whole line, tabs expanded: the rough width the no-wrap extent is sized from.</summary>
    public static int CellCount(string line, int tabWidth)
    {
        if (IsSimple(line))
        {
            return line.Length;
        }

        var cells = 0;
        var col = 0;
        while (col < line.Length)
        {
            var next = NextBoundary(line, col);
            cells += line[col] == '\t' ? tabWidth - (cells % tabWidth) : CellWidth(line.AsSpan(col, next - col));
            col = next;
        }

        return cells;
    }
}
