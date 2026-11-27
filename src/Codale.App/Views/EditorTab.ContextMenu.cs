using Codale.Search.Engine;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Views;

/// <summary>
/// The editor's right-click menu: edit commands, references and rendered preview.
/// </summary>
public sealed partial class EditorTab
{
    /// <summary>True when <paramref name="view"/> is this tab's rendered Markdown preview.</summary>
    public bool ContainsPreview(MarkdownView view) => ReferenceEquals(view, MarkdownPreview);

    private MenuFlyoutItem _copyItem = null!;
    private MenuFlyoutItem _pasteItem = null!;
    private MenuFlyoutItem _previewItem = null!;

    private MenuFlyoutItem _referencesItem = null!;

    /// <summary>Raised with the selection, or the word under the cursor, when the user asks for its usages across the project.</summary>
    public event EventHandler<ReferenceQuery>? FindReferencesRequested;

    private bool _previewing;

    public string? ProjectRoot { get; set; }

    /// <summary>The project's knowledge index: related code for ghost text completions.</summary>
    public Codale.Search.ProjectKnowledgeIndex? Knowledge { get; set; }

    private static MenuFlyoutItem Item(string text, Action click, string? accelerator = null, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text, KeyboardAcceleratorTextOverride = accelerator ?? string.Empty };
        if (glyph is not null)
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }
        item.Click += (_, _) => click();
        return item;
    }

    private readonly MenuFlyoutSeparator _previewSeparator = new();

    private void BuildContextMenu()
    {
        _copyItem = Item("Copy", FileEditor.CopyToClipboard, "Ctrl+C", "");
        _pasteItem = Item("Paste", FileEditor.PasteFromClipboard, "Ctrl+V", "");
        _referencesItem = Item("Find References", FindReferences, null, "");
        _previewItem = Item("Preview", TogglePreview, null, "");

        // Clipboard, navigation and preview.
        var menu = new MenuFlyout();
        foreach (var item in new MenuFlyoutItemBase[]
        {
            _copyItem, _pasteItem, new MenuFlyoutSeparator(),
            _referencesItem,
            _previewSeparator,
            _previewItem,
        })
        {
            menu.Items.Add(item);
        }

        menu.Opening += (_, _) => RefreshContextMenu();
        FileEditor.ContextFlyout = menu;

        // The preview can copy the source or return to editing.
        var back = new MenuFlyout();
        back.Items.Add(MarkdownPreview.CreateCopyMarkdownItem());
        back.Items.Add(Item("Edit Markdown", TogglePreview, null, ""));
        MarkdownPreviewHost.ContextFlyout = back;
    }

    private void RefreshContextMenu()
    {
        var editable = ViewModel is { CanEdit: true };
        var hasSelection = FileEditor.HasSelection;

        _copyItem.IsEnabled = hasSelection;
        _pasteItem.IsEnabled = editable;
        _referencesItem.IsEnabled = ReferenceTarget() is not null;


        var kind = PreviewKind();
        _previewItem.Visibility = kind == PreviewTarget.None ? Visibility.Collapsed : Visibility.Visible;
        _previewSeparator.Visibility = _previewItem.Visibility;
        _previewItem.Text = kind == PreviewTarget.Browser ? "Preview in Browser" : "Preview";
    }

    private ReferenceQuery? ReferenceTarget()
    {
        var (start, end) = FileEditor.SelectionOffsets;
        return ReferenceQuery.At(ViewModel?.FilePath, FileEditor.GetText(), start, end);
    }

    private void FindReferences()
    {
        if (ReferenceTarget() is { } target)
        {
            FindReferencesRequested?.Invoke(this, target);
        }
    }

    // ------------------------------------------------------------------ preview

    private enum PreviewTarget
    {
        None,
        Markdown,
        Browser,
    }

    private PreviewTarget PreviewKind()
    {
        return ViewModel?.FilePath is not { } path
            ? PreviewTarget.None
            : Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" or ".markdown" => PreviewTarget.Markdown,
            ".html" or ".htm" => PreviewTarget.Browser,
            _ => PreviewTarget.None,
        };
    }

    private void TogglePreview()
    {
        switch (PreviewKind())
        {
            case PreviewTarget.Markdown:
                _previewing = !_previewing;
                if (_previewing)
                {
                    MarkdownPreview.BasePath = ViewModel.FilePath is { } file ? Path.GetDirectoryName(file) : null;
                    MarkdownPreview.Markdown = FileEditor.GetText();
                }

                MarkdownPreviewHost.Visibility = _previewing ? Visibility.Visible : Visibility.Collapsed;
                FileEditor.Visibility = _previewing ? Visibility.Collapsed : Visibility.Visible;
                if (!_previewing)
                {
                    FileEditor.Focus(FocusState.Programmatic);
                }

                break;

            case PreviewTarget.Browser:
                OpenExternally();
                break;
        }
    }

}
