using System.Collections.Specialized;

using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.ApplicationModel.DataTransfer;

namespace Codale.App.Views;

/// <summary>
/// The terminal dock: a command block dragged to the right edge of the centre (or docked from its
/// header menu) runs in a column beside the tabs - the whole height, or the top or bottom half -
/// and the centre narrows to make room, like the side preview. A docked block still belongs to its
/// notebook (its history, transcript and tab state); the notebook just hides it meanwhile, so it
/// stays in view whichever tab is in front. "Back to its terminal" returns it.
/// </summary>
public sealed partial class WorkspacePage
{
    /// <summary>The right part of the centre where a drop docks (as a fraction of its width).</summary>
    private const double DockZoneFraction = 0.35;

    private sealed class DockedBlock(CommandBlockViewModel block, TerminalViewModel owner, CommandBlockView view)
    {
        public CommandBlockViewModel Block { get; } = block;

        public TerminalViewModel Owner { get; } = owner;

        public CommandBlockView View { get; } = view;

        public NotifyCollectionChangedEventHandler? OwnerChanged { get; set; }
    }

    /// <summary>The top half, or the whole column while <see cref="_dockFull"/>.</summary>
    private DockedBlock? _dockTop;

    private DockedBlock? _dockBottom;

    private bool _dockFull;

    /// <summary>The block whose header is being dragged, and its notebook.</summary>
    private (CommandBlockViewModel Block, TerminalViewModel Owner)? _dockDrag;

    private BlockDock _dockDropZone;

    /// <summary>Docks <paramref name="block"/> in <paramref name="dock"/>, moving whoever was there back to their terminal.</summary>
    private void DockBlock(TerminalViewModel owner, CommandBlockViewModel block, BlockDock dock)
    {
        if (dock == BlockDock.None)
        {
            UndockBlock(block);
            return;
        }

        if (!owner.Blocks.Contains(block))
        {
            return;
        }

        // Already docked: its view is rebuilt in the new slot.
        if (FindDocked(block) is { } current)
        {
            Detach(current);
            ClearSlot(current);
        }

        switch (dock)
        {
            case BlockDock.Right:
                ReturnToTerminal(_dockTop);
                ReturnToTerminal(_dockBottom);
                _dockTop = _dockBottom = null;
                _dockFull = true;
                break;

            case BlockDock.TopRight:
                if (_dockFull && _dockTop is { } wholeForBottom)
                {
                    // The block filling the column makes room: it keeps the other half.
                    _dockBottom = wholeForBottom;
                    _dockTop = null;
                    wholeForBottom.Block.Dock = BlockDock.BottomRight;
                }

                _dockFull = false;
                ReturnToTerminal(_dockTop);
                _dockTop = null;
                break;

            case BlockDock.BottomRight:
                if (_dockFull && _dockTop is { } wholeForTop)
                {
                    wholeForTop.Block.Dock = BlockDock.TopRight;
                }

                _dockFull = false;
                ReturnToTerminal(_dockBottom);
                _dockBottom = null;
                break;
        }

        // The notebook's view lets go of the emulator first; then the dock's view takes it.
        block.Dock = dock;
        var entry = CreateDocked(owner, block);
        if (dock == BlockDock.BottomRight)
        {
            _dockBottom = entry;
        }
        else
        {
            _dockTop = entry;
        }

        LayoutDock();
    }

    /// <summary>"Back to its terminal": the block reappears in its notebook.</summary>
    private void UndockBlock(CommandBlockViewModel block)
    {
        if (FindDocked(block) is not { } entry)
        {
            return;
        }

        ClearSlot(entry);
        ReturnToTerminal(entry);
        LayoutDock();
    }

    private DockedBlock? FindDocked(CommandBlockViewModel block) =>
        ReferenceEquals(_dockTop?.Block, block) ? _dockTop
        : ReferenceEquals(_dockBottom?.Block, block) ? _dockBottom
        : null;

    private void ClearSlot(DockedBlock entry)
    {
        if (ReferenceEquals(_dockTop, entry))
        {
            _dockTop = null;
            if (_dockFull)
            {
                _dockFull = false;
            }
        }

        if (ReferenceEquals(_dockBottom, entry))
        {
            _dockBottom = null;
        }
    }

    private DockedBlock CreateDocked(TerminalViewModel owner, CommandBlockViewModel block)
    {
        var view = new CommandBlockView(block, docked: true);
        var entry = new DockedBlock(block, owner, view);

        view.UndockRequested += (_, b) => UndockBlock(b);
        view.DockRequested += (_, request) => DockBlock(owner, request.Block, request.Dock);
        view.RerunRequested += (_, b) => owner.Rerun(b);
        view.RemoveRequested += async (_, b) =>
        {
            UndockBlock(b);
            if (b.IsRunning)
            {
                await b.StopAsync();
            }

            owner.RemoveBlock(b);
        };
        view.PopOutRequested += (_, b) =>
        {
            UndockBlock(b);
            OpenBlockInNewTab(owner, b);
        };
        view.LinkActivated += OnTerminalLinkActivated;
        view.DragStarted += (_, b) => BeginDockDrag(owner, b);
        view.DragEnded += (_, _) => EndDockDrag();

        // The block left its notebook (removed, moved to a new tab, or the tab closed): it leaves the dock too.
        entry.OwnerChanged = (_, _) =>
        {
            if (!owner.Blocks.Contains(block) && FindDocked(block) is { } gone)
            {
                ClearSlot(gone);
                Detach(gone);
                block.Dock = BlockDock.None;
                LayoutDock();
            }
        };
        owner.Blocks.CollectionChanged += entry.OwnerChanged;
        return entry;
    }

    /// <summary>Closes an entry's dock view; the block itself is untouched.</summary>
    private static void Detach(DockedBlock entry)
    {
        entry.Owner.Blocks.CollectionChanged -= entry.OwnerChanged;
        if (entry.View.Parent is Border host)
        {
            host.Child = null;
        }

        entry.View.Close();
    }

    private static void ReturnToTerminal(DockedBlock? entry)
    {
        if (entry is null)
        {
            return;
        }

        // The dock's view lets go of the emulator first; then the notebook's view takes it back.
        Detach(entry);
        entry.Block.Dock = BlockDock.None;
    }

    /// <summary>Opens, sizes or closes the dock column for what is docked.</summary>
    private void LayoutDock()
    {
        if (_dockTop is null && _dockBottom is null)
        {
            _dockFull = false;
            DockTopHost.Child = null;
            DockBottomHost.Child = null;
            DockPane.Visibility = Visibility.Collapsed;
            DockSplitter.Visibility = Visibility.Collapsed;
            DockSplitterColumn.Width = new GridLength(0);
            DockColumn.Width = new GridLength(0);
            return;
        }

        if (DockPane.Visibility != Visibility.Visible)
        {
            // Opens at half the centre, like the side preview; the splitter resizes it from there.
            DockSplitterColumn.Width = GridLength.Auto;
            DockColumn.Width = new GridLength(1, GridUnitType.Star);
            DockColumn.MinWidth = 240;
            DockPane.Visibility = Visibility.Visible;
            DockSplitter.Visibility = Visibility.Visible;
        }

        DockTopHost.Child = _dockTop?.View ?? (_dockFull ? null : EmptyDockSlot());
        if (_dockFull)
        {
            DockTopRow.Height = new GridLength(1, GridUnitType.Star);
            DockSplitterRow.Height = new GridLength(0);
            DockBottomRow.Height = new GridLength(0);
            DockRowSplitter.Visibility = Visibility.Collapsed;
            DockBottomHost.Child = null;
        }
        else
        {
            DockTopRow.Height = new GridLength(1, GridUnitType.Star);
            DockSplitterRow.Height = GridLength.Auto;
            DockBottomRow.Height = new GridLength(1, GridUnitType.Star);
            DockRowSplitter.Visibility = Visibility.Visible;
            DockBottomHost.Child = _dockBottom?.View ?? EmptyDockSlot();
        }
    }

    /// <summary>What an empty half shows: where to drop the next command.</summary>
    private UIElement EmptyDockSlot() => new Border
    {
        BorderBrush = CommandBlockView.ThemeBrush(DockPane, "ShellHairlineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Child = new TextBlock
        {
            Text = "Drag a command's header here",
            FontSize = 12,
            Foreground = CommandBlockView.ThemeBrush(DockPane, "TextFillColorTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    // ---- Dragging a block to the dock ------------------------------------------------------

    private void BeginDockDrag(TerminalViewModel owner, CommandBlockViewModel block)
    {
        _dockDrag = (block, owner);
        _dockDropZone = BlockDock.None;
        DockDropHint.Visibility = Visibility.Collapsed;
        DockDropTarget.Visibility = Visibility.Visible;
    }

    private void EndDockDrag()
    {
        _dockDrag = null;
        _dockDropZone = BlockDock.None;
        DockDropHint.Visibility = Visibility.Collapsed;
        DockDropTarget.Visibility = Visibility.Collapsed;
    }

    /// <summary>The right edge of the centre docks: its top third the top half, its bottom third the bottom half, between the whole column.</summary>
    private BlockDock DockZoneAt(Windows.Foundation.Point p)
    {
        var width = DockDropTarget.ActualWidth;
        var height = DockDropTarget.ActualHeight;
        if (_dockDrag is null || width <= 0 || height <= 0 || p.X < width * (1 - DockZoneFraction))
        {
            return BlockDock.None;
        }

        return p.Y < height / 3 ? BlockDock.TopRight
            : p.Y > height * 2 / 3 ? BlockDock.BottomRight
            : BlockDock.Right;
    }

    private void OnDockDropTargetDragOver(object sender, DragEventArgs e)
    {
        _dockDropZone = DockZoneAt(e.GetPosition(DockDropTarget));
        e.AcceptedOperation = _dockDropZone == BlockDock.None ? DataPackageOperation.None : DataPackageOperation.Move;
        var caption = _dockDropZone switch
        {
            BlockDock.Right => "Dock right",
            BlockDock.TopRight => "Dock top right",
            BlockDock.BottomRight => "Dock bottom right",
            _ => "",
        };
        e.DragUIOverride.Caption = caption;
        e.DragUIOverride.IsCaptionVisible = caption.Length > 0;
        e.Handled = true;

        if (_dockDropZone == BlockDock.None)
        {
            DockDropHint.Visibility = Visibility.Collapsed;
            return;
        }

        var width = DockDropTarget.ActualWidth;
        var height = DockDropTarget.ActualHeight;
        DockDropHint.HorizontalAlignment = HorizontalAlignment.Right;
        DockDropHint.Width = Math.Max(0, width * 0.45 - 8);
        DockDropHint.VerticalAlignment = _dockDropZone switch
        {
            BlockDock.TopRight => VerticalAlignment.Top,
            BlockDock.BottomRight => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch,
        };
        DockDropHint.Height = _dockDropZone == BlockDock.Right ? double.NaN : Math.Max(0, height / 2 - 8);
        DockDropCaption.Text = caption;
        DockDropHint.Visibility = Visibility.Visible;
    }

    private void OnDockDropTargetDragLeave(object sender, DragEventArgs e) =>
        DockDropHint.Visibility = Visibility.Collapsed;

    private void OnDockDropTargetDrop(object sender, DragEventArgs e)
    {
        var zone = DockZoneAt(e.GetPosition(DockDropTarget));
        var drag = _dockDrag;
        e.Handled = true;
        EndDockDrag();
        if (drag is { } d && zone != BlockDock.None)
        {
            // Let the drag source finish before its view is hidden under it.
            DispatcherQueue.TryEnqueue(() => DockBlock(d.Owner, d.Block, zone));
        }
    }
}
