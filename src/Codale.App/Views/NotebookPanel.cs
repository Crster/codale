using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace Codale.App.Views;

/// <summary>How a child of <see cref="NotebookPanel"/> wants to be placed.</summary>
public interface INotebookCell
{
    /// <summary>Takes the whole visible height of the notebook (a block alone in its tab, or a full-screen app).</summary>
    bool FillsViewport { get; }
}

/// <summary>
/// The notebook terminal's flow: one cell per row, full width, in order. A cell that fills the
/// viewport gets <see cref="FillHeight"/>; collapsed cells (blocks docked beside the centre) take
/// no room. The input cell is just the last child, so it always follows the newest block: at the
/// top of an empty terminal, pushed down by every command.
/// </summary>
public sealed class NotebookPanel : Panel
{
    /// <summary>The height a viewport-filling cell gets: the visible height of the notebook.</summary>
    public double FillHeight
    {
        get => _fillHeight;
        set
        {
            if (Math.Abs(_fillHeight - value) < 0.5)
            {
                return;
            }

            _fillHeight = value;
            InvalidateMeasure();
        }
    }

    private double _fillHeight = 400;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        double total = 0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(new Size(0, 0));
                continue;
            }

            var fills = child is INotebookCell { FillsViewport: true };
            child.Measure(new Size(width, fills ? _fillHeight : double.PositiveInfinity));
            total += fills ? _fillHeight : child.DesiredSize.Height;
        }

        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            var height = child is INotebookCell { FillsViewport: true } ? _fillHeight : child.DesiredSize.Height;
            child.Arrange(new Rect(0, y, finalSize.Width, height));
            y += height;
        }

        // The full slot, not just the rows: a smaller size would be centred in it, floating the
        // input cell of an empty notebook to the middle instead of the top.
        return finalSize;
    }
}
