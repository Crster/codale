using Codale.App.Services;

namespace Codale.App.Controls;

/// <summary>A position in the document: 0-based line and 0-based column.</summary>
public readonly record struct DocumentPosition(int Line, int Col) : IComparable<DocumentPosition>
{
    public int CompareTo(DocumentPosition other)
    {
        var byLine = Line.CompareTo(other.Line);
        return byLine != 0 ? byLine : Col.CompareTo(other.Col);
    }

    public static bool operator <(DocumentPosition left, DocumentPosition right) => left.CompareTo(right) < 0;
    public static bool operator >(DocumentPosition left, DocumentPosition right) => left.CompareTo(right) > 0;
}

/// <summary>
/// The editor's text: a list of lines, never empty, with the editing primitives the
/// control drives. Newlines are normalized on load and re-joined with CRLF on the way
/// out, matching what a Windows file on disk expects. Multi-line edits arrive as one
/// (range, replacement) pair so undo stays one step.
/// </summary>
internal sealed class TextDocument
{
    /// <summary>
    /// Spaces a Tab keypress inserts and tab stops expand to. A static settable from
    /// Settings, so every document follows the one preference.
    /// </summary>
    public static int TabWidth = AppSettings.EditorTabWidth;

    /// <summary>The lines. Always holds at least one entry, possibly empty.</summary>
    public List<string> Lines { get; private set; } = [""];

    /// <summary>Loads new text, replacing everything. Undo history is the caller's concern.</summary>
    public void Load(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        // Split always yields at least one entry, so the never-empty invariant holds.
        Lines = [.. normalized.Split('\n')];
    }

    public string GetText() => string.Join(Environment.NewLine, Lines);

    /// <summary>The text between two ordered positions, lines joined with "\n" (the form <see cref="Replace"/> takes back).</summary>
    public string GetRange(DocumentPosition start, DocumentPosition end)
    {
        if (start.Line == end.Line)
        {
            return Lines[start.Line][start.Col..end.Col];
        }

        var builder = new System.Text.StringBuilder();
        builder.Append(Lines[start.Line], start.Col, Lines[start.Line].Length - start.Col);
        for (var line = start.Line + 1; line < end.Line; line++)
        {
            builder.Append('\n').Append(Lines[line]);
        }

        builder.Append('\n').Append(Lines[end.Line], 0, end.Col);
        return builder.ToString();
    }

    public int LineCount => Lines.Count;

    public string GetLine(int line) =>
        line >= 0 && line < Lines.Count ? Lines[line] : string.Empty;

    /// <summary>Replaces the span between two positions with text; returns the position after the inserted text.</summary>
    public DocumentPosition Replace(DocumentPosition start, DocumentPosition end, string text)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        text = text?.Replace("\r\n", "\n").Replace('\r', '\n') ?? string.Empty;

        var head = Lines[start.Line][..start.Col];
        var tail = Lines[end.Line][end.Col..];

        // The overwhelmingly common edit is one character (or a paste) on one line:
        // no line surgery needed, one string allocation and done. Every keystroke was
        // otherwise paying for a Split, a list rebuild and two range memmoves.
        if (start.Line == end.Line && !text.Contains('\n'))
        {
            Lines[start.Line] = head + text + tail;
            return new DocumentPosition(start.Line, head.Length + text.Length);
        }

        var inserted = text.Split('\n');
        var replacement = new List<string>(inserted.Length + 1);
        if (inserted.Length == 1)
        {
            // Deleting across lines (or replacing a multi-line span with one line)
            // collapses to a single joined line, not a head line plus a tail line.
            replacement.Add(head + inserted[0] + tail);
        }
        else
        {
            replacement.Add(head + inserted[0]);
            for (var i = 1; i < inserted.Length - 1; i++)
            {
                replacement.Add(inserted[i]);
            }

            replacement.Add(inserted[^1] + tail);
        }

        Lines.RemoveRange(start.Line, end.Line - start.Line + 1);
        Lines.InsertRange(start.Line, replacement);

        // A single-line insert keeps the head before it, so the caret column counts it.
        var col = (inserted.Length == 1 ? head.Length : 0) + inserted[^1].Length;
        return new DocumentPosition(start.Line + inserted.Length - 1, col);
    }

    /// <summary>
    /// The leading whitespace of a line, used to auto-indent a new line under its
    /// predecessor.
    /// </summary>
    public string IndentOf(int line)
    {
        var text = GetLine(line);
        var count = 0;
        while (count < text.Length && (text[count] == ' ' || text[count] == '\t'))
        {
            count++;
        }

        return text[..count];
    }

    /// <summary>Expands tabs so a monospace column mapping stays exact.</summary>
    public static string ExpandTabs(string line)
    {
        if (!line.Contains('\t'))
        {
            return line;
        }

        var builder = new System.Text.StringBuilder(line.Length + 8);
        foreach (var ch in line)
        {
            if (ch == '\t')
            {
                builder.Append(' ', TabWidth - (builder.Length % TabWidth));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    /// <summary>Maps a column in the tab-expanded rendering back to the raw line.</summary>
    public static int RawColFromExpanded(string line, int expandedCol)
    {
        var expanded = 0;
        for (var raw = 0; raw <= line.Length; raw++)
        {
            if (expanded >= expandedCol)
            {
                return raw;
            }

            if (raw < line.Length && line[raw] == '\t')
            {
                expanded += TabWidth - (expanded % TabWidth);
            }
            else
            {
                expanded++;
            }
        }

        return line.Length;
    }

    /// <summary>Maps a raw column to its tab-expanded position.</summary>
    public static int ExpandedColFromRaw(string line, int rawCol)
    {
        var expanded = 0;
        for (var raw = 0; raw < rawCol && raw < line.Length; raw++)
        {
            if (line[raw] == '\t')
            {
                expanded += TabWidth - (expanded % TabWidth);
            }
            else
            {
                expanded++;
            }
        }

        return expanded;
    }
}
