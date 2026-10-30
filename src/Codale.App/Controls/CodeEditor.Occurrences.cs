using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace Codale.App.Controls;

// Selecting a piece of text lightly marks every other copy of it: in the text, and as
// stripes on the scrollbar track.
public sealed partial class CodeEditor
{
    private const int MinOccurrenceLength = 2;
    private const int MaxOccurrenceLength = 200;
    private const int MaxOccurrences = 5000;

    private List<(int Line, int Col, int Length)> _occurrences = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _occurrenceTimer;
    private Windows.UI.Color _occurrenceColor = Windows.UI.Color.FromArgb(0x30, 0xE5, 0xB2, 0x3C);

    private static readonly Windows.UI.Color OccurrenceMarkColor = Windows.UI.Color.FromArgb(0x80, 0x8A, 0x9B, 0xB4);

    /// <summary>
    /// The selection or the text changed: refind the other copies a moment later, so
    /// dragging a selection or typing never rescans the buffer per event.
    /// </summary>
    private void QueueOccurrences()
    {
        if (_occurrenceTimer is null)
        {
            _occurrenceTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _occurrenceTimer.IsRepeating = false;
            _occurrenceTimer.Interval = TimeSpan.FromMilliseconds(150);
            _occurrenceTimer.Tick += (_, _) => RefreshOccurrences();
        }

        // A stale set would sit at the wrong place while the debounce waits.
        if (_occurrences.Count > 0 && !HasSelection)
        {
            ClearOccurrences();
        }

        _occurrenceTimer.Stop();
        _occurrenceTimer.Start();
    }

    private void ClearOccurrences()
    {
        _occurrences = [];
        _marksDirty = true;
        UpdateExtent();
        Canvas.Invalidate();
    }

    private void RefreshOccurrences()
    {
        var found = new List<(int Line, int Col, int Length)>();
        if (HasSelection)
        {
            var (start, end) = OrderedSelection();
            if (start.Line == end.Line)
            {
                var line = _doc.GetLine(start.Line);
                var length = Math.Min(end.Col, line.Length) - start.Col;
                if (length is >= MinOccurrenceLength and <= MaxOccurrenceLength && start.Col >= 0 &&
                    start.Col + length <= line.Length)
                {
                    var needle = line.Substring(start.Col, length);
                    if (!string.IsNullOrWhiteSpace(needle))
                    {
                        for (var i = 0; i < _doc.LineCount && found.Count < MaxOccurrences; i++)
                        {
                            var text = _doc.GetLine(i);
                            for (var at = text.IndexOf(needle, StringComparison.Ordinal);
                                 at >= 0 && found.Count < MaxOccurrences;
                                 at = text.IndexOf(needle, at + length, StringComparison.Ordinal))
                            {
                                // The selection itself already has its own colour.
                                if (i != start.Line || at != start.Col)
                                {
                                    found.Add((i, at, length));
                                }
                            }
                        }
                    }
                }
            }
        }

        if (found.Count == 0 && _occurrences.Count == 0)
        {
            return;
        }

        _occurrences = found;
        _marksDirty = true;
        UpdateExtent();
        Canvas.Invalidate();
    }

    private void DrawOccurrences(CanvasDrawingSession session, int firstLine, int lastLine)
    {
        foreach (var (line, col, length) in _occurrences)
        {
            if (line < firstLine)
            {
                continue;
            }

            if (line > lastLine)
            {
                break;
            }

            var raw = _doc.GetLine(line);
            if (col + length > raw.Length)
            {
                continue;
            }

            var fromCol = TextDocument.ExpandedColFromRaw(raw, col);
            var toCol = TextDocument.ExpandedColFromRaw(raw, col + length);
            var offset = _rowOffsets[line];
            var firstR = fromCol / _wrapCols;
            var lastR = toCol == 0 ? 0 : (toCol - 1) / _wrapCols;
            for (var r = firstR; r <= lastR; r++)
            {
                var rowStart = r * _wrapCols;
                var s = Math.Max(fromCol, rowStart);
                var e = Math.Min(toCol, rowStart + _wrapCols);
                var x1 = TextX + (s - rowStart) * _charWidth - _horizontalOffset;
                var x2 = TextX + (e - rowStart) * _charWidth - _horizontalOffset;
                var y = (offset + r) * _lineHeight - _verticalOffset + LineHighlightOffset;
                session.FillRectangle((float)x1, (float)y, (float)Math.Max(2, x2 - x1), (float)_lineHeight, Brush(session, _occurrenceColor));
            }
        }
    }

    /// <summary>One stripe per line holding a copy; the track cannot show finer than that.</summary>
    private void AddOccurrenceMarks(List<ScrollMark> marks, Func<int, double> rowOf, int lines)
    {
        var previous = -1;
        foreach (var (line, col, _) in _occurrences)
        {
            if (line == previous || line >= lines)
            {
                continue;
            }

            previous = line;
            marks.Add(new ScrollMark(rowOf(line), rowOf(line + 1), OccurrenceMarkColor, 1, line, col));
        }
    }
}
