using Codale.App.Services;
using Codale.Core.Syntax;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Controls;

/// <summary>One line in the language list: a language, auto-detect, or plain text.</summary>
public sealed class LanguageRow
{
    /// <summary>The language id; <see cref="SyntaxSelection.PlainText"/> for plain text; null for auto-detect.</summary>
    public string? Id { get; init; }

    public string Text { get; init; } = "";

    public string Detail { get; init; } = "";

    public bool IsCurrent { get; init; }

    public Visibility CheckVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// The status bar's language picker: a searchable list of every language the editor can colour,
/// with auto-detect and plain text on top. The host adds its own footer actions (download,
/// generate) with <see cref="AddAction"/> so the same list serves the status bar and anywhere else.
/// </summary>
public sealed partial class LanguagePicker : UserControl
{
    private List<LanguageRow> _rows = [];

    public LanguagePicker()
    {
        InitializeComponent();
    }

    /// <summary>Raised with the chosen language id, or null for "Auto-detect".</summary>
    public event Action<string?>? LanguageChosen;

    /// <summary>Adds a button under the list.</summary>
    public void AddAction(string text, Action onClick)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => onClick();
        Actions.Children.Add(button);
    }

    /// <summary>Rebuilds the list for a file: <paramref name="currentId"/> is what colours it now.</summary>
    public void Load(string? currentId, bool hasOverride, string fileName)
    {
        SearchBox.Text = "";
        Caption.Text = hasOverride
            ? $"{fileName} is set by hand. Choose Auto-detect to go back to detection."
            : $"Detected from {fileName}. Pick a language to override it for this file.";

        var rows = new List<LanguageRow>
        {
            new() { Id = null, Text = "Auto-detect", IsCurrent = !hasOverride },
            new() { Id = SyntaxSelection.PlainText, Text = "Plain Text", IsCurrent = hasOverride && currentId is null },
        };

        foreach (var language in SyntaxService.Catalog.All.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new LanguageRow
            {
                Id = language.Id,
                Text = language.Name,
                Detail = language.IsBuiltin ? "" : language.Source.ToString().ToLowerInvariant(),
                IsCurrent = hasOverride && string.Equals(language.Id, currentId, StringComparison.OrdinalIgnoreCase),
            });
        }

        _rows = rows;
        Filter("");
    }

    private void Filter(string query)
    {
        var q = query.Trim();
        List.ItemsSource = q.Length == 0
            ? _rows
            : _rows.Where(r => r.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || (r.Id?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Filter(SearchBox.Text);

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LanguageRow row)
        {
            LanguageChosen?.Invoke(row.Id);
        }
    }
}
