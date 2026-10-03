using Codale.Core.Helper;
using Codale.Core.Syntax;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Storage.Pickers;

namespace Codale.App.Controls;

/// <summary>
/// The dialogs behind the status bar's language picker and the Syntax settings: installing from
/// the built-in catalog, having the helper model write a grammar, and importing one from disk.
/// Built in code (they are small and share one shape) rather than as separate XAML files.
/// </summary>
public static class SyntaxDialogs
{
    private static ContentDialog NewDialog(XamlRoot root, string title, UIElement content, string primary, string close = "Close") => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary,
    };

    private static TextBlock Note(string text = "") => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
    };

    /// <summary>Lists the catalog's grammars with check boxes and installs the ones ticked.</summary>
    public static async Task ShowCatalogAsync(XamlRoot root)
    {
        var store = SyntaxService.Store;
        var installed = store.Languages.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boxes = new List<(CheckBox Box, CatalogEntry Entry)>();
        var list = new StackPanel { Spacing = 2 };
        foreach (var entry in SyntaxCatalogClient.Entries)
        {
            var already = installed.Contains(entry.Id);
            var box = new CheckBox
            {
                Content = $"{entry.Name}   ({string.Join(", ", entry.Extensions.Select(e => "." + e))}){(already ? "   installed" : "")}",
                IsEnabled = !already,
                IsChecked = false,
            };
            ToolTipService.SetToolTip(box, $"From github.com/{entry.Source}");
            boxes.Add((box, entry));
            list.Children.Add(box);
        }

        var status = Note();
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Spacing = 10, Width = 420 };
        body.Children.Add(Note("Grammars are downloaded from the projects named in each tooltip when you click Install. Nothing is fetched until then."));
        body.Children.Add(new ScrollViewer { MaxHeight = 280, Content = list });
        body.Children.Add(progress);
        body.Children.Add(status);

        var dialog = NewDialog(root, "Download syntax grammars", body, "Install");
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
                catch (InvalidDataException ex)
                {
                    failures.Add(ex.Message);
                }
            }

            progress.Visibility = Visibility.Collapsed;
            status.Text = (done > 0 ? $"Installed {done}. " : "") + string.Join(" ", failures);
            dialog.IsPrimaryButtonEnabled = true;
            deferral.Complete();
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// Asks the helper model for a grammar for one file type, shows what came back, and saves it on
    /// confirmation. <paramref name="sample"/> is the file's text, which the model reads to learn the language.
    /// </summary>
    public static async Task ShowGenerateAsync(XamlRoot root, IHelperModel helper, string suggestedName, string extension, string sample)
    {
        var store = SyntaxService.Store;
        var generator = new SyntaxGenerator(helper);
        var nameBox = new TextBox { Header = "Language name", Text = suggestedName, PlaceholderText = "e.g. Kotlin" };
        var extBox = new TextBox { Header = "File extension", Text = extension.TrimStart('.'), PlaceholderText = "e.g. kt" };
        var status = Note(generator.IsAvailable
            ? "The helper model reads a sample of the open file and writes a grammar. You can review it before it is saved."
            : "No helper model is available. Add a provider in Settings, or install the Claude CLI.");
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var body = new StackPanel { Spacing = 10, Width = 420 };
        body.Children.Add(nameBox);
        body.Children.Add(extBox);

        // From Settings there is no open file to learn from, so a short sample can be pasted instead.
        var sampleBox = sample.Length == 0
            ? new TextBox { Header = "Sample code (optional, helps accuracy)", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 110 }
            : null;
        if (sampleBox is not null)
        {
            body.Children.Add(sampleBox);
        }

        body.Children.Add(progress);
        body.Children.Add(status);

        GeneratedGrammar? generated = null;
        CancellationTokenSource? cts = null;
        var dialog = NewDialog(root, "Generate syntax with AI", body, "Generate", "Cancel");
        dialog.IsPrimaryButtonEnabled = generator.IsAvailable;
        dialog.Closing += (_, _) => cts?.Cancel();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (generated is not null)
            {
                // Second click: save what was previewed.
                store.Add(new LanguageEntry
                {
                    Id = generated.Name,
                    Name = generated.Name,
                    ScopeName = generated.Meta.ScopeName,
                    Extensions = generated.Extensions,
                    FirstLine = generated.Meta.FirstLineMatch,
                    Source = GrammarSource.Generated,
                }, generated.Json);
                return;
            }

            var language = nameBox.Text.Trim();
            var ext = extBox.Text.Trim().TrimStart('.');
            if (language.Length == 0 || ext.Length == 0)
            {
                status.Text = "Give the language a name and a file extension.";
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            args.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            nameBox.IsEnabled = extBox.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            cts = new CancellationTokenSource();
            var shownSample = sampleBox?.Text ?? sample;
            try
            {
                generated = await Task.Run(() => generator.GenerateAsync(language, ext, shownSample, m => dialog.DispatcherQueue.TryEnqueue(() => status.Text = m), cts.Token));
                var rules = generated.Json.Split('\n').Length;
                status.Text = $"Ready: {generated.Name} ({generated.Meta.ScopeName}), colours .{string.Join(", .", generated.Extensions)} · {rules} lines. Save it to use it now.";
                dialog.PrimaryButtonText = "Save";
            }
            catch (OperationCanceledException)
            {
                // The dialog is closing.
            }
            catch (Exception ex) when (ex is InvalidDataException or HelperModelException)
            {
                status.Text = ex.Message;
                nameBox.IsEnabled = extBox.IsEnabled = true;
                dialog.PrimaryButtonText = "Try again";
            }

            progress.Visibility = Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = true;
            deferral.Complete();
        };

        await dialog.ShowAsync();
    }

    /// <summary>Lets the user pick a .tmLanguage.json from disk and installs it.</summary>
    public static async Task ImportAsync(XamlRoot root, IntPtr hwnd)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var entry = SyntaxService.Store.Import(file.Path);
            await Message(root, "Grammar imported", $"{entry.Name} is installed and colours .{string.Join(", .", entry.Extensions)} files.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await Message(root, "Could not import that grammar", ex.Message);
        }
    }

    private static async Task Message(XamlRoot root, string title, string text)
    {
        var dialog = NewDialog(root, title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontWeight = FontWeights.Normal }, "", "OK");
        dialog.PrimaryButtonText = "";
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }
}
