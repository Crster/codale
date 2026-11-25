using System.Collections.Specialized;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace Codale.App.Views;

/// <summary>Virtualizes chat rows without extrapolating the visible rows' heights to the entire transcript.</summary>
internal sealed class TranscriptLayout : VirtualizingLayout
{
    private const double EstimatedRowHeight = 64;
    private readonly List<double> _heights = [];
    private readonly List<double> _offsets = [0];
    private readonly List<(int Index, UIElement Element)> _realized = [];
    private double _width = double.NaN;

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        if (double.IsNaN(_width)) return;

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add when args.NewStartingIndex >= 0:
                _heights.InsertRange(args.NewStartingIndex, Enumerable.Repeat(EstimatedRowHeight, args.NewItems!.Count));
                InvalidateOffsets(args.NewStartingIndex);
                break;
            case NotifyCollectionChangedAction.Remove when args.OldStartingIndex >= 0:
                _heights.RemoveRange(args.OldStartingIndex, args.OldItems!.Count);
                InvalidateOffsets(args.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace when args.NewStartingIndex >= 0 && args.NewItems!.Count == args.OldItems!.Count:
                for (int i = 0; i < args.NewItems.Count; i++)
                    _heights[args.NewStartingIndex + i] = EstimatedRowHeight;
                InvalidateOffsets(args.NewStartingIndex);
                break;
            default:
                ResetMeasurements(context.ItemCount);
                break;
        }

        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        int count = context.ItemCount;
        if (_width != availableSize.Width || _heights.Count != count)
        {
            _width = availableSize.Width;
            ResetMeasurements(count);
        }

        _realized.Clear();
        if (count == 0) return new Size(0, 0);

        int last = count - 1;
        var constraint = new Size(_width, double.PositiveInfinity);
        // Measure the live row even when reading history so reaching the bottom uses its current height.
        MeasureRow(context, last, constraint);
        ExtendOffsets();

        var window = context.RealizationRect;
        int first = _offsets.BinarySearch(Math.Max(0, window.Top));
        first = Math.Clamp(first < 0 ? ~first - 1 : first, 0, last);
        int anchor = context.RecommendedAnchorIndex;
        double top = _offsets[first];
        int index = first;
        for (; index < last && (index == first || top < window.Bottom); index++)
        {
            MeasureRow(context, index, constraint);
            top += _heights[index];
        }

        // The scroller may be keeping a previously visible row in place as the extent changes.
        if (anchor >= 0 && anchor < last && (anchor < first || anchor >= index))
            MeasureRow(context, anchor, constraint);

        ExtendOffsets();
        return new Size(_width, _offsets[^1]);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        foreach (var (index, element) in _realized)
            element.Arrange(new Rect(0, _offsets[index], finalSize.Width, _heights[index]));
        return finalSize;
    }

    private void MeasureRow(VirtualizingLayoutContext context, int index, Size constraint)
    {
        var element = context.GetOrCreateElementAt(index);
        element.Measure(constraint);
        _realized.Add((index, element));
        double height = element.DesiredSize.Height;
        if (_heights[index] == height) return;

        _heights[index] = height;
        InvalidateOffsets(index);
    }

    private void ResetMeasurements(int count)
    {
        _heights.Clear();
        _heights.AddRange(Enumerable.Repeat(EstimatedRowHeight, count));
        InvalidateOffsets(0);
    }

    private void InvalidateOffsets(int index)
    {
        int start = index + 1;
        if (start < _offsets.Count)
            _offsets.RemoveRange(start, _offsets.Count - start);
    }

    private void ExtendOffsets()
    {
        for (int i = _offsets.Count - 1; i < _heights.Count; i++)
            _offsets.Add(_offsets[^1] + _heights[i]);
    }
}
