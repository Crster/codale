using System.Diagnostics;

using Codale.Editor;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Controls;

// The frame. Rendering is demand-driven: a change marks the view dirty and hooks the composition
// clock; the next tick draws one complete frame into the swap chain and presents it, then the
// hook comes off again. An idle editor draws nothing at all (the caret blink is two frames a
// second). Because every layer is drawn in the same pass, text, backgrounds, carets and bars
// can never be a frame apart while scrolling.
public sealed partial class CodeEditor
{
    private CanvasSwapChain? _swapChain;
    private bool _dirty = true;
    private bool _renderHooked;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private double _lastTickMs;
    private int _firstVisibleLine;
    private int _lastVisibleLine;

    /// <summary>Solid brushes reused across frames, keyed by colour.</summary>
    private readonly Dictionary<Windows.UI.Color, CanvasSolidColorBrush> _brushes = [];

    /// <summary>Line-number layouts by number; the text depends on nothing else.</summary>
    private readonly Dictionary<int, CanvasTextLayout> _numberLayouts = [];

    /// <summary>The last frame's cost in milliseconds (exposed for profiling via <see cref="RenderStats"/>).</summary>
    private double _lastFrameMs;
    private long _framesDrawn;

    /// <summary>Frames drawn and the cost of the last one; for diagnostics and profiling.</summary>
    public (long Frames, double LastFrameMs) RenderStats => (_framesDrawn, _lastFrameMs);

    private void Invalidate()
    {
        if (_closed)
        {
            return;
        }

        _dirty = true;
        StartRenderLoop();
    }

    private void StartRenderLoop()
    {
        if (!_renderHooked && !_closed)
        {
            _renderHooked = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void StopRenderLoop()
    {
        if (_renderHooked)
        {
            _renderHooked = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (_closed)
        {
            StopRenderLoop();
            return;
        }

        var now = _frameClock.Elapsed.TotalMilliseconds;
        var dt = Math.Clamp(now - _lastTickMs, 0, 100);
        _lastTickMs = now;

        var animating = StepAnimations(dt);
        if (_dirty || animating)
        {
            _dirty = false;
            Render();
        }
        else
        {
            StopRenderLoop();
        }
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (WordWrap)
        {
            EnsureMetrics();
        }

        ClampScroll();
        _marksDirty = true;
        Invalidate();
    }

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        _marksDirty = true;
        Invalidate();
    }

    private void DisposeGraphics()
    {
        foreach (var brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _brushes.Clear();
        DropNumberLayouts();
        _textFormat?.Dispose();
        _numberFormat?.Dispose();
        _textFormat = null;
        _numberFormat = null;
        _swapChain?.Dispose();
        _swapChain = null;
        if (!_closed)
        {
            Surface.SwapChain = null;
        }
    }

    private void DropNumberLayouts()
    {
        foreach (var layout in _numberLayouts.Values)
        {
            layout.Dispose();
        }

        _numberLayouts.Clear();
    }

    private CanvasSolidColorBrush Brush(Windows.UI.Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new CanvasSolidColorBrush(Device, color);
            _brushes[color] = brush;
        }

        return brush;
    }

    /// <summary>Makes sure the swap chain matches the panel's size and scale; false when there is nothing to draw on.</summary>
    private bool EnsureSwapChain()
    {
        var width = (float)ViewportWidth;
        var height = (float)ViewportHeight;
        if (width < 1 || height < 1 || Visibility != Visibility.Visible)
        {
            return false;
        }

        var dpi = 96f * Math.Max(Surface.CompositionScaleX, 0.25f);
        if (_swapChain is null || _swapChain.Device != Device)
        {
            _swapChain?.Dispose();
            _swapChain = new CanvasSwapChain(Device, width, height, dpi, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, CanvasAlphaMode.Premultiplied);
            Surface.SwapChain = _swapChain;
        }
        else if (Math.Abs(_swapChain.Size.Width - width) > 0.01 || Math.Abs(_swapChain.Size.Height - height) > 0.01 || Math.Abs(_swapChain.Dpi - dpi) > 0.01)
        {
            _swapChain.ResizeBuffers(width, height, dpi);
        }

        return true;
    }

    private void Render()
    {
        try
        {
            if (!EnsureSwapChain())
            {
                return;
            }

            var started = _frameClock.Elapsed.TotalMilliseconds;
            using (var session = _swapChain!.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
            {
                session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                DrawFrame(session);
            }

            _swapChain.Present(0);
            _lastFrameMs = _frameClock.Elapsed.TotalMilliseconds - started;
            _framesDrawn++;
        }
        catch (Exception ex) when (Device.IsDeviceLost(ex.HResult))
        {
            // The GPU went away (driver update, sleep): rebuild everything against a fresh device.
            DropAllVisuals();
            DisposeGraphics();
            Invalidate();
        }
    }

    private void DrawFrame(CanvasDrawingSession ds)
    {
        EnsureMetrics();
        RefreshMaxLineCells();
        UpdateExtent();

        var width = ViewportWidth;
        var height = ViewportHeight;
        var totalRows = TotalRows;
        var firstRow = Math.Clamp((int)(_scrollY / _lineHeight), 0, totalRows - 1);
        var lastRow = Math.Clamp((int)((_scrollY + height) / _lineHeight), 0, totalRows - 1);
        var (firstLine, _) = LineOfRow(firstRow);
        var (lastLine, _) = LineOfRow(lastRow);
        _firstVisibleLine = firstLine;
        _lastVisibleLine = lastLine;

        var gutter = GutterWidth;
        var textLeft = TextLeft;
        var focused = FocusState != FocusState.Unfocused;
        var originX = (float)(textLeft - _scrollX);

        // Line bands under everything: the caret lines and bookmarked lines, full width.
        DrawLineBands(ds, firstLine, lastLine, width, focused);

        // The text area is clipped so sideways scrolling never paints over the gutter.
        using (ds.CreateLayer(1f, new Windows.Foundation.Rect(gutter, 0, Math.Max(0, width - gutter), height)))
        {
            DrawPinnedWords(ds, firstLine, lastLine);
            DrawResultHighlights(ds, firstLine, lastLine);
            DrawOccurrences(ds, firstLine, lastLine);
            DrawSelections(ds, firstLine, lastLine, focused);
            DrawText(ds, firstRow, lastRow, originX);
            if (focused)
            {
                DrawCarets(ds, firstLine, lastLine);
            }
        }

        DrawGutter(ds, firstRow, lastRow, gutter);
        DrawDiffMarkers(ds, height);
        DrawDiffPeek(ds, width, height);
        DrawScrollBars(ds, width, height);

        TrimVisuals(firstLine, lastLine);
        QueueTokenizing();
    }

    // ------------------------------------------------------------------ layers

    private float RowY(int row) => (float)(row * _lineHeight - _scrollY);

    private void DrawLineBands(CanvasDrawingSession ds, int firstLine, int lastLine, double width, bool focused)
    {
        foreach (var line in _bookmarks.Lines)
        {
            if (line < firstLine || line > lastLine)
            {
                continue;
            }

            var top = RowY(FirstRowOf(line));
            ds.FillRectangle(0, top, (float)width, (float)(RowCountOf(line) * _lineHeight), Brush(_bookmarkLineColor));
        }

        if (!focused)
        {
            return;
        }

        var previous = -1;
        foreach (var selection in _model.Selections)
        {
            var line = selection.Caret.Line;
            if (line == previous || line < firstLine || line > lastLine || selection.Start.Line != selection.End.Line && !selection.IsEmpty)
            {
                continue;
            }

            previous = line;
            var row = FirstRowOf(line) + RowInLineOf(line, selection.Caret.Col);
            ds.FillRectangle(0, RowY(row), (float)width, (float)_lineHeight, Brush(_activeLineColor));
        }
    }

    /// <summary>Fills the span [fromCol, toCol) of a line, one rectangle per wrapped row it crosses.</summary>
    private void FillSpan(CanvasDrawingSession ds, int line, int fromCol, int toCol, Windows.UI.Color color, bool pastEnd = false, float inset = 0)
    {
        var source = _model.Buffer[line];
        fromCol = Math.Clamp(fromCol, 0, source.Length);
        toCol = Math.Clamp(toCol, 0, source.Length);
        var firstRowInLine = RowInLineOf(line, fromCol);
        var lastRowInLine = RowInLineOf(line, toCol);
        var lineRow = FirstRowOf(line);
        var brush = Brush(color);
        var originX = TextLeft - _scrollX;
        for (var r = firstRowInLine; r <= lastRowInLine; r++)
        {
            var x1 = r == firstRowInLine ? ColumnX(line, r, fromCol) : 0;
            double x2;
            if (r == lastRowInLine)
            {
                x2 = ColumnX(line, r, toCol);
                if (pastEnd)
                {
                    x2 += _charWidth * 0.6;
                }
            }
            else
            {
                x2 = ColumnX(line, r, int.MaxValue) + _charWidth * 0.6;
            }

            if (x2 <= x1 && !pastEnd)
            {
                continue;
            }

            ds.FillRectangle((float)(originX + x1), RowY(lineRow + r) + inset, (float)Math.Max(1, x2 - x1), (float)_lineHeight - inset * 2, brush);
        }
    }

    private void DrawSelections(CanvasDrawingSession ds, int firstLine, int lastLine, bool focused)
    {
        var color = focused ? _selectionColor : _inactiveSelectionColor;
        foreach (var selection in _model.Selections)
        {
            if (selection.IsEmpty || selection.End.Line < firstLine || selection.Start.Line > lastLine)
            {
                continue;
            }

            var start = selection.Start;
            var end = selection.End;
            for (var line = Math.Max(start.Line, firstLine); line <= Math.Min(end.Line, lastLine); line++)
            {
                var from = line == start.Line ? start.Col : 0;
                var to = line == end.Line ? end.Col : int.MaxValue;
                FillSpan(ds, line, from, to, color, pastEnd: line != end.Line);
            }
        }
    }

    private void DrawText(CanvasDrawingSession ds, int firstRow, int lastRow, float originX)
    {
        var brush = Brush(_baseText);
        var row = firstRow;
        var viewLeft = _scrollX;
        var viewRight = _scrollX + ViewportWidth;
        while (row <= lastRow)
        {
            var (line, rowInLine) = LineOfRow(row);
            var source = _model.Buffer[line];
            if (source.Length == 0)
            {
                row += RowCountOf(line) - rowInLine;
                continue;
            }

            var visual = GetVisual(line);
            if (_wrapCols != NoWrap)
            {
                var rows = visual.RowStarts.Length;
                for (var r = rowInLine; r < rows && row <= lastRow; r++, row++)
                {
                    if (GetSegment(visual, r) is { } layout)
                    {
                        ds.DrawTextLayout(layout, originX, RowY(row), brush);
                    }
                }

                continue;
            }

            if (visual.ChunkSize > 0)
            {
                // A very long line: shape only the chunks that are on screen.
                var chunkWidth = visual.ChunkSize * _charWidth;
                var firstChunk = Math.Max(0, (int)(viewLeft / chunkWidth) - 1);
                var lastChunk = Math.Min(visual.Segments.Length - 1, (int)(viewRight / chunkWidth) + 1);
                for (var c = firstChunk; c <= lastChunk; c++)
                {
                    if (GetSegment(visual, c) is { } chunk)
                    {
                        ds.DrawTextLayout(chunk, (float)(originX + c * chunkWidth), RowY(row), brush);
                    }
                }
            }
            else if (GetSegment(visual, 0) is { } layout)
            {
                ds.DrawTextLayout(layout, originX, RowY(row), brush);
            }

            row++;
        }

        DrawGhost(ds);
    }

    private void DrawCarets(CanvasDrawingSession ds, int firstLine, int lastLine)
    {
        if (!_caretVisible)
        {
            return;
        }

        var brush = Brush(_caretColor);
        var originX = TextLeft - _scrollX;
        foreach (var selection in _model.Selections)
        {
            var caret = selection.Caret;
            if (caret.Line < firstLine || caret.Line > lastLine)
            {
                continue;
            }

            var (row, x) = Locate(caret);
            ds.FillRectangle((float)(originX + x - CaretWidth / 2), RowY(row), CaretWidth, (float)_lineHeight, brush);
        }
    }

    private void DrawGutter(CanvasDrawingSession ds, int firstRow, int lastRow, double gutter)
    {
        var primaryLine = _model.Primary.Caret.Line;
        var caretLines = _model.HasMultipleSelections ? _model.Selections.Select(s => s.Caret.Line).ToHashSet() : null;
        var numberRight = (float)(gutter - DiffZone - 4);
        var row = firstRow;
        while (row <= lastRow)
        {
            var (line, rowInLine) = LineOfRow(row);
            var y = RowY(row);
            if (rowInLine == 0)
            {
                if (ShowLineNumbers)
                {
                    if (!_numberLayouts.TryGetValue(line, out var number))
                    {
                        number = new CanvasTextLayout(Device, (line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), _numberFormat!, 0, 0);
                        _numberLayouts[line] = number;
                    }

                    var active = line == primaryLine || caretLines?.Contains(line) == true;
                    ds.DrawTextLayout(number, numberRight, y, Brush(active ? _activeLineNumberText : _lineNumberText));
                }

                if (_bookmarks.Contains(line))
                {
                    var size = (float)Math.Min(8, _lineHeight * 0.45);
                    var cx = (float)(BookmarkMargin / 2);
                    var cy = y + (float)_lineHeight / 2;
                    ds.FillRoundedRectangle(cx - size / 2, cy - size / 2, size, size, 2, 2, Brush(_bookmarkColor));
                }
            }

            row += RowCountOf(line) - rowInLine;
        }

        if (_numberLayouts.Count > 1024)
        {
            var (first, _) = LineOfRow(firstRow);
            foreach (var key in _numberLayouts.Keys.Where(k => k < first - 256 || k > first + 1024).ToList())
            {
                _numberLayouts[key].Dispose();
                _numberLayouts.Remove(key);
            }
        }
    }

    // ------------------------------------------------------------------ background colouring

    private bool _tokenizeQueued;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tokenizeTimer;

    /// <summary>
    /// Runs the syntax highlighter's verified pass in short slices up to a little past the view.
    /// Slices are spaced out (about a quarter of one core at most), so verifying a whole large
    /// file after a jump to its end never pins the CPU; the view meanwhile shows provisional colour.
    /// </summary>
    private void QueueTokenizing()
    {
        if (_tokenizeQueued || _closed)
        {
            return;
        }

        _tokenizeQueued = true;
        if (_tokenizeTimer is null)
        {
            _tokenizeTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _tokenizeTimer.IsRepeating = false;
            _tokenizeTimer.Interval = TimeSpan.FromMilliseconds(15);
            _tokenizeTimer.Tick += (_, _) => RunTokenizeSlice();
        }

        _tokenizeTimer.Start();
    }

    private void RunTokenizeSlice()
    {
        _tokenizeQueued = false;
        if (_closed)
        {
            return;
        }

        var target = _lastVisibleLine + Math.Max(50, _lastVisibleLine - _firstVisibleLine);
        var more = _highlighter.Advance(target, TimeSpan.FromMilliseconds(5));
        more |= PrefetchVisuals(TimeSpan.FromMilliseconds(3));
        if (more)
        {
            QueueTokenizing();
        }
    }

    /// <summary>
    /// Shapes the lines just outside the view (a viewport each way) in idle time, so the next
    /// scroll finds them ready instead of shaping them inside the frame. True while unfinished.
    /// </summary>
    private bool PrefetchVisuals(TimeSpan budget)
    {
        if (_textFormat is null || _wrapCols == -1)
        {
            return false;
        }

        var lines = _model.Buffer.LineCount;
        var span = Math.Max(1, _lastVisibleLine - _firstVisibleLine + 1);
        var clock = Stopwatch.StartNew();
        for (var distance = 1; distance <= span; distance++)
        {
            foreach (var line in (ReadOnlySpan<int>)[_lastVisibleLine + distance, _firstVisibleLine - distance])
            {
                if (line < 0 || line >= lines || _model.Buffer[line].Length == 0)
                {
                    continue;
                }

                var visual = GetVisual(line);
                if (visual.Segments.Length > 0 && visual.Segments[0] is null && visual.ChunkSize == 0)
                {
                    GetSegment(visual, 0);
                    if (clock.Elapsed >= budget)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}

