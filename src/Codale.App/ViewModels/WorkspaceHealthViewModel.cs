using Codale.App.Services;
using Codale.Search;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// The status bar's right side for one workspace: the indexer (what the project index holds, or whether it is
/// scanning) and the background worker (its provider and how many jobs are running now).
/// </summary>
public sealed partial class WorkspaceHealthViewModel : ObservableObject, IDisposable
{
    private readonly SourceIndex _index;
    private readonly CustomUsageTally _usage;
    private readonly SynchronizationContext? _ui;

    public WorkspaceHealthViewModel(string projectPath, CustomUsageTally usage)
    {
        _usage = usage;
        _ui = SynchronizationContext.Current;
        _index = SourceIndex.For(projectPath);

        _index.StateChanged += OnIndexStateChanged;
        BackgroundWorkers.Changed += OnWorkerChanged;
        _usage.Changed += OnWorkerChanged;
        AppSettings.Changed += OnSettingsChanged;

        _index.Warm();
        Refresh();
    }

    [ObservableProperty]
    public partial string IndexText { get; set; } = "";

    [ObservableProperty]
    public partial string IndexTooltip { get; set; } = "";

    [ObservableProperty]
    public partial string WorkerText { get; set; } = "";

    [ObservableProperty]
    public partial string WorkerTooltip { get; set; } = "";

    private void OnIndexStateChanged() => OnUi(Refresh);

    private void OnWorkerChanged() => OnUi(Refresh);

    private void OnSettingsChanged(object? sender, EventArgs e) => OnUi(Refresh);

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

        if (_index.IsScanning)
        {
            IndexText = "Indexing…";
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

        var provider = AppSettings.BackgroundProvider == AppSettings.OpenRouterBackgroundProvider ? "OpenRouter" : "Off";
        var running = BackgroundWorkers.Running;
        WorkerText = provider == "Off" ? "Worker off" : running > 0 ? $"Worker · {running} running" : "Worker idle";
        WorkerTooltip = $"Background tasks ({provider}): commit messages, titles, routing, search and ghost text · " +
                        $"{_usage.Calls:N0} {(_usage.Calls == 1 ? "call" : "calls")} · ↑{_usage.InputTokens:N0} ↓{_usage.OutputTokens:N0}";
    }

    public void Dispose()
    {
        _index.StateChanged -= OnIndexStateChanged;
        BackgroundWorkers.Changed -= OnWorkerChanged;
        _usage.Changed -= OnWorkerChanged;
        AppSettings.Changed -= OnSettingsChanged;
    }
}
