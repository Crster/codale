using Codale.App.Controls;
using Codale.App.Services;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Storage.Pickers;

namespace Codale.App.Views;

public sealed partial class ProjectPickerPage : Page
{
    private MainWindow _window = null!;

    public ProjectPickerPage()
    {
        InitializeComponent();

    }

    private RecentProjects _recents = null!;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var navigation = (ProjectPickerNavigation)e.Parameter;
        _recents = navigation.Recents;
        _window = navigation.Window;
        LoadRecents();
    }

    private void LoadRecents()
    {
        var recents = _recents.Load();
        RecentList.ItemsSource = recents;
        var hasRecents = recents.Count > 0;
        RecentList.Visibility = hasRecents ? Visibility.Visible : Visibility.Collapsed;
        RecentHeader.Visibility = hasRecents ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        BrowseButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");

            // A packaged picker needs to be told which window owns it.
            PickerOwner.Attach(picker, _window.WindowHandle);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            // Open in this window: the welcome screen is replaced by the workspace
            // rather than a second window spawning while the picker stays behind.
            _window.OpenProject(folder.Path);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            BrowseButton.IsEnabled = true;
        }
    }

    private async void OnClearRecentsClick(object sender, RoutedEventArgs e)
    {
        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear all recent projects?",
            Content = "The list on this page will be emptied. This cannot be undone.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }, DialogTone.Danger, AppDialog.Glyphs.Clear);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _recents.Clear();
        LoadRecents();
    }

    private void OnRecentClick(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentProject recent)
        {
            return;
        }

        RecentList.SelectedItem = null;

        if (!Directory.Exists(recent.Path))
        {
            Fail($"That folder no longer exists: {recent.Path}");
            return;
        }

        _window.OpenProject(recent.Path);
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}