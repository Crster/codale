using System.Collections.ObjectModel;

using Codale.App.Controls;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Codale.App.Views;

/// <summary>A row of the Session tab's Memory &amp; Todos group: a saved note or user-defined todo.</summary>
public sealed class KnowledgeRow(bool isTodo, string id, string title, string detail, string tooltip)
{
    public bool IsTodo { get; } = isTodo;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string Tooltip { get; } = tooltip;
    public string Glyph => IsTodo ? "" : "";
    public string DeleteText => "Forget memory";
    public Visibility ForgetVisibility => IsTodo ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// The Session tab: the conversation history, and below it what the agent keeps for the workspace -
/// memory notes and unfinished user todos. Read from the workspace database each time the tab shows.
/// </summary>
public sealed partial class WorkspacePage
{
    public ObservableCollection<KnowledgeRow> KnowledgeRows { get; } = [];

    private int _knowledgeLoad;

    private void OnSessionSectionClick(object sender, RoutedEventArgs e)
    {
        static GridLength Height(ToggleButton section) =>
            section.IsChecked == true ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

        SessionHistoryRow.Height = Height(SessionHistorySection);
        SessionKnowledgeRow.Height = Height(SessionKnowledgeSection);
    }

    private void OnRefreshKnowledgeClick(object sender, RoutedEventArgs e) => RefreshKnowledge();

    private async void OnAddTodoClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.ProjectPath is not { Length: > 0 }) return;
        var input = new TextBox { Header = "Todo", PlaceholderText = "What should the agent verify or fix?", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 320 };
        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Add todo",
            Content = new StackPanel { Spacing = 12, Children = { input, error,
                new TextBlock { Text = "The agent will verify and complete this todo.", TextWrapping = TextWrapping.Wrap } } },
            PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        });
        var data = ViewModel.Data;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try { var text = input.Text; await Task.Run(() => data.UserTodos.Add(text)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { error.Text = ex.Message; error.Visibility = Visibility.Visible; args.Cancel = true; }
            finally { deferral.Complete(); }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) RefreshKnowledge();
    }

    private async void RefreshKnowledge()
    {
        if (ViewModel?.ProjectPath is not { Length: > 0 } project)
        {
            return;
        }

        var load = ++_knowledgeLoad;
        var data = ViewModel.Data;
        List<KnowledgeRow> rows;
        try
        {
            rows = await Task.Run(() =>
            {
                var list = new List<KnowledgeRow>();
                foreach (var note in data.Memory.List())
                {
                    var tags = note.Tags.Count > 0 ? $"\n{string.Join(", ", note.Tags)}" : "";
                    list.Add(new KnowledgeRow(false, note.Id, note.Title, note.Text.ReplaceLineEndings(" "),
                        $"{note.Title}\n\n{note.Text}{tags}"));
                }

                foreach (var todo in data.UserTodos.List())
                {
                    list.Add(new KnowledgeRow(true, todo.Id, todo.Text.ReplaceLineEndings(" "), "Todo · awaiting agent verification", todo.Text));
                }

                return list;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The database is busy in another process (an agent saving a note): keep what is shown.
            return;
        }

        if (load != _knowledgeLoad)
        {
            return;
        }

        KnowledgeRows.Clear();
        foreach (var row in rows)
        {
            KnowledgeRows.Add(row);
        }

        KnowledgeHint.Visibility = KnowledgeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnForgetKnowledgeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: KnowledgeRow row } || ViewModel?.ProjectPath is not { Length: > 0 } project)
        {
            return;
        }

        if (row.IsTodo) return;
        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Forget \"{row.Title}\"?",
            Content = "The agent will no longer recall this note in later conversations.",
            PrimaryButtonText = "Forget", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        }, DialogTone.Danger, AppDialog.Glyphs.Delete);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var data = ViewModel.Data;
        try
        {
            await Task.Run(() => data.Memory.Forget(row.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await AppDialog.AlertAsync(XamlRoot, "Could not delete", ex.Message);
            return;
        }

        RefreshKnowledge();
    }
}