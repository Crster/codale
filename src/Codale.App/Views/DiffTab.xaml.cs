using System.ComponentModel;
using System.Text;

using Codale.App.ViewModels;
using Codale.Git;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;

using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Codale.App.Views;

/// <summary>
/// A diff in the centre area: one uncommitted file, or every file a commit changed.
/// There is one tab for all diffs - each request replaces its contents, the way the
/// preview editor tab works for files.
/// </summary>
/// <remarks>
/// Rows select like lines in an editor (click, Shift+click, drag, Ctrl+A) and copy with
/// Ctrl+C or the context menu. The selection is a range of row indexes, not state on the row
/// objects: rows are virtualized and made on demand, so realized elements are painted from it.
/// </remarks>
public sealed partial class DiffTab : UserControl
{
    private int _anchor = -1;
    private int _active = -1;
    private bool _dragging;
    private readonly HashSet<UIElement> _hooked = [];

    public DiffTab()
    {
        InitializeComponent();

        AddAccelerator(VirtualKey.C, () => CopySelection(asPatch: false));
        AddAccelerator(VirtualKey.A, () =>
        {
            SelectAll();
            return true;
        });

        // Shift+Up/Down grow or shrink the selection a row at a time from its moving end.
        foreach (var (key, step) in new[] { (VirtualKey.Down, 1), (VirtualKey.Up, -1) })
        {
            var extend = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Shift };
            extend.Invoked += (_, args) => args.Handled = ExtendSelection(step);
            RowsHost.KeyboardAccelerators.Add(extend);
        }

        RowsHost.PointerReleased += (_, _) => _dragging = false;
        RowsHost.PointerCaptureLost += (_, _) => _dragging = false;
    }

    public DiffViewerViewModel ViewModel { get; private set; } = null!;

    public DiffTab WithViewModel(DiffViewerViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Bindings.Update();
        return this;
    }

    /// <summary>The user asked for the file behind the diff; the workspace opens an editor tab.</summary>
    public event EventHandler<string>? FileOpenRequested;

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } file)
        {
            FileOpenRequested?.Invoke(this, file.Path);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffViewerViewModel.Rows))
        {
            // A different file: the old selection indexes mean nothing in it.
            _anchor = _active = -1;
            _dragging = false;
            RefreshSelection();
        }
    }

    private void AddAccelerator(VirtualKey key, Func<bool> invoke)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) => args.Handled = invoke();
        RowsHost.KeyboardAccelerators.Add(accelerator);
    }

    // ------------------------------------------------------------------ selection

    private bool HasSelection => _anchor >= 0 && _active >= 0;

    private (int First, int Last) SelectedRange =>
        HasSelection ? (Math.Min(_anchor, _active), Math.Max(_anchor, _active)) : (0, -1);

    private bool IsSelected(int index)
    {
        var (first, last) = SelectedRange;
        return index >= first && index <= last;
    }

    private void SelectAll()
    {
        var count = ViewModel.Rows.Count;
        if (count == 0)
        {
            return;
        }

        _anchor = 0;
        _active = count - 1;
        RefreshSelection();
    }

    private bool ExtendSelection(int step)
    {
        if (!HasSelection)
        {
            return false;
        }

        var next = Math.Clamp(_active + step, 0, ViewModel.Rows.Count - 1);
        if (next == _active)
        {
            return true;
        }

        _active = next;
        RefreshSelection();
        RowsRepeater.GetOrCreateElement(next).StartBringIntoView();
        return true;
    }

    private void OnRowPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_hooked.Add(args.Element))
        {
            args.Element.PointerPressed += OnRowPointerPressed;
            args.Element.PointerEntered += OnRowPointerEntered;
        }

        Paint(args.Element, args.Index);
    }

    private void OnRowIndexChanged(ItemsRepeater sender, ItemsRepeaterElementIndexChangedEventArgs args) =>
        Paint(args.Element, args.NewIndex);

    private void Paint(UIElement element, int index)
    {
        if (element is Grid { Children.Count: > 0 } grid && grid.Children[^1] is Rectangle overlay)
        {
            overlay.Visibility = IsSelected(index) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Repaints the realized rows; the rest pick the selection up when they are prepared.</summary>
    private void RefreshSelection()
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(RowsRepeater);
        for (var i = 0; i < count; i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(RowsRepeater, i) is not UIElement child)
            {
                continue;
            }

            var index = RowsRepeater.GetElementIndex(child);
            if (index >= 0)
            {
                Paint(child, index);
            }
        }
    }

    private void OnRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var index = RowsRepeater.GetElementIndex((UIElement)sender);
        if (index < 0)
        {
            return;
        }

        RowsHost.Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint((UIElement)sender);
        if (point.Properties.IsRightButtonPressed)
        {
            // The menu acts on the selection; a right click outside it selects that row first.
            if (!IsSelected(index))
            {
                _anchor = _active = index;
                RefreshSelection();
            }

            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0
            || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift && _anchor >= 0)
        {
            _active = index;
        }
        else
        {
            _anchor = _active = index;
        }

        _dragging = true;
        RefreshSelection();
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
        {
            _dragging = false;
            return;
        }

        var index = RowsRepeater.GetElementIndex((UIElement)sender);
        if (index >= 0 && index != _active)
        {
            _active = index;
            RefreshSelection();
        }
    }

    // ------------------------------------------------------------------ copying

    private void OnRowsMenuOpening(object? sender, object e)
    {
        CopyLinesItem.IsEnabled = HasSelection;
        CopyPatchItem.IsEnabled = HasSelection;
    }

    private void OnCopyLinesClick(object sender, RoutedEventArgs e) => CopySelection(asPatch: false);

    private void OnCopyPatchClick(object sender, RoutedEventArgs e) => CopySelection(asPatch: true);

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SelectAll();

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } file)
        {
            SetClipboard(Patch(file));
        }
    }

    /// <summary>
    /// Copies the selected rows: as code (the lines' text, hunk headers left out), or as diff
    /// text (headers kept, each line with its +, - or space).
    /// </summary>
    private bool CopySelection(bool asPatch)
    {
        if (!HasSelection)
        {
            return false;
        }

        var rows = ViewModel.Rows;
        var (first, last) = SelectedRange;
        last = Math.Min(last, rows.Count - 1);
        var builder = new StringBuilder();
        for (var i = first; i <= last; i++)
        {
            var row = rows[i];
            if (row.Line is { } line)
            {
                if (asPatch)
                {
                    builder.Append(SignOf(line.Kind));
                }

                builder.Append(line.Text).Append("\r\n");
            }
            else if (asPatch)
            {
                builder.Append(row.Header).Append("\r\n");
            }
        }

        if (builder.Length == 0)
        {
            return false;
        }

        builder.Length -= 2;
        SetClipboard(builder.ToString());
        return true;
    }

    /// <summary>The whole file as a unified diff, with the ---/+++ lines a patch tool expects.</summary>
    private static string Patch(FileDiff file)
    {
        var builder = new StringBuilder();
        builder.Append("--- ").Append(file.IsNew ? "/dev/null" : "a/" + (file.OldPath ?? file.Path)).Append("\r\n");
        builder.Append("+++ ").Append(file.IsDeleted ? "/dev/null" : "b/" + file.Path).Append("\r\n");
        foreach (var hunk in file.Hunks)
        {
            builder.Append(hunk.Header).Append("\r\n");
            foreach (var line in hunk.Lines)
            {
                builder.Append(SignOf(line.Kind)).Append(line.Text).Append("\r\n");
            }
        }

        return builder.ToString();
    }

    private static char SignOf(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => '+',
        DiffLineKind.Removed => '-',
        _ => ' ',
    };

    private static void SetClipboard(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
