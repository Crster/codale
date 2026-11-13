using Codale.Editor;

using Microsoft.Graphics.Canvas;

namespace Codale.App.Controls;

// Scrolling and the two scrollbars. Both bars are drawn in the editor's own frame. The vertical
// bar doubles as a minimap of markers: diff hunks, search hits, highlighted words, bookmarks and
// carets show as stripes along its track (no text preview). It rests slim and widens while the
// pointer is over it. The horizontal bar stays out of the way until the pointer comes near the
// bottom edge or the view scrolls sideways.
public sealed partial class CodeEditor
{
    private const double VBarRestWidth = 7;
    private const double VBarHoverWidth = 14;
    private const double HBarRestHeight = 6;
    private const double HBarHoverHeight = 12;
    private const double BarHitSize = 14;
    private const double MinThumb = 24;
    private const double WheelScrollTimeConstantMs = 55;

    private double _scrollX;
    private double _scrollY;
    private double _targetY = double.NaN;
    private double _extentWidth;
    private double _extentHeight;

    // Bar animation state: 0..1, eased toward their targets by the frame clock.
    private double _vExpand;
    private double _hReveal;
    private double _hExpand;
    private bool _vHover;
    private bool _hHover;
    private bool _hNear;
    private double _hFlashUntilMs;

    private enum BarDrag
    {
        None,
        Vertical,
        Horizontal,
    }

    private BarDrag _barDrag;
    private double _barGrab;

    // ------------------------------------------------------------------ offsets

    private double MaxScrollY => Math.Max(0, _extentHeight - ViewportHeight);

    private double MaxScrollX => Math.Max(0, _extentWidth - ViewportWidth);

    private void UpdateExtent()
    {
        var height = TotalRows * _lineHeight + Math.Max(_lineHeight * 2, ViewportHeight / 3);
        var width = _wrapCols != NoWrap ? 0 : TextLeft + Math.Max(_maxLineCells * _charWidth, _maxMeasuredWidth) + 80;
        if (height != _extentHeight || width != _extentWidth)
        {
            _extentHeight = height;
            _extentWidth = width;
            ClampScroll();
        }
    }

    private void ClampScroll()
    {
        var x = Math.Clamp(_scrollX, 0, MaxScrollX);
        var y = Math.Clamp(_scrollY, 0, MaxScrollY);
        if (!double.IsNaN(_targetY))
        {
            _targetY = Math.Clamp(_targetY, 0, MaxScrollY);
        }

        if (x != _scrollX || y != _scrollY)
        {
            SetScroll(x, y);
        }
    }

    /// <summary>Moves the view; a null axis stays. Animated moves ease there over a few frames.</summary>
    private void ScrollTo(double? horizontal, double? vertical, bool animate)
    {
        EnsureMetrics();
        UpdateExtent();
        var x = horizontal is { } h ? Math.Clamp(h, 0, MaxScrollX) : _scrollX;
        if (vertical is { } v)
        {
            v = Math.Clamp(v, 0, MaxScrollY);
            if (animate)
            {
                _targetY = v;
                if (x != _scrollX)
                {
                    SetScroll(x, _scrollY);
                }

                StartRenderLoop();
                return;
            }

            _targetY = double.NaN;
            SetScroll(x, v);
            return;
        }

        SetScroll(x, _scrollY);
    }

    private void SetScroll(double x, double y)
    {
        if (x == _scrollX && y == _scrollY)
        {
            return;
        }

        if (x != _scrollX)
        {
            _hFlashUntilMs = _frameClock.Elapsed.TotalMilliseconds + 900;
        }

        _scrollX = x;
        _scrollY = y;
        Invalidate();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scrolls just enough to bring the primary caret into view.</summary>
    private void RevealCaret()
    {
        EnsureMetrics();
        UpdateExtent();
        var (row, x) = Locate(_model.Primary.Caret);
        var top = row * _lineHeight;
        var viewTop = double.IsNaN(_targetY) ? _scrollY : _targetY;
        var height = ViewportHeight;
        var margin = Math.Min(_lineHeight, height / 4);

        double? targetY = null;
        if (top < viewTop + (viewTop > 0 ? margin : 0))
        {
            targetY = Math.Max(0, top - margin);
        }
        else if (top + _lineHeight > viewTop + height - margin)
        {
            targetY = top + _lineHeight + margin - height;
        }

        double? targetX = null;
        if (_wrapCols != NoWrap)
        {
            targetX = 0;
        }
        else
        {
            var textWidth = ViewportWidth - TextLeft - VBarHoverWidth;
            if (x < _scrollX)
            {
                targetX = Math.Max(0, x - textWidth / 4);
            }
            else if (x > _scrollX + textWidth)
            {
                targetX = x - textWidth * 3 / 4;
            }
        }

        if (targetX is not null || targetY is not null)
        {
            ScrollTo(targetX, targetY, animate: false);
        }
    }

    /// <summary>Advances eased motion (wheel scrolling, bars widening); true while anything still moves.</summary>
    private bool StepAnimations(double dt)
    {
        var moving = false;
        if (!double.IsNaN(_targetY))
        {
            var diff = _targetY - _scrollY;
            if (Math.Abs(diff) < 0.5)
            {
                SetScroll(_scrollX, _targetY);
                _targetY = double.NaN;
            }
            else
            {
                var step = diff * (1 - Math.Exp(-dt / WheelScrollTimeConstantMs));
                SetScroll(_scrollX, _scrollY + step);
                moving = true;
            }
        }

        var now = _frameClock.Elapsed.TotalMilliseconds;
        moving |= Ease(ref _vExpand, _vHover || _barDrag == BarDrag.Vertical ? 1 : 0, dt, 110);
        var showH = MaxScrollX > 0.5 && (_hNear || _hHover || _barDrag == BarDrag.Horizontal || now < _hFlashUntilMs);
        moving |= Ease(ref _hReveal, showH ? 1 : 0, dt, showH ? 90 : 220);
        moving |= Ease(ref _hExpand, _hHover || _barDrag == BarDrag.Horizontal ? 1 : 0, dt, 110);
        if (now < _hFlashUntilMs)
        {
            moving = true;
        }

        return moving;
    }

    private static bool Ease(ref double value, double target, double dt, double durationMs)
    {
        if (value == target)
        {
            return false;
        }

        var step = dt / durationMs;
        value = target > value ? Math.Min(target, value + step) : Math.Max(target, value - step);
        return true;
    }

    // ------------------------------------------------------------------ geometry

    private double VBarWidth => VBarRestWidth + (VBarHoverWidth - VBarRestWidth) * EaseOut(_vExpand);

    private double HBarHeight => HBarRestHeight + (HBarHoverHeight - HBarRestHeight) * EaseOut(_hExpand);

    private static double EaseOut(double t) => 1 - (1 - t) * (1 - t);

    private bool HasVBar => MaxScrollY > 0.5;

    private bool HasHBar => MaxScrollX > 0.5;

    private (double Start, double Length) VThumb()
    {
        var track = ViewportHeight;
        var length = Math.Clamp(track * ViewportHeight / Math.Max(1, _extentHeight), Math.Min(MinThumb, track), track);
        var travel = track - length;
        var start = MaxScrollY > 0 ? _scrollY / MaxScrollY * travel : 0;
        return (start, length);
    }

    private (double Start, double Length) HThumb()
    {
        var left = GutterWidth;
        var track = Math.Max(0, ViewportWidth - left - (HasVBar ? VBarHoverWidth : 0));
        var length = Math.Clamp(track * ViewportWidth / Math.Max(1, _extentWidth), Math.Min(MinThumb, track), track);
        var travel = track - length;
        var start = left + (MaxScrollX > 0 ? _scrollX / MaxScrollX * travel : 0);
        return (start, length);
    }

    // ------------------------------------------------------------------ drawing

    private void DrawScrollBars(CanvasDrawingSession ds, double width, double height)
    {
        if (HasVBar)
        {
            var barWidth = VBarWidth;
            var left = (float)(width - barWidth);
            if (_vExpand > 0)
            {
                ds.FillRectangle(left, 0, (float)barWidth, (float)height, Brush(WithAlpha(_trackColor, _trackColor.A * EaseOut(_vExpand))));
            }

            var (start, length) = VThumb();
            var thumbAlpha = _barDrag == BarDrag.Vertical ? 0.55 : 0.28 + 0.17 * _vExpand;
            ds.FillRoundedRectangle(left + 1, (float)start + 1, (float)barWidth - 2, (float)length - 2, 2, 2, Brush(WithAlpha(_thumbColor, 255 * thumbAlpha)));
            DrawMarks(ds, left, (float)barWidth, (float)height);
        }

        if (HasHBar && _hReveal > 0.01)
        {
            var barHeight = HBarHeight;
            var top = (float)(height - barHeight);
            var alpha = EaseOut(_hReveal);
            var (start, length) = HThumb();
            if (_hExpand > 0)
            {
                ds.FillRectangle((float)GutterWidth, top, (float)(width - GutterWidth), (float)barHeight, Brush(WithAlpha(_trackColor, _trackColor.A * _hExpand)));
            }

            var thumbAlpha = (_barDrag == BarDrag.Horizontal ? 0.55 : 0.3 + 0.15 * _hExpand) * alpha;
            ds.FillRoundedRectangle((float)start + 1, top + 1, (float)length - 2, (float)barHeight - 2, 2, 2, Brush(WithAlpha(_thumbColor, 255 * thumbAlpha)));
        }
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, double alpha)
    {
        color.A = (byte)Math.Clamp(alpha, 0, 255);
        return color;
    }

    // ------------------------------------------------------------------ minimap marks

    /// <summary>A stripe on the vertical bar, in track pixels; Line/Col say where a click on it goes.</summary>
    private readonly record struct MarkRun(float Top, float Height, Windows.UI.Color Color, int Lane, int Line, int Col);

    private bool _marksDirty = true;
    private int _marksTrack = -1;
    private readonly List<MarkRun> _markRuns = [];

    private IReadOnlyList<(int StartLine, int EndLine)> _searchMarks = [];
    private int _currentSearchMark = -1;

    private static readonly Windows.UI.Color SearchMarkColor = Windows.UI.Color.FromArgb(0xC0, 0xE5, 0xB2, 0x3C);
    private static readonly Windows.UI.Color CurrentSearchMarkColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x8C, 0x1A);
    private static readonly Windows.UI.Color OccurrenceMarkColor = Windows.UI.Color.FromArgb(0xB0, 0x8A, 0x9B, 0xB4);
    private static readonly Windows.UI.Color CaretMarkColor = Windows.UI.Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);

    /// <summary>
    /// Stripes the scrollbar with where a search hit (0-based, inclusive line ranges);
    /// <paramref name="current"/> is drawn brighter, -1 for none.
    /// </summary>
    public void SetSearchMarks(IReadOnlyList<(int StartLine, int EndLine)> spans, int current)
    {
        _searchMarks = spans;
        _currentSearchMark = current;
        _marksDirty = true;
        Invalidate();
    }

    private void DrawMarks(CanvasDrawingSession ds, float left, float barWidth, float height)
    {
        var track = (int)height;
        if (_marksDirty || track != _marksTrack)
        {
            RebuildMarks(track);
        }

        foreach (var run in _markRuns)
        {
            var (x, w) = run.Lane switch
            {
                0 => (left + 1, Math.Max(2f, barWidth * 0.3f)),
                1 => (left + barWidth * 0.32f, barWidth * 0.42f),
                2 => (left + barWidth * 0.72f, barWidth * 0.28f - 1),
                _ => (left, barWidth),
            };
            ds.FillRectangle(x, run.Top, w, run.Height, Brush(run.Color));
        }
    }

    /// <summary>
    /// Buckets every mark into track pixels per lane (later sources win a pixel), then merges
    /// neighbouring pixels of one colour into runs. Thousands of hits become a few dozen rects.
    /// </summary>
    private void RebuildMarks(int track)
    {
        _marksDirty = false;
        _marksTrack = track;
        _markRuns.Clear();
        var lines = _model.Buffer.LineCount;
        if (track <= 0 || lines == 0)
        {
            return;
        }

        const int Lanes = 4;
        var colors = new Windows.UI.Color[Lanes, track];
        var targets = new (int Line, int Col)[Lanes, track];
        var used = new bool[Lanes, track];
        var totalRows = (double)Math.Max(1, TotalRows);
        var scale = track / totalRows;

        void Put(int lane, int firstLine, int lastLine, Windows.UI.Color color, int col = 0)
        {
            firstLine = Math.Clamp(firstLine, 0, lines - 1);
            lastLine = Math.Clamp(Math.Max(firstLine, lastLine), 0, lines - 1);
            var top = (int)(FirstRowOf(firstLine) * scale);
            var bottom = (int)Math.Ceiling((FirstRowOf(lastLine) + RowCountOf(lastLine)) * scale);
            bottom = Math.Max(bottom, top + 2);
            for (var p = Math.Max(0, top); p < Math.Min(track, bottom); p++)
            {
                colors[lane, p] = color;
                targets[lane, p] = (firstLine, col);
                used[lane, p] = true;
            }
        }

        foreach (var marker in _diffMarkers)
        {
            var end = marker.LineCount == 0 ? marker.StartLine : marker.StartLine + marker.LineCount - 1;
            Put(0, marker.StartLine, end, DiffColor(marker.Kind));
        }

        foreach (var (line, col, _) in _occurrences)
        {
            Put(1, line, line, OccurrenceMarkColor, col);
        }

        foreach (var pinned in _pinnedWords)
        {
            foreach (var (line, col) in pinned.Hits)
            {
                Put(1, line, line, WithAlpha(pinned.Color, 0xE0), col);
            }
        }

        for (var i = 0; i < _searchMarks.Count; i++)
        {
            var (first, last) = _searchMarks[i];
            if (i != _currentSearchMark)
            {
                Put(1, first, last, SearchMarkColor);
            }
        }

        if (_currentSearchMark >= 0 && _currentSearchMark < _searchMarks.Count)
        {
            var (first, last) = _searchMarks[_currentSearchMark];
            Put(1, first, last, CurrentSearchMarkColor);
        }

        foreach (var line in _bookmarks.Lines)
        {
            Put(2, line, line, _bookmarkColor);
        }

        foreach (var selection in _model.Selections)
        {
            if (!selection.IsEmpty)
            {
                Put(2, selection.Start.Line, selection.End.Line, WithAlpha(CaretMarkColor, 0x90), selection.Start.Col);
            }
        }

        // Carets as thin full-width ticks.
        foreach (var selection in _model.Selections)
        {
            var line = Math.Clamp(selection.Caret.Line, 0, lines - 1);
            var p = Math.Clamp((int)(FirstRowOf(line) * scale), 0, track - 1);
            colors[3, p] = CaretMarkColor;
            targets[3, p] = (line, selection.Caret.Col);
            used[3, p] = true;
        }

        for (var lane = 0; lane < Lanes; lane++)
        {
            var p = 0;
            while (p < track)
            {
                if (!used[lane, p])
                {
                    p++;
                    continue;
                }

                var start = p;
                var color = colors[lane, p];
                while (p < track && used[lane, p] && colors[lane, p] == color)
                {
                    p++;
                }

                var (line, col) = targets[lane, start];
                _markRuns.Add(new MarkRun(start, Math.Max(2, p - start), color, lane, line, col));
            }
        }
    }

    /// <summary>The mark under a track y (nearest within a few pixels), preferring the caret and search lanes.</summary>
    private MarkRun? MarkAt(double y)
    {
        if (_markRuns.Count == 0)
        {
            return null;
        }

        MarkRun? best = null;
        var bestDistance = double.MaxValue;
        foreach (var run in _markRuns)
        {
            if (y < run.Top - 3 || y > run.Top + run.Height + 3)
            {
                continue;
            }

            var distance = Math.Abs(run.Top + run.Height / 2 - y);
            if (distance < bestDistance)
            {
                best = run;
                bestDistance = distance;
            }
        }

        return best;
    }
}
