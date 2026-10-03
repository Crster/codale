using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Views;

/// <summary>
/// A diff in the centre area: one uncommitted file, or every file a commit changed.
/// There is one tab for all diffs - each request replaces its contents, the way the
/// preview editor tab works for files.
/// </summary>
public sealed partial class DiffTab : UserControl
{
    public DiffTab()
    {
        InitializeComponent();
    }

    public DiffViewerViewModel ViewModel { get; private set; } = null!;

    public DiffTab WithViewModel(DiffViewerViewModel viewModel)
    {
        ViewModel = viewModel;
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
}
