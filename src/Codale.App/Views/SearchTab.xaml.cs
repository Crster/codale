using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Codale.App.Views;

/// <summary>
/// Shows the result of a title bar search: the discovered files and the code in them
/// that matched, highlighted. There is no input here - the title bar box is the one
/// place a search starts. Clicking a file raises <see cref="FileOpenRequested"/> with
/// the spans to highlight; the workspace page routes it to the editor tab.
/// </summary>
public sealed partial class SearchTab : UserControl
{
    /// <summary>The workspace opens the file in the editor tab and highlights the spans.</summary>
    public event EventHandler<SearchOpenRequest>? FileOpenRequested;

    private SearchViewModel? _viewModel;

    public SearchTab() => InitializeComponent();

    public SearchViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }

    private void OnFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SearchFileItem file })
        {
            FileOpenRequested?.Invoke(this, file.OpenRequest());
        }
    }

    /// <summary>
    /// The selectable code text handles double-tap itself (word selection), so the event
    /// never bubbles to the border unless we listen for already-handled events too.
    /// </summary>
    private void OnRangeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement element)
        {
            element.RemoveHandler(DoubleTappedEvent, new DoubleTappedEventHandler(OnRangeDoubleTapped));
            element.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(OnRangeDoubleTapped), true);
        }
    }

    /// <summary>A double-click on one slice opens the file at that slice, not the first.</summary>
    private void OnRangeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SearchRangeItem range } &&
            ViewModel.Files.FirstOrDefault(f => f.Ranges.Contains(range)) is { } file)
        {
            e.Handled = true;
            FileOpenRequested?.Invoke(this, file.OpenRequest(range));
        }
    }
}
