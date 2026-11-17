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
        var resources = Application.Current.Resources;
        var iconStyle = (Style)resources["ShellIconButton"];

        // One compact row on top \u2014 pick a command, start a new one, delete the current one \u2014
        // instead of a sidebar that sits mostly empty when a project has only a few commands.
        var list = new ComboBox
        {
            PlaceholderText = "New command",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 260,
        };

        var newButton = new Button
        {
            Style = iconStyle,
            Width = 32,
            Height = 32,
            Content = new FontIcon { Glyph = "\uE710", FontSize = 13 },
        };
        ToolTipService.SetToolTip(newButton, "New command");

        var deleteLabel = new TextBlock { Text = "Delete", FontSize = 12, Visibility = Visibility.Collapsed };
        var deleteButton = new Button
        {
            Style = iconStyle,
            Width = double.NaN,
            MinWidth = 32,
            Height = 32,
            Padding = new Thickness(9, 0, 9, 0),
            Visibility = Visibility.Collapsed,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new FontIcon { Glyph = "\uE74D", FontSize = 13 }, deleteLabel },
            },
        };
        ToolTipService.SetToolTip(deleteButton, "Delete command");

        var picker = new Grid { ColumnSpacing = 4 };
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(newButton, 1);
        Grid.SetColumn(deleteButton, 2);
        picker.Children.Add(list);
        picker.Children.Add(newButton);
        picker.Children.Add(deleteButton);

        var name = new TextBox
        {
            Header = "Name",
            PlaceholderText = "dev server",
        };

        var body = new TextBox
        {
            // The header keeps the UI font; only the script itself is monospace.
            Header = new TextBlock
            {
                Text = "Script",
                FontFamily = (FontFamily)resources["ContentControlThemeFontFamily"],
            },
            PlaceholderText = "npm run dev",
            AcceptsReturn = true,
            IsSpellCheckEnabled = false,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            MinHeight = 96,
            MaxHeight = 220,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(body, ScrollBarVisibility.Auto);

        var error = new TextBlock
        {
            Foreground = (Brush)resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel
        {
            Width = 440,
            Spacing = 12,
            Children = { picker, name, body, error },
        };

        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Project commands",
            Content = content,
            PrimaryButtonText = "Save",
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
                deleteLabel.Visibility = Visibility.Collapsed;
                deleteButton.ClearValue(Control.ForegroundProperty);
            }
        }

        void RefreshList(string? select)
        {
            loading = true;
            list.Items.Clear();
            var commands = ViewModel.SavedCommands();
            foreach (var command in commands)
            {
                list.Items.Add(new ComboBoxItem { Content = command.Name, Tag = command });
            }

            if (select is not null && list.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                    item.Tag is ProjectCommand saved && string.Equals(saved.Name, select, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                list.SelectedItem = match;
            }

            loading = false;
            list.IsEnabled = list.Items.Count > 0;
            deleteButton.Visibility = originalName is not null ? Visibility.Visible : Visibility.Collapsed;
        }

        void Load(ProjectCommand? command)
        {
            loading = true;
            originalName = command?.Name;
            name.Text = command?.Name ?? "";
            body.Text = command?.Script ?? command?.Command ?? "";
            deleteButton.Visibility = originalName is not null ? Visibility.Visible : Visibility.Collapsed;
            loading = false;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (loading || list.SelectedItem is not ComboBoxItem item)
                return;

            DisarmDelete();
            ClearError();
            Load(item.Tag as ProjectCommand);
        };

        newButton.Click += (_, _) =>
        {
            DisarmDelete();
            ClearError();
            loading = true;
            list.SelectedItem = null;
            loading = false;
            Load(null);
            name.Focus(FocusState.Programmatic);
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
                body.Focus(FocusState.Programmatic);
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            if (edit.Name.Length == 0)
            {
                error.Text = "Give the command a name.";
                name.Focus(FocusState.Programmatic);
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

        deleteButton.Click += (_, _) =>
        {
            if (originalName is null)
                return;

            if (!deleteArmed)
            {
                deleteArmed = true;
                deleteLabel.Text = "Confirm";
                deleteLabel.Visibility = Visibility.Visible;
                deleteButton.Foreground = (Brush)resources["SystemFillColorCriticalBrush"];
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
                return;
            }

            DisarmDelete();
            Load(null);
            RefreshList(null);
            if (list.Items.FirstOrDefault() is ComboBoxItem first)
                list.SelectedItem = first;
            else
                name.Focus(FocusState.Programmatic);
        };

        RefreshList(null);
        if (list.Items.FirstOrDefault() is ComboBoxItem first)
            list.SelectedItem = first;
        else
            Load(null);

        await dialog.ShowAsync();
    }

}
