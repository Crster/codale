using System.Collections.ObjectModel;
using System.ComponentModel;

using Codale.Agents;
using Codale.Agents.Claude;
using Codale.Agents.History;
using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Views;

/// <summary>One bar in the panel's vitals: a label, what it reads, and how full it is.</summary>
public sealed class UsageMeter(string label, string summary, double percent)
{
    public string Label { get; set; } = label;

    public string Summary { get; set; } = summary;

    public double Percent { get; set; } = percent;
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
                _reader = CliResume.CreateReader(_session.TerminalCli?.Id, ProjectPath);
                WatchHistory();
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
            Raise(nameof(CliName));
        }
    }

    public string CliName => Session?.TerminalCli?.DisplayName ?? "Agent";

    public bool IsLiveEmpty =>
        Session is not { } s ||
        s.Todos.Count == 0 && s.RunningTasks.Count == 0 && s.Artifacts.Count == 0 && s.Status.ChangeCount == 0;

    private CliDescriptor Cli => Session?.TerminalCli ?? CliLocator.ActiveCli;

    private string ProjectPath => Session?.WorktreePath ?? Workspace?.ProjectPath ?? "";

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Attach(ChatViewModel session)
    {
        session.Todos.CollectionChanged += OnLiveChanged;
        session.RunningTasks.CollectionChanged += OnLiveChanged;
        session.Artifacts.CollectionChanged += OnLiveChanged;
        session.Status.PropertyChanged += OnStatusChanged;
    }

    private void Detach(ChatViewModel session)
    {
        session.Todos.CollectionChanged -= OnLiveChanged;
        session.RunningTasks.CollectionChanged -= OnLiveChanged;
        session.Artifacts.CollectionChanged -= OnLiveChanged;
        session.Status.PropertyChanged -= OnStatusChanged;
    }

    private void OnLiveChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => Raise(nameof(IsLiveEmpty)));

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusViewModel.ChangeCount))
        {
            DispatcherQueue.TryEnqueue(() => Raise(nameof(IsLiveEmpty)));
        }
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
        OnTick();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
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
        if (_probing || Session is not { TerminalCli.Id: "claude" } session)
        {
            return;
        }

        _probing = true;
        try
        {
            var path = ProjectPath;
            var since = _openedAtUtc;
            var tokens = await Task.Run(() => ClaudeContextProbe.ReadTokens(path, since));
            if (tokens is { } used)
            {
                session.Status.ContextTokens = used;
                session.Status.ContextWindow = ClaudeContextProbe.WindowFor(used);
            }

            if (_ticks++ >= UsageEveryTicks)
            {
                _ticks = 0;
                if (await ClaudeUsageClient.FetchAsync() is { } limits)
                {
                    session.Status.Apply(limits);
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
        Vitals.Clear();
        if (Session?.Status is { } status)
        {
            if (status.HasContext && status.HasContextWindow)
            {
                Vitals.Add(new UsageMeter("CONTEXT", status.ContextSummary, status.ContextPercent));
            }

            if (status.HasFiveHour)
            {
                Vitals.Add(new UsageMeter("5-HOUR", status.FiveHourSummary, status.FiveHourPercent));
            }

            if (status.HasSevenDay)
            {
                Vitals.Add(new UsageMeter(status.LongWindowLabel.ToUpperInvariant(), status.SevenDaySummary, status.SevenDayPercent));
            }
        }

        Raise(nameof(HasVitals));
    }

    #endregion

    #region Session

    private async void OnUpdateCliClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var cli = CliUpdate.Cli;
            if (CliVersionCheck.UpdateCommand(cli) is not { } command)
            {
                return;
            }

            UpdateCliRequested?.Invoke(this, command);

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

    private void OnViewChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var history = ReferenceEquals(sender.SelectedItem, HistoryItem);
        LiveHost.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryHost.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        if (history)
        {
            _ = RefreshHistoryAsync();
        }
    }

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
            HistoryHint.Text = $"{cli.DisplayName} has no session id Codale can resume by, so its past conversations are not listed here.";
            return;
        }

        try
        {
            var rows = await Task.Run(() => reader.ListSessions());
            HistoryList.ItemsSource = rows.Select(r => new TerminalHistoryRow(r, true)).ToList();
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
