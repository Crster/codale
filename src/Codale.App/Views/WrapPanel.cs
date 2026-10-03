using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace Codale.App.Views;

/// <summary>
/// Lays children out left to right, wrapping to a new line when the row runs out of
/// width - what the diff tab's file chips want, and what the platform's ItemsWrapGrid
/// cannot give, since it forces every cell to the widest child's width.
/// </summary>
/// <remarks>
/// Gaps are the children's own business: margins are part of their desired size, so a
/// chip row gets its spacing from the item template's margin, not from panel properties.
/// </remarks>
public sealed class WrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double rowWidth = 0, rowHeight = 0, widestRow = 0, totalHeight = 0;

        foreach (var child in Children)
        {
            child.Measure(availableSize);

            var size = child.DesiredSize;

            if (rowWidth > 0 && rowWidth + size.Width > availableSize.Width)
            {
                widestRow = Math.Max(widestRow, rowWidth);
                totalHeight += rowHeight;
                rowWidth = 0;
                rowHeight = 0;
            }

            rowWidth += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        widestRow = Math.Max(widestRow, rowWidth);
        totalHeight += rowHeight;

        return new Size(widestRow, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;

            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }
}
