using Codale.App.Controls;
using Codale.Commands;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Views;

/// <summary>
/// The commands editor: a name, a command or script (shell auto-detected), and Save.
/// </summary>
public sealed partial class WorkspacePage
{
    private async Task ShowCommandEditorAsync()
    {
        var list = new ListView
        {
            MaxHeight = 140,
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);

        var name = new TextBox { Header = "Name", PlaceholderText = "dev server" };
        var body = new TextBox
        {
            Header = "Script",
            PlaceholderText = "npm run dev",
            AcceptsReturn = true,
            IsSpellCheckEnabled = false,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Height = 140,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(body, ScrollBarVisibility.Auto);

        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel
        {
            Width = 420,
            Spacing = 10,
            Children = { list, name, body, error },
        };

        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Project commands",
            Content = content,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Delete",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        }, DialogTone.Info, AppDialog.Glyphs.Terminal);

        // The entry being edited, so a rename replaces it instead of leaving a duplicate behind.
        string? originalName = null;
        var loading = false;
        var deleteArmed = false;

        void ClearError()
        {
            error.Visibility = Visibility.Collapsed;
            error.Text = "";
        }

        void DisarmDelete()
        {
            if (deleteArmed)
            {
                deleteArmed = false;
                dialog.SecondaryButtonText = "Delete";
            }
        }

        void RefreshList(string? select)
        {
            loading = true;
            list.Items.Clear();
            var commands = ViewModel.SavedCommands();
            foreach (var command in commands)
            {
                var title = new TextBlock { Text = command.Name, TextTrimming = TextTrimming.CharacterEllipsis };
                var detail = new TextBlock
                {
                    Text = Detail(command),
                    FontSize = 12,
                    Opacity = 0.65,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var row = new StackPanel { Spacing = 2, Children = { title, detail } };
                list.Items.Add(new ListViewItem { Content = row, Tag = command, Padding = new Thickness(10, 6, 10, 6) });
            }

            list.Visibility = commands.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            if (select is not null && list.Items.OfType<ListViewItem>().FirstOrDefault(item =>
                    item.Tag is ProjectCommand saved && string.Equals(saved.Name, select, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                list.SelectedItem = match;
            }

            loading = false;
            dialog.IsSecondaryButtonEnabled = originalName is not null;
        }

        void Load(ProjectCommand? command)
        {
            loading = true;
            originalName = command?.Name;
            name.Text = command?.Name ?? "";
            body.Text = command?.Script ?? command?.Command ?? "";
            dialog.IsSecondaryButtonEnabled = originalName is not null;
            loading = false;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (!loading && list.SelectedItem is ListViewItem { Tag: ProjectCommand command })
            {
                DisarmDelete();
                ClearError();
                Load(command);
            }
        };

        name.TextChanged += (_, _) => DisarmDelete();
        body.TextChanged += (_, _) => DisarmDelete();

        dialog.PrimaryButtonClick += (_, args) =>
        {
            DisarmDelete();
            ClearError();
            var detectedKind = ProjectCommandService.DetectScriptKind(body.Text);
            var edit = new ProjectCommandEdit(name.Text, body.Text, null, detectedKind).Normalize();
            if (edit.Command.Length == 0)
            {
                error.Text = "Enter the command line or script to run.";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            if (edit.Name.Length == 0)
            {
                error.Text = "Give the command a name.";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            try
            {
                var saved = ViewModel.SaveCommand(originalName, edit);
                originalName = saved.Name;
                RefreshList(saved.Name);
                Load(saved);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        dialog.SecondaryButtonClick += (_, args) =>
        {
            if (originalName is null)
            {
                args.Cancel = true;
                return;
            }

            if (!deleteArmed)
            {
                deleteArmed = true;
                args.Cancel = true;
                dialog.SecondaryButtonText = "Delete now";
                return;
            }

            try
            {
                ViewModel.RemoveCommand(originalName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            DisarmDelete();
            Load(null);
            RefreshList(null);
            args.Cancel = true;
        };

        RefreshList(null);
        Load(null);

        await dialog.ShowAsync();
    }

    /// <summary>The one-line summary under a saved command's name in the editor's list.</summary>
    private static string Detail(ProjectCommand command)
    {
        var firstLine = (command.Script ?? command.Command).Split('\n')[0].Trim();
        return firstLine;
    }
}
