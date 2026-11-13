using Codale.Core.Syntax;
using Codale.Editor;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

using Windows.UI.Text;

namespace Codale.App.Controls;

// Metrics, line wrapping and the mapping between document columns and pixels.
//
// Columns map to pixels two ways. A "simple" line (printable ASCII only) is one monospace
// advance per character, so x = column × advance, with no text layout needed. Every other line
// (tabs, emoji, CJK, combining marks) asks its DirectWrite layout, the same object that draws
// the glyphs, so carets, selections and highlights land exactly on what is drawn.
public sealed partial class CodeEditor
{
    private const string EditorFontFamily = "Consolas";
    private const string IconFontFamily = "Segoe Fluent Icons";
    private const float CaretWidth = 2;

    /// <summary>Space between the line numbers and the text.</summary>
    private const double TextLeftPad = 10;

    /// <summary>Left strip of the gutter where bookmarks show and a click toggles one.</summary>
    private const double BookmarkMargin = 14;

    /// <summary>Strip right of the line numbers where the diff bars sit.</summary>
    private const double DiffZone = 6;

    /// <summary>A simple line longer than this is laid out in chunks, and only the visible chunks are shaped.</summary>
    private const int ChunkChars = 1024;

    private const int NoWrap = int.MaxValue;

    private CanvasTextFormat? _textFormat;
    private CanvasTextFormat? _numberFormat;
    private double _lineHeight = 18;
    private double _charWidth = 8;
    private float _fontPx = DefaultFontSize;

    // Wrapping: with wrap on, every line knows its row count and the prefix sums map rows to lines.
    private int _wrapCols = -1;
    private List<int>? _rowCounts;
    private readonly List<int> _rowPrefix = [];
    private int _prefixValidUpTo;

    // The widest line in cells, for the horizontal extent; recomputed lazily after shrinking edits.
    private int _maxLineCells;
    private bool _maxLineCellsStale = true;
    private double _maxMeasuredWidth;

    /// <summary>Per-line render cache, aligned with the buffer's lines; null where nothing is cached.</summary>
    private readonly List<LineVisual?> _visuals = [];
    private int _liveVisuals;

    private sealed class LineVisual
    {
        public string Source = "";
        public CodeToken[]? Tokens;
        public bool Simple;
        public int[] RowStarts = [0];
        public int ChunkSize;
        public CanvasTextLayout?[] Segments = [];

        public int SegmentCount(int wrapCols) => wrapCols != NoWrap
            ? RowStarts.Length
            : ChunkSize > 0 ? Math.Max(1, (Source.Length + ChunkSize - 1) / ChunkSize) : 1;

        public (int Start, int Length) SegmentRange(int index, int wrapCols)
        {
            if (wrapCols != NoWrap)
            {
                var start = RowStarts[index];
                var end = index + 1 < RowStarts.Length ? RowStarts[index + 1] : Source.Length;
                return (start, end - start);
            }

            if (ChunkSize > 0)
            {
                var start = index * ChunkSize;
                return (start, Math.Min(ChunkSize, Source.Length - start));
            }

            return (0, Source.Length);
        }

        public void DisposeSegments()
        {
            for (var i = 0; i < Segments.Length; i++)
            {
                Segments[i]?.Dispose();
                Segments[i] = null;
            }
        }
    }

    // ------------------------------------------------------------------ metrics

    private double GutterWidth
    {
        get
        {
            if (!ShowLineNumbers)
            {
                return BookmarkMargin + DiffZone;
            }

            var digits = Math.Max(3, DigitCount(_model.Buffer.LineCount));
            return BookmarkMargin + digits * _charWidth + DiffZone + 4;
        }
    }

    private static int DigitCount(int value) => value < 10 ? 1 : (int)Math.Floor(Math.Log10(value)) + 1;

    /// <summary>X of the text origin when not scrolled sideways.</summary>
    private double TextLeft => GutterWidth + TextLeftPad;

    private double ViewportWidth => RootGrid.ActualWidth;

    private double ViewportHeight => RootGrid.ActualHeight;

    private CanvasDevice Device => CanvasDevice.GetSharedDevice();

    private void ResetMetrics()
    {
        _textFormat?.Dispose();
        _numberFormat?.Dispose();
        _textFormat = null;
        _numberFormat = null;
        _wrapCols = -1;
        DropAllVisuals();
        _marksDirty = true;
    }

    private void EnsureMetrics()
    {
        if (_textFormat is null)
        {
            _fontPx = (float)(double.IsFinite(FontSize) && FontSize > 0 ? FontSize : DefaultFontSize);
            _lineHeight = Math.Round(_fontPx * 1.4);
            var baseline = (float)Math.Round(_lineHeight * 0.78);

            using (var probe = new CanvasTextFormat { FontFamily = EditorFontFamily, FontSize = _fontPx, WordWrapping = CanvasWordWrapping.NoWrap })
            using (var measure = new CanvasTextLayout(Device, new string('M', 100), probe, 100000f, 100f))
            {
                // LayoutBounds is advance-based; DrawBounds is ink only and would drift on long lines.
                _charWidth = measure.LayoutBounds.Width / 100;
            }

            if (!(_charWidth > 0))
            {
                _charWidth = _fontPx * 0.55;
            }

            _textFormat = new CanvasTextFormat
            {
                FontFamily = EditorFontFamily,
                FontSize = _fontPx,
                FontWeight = new FontWeight { Weight = 400 },
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                LineSpacingMode = CanvasLineSpacingMode.Uniform,
                LineSpacing = (float)_lineHeight,
                LineSpacingBaseline = baseline,
                IncrementalTabStop = (float)(_charWidth * _model.TabWidth),

                // Emoji draw in colour (Segoe UI Emoji's COLR glyphs) instead of monochrome outlines.
                Options = CanvasDrawTextOptions.EnableColorFont,
            };
            _numberFormat = new CanvasTextFormat
            {
                FontFamily = EditorFontFamily,
                FontSize = _fontPx,
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Right,
                LineSpacingMode = CanvasLineSpacingMode.Uniform,
                LineSpacing = (float)_lineHeight,
                LineSpacingBaseline = baseline,
            };
            _maxMeasuredWidth = 0;
        }

        EnsureWrap();
    }

    // ------------------------------------------------------------------ wrapping and rows

    private int DesiredWrapCols()
    {
        if (!WordWrap || ViewportWidth <= 0)
        {
            return NoWrap;
        }

        return Math.Max(8, (int)((ViewportWidth - TextLeft - VBarRestWidth - 12) / Math.Max(1, _charWidth)));
    }

    private void EnsureWrap()
    {
        var cols = DesiredWrapCols();
        if (cols == _wrapCols)
        {
            return;
        }

        _wrapCols = cols;
        DropAllVisuals();
        _marksDirty = true;
        if (cols == NoWrap)
        {
            _rowCounts = null;
            return;
        }

        var lines = _model.Buffer.Lines;
        _rowCounts = new List<int>(lines.Count);
        foreach (var line in lines)
        {
            _rowCounts.Add(TextUnits.Wrap(line, cols, _model.TabWidth, null));
        }

        _prefixValidUpTo = 0;
    }

    private bool Wrapped => _wrapCols != NoWrap && _rowCounts is not null;

    private void EnsurePrefix()
    {
        var counts = _rowCounts!;
        if (_rowPrefix.Count != counts.Count + 1)
        {
            _prefixValidUpTo = Math.Min(_prefixValidUpTo, Math.Min(_rowPrefix.Count, counts.Count));
            if (_rowPrefix.Count > counts.Count + 1)
            {
                _rowPrefix.RemoveRange(counts.Count + 1, _rowPrefix.Count - counts.Count - 1);
            }

            while (_rowPrefix.Count < counts.Count + 1)
            {
                _rowPrefix.Add(0);
            }
        }

        if (_prefixValidUpTo >= counts.Count)
        {
            return;
        }

        var total = _prefixValidUpTo == 0 ? 0 : _rowPrefix[_prefixValidUpTo];
        _rowPrefix[0] = 0;
        for (var i = _prefixValidUpTo; i < counts.Count; i++)
        {
            total += counts[i];
            _rowPrefix[i + 1] = total;
        }

        _prefixValidUpTo = counts.Count;
    }

    private int TotalRows
    {
        get
        {
            if (!Wrapped)
            {
                return _model.Buffer.LineCount;
            }

            EnsurePrefix();
            return Math.Max(1, _rowPrefix[^1]);
        }
    }

    private int FirstRowOf(int line)
    {
        if (!Wrapped)
        {
            return line;
        }

        EnsurePrefix();
        return _rowPrefix[Math.Clamp(line, 0, _rowPrefix.Count - 1)];
    }

    private int RowCountOf(int line) => Wrapped ? _rowCounts![line] : 1;

    /// <summary>The line a visual row belongs to, and which of its rows it is.</summary>
    private (int Line, int RowInLine) LineOfRow(int row)
    {
        var lines = _model.Buffer.LineCount;
        if (!Wrapped)
        {
            return (Math.Clamp(row, 0, lines - 1), 0);
        }

        EnsurePrefix();
        row = Math.Clamp(row, 0, Math.Max(0, _rowPrefix[^1] - 1));
        var lo = 0;
        var hi = lines - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_rowPrefix[mid] <= row)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (lo, row - _rowPrefix[lo]);
    }

    /// <summary>Which wrapped row of its line a column is drawn on.</summary>
    private int RowInLineOf(int line, int col)
    {
        if (!Wrapped)
        {
            return 0;
        }

        var starts = GetVisual(line).RowStarts;
        var row = 0;
        while (row + 1 < starts.Length && starts[row + 1] <= col)
        {
            row++;
        }

        return row;
    }

    // ------------------------------------------------------------------ buffer changes

    private void OnBufferChanged(TextChange change)
    {
        // Splice the per-line caches so lines below an edit keep what they had built.
        var start = change.StartLine;
        var removed = change.OldLineCount;
        var added = change.NewLineCount;
        SpliceVisuals(start, removed, added);

        if (_rowCounts is not null && _wrapCols != NoWrap)
        {
            _rowCounts.RemoveRange(start, Math.Min(removed, _rowCounts.Count - start));
            var lines = _model.Buffer.Lines;
            var fresh = new int[added];
            for (var i = 0; i < added; i++)
            {
                fresh[i] = TextUnits.Wrap(lines[start + i], _wrapCols, _model.TabWidth, null);
            }

            _rowCounts.InsertRange(start, fresh);
            _prefixValidUpTo = Math.Min(_prefixValidUpTo, start);
        }

        // The widest line can only grow from the new text; a shrink is found lazily.
        if (!_maxLineCellsStale)
        {
            for (var i = 0; i < added; i++)
            {
                _maxLineCells = Math.Max(_maxLineCells, TextUnits.CellCount(_model.Buffer[start + i], _model.TabWidth));
            }

            if (removed > 1 || change.OldEnd.Col > change.NewEnd.Col)
            {
                _maxLineCellsStale = true;
            }
        }

        if (DigitCount(_model.Buffer.LineCount) != DigitCount(_model.Buffer.LineCount - change.LineDelta))
        {
            // The gutter grew or shrank by a digit: wrap widths follow the text area.
            _wrapCols = -1;
        }

        _marksDirty = true;
        QueueTokenizing();
        Invalidate();
    }

    private void OnBufferReloaded()
    {
        DropAllVisuals();
        _visuals.Clear();
        _wrapCols = -1;
        _maxLineCellsStale = true;
        _maxMeasuredWidth = 0;
        _diffMarkers = [];
        _occurrences = [];
        _marksDirty = true;
        QueueTokenizing();
        Invalidate();
    }

    private void OnTokensChanged(int first, int last)
    {
        // Colours of lines on screen changed (the verified pass caught up): redraw.
        if (last >= _firstVisibleLine && first <= _lastVisibleLine)
        {
            Invalidate();
        }
    }

    private void SpliceVisuals(int start, int removed, int added)
    {
        if (_visuals.Count == 0)
        {
            return;
        }

        var end = Math.Min(start + removed, _visuals.Count);
        for (var i = start; i < end; i++)
        {
            if (_visuals[i] is { } visual)
            {
                visual.DisposeSegments();
                _liveVisuals--;
            }
        }

        if (start < _visuals.Count)
        {
            _visuals.RemoveRange(start, end - start);
            _visuals.InsertRange(start, new LineVisual?[added]);
        }
    }

    private void DropAllVisuals()
    {
        foreach (var visual in _visuals)
        {
            visual?.DisposeSegments();
        }

        _visuals.Clear();
        _liveVisuals = 0;
        DropNumberLayouts();
    }

    /// <summary>Recomputes the widest line when an edit may have shortened it (called from idle time).</summary>
    private void RefreshMaxLineCells()
    {
        if (!_maxLineCellsStale)
        {
            return;
        }

        var max = 0;
        foreach (var line in _model.Buffer.Lines)
        {
            if (line.Length > max)
            {
                max = Math.Max(max, TextUnits.CellCount(line, _model.TabWidth));
            }
        }

        _maxLineCells = max;
        _maxLineCellsStale = false;
    }

    // ------------------------------------------------------------------ line visuals

    private LineVisual GetVisual(int line)
    {
        while (_visuals.Count < _model.Buffer.LineCount)
        {
            _visuals.Add(null);
        }

        var source = _model.Buffer[line];
        var tokens = _highlighter.Get(line).Tokens;
        var visual = _visuals[line];
        if (visual is not null && ReferenceEquals(visual.Source, source))
        {
            if (!ReferenceEquals(visual.Tokens, tokens))
            {
                visual.DisposeSegments();
                visual.Tokens = tokens;
            }

            return visual;
        }

        if (visual is null)
        {
            visual = new LineVisual();
            _visuals[line] = visual;
            _liveVisuals++;
        }
        else
        {
            visual.DisposeSegments();
        }

        visual.Source = source;
        visual.Tokens = tokens;
        visual.Simple = TextUnits.IsSimple(source);
        if (_wrapCols != NoWrap)
        {
            var starts = new List<int>();
            TextUnits.Wrap(source, _wrapCols, _model.TabWidth, starts);
            visual.RowStarts = [.. starts];
            visual.ChunkSize = 0;
        }
        else
        {
            visual.RowStarts = [0];
            visual.ChunkSize = visual.Simple && source.Length > ChunkChars * 2 ? ChunkChars : 0;
        }

        var count = visual.SegmentCount(_wrapCols);
        if (visual.Segments.Length != count)
        {
            visual.Segments = new CanvasTextLayout?[count];
        }

        return visual;
    }

    /// <summary>The laid-out text of one segment (a wrapped row or a chunk of a long line); null when empty.</summary>
    private CanvasTextLayout? GetSegment(LineVisual visual, int index)
    {
        if (index < 0 || index >= visual.Segments.Length)
        {
            return null;
        }

        if (visual.Segments[index] is { } cached)
        {
            return cached;
        }

        var (start, length) = visual.SegmentRange(index, _wrapCols);
        if (length <= 0)
        {
            return null;
        }

        var text = start == 0 && length == visual.Source.Length ? visual.Source : visual.Source.Substring(start, length);
        var layout = new CanvasTextLayout(Device, text, _textFormat!, 0f, (float)_lineHeight);
        layout.Options = Microsoft.Graphics.Canvas.Text.CanvasDrawTextOptions.EnableColorFont;

        if (visual.Tokens is { Length: > 0 } tokens)
        {
            foreach (var token in tokens)
            {
                var from = Math.Max(token.Start, start);
                var to = Math.Min(token.Start + token.Length, start + length);
                if (to > from)
                {
                    layout.SetColor(from - start, to - from, CodePalette.Get(token.Kind, _light));
                }
            }
        }

        // Consolas has no glyphs for private-use icons; let them draw from the icon font.
        if (!visual.Simple)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] is >= '' and <= '')
                {
                    var runStart = i;
                    while (i + 1 < text.Length && text[i + 1] is >= '' and <= '')
                    {
                        i++;
                    }

                    layout.SetFontFamily(runStart, i - runStart + 1, IconFontFamily);
                }
            }

            if (_wrapCols == NoWrap)
            {
                _maxMeasuredWidth = Math.Max(_maxMeasuredWidth, layout.LayoutBounds.Width);
            }
        }

        visual.Segments[index] = layout;
        return layout;
    }

    /// <summary>Releases cached layouts of lines far from the view once there are many.</summary>
    private void TrimVisuals(int firstLine, int lastLine)
    {
        var span = Math.Max(1, lastLine - firstLine + 1);
        if (_liveVisuals <= Math.Max(512, span * 8))
        {
            return;
        }

        var keepFrom = firstLine - span * 2;
        var keepTo = lastLine + span * 2;
        for (var i = 0; i < _visuals.Count; i++)
        {
            if ((i < keepFrom || i > keepTo) && _visuals[i] is { } visual)
            {
                visual.DisposeSegments();
                _visuals[i] = null;
                _liveVisuals--;
            }
        }
    }

    // ------------------------------------------------------------------ columns ↔ pixels

    /// <summary>The visual row and the x (from the text origin, unscrolled) a position is drawn at.</summary>
    private (int Row, double X) Locate(DocumentPosition position)
    {
        var line = position.Line;
        var rowInLine = RowInLineOf(line, position.Col);
        return (FirstRowOf(line) + rowInLine, ColumnX(line, rowInLine, position.Col));
    }

    /// <summary>X (from the text origin) of a column on one of the line's rows.</summary>
    private double ColumnX(int line, int rowInLine, int col)
    {
        var source = _model.Buffer[line];
        if (!Wrapped && TextUnits.IsSimple(source))
        {
            return Math.Min(col, source.Length) * _charWidth;
        }

        var visual = GetVisual(line);
        var (start, length) = visual.SegmentRange(Math.Min(rowInLine, visual.RowStarts.Length - 1), _wrapCols);
        var index = Math.Clamp(col - start, 0, length);
        if (visual.Simple)
        {
            return index * _charWidth;
        }

        var layout = GetSegment(visual, rowInLine);
        return layout is null ? 0 : CaretX(layout, index, length);
    }

    private static double CaretX(CanvasTextLayout layout, int index, int length)
    {
        if (length == 0)
        {
            return 0;
        }

        return index >= length
            ? layout.GetCaretPosition(length - 1, true).X
            : layout.GetCaretPosition(index, false).X;
    }

    /// <summary>The column nearest an x (from the text origin) on a given row of a line.</summary>
    private int ColumnAt(int line, int rowInLine, double x)
    {
        var source = _model.Buffer[line];
        if (!Wrapped && TextUnits.IsSimple(source))
        {
            return Math.Clamp((int)Math.Round(x / _charWidth), 0, source.Length);
        }

        var visual = GetVisual(line);
        rowInLine = Math.Clamp(rowInLine, 0, visual.RowStarts.Length - 1);
        var (start, length) = visual.SegmentRange(rowInLine, _wrapCols);
        if (visual.Simple)
        {
            return start + Math.Clamp((int)Math.Round(x / _charWidth), 0, length);
        }

        var layout = GetSegment(visual, rowInLine);
        if (layout is null)
        {
            return start;
        }

        layout.HitTest((float)x, (float)(_lineHeight / 2), out var region, out var trailing);
        var index = Math.Clamp(region.CharacterIndex + (trailing ? region.CharacterCount : 0), 0, length);
        return TextUnits.Snap(source, start + index);
    }

    /// <summary>The document position under a point in control coordinates.</summary>
    private DocumentPosition PositionAt(Windows.Foundation.Point point)
    {
        EnsureMetrics();
        var row = Math.Clamp((int)Math.Floor((point.Y + _scrollY) / _lineHeight), 0, TotalRows - 1);
        var (line, rowInLine) = LineOfRow(row);
        return new DocumentPosition(line, ColumnAt(line, rowInLine, point.X - TextLeft + _scrollX));
    }
}
