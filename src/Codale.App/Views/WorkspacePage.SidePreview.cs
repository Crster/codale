using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Views;

/// <summary>
/// The side preview: what the chat points at - a file, a README, a plan, a diff - opens in
/// the column beside the conversation, so reading it never takes the chat off screen.
/// "Open in tab" hands the same view (with its scroll position, highlights and any edits)
/// over to the tab strip. The column is shared with the task peek and Find References;
/// one occupant at a time.
/// </summary>
public sealed partial class WorkspacePage
{
    /// <summary>The editor shown in the side preview, while it is a file.</summary>
    private EditorTab? _sideEditor;

    /// <summary>The diff view shown in the side preview: its own view model, so the diff tab is left alone.</summary>
    private DiffTab? _sideDiff;
    private DiffViewerViewModel? _sideDiffViewModel;

    /// <summary>The plan or other artifact shown in the side preview.</summary>
    private ArtifactTab? _sideArtifact;

    /// <summary>What "Open in tab" does for the current occupant.</summary>
    private Action? _sidePreviewOpenInTab;

    private bool IsSidePreviewOpen => SidePreviewPane.Visibility == Visibility.Visible;

    /// <summary>True when <paramref name="node"/> sits inside a chat transcript (a chat or terminal-agent tab).</summary>
    private static bool IsInChat(DependencyObject? node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ChatTab or CliTerminalTab)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Shows a file beside the chat and returns the editor it is in, so a caller can mark lines.</summary>
    private async Task<EditorTab> ShowFileBesideAsync(string path)
    {
        // A file that is already pinned in a tab is edited there; showing a second copy beside would split the edits.
        if (_editorTabs.FirstOrDefault(t => !t.Tab.ViewModel.IsPreview && PathsEqual(t.Tab.ViewModel.FilePath, path)) is { } pinned)
        {
            SelectTab(pinned.Item);
            return pinned.Tab;
        }

        if (_sideEditor is null)
        {
            _sideEditor = NewEditorSurface(isPreview: true);
            _sideEditor.Edited += OnSideEditorEdited;
        }

        var editor = _sideEditor;
        SetSidePreviewContent(editor);
        SetSidePreviewHeader(FileIcons.NodeGlyph(false, Path.GetFileName(path)), Path.GetFileName(path), RelativeFolder(path));
        _sidePreviewOpenInTab = MoveSideEditorToTab;
        ShowSidePreview();
        await editor.OpenFileAsync(path);
        return editor;
    }

    /// <summary>The first keystroke in the preview moves it to a tab, where edits are tracked and saved like any other.</summary>
    private void OnSideEditorEdited(object? sender, EventArgs e)
    {
        var editor = _sideEditor;
        MoveSideEditorToTab();
        DispatcherQueue.TryEnqueue(() => editor?.FocusText());
    }

    private void MoveSideEditorToTab()
    {
        if (_sideEditor is not { } editor)
        {
            return;
        }

        editor.Edited -= OnSideEditorEdited;
        _sideEditor = null;
        SidePreviewHost.Child = null;
        HideSidePreview();

        var entry = AdoptEditorTab(editor, isPreview: false);
        SelectTab(entry.Item);
    }

    /// <summary>An agent's edit beside the chat: the one change it made, or the file's working-tree diff.</summary>
    private async Task ShowDiffBesideAsync(string path, Codale.Git.FileDiff? diff)
    {
        if (_sideDiff is null)
        {
            _sideDiffViewModel = new DiffViewerViewModel(ViewModel.ProjectPath);
            _sideDiff = new DiffTab().WithViewModel(_sideDiffViewModel);
            _sideDiff.FileOpenRequested += (_, relativePath) =>
            {
                if (ResolveGitPath(relativePath) is { } absolute)
                {
                    _ = ShowFileBesideAsync(absolute);
                }
            };
        }

        var absolutePath = ResolveWorkspacePath(path);
        var relative = ToRepoRelative(absolutePath);
        SetSidePreviewContent(_sideDiff);
        SetSidePreviewHeader("", Path.GetFileName(absolutePath), "changes");
        ShowSidePreview();

        if (diff is { Hunks.Count: > 0 })
        {
            _sideDiffViewModel!.LoadDiffs(
                Path.GetFileName(absolutePath),
                relative ?? absolutePath,
                [diff with { Path = relative ?? absolutePath }],
                File.Exists(absolutePath));
            _sidePreviewOpenInTab = () => ShowAgentDiff(path, diff);
        }
        else if (relative is not null)
        {
            var resolved = ResolveGitPath(relative);
            await _sideDiffViewModel!.LoadFileAsync(relative, resolved is not null && File.Exists(resolved));
            _sidePreviewOpenInTab = async () =>
            {
                EnsureDiffTab();
                SelectTab(_diffTabItem);
                var again = ResolveGitPath(relative);
                await ViewModel.DiffViewer.LoadFileAsync(relative, again is not null && File.Exists(again));
            };
        }
    }

    /// <summary>A plan (or other artifact) beside the chat; revisions still go back to the conversation.</summary>
    private void ShowArtifactBeside(SessionArtifact artifact)
    {
        if (_sideArtifact is null)
        {
            _sideArtifact = new ArtifactTab();
            _sideArtifact.RevisionRequested += (plan, message) =>
                _ = ViewModel.Chat.SendPlanRevisionAsync(message, plan);
        }

        _sideArtifact.Show(artifact);
        SetSidePreviewContent(_sideArtifact);
        SetSidePreviewHeader("", artifact.Title, artifact.Kind == SessionArtifactKind.Plan ? "plan" : "");
        _sidePreviewOpenInTab = () => ShowArtifact(artifact);
        ShowSidePreview();
    }

    private void SetSidePreviewContent(UIElement content)
    {
        if (!ReferenceEquals(SidePreviewHost.Child, content))
        {
            ReleaseSideEditorIfHidden(content);
            SidePreviewHost.Child = content;
        }
    }

    /// <summary>A file preview replaced by a diff or plan has nothing left to show; its buffer is closed.</summary>
    private void ReleaseSideEditorIfHidden(UIElement next)
    {
        if (_sideEditor is { } editor && !ReferenceEquals(editor, next) && ReferenceEquals(SidePreviewHost.Child, editor))
        {
            editor.Edited -= OnSideEditorEdited;
            editor.ViewModel.Close();
            editor.CloseHost();
            _sideEditor = null;
        }
    }

    private void SetSidePreviewHeader(string glyph, string title, string detail)
    {
        SidePreviewGlyph.Glyph = glyph;
        SidePreviewTitle.Text = title;
        SidePreviewDetail.Text = detail;
    }

    private string RelativeFolder(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var root = ViewModel.ProjectPath;
        if (folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            folder = folder[root.Length..].TrimStart('\\', '/');
        }

        return folder.Replace('\\', '/');
    }

    /// <summary>Takes the side column for the preview: the peek and references step aside.</summary>
    private void ShowSidePreview()
    {
        if (PeekPane.Visibility == Visibility.Visible)
        {
            PeekPane.Detach();
            PeekPane.Visibility = Visibility.Collapsed;
        }

        ReferencesPane.Visibility = Visibility.Collapsed;
        SidePreviewPane.Visibility = Visibility.Visible;
        OpenSideColumn();
    }

    /// <summary>Hides the preview without touching its content (the column closes if nothing else is in it).</summary>
    private void HideSidePreview()
    {
        SidePreviewPane.Visibility = Visibility.Collapsed;
        if (PeekPane.Visibility != Visibility.Visible && ReferencesPane.Visibility != Visibility.Visible)
        {
            CloseSideColumn();
        }
    }

    /// <summary>Closes the preview and lets go of what it showed.</summary>
    private void CloseSidePreview()
    {
        if (_sideEditor is { } editor)
        {
            editor.Edited -= OnSideEditorEdited;
            editor.ViewModel.Close();
            editor.CloseHost();
            _sideEditor = null;
        }

        SidePreviewHost.Child = null;
        _sidePreviewOpenInTab = null;
        HideSidePreview();
    }

    /// <summary>Another occupant takes the column: the preview steps aside but keeps the column open.</summary>
    private void YieldSidePreview()
    {
        if (!IsSidePreviewOpen)
        {
            return;
        }

        if (_sideEditor is { } editor)
        {
            editor.Edited -= OnSideEditorEdited;
            editor.ViewModel.Close();
            editor.CloseHost();
            _sideEditor = null;
        }

        SidePreviewHost.Child = null;
        _sidePreviewOpenInTab = null;
        SidePreviewPane.Visibility = Visibility.Collapsed;
    }

    private void OnSidePreviewOpenInTabClick(object sender, RoutedEventArgs e)
    {
        var open = _sidePreviewOpenInTab;
        if (open is null)
        {
            return;
        }

        // The file editor moves itself; diffs and plans re-open in their tab, so the preview closes after.
        if (_sideEditor is not null && ReferenceEquals(SidePreviewHost.Child, _sideEditor))
        {
            open();
            return;
        }

        CloseSidePreview();
        open();
    }

    private void OnSidePreviewCloseClick(object sender, RoutedEventArgs e) => CloseSidePreview();

    /// <summary>The peek's "Open in tab": the task's live output, full width in a tab of its own.</summary>
    private void OnPeekOpenInTabRequested(object? sender, RunningTaskItem task)
    {
        SetPeek(null);

        var pane = new TaskPeekPane { CanOpenInTab = false };
        var item = new TabViewItem
        {
            IsClosable = true,
            IconSource = new FontIconSource { Glyph = "" },
            Header = task.DisplayTitle is { Length: > 32 } long_ ? long_[..32] + "…" : task.DisplayTitle,
            Content = pane,
        };

        pane.CloseRequested += (_, _) =>
        {
            pane.Detach();
            RemoveTab(item);
            UpdateEmptyState();
        };
        pane.StopRequested += (s, t) => OnPeekStopRequested(s, t);
        pane.Unloaded += (_, _) =>
        {
            if (GroupOf(item) is null)
            {
                pane.Detach();
            }
        };

        AddTab(item);
        UpdateEmptyState();
        SelectTab(item);
        pane.Show(task, ViewModel.ProjectPath, ViewModel.ExtraFolders.Folders);
    }
}
