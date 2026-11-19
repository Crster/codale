using Codale.App.Services;
using Codale.Core.Syntax;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Storage.Pickers;

namespace Codale.App.Controls;

/// <summary>
/// The dialogs behind the status bar's language picker and the Syntax settings: installing from
/// the built-in catalog and importing one from disk.
/// Built in code (they are small and share one shape) rather than as separate XAML files.
/// </summary>
public static class SyntaxDialogs
{
    private static ContentDialog NewDialog(XamlRoot root, string title, UIElement content, string primary, string close = "Close", string? glyph = null) => AppDialog.Apply(new ContentDialog
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary,
    }, DialogTone.Info, glyph);

    private static TextBlock Note(string text = "") => AppDialog.Note(text);

    /// <summary>Lists the catalog's grammars with check boxes and installs the ones ticked.</summary>
    public static async Task ShowCatalogAsync(XamlRoot root)
    {
        var store = SyntaxService.Store;
        var installed = store.Languages.Where(l => !l.IsDependency).Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boxes = new List<(CheckBox Box, CatalogEntry Entry)>();
        var list = new StackPanel { Spacing = 2 };
        foreach (var entry in SyntaxCatalogClient.Entries.Where(e => !e.Hidden).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var already = installed.Contains(entry.Id);
            var box = new CheckBox
            {
                Content = $"{entry.Name}{(entry.Extensions.Length > 0 ? "   (" + string.Join(", ", entry.Extensions.Select(e => "." + e)) + ")" : "")}{(already ? "   installed" : "")}",
                IsEnabled = !already,
                IsChecked = false,
            };
            ToolTipService.SetToolTip(box, $"{entry.Source}\nLicense: {entry.License}\n{entry.LicenseUrl}");
            boxes.Add((box, entry));
            list.Children.Add(box);
        }

        var status = Note();
        var progress = new ProgressBar { IsIndeterminate = false, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Spacing = 10, Width = 420 };
        body.Children.Add(Note("Browse the Shiki TextMate catalog. Install downloads the selected grammars and their embedded language dependencies."));
        var search = new TextBox { PlaceholderText = "Search languages, aliases or extensions" };
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim();
            foreach (var (box, entry) in boxes)
                box.Visibility = new[] { entry.Name, entry.Id }.Concat(entry.Aliases).Concat(entry.Extensions)
                    .Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
        };
        body.Children.Add(search);
        body.Children.Add(new ScrollViewer { MaxHeight = 280, Content = list });
        body.Children.Add(progress);
        body.Children.Add(status);

        var dialog = NewDialog(root, "Download syntax grammars", body, "Install", glyph: AppDialog.Glyphs.Download);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var picked = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Entry).ToList();
            if (picked.Count == 0)
            {
                status.Text = "Tick the grammars to install.";
                args.Cancel = true;
                return;
            }

            // Hold the dialog open while the downloads run, so errors can be shown in it.
            var deferral = args.GetDeferral();
            args.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            progress.IsIndeterminate = true;
            progress.Visibility = Visibility.Visible;
            var client = new SyntaxCatalogClient();
            var failures = new List<string>();
            var done = 0;
            foreach (var entry in picked)
            {
                status.Text = $"Downloading {entry.Name}…";
                try
                {
                    await client.InstallAsync(entry, store);
                    done++;
                    var row = boxes.First(b => b.Entry == entry).Box;
                    row.IsChecked = false;
                    row.IsEnabled = false;
                    row.Content = $"{entry.Name}   installed";
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(ex.Message);
                }
            }

            progress.IsIndeterminate = false;
            progress.Visibility = Visibility.Collapsed;
            status.Text = (done > 0 ? $"Installed {done}. " : "") + string.Join(" ", failures);
            dialog.IsPrimaryButtonEnabled = true;
            deferral.Complete();
        };

        await dialog.ShowAsync();
    }

    public static async Task ImportAsync(XamlRoot root, IntPtr hwnd)
    {
        var picker = new FileOpenPicker();
        PickerOwner.Attach(picker, hwnd);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var entry = SyntaxService.Store.Import(file.Path);
            await AppDialog.AlertAsync(root, "Grammar imported", $"{entry.Name} is installed and colours .{string.Join(", .", entry.Extensions)} files.", DialogTone.Success);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await AppDialog.AlertAsync(root, "Could not import that grammar", ex.Message);
        }
    }

    public static async Task ShowThemesAsync(XamlRoot root)
    {
        var search = new TextBox { PlaceholderText = "Search themes" };
        var list = new StackPanel { Spacing = 2 };
        var rows = new List<(CheckBox Box, ThemeCatalogEntry Entry)>();
        foreach (var entry in SyntaxThemeCatalog.Entries.OrderBy(t => t.Name))
        {
            var installed = File.Exists(SyntaxThemeCatalog.PathFor(SyntaxService.Store, entry.Id));
            var box = new CheckBox { Content = $"{entry.Name} · {entry.Type}{(installed ? " · installed" : "")}", IsEnabled = !installed };
            ToolTipService.SetToolTip(box, $"{entry.Source}\nLicense: {entry.License}\n{entry.LicenseUrl}");
            rows.Add((box, entry));
            list.Children.Add(box);
        }
        search.TextChanged += (_, _) =>
        {
            foreach (var (box, entry) in rows)
                box.Visibility = (entry.Name + " " + entry.Id + " " + entry.Type).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        var status = Note("Download a theme, then choose it in the light or dark editor theme setting.");
        var body = new StackPanel { Spacing = 10, Width = 420 };
        body.Children.Add(search);
        body.Children.Add(new ScrollViewer { MaxHeight = 280, Content = list });
        body.Children.Add(status);
        var dialog = NewDialog(root, "Download syntax themes", body, "Install", glyph: AppDialog.Glyphs.Download);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var selected = rows.Where(r => r.Box.IsChecked == true).ToArray();
            if (selected.Length == 0) { status.Text = "Tick the themes to install."; return; }
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            var failures = new List<string>();
            var done = 0;
            try
            {
                foreach (var (box, entry) in selected)
                {
                    status.Text = $"Downloading {entry.Name}…";
                    try
                    {
                        await SyntaxThemeCatalog.InstallAsync(entry, SyntaxService.Store);
                        box.IsChecked = false;
                        box.IsEnabled = false;
                        box.Content = $"{entry.Name} · {entry.Type} · installed";
                        done++;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                    { failures.Add($"{entry.Name}: {ex.Message}"); }
                }
                status.Text = $"Installed {done}. " + string.Join(" ", failures);
            }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
}
