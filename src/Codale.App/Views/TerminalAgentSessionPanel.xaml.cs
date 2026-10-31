using System.Collections.ObjectModel;
using System.ComponentModel;

using Codale.Agents;
using Codale.Agents.Agy;
using Codale.Agents.Claude;
using Codale.Agents.Codex;
using Codale.Agents.History;
using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Core.Agents;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Views;

/// <summary>One bar in the panel's vitals: a label, what it reads, and how full it is.</summary>
public sealed partial class UsageMeter : ObservableObject
{
    [ObservableProperty]
    public partial string Label { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial double Percent { get; set; }

    public UsageMeter(string label, string summary, double percent)
    {
        Label = label;
        Summary = summary;
        Percent = percent;
    }
}

/// <summary>One stored conversation of the panel's CLI, shown in the History view.</summary>
public sealed class TerminalHistoryRow(TranscriptSummary summary, bool canResume)
{
    public TranscriptSummary Summary { get; set; } = summary;

    public bool CanResume { get; set; } = canResume;

    public string Title => Summary.Title;

    public string Meta => $"{Summary.UserTurns} {(Summary.UserTurns == 1 ? "turn" : "turns")} · {Summary.UpdatedAt.ToLocalTime():g}";

    public string Tip => Summary.FirstPrompt ?? Summary.Title;

    public Visibility ResumeVisibility => CanResume ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// The side panel of a terminal tab that hosts an agent CLI. The TUI owns the conversation, so this
/// panel only mirrors what Codale can see around it: the CLI's todos, file changes and artifacts, how
/// full its context and usage windows are, and the CLI's own past conversations, each resumable
/// natively. It shares nothing with <see cref="AgentSessionPanel"/>, which drives Codale's own chat.
/// </summary>
public sealed partial class TerminalAgentSessionPanel : UserControl, INotifyPropertyChanged
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(10);
    private const int UsageEveryTicks = 30;

    private readonly DateTime _openedAtUtc = DateTime.UtcNow.AddMinutes(-1);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _elapsedTimer;
    private ChatViewModel? _session;
    private WorkspaceViewModel? _workspace;
    private ICliHistoryReader? _reader;
    private int _ticks;
    private bool _probing;
    private FileSystemWatcher? _watcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _historyDebounce;

    public TerminalAgentSessionPanel()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = ProbeInterval;
        _timer.Tick += (_, _) => OnTick();
        _elapsedTimer = DispatcherQueue.CreateTimer();
        _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        _elapsedTimer.Tick += (_, _) =>
        {
            foreach (var task in Session?.RunningTasks.Where(t => t.IsRunning).ToList() ?? [])
            {
                task.RefreshElapsed();
            }
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A file change was clicked: show its diff.</summary>
    public event EventHandler<string>? FileDiffRequested;

    /// <summary>An artifact was clicked.</summary>
    public event EventHandler<SessionArtifact>? ArtifactRequested;

    /// <summary>A running task was clicked.</summary>
    public event EventHandler<RunningTaskItem>? TaskPeekRequested;

    /// <summary>The CLI's update command should run in a new terminal.</summary>
    public event EventHandler<string>? UpdateCliRequested;

    /// <summary>A past conversation was picked: launch the CLI with the given arguments in a new tab.</summary>
    public event EventHandler<(CliDescriptor Cli, string Arguments)>? ResumeRequested;

    public CliUpdateViewModel CliUpdate { get; } = new();

    public ObservableCollection<UsageMeter> Vitals { get; } = [];

    public bool HasVitals => Vitals.Count > 0;

    public ChatViewModel? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value))
            {
                return;
            }

            if (_session is not null)
            {
                Detach(_session);
            }

            _session = value;
            _reader = null;
            StopWatchingHistory();

            if (_session is not null)
            {
                Attach(_session);
                EnsureReader();
            }

            Raise(nameof(Session));
            Raise(nameof(CliName));
            Raise(nameof(IsLiveEmpty));
            RefreshVitals();
            _ = CliUpdate.RefreshAsync(Cli);
            _ = RefreshHistoryAsync();
        }
    }

    public WorkspaceViewModel? Workspace
    {
        get => _workspace;
        set
        {
            _workspace = value;
            EnsureReader();
            Raise(nameof(CliName));
        }
    }

    /// <summary>Opens the history reader once there is a project path; Workspace can arrive after Session.</summary>
    private void EnsureReader()
    {
        if (_session is null || _reader is not null || string.IsNullOrWhiteSpace(ProjectPath))
        {
            return;
        }

        _reader = CliResume.CreateReader(_session.TerminalCli?.Id, ProjectPath);
        WatchHistory();
        _ = RefreshHistoryAsync();
    }

    public string CliName => Session?.TerminalCli?.DisplayName ?? "Agent";

    public bool IsLiveEmpty =>
        Session is not { } s ||
        s.Todos.Count == 0 && s.SortedTasks.Count == 0 && s.Artifacts.Count == 0 && s.Status.ChangeCount == 0;

    private CliDescriptor Cli => Session?.TerminalCli ?? CliLocator.ActiveCli;

    private string ProjectPath => Session?.WorktreePath ?? Workspace?.ProjectPath ?? "";

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // A task starting or finishing reorders the list: keep the running ones in view at the top.
    private void OnSortedTasksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(RaiseLive);
        if (Session?.SortedTasks.FirstOrDefault() is { } first)
        {
            DispatcherQueue.TryEnqueue(() => RunningTasksList.ScrollIntoView(first, ScrollIntoViewAlignment.Leading));
        }
    }

    private void Attach(ChatViewModel session)
    {
        session.Todos.CollectionChanged += OnLiveChanged;
        session.RunningTasks.CollectionChanged += OnLiveChanged;
        session.SortedTasks.CollectionChanged += OnSortedTasksChanged;
        session.Artifacts.CollectionChanged += OnLiveChanged;
        session.Status.Changes.CollectionChanged += OnLiveChanged;
        session.Status.PropertyChanged += OnStatusChanged;
    }

    private void Detach(ChatViewModel session)
    {
        session.Todos.CollectionChanged -= OnLiveChanged;
        session.RunningTasks.CollectionChanged -= OnLiveChanged;
        session.SortedTasks.CollectionChanged -= OnSortedTasksChanged;
        session.Artifacts.CollectionChanged -= OnLiveChanged;
        session.Status.Changes.CollectionChanged -= OnLiveChanged;
        session.Status.PropertyChanged -= OnStatusChanged;
    }

    private void OnLiveChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(RaiseLive);

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusViewModel.ChangeCount))
        {
            DispatcherQueue.TryEnqueue(RaiseLive);
        }
        else if (e.PropertyName is nameof(StatusViewModel.ContextTokens)
            or nameof(StatusViewModel.ContextWindow)
            or nameof(StatusViewModel.FiveHourUtilization)
            or nameof(StatusViewModel.SevenDayUtilization)
            or nameof(StatusViewModel.FiveHourResetsAt)
            or nameof(StatusViewModel.SevenDayResetsAt)
            or nameof(StatusViewModel.SessionTokens))
        {
            DispatcherQueue.TryEnqueue(RefreshVitals);
        }
    }

    /// <summary>
    /// The sections' visibility binds to Session.*.Count, a nested path the binding does not
    /// re-read when only a collection changes (the Todos list shows only because it is replaced
    /// whole): raising Session re-evaluates every section, as AgentSessionPanel does.
    /// </summary>
    private void RaiseLive()
    {
        Raise(nameof(Session));
        Raise(nameof(IsLiveEmpty));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Session is { } session)
        {
            Detach(session);
            Attach(session);
        }

        _ = CliUpdate.RefreshAsync(Cli);
        _ = RefreshHistoryAsync();
        WatchHistory();
        _ticks = UsageEveryTicks;
        _timer.Start();
        _elapsedTimer.Start();
        OnTick();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _elapsedTimer.Stop();
        StopWatchingHistory();
        if (Session is { } session)
        {
            Detach(session);
        }
    }

    #region Vitals

    /// <summary>
    /// Context and usage are read from outside the CLI, so they are only offered where that is
    /// dependable: Claude's own transcript for the context, and the account's usage windows. The
    /// bars appear once there is something real to show and never block the terminal.
    /// </summary>
    private async void OnTick()
    {
        if (_probing || Session is not { TerminalCli.Id: "claude" or "codex" or "agy" } session || _reader?.HistorySourcePath is not { Length: > 0 } source)
        {
            return;
        }

        _probing = true;
        try
        {
            var since = _openedAtUtc;
            if (session.TerminalCli.Id == "codex")
            {
                // Codex's rollout carries its own window size and the account's windows.
                var path = ProjectPath;
                if (await Task.Run(() => CodexContextProbe.Read(source, path, since)) is { } reading)
                {
                    if (reading.ContextTokens > 0)
                    {
                        session.Status.ContextTokens = reading.ContextTokens;
                        session.Status.ContextWindow = reading.ContextWindow ?? ClaudeContextProbe.WindowFor(reading.ContextTokens);
                    }

                    if (reading.Limits is { } codexLimits)
                    {
                        session.Status.Apply(codexLimits);
                    }
                }
            }
            else if (session.TerminalCli.Id == "agy")
            {
                var path = ProjectPath;
                if (await Task.Run(() => AgyContextProbe.Read(source, path, since)) is { } reading)
                {
                    if (reading.ContextTokens > 0)
                    {
                        session.Status.ContextTokens = reading.ContextTokens;
                        session.Status.ContextWindow = reading.ContextWindow;
                    }
                }
            }
            else
            {
                if (await Task.Run(() => ClaudeContextProbe.Read(source, since)) is { } reading)
                {
                    var known = reading.Model is { Length: > 0 } model ? AppSettings.ClaudeContextWindowFor(model) : null;
                    var window = ClaudeContextProbe.WindowFor(reading.Tokens, known);
                    session.Status.ContextTokens = reading.Tokens;
                    session.Status.ContextWindow = window;

                    // A session past 200K proves the model's window, so the next session (or
                    // this one after a compact) does not fall back to 200K.
                    if (reading.Model is { Length: > 0 } seen && window > (known ?? 200_000))
                    {
                        AppSettings.RememberClaudeContextWindow(seen, window);
                    }
                }

                if (_ticks++ >= UsageEveryTicks)
                {
                    _ticks = 0;
                    // The terminal's CLI runs on the default account, not Codale's: its quota is that account's.
                    if (await ClaudeUsageClient.FetchAsync(CodaleAccount.UserClaudeHome) is { } limits)
                    {
                        session.Status.Apply(limits);
                    }
                }
            }

            RefreshVitals();
        }
        catch (Exception ex)
        {
            CrashLog.Write("TerminalAgentSessionPanel.OnTick", ex.Message, ex);
        }
        finally
        {
            _probing = false;
        }
    }

    private void RefreshVitals()
    {
        if (Session?.Status is not { } status)
        {
            if (Vitals.Count > 0)
            {
                Vitals.Clear();
                Raise(nameof(HasVitals));
            }

            return;
        }

        var desired = new List<(string Label, string Summary, double Percent)>();

        if (status.HasContext && status.HasContextWindow)
        {
            desired.Add(("CONTEXT", status.ContextSummary, status.ContextPercent));
        }

        // Only show one usage bar: the rate limit quota closest to exhaustion (least remaining quota).
        (string Summary, double Percent)? usage = null;
        if (status.HasFiveHour && status.HasSevenDay)
        {
            usage = status.SevenDayPercent > status.FiveHourPercent
                ? (status.SevenDaySummary, status.SevenDayPercent)
                : (status.FiveHourSummary, status.FiveHourPercent);
        }
        else if (status.HasFiveHour)
        {
            usage = (status.FiveHourSummary, status.FiveHourPercent);
        }
        else if (status.HasSevenDay)
        {
            usage = (status.SevenDaySummary, status.SevenDayPercent);
        }
        else if (status.HasTokenUsage)
        {
            usage = (status.TokenUsageSummary, status.TokenUsagePercent);
        }

        if (usage is not null)
        {
            desired.Add(("USAGE", usage.Value.Summary, usage.Value.Percent));
        }

        var hadVitals = HasVitals;

        // Remove any meters not matching a desired label, or duplicate labels.
        var seenLabels = new HashSet<string>();
        for (var i = Vitals.Count - 1; i >= 0; i--)
        {
            var label = Vitals[i].Label;
            if (!desired.Any(d => d.Label == label) || !seenLabels.Add(label))
            {
                Vitals.RemoveAt(i);
            }
        }

        // Add or update desired meters in order without recreating existing elements,
        // avoiding repeated visual reset animations on every tick.
        for (var i = 0; i < desired.Count; i++)
        {
            var (label, summary, percent) = desired[i];
            var existingIndex = -1;
            for (var j = 0; j < Vitals.Count; j++)
            {
                if (Vitals[j].Label == label)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var meter = Vitals[existingIndex];
                if (meter.Summary != summary)
                {
                    meter.Summary = summary;
                }

                if (Math.Abs(meter.Percent - percent) > 0.001)
                {
                    meter.Percent = percent;
                }

                if (existingIndex != i)
                {
                    Vitals.Move(existingIndex, i);
                }
            }
            else
            {
                Vitals.Insert(Math.Min(i, Vitals.Count), new UsageMeter(label, summary, percent));
            }
        }

        if (HasVitals != hadVitals)
        {
            Raise(nameof(HasVitals));
        }
    }

    #endregion

    #region Session

    private async void OnUpdateCliClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var cli = CliUpdate.Cli;
            if (cli != Cli || !CliUpdate.UpdateAvailable ||
                CliVersionCheck.UpdateCommand(cli) is not { } command)
            {
                return;
            }

            UpdateCliRequested?.Invoke(this, command);

            if (cli.Id == "codex")
            {
                return;
            }

            for (var i = 0; i < 36 && CliUpdate.UpdateAvailable && CliUpdate.Cli == cli; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await CliUpdate.RefreshAsync(cli);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("TerminalAgentSessionPanel.OnUpdateCliClick", ex.Message, ex);
        }
    }

    private void OnRunningTaskClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RunningTaskItem task)
        {
            TaskPeekRequested?.Invoke(this, task);
        }
    }

    private async void OnStopTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RunningTaskItem task } && Session is not null)
        {
            await Session.StopTaskAsync(task);
        }
    }

    private void OnChangeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionFileChange change)
        {
            FileDiffRequested?.Invoke(this, change.Path);
        }
    }

    private void OnArtifactClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionArtifact artifact)
        {
            ArtifactRequested?.Invoke(this, artifact);
        }
    }

    #endregion

    #region History

    /// <summary>
    /// Refreshes the list when the CLI logs a turn: its history file or folder is watched, and a burst of
    /// writes collapses into one refresh a moment after the last.
    /// </summary>
    private void WatchHistory()
    {
        StopWatchingHistory();
        if (_reader?.HistorySourcePath is not { Length: > 0 } source)
        {
            return;
        }

        try
        {
            var isFile = File.Exists(source) || Path.HasExtension(source);
            var directory = isFile ? Path.GetDirectoryName(source) : source;
            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }

            _historyDebounce ??= DispatcherQueue.CreateTimer();
            _historyDebounce.Interval = TimeSpan.FromSeconds(1.5);
            _historyDebounce.IsRepeating = false;
            _historyDebounce.Tick -= OnHistoryDebounced;
            _historyDebounce.Tick += OnHistoryDebounced;

            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = !isFile,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            if (isFile)
            {
                watcher.Filter = Path.GetFileName(source);
            }

            watcher.Changed += OnHistoryFileChanged;
            watcher.Created += OnHistoryFileChanged;
            watcher.Renamed += OnHistoryFileChanged;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            CrashLog.Write("TerminalAgentSessionPanel.WatchHistory", ex.Message, ex);
        }
    }

    private void StopWatchingHistory()
    {
        _historyDebounce?.Stop();
        if (_watcher is { } watcher)
        {
            _watcher = null;
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void OnHistoryFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_reader is { } reader && reader.IsSessionFile(e.FullPath))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _historyDebounce?.Stop();
                _historyDebounce?.Start();
            });
        }
    }

    private void OnHistoryDebounced(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => _ = RefreshHistoryAsync();

    private async Task RefreshHistoryAsync()
    {
        var reader = _reader;
        var cli = Session?.TerminalCli;
        if (cli is null)
        {
            return;
        }

        if (reader is null || !CliResume.IsSupported(cli.Id))
        {
            HistoryList.ItemsSource = null;
            HistoryCountBadge.Visibility = Visibility.Collapsed;
            HistoryHint.Text = $"{cli.DisplayName} has no session id Codale can resume by, so its past conversations are not listed here.";
            return;
        }

        try
        {
            var rows = await Task.Run(() => reader.ListSessions());
            HistoryList.ItemsSource = rows.Select(r => new TerminalHistoryRow(r, true)).ToList();
            HistoryCountText.Text = rows.Count.ToString();
            HistoryCountBadge.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            HistoryHint.Text = rows.Count == 0
                ? $"No {cli.DisplayName} conversations in this project yet."
                : "Pick a conversation to resume it in a new tab, with its full context.";
        }
        catch (Exception ex)
        {
            CrashLog.Write("TerminalAgentSessionPanel.RefreshHistory", ex.Message, ex);
        }
    }

    private void OnHistoryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TerminalHistoryRow row &&
            Session?.TerminalCli is { } cli &&
            CliResume.Arguments(cli.Id, row.Summary.SessionId) is { } arguments)
        {
            ResumeRequested?.Invoke(this, (cli, arguments));
        }
    }

    #endregion
}
