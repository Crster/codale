using Codale.App.Controls;
using Codale.App.ViewModels;
using Codale.Core.Helper;
using Codale.Core.Text;

using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

using Windows.System;
using Windows.UI.Core;

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
    public event EventHandler<string>? FindReferencesRequested;

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
            _referencesItem, new MenuFlyoutSeparator(),
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
        _referencesItem.IsEnabled = ReferenceTarget() is { } target && target.Trim().Length > 0;


        var kind = PreviewKind();
        _previewItem.Visibility = kind == PreviewTarget.None ? Visibility.Collapsed : Visibility.Visible;
        _previewSeparator.Visibility = _previewItem.Visibility;
        _previewItem.Text = kind == PreviewTarget.Browser ? "Preview in Browser" : "Preview";
    }

    private string SelectedText()
    {
        var (start, end) = FileEditor.SelectionOffsets;
        return start == end ? string.Empty : FileEditor.GetText()[start..end];
    }

    /// <summary>
    /// What Find References looks for: the selection, or with nothing selected, the word
    /// under the caret - either edge of it, so a caret resting after a name still finds
    /// that name. Null when the caret sits between words with nothing selected.
    /// </summary>
    private string? ReferenceTarget()
    {
        var (start, end) = FileEditor.SelectionOffsets;
        if (start != end)
        {
            return SelectedText();
        }

        var text = FileEditor.GetText();
        bool IsWordChar(int i) =>
            i >= 0 && i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');

        var at = IsWordChar(start) ? start : start - 1;
        if (!IsWordChar(at))
        {
            return null;
        }

        var first = at;
        while (IsWordChar(first - 1))
        {
            first--;
        }

        var last = at + 1;
        while (IsWordChar(last))
        {
            last++;
        }

        return text[first..last];
    }

    private void FindReferences()
    {
        if (ReferenceTarget() is { } target && target.Trim() is { Length: > 0 } word)
        {
            FindReferencesRequested?.Invoke(this, word);
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
        if (ViewModel?.FilePath is not { } path)
        {
            return PreviewTarget.None;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
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
