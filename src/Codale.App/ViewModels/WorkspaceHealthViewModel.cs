using Codale.App.Services;
using Codale.Search;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// The status bar's project index state for one workspace.
/// </summary>
public sealed partial class WorkspaceHealthViewModel : ObservableObject, IDisposable
{
    private readonly SourceIndex _index;
    private readonly SynchronizationContext? _ui;

    private readonly ProjectKnowledgeIndex? _knowledge;
    private (int Done, int Total) _summary;

    public WorkspaceHealthViewModel(string projectPath, ProjectKnowledgeIndex? knowledge = null)
    {
        _ui = SynchronizationContext.Current;
        _index = SourceIndex.For(projectPath);
        _knowledge = knowledge;

        _index.StateChanged += OnIndexStateChanged;
        if (_knowledge is not null)
        {
            _knowledge.IndexingChanged += OnIndexStateChanged;
            _knowledge.SummaryProgressChanged += OnSummaryProgress;
        }

        _index.Warm();
        Refresh();
    }

    [ObservableProperty]
    public partial string IndexText { get; set; } = "";

    [ObservableProperty]
    public partial string IndexTooltip { get; set; } = "";

    private void OnIndexStateChanged() => OnUi(Refresh);

    private void OnSummaryProgress(int done, int total)
    {
        _summary = (done, total);
        OnUi(Refresh);
    }

    /// <summary>Scans and jobs report from background threads; the text is set on the UI thread.</summary>
    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    private void Refresh()
    {
        var snapshot = _index.Current;
        var model = _index.Model;

        var (done, total) = _summary;
        if (_index.IsScanning || _knowledge?.IsIndexing == true)
        {
            IndexText = "Indexing…";
        }
        else if (total > 0 && done < total)
        {
            IndexText = $"Summarizing {Math.Min(done + 1, total)}/{total}";
        }
        else if (snapshot is null)
        {
            IndexText = "Index not built";
        }
        else
        {
            IndexText = $"{snapshot.Files.Count:N0} files indexed";
        }

        IndexTooltip = snapshot is null
            ? "The project index is built when the workspace opens."
            : $"Listed {snapshot.AllFiles.Count:N0} files · read {snapshot.FilesRead:N0} in the last scan · " +
              $"model holds {model.FileCount:N0} files{(model.IsDirty ? " · saving" : "")} · built {snapshot.BuiltAt:t}";


    }

    public void Dispose()
    {
        _index.StateChanged -= OnIndexStateChanged;
        if (_knowledge is not null)
        {
            _knowledge.IndexingChanged -= OnIndexStateChanged;
            _knowledge.SummaryProgressChanged -= OnSummaryProgress;
        }
    }
}
