using System.Collections.ObjectModel;

using Codale.App.Controls;
using Codale.App.Services;
using Codale.Workspace;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Codale.App.Views;

/// <summary>A row of the Session tab's Memory &amp; Secrets group: a saved note, or a secret's name (never its value).</summary>
public sealed class KnowledgeRow(bool isSecret, string id, string title, string detail, string tooltip)
{
    public bool IsSecret { get; } = isSecret;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string Tooltip { get; } = tooltip;
    public string Glyph => IsSecret ? "" : "";
    public string DeleteText => IsSecret ? "Delete secret" : "Forget memory";
}

/// <summary>
/// The Session tab: the conversation history, and below it what the agent keeps for the workspace -
/// memory notes and the names of stored secrets. Read from the workspace database each time the tab shows.
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

    /// <summary>Stores a new secret (or replaces one by the same name) for the agent to read with env_get.</summary>
    private async void OnAddSecretClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.ProjectPath is not { Length: > 0 } project)
        {
            return;
        }

        var name = new TextBox { Header = "Name", PlaceholderText = "API_KEY" };
        var value = new PasswordBox { Header = "Value" };
        var error = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add secret",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 320,
                Children =
                {
                    name,
                    value,
                    error,
                    new TextBlock
                    {
                        FontSize = 12,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                        Text = "Encrypted for your Windows account. A secret with the same name is replaced.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });

        var directory = WorkspaceDb.DirectoryFor(project);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var key = name.Text.Trim();
                var secret = value.Password;
                if (secret.Length == 0)
                {
                    throw new ArgumentException("Enter the secret's value.");
                }

                await Task.Run(() => new EnvStore(directory).Set(key, secret));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            RefreshKnowledge();
        }
    }

    private async void RefreshKnowledge()
    {
        if (ViewModel?.ProjectPath is not { Length: > 0 } project)
        {
            return;
        }

        var load = ++_knowledgeLoad;
        var directory = WorkspaceDb.DirectoryFor(project);
        List<KnowledgeRow> rows;
        try
        {
            rows = await Task.Run(() =>
            {
                var list = new List<KnowledgeRow>();
                foreach (var note in new MemoryStore(directory).List())
                {
                    var tags = note.Tags.Count > 0 ? $"\n{string.Join(", ", note.Tags)}" : "";
                    list.Add(new KnowledgeRow(false, note.Id, note.Title, note.Text.ReplaceLineEndings(" "),
                        $"{note.Title}\n\n{note.Text}{tags}"));
                }

                foreach (var name in new EnvStore(directory).Names())
                {
                    list.Add(new KnowledgeRow(true, name, name, "Secret · ••••••••",
                        $"{name}\nThe value is encrypted and only given to the agent on request."));
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

        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = row.IsSecret ? $"Delete secret \"{row.Title}\"?" : $"Forget \"{row.Title}\"?",
            Content = row.IsSecret
                ? "The stored value is removed for good; the agent can no longer read it."
                : "The agent will no longer recall this note in later conversations.",
            PrimaryButtonText = row.IsSecret ? "Delete" : "Forget",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }, DialogTone.Danger, AppDialog.Glyphs.Delete);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var directory = WorkspaceDb.DirectoryFor(project);
        try
        {
            await Task.Run(() => row.IsSecret ? new EnvStore(directory).Remove(row.Id) : new MemoryStore(directory).Forget(row.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await AppDialog.AlertAsync(XamlRoot, "Could not delete", ex.Message);
            return;
        }

        RefreshKnowledge();
    }
}
