namespace Codale.Editor;

/// <summary>
/// Bookmarked lines (Ctrl+F2). They ride along with edits: inserting lines above a marker moves
/// it down, and deleting the lines around it pulls it up onto the line the deletion joined into.
/// </summary>
public sealed class LineMarkers
{
    private readonly TextBuffer _buffer;
    private readonly List<int> _lines = [];

    public LineMarkers(TextBuffer buffer)
    {
        _buffer = buffer;
        _buffer.Changed += OnChanged;
        _buffer.Reloaded += () =>
        {
            if (_lines.Count > 0)
            {
                _lines.Clear();
                Changed?.Invoke();
            }
        };
    }

    public event Action? Changed;

    /// <summary>Marked lines, ascending.</summary>
    public IReadOnlyList<int> Lines => _lines;

    public int Count => _lines.Count;

    public bool Contains(int line) => _lines.BinarySearch(line) >= 0;

    public void Toggle(int line)
    {
        line = Math.Clamp(line, 0, _buffer.LineCount - 1);
        var at = _lines.BinarySearch(line);
        if (at >= 0)
        {
            _lines.RemoveAt(at);
        }
        else
        {
            _lines.Insert(~at, line);
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_lines.Count > 0)
        {
            _lines.Clear();
            Changed?.Invoke();
        }
    }

    /// <summary>The next marked line after <paramref name="line"/> (wrapping), or -1.</summary>
    public int Next(int line)
    {
        if (_lines.Count == 0)
        {
            return -1;
        }

        var at = _lines.BinarySearch(line + 1);
        at = at >= 0 ? at : ~at;
        return at < _lines.Count ? _lines[at] : _lines[0];
    }

    /// <summary>The previous marked line before <paramref name="line"/> (wrapping), or -1.</summary>
    public int Previous(int line)
    {
        if (_lines.Count == 0)
        {
            return -1;
        }

        var at = _lines.BinarySearch(line - 1);
        at = at >= 0 ? at : ~at - 1;
        return at >= 0 ? _lines[at] : _lines[^1];
    }

    private void OnChanged(TextChange change)
    {
        if (_lines.Count == 0 || change.LineDelta == 0)
        {
            return;
        }

        var moved = false;
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            // Lines pushed down by a break typed at column 0 of a marked line take the marker along.
            var pushedDown = line == change.Start.Line && change.Start.Col == 0
                && change.OldEnd == change.Start && change.NewEnd.Col == 0;
            if (line < change.Start.Line || (line == change.Start.Line && !pushedDown))
            {
                continue;
            }

            int mapped;
            if (line > change.OldEnd.Line || pushedDown)
            {
                mapped = line + change.LineDelta;
            }
            else
            {
                // Inside the replaced lines: stay put while they still exist, else join the end.
                mapped = Math.Min(line, change.NewEnd.Line);
            }

            if (mapped != line)
            {
                _lines[i] = mapped;
                moved = true;
            }
        }

        if (moved)
        {
            var distinct = _lines.Distinct().OrderBy(l => l).ToList();
            _lines.Clear();
            _lines.AddRange(distinct);
            Changed?.Invoke();
        }
    }
}
