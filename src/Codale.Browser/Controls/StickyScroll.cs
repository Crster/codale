using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Codale.Browser;

/// <summary>
/// Keeps a list pinned to its newest row while the user is at the bottom, like a terminal: new rows scroll into view,
/// but once the user scrolls up to read, the list stays where they left it until they scroll back down.
/// </summary>
internal sealed class StickyScroll
{
    private readonly ListView _list;
    private ScrollViewer? _viewer;
    private bool _queued;

    public StickyScroll(ListView list)
    {
        _list = list;
        list.Loaded += (_, _) => Attach();
    }

    /// <summary>True while the list is scrolled to (or near) its end.</summary>
    public bool AtEnd { get; private set; } = true;

    private void Attach()
    {
        if (_viewer is not null) return;
        if (Find<ScrollViewer>(_list) is not { } viewer) return;
        _viewer = viewer;
        viewer.ViewChanged += (_, _) => AtEnd = viewer.VerticalOffset >= viewer.ScrollableHeight - 24;
    }

    /// <summary>Scrolls to the newest row after the next layout pass, if the list is at its end. Calls in one frame coalesce.</summary>
    public void Follow()
    {
        if (!AtEnd || _queued) return;
        _queued = true;
        _list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _queued = false;
            if (_list.Items.Count == 0) return;
            Attach();
            // Rows have different heights (wrapped messages), so the extent is an estimate; bringing the last row in is exact.
            _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
        });
    }

    /// <summary>Pins the list to its end again (after it was refilled) and scrolls there once laid out.</summary>
    public void JumpToEnd()
    {
        AtEnd = true;
        Follow();
    }

    private static T? Find<T>(Microsoft.UI.Xaml.DependencyObject root) where T : class
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Find<T>(child) is { } nested) return nested;
        }

        return null;
    }
}
