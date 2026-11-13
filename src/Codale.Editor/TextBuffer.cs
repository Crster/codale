using System.Text;

namespace Codale.Editor;

/// <summary>
/// One edit as the buffer applied it: the range <see cref="Start"/>..<see cref="OldEnd"/> was
/// replaced, and the new text now spans <see cref="Start"/>..<see cref="NewEnd"/>. Line caches
/// splice with <see cref="StartLine"/>, <see cref="OldLineCount"/> and <see cref="NewLineCount"/>.
/// </summary>
public readonly record struct TextChange(DocumentPosition Start, DocumentPosition OldEnd, DocumentPosition NewEnd)
{
    public int StartLine => Start.Line;

    /// <summary>Lines the edit touched before it ran (at least one).</summary>
    public int OldLineCount => OldEnd.Line - Start.Line + 1;

    /// <summary>Lines the edited region spans now (at least one).</summary>
    public int NewLineCount => NewEnd.Line - Start.Line + 1;

    public int LineDelta => NewEnd.Line - OldEnd.Line;

    /// <summary>Where a position after (or inside) the old range lands once this edit applied.</summary>
    public DocumentPosition Map(DocumentPosition position, bool stickToStart = false)
    {
        if (position < Start || (stickToStart && position == Start))
        {
            return position;
        }

        if (position < OldEnd)
        {
            // Inside the replaced text: the closest surviving spot is the end of the new text.
            return stickToStart ? Start : NewEnd;
        }

        return position.Line == OldEnd.Line
            ? new DocumentPosition(NewEnd.Line, NewEnd.Col + (position.Col - OldEnd.Col))
            : new DocumentPosition(position.Line + LineDelta, position.Col);
    }
}

/// <summary>
/// The document text as a list of lines (never empty). Lines are immutable strings, so a line
/// that did not change keeps its reference: render and token caches use reference equality to
/// know a line is still the one they processed. Newlines are normalized on load to "\n" inside
/// and joined with <see cref="NewLine"/> on the way out.
/// </summary>
public sealed class TextBuffer
{
    private readonly List<string> _lines = [""];
    private long _charCount;
    private string? _textCache;
    private int _textCacheVersion = -1;

    /// <summary>The separator <see cref="GetText"/> joins lines with.</summary>
    public string NewLine { get; set; } = "\r\n";

    /// <summary>Bumped by every change; caches compare it to know they are current.</summary>
    public int Version { get; private set; }

    public int LineCount => _lines.Count;

    /// <summary>Characters in the document, not counting line breaks.</summary>
    public long CharCount => _charCount;

    /// <summary>Raised after each edit, in the order edits were applied.</summary>
    public event Action<TextChange>? Changed;

    /// <summary>Raised after the whole text was replaced by <see cref="Load"/>.</summary>
    public event Action? Reloaded;

    public string this[int line] => _lines[line];

    public IReadOnlyList<string> Lines => _lines;

    public string GetLine(int line) => (uint)line < (uint)_lines.Count ? _lines[line] : string.Empty;

    public int LineLength(int line) => (uint)line < (uint)_lines.Count ? _lines[line].Length : 0;

    public DocumentPosition End => new(_lines.Count - 1, _lines[^1].Length);

    /// <summary>
    /// Replaces the whole text. Splits in one pass over the span: CRLF, CR and LF all end a line,
    /// with no intermediate normalized copy of the text.
    /// </summary>
    public void Load(ReadOnlySpan<char> text)
    {
        _lines.Clear();
        _charCount = 0;
        var estimate = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                estimate++;
            }
        }

        _lines.Capacity = Math.Max(_lines.Capacity, estimate + 1);
        while (true)
        {
            var at = text.IndexOfAny('\r', '\n');
            if (at < 0)
            {
                AddLine(text);
                break;
            }

            AddLine(text[..at]);
            var skip = text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n' ? 2 : 1;
            text = text[(at + skip)..];
        }

        Version++;
        _textCache = null;
        Reloaded?.Invoke();
    }

    private void AddLine(ReadOnlySpan<char> line)
    {
        _lines.Add(line.Length == 0 ? string.Empty : new string(line));
        _charCount += line.Length;
    }

    /// <summary>The whole text joined with <see cref="NewLine"/>. Cached until the next change.</summary>
    public string GetText()
    {
        if (_textCache is not null && _textCacheVersion == Version)
        {
            return _textCache;
        }

        var length = _charCount + (long)(_lines.Count - 1) * NewLine.Length;
        if (length > int.MaxValue)
        {
            throw new InvalidOperationException("The document is too large to join into one string.");
        }

        _textCache = string.Create((int)length, (_lines, NewLine), static (span, state) =>
        {
            var (lines, newLine) = state;
            var at = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    newLine.AsSpan().CopyTo(span[at..]);
                    at += newLine.Length;
                }

                lines[i].AsSpan().CopyTo(span[at..]);
                at += lines[i].Length;
            }
        });
        _textCacheVersion = Version;
        return _textCache;
    }

    public DocumentPosition Clamp(DocumentPosition position)
    {
        var line = Math.Clamp(position.Line, 0, _lines.Count - 1);
        return new DocumentPosition(line, Math.Clamp(position.Col, 0, _lines[line].Length));
    }

    /// <summary>The text between two positions (either order), lines joined with "\n".</summary>
    public string GetRange(DocumentPosition start, DocumentPosition end)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        start = Clamp(start);
        end = Clamp(end);
        if (start.Line == end.Line)
        {
            return _lines[start.Line][start.Col..end.Col];
        }

        var builder = new StringBuilder();
        builder.Append(_lines[start.Line], start.Col, _lines[start.Line].Length - start.Col);
        for (var line = start.Line + 1; line < end.Line; line++)
        {
            builder.Append('\n').Append(_lines[line]);
        }

        builder.Append('\n').Append(_lines[end.Line], 0, end.Col);
        return builder.ToString();
    }

    /// <summary>Characters between two positions, counting a line break as <paramref name="newLineLength"/>.</summary>
    public long Distance(DocumentPosition start, DocumentPosition end, int newLineLength = 1)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        if (start.Line == end.Line)
        {
            return end.Col - start.Col;
        }

        long count = _lines[start.Line].Length - start.Col;
        for (var line = start.Line + 1; line < end.Line; line++)
        {
            count += _lines[line].Length + newLineLength;
        }

        return count + newLineLength + end.Col;
    }

    /// <summary>The character offset of a position in <see cref="GetText"/>.</summary>
    public long OffsetOf(DocumentPosition position) => Distance(new DocumentPosition(0, 0), Clamp(position), NewLine.Length);

    /// <summary>
    /// Replaces the range between two positions with <paramref name="text"/> ("\n", "\r\n" or "\r"
    /// separated). Returns the position just after the inserted text.
    /// </summary>
    public DocumentPosition Replace(DocumentPosition start, DocumentPosition end, ReadOnlySpan<char> text) =>
        Replace(start, end, text, out _, captureRemoved: false);

    /// <summary>As <see cref="Replace(DocumentPosition, DocumentPosition, ReadOnlySpan{char})"/>, also handing back the text it removed.</summary>
    public DocumentPosition Replace(DocumentPosition start, DocumentPosition end, ReadOnlySpan<char> text, out string removed) =>
        Replace(start, end, text, out removed, captureRemoved: true);

    private DocumentPosition Replace(DocumentPosition start, DocumentPosition end, ReadOnlySpan<char> text, out string removed, bool captureRemoved)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        start = Clamp(start);
        end = Clamp(end);
        removed = captureRemoved ? GetRange(start, end) : string.Empty;

        var first = _lines[start.Line];
        var last = _lines[end.Line];
        var removedChars = Distance(start, end, 0);
        DocumentPosition newEnd;

        var breakAt = text.IndexOfAny('\r', '\n');
        if (breakAt < 0)
        {
            // The common case - a keystroke, a backspace, a one-line paste: one string, no list surgery.
            var joined = string.Concat(first.AsSpan(0, start.Col), text, last.AsSpan(end.Col));
            if (start.Line != end.Line)
            {
                _lines.RemoveRange(start.Line + 1, end.Line - start.Line);
            }

            _lines[start.Line] = joined;
            newEnd = new DocumentPosition(start.Line, start.Col + text.Length);
        }
        else
        {
            var replacement = new List<string>();
            var head = first.AsSpan(0, start.Col);
            var tail = last.AsSpan(end.Col);
            var rest = text;
            var firstPiece = true;
            while (true)
            {
                var at = rest.IndexOfAny('\r', '\n');
                var piece = at < 0 ? rest : rest[..at];
                if (at < 0)
                {
                    replacement.Add(firstPiece ? string.Concat(head, piece, tail) : string.Concat(piece, tail));
                    newEnd = new DocumentPosition(start.Line + replacement.Count - 1, (firstPiece ? head.Length : 0) + piece.Length);
                    break;
                }

                replacement.Add(firstPiece ? string.Concat(head, piece) : new string(piece));
                firstPiece = false;
                var skip = rest[at] == '\r' && at + 1 < rest.Length && rest[at + 1] == '\n' ? 2 : 1;
                rest = rest[(at + skip)..];
            }

            var oldCount = end.Line - start.Line + 1;
            var common = Math.Min(oldCount, replacement.Count);
            for (var i = 0; i < common; i++)
            {
                _lines[start.Line + i] = replacement[i];
            }

            if (replacement.Count > oldCount)
            {
                _lines.InsertRange(start.Line + oldCount, replacement.GetRange(oldCount, replacement.Count - oldCount));
            }
            else if (replacement.Count < oldCount)
            {
                _lines.RemoveRange(start.Line + common, oldCount - common);
            }
        }

        _charCount += CountChars(text) - removedChars;
        Version++;
        Changed?.Invoke(new TextChange(start, end, newEnd));
        return newEnd;
    }

    private static long CountChars(ReadOnlySpan<char> text)
    {
        long count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                continue;
            }

            if (c == '\r')
            {
                continue;
            }

            count++;
        }

        return count;
    }

    /// <summary>Leading spaces and tabs of a line.</summary>
    public string IndentOf(int line)
    {
        var text = GetLine(line);
        var count = 0;
        while (count < text.Length && text[count] is ' ' or '\t')
        {
            count++;
        }

        return text[..count];
    }
}
