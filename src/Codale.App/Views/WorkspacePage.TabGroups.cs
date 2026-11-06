using CommunityToolkit.WinUI.Controls;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Codale.App.Views;

/// <summary>
/// The centre can hold two tab groups, split side by side or stacked. Dragging a tab onto
/// an edge of the content splits the centre and moves the tab into the new group; dragging
/// it onto the other group (its content or its strip) moves it there. A group that runs out
/// of tabs closes and the centre is one group again. <see cref="CentreTabs"/> is always the
/// primary group, so code that only knows about one strip keeps working when not split.
/// </summary>
public sealed partial class WorkspacePage
{
    private enum DropZone { None, Left, Right, Top, Bottom, Centre }

    /// <summary>Edges within this fraction of the content split it; the middle moves the tab.</summary>
    private const double EdgeFraction = 0.3;

    private TabView? _activeGroup;
    private bool _splitSideBySide = true;
    private bool _secondaryFirst;

    private TabViewItem? _draggedTab;
    private TabView? _dragSource;
    private DropZone _dropZone;

    private bool IsSplit => SecondaryGroup.Visibility == Visibility.Visible;

    /// <summary>The group new tabs open in and whose selected tab counts as "in front".</summary>
    private TabView ActiveGroup =>
        _activeGroup is { } group && (ReferenceEquals(group, CentreTabs) || IsSplit) ? group : CentreTabs;

    /// <summary>The tab in front: the selected tab of the active group.</summary>
    private object? ActiveTabItem => ActiveGroup.SelectedItem;

    private void InitializeTabGroups()
    {
        foreach (var group in new[] { CentreTabs, SplitTabs })
        {
            // Clicking anywhere in a group (its strip or its content) makes it the active one.
            var g = group;
            g.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => SetActiveGroup(g)), handledEventsToo: true);
            g.GotFocus += (_, _) => SetActiveGroup(g);
        }

        ApplyTabGroupLayout();
    }

    private TabView? GroupOf(object item) =>
        CentreTabs.TabItems.Contains(item) ? CentreTabs
        : SplitTabs.TabItems.Contains(item) ? SplitTabs
        : null;

    /// <summary>True while the tab is on screen: selected in either visible group.</summary>
    private bool IsTabShowing(object item) =>
        ReferenceEquals(CentreTabs.SelectedItem, item) || (IsSplit && ReferenceEquals(SplitTabs.SelectedItem, item));

    private void SetActiveGroup(TabView group)
    {
        if (ReferenceEquals(ActiveGroup, group))
        {
            return;
        }

        _activeGroup = group;
        OnActiveTabChanged();
    }

    /// <summary>Brings a tab to front in whichever group holds it, and makes that group active.</summary>
    private void SelectTab(object? item)
    {
        if (item is null)
        {
            return;
        }

        var group = GroupOf(item) ?? ActiveGroup;
        var changed = !ReferenceEquals(ActiveGroup, group);
        _activeGroup = group;
        group.SelectedItem = item;
        if (changed)
        {
            OnActiveTabChanged();
        }
    }

    /// <summary>Opens a tab in the active group, at <paramref name="index"/> when it is in range.</summary>
    private void AddTab(TabViewItem item, int index = -1)
    {
        var group = ActiveGroup;
        if (index >= 0 && index <= group.TabItems.Count)
        {
            group.TabItems.Insert(index, item);
        }
        else
        {
            group.TabItems.Add(item);
        }
    }

    private void RemoveTab(object item) => GroupOf(item)?.TabItems.Remove(item);

    /// <summary>
    /// Keeps the invariant that the primary group is never empty while split, and closes the
    /// secondary group once it has no tabs. Called from <see cref="UpdateEmptyState"/>.
    /// </summary>
    private void NormalizeTabGroups()
    {
        if (!IsSplit)
        {
            return;
        }

        if (CentreTabs.TabItems.Count == 0)
        {
            var selected = SplitTabs.SelectedItem;
            var items = SplitTabs.TabItems.ToList();
            SplitTabs.TabItems.Clear();
            foreach (var item in items)
            {
                CentreTabs.TabItems.Add(item);
            }

            CentreTabs.SelectedItem = selected ?? items.FirstOrDefault();
        }

        if (SplitTabs.TabItems.Count == 0)
        {
            SecondaryGroup.Visibility = Visibility.Collapsed;
            _activeGroup = CentreTabs;
            ApplyTabGroupLayout();
            OnActiveTabChanged();
        }
    }

    /// <summary>Places the groups and the splitter in the 3x3 grid for the current split.</summary>
    private void ApplyTabGroupLayout()
    {
        var cols = TabGroups.ColumnDefinitions;
        var rows = TabGroups.RowDefinitions;
        var star = new GridLength(1, GridUnitType.Star);
        var zero = new GridLength(0);

        for (var i = 0; i < 3; i++)
        {
            cols[i].Width = i == 0 ? star : zero;
            cols[i].MinWidth = 0;
            rows[i].Height = i == 0 ? star : zero;
            rows[i].MinHeight = 0;
        }

        Place(PrimaryGroup, 0);
        Place(SecondaryGroup, 0);
        Place(TabGroupSplitter, 0);

        if (!IsSplit)
        {
            TabGroupSplitter.Visibility = Visibility.Collapsed;
            return;
        }

        var (first, second) = _secondaryFirst ? (SecondaryGroup, PrimaryGroup) : (PrimaryGroup, SecondaryGroup);
        if (_splitSideBySide)
        {
            cols[0].Width = star;
            cols[1].Width = GridLength.Auto;
            cols[2].Width = star;
            cols[0].MinWidth = cols[2].MinWidth = 160;
            TabGroupSplitter.ResizeDirection = GridSplitter.GridResizeDirection.Columns;
            TabGroupSplitter.Width = 8;
            TabGroupSplitter.Height = double.NaN;
            TabGroupSplitter.HorizontalAlignment = HorizontalAlignment.Center;
            TabGroupSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(first, 0);
            Grid.SetColumn(TabGroupSplitter, 1);
            Grid.SetColumn(second, 2);
        }
        else
        {
            rows[0].Height = star;
            rows[1].Height = GridLength.Auto;
            rows[2].Height = star;
            rows[0].MinHeight = rows[2].MinHeight = 120;
            TabGroupSplitter.ResizeDirection = GridSplitter.GridResizeDirection.Rows;
            TabGroupSplitter.Height = 8;
            TabGroupSplitter.Width = double.NaN;
            TabGroupSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            TabGroupSplitter.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(first, 0);
            Grid.SetRow(TabGroupSplitter, 1);
            Grid.SetRow(second, 2);
        }

        TabGroupSplitter.Visibility = Visibility.Visible;

        static void Place(FrameworkElement element, int index)
        {
            Grid.SetRow(element, index);
            Grid.SetColumn(element, index);
        }
    }

    /// <summary>Moves a tab to another group (at the end, or at <paramref name="index"/>) and brings it to front.</summary>
    private void MoveTab(TabViewItem item, TabView target, int index = -1)
    {
        if (GroupOf(item) is not { } source)
        {
            return;
        }

        if (ReferenceEquals(source, target))
        {
            return;
        }

        source.TabItems.Remove(item);
        if (index >= 0 && index <= target.TabItems.Count)
        {
            target.TabItems.Insert(index, item);
        }
        else
        {
            target.TabItems.Add(item);
        }

        _activeGroup = target;
        target.SelectedItem = item;
        UpdateEmptyState();
        OnActiveTabChanged();
    }

    /// <summary>Splits the centre with <paramref name="item"/> alone in the new group, on the given edge.</summary>
    private void SplitWithTab(TabViewItem item, DropZone edge)
    {
        if (IsSplit || !CentreTabs.TabItems.Contains(item) || CentreTabs.TabItems.Count < 2)
        {
            return;
        }

        _splitSideBySide = edge is DropZone.Left or DropZone.Right;
        _secondaryFirst = edge is DropZone.Left or DropZone.Top;
        SecondaryGroup.Visibility = Visibility.Visible;
        ApplyTabGroupLayout();
        MoveTab(item, SplitTabs);
    }

    // ---- Dragging -------------------------------------------------------------------------

    private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        _draggedTab = args.Tab;
        _dragSource = sender;
        _dropZone = DropZone.None;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.Properties["CodaleTab"] = true;

        foreach (var (group, target) in DropTargets())
        {
            // Leave the strip uncovered so tabs still reorder and drop onto the other strip.
            var top = FindTemplateChild(group, "TabContentPresenter") is FrameworkElement content
                ? content.TransformToVisual(group).TransformPoint(new Point(0, 0)).Y
                : 40;
            target.Margin = new Thickness(0, Math.Max(0, top), 0, 0);
            target.Visibility = Visibility.Visible;
        }
    }

    private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args) => EndTabDrag();

    private void EndTabDrag()
    {
        _draggedTab = null;
        _dragSource = null;
        _dropZone = DropZone.None;
        PrimaryDropHint.Visibility = SecondaryDropHint.Visibility = Visibility.Collapsed;
        PrimaryDropTarget.Visibility = SecondaryDropTarget.Visibility = Visibility.Collapsed;
    }

    private IEnumerable<(TabView Group, Grid Target)> DropTargets()
    {
        yield return (CentreTabs, PrimaryDropTarget);
        if (IsSplit)
        {
            yield return (SplitTabs, SecondaryDropTarget);
        }
    }

    private TabView GroupForTarget(object target) =>
        ReferenceEquals(target, SecondaryDropTarget) ? SplitTabs : CentreTabs;

    private Border HintForTarget(object target) =>
        ReferenceEquals(target, SecondaryDropTarget) ? SecondaryDropHint : PrimaryDropHint;

    /// <summary>What dropping here would do: split on an edge, move into this group, or nothing.</summary>
    private DropZone ZoneAt(Grid target, Point p)
    {
        if (_draggedTab is null || _dragSource is null)
        {
            return DropZone.None;
        }

        var group = GroupForTarget(target);
        if (IsSplit)
        {
            // Two groups already: the only move is into the other one.
            return ReferenceEquals(group, _dragSource) ? DropZone.None : DropZone.Centre;
        }

        // Splitting needs a tab left behind in the primary group.
        if (CentreTabs.TabItems.Count < 2 || target.ActualWidth <= 0 || target.ActualHeight <= 0)
        {
            return DropZone.None;
        }

        var fx = p.X / target.ActualWidth;
        var fy = p.Y / target.ActualHeight;
        var (zone, distance) = new[]
        {
            (DropZone.Left, fx),
            (DropZone.Right, 1 - fx),
            (DropZone.Top, fy),
            (DropZone.Bottom, 1 - fy),
        }.MinBy(z => z.Item2);

        return distance < EdgeFraction ? zone : DropZone.None;
    }

    private void OnGroupDropTargetDragOver(object sender, DragEventArgs e)
    {
        if (sender is not Grid target)
        {
            return;
        }

        _dropZone = ZoneAt(target, e.GetPosition(target));
        e.AcceptedOperation = _dropZone == DropZone.None ? DataPackageOperation.None : DataPackageOperation.Move;
        e.DragUIOverride.Caption = _dropZone switch
        {
            DropZone.Left => "Split left",
            DropZone.Right => "Split right",
            DropZone.Top => "Split up",
            DropZone.Bottom => "Split down",
            DropZone.Centre => "Move here",
            _ => string.Empty,
        };
        e.DragUIOverride.IsCaptionVisible = _dropZone != DropZone.None;
        e.Handled = true;

        ShowDropHint(HintForTarget(target), target, _dropZone);
    }

    private static void ShowDropHint(Border hint, Grid target, DropZone zone)
    {
        if (zone == DropZone.None)
        {
            hint.Visibility = Visibility.Collapsed;
            return;
        }

        var halfWidth = Math.Max(0, target.ActualWidth / 2 - 8);
        var halfHeight = Math.Max(0, target.ActualHeight / 2 - 8);
        hint.HorizontalAlignment = zone switch
        {
            DropZone.Left => HorizontalAlignment.Left,
            DropZone.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch,
        };
        hint.VerticalAlignment = zone switch
        {
            DropZone.Top => VerticalAlignment.Top,
            DropZone.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch,
        };
        hint.Width = zone is DropZone.Left or DropZone.Right ? halfWidth : double.NaN;
        hint.Height = zone is DropZone.Top or DropZone.Bottom ? halfHeight : double.NaN;
        hint.Visibility = Visibility.Visible;
    }

    private void OnGroupDropTargetDragLeave(object sender, DragEventArgs e) =>
        HintForTarget(sender).Visibility = Visibility.Collapsed;

    private void OnGroupDropTargetDrop(object sender, DragEventArgs e)
    {
        if (sender is not Grid target || _draggedTab is not { } item)
        {
            return;
        }

        var zone = ZoneAt(target, e.GetPosition(target));
        var group = GroupForTarget(target);
        e.Handled = true;
        EndTabDrag();

        // Let the source TabView finish its own drag before its item is taken away.
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (zone)
            {
                case DropZone.Centre:
                    MoveTab(item, group);
                    break;
                case DropZone.Left or DropZone.Right or DropZone.Top or DropZone.Bottom:
                    SplitWithTab(item, zone);
                    break;
            }
        });
    }

    /// <summary>A tab dragged over the other group's strip can be dropped among its tabs.</summary>
    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        if (_draggedTab is not null && _dragSource is not null && !ReferenceEquals(sender, _dragSource))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "Move here";
            e.DragUIOverride.IsCaptionVisible = true;
            e.Handled = true;
        }
    }

    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        if (sender is not TabView target || _draggedTab is not { } item || ReferenceEquals(target, _dragSource))
        {
            return;
        }

        // Insert before the first tab whose middle is right of the pointer.
        var x = e.GetPosition(target).X;
        var index = target.TabItems.Count;
        for (var i = 0; i < target.TabItems.Count; i++)
        {
            if (target.ContainerFromIndex(i) is TabViewItem tab)
            {
                var left = tab.TransformToVisual(target).TransformPoint(new Point(0, 0)).X;
                if (x < left + tab.ActualWidth / 2)
                {
                    index = i;
                    break;
                }
            }
        }

        e.Handled = true;
        EndTabDrag();
        DispatcherQueue.TryEnqueue(() => MoveTab(item, target, index));
    }
}
