using System.Collections.ObjectModel;
using System.Text;
using Codale.App.Services;
using Codale.Smart;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace Codale.App.ViewModels;

public sealed partial class SmartActivityItem(string id) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] public partial string Heading { get; set; } = "";
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DetailsVisibility))] public partial bool IsExpanded { get; set; }
    public bool HasDetails { get; set; }
    public Microsoft.UI.Xaml.Visibility DetailsVisibility => IsExpanded && HasDetails ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public void Toggle() { if (HasDetails) IsExpanded = !IsExpanded; }
}

/// <summary>Project-scoped Smart activity in the status bar, including background work.</summary>
public sealed partial class SmartActivityViewModel : ObservableObject, IDisposable
{
    private readonly string _project;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly SmartExperience _smart = SmartExperienceHost.Instance;
    private int _refreshing, _dirty;
    private bool _disposed;
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
                    var running = activities.FirstOrDefault(a => a.State is "queued" or "exploring" or "finalizing");
                    Text = _smart.Status.State switch
                    {
                        SmartState.Disabled => "Smart off",
                        SmartState.Error => "Smart error",
                        SmartState.Ready when running is not null => $"Smart · {running.Context.Task}",
                        SmartState.Ready when activities.FirstOrDefault()?.State == "error" => "Smart · last task failed",
                        SmartState.Ready => "Smart ready",
                        _ => _smart.Status.Message
                    };
                    var rows = new List<(string Id, string Heading, string Details)>();
                    foreach (var run in LatestPrompt(activities))
                    {
                        var head = $"{run.Context.Task} · {run.State} · {run.ElapsedMilliseconds / 1000.0:0.0}s";
                        var info = new StringBuilder($"Worktree: {run.Context.WorkingDirectory}\nActivity: {run.Id}");
                        if (run.Context.ChatId is not null) info.Append("\nChat: " + run.Context.ChatId);
                        if (run.Error is not null) info.Append('\n').Append(run.Error);
                        rows.Add((run.Id, head, info.ToString()));
                        var n = 0;
                        foreach (var tool in run.Tools ?? [])
                        {
                            var d = new StringBuilder($"Directory: {tool.WorkingDirectory}\n{tool.Arguments}");
                            if (tool.ExitCode is { } exit) d.Append($"\nExit: {exit}");
                            d.Append('\n').Append(tool.Output);
                            rows.Add(($"{run.Id}:{n++}", $"   {tool.Tool} · {Short(tool.Arguments, 40)} · {tool.State} · {tool.ElapsedMilliseconds}ms", d.ToString()));
                        }
                    }
                    Title = LatestPrompt(activities) is { Count: > 0 } latest ? $"Latest prompt · {latest[0].StartedAt.ToLocalTime():HH:mm:ss}" : "No Smart activity yet";
                    foreach (var old in Runs.Where(r => !rows.Any(x => x.Id == r.Id)).ToArray()) Runs.Remove(old);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        var item = Runs.FirstOrDefault(r => r.Id == row.Id);
                        if (item is null) { item = new(row.Id); Runs.Insert(i, item); }
                        else if (Runs.IndexOf(item) != i) Runs.Move(Runs.IndexOf(item), i);
                        item.Heading = row.Heading; item.HasDetails = true; item.Details = row.Details;
                    }
                });
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
    }
}
