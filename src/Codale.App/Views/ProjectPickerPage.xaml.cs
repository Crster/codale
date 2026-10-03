using Codale.App.Services;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Storage.Pickers;

namespace Codale.App.Views;

public sealed partial class ProjectPickerPage : Page
{
    public ProjectPickerPage()
    {
        InitializeComponent();

        LoadRecents();
    }

    private void LoadRecents()
    {
        var recents = RecentProjects.Load();
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
            if (App.Current.MainWindowHandle is { } hwnd)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            // Open in this window: the welcome screen is replaced by the workspace
            // rather than a second window spawning while the picker stays behind.
            App.Current.OpenProjectInPlace(folder.Path);
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
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear all recent projects?",
            Content = "The list on this page will be emptied. This cannot be undone.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        RecentProjects.Clear();
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

        App.Current.OpenProjectInPlace(recent.Path);
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
