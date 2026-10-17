using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Controls;

/// <summary>
/// A stripe on the scrollbar track: a span of the content (0..1 of its length) in one of
/// two lanes, so an editor can show git edits and search hits next to the thumb.
/// </summary>
public delegate void WheelHandler(object sender, PointerRoutedEventArgs e);

public readonly record struct ScrollMark(double Start, double End, Windows.UI.Color Color, int Lane);

/// <summary>
/// The scroll position of a surface that draws its own content: offsets, viewport and
/// extent, clamped against each other. Replaces the ScrollViewer these controls used to
/// carry only for its bars - nothing here is a XAML element, so scrolling is a number
/// change followed by one repaint.
/// </summary>
internal sealed class ScrollModel
{
    public double HorizontalOffset { get; private set; }

    public double VerticalOffset { get; private set; }

    public double ViewportWidth { get; private set; }

    public double ViewportHeight { get; private set; }

    public double ExtentWidth { get; private set; }

    public double ExtentHeight { get; private set; }

    public double ScrollableWidth => Math.Max(0, ExtentWidth - ViewportWidth);

    public double ScrollableHeight => Math.Max(0, ExtentHeight - ViewportHeight);

    /// <summary>Raised when an offset or a size moved, so bars and painting can follow.</summary>
    public event Action? Changed;

    public void SetLayout(double viewportWidth, double viewportHeight, double extentWidth, double extentHeight)
    {
        if (viewportWidth == ViewportWidth && viewportHeight == ViewportHeight
            && extentWidth == ExtentWidth && extentHeight == ExtentHeight)
        {
            return;
        }

        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
        ExtentWidth = extentWidth;
        ExtentHeight = extentHeight;
        HorizontalOffset = Clamp(HorizontalOffset, ViewportWidth, ScrollableWidth);
        VerticalOffset = Clamp(VerticalOffset, ViewportHeight, ScrollableHeight);
        Changed?.Invoke();
    }

    /// <summary>Moves the view; a null axis stays where it is.</summary>
    public void ChangeView(double? horizontal, double? vertical)
    {
        var h = horizontal is { } hv ? Clamp(hv, ViewportWidth, ScrollableWidth) : HorizontalOffset;
        var v = vertical is { } vv ? Clamp(vv, ViewportHeight, ScrollableHeight) : VerticalOffset;
        if (h == HorizontalOffset && v == VerticalOffset)
        {
            return;
        }

        HorizontalOffset = h;
        VerticalOffset = v;
        Changed?.Invoke();
    }

    // Before the first layout the viewport is 0 and the extent means nothing yet, so a
    // restored scroll position is kept rather than clamped to zero.
    private static double Clamp(double value, double viewport, double scrollable) =>
        viewport <= 0 ? Math.Max(0, value) : Math.Clamp(value, 0, scrollable);
}

/// <summary>
/// A thin scrollbar drawn on top of its content. It stays invisible until the pointer
/// reaches it or the content scrolls, then fades out again a moment later. It owns no
/// scroll state: the host feeds it <see cref="Update"/> and listens for
/// <see cref="ValueChanged"/> when the user drags or clicks the track.
/// </summary>
public sealed class OverlayScrollBar : Grid
{
    public const double Thickness = 12;

    private const double MinThumb = 28;
    private const double Inset = 2;
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(900);

    private readonly bool _vertical;
    private readonly Border _thumb;
    private readonly CanvasControl _marksCanvas;
    private readonly DispatcherQueueTimer _hideTimer;
    private IReadOnlyList<ScrollMark> _marks = [];

    private double _value;
    private double _maximum;
    private double _viewport;
    private bool _hovering;
    private bool _dragging;
    private double _grabOffset;

    public OverlayScrollBar(bool vertical)
    {
        _vertical = vertical;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Opacity = 0;
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) };
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = vertical ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        if (vertical)
        {
            Width = Thickness;
        }
        else
        {
            Height = Thickness;
        }

        _marksCanvas = new CanvasControl { IsHitTestVisible = false };
        _marksCanvas.Draw += OnMarksDraw;
        Children.Add(_marksCanvas);

        _thumb = new Border
        {
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Children.Add(_thumb);

        _hideTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hideTimer.Interval = HideDelay;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => Fade(visible: _hovering || _dragging);

        ActualThemeChanged += (_, _) => ApplyThumbColor();
        SizeChanged += (_, _) =>
        {
            LayoutThumb();
            _marksCanvas.Invalidate();
        };
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerWheelChanged += (_, e) => WheelScrolled?.Invoke(this, e);
        ApplyThumbColor();
    }

    /// <summary>The user moved the thumb; the argument is the new offset in the host's units.</summary>
    public event EventHandler<double>? ValueChanged;

    /// <summary>The wheel turned over the bar; the host scrolls as if it had turned over the content.</summary>
    public event WheelHandler? WheelScrolled;

    /// <summary>The stripes drawn on the track; replace the list to repaint.</summary>
    public IReadOnlyList<ScrollMark> Marks
    {
        get => _marks;
        set
        {
            _marks = value;
            _marksCanvas.Invalidate();
        }
    }

    /// <summary>
    /// Mirrors the host's scroll position: <paramref name="maximum"/> is the largest offset,
    /// <paramref name="viewport"/> the visible length in the same units. <paramref name="reveal"/>
    /// flashes the bar, for scrolls the user would want to see the position of.
    /// </summary>
    public void Update(double value, double maximum, double viewport, bool reveal)
    {
        var moved = value != _value;
        _value = value;
        _maximum = maximum;
        _viewport = viewport;

        var scrollable = maximum > 0.5;
        Visibility = scrollable ? Visibility.Visible : Visibility.Collapsed;
        LayoutThumb();

        if (scrollable && reveal && moved)
        {
            Flash();
        }
    }

    /// <summary>Shows the bar for a moment.</summary>
    public void Flash()
    {
        Fade(visible: true);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void Fade(bool visible) => Opacity = visible ? 1 : 0;

    private double Track => Math.Max(0, (_vertical ? ActualHeight : ActualWidth));

    private double ThumbLength()
    {
        var track = Track - Inset * 2;
        if (track <= 0)
        {
            return 0;
        }

        var total = _maximum + _viewport;
        return total <= 0 ? track : Math.Clamp(track * _viewport / total, Math.Min(MinThumb, track), track);
    }

    private void LayoutThumb()
    {
        var length = ThumbLength();
        var travel = Math.Max(0, Track - Inset * 2 - length);
        var position = Inset + (_maximum > 0 ? Math.Clamp(_value / _maximum, 0, 1) * travel : 0);
        var across = Thickness - Inset * 2 - 2;
        if (_vertical)
        {
            _thumb.Width = across;
            _thumb.Height = length;
            _thumb.Margin = new Thickness(Inset + 1, position, 0, 0);
        }
        else
        {
            _thumb.Width = length;
            _thumb.Height = across;
            _thumb.Margin = new Thickness(position, Inset + 1, 0, 0);
        }
    }

    private void ApplyThumbColor()
    {
        var dark = ActualTheme != ElementTheme.Light;
        var alpha = (byte)(_dragging ? 0xB0 : _hovering ? 0x90 : 0x66);
        _thumb.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(alpha, 0xC8, 0xC8, 0xC8)
            : Windows.UI.Color.FromArgb(alpha, 0x50, 0x50, 0x50));
    }

    private void OnMarksDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!_vertical || _marks.Count == 0)
        {
            return;
        }

        var track = (float)(sender.ActualHeight - Inset * 2);
        if (track <= 0)
        {
            return;
        }

        // Two lanes side by side: lane 0 on the left, lane 1 on the right.
        const float laneWidth = 4;
        foreach (var mark in _marks)
        {
            var top = (float)(Inset + Math.Clamp(mark.Start, 0, 1) * track);
            var height = Math.Max(2f, (float)((Math.Clamp(mark.End, 0, 1) - Math.Clamp(mark.Start, 0, 1)) * track));
            var left = mark.Lane == 0 ? 1.5f : 6.5f;
            args.DrawingSession.FillRectangle(left, top, laneWidth, height, mark.Color);
        }
    }

    // ------------------------------------------------------------------ pointer

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hovering = true;
        _hideTimer.Stop();
        Fade(visible: true);
        ApplyThumbColor();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hovering = false;
        ApplyThumbColor();
        if (!_dragging)
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }
    }

    private double AxisOf(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        return _vertical ? point.Y : point.X;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var axis = AxisOf(e);
        var length = ThumbLength();
        var margin = _thumb.Margin;
        var start = _vertical ? margin.Top : margin.Left;
        if (axis >= start && axis <= start + length)
        {
            _grabOffset = axis - start;
        }
        else
        {
            // A click on the track centres the thumb there, then carries on as a drag.
            _grabOffset = length / 2;
        }

        _dragging = true;
        CapturePointer(e.Pointer);
        ApplyThumbColor();
        MoveTo(axis);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            MoveTo(AxisOf(e));
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ApplyThumbColor();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void MoveTo(double axis)
    {
        var travel = Track - Inset * 2 - ThumbLength();
        if (travel <= 0 || _maximum <= 0)
        {
            return;
        }

        var fraction = Math.Clamp((axis - _grabOffset - Inset) / travel, 0, 1);
        var value = fraction * _maximum;
        if (value != _value)
        {
            ValueChanged?.Invoke(this, value);
        }
    }
}
