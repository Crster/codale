using System.Collections.ObjectModel;
using System.Text;
using Codale.App.Services;
using Codale.Smart;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace Codale.App.ViewModels;

/// <summary>One plain row of the log: what was called, on what, and how long it took.</summary>
public sealed partial class SmartActivityItem(string id) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] public partial string Verb { get; set; } = "";
    [ObservableProperty] public partial string Target { get; set; } = "";
    [ObservableProperty] public partial string Meta { get; set; } = "";
    [ObservableProperty] public partial Microsoft.UI.Xaml.Media.Brush KindBrush { get; set; } = Palette("KindOtherBrush");
    public static Microsoft.UI.Xaml.Media.Brush Palette(string key) => (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];
    public bool IsTask { get; set; }
    public Microsoft.UI.Xaml.Thickness Indent => IsTask ? new(0, 6, 0, 0) : new(12, 0, 0, 0);
}
/// <summary>Project-scoped Smart activity in the status bar, including background work.</summary>
public sealed partial class SmartActivityViewModel : ObservableObject, IDisposable
{
    private const int MaxToolRows = 5;
    private readonly string _project;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly SmartExperience _smart = SmartExperienceHost.Instance;
    private int _refreshing, _dirty;
    private bool _disposed;
    private (int Done, int Total) _summary;
    private Codale.Search.ProjectKnowledgeIndex? _knowledge;
    public void ObserveIndex(Codale.Search.ProjectKnowledgeIndex knowledge)
    {
        _knowledge = knowledge;
        knowledge.IndexingChanged += OnIndexingChanged;
        knowledge.SummaryProgressChanged += SetSummaryProgress;
        Refresh();
    }
    private void OnIndexingChanged() => Refresh();
    /// <summary>Progress of the background file summaries; called from any thread.</summary>
    public void SetSummaryProgress(int done, int total) { _summary = (done, total); Refresh(); }
    public SmartActivityViewModel(string project)
    {
        _project = project;
        _smart.Changed += OnChanged;
        _smart.ActivityChanged += OnChanged;
        Refresh();
    }
    [ObservableProperty] public partial string Text { get; set; } = "Smart off";
    [ObservableProperty] public partial string Title { get; set; } = "";
    public ObservableCollection<SmartActivityItem> Runs { get; } = [];
    private void OnChanged(object? sender, EventArgs e) => Refresh();
    // The latest prompt: routing and discovery run together, so show them as one. Anything else is a single task.
    private static IReadOnlyList<SmartActivity> LatestPrompt(IReadOnlyList<SmartActivity> all)
    {
        if (all.Count == 0) return [];
        var newest = all.OrderByDescending(a => a.StartedAt).First();
        if (newest.Context.Task is not ("routing" or "context")) return [newest];
        var routing = all.Where(a => a.Context.Task == "routing").OrderByDescending(a => a.StartedAt).FirstOrDefault();
        var from = routing?.StartedAt ?? newest.StartedAt;
        return all.Where(a => a.Context.Task is "routing" or "context" && a.StartedAt >= from).OrderBy(a => a.StartedAt).ToList();
    }
    private static string Short(string text, int max) { text = text.ReplaceLineEndings(" "); return text.Length <= max ? text : text[..max] + "…"; }
    private static string Seconds(long ms) =>
        ms < 100 ? "" : ms < 1000 ? $"{ms}ms" : ms < 60_000 ? $"{ms / 1000.0:0.#}s" : $"{ms / 60_000}m {ms / 1000 % 60}s";
    private static string TaskLabel(string task) => task switch
    {
        "routing" => "Choosing the best agent",
        "context" => "Finding relevant code",
        "summary" => "Summarizing files",
        "" => "Task",
        _ => char.ToUpperInvariant(task[0]) + task[1..]
    };
    private static string ToolLabel(string tool) => tool switch
    {
        "read_index" => "Looked at a code match",
        "read_file" => "Read",
        "list_directory" => "Browsed",
        "search_files" => "Searched for",
        "run_shell" => "Ran",
        "parse" => "Couldn't read model reply",
        _ => tool.Replace('_', ' ')
    };
    private static void FillTask(SmartActivityItem i, SmartActivity run)
    {
        i.Verb = TaskLabel(run.Context.Task);
        i.Target = run.State switch
        {
            "completed" => "",
            "cancelled" => "Stopped",
            "running" => "In progress…",
            "error" => run.Error is { Length: > 0 } why ? "Failed – " + Short(why, 70) : "Failed",
            _ => run.State
        };
        i.Meta = Seconds(run.ElapsedMilliseconds); i.KindBrush = SmartActivityItem.Palette("KindAgentBrush");
    }
    private static void FillTool(SmartActivityItem i, SmartToolActivity tool)
    {
        string? Arg(string name) { try { using var d = System.Text.Json.JsonDocument.Parse(tool.Arguments); return d.RootElement.TryGetProperty(name, out var v) ? v.ToString() : null; } catch { return null; } }
        i.Verb = ToolLabel(tool.Tool);
        i.KindBrush = SmartActivityItem.Palette(tool.Tool switch { "read_file" => "KindReadBrush", "list_directory" or "search_files" => "KindSearchBrush", "run_shell" => "KindShellBrush", _ => "KindOtherBrush" });
        i.Target = tool.Tool switch
        {
            "read_file" => ShortPath(Arg("path")),
            "list_directory" => ShortPath(Arg("path") ?? tool.WorkingDirectory),
            "search_files" => Arg("pattern") ?? "",
            "run_shell" => Short(Arg("command") ?? "", 60),
            "read_index" or "parse" => "",
            _ => Short(tool.Arguments, 60)
        };
        i.Meta = (tool.State is "running" ? "running" : tool.ExitCode is { } code and not 0 ? $"exit {code}" : "") is { Length: > 0 } w ? $"{w} · {Seconds(tool.ElapsedMilliseconds)}" : Seconds(tool.ElapsedMilliseconds);
    }
    private static string ShortPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : path;
    }
    private async void Refresh()
    {
        Interlocked.Exchange(ref _dirty, 1);
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            do
            {
                Interlocked.Exchange(ref _dirty, 0);
                var activities = await Task.Run(() => _smart.Activities(_project)).ConfigureAwait(false);
                _ui.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    var running = _smart.CurrentActivity;
                    var (sumDone, sumTotal) = _summary;
                    Text = _smart.Status.State switch
                    {
                        SmartState.Disabled => "Smart off",
                        SmartState.Error => "Smart error",
                        SmartState.Ready when running is { Context.Task: "summary" } => sumTotal > 0 ? $"Smart · summarizing {Math.Min(sumDone + 1, sumTotal)}/{sumTotal}" : "Smart · summarizing",
                        SmartState.Ready when running is not null => $"Smart · {running.Context.Task}",
                        SmartState.Ready when sumTotal > 0 => $"Smart · summarizing {Math.Min(sumDone + 1, sumTotal)}/{sumTotal}",
                        SmartState.Ready when _knowledge?.IsIndexing == true => "Smart · indexing",
                        SmartState.Ready when activities.FirstOrDefault()?.State == "error" => "Smart · last task failed",
                        SmartState.Ready => "Smart ready",
                        _ => _smart.Status.Message
                    };
                    var latest = LatestPrompt(activities);
                    var rows = new List<(string Id, Action<SmartActivityItem> Fill)>();
                    foreach (var run in latest)
                    {
                        var r = run;
                        rows.Add((r.Id, i => FillTask(i, r)));
                        var tools = r.Tools ?? [];
                        var skip = Math.Max(0, tools.Count - MaxToolRows);
                        for (var n = skip; n < tools.Count; n++)
                        {
                            var t = tools[n];
                            rows.Add(($"{r.Id}:{n}", i => FillTool(i, t)));
                        }
                    }
                    Title = latest.Count > 0 ? $"Latest prompt · {latest[0].StartedAt.ToLocalTime():HH:mm:ss}" : "No Smart activity yet";
                    foreach (var old in Runs.Where(x => !rows.Any(y => y.Id == x.Id)).ToArray()) Runs.Remove(old);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        var item = Runs.FirstOrDefault(x => x.Id == row.Id);
                        if (item is null) { item = new(row.Id) { IsTask = !row.Id.Contains(':') }; Runs.Insert(i, item); }
                        else if (Runs.IndexOf(item) != i) Runs.Move(Runs.IndexOf(item), i);
                        row.Fill(item);
                    }                });
            } while (Volatile.Read(ref _dirty) != 0 && !_disposed);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
            if (Volatile.Read(ref _dirty) != 0 && !_disposed) Refresh();
        }
    }    public void Dispose()
    {
        _disposed = true;
        _smart.Changed -= OnChanged;
        _smart.ActivityChanged -= OnChanged;
        if (_knowledge is { } knowledge)
        {
            knowledge.IndexingChanged -= OnIndexingChanged;
            knowledge.SummaryProgressChanged -= SetSummaryProgress;
        }
    }
}
