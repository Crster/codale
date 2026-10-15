using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Codale.Agents;
using Codale.Agents.Claude;
using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Core.Features;
using Codale.Core.Helper;
using Codale.Core.Tasks;
using Codale.Core.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Codale.App.ViewModels;

/// <summary>
/// Owns one agent session and projects its event stream onto the transcript.
/// All mutation happens on the UI thread; the session raises events on a reader task.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly string _projectPath;
    private readonly StatusViewModel _status;

    private IAgentSession? _session;
    private AssistantMessageItem? _streaming;

    /// <summary>
    /// The main thread's most recent assistant-message usage: the true context-window
    /// reading. The result event's usage is the turn's cumulative total instead, so the
    /// meter must not take its context number from there.
    /// </summary>
    private UsageSnapshot? _lastMessageUsage;

    /// <summary>
    /// Tokens per API request this session, by message id, for the BYOK usage bar on a CLI
    /// with no running total (Claude). Keyed because one request can land as several
    /// messages repeating its usage; each id counts once, at its latest reading.
    /// </summary>
    private readonly Dictionary<string, long> _messageTokens = [];
    private readonly Dictionary<string, ToolCallItem> _toolCalls = [];
    private readonly Dictionary<string, TaskCompletionSource<ApprovalDecision>> _approvals = [];

    /// <summary>The timeline of the turn in flight: steps land here while it runs, and it folds when it lands.</summary>
    private TurnActivityItem? _activeTurn;

    /// <summary>The active turn's one task-list row, updated in place as the list moves.</summary>
    private TodoStepItem? _turnTodo;

    /// <summary>The plan document the active turn wrote, which its ExitPlanMode proposes.</summary>
    private SessionArtifact? _turnPlanFile;

    /// <summary>Tool calls tracked but kept out of the timeline: task bookkeeping and CLI plumbing.</summary>
    private readonly HashSet<string> _hiddenCalls = [];

    /// <summary>Ticks the active turn's clock; UI-thread timer, so ticks touch items freely.</summary>
    private readonly DispatcherQueueTimer _turnTimer;

    private DateTimeOffset _turnStarted;

    /// <summary>Effort the running session was actually started with; spawn-time, so a change means a restart.</summary>
    private string? _liveEffort;

    /// <summary>
    /// Plan turns restore the previous permission mode when they finish. The session is
    /// captured with the mode so a session replaced mid-turn is never touched.
    /// </summary>
    private IAgentSession? _planSession;
    private string? _planRestoreMode;
    private string? _planRestoreEffort;
    private bool _planEffortRaised;

    /// <summary>Relative workspace paths for @-references, built on first use per connect.</summary>
    private IReadOnlyList<string>? _workspaceFiles;

    /// <summary>The query token the suggestion popup was last built from.</summary>
    private string? _lastSuggestionKey;
    private bool _loadingWorkspaceFiles;

    /// <summary>Set when the user pressed Stop, cleared when the turn ends.</summary>
    private bool _interruptPending;

    /// <summary>Endpoint overrides (proxy/gateway) the user configured; null keeps the CLI defaults.</summary>
    private readonly CliEndpointSettings? _endpointSettings;

    /// <summary>Which built-in MCP servers (browser, desktop control) sessions get; null attaches none.</summary>
    private readonly McpServerSettings? _mcpSettings;

    /// <summary>Extra folders granted to the agent next to the project; null grants none.</summary>
    public WorkspaceFolders? ExtraFolders { get; init; }

    /// <summary>
    /// The CLI this chat's panel session runs: "claude" (the default) or "codex",
    /// picked by the button that opened the chat ("Claude chat" / "Codex chat").
    /// Terminal-CLI chats are unaffected. Settable until the session connects.
    /// </summary>
    private string _cliId = Codale.Agents.CliLocator.ActiveCli.Id;

    public string CliId
    {
        get => _cliId;
        set
        {
            if (_cliId != value)
            {
                _cliId = value;
                OnPropertyChanged(nameof(ProviderName));

                // Provider and tier are per CLI: the constructor read them for the default CLI.
                ReloadCliDefaults();
            }
        }
    }

    /// <summary>
    /// Re-reads Settings' provider and tier for <see cref="CliId"/> while nothing is
    /// connected yet; a live session keeps what it was started with.
    /// </summary>
    public void ReloadCliDefaults()
    {
        if (IsConnected || _connecting)
        {
            return;
        }

        var provider = AppSettings.CliProviderFor(CliId);
        if (!string.Equals(EndpointName, provider, StringComparison.OrdinalIgnoreCase))
        {
            EndpointName = provider;
            OnPropertyChanged(nameof(EndpointName));
        }

        (RequestedModel, RequestedEffort) = TierConfig(AppSettings.DefaultTierFor(CliId));
        RefreshCustomProviderName();
    }

    /// <summary>The CLI descriptor behind <see cref="CliId"/>.</summary>
    public Codale.Agents.CliDescriptor SessionCli =>
        Codale.Agents.CliLocator.KnownClis.FirstOrDefault(cli => cli.Id == CliId) ?? Codale.Agents.CliLocator.ActiveCli;

    /// <summary>True when this chat's panel session is a Codex CLI session.</summary>
    private bool IsCodexSession => TerminalCli is null && SessionCli.Id == "codex";

    public ChatViewModel(string projectPath, StatusViewModel status, CliEndpointSettings? endpointSettings = null, McpServerSettings? mcpSettings = null)
    {
        _projectPath = projectPath;
        _status = status;
        _endpointSettings = endpointSettings;
        _mcpSettings = mcpSettings;
        EndpointName = AppSettings.CliProviderFor(CliId);
        RefreshCustomProviderName();

        // A new chat starts where the user last left this CLI: the saved tier (resolved
        // to model/effort), or the CLI's own default when nothing has been chosen yet.
        (RequestedModel, RequestedEffort) = TierConfig(AppSettings.DefaultTierFor(CliId));

        // The session panel shows its live sections (tasks, changes, subagents) only once
        // there is a conversation; a chat opened empty from history gains one later.
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConversation));

        _turnTimer = _dispatcher.CreateTimer();
        _turnTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _turnTimer.Tick += (_, _) =>
        {
            if (_activeTurn is { HasClock: true } turn)
            {
                turn.Elapsed = DateTimeOffset.Now - _turnStarted;
                WorkingElapsed = ToolKinds.FormatElapsed(turn.Elapsed);
            }

            // A new line of nonsense every 10 seconds (10 one-second ticks) keeps the wait feeling alive.
            if (++_workingTicks % 10 == 0)
            {
                WorkingMessage = WorkingPhrases.Next(WorkingMessage);
            }
        };

        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
    }

    public ObservableCollection<ChatItem> Items { get; } = [];

    /// <summary>This conversation's own status: model, context, cost and the files it changed.</summary>
    public StatusViewModel Status => _status;

    /// <summary>
    /// Labels the custom-provider usage button with the background-task provider, else "Claude".
    /// Never follows the chat's CLI provider override: the button counts what that provider serves.
    /// </summary>
    public void RefreshCustomProviderName()
    {
        var helperName = AppSettings.HelperApiProvider?.Provider.Name.Trim();
        if (helperName is { Length: > 0 })
        {
            _status.CustomProviderName = helperName;
        }
        else if (string.Equals(AppSettings.HelperProvider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            _status.CustomProviderName = "Codex";
        }
        else
        {
            // Settings' "Claude" entry is stored as "": background tasks run on the Claude CLI.
            _status.CustomProviderName = "Claude";
        }

        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(TabTitle));
    }

    /// <summary>True when this chat's turns go to the same BYOK provider background tasks use, so they share its tally.</summary>
    private bool ServedByHelperProvider() =>
        AppSettings.FindByok(EndpointName) is { } byok &&
        AppSettings.HelperApiProvider?.Provider is { } helper &&
        string.Equals(byok.Name.Trim(), helper.Name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A name for the conversation's tab: the session's title when it came from history,
    /// else the opening prompt. Null until there is one - the tab shows the provider.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    public partial string? Title { get; set; }

    public string TabTitle => Title is { Length: > 0 } title ? title : ProviderName;

    /// <summary>
    /// True once the chat holds a conversation worth keeping - a prompt was sent, a
    /// session is being resumed, or a turn is running. Opening another session from
    /// history then gets a tab of its own instead of replacing this one.
    /// </summary>
    public bool HasConversation =>
        IsBusy || ResumeSessionId is not null || Items.Any(i => i is UserMessageItem or TurnActivityItem);

    private int _workingTicks;

    /// <summary>The playful line in the bottom status while a turn runs; it changes every few seconds.</summary>
    [ObservableProperty]
    public partial string WorkingMessage { get; set; } = "";

    /// <summary>How long the running turn has taken so far, e.g. "1m 04s".</summary>
    [ObservableProperty]
    public partial string WorkingElapsed { get; set; } = "";

    /// <summary>The turn is blocked on the reader: a question or a permission is open.</summary>
    public bool IsWaitingForYou => _approvals.Count > 0;

    public bool IsWorkingOnItsOwn => !IsWaitingForYou;

    private void OnApprovalsChanged()
    {
        OnPropertyChanged(nameof(IsWaitingForYou));
        OnPropertyChanged(nameof(IsWorkingOnItsOwn));
        OnPropertyChanged(nameof(IsBusyWorking));
    }

    /// <summary>The agent's task list, as of its last todos_set.</summary>
    public RangeObservableCollection<TodoItem> Todos { get; } = [];

    /// <summary>Background shells and subagents this conversation started, oldest first.</summary>
    public ObservableCollection<RunningTaskItem> RunningTasks { get; } = [];

    private readonly Dictionary<string, RunningTaskItem> _tasksByToolUse = [];

    // Commands Codale itself runs for the agent (codale-tasks). One host per chat, kept
    // across session restarts (DisposeSessionAsync) so a dev server survives a model
    // switch; it dies with the chat (DisposeAsync), or when the conversation is reset.
    private HostedTaskManager? _taskManager;
    private TaskPipeServer? _taskPipe;
    private string? _taskManagerDir;
    private DispatcherQueueTimer? _hostedTimer;

    /// <summary>The workspace's terminal tabs, readable by the agent's terminal tools; null for a chat without a workspace.</summary>
    public ITerminalSource? Terminals { get; init; }

    private string? _lastUserPrompt;

    /// <summary>The most recent user prompt in this chat.</summary>
    public string? LastUserPrompt =>
        _lastUserPrompt ?? Items.OfType<UserMessageItem>().LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Text))?.Text;

    /// <summary>The pipe and secret the codale-tasks MCP exe uses to reach this chat's manager, created on first use.</summary>
    private (string Pipe, string Token)? EnsureTaskHost(string workingDirectory)
    {
        if (_taskManager is not null && !string.Equals(_taskManagerDir, workingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            StopTaskHost(); // the project moved (worktree): its tasks belong to the old folder
        }

        if (_taskManager is null)
        {
            _taskManager = new HostedTaskManager(workingDirectory);
            _taskManagerDir = workingDirectory;
            _taskManager.TaskStarted += task => _dispatcher.TryEnqueue(() => OnHostedTaskStarted(task));
            _taskPipe = new TaskPipeServer(new LocalTaskService(_taskManager, Terminals));

            // An agent CLI in a terminal reports its task list and artifacts here (see ServersForTerminalCli).
            _taskPipe.PanelPublished = (kind, input) => _dispatcher.TryEnqueue(() =>
            {
                if (kind == "todos")
                {
                    ApplyTodos(input);
                }
                else if (kind == "artifact")
                {
                    RecordAgentArtifact(input);
                }
            });
            _taskPipe.Start();
        }

        return (_taskPipe!.PipeName, _taskPipe.Token);
    }

    /// <summary>
    /// The servers an agent CLI running in a terminal gets: the browser and computer servers plus
    /// the tasks server wired to this chat's host, so tasks the CLI starts are listed in the panel.
    /// </summary>
    public IReadOnlyList<McpServerSpec> ServersForTerminalCli()
    {
        if (_mcpSettings is null)
        {
            return [];
        }

        var servers = _mcpSettings.Servers();
        var workingDirectory = WorktreePath ?? _projectPath;
        if (_mcpSettings.TasksAvailable &&
            EnsureTaskHost(workingDirectory) is { } host &&
            _mcpSettings.TasksSpec(host.Pipe, host.Token, workingDirectory, pushToPanel: true) is { } tasks)
        {
            servers = [.. servers, tasks];
        }

        return servers;
    }

    private void StopTaskHost()
    {
        _hostedTimer?.Stop();
        _taskPipe?.Dispose();
        _taskManager?.Dispose();
        _taskPipe = null;
        _taskManager = null;
        _taskManagerDir = null;

        foreach (var t in RunningTasks.Where(t => t.Hosted is not null && t.IsRunning))
        {
            t.Status = RunningTaskStatus.Ended;
        }
    }

    private void OnHostedTaskStarted(HostedTask hosted)
    {
        if (RunningTasks.FirstOrDefault(t => t.Hosted == hosted) is not null)
        {
            return;
        }

        var item = new RunningTaskItem
        {
            ToolUseId = $"hosted:{hosted.Id}",
            Kind = RunningTaskKind.Shell,
            Title = hosted.Name,
            StartedAt = hosted.StartedAt,
            Hosted = hosted,
            Pid = hosted.Pid,
            Header = RunningTaskItem.CommandHeader(hosted.Command),
        };
        AddTask(item);

        if (_toolCalls.Values.LastOrDefault(c => c.ToolName.Contains("start_task", StringComparison.OrdinalIgnoreCase)) is { } activeCall)
        {
            _tasksByToolUse[activeCall.ToolUseId] = item;
        }

        hosted.Finished += _ => _dispatcher.TryEnqueue(SyncHostedTasks);

        if (_hostedTimer is null)
        {
            // Copy new output across in small batches: a chatty server would otherwise
            // rebuild the log text once per line.
            _hostedTimer = _dispatcher.CreateTimer();
            _hostedTimer.Interval = TimeSpan.FromMilliseconds(250);
            _hostedTimer.Tick += (_, _) => SyncHostedTasks();
        }

        if (!_hostedTimer.IsRunning)
        {
            _hostedTimer.Start();
        }

        SyncHostedTasks();
    }

    private void SyncHostedTasks()
    {
        var anyRunning = false;

        foreach (var item in RunningTasks.Where(t => t.Hosted is not null && t.IsRunning).ToList())
        {
            var hosted = item.Hosted!;

            // Sample the state first: output that lands after the read below is caught
            // by the next tick only while the task still counts as running.
            var state = hosted.State;
            var (text, next) = hosted.Read(item.HostOffset);
            if (text.Length > 0)
            {
                item.AppendOutput(text);
            }

            item.HostOffset = next;

            switch (state)
            {
                case HostedTaskState.Exited:
                    item.Status = hosted.ExitCode is 0 ? RunningTaskStatus.Completed : RunningTaskStatus.Failed;
                    break;
                case HostedTaskState.Stopped:
                    item.Status = RunningTaskStatus.Ended;
                    break;
                default:
                    anyRunning = true;
                    break;
            }
        }

        if (!anyRunning)
        {
            _hostedTimer?.Stop();
        }
    }

    /// <summary>The tracked subagent a nested event belongs to, if any.</summary>
    private RunningTaskItem? OwnerOf(string? parentToolUseId) =>
        parentToolUseId is not null && _tasksByToolUse.TryGetValue(parentToolUseId, out var owner) ? owner : null;

    /// <summary>
    /// A subagent is still working: the main agent is waiting on it (or it outlived the turn),
    /// so the composer keeps offering Stop and a stop reaches it.
    /// </summary>
    private bool HasActiveSubagents =>
        RunningTasks.Any(t => t.Kind == RunningTaskKind.Subagent && t.Hosted is null && t.IsRunning);

    /// <summary>Subagents still working, for the status bar.</summary>
    public int ActiveSubagentCount =>
        RunningTasks.Count(t => t.Kind == RunningTaskKind.Subagent && t.Hosted is null && t.IsRunning);

    public string ActiveSubagentText => ActiveSubagentCount == 1 ? "1 agent" : $"{ActiveSubagentCount} agents";

    /// <summary>True while the turn runs or a subagent is still going: the composer shows Stop.</summary>
    public bool ShowStop => IsBusy || HasActiveSubagents;

    private void WatchTask(RunningTaskItem task) =>
        task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RunningTaskItem.Status) or nameof(RunningTaskItem.IsRunning))
            {
                NotifySubagents();
                RefreshTaskStatusLine(task);
            }
            else if (e.PropertyName == nameof(RunningTaskItem.LastLine))
            {
                RefreshTaskStatusLine(task);
            }
        };

    private RunningTaskItem? _liveTask;

    /// <summary>"[Task 2] finding file x.png": the newest message from a running task, streamed in the status bar.</summary>
    [ObservableProperty]
    public partial string TaskStatusLine { get; set; } = "";

    private void RefreshTaskStatusLine(RunningTaskItem changed)
    {
        if (changed.IsRunning && changed.LastLine.Length > 0)
        {
            _liveTask = changed;
        }
        else if (ReferenceEquals(_liveTask, changed) && !changed.IsRunning)
        {
            _liveTask = RunningTasks.LastOrDefault(t => t.IsRunning && t.LastLine.Length > 0);
        }

        var index = _liveTask is null ? -1 : RunningTasks.IndexOf(_liveTask);
        TaskStatusLine = index < 0 || _liveTask is null ? "" : $"[Task {index + 1}] {_liveTask.LastLine}";
    }

    private void NotifySubagents()
    {
        OnPropertyChanged(nameof(ShowStop));
        OnPropertyChanged(nameof(ActiveSubagentCount));
        OnPropertyChanged(nameof(ActiveSubagentText));
    }

    private void TrackTaskStart(ToolCallStarted started, ToolCallItem item)
    {
        if (_tasksByToolUse.TryGetValue(started.ParentToolUseId ?? "", out var parent))
        {
            // A subagent's own tool calls are its visible activity.
            parent.AppendOutput($"\n> {item.Verb} {item.Target}".TrimEnd() + "\n");
            parent.AddCall(started.ToolUseId, item.Kind, $"{item.Verb} {item.Target}");
            _subagentCalls[started.ToolUseId] = parent;
            return;
        }

        if (BackgroundTaskDetector.IsSubagentTool(started.ToolName))
        {
            var title = started.Input.ValueKind == JsonValueKind.Object
                && started.Input.TryGetProperty("description", out var d)
                && d.ValueKind == JsonValueKind.String
                ? d.GetString()! : "Subagent";

            var prompt = started.Input.ValueKind == JsonValueKind.Object
                && started.Input.TryGetProperty("prompt", out var p)
                && p.ValueKind == JsonValueKind.String
                ? p.GetString()! : "";

            AddTask(new RunningTaskItem
            {
                ToolUseId = started.ToolUseId,
                Kind = RunningTaskKind.Subagent,
                Title = title,
                StartedAt = started.Timestamp ?? DateTimeOffset.Now,
                Header = string.IsNullOrWhiteSpace(prompt) ? "" : RunningTaskItem.PromptHeader(prompt),
                Prompt = prompt.Trim(),
            });
        }
        else if (BackgroundTaskDetector.IsBackgroundLaunch(started.ToolName, started.Input))
        {
            AddTask(new RunningTaskItem
            {
                ToolUseId = started.ToolUseId,
                Kind = RunningTaskKind.Shell,
                Title = item.Command.Trim(),
                StartedAt = started.Timestamp ?? DateTimeOffset.Now,
                Header = RunningTaskItem.CommandHeader(item.Command),
            });
        }
        else if (started.ToolName == "KillShell" && FindShell(BackgroundTaskDetector.ShellIdOf(started.Input)) is { } killed)
        {
            killed.Status = RunningTaskStatus.Ended;
        }
    }

    /// <summary>The CLI's own id for a background task lets it be stopped, and its notification is what ends it.</summary>
    private void ApplyBackgroundTask(BackgroundTaskChanged changed)
    {
        RunningTaskItem? task = null;
        if (changed.ToolUseId is { Length: > 0 } toolUseId && _tasksByToolUse.TryGetValue(toolUseId, out var byTool))
        {
            task = byTool;
        }

        task ??= RunningTasks.FirstOrDefault(t => t.RuntimeId == changed.TaskId);

        if (task is null)
        {
            task = new RunningTaskItem
            {
                ToolUseId = changed.ToolUseId ?? $"task:{changed.TaskId}",
                Kind = RunningTaskKind.Shell,
                Title = string.IsNullOrWhiteSpace(changed.Prompt) ? $"Task {changed.TaskId}" : changed.Prompt.Trim(),
                StartedAt = DateTimeOffset.Now,
                Header = string.IsNullOrWhiteSpace(changed.Prompt) ? "" : RunningTaskItem.PromptHeader(changed.Prompt),
                Prompt = changed.Prompt?.Trim() ?? "",
                RuntimeId = changed.TaskId,
                LaunchedInBackground = true,
            };
            AddTask(task);
        }

        task.RuntimeId = changed.TaskId;
        task.LaunchedInBackground = true;
        if (changed.Prompt is { Length: > 0 } prompt && task.Header.Length == 0)
        {
            task.Header = RunningTaskItem.PromptHeader(prompt);
            task.Prompt = prompt.Trim();
        }

        task.Status = changed.Status switch
        {
            null => RunningTaskStatus.Running,
            "completed" => RunningTaskStatus.Completed,
            "failed" => RunningTaskStatus.Failed,
            _ => RunningTaskStatus.Ended,
        };
    }

    /// <summary>Tool calls a subagent made, so their results land in its log.</summary>
    private readonly Dictionary<string, RunningTaskItem> _subagentCalls = [];

    private void TrackTaskCompleted(ToolCallCompleted completed)
    {
        if (_subagentCalls.Remove(completed.ToolUseId, out var owner) && !string.IsNullOrWhiteSpace(completed.ResultText))
        {
            var result = completed.ResultText.Trim();
            var shown = result.Length > 1500 ? result[..1500] + "\n… (truncated)" : result;
            owner.AppendOutput(shown + "\n");
            owner.CompleteCall(completed.ToolUseId, shown, completed.IsError);
        }
        else if (owner is not null)
        {
            owner.CompleteCall(completed.ToolUseId, null, completed.IsError);
        }

        if (_tasksByToolUse.TryGetValue(completed.ToolUseId, out var task))
        {
            // A backgrounded subagent's tool result is only the launch receipt; its notification ends it.
            if (!task.IsShell && !completed.IsError
                && (task.LaunchedInBackground || completed.ResultText?.StartsWith("Async agent launched", StringComparison.Ordinal) == true))
            {
                task.LaunchedInBackground = true;
                return;
            }

            if (task.IsShell)
            {
                // Completion of a background launch only means it started.
                var (id, path) = BackgroundTaskDetector.ParseLaunchResult(completed.ResultText);
                task.ShellId = id;
                task.OutputPath = path;
                if (completed.IsError)
                {
                    task.Status = RunningTaskStatus.Failed;
                }
            }
            else
            {
                task.Status = completed.IsError ? RunningTaskStatus.Failed : RunningTaskStatus.Completed;
                if (completed.ResultText is { Length: > 0 } summary)
                {
                    task.AppendOutput("\n" + summary);
                    task.AddText(summary);
                }
            }

            return;
        }

        // BashOutput results feed a shell's log when there is no file to tail.
        if (_toolCalls.TryGetValue(completed.ToolUseId, out var call)
            && call.ToolName == "BashOutput"
            && FindShell(BackgroundTaskDetector.ShellIdOf(call.Input)) is { OutputPath: null } shell
            && completed.ResultText is { Length: > 0 } text)
        {
            shell.AppendOutput(text + "\n");
        }
    }

    /// <summary>
    /// Sessions whose runtime tracks its own tasks are asked for the list
    /// every couple of seconds: their detached shells announce themselves in no event,
    /// and a shell that exits or is killed elsewhere would otherwise stay "running".
    /// </summary>
    private async Task PollTasksAsync(IAgentSession session)
    {
        var everSeen = false;
        var idleDelayMs = 2000;
        var failures = 0;

        while (ReferenceEquals(_session, session))
        {
            try
            {
                // The collection belongs to the UI thread; read it there.
                var watchedNow = await OnUiAsync(() => RunningTasks
                    .Where(t => t.IsPeeked && t.OutputPath is null && t.RuntimeId is not null)
                    .ToList()) ?? [];

                // Quicker while someone is watching a log.
                await Task.Delay(watchedNow.Count > 0 ? 1000 : idleDelayMs);

                // SessionEnded clears IsConnected for good: a dead session has nothing to list.
                if (!ReferenceEquals(_session, session) || !IsConnected)
                {
                    break;
                }

                var list = await session.ListTasksAsync();

                // Shells with no log file (PTY-attached) only expose a rolling tail.
                foreach (var watched in watchedNow)
                {
                    if (await session.GetTaskOutputAsync(watched.RuntimeId!) is { } snapshot)
                    {
                        _dispatcher.TryEnqueue(() => watched.SetSnapshot(snapshot));
                    }
                }

                failures = 0;

                // Nothing tracked, nothing ever tracked: back the idle probe off to 8s so a
                // session that never starts anything stops costing two calls a second for
                // the tab's whole life. Discovery latency of a few seconds is fine for a
                // detached shell announcing itself.
                if (list.Count == 0 && !everSeen)
                {
                    idleDelayMs = Math.Min(idleDelayMs * 2, 8000);
                    continue;
                }

                everSeen = true;
                idleDelayMs = 2000;
                _dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_session, session))
                    {
                        ApplyRuntimeTasks(list);
                    }
                });
            }
            catch (Exception ex)
            {
                // The session was dying under the call, or the CLI stopped answering: a few
                // failures in a row end the poll rather than spin on a dead pipe.
                CrashLog.Debug("tasks", $"task poll failed: {ex.Message}");
                if (++failures >= 5)
                {
                    CrashLog.Warn("tasks", "task poll stopped after repeated failures");
                    break;
                }
            }
        }
    }

    private Task<T> OnUiAsync<T>(Func<T> read)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                done.SetResult(read());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });

        if (!queued)
        {
            done.SetResult(default!);
        }

        return done.Task;
    }

    private void ApplyRuntimeTasks(IReadOnlyList<AgentTaskInfo> list)
    {
        var listed = new HashSet<string>();

        foreach (var info in list)
        {
            var task = RunningTasks.FirstOrDefault(t => t.RuntimeId == info.Id)
                ?? (info.ToolCallId is { } call && _tasksByToolUse.TryGetValue(call, out var byCall) ? byCall : null);

            if (task is null)
            {
                var kind = info.Kind == AgentTaskKind.Shell ? RunningTaskKind.Shell : RunningTaskKind.Subagent;
                var trimmedTitle = info.Title.Trim();
                task = RunningTasks.FirstOrDefault(t => t.IsRunning && t.Kind == kind && t.Title == trimmedTitle && t.RuntimeId is null);
            }

            if (task is null)
            {
                task = new RunningTaskItem
                {
                    ToolUseId = info.ToolCallId ?? $"runtime:{info.Id}",
                    Kind = info.Kind == AgentTaskKind.Shell ? RunningTaskKind.Shell : RunningTaskKind.Subagent,
                    Title = info.Title.Trim(),
                    StartedAt = info.StartedAt,
                };
                if (task.IsShell)
                {
                    task.Header = RunningTaskItem.CommandHeader(task.Title);
                }

                AddTask(task);
            }

            listed.Add(info.Id);
            task.RuntimeId = info.Id;
            task.Pid = info.Pid;
            task.ShellId ??= info.Kind == AgentTaskKind.Shell ? info.Id : null;
            task.OutputPath ??= info.LogPath;

            task.Status = info.State switch
            {
                AgentTaskState.Running => RunningTaskStatus.Running,
                AgentTaskState.Completed => RunningTaskStatus.Completed,
                AgentTaskState.Failed => RunningTaskStatus.Failed,
                _ => RunningTaskStatus.Ended,
            };

            if (info.Kind == AgentTaskKind.Subagent && info.LatestText is { Length: > 0 } text && task.LastRuntimeText != text)
            {
                task.LastRuntimeText = text;
                task.AppendOutput("\n" + text);
                task.AddText(text);
            }
        }

        // A task the runtime no longer lists has been removed or has exited.
        foreach (var gone in RunningTasks.Where(t => t.IsRunning && t.RuntimeId is { } id && !listed.Contains(id)))
        {
            gone.Status = RunningTaskStatus.Ended;
        }
    }

    /// <summary>
    /// Stops a task through the runtime; if it refuses, kills the shell's process tree
    /// directly. Background shells outlive Stop (which only ends the turn), so this is
    /// the way to end a dev server without asking the agent to.
    /// </summary>
    public async Task StopTaskAsync(RunningTaskItem task)
    {
        // A task Codale runs itself: kill the tree, the sync tick settles the status.
        if (task.Hosted is { } hosted)
        {
            task.IsStopping = true;
            await Task.Run(hosted.Stop);
            task.IsStopping = false;
            SyncHostedTasks();
            return;
        }

        if (task.Cancel is { } cancel)
        {
            cancel();
            return;
        }

        if (task.RuntimeId is not { } id || _session is not { } session || !task.IsRunning)
        {
            return;
        }

        task.IsStopping = true;
        var stopped = await session.CancelTaskAsync(id);

        if (!stopped && task.Pid is { } pid)
        {
            stopped = KillTaskProcess(pid, task.StartedAt);
        }

        if (stopped)
        {
            task.Status = RunningTaskStatus.Ended;
        }

        task.IsStopping = false;
    }

    /// <summary>
    /// Kills the process a task was listed with. Pids are recycled, so a process that
    /// started after the task did is somebody else's and is left alone. True when the
    /// task's process is gone afterwards.
    /// </summary>
    private static bool KillTaskProcess(int pid, DateTimeOffset taskStartedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            // Slack for the CLI's clock against the process creation time.
            if (process.StartTime > taskStartedAt.LocalDateTime.AddSeconds(30))
            {
                return true;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            // Already gone: that is the outcome asked for.
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private RunningTaskItem? FindShell(string? shellId) =>
        shellId is null ? null : RunningTasks.FirstOrDefault(t => t.IsShell && t.ShellId == shellId);

    private void AddTask(RunningTaskItem task)
    {
        _tasksByToolUse[task.ToolUseId] = task;
        RunningTasks.Add(task);
        WatchTask(task);
        NotifySubagents();
    }

    /// <summary>Whatever was still running has lost its process: mark it ended.</summary>
    private void EndAllTasks()
    {
        foreach (var t in RunningTasks.Where(t => t.IsRunning))
        {
            t.Status = RunningTaskStatus.Ended;
        }
    }

    private void ClearTasks()
    {
        // A dev server Codale is running is not part of the transcript being cleared:
        // its row stays until the process ends or the user stops it.
        foreach (var task in RunningTasks.Where(t => t.Hosted is null || !t.IsRunning).ToList())
        {
            RunningTasks.Remove(task);
            _tasksByToolUse.Remove(task.ToolUseId);
            if (ReferenceEquals(_liveTask, task))
            {
                _liveTask = null;
            }
        }

        _liveTask ??= RunningTasks.LastOrDefault(t => t.IsRunning && t.LastLine.Length > 0);
        TaskStatusLine = _liveTask is null ? "" : $"[Task {RunningTasks.IndexOf(_liveTask) + 1}] {_liveTask.LastLine}";
    }

    /// <summary>Plans this conversation produced - proposed in chat or written to disk - newest first.</summary>
    public ObservableCollection<SessionArtifact> Artifacts { get; } = [];

    /// <summary>
    /// A proposal took over a listed plan's entry (the plan-file takeover): raised so a
    /// view showing the old plan - the artifact tab - can follow the replacement.
    /// </summary>
    public event EventHandler<(SessionArtifact OldPlan, SessionArtifact NewPlan)>? PlanReplaced;

    /// <summary>Files staged in the composer for the next turn.</summary>
    public ObservableCollection<ComposerAttachment> Attachments { get; } = [];

    /// <summary>Rows for the composer's autocomplete popup, recomputed from the draft.</summary>
    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Slash commands both the Claude and Codex CLIs understand, with what each does.</summary>
    public static IReadOnlyList<(string Name, string Description)> SlashCommands { get; } =
    [
        ("clear", "Start a fresh conversation and clear the context"),
        ("compact", "Summarize the conversation to free up context"),
        ("diff", "Show the changes made in the working tree"),
        ("init", "Create an AGENTS.md / CLAUDE.md guide for this project"),
        ("mcp", "List the connected MCP servers and their tools"),
        ("model", "Choose the model for this session"),
        ("permissions", "View or change what the agent may do without asking"),
        ("resume", "Pick up a previous conversation"),
        ("review", "Review the current changes for problems"),
        ("status", "Show session, model and usage details"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskSummary))]
    public partial int TasksDone { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskSummary))]
    public partial int TasksTotal { get; set; }

    /// <summary>Progress line for the status bar, e.g. "2/5 tasks".</summary>
    public string TaskSummary => TasksTotal == 0 ? "no tasks" : $"{TasksDone}/{TasksTotal} tasks";

    /// <summary>What the agent says it is working on right now, for the status bar tooltip.</summary>
    public string? ActiveTask => Todos.FirstOrDefault(t => t.Status == TodoStatus.InProgress)?.Content;

    /// <summary>
    /// What the agent is doing this moment - "Thinking", "Running dotnet build",
    /// "Loading tools" - for the bottom status line. Null between steps.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    public partial string? CurrentActivity { get; set; }

    /// <summary>The call <see cref="CurrentActivity"/> describes, so its result can clear it.</summary>
    private string? _activityToolId;

    /// <summary>The status line's detail: the step in flight, else the task the agent says it is on.</summary>
    public string? StatusDetail => CurrentActivity is { Length: > 0 } activity ? activity : ActiveTask;

    private void SetActivity(string? activity, string? toolUseId)
    {
        CurrentActivity = activity is { Length: > 90 } ? activity[..90] + "…" : activity;
        _activityToolId = toolUseId;
    }

    /// <summary>Raised after every completed turn, so git and the diff can be refreshed.</summary>
    public event EventHandler? TurnFinished;

    /// <summary>
    /// Raised mid-turn whenever a tool reports a file change, so the file tree can
    /// pick up creations while the turn is still running - turn end would be too
    /// late to feel responsive.
    /// </summary>
    public event EventHandler? FilesChanged;

    /// <summary>Raised when the session id or running state changes, for persistence.</summary>
    public event EventHandler? SessionStateChanged;

    [ObservableProperty]
    public partial string Draft { get; set; } = "";

    /// <summary>
    /// Notice pinned above the composer (denials and the like): it must sit where the
    /// reader is looking, not at the bottom of the transcript. Null when dismissed;
    /// cleared on the next send.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComposerNotice))]
    public partial NoticeItem? ComposerNotice { get; set; }

    public bool HasComposerNotice => ComposerNotice is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusyWorking))]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    [NotifyPropertyChangedFor(nameof(CanChangeMode))]
    [NotifyPropertyChangedFor(nameof(HasConversation))]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// The agent CLI running in a terminal tab that this headless session stands for, or null for a real chat.
    /// It never connects to the Claude CLI and has no tab: it only holds what the CLI reports (tasks, artifacts,
    /// changes) so the panel can mirror it, with the model, effort, mode and provider choices locked.
    /// </summary>
    public Codale.Agents.CliDescriptor? TerminalCli { get; init; }

    /// <summary>True for a terminal CLI's session, which gets the simple panel.</summary>
    public bool IsTerminalSession => TerminalCli is not null;

    /// <summary>The model, effort, mode and provider are locked while a turn is running or for a terminal CLI's session.</summary>
    public bool CanChangeMode => !IsBusy && !IsTerminalSession;

    /// <summary>
    /// True once a CLI-reported model catalogue is in hand. The model and effort pickers
    /// stay disabled until then, so a click never shows the documented stand-in list
    /// instead of the models this CLI can actually run.
    /// </summary>
    public bool HasModelCatalog { get; private set; }

    /// <summary>The model and effort chips unlock when the session can take changes and the catalogue has landed.</summary>
    public bool CanPickModel => CanChangeMode;

    /// <summary>Running a turn without waiting on anyone: the tab's blue dot. Waiting shows amber instead.</summary>
    public bool IsBusyWorking => IsBusy && !IsWaitingForYou;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    public const string DefaultPlaceholder = "Ask anything…  / for commands, @ to mention a file";

    /// <summary>The CLI's guess at the next prompt; shown as the composer's placeholder until the user types or sends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowSuggestionHint))]
    public partial string? NextPromptSuggestion { get; set; }

    public string ComposerPlaceholder => NextPromptSuggestion is { Length: > 0 } s ? s : DefaultPlaceholder;

    /// <summary>The "Tab" cue beside a suggestion: only while the composer is empty, so typing hides it.</summary>
    public bool ShowSuggestionHint => NextPromptSuggestion is { Length: > 0 } && Draft.Length == 0;

    partial void OnDraftChanged(string value)
    {
        OnPropertyChanged(nameof(ShowSuggestionHint));

        // Automatic mode reads the message on send; starting the reader while the user
        // types takes its few seconds of start-up off the wait. A busy turn counts too:
        // the next message is often typed while the agent works and sent once it is done.
        if ((IsAutomaticMode || SmartHistory) && value.Length > 0 && !value.StartsWith('/') && Router is { } router)
        {
            router.Prewarm(Features);
        }
    }

    /// <summary>Puts the suggestion into the composer for editing or sending; false when there is none or the user already typed.</summary>
    public bool AcceptSuggestion()
    {
        if (NextPromptSuggestion is not { Length: > 0 } suggestion || Draft.Length > 0)
        {
            return false;
        }

        Draft = suggestion;
        NextPromptSuggestion = null;
        return true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeDisplayText))]
    public partial string PermissionMode { get; set; } = AppSettings.DefaultChatMode;

    /// <summary>The mode a fresh chat starts in when Settings does not say otherwise: Auto, which is plain Full without an agent CLI for the helper model.</summary>
    public const string DefaultMode = AutomaticMode;

    /// <summary>
    /// The host's own mode: the agent explains and changes nothing. No CLI has it, so
    /// the session runs in <see cref="CliMode"/>'s "manual" - every acting tool comes to
    /// the host for approval - and the host denies each one; turns carry the
    /// explain-only instruction (<see cref="UserTurn.AskOnly"/>).
    /// </summary>
    public const string AskMode = "ask";

    public bool IsAskMode => PermissionMode == AskMode;

    /// <summary>
    /// The host's routed mode: each message is classified by the helper model, sent as an
    /// ask, plan or act turn as it reads, and moved to a new session when it changes
    /// the subject. Between turns the CLI runs "auto", which act turns use as-is.
    /// </summary>
    // NOTE: Auto is Claude's own auto mode (--permission-mode auto): the model evaluates each
    // action's risk and only asks or denies the risky ones. This is already handled; do not
    // auto-approve in the host when the CLI reports "default" - that would bypass the risk check
    // (see RidesOn and CliMode).
    public const string AutomaticMode = "automatic";

    public bool IsAutomaticMode => PermissionMode == AutomaticMode;

    /// <summary>The permission mode the CLI last reported at init; "default" under Auto means it has no classifier.</summary>
    private string? _cliReportedMode;

    /// <summary>Modes only the host knows; the CLI is handed <see cref="CliMode"/> instead.</summary>
    public static bool IsHostMode(string mode) => mode is AskMode or AutomaticMode;

    /// <summary>
    /// What to hand the CLI for a host mode: ask runs on "manual", automatic on "auto", and
    /// Full (stored as "auto" or "dontAsk") on "bypassPermissions" - Claude's own "auto" is a
    /// classifier that can still deny or ask, which Full must never do.
    /// </summary>
    public static string CliMode(string mode) => ClaudeSessionOptions.ClampPermissionMode(mode switch
    {
        AskMode => "manual",
        AutomaticMode => "auto",
        "auto" or "dontAsk" => "bypassPermissions",
        _ => mode,
    });

    /// <summary>True when the CLI reporting <paramref name="reported"/> is just the CLI half of host mode <paramref name="hostMode"/>.</summary>
    private static bool RidesOn(string hostMode, string reported) => hostMode switch
    {
        AskMode => reported is "manual" or "default",
        // A CLI that does not offer its "auto" classifier starts in "default" instead; Auto is the host's routing either way.
        AutomaticMode => reported is "auto" or "default",
        "auto" or "dontAsk" => reported is "bypassPermissions" or "dontAsk",
        _ => false,
    };

    /// <summary>Picks each message's mode and session in <see cref="AutomaticMode"/>; null sends every message as a plain turn.</summary>
    public MessageRouter? Router { get; init; }

    /// <summary>
    /// The background-task model, which names sessions and writes summaries. Null turns those off.
    /// </summary>
    public IHelperModel? Helper { get; init; }

    /// <summary>The helper model is reading the message before it goes out.</summary>
    [ObservableProperty]
    public partial bool IsRouting { get; set; }

    /// <summary>
    /// Automatic mode read the message as a new subject: the workspace opens a fresh
    /// session and sends it there through <see cref="SendRoutedAsync"/>.
    /// </summary>
    public event EventHandler<RoutedMessage>? NewTopicRequested;

    /// <summary>The chip label for the raw CLI mode vocabulary.</summary>
    public string ModeDisplayText => ModeDisplay(PermissionMode);

    /// <summary>Human words for the CLI's mode names; unknown ids stay as they are.</summary>
    public static string ModeDisplay(string mode) => mode switch
    {
        AskMode => "Ask",
        AutomaticMode => "Auto",
        "" => "Default",
        "plan" => "Plan",
        "manual" or "default" => "Interactive",
        "acceptEdits" => "Accept edits",
        "auto" or "dontAsk" or "bypassPermissions" => "Full",
        _ => mode,
    };

    /// <summary>The mode chip's icon: a speech bubble, a lightbulb, a map, a hand, a pencil, a bolt.</summary>
    public string ModeGlyph => PermissionMode switch
    {
        AskMode => "\uE8BD",
        AutomaticMode => "\uE82F",
        "" => "",
        "plan" => "\uE707",
        "manual" or "default" => "\uE7C9",
        "acceptEdits" => "\uE70F",
        _ => "\uE945",
    };

    partial void OnPermissionModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAskMode));
        OnPropertyChanged(nameof(IsAutomaticMode));
        OnPropertyChanged(nameof(ModeGlyph));
        OnPropertyChanged(nameof(ModeDisplayText));
    }

    /// <summary>
    /// The user picked a permission mode. A connected session switches live over the
    /// CLI's control channel, the same switch plan mode uses; anything else (not
    /// connected yet, a CLI without the switch) keeps the pick for the next spawn, where
    /// <see cref="ConnectAsync"/> passes it and the session's own report confirms it.
    /// </summary>
    public async Task SetPermissionModeAsync(string mode)
    {
        mode = IsHostMode(mode) ? mode : ClaudeSessionOptions.ClampPermissionMode(mode);
        if (mode == PermissionMode)
        {
            return;
        }

        // Only the CLI half needs a switch: leaving ask mode for manual, or automatic
        // for auto, or the reverse, is purely the host's business.
        var cliMode = CliMode(mode);
        if (IsConnected && _session is { } session && cliMode != CliMode(PermissionMode))
        {
            await session.TrySetPermissionModeAsync(cliMode);
        }

        PermissionMode = mode;
    }

    /// <summary>
    /// Model the user picked in the session card: an alias or a full id. Null hands the
    /// choice to the CLI's own default. Passed to the CLI on every connect.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelDisplay))]
    [NotifyPropertyChangedFor(nameof(CurrentTier))]
    public partial string? RequestedModel { get; set; }

    /// <summary>
    /// Reasoning effort the user picked. Null means the CLI default; the CLI only
    /// accepts its own vocabulary, so connects clamp through the option records.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTier))]
    public partial string? RequestedEffort { get; set; }

    /// <summary>
    /// The model chip: the tier the user picked, else what the CLI reported it connected
    /// with - the CLI's own pick is not guessed before it connects.
    /// </summary>
    public string ModelDisplay => TierTitle(CurrentTier);

    /// <summary>
    /// The model or effort to hand the CLI: the pick, else null so the CLI uses its own
    /// default (including a settings.json model or effortLevel).
    /// </summary>
    private static string? LaunchValue(string? requested) =>
        requested is { Length: > 0 } ? requested : null;

    /// <summary>Records the user's permission-mode pick as this CLI's new default.</summary>
    public void RememberModeAsDefault(string mode) => AppSettings.SetDefaultMode(CliId, mode);

    /// <summary>Models the session reports, for the session card's picker.</summary>
    public IReadOnlyList<AgentModelInfo> AvailableModels { get; private set; } = [];

    /// <summary>
    /// The model picker's tiers. Lite and Flash run the fast ("flash") model, Pro and
    /// Plus the capable ("pro") one, each at its tier's effort; CLI hands the choice to
    /// the CLI. Which model plays flash and pro comes from settings, guessed from the
    /// catalogue when unset.
    /// </summary>
    public static readonly string[] ModelTiers = ["cli", "lite", "flash", "pro", "plus"];

    public static string TierTitle(string tier) => tier switch
    {
        "lite" => "Lite",
        "flash" => "Flash",
        "pro" => "Pro",
        "plus" => "Plus",
        _ => "CLI",
    };

    /// <summary>The model id playing the fast tier for this CLI: the setting, else a guess from the catalogue.</summary>
    public string? FlashModel => ModelForTier("flash");

    /// <summary>The model id playing the capable tier for this CLI: the setting, else a guess from the catalogue.</summary>
    public string? ProModel => ModelForTier("pro");

    private string? ModelForTier(string tier)
    {
        // A BYOK provider serves only its own flash / pro models: the CLI's tier settings and
        // catalogue name models it rejects ("Unknown Model"). Pro falls back to flash.
        if (_endpointSettings?.IsActive(EndpointName, openAiCompatible: IsCodexSession) == true)
        {
            var models = EndpointModels();
            var pick = tier == "pro" ? models.FirstOrDefault(m => !m.IsDefault) : null;
            return (pick ?? models.FirstOrDefault())?.Id;
        }

        if (AppSettings.TierModelFor(CliId, tier) is { Length: > 0 } configured)
        {
            return configured;
        }

        string[] fastHints = ["flash", "lite", "mini", "haiku", "luna", "small"];
        string[] proHints = ["pro", "max", "opus", "ultra", "big"];
        var hints = tier == "flash" ? fastHints : proHints;

        static string Name(AgentModelInfo model) => $"{model.Id} {model.DisplayName}";
        var hinted = AvailableModels.FirstOrDefault(model => hints.Any(hint => Name(model).Contains(hint, StringComparison.OrdinalIgnoreCase)));
        if (hinted is not null)
        {
            return hinted.Id;
        }

        // No name hints: flash stays on the CLI's own model; pro takes the catalogue's default.
        return tier == "pro"
            ? (AvailableModels.FirstOrDefault(m => m.IsDefault) ?? AvailableModels.FirstOrDefault())?.Id
            : null;
    }

    /// <summary>The (model, effort) a tier hands the CLI. Null model keeps the CLI's own pick.</summary>
    public (string? Model, string? Effort) TierConfig(string tier) => tier switch
    {
        "lite" => (FlashModel, "low"),
        "flash" => (FlashModel, "medium"),
        "pro" => (ProModel, "medium"),
        "plus" => (ProModel, "high"),
        _ => (null, null),
    };

    /// <summary>The tier matching the current model/effort pick, or "cli" when none does.</summary>
    public string CurrentTier
    {
        get
        {
            foreach (var tier in ModelTiers)
            {
                var (model, effort) = TierConfig(tier);
                if (string.Equals(model, RequestedModel, StringComparison.OrdinalIgnoreCase) && effort == RequestedEffort)
                {
                    return tier;
                }
            }

            return "cli";
        }
    }

    /// <summary>
    /// Applies a tier pick: the model/effort it resolves to goes to the running session
    /// live when it can, a restart when it cannot, and it becomes this CLI's new default.
    /// </summary>
    public async Task ApplyTierAsync(string tier)
    {
        var (model, effort) = TierConfig(tier);
        if (RequestedModel == model && RequestedEffort == effort)
        {
            return;
        }

        RequestedModel = model;
        RequestedEffort = effort;
        AppSettings.SetDefaultTier(CliId, tier);

        if (!IsConnected)
        {
            CrashLog.Trace($"Session config queued: tier={tier} (nothing running)");
            return;
        }

        if (await TryApplyModelEffortLiveAsync(model, effort))
        {
            return;
        }

        await RestartWithCurrentConfigAsync();
    }

    /// <summary>Everything the chips show that depends on the catalogue or the model/effort pick.</summary>
    private void NotifyModelDisplays()
    {
        OnPropertyChanged(nameof(ModelDisplay));
        OnPropertyChanged(nameof(CurrentTier));
        OnPropertyChanged(nameof(FlashModel));
        OnPropertyChanged(nameof(ProModel));
    }

    partial void OnRequestedModelChanged(string? value) => NotifyModelDisplays();

    partial void OnRequestedEffortChanged(string? value) => OnPropertyChanged(nameof(ModelDisplay));

    /// <summary>
    /// The catalogues the CLIs reported, fetched once per CLI run and reused: a new chat
    /// starts from its CLI's list, so the picker is populated the moment the tab appears
    /// instead of after the CLI has connected and answered; the live answer then replaces
    /// it only if it differs. Also feeds the settings page's tier-model pickers.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<AgentModelInfo>> CachedModels = new(StringComparer.Ordinal);

    /// <summary>The catalogue <paramref name="cliId"/> reported this run, or null before its first chat connected.</summary>
    public static IReadOnlyList<AgentModelInfo>? KnownCatalogue(string cliId) =>
        CachedModels.TryGetValue(cliId, out var models) ? models : null;

    /// <summary>Drops <paramref name="cliId"/>'s cached catalogue: the account changed, and with it the plan's models.</summary>
    public static void ForgetCatalogue(string cliId) => CachedModels.Remove(cliId);

    /// <summary>
    /// Asks the CLI itself for its model list by starting a throwaway session, caches the
    /// answer and returns it. Empty when the CLI is missing, signed out or too old to say.
    /// </summary>
    public static async Task<IReadOnlyList<AgentModelInfo>> FetchCatalogueAsync(string cliId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IAgentSession? session = null;
        try
        {
            // A signed-out CLI still answers with a generic list that ignores the plan; caching
            // it would keep offering models the account cannot use after the login.
            if ((cliId == "codex" || !EnvironmentOverridesEndpoint()) && !await CodaleAccountCli.IsSignedInAsync(cliId))
            {
                CachedModels.Remove(cliId);
                CrashLog.Trace($"Model list fetch for {cliId}: skipped, Codale's account is signed out");
                return [];
            }

            session = cliId == "codex"
                ? new Codale.Agents.Codex.CodexAgentSession(new Codale.Agents.Codex.CodexSessionOptions { WorkingDirectory = home })
                : new ClaudeAgentSession(new ClaudeSessionOptions { WorkingDirectory = home });
            await session.StartAsync();
            // StartAsync returns once the CLI answered its handshake; it can still be loading
            // its catalogue, so ask again until it answers instead of giving up on one reply.
            IReadOnlyList<AgentModelInfo> models = [];
            for (var attempt = 0; attempt < 4 && models.Count == 0; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(2000);
                }

                models = await session.ListModelsAsync();
            }

            if (models.Count > 0)
            {
                CachedModels[cliId] = models;
            }
            else
            {
                CrashLog.Trace($"Model list fetch for {cliId}: the CLI returned none");
            }

            return models;
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Model list fetch for {cliId} failed: {ex.Message}");
            return [];
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The CLI answered that it has no credentials ("Not logged in", "401 Unauthorized", etc.).
    /// Logging in is interactive, so the page offers a terminal for it.
    /// </summary>
    public event EventHandler<string?>? LoginRequired;

    /// <summary>
    /// The conversation needs the reader: a question or permission is open, or the turn
    /// ended. The page decides whether that is worth a taskbar flash and a notification
    /// (it is when the window is not in front).
    /// </summary>
    public event EventHandler<AttentionRequest>? AttentionNeeded;

    private void CheckLoginRequired(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Please run /login", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("codex login", StringComparison.OrdinalIgnoreCase) ||
            (IsCodexSession && (text.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("Missing bearer or basic authentication", StringComparison.OrdinalIgnoreCase))))
        {
            LoginRequired?.Invoke(this, IsCodexSession ? "Codex" : ProviderName);
        }
    }

    public string ProviderName => TerminalCli?.DisplayName ?? SessionCli.DisplayName;

    /// <summary>Provider-assigned id of the live session, once it has started.</summary>
    public string? SessionId => _session?.SessionId;

    /// <summary>
    /// Resume an existing conversation instead of starting a new one. Set before
    /// <see cref="ConnectAsync"/>; this is what crash recovery uses.
    /// </summary>
    public string? ResumeSessionId
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(HasConversation));
        }
    }

    /// <summary>Working directory for the agent: the project, or a session worktree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIsolated))]
    public partial string? WorktreePath { get; set; }

    /// <summary>True while the agent works in its own worktree rather than the project.</summary>
    public bool IsIsolated => WorktreePath is { Length: > 0 };

    /// <summary>
    /// Standing instruction appended to every Claude session's system prompt: the task
    /// panel and the "N/M tasks" counter can only show steps the CLI emits as task
    /// tools, and models don't volunteer them - so every session is told to plan its
    /// work out loud before starting it. System prompt, not a per-turn prefix, so the
    /// conversation the user reads back stays clean.
    /// </summary>
    /// <remarks>
    /// It must never be able to block the work: an earlier wording ("create them ...
    /// before running any other tool") left a model on a CLI without either task tool
    /// searching for them over and over, never starting the actual request. So the
    /// list is conditional on the tool existing, the lookup is capped at one search,
    /// and the fallback is simply to get on with it.
    /// </remarks>
    public const string AlwaysPlanDirective =
        "When a request needs tools, first break it into 2-6 short, concrete steps, e.g. for \"commit all " +
        "changes\": \"Check git status\", \"Draft the commit message\", \"Commit the changes\". Requests that need no tools need no list.";

    /// <summary>True while <see cref="ConnectAsync"/> runs; a second call would spawn a second CLI and orphan the first.</summary>
    private bool _connecting;

    /// <summary>The BYOK provider the live session was spawned with ("" for Default); null before the first connect.</summary>
    private string? _connectedProvider;

    /// <summary>
    /// The BYOK provider this chat uses ("" for Default). Each chat has its own; a new one starts
    /// on the provider last picked. A change reaches the CLI through <see cref="SetEndpointAsync"/>.
    /// </summary>
    public string EndpointName { get; private set; } = "";

    private string SelectedProviderName() => AppSettings.FindByok(EndpointName)?.Name.Trim() ?? "";

    public async Task ConnectAsync()
    {
        if (_connecting || (IsConnected && _session is not null))
        {
            CrashLog.Trace("Connect skipped: a session is already starting or running");
            return;
        }

        _connecting = true;
        try
        {
            await ConnectCoreAsync();
        }
        finally
        {
            _connecting = false;
        }
    }

    private async Task ConnectCoreAsync()
    {
        // Never leave the mode to the CLI's own default (it asks about every edit and run):
        // an unset mode is Auto, so --permission-mode auto is always spelled out for it.
        if (PermissionMode.Length == 0)
        {
            PermissionMode = DefaultMode;
        }

        var permissionMode = CliMode(PermissionMode);
        if (permissionMode != PermissionMode && !IsHostMode(PermissionMode))
        {
            CrashLog.Trace($"Permission mode '{PermissionMode}' is not a Claude mode; starting with '{permissionMode}'");
            PermissionMode = permissionMode;
        }

        // With a custom endpoint the catalogue's Anthropic ids mean nothing to the
        // gateway (it answers "issue with the selected model"): the picker offers the
        // endpoint's own default / pro model and a session starts with the default.
        // Codex talks to the OpenAI-compatible base URL, Claude to the Anthropic one.
        IReadOnlyDictionary<string, string>? endpointEnv;
        try
        {
            endpointEnv = IsCodexSession ? CodexEndpointEnvironment() : ClaudeEndpointEnvironment();
        }
        catch (InvalidOperationException ex)
        {
            // The provider cannot serve this CLI (e.g. OpenAI-only for Claude): say so rather than fall back to a login.
            CrashLog.Error("chat", "Connect FAILED: no endpoint", ex);
            Add(new NoticeItem { Text = $"Could not start {ProviderName}: {ex.Message}", Severity = NoticeSeverity.Error });
            return;
        }

        // Codale's own account must be signed in before the CLI is started; a provider or an
        // ANTHROPIC_* override brings its own credentials and needs no login.
        var cliId = IsCodexSession ? "codex" : "claude";
        if (endpointEnv is null && (IsCodexSession || !EnvironmentOverridesEndpoint()) &&
            !await CodaleAccountCli.IsSignedInAsync(cliId))
        {
            CrashLog.Trace($"Connect: Codale's {cliId} account is signed out");
            Add(new NoticeItem { Text = $"Codale isn't signed in to {ProviderName}. Sign in, then resend your message.", Severity = NoticeSeverity.Warning });
            LoginRequired?.Invoke(this, IsCodexSession ? "Codex" : ProviderName);
            return;
        }

        _connectedProvider = SelectedProviderName();
        _status.IsCustomProvider = endpointEnv is not null;
        RefreshCustomProviderName();
        var endpointModels = endpointEnv is not null ? EndpointModels() : [];
        if (endpointModels.Count > 0)
        {
            AvailableModels = endpointModels;
            HasModelCatalog = endpointModels.Count > 0;
            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(HasModelCatalog));
            OnPropertyChanged(nameof(CanPickModel));
            NotifyModelDisplays();
        }

        var model = endpointModels.Count > 0 || endpointEnv is not null
            ? EndpointModelFor(RequestedModel, endpointModels)
            : LaunchValue(RequestedModel);

        // A stale Claude alias (sonnet, opus...) the provider does not serve is replaced by the
        // model it maps to, so the picker shows what the session really runs.
        if (endpointModels.Count > 0 && model is not null && RequestedModel is { Length: > 0 } && RequestedModel != model)
        {
            RequestedModel = model;
        }

        // A custom endpoint's models are unknown to the effort tables, so they get only a pick.
        var effort = IsCodexSession
            ? Codale.Agents.Codex.CodexSessionOptions.ClampEffort(endpointEnv is not null ? RequestedEffort : LaunchValue(RequestedEffort))
            : ClaudeSessionOptions.ClampEffort(endpointEnv is not null ? RequestedEffort : LaunchValue(RequestedEffort));

        CrashLog.Trace(
            $"Connect: provider={ProviderName} model={model ?? "default"} effort={effort ?? "default"} " +
            $"mode={permissionMode} resume={ResumeSessionId ?? "none"} worktree={WorktreePath ?? "project"}");

        _liveEffort = effort;
        _workspaceFiles = null;

        IAgentSession? starting = null;
        try
        {
            var workingDirectory = WorktreePath ?? _projectPath;
            IReadOnlyList<McpServerSpec> mcpServers = _mcpSettings is null ? [] : await Task.Run(_mcpSettings.Servers);

            // Long-running commands go through Codale's own task server; only when it
            // shipped are the agent's background shells refused (there'd be no other way).
            string? shellGuard = null;
            var environment = endpointEnv is null ? new Dictionary<string, string>() : new Dictionary<string, string>(endpointEnv);
            var tasksHost = _mcpSettings is { TasksAvailable: true } ? EnsureTaskHost(workingDirectory) : null;
            if (tasksHost is { } host &&
                _mcpSettings?.TasksSpec(host.Pipe, host.Token, workingDirectory) is { } tasksSpec)
            {
                mcpServers = [.. mcpServers, tasksSpec];
                shellGuard = _mcpSettings.TasksGuardCommand();
            }

            _sessionMcpServers = mcpServers;

            if (endpointEnv is not null)
            {
                CrashLog.Trace($"Connect: endpoint override active for {ProviderName}");
            }

            var activeCli = SessionCli;
            var cliExecutable = activeCli.ResolvedPath ?? activeCli.Executable;
            var cliName = $"{activeCli.DisplayName} CLI";
            if (_status.CliName != cliName)
            {
                // Context and subscription windows belong to the CLI that reported them;
                // switching Claude <-> Codex must not carry the other one's readings over.
                _status.ResetCliReadings();
            }

            _status.CliName = cliName;

            IAgentSession session = starting = IsCodexSession
                ? new Codale.Agents.Codex.CodexAgentSession(new Codale.Agents.Codex.CodexSessionOptions
                {
                    WorkingDirectory = workingDirectory,
                    Executable = cliExecutable,
                    Model = model,
                    ContextWindow = _endpointSettings?.ContextWindowFor(EndpointName, model),
                    Effort = effort,
                    PermissionMode = permissionMode,
                    McpServers = mcpServers,
                    SystemPromptAppend = mcpServers.Count > 0 ? WithMcpDirective("", mcpServers).Trim() : null,
                    ResumeSessionId = ResumeSessionId,
                    RawLogPath = CrashLog.NewRawLogPath($"{activeCli.Id}-{ResumeSessionId?[..8] ?? "new"}"),
                    Environment = environment.Count == 0 ? null : environment,
                    CustomProvider = _endpointSettings?.CodexProvider(EndpointName),
                })
                : new ClaudeAgentSession(new ClaudeSessionOptions
                {
                    WorkingDirectory = workingDirectory,
                    Executable = cliExecutable,
                    AdditionalDirectories = ExtraFolders?.Folders ?? [],
                    Model = model,
                    Effort = effort,
                    PermissionMode = permissionMode,
                    SystemPromptAppend = WithMcpDirective(AlwaysPlanDirective, mcpServers),
                    McpServers = mcpServers,
                    BackgroundShellGuardCommand = shellGuard,
                    DisallowedTools = shellGuard is not null ? McpServerSettings.NativeTodoTools : null,
                    PromptSuggestions = AppSettings.SuggestNextPrompt,
                    SessionId = ResumeSessionId is null ? Guid.NewGuid() : null,
                    ResumeSessionId = ResumeSessionId,
                    RawLogPath = CrashLog.NewRawLogPath($"{activeCli.Id}-{ResumeSessionId?[..8] ?? "new"}"),
                    Environment = environment.Count == 0 ? null : environment,
                });

            _session = session;

            await session.StartAsync();

            IsConnected = true;

            SessionStateChanged?.Invoke(this, EventArgs.Empty);
            CrashLog.Trace($"Connect ok: session={session.SessionId ?? "unknown"}");

            RunInBackground(() => PumpAsync(session), "event pump");
            RunInBackground(() => PollTasksAsync(session), "task poll");

            // Usage otherwise arrives only with a turn's rate_limit_event; a BYOK endpoint or
            // an inherited ANTHROPIC_* override has no Claude subscription behind it.
            // ANTHROPIC_* / Bedrock / Vertex variables redirect the Claude CLI only; Codex is
            // overridden solely by its own configured provider (endpointEnv).
            var overrideCli = endpointEnv is not null || (!IsCodexSession && EnvironmentOverridesEndpoint());
            _status.IsOverrideCli = overrideCli;
            // The usage endpoint is Claude's; Codex reports its own windows through its session.
            if (!overrideCli && !IsCodexSession)
            {
                RunInBackground(LoadUsageAsync, "usage fetch");
            }
        }
        catch (Exception ex)
        {
            // The CLI has no stored conversation for the id (the session died before its first
            // turn was saved, or ran under another provider): start a fresh one instead.
            var resumeLost = ResumeSessionId is not null &&
                (ex.Message.Contains("No conversation found with session ID", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("no rollout found", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("thread not found", StringComparison.OrdinalIgnoreCase) ||
                 (ex.Message.Contains("thread", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)));

            CrashLog.Error("chat", "Connect FAILED", ex);
            if (!resumeLost)
            {
                Add(new NoticeItem { Text = $"Could not start {ProviderName}: {ex.Message}", Severity = NoticeSeverity.Error });
                CheckLoginRequired(ex.Message);
            }

            // A session that never started is not this chat's session: leaving it in place
            // would make the chat look connected-less but unreplaceable, and keep its process.
            if (ReferenceEquals(_session, starting))
            {
                _session = null;
            }

            if (starting is not null)
            {
                try
                {
                    await starting.DisposeAsync();
                }
                catch (Exception disposeError)
                {
                    CrashLog.Debug("chat", $"disposing the failed session threw: {disposeError.Message}");
                }
            }

            if (resumeLost)
            {
                ResumeSessionId = null;
                Add(new NoticeItem { Text = "The previous CLI conversation could not be resumed; started a new one.", Severity = NoticeSeverity.Info });
                await ConnectCoreAsync();
            }
        }
    }

    /// <summary>Shows the subscription usage as soon as the chat is open, before any message.</summary>
    private async Task LoadUsageAsync()
    {
        if (await ClaudeUsageClient.FetchAsync().ConfigureAwait(false) is { } limits)
        {
            _dispatcher.TryEnqueue(() => _status.Apply(limits));
        }
    }

    /// <summary>Starts long-lived work off the UI thread; a failure is logged, never left unobserved.</summary>
    private static void RunInBackground(Func<Task> work, string what)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                CrashLog.Error("chat", $"{what} failed", ex);
            }
        });
    }

    /// <summary>Whether the running session has the large-file read guard, whose refusals are redirects.</summary>

    /// <summary>The MCP servers the running session was started with; their auto-approved tools skip the prompt outside Manual.</summary>
    private IReadOnlyList<McpServerSpec> _sessionMcpServers = [];

    /// <summary>
    /// A saved pick such as "sonnet" or "claude-opus-5-5" means nothing to a custom provider:
    /// opus / fable become its pro model, anything else not on its list its default model.
    /// </summary>
    internal static string? EndpointModelFor(string? requested, IReadOnlyList<AgentModelInfo> endpointModels)
    {
        if (endpointModels.Count == 0)
        {
            return string.IsNullOrEmpty(requested) ? null : requested;
        }

        if (!string.IsNullOrEmpty(requested) && endpointModels.Any(m => m.Id == requested))
        {
            return requested;
        }

        var smart = requested is not null &&
            (requested.Contains("opus", StringComparison.OrdinalIgnoreCase) || requested.Contains("fable", StringComparison.OrdinalIgnoreCase));
        var fallback = smart ? endpointModels.FirstOrDefault(m => !m.IsDefault) : null;
        return (fallback ?? endpointModels.FirstOrDefault(m => m.IsDefault) ?? endpointModels[0]).Id;
    }

    /// <summary>The endpoint's default and pro model as a catalogue; empty when it is off or has no names.</summary>
    private IReadOnlyList<AgentModelInfo> EndpointModels() => _endpointSettings?.Models(EndpointName) ?? [];

    /// <summary>The endpoint as Claude's spawn environment, or null when it is off.</summary>
    private IReadOnlyDictionary<string, string>? ClaudeEndpointEnvironment() => _endpointSettings?.ClaudeEnvironment(EndpointName);

    private IReadOnlyDictionary<string, string>? CodexEndpointEnvironment() => _endpointSettings?.CodexEnvironment(EndpointName);

    /// <summary>
    /// True when the ambient environment points the CLI away from Anthropic - an
    /// ANTHROPIC_BASE_URL (or an auth override) set outside Codale, e.g. a Z.ai-style
    /// session override CLI. The spawn environment Codale builds never contains them,
    /// so this reads only what the app itself was launched with.
    /// </summary>
    private static bool EnvironmentOverridesEndpoint()
    {
        static bool Flag(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

        return Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 }
            || Flag("CLAUDE_CODE_USE_BEDROCK")
            || Flag("CLAUDE_CODE_USE_VERTEX");
    }

    /// <summary>The standing instruction followed by the enabled MCP servers' usage note, when there is one.</summary>
    private string WithMcpDirective(string directive, IReadOnlyList<McpServerSpec> servers)
    {
        var text = servers.Count > 0 && _mcpSettings?.Directive(servers) is { Length: > 0 } note ? $"{directive} {note}" : directive;
        return servers.Any(s => s.Name == McpServerSettings.TasksName) ? $"{text} {McpServerSettings.TasksDirective}" : text;
    }

    /// <summary>
    /// Pumps one session's events onto the transcript. Scoped to the session instance:
    /// once it has been replaced (resume, isolated restart), its
    /// trailing events - the killed process's SessionEnded, say - must not touch the
    /// new session's transcript or state, so the pump walks away instead.
    /// </summary>
    private async Task PumpAsync(IAgentSession session)
    {
        await foreach (var e in session.Events.ReadAllAsync())
        {
            if (!ReferenceEquals(_session, session))
            {
                CrashLog.Trace("Session replaced: dropping trailing events from the old session");
                return;
            }

            // Chatter the UI never shows (thinking_tokens arrives per token, thousands a
            // turn) is logged from here instead of queueing a UI-thread callback each;
            // thinking_tokens is not logged at all, or it rotates the trace out in one turn.
            if (e is CliMessageIgnored ignored)
            {
                if (ignored.Kind != "system/thinking_tokens")
                {
                    CrashLog.Debug("cli-ignored", $"{ignored.Kind}: {ignored.Raw}");
                }

                continue;
            }

            var captured = e;

            // The swap can also happen between the enqueue and the dispatch, so the
            // handler guard repeats the check.
            _dispatcher.TryEnqueue(() =>
            {
                if (ReferenceEquals(_session, session))
                {
                    HandleSafely(captured);
                }
            });
        }
    }

    /// <summary>
    /// One event's handler must not take the app down (an exception in a dispatcher
    /// callback is unhandled): the failure is logged, the transcript says so, and the
    /// next event is handled normally.
    /// </summary>
    private void HandleSafely(AgentEvent e)
    {
        try
        {
            Handle(e);
        }
        catch (Exception ex)
        {
            CrashLog.Error("chat", $"handling {e.GetType().Name} failed", ex);
            Add(new NoticeItem
            {
                Text = $"Codale could not show a {e.GetType().Name} from the session: {ex.Message}",
                Severity = NoticeSeverity.Error,
            });
        }
    }

    private void Handle(AgentEvent e)
    {
        ResumeUnpromptedTurn(e);

        RecordFeatureEvent(e);

        switch (e)
        {
            case SessionInitialized init:
                _status.Model = init.Model;
                if (init.ContextWindow is int contextWindow && contextWindow > 0)
                {
                    _status.ContextWindow = contextWindow;
                }
                _cliReportedMode = init.PermissionMode;

                // Ask and automatic are the host's, riding on the CLI's manual and auto:
                // the CLI confirming its half is not a reason to drop them.
                if (!RidesOn(PermissionMode, init.PermissionMode))
                {
                    // Full never yields to a CLI that started more cautious: ask it again for YOLO.
                    if (PermissionMode is "auto" or "dontAsk" or "bypassPermissions")
                    {
                        if (_session is { } live)
                        {
                            _ = live.TrySetPermissionModeAsync("bypassPermissions");
                        }
                    }
                    else
                    {
                        PermissionMode = init.PermissionMode;
                    }
                }

                NotifyModelDisplays();
                CrashLog.Trace(
                    $"Session initialized: model={init.Model} mode={init.PermissionMode} " +
                    $"commands={init.SlashCommands.Count}");

                // The CLI names the session only now (the first message makes it say
                // so): the history list and the persisted record need the real id.
                SessionStateChanged?.Invoke(this, EventArgs.Empty);
                break;

            case AssistantTextDelta sub when OwnerOf(sub.ParentToolUseId) is not null:
            case AssistantThinkingDelta subThought when OwnerOf(subThought.ParentToolUseId) is not null:
                // A subagent's stream lands in its own log (on completion), never in the main reply.
                break;

            case AssistantTextDelta delta:
                Streaming().Append(delta.Text);
                SetActivity("Writing", toolUseId: null);
                break;

            case AssistantThinkingDelta thought:
                // Nothing lands in the transcript while the model thinks; the status
                // line is the one place that shows the turn is alive.
                SetActivity("Thinking", toolUseId: null);
                break;

            case AssistantMessageCompleted done:
                if (_status.IsCustomProvider && done.Usage is { } served && ServedByHelperProvider())
                {
                    _status.RecordCustomCall(served);
                }

                // Codex reports its own session total (SessionTokensUpdated); Claude's has
                // to be summed here, subagents' requests included.
                if (done.Usage is { } spent)
                {
                    CountMessageTokens(done.MessageId, spent);
                }

                if (done.ParentToolUseId is { } owner && _tasksByToolUse.TryGetValue(owner, out var subagent))
                {
                    // A subagent's own words belong in its log, not in the main transcript.
                    if (done.Thinking is { Length: > 0 } thought)
                    {
                        subagent.AppendOutput($"\n[thinking] {thought.Trim()}\n");
                    }

                    if (!string.IsNullOrWhiteSpace(done.Text))
                    {
                        subagent.AppendOutput($"\n{done.Text.Trim()}\n");
                        subagent.AddText(done.Text);
                    }

                    break;
                }

                // Only the CLI's own messages ("<synthetic>": its login and API errors) can
                // mean signed out. A model's reply that quotes "Not logged in" or
                // "codex login" - reading Codale's own source, say - is just conversation.
                if (done.Model == "<synthetic>")
                {
                    CheckLoginRequired(done.Text);
                }

                CompleteStreaming(done);
                FoldTurnSteps();
                break;

            case ToolCallStarted nested when OwnerOf(nested.ParentToolUseId) is not null:
                // A subagent's own tool calls are its log's business, not main-transcript rows.
                TrackTaskStart(nested, CreateToolCall(nested.ToolUseId, nested.ToolName, nested.Input));
                break;

            case ToolCallStarted started:
                // The agent reports its task list and artifacts through Codale's own
                // todos_set / artifact_add tools (codale-tasks), whose schema is ours.
                if (TodoParser.IsTodosTool(started.ToolName))
                {
                    ApplyTodos(started.Input);
                }
                else if (TodoParser.IsArtifactTool(started.ToolName))
                {
                    RecordAgentArtifact(started.Input);
                }

                var item = CreateToolCall(started.ToolUseId, started.ToolName, started.Input);
                _toolCalls[started.ToolUseId] = item;
                TrackTaskStart(started, item);

                // Task-list bookkeeping is one quiet row per turn - the panel has the
                // list itself - and the CLI's own plumbing (loading deferred tools) is
                // no step of the work at all: those calls are tracked, never shown.
                if (ToolKinds.IsTodo(started.ToolName))
                {
                    _hiddenCalls.Add(started.ToolUseId);
                    UpdateTodoStep();
                }
                else if (ToolKinds.IsPlumbing(started.ToolName))
                {
                    _hiddenCalls.Add(started.ToolUseId);
                }
                else
                {
                    AddToolStep(item);
                }

                if (item.IsPlan)
                {
                    RecordPlanArtifact(item);
                }

                // Hidden calls still count as activity: the status line names them, so
                // a turn busy with plumbing never looks like it is doing nothing.
                SetActivity(
                    ToolKinds.IsPlumbing(started.ToolName) ? "Loading tools"
                    : ToolKinds.IsTodo(started.ToolName) ? "Updating tasks"
                    : $"{item.Verb} {item.Target}".Trim(),
                    started.ToolUseId);

                CrashLog.Trace($"Tool start: {started.ToolName} id={started.ToolUseId}");

                break;

            case ToolCallCompleted nestedDone when _subagentCalls.ContainsKey(nestedDone.ToolUseId):
                TrackTaskCompleted(nestedDone);
                break;

            case ToolCallCompleted completed:
                CrashLog.Trace($"Tool done: id={completed.ToolUseId} error={completed.IsError}");

                if (_activityToolId == completed.ToolUseId)
                {
                    SetActivity(null, toolUseId: null);
                }

                TrackTaskCompleted(completed);

                if (_toolCalls.TryGetValue(completed.ToolUseId, out var call))
                {
                    // A refused call stays refused: its "result" is only the refusal echoed back.
                    call.Status = call.Status == ToolCallStatus.Denied && completed.IsError
                        ? ToolCallStatus.Denied
                        : completed.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
                    call.Duration = DateTimeOffset.Now - call.Timestamp;

                    // A result is a terminal page, not a sentence: keep its shape and
                    // cap it. Flattening newlines turned a directory table into one
                    // unwrapped-in-any-language line of text.
                    call.Output = CapOutput(completed.ResultText);

                    // The plan card already shows the plan itself; the ExitPlanMode
                    // result only echoes "the user approved the plan" back, so it
                    // would read as the same plan twice. The decision is what the
                    // step's status says - and what the plan's panel entry records.
                    if (call.IsPlan)
                    {
                        call.Output = null;
                        SetPlanStatus(call.ToolUseId, completed.IsError ? SessionArtifactStatus.Revised : SessionArtifactStatus.Accepted);
                    }

                    if (completed.Images is { Count: > 0 } images)
                    {
                        RecordImageArtifacts(call, images);
                    }

                    if (completed.FileChange is { } change)
                    {
                        // The CLI's own patch beats the input preview: real line numbers.
                        call.Diff = ToolDiffs.FromChange(change, call.Diff) ?? call.Diff;

                        var resolved = change with { FilePath = ResolvePath(change.FilePath) };
                        TrackPlanFile(resolved);

                        _status.RecordFileChange(resolved, call.Diff);
                        IndexWrittenFile(resolved.FilePath);
                        FilesChanged?.Invoke(this, EventArgs.Empty);
                    }

                    // The step is done: fold it to its one-line row.
                    call.AutoCollapse();

                    // The result and the built diff outlive the need for the raw input.
                    call.ReleaseInput();
                }
                break;

            case ApprovalRequested approval:
                CrashLog.Trace($"Approval requested: {approval.ToolName} id={approval.ToolUseId ?? "none"}");
                _ = HandleApprovalAsync(approval);
                break;

            case AgentError error:
                CheckLoginRequired(error.Message);
                Add(new NoticeItem { Text = error.Message, Severity = NoticeSeverity.Error });
                break;

            case PermissionDenied denied:
                MarkDenied(denied.ToolUseId);

                // Pinned above the composer rather than appended to the transcript: it
                // always lands last in the chat, where the reader is about to type.
                ComposerNotice = new NoticeItem
                {
                    Text = denied.Message ?? $"{denied.ToolName} was denied.",
                    Severity = NoticeSeverity.Warning,
                };
                break;

            case RateLimitUpdated limits:
                _status.Apply(limits.Limits);
                break;

            case SessionTokensUpdated tokens:
                _status.SessionTokens = tokens.Total;
                break;

            case UsageUpdated usage:
                // A Claude stream's message_delta: the request's own usage (the only real
                // one a Z.ai-style endpoint sends). A subagent's request fills a different
                // context, but its tokens are still spent.
                if (usage.MessageId is { } usageMessageId)
                {
                    CountMessageTokens(usageMessageId, usage.Usage);
                    if (usage.ParentToolUseId is not null)
                    {
                        break;
                    }

                    _lastMessageUsage = usage.Usage;
                }

                // Mid-turn context readings (one after every model
                // call), so the meter moves while the turn runs, not just when it lands.
                _status.Apply(usage.Usage);

                // The CLI's own window size, only when the provider settings publish none
                // for this model: a configured size is the better-informed number.
                if (usage.ContextWindow is int reportedWindow && _status.ContextWindow <= 0)
                {
                    _status.ContextWindow = reportedWindow;
                }
                break;

            case TurnCompleted turn:
                IsBusy = false;
                CrashLog.Trace($"Turn completed: {turn.Subtype}");
                SetActivity(null, toolUseId: null);

                RestoreAfterPlanTurn();
                _status.Apply(turn);

                // The context window reading comes from the last assistant message, not
                // from the result: the result's usage sums every request of the turn
                // (inputs re-counted, cache reads included), so taking it directly made
                // the meter climb toward full no matter how much context was really left.
                if (_lastMessageUsage is { } last)
                {
                    _status.Apply(last);
                }
                else if (turn.Usage is { } usage)
                {
                    // No message seen this turn; an inflated reading beats none.
                    _status.Apply(usage);
                }

                AbandonPendingApprovals();

                var outcome = turn.Subtype == "success" ? TurnOutcome.Completed
                    : _interruptPending && turn.Subtype == "error_during_execution" ? TurnOutcome.Stopped
                    : TurnOutcome.Failed;

                if (_interruptPending && turn.Subtype == "error_during_execution")
                {
                    // What an interrupted turn looks like (see interrupt.jsonl) - expected,
                    // not a failure.
                    Add(new NoticeItem { Text = "Turn interrupted.", Severity = NoticeSeverity.Info });
                }
                else if (turn.IsError)
                {
                    Add(new NoticeItem { Text = $"Turn ended: {turn.Subtype}", Severity = NoticeSeverity.Error });
                }

                _interruptPending = false;
                EndTurn(outcome);

                // The agent is done and the next move is the reader's.
                if (outcome is TurnOutcome.Completed or TurnOutcome.Failed)
                {
                    var answer = Items.OfType<AssistantMessageItem>().LastOrDefault()?.Text;
                    AttentionNeeded?.Invoke(this, outcome == TurnOutcome.Completed
                        ? new AttentionRequest(AttentionKind.TurnFinished, answer is { Length: > 0 } ? Shorten(answer, 140) : "Finished.")
                        : new AttentionRequest(AttentionKind.TurnFailed, $"The turn ended with an error ({turn.Subtype})."));
                }
                TurnFinished?.Invoke(this, EventArgs.Empty);
                SessionStateChanged?.Invoke(this, EventArgs.Empty);
                break;

            case SessionEnded ended:
                IsBusy = false;
                IsConnected = false;
                AbandonPendingApprovals();
                EndAllTasks();

                // A session dying mid-turn never sends TurnCompleted; the clock has to
                // stop and the line has to settle anyway, or it runs forever.
                EndTurn(TurnOutcome.Stopped);
                CrashLog.Trace($"Session ended: exit={ended.ExitCode} error={ended.Error ?? "none"}");
                SessionStateChanged?.Invoke(this, EventArgs.Empty);
                Add(new NoticeItem
                {
                    Text = ended.Error is { Length: > 0 } err
                        ? $"Session ended ({ended.ExitCode}): {err}"
                        : $"Session ended ({ended.ExitCode}).",
                    Severity = NoticeSeverity.Warning,
                });
                break;

            case BackgroundTaskChanged changed:
                ApplyBackgroundTask(changed);
                break;

            case CliMessageIgnored ignored:
                CrashLog.Debug("cli-ignored", $"{ignored.Kind}: {ignored.Raw}");
                break;

            case HookFailed hook:
                ComposerNotice = new NoticeItem
                {
                    Text = $"Hook {hook.HookName} failed" +
                        (hook.ExitCode is { } exit ? $" (exit {exit})" : "") +
                        (string.IsNullOrWhiteSpace(hook.Detail) ? "." : $": {hook.Detail.Trim()}"),
                    Severity = NoticeSeverity.Warning,
                };
                break;

            case CliStatusChanged cliStatus when IsBusy:
                switch (cliStatus.Status)
                {
                    case "requesting":
                        SetActivity("Waiting for the model", toolUseId: null);
                        break;
                    case "compacting":
                        SetActivity("Compacting the conversation", toolUseId: null);
                        break;
                    case "retrying":
                        SetActivity("Retrying", toolUseId: null);
                        break;
                }

                break;

            case CliInformation info:
                // A notification about the CLI itself, not part of the conversation: pinned
                // above the composer, where it can be dismissed.
                ComposerNotice = new NoticeItem { Text = info.Text, Severity = info.IsWarning ? NoticeSeverity.Warning : NoticeSeverity.Info };
                break;

            case CommandsChanged:
            case HookFailed or CliStatusChanged or CliInformation:
                break;

            case VcsStateChanged:
                // Same refresh a file edit triggers: git status and the diff are stale.
                FilesChanged?.Invoke(this, EventArgs.Empty);
                break;

            case PromptSuggested suggested:
                // Only between turns: a suggestion that lands mid-turn would be stale.
                if (!IsBusy)
                {
                    NextPromptSuggestion = suggested.Text;
                }

                break;

            case ConversationReset:
                // A reset is not a turn the CLI finishes - no result event follows it -
                // so the turn the /clear prompt opened ends here, busy flag included.
                IsBusy = false;
                NextPromptSuggestion = null;
                ComposerNotice = null;
                // /clear: the CLI has dropped its own history, so the transcript
                // follows - same process, fresh conversation.
                EndTurn(TurnOutcome.Stopped);
                Items.Clear();
                _toolCalls.Clear();
                _hiddenCalls.Clear();
                ClearTasks();
                AbandonPendingApprovals();
                _streaming = null;
                ShowTodos([]);
                Artifacts.Clear();
                _status.ClearSessionChanges();
                break;

            case ConversationCompacted compacted:
                // The boundary carries the window's new occupancy, and the turn it sits in
                // ends with no assistant message: the stale pre-compact reading below would
                // otherwise be re-applied at turn end and the meter would never drop.
                _lastMessageUsage = null;
                if (compacted.PostTokens is { } post and > 0)
                {
                    _status.Apply(new UsageSnapshot { InputTokens = (int)Math.Min(post, int.MaxValue) });
                }

                Add(new NoticeItem
                {
                    Text = "Conversation compacted" +
                        (compacted.Trigger == "auto" ? " automatically" : "") +
                        (compacted.PreTokens is { } before ? $" (was {before:N0} tokens)." : "."),
                    Severity = NoticeSeverity.Info,
                });
                break;

            case UnknownEvent unknown:
                // Surfaced rather than swallowed: this is how CLI protocol drift becomes visible.
                // It goes to the notice above the composer, not the transcript: the same
                // message can repeat every turn, and one notice replaces the last instead of
                // stacking. A notice that is already showing something else is left alone.
                var unrecognised = $"Unrecognised message from the CLI: {unknown.RawType}";
                if (ComposerNotice is null || ComposerNotice.Text.StartsWith("Unrecognised message from the CLI", StringComparison.Ordinal))
                {
                    ComposerNotice = new NoticeItem { Text = unrecognised, Severity = NoticeSeverity.Warning };
                }

                break;
        }
    }

    /// <summary>
    /// Folds the turn's finished tool cards to their summary lines - the agent has
    /// replied in prose or the turn has landed, so the steps before it are history.
    /// </summary>
    private void FoldTurnSteps()
    {
        foreach (var step in _activeTurn?.AllTools ?? [])
        {
            step.AutoCollapse();
        }
    }

    /// <summary>
    /// Builds a tool call's step. What opens by default depends on what the body shows:
    /// a command about to run, a diff being made, a question or a plan are worth seeing
    /// live; a read or a search is not - its row says it all.
    /// </summary>
    private ToolCallItem CreateToolCall(string toolUseId, string toolName, JsonElement input)
    {
        var plan = ToolCallItem.PlanInput(toolName, input);
        var kind = ToolKinds.Of(toolName);

        return new ToolCallItem
        {
            ToolUseId = toolUseId,
            ToolName = toolName,
            Input = input.ValueKind == JsonValueKind.Undefined ? default : input.Clone(),
            Summary = ToolCallItem.Describe(toolName, input),
            Command = ToolCallItem.CommandInput(toolName, input) ?? "",
            PlanText = plan,
            Files = ToolCallItem.FileInputs(toolName, input),
            Diff = ToolDiffs.FromInput(toolName, input),
            IsExpanded = plan is not null || kind is ToolKind.Shell or ToolKind.Edit or ToolKind.Write or ToolKind.Ask,
            Timestamp = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Adds a tool call to the running turn's timeline. Look-around calls in a row
    /// fold together: the second one turns the first into an "Explored" group, later
    /// ones join it, and anything else in between - prose, an edit - ends the run.
    /// </summary>
    private void AddToolStep(ToolCallItem item)
    {
        if (_activeTurn is not { } turn)
        {
            Add(item);
            return;
        }

        var steps = turn.Steps;

        if (ToolKinds.IsExplore(item.Kind))
        {
            switch (steps.LastOrDefault())
            {
                case ExploreGroupStep group:
                    group.Add(item);
                    return;

                case ToolCallItem previous when ToolKinds.IsExplore(previous.Kind) && !previous.IsAwaitingApproval:
                    var merged = new ExploreGroupStep { Timestamp = previous.Timestamp };
                    steps[steps.Count - 1] = merged;
                    merged.Add(previous);
                    merged.Add(item);
                    return;
            }
        }

        steps.Add(item);
    }

    /// <summary>The turn's task-list row: created on the first list update, then kept current.</summary>
    private void UpdateTodoStep()
    {
        if (_activeTurn is not { } turn)
        {
            return;
        }

        if (_turnTodo is null)
        {
            _turnTodo = new TodoStepItem();
            turn.Steps.Add(_turnTodo);
        }

        _turnTodo.Done = TasksDone;
        _turnTodo.Total = TasksTotal;
        _turnTodo.ActiveTask = ActiveTask;
    }

    /// <summary>
    /// A turn that lands with its list still half-open reads as stuck halfway: the
    /// model often ends without a closing todos_set, most often when its execution
    /// changed mid-flight and the closing update never came. On a completed turn the
    /// work is done, so whatever is left unchecked settles to done - the panel and the
    /// turn's row move together. A stopped or failed turn keeps the model's statuses:
    /// unchecked is then what actually happened, and the next turn's list replaces it.
    /// </summary>
    private void SettleTodos(TurnOutcome outcome)
    {
        // Only a list this turn set: one carried over from history or an earlier turn is
        // not this turn's work, and settling it gave a task-less turn a "Tasks n/n done" row.
        if (_turnTodo is null || outcome != TurnOutcome.Completed || TasksTotal == 0 || TasksDone == TasksTotal)
        {
            return;
        }

        ShowTodos(Todos.Select(t => t.IsDone ? t : t with { Status = TodoStatus.Completed }).ToList());
        UpdateTodoStep();
    }

    /// <summary>Tool paths may be relative to the agent's working directory; the panel wants them whole.</summary>
    private string ResolvePath(string path)
    {
        try
        {
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(WorktreePath ?? _projectPath, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// What the next plan is a revision of: the plan the reader annotated, its text at
    /// that moment (the agent may overwrite the plan file before re-proposing, so the
    /// old wording is snapshot here rather than read back later), and what was asked.
    /// </summary>
    private (SessionArtifact Plan, string PreviousMarkdown, IReadOnlyList<PlanAnnotation> Notes)? _pendingRevision;

    /// <summary>
    /// The turn the revision request rides on: the one blocked on the refused approval,
    /// the running turn a steered message joined, or the next turn to begin. When that
    /// turn ends without proposing a plan, the request is spent - a plan the agent
    /// proposes much later, unprompted, is not a revision of it.
    /// </summary>
    private TurnActivityItem? _revisionTurn;

    private void NoteRevisionRequest(ToolCallItem call, ApprovalDecision decision)
    {
        if (!call.IsPlan || decision.Behavior != ApprovalBehavior.Deny ||
            Artifacts.FirstOrDefault(a => a.ToolUseId == call.ToolUseId) is not { } plan)
        {
            return;
        }

        IReadOnlyList<PlanAnnotation> notes = plan.SentNotes is { Count: > 0 } sent
            ? sent
            : decision.Message is { Length: > 0 } message ? [new PlanAnnotation("", message)] : [];
        plan.SentNotes = null;
        _pendingRevision = (plan, plan.Markdown, notes);
        _revisionTurn = _activeTurn;
    }

    /// <summary>
    /// An ExitPlanMode plan joins the session's plans the moment it is proposed. Plan mode
    /// writes the plan to a file under .claude/plans first, then proposes that same text:
    /// the file's entry is taken over rather than listed beside it, so one plan is one row.
    /// </summary>
    private void RecordPlanArtifact(ToolCallItem item)
    {
        if (Artifacts.Any(a => a.ToolUseId == item.ToolUseId))
        {
            return;
        }

        var asked = _pendingRevision;
        var source = PlanSourceOf(item);
        var plan = new SessionArtifact
        {
            Kind = SessionArtifactKind.Plan,
            ToolUseId = item.ToolUseId,
            SourcePath = source?.SourcePath,
            Markdown = item.PlanText ?? "",
            UpdatedAt = item.Timestamp,
            Status = SessionArtifactStatus.Proposed,
            Revision = asked is { } from ? new PlanRevision(from.PreviousMarkdown, from.Notes) : null,
        };
        _pendingRevision = null;
        _revisionTurn = null;

        if (source is not null)
        {
            var at = Artifacts.IndexOf(source);
            Artifacts.RemoveAt(at);
            if (ReferenceEquals(_turnPlanFile, source))
            {
                _turnPlanFile = plan;
            }
        }

        Artifacts.Insert(0, plan);

        // Views showing the plan this one replaced follow it: the file row it took over,
        // and - when that is another row - the plan the annotations were sent on.
        if (source is not null && !ReferenceEquals(source, plan))
        {
            PlanReplaced?.Invoke(this, (source, plan));
        }

        if (asked?.Plan is { } annotated && !ReferenceEquals(annotated, source) && !ReferenceEquals(annotated, plan))
        {
            PlanReplaced?.Invoke(this, (annotated, plan));
        }
    }

    /// <summary>
    /// The listed plan document (or earlier proposal of it) a proposal came from: the file
    /// ExitPlanMode names, else one holding the same text, else the plan file this turn wrote.
    /// </summary>
    private SessionArtifact? PlanSourceOf(ToolCallItem item)
    {
        var candidates = Artifacts
            .Where(a => a.SourcePath is not null && a.Kind is SessionArtifactKind.PlanFile or SessionArtifactKind.Plan)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        if (item.Input.ValueKind == JsonValueKind.Object &&
            item.Input.TryGetProperty("planFilePath", out var named) &&
            named.ValueKind == JsonValueKind.String &&
            named.GetString() is { Length: > 0 } namedPath)
        {
            var full = ResolvePath(namedPath);
            if (candidates.FirstOrDefault(a => string.Equals(a.SourcePath, full, StringComparison.OrdinalIgnoreCase)) is { } byPath)
            {
                return byPath;
            }
        }

        static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();
        var proposed = Normalize(item.PlanText);
        if (candidates.FirstOrDefault(a => Normalize(a.Markdown) == proposed) is { } byText)
        {
            return byText;
        }

        // Edited after it was written (or read back with different whitespace): the plan
        // file touched this turn is still the one being proposed.
        return _turnPlanFile is { } written && candidates.Contains(written) ? written : null;
    }

    /// <summary>Where saved tool images and agent-written markdown documents live.</summary>
    private static string ArtifactsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "artifacts");

    /// <summary>Saved tool images are only session artifacts; a week is long enough to reopen one. Documents saved beside them (.md) are left alone.</summary>
    private static void PruneOldImages(string dir)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (Path.GetExtension(file).ToLowerInvariant() is not (".png" or ".jpg"))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or locked: it is picked up next time.
            }
        }
    }

    /// <summary>Screenshots and other images a tool returned are kept on disk and listed with the session artifacts.</summary>
    private void RecordImageArtifacts(ToolCallItem call, IReadOnlyList<ResultImage> images)
    {
        var toolUseId = call.ToolUseId;
        var title = call.ToolName.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
            || call.ToolName.Contains("responsive", StringComparison.OrdinalIgnoreCase)
            ? "Browser screenshot"
            : "Image";

        _ = SaveImageArtifactsAsync(toolUseId, title, images.ToList());
    }

    /// <summary>Decodes and writes the images off the UI thread (a full-page screenshot is megabytes), then lists them.</summary>
    private async Task SaveImageArtifactsAsync(string toolUseId, string title, List<ResultImage> images)
    {
        try
        {
            var paths = await Task.Run(() =>
            {
                var dir = ArtifactsDirectory;
                Directory.CreateDirectory(dir);
                PruneOldImages(dir);

                var saved = new List<string>();
                for (var i = 0; i < images.Count; i++)
                {
                    var ext = images[i].MediaType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
                    var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{i}{ext}");
                    File.WriteAllBytes(path, Convert.FromBase64String(images[i].Base64));
                    saved.Add(path);
                }

                return saved;
            });

            foreach (var path in paths)
            {
                Artifacts.Insert(0, new SessionArtifact
                {
                    Kind = SessionArtifactKind.Image,
                    SourcePath = path,
                    ToolUseId = toolUseId,
                    Title = title,
                    Status = SessionArtifactStatus.Saved,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            CrashLog.Trace($"Could not save tool image: {ex.Message}");
        }
    }

    private void SetPlanStatus(string toolUseId, SessionArtifactStatus status)
    {
        if (Artifacts.FirstOrDefault(a => a.ToolUseId == toolUseId) is { } artifact)
        {
            artifact.Status = status;
        }
    }

    /// <summary>
    /// The agent registered an artifact through artifact_add: a file it wrote, or markdown
    /// it wants kept as a document. Images and other files open as they do elsewhere.
    /// </summary>
    private void RecordAgentArtifact(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var title = Str(input, "title");
        var path = Str(input, "path") is { Length: > 0 } p ? ResolvePath(p) : null;
        var markdown = Str(input, "markdown");

        if (path is null && markdown is { Length: > 0 })
        {
            try
            {
                Directory.CreateDirectory(ArtifactsDirectory);
                path = Path.Combine(ArtifactsDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.md");
                File.WriteAllText(path, markdown);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashLog.Trace($"Could not save artifact markdown: {ex.Message}");
                return;
            }
        }

        if (path is null)
        {
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var kind = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" ? SessionArtifactKind.Image
            : ext == ".md" ? SessionArtifactKind.PlanFile
            : SessionArtifactKind.File;

        var existing = Artifacts.FirstOrDefault(a => a.SourcePath is not null && string.Equals(a.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.UpdatedAt = DateTimeOffset.Now;
            Artifacts.Move(Artifacts.IndexOf(existing), 0);
            return;
        }

        Artifacts.Insert(0, new SessionArtifact
        {
            Kind = kind,
            SourcePath = path,
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title.Trim(),
            Status = SessionArtifactStatus.Saved,
        });
    }

    /// <summary>
    /// A markdown file written under a plans folder is a plan document: it joins the
    /// session's plans (once per path), and later writes refresh it.
    /// </summary>
    private void TrackPlanFile(FileChange change)
    {
        if (change.Kind == FileChangeKind.Delete)
        {
            return;
        }

        // Other agent-written files are listed only when the agent adds them (artifact_add).
        var isPlan = SessionArtifact.IsPlanPath(change.FilePath);
        var existing = Artifacts.Any(a => a.SourcePath is not null && string.Equals(a.SourcePath, change.FilePath, StringComparison.OrdinalIgnoreCase));
        if (!isPlan && !existing)
        {
            return;
        }

        var content = change.NewContent;
        if (content is null)
        {
            try
            {
                content = File.Exists(change.FilePath) ? File.ReadAllText(change.FilePath) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                content = null;
            }
        }

        var artifact = Artifacts.FirstOrDefault(a =>
            a.SourcePath is not null && a.Kind != SessionArtifactKind.Image && string.Equals(a.SourcePath, change.FilePath, StringComparison.OrdinalIgnoreCase));

        if (artifact is null)
        {
            artifact = new SessionArtifact
            {
                Kind = isPlan ? SessionArtifactKind.PlanFile : SessionArtifactKind.File,
                SourcePath = change.FilePath,
                Title = isPlan ? "Plan" : Path.GetFileName(change.FilePath),
                Status = SessionArtifactStatus.Saved,
            };
            Artifacts.Insert(0, artifact);
        }
        else if (Artifacts.IndexOf(artifact) > 0)
        {
            Artifacts.Move(Artifacts.IndexOf(artifact), 0);
        }

        if (isPlan)
        {
            artifact.Markdown = content ?? artifact.Markdown;
            _turnPlanFile = artifact;
        }

        artifact.UpdatedAt = DateTimeOffset.Now;
    }

    /// <summary>Why ask mode turned a tool down, in words the model acts on.</summary>
    private const string AskModeDenial =
        "Ask mode is on: the user wants an explanation only, so nothing may be edited, created, " +
        "deleted or run. Reading files and searching the project or the web are fine. Answer in chat " +
        "instead - describe the change and show the code.";

    /// <summary>
    /// Shows the approval and relays the answer. Runs detached from the event handler, so
    /// a failure is dealt with here: the request is denied (the CLI would otherwise wait
    /// for an answer forever) and the transcript says why.
    /// </summary>
    private async Task HandleApprovalAsync(ApprovalRequested approval)
    {
        try
        {
            await HandleApprovalCoreAsync(approval);
        }
        catch (Exception ex)
        {
            CrashLog.Error("chat", $"approval for {approval.ToolName} failed", ex);
            _approvals.Remove(approval.RequestId);
            OnApprovalsChanged();
            Add(new NoticeItem { Text = $"Could not ask about {approval.ToolName}: {ex.Message}", Severity = NoticeSeverity.Error });

            if (_session is { } session)
            {
                try
                {
                    await session.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Deny("Codale could not show this request."));
                }
                catch (Exception respondError)
                {
                    CrashLog.Debug("chat", $"denying the failed approval also failed: {respondError.Message}");
                }
            }
        }
    }

    private async Task HandleApprovalCoreAsync(ApprovalRequested approval)
    {
        // Codale's own sandboxed tools (explore, the task list...) are essential
        // and harmless; the CLI still asks for them in plan mode, so the host answers yes
        // itself in every mode but Manual, where the user wants to see each call.
        if (PermissionMode is not ("manual" or "default") &&
            McpServerSpec.RunsUnasked(_sessionMcpServers, approval.ToolName))
        {
            CrashLog.Trace($"{ModeDisplay(PermissionMode)} mode: allowed {approval.ToolName} unasked");

            if (_session is { } allowing)
            {
                await allowing.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Allow());
            }

            return;
        }

        // In acceptEdits mode, or Auto mode when a CLI runs in "default", file edits go
        // through without asking.
        if ((PermissionMode == "acceptEdits" || (IsAutomaticMode && _cliReportedMode == "default")) &&
            approval.ToolName is "Edit" or "Write" or "MultiEdit" or "NotebookEdit")
        {
            CrashLog.Trace($"{ModeDisplay(PermissionMode)} mode: allowed {approval.ToolName} unasked");

            if (_session is { } editing)
            {
                await editing.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Allow());
            }

            return;
        }

        // Ask mode backs its instruction with enforcement: anything the CLI had to ask
        // about is an action, and the answer is no - without bothering the reader,
        // except shell commands (below).
        // Questions for the user are conversation, not action, so they still get through.
        // Looking things up on the web reads, it changes nothing, and it often makes the
        // answer: ask mode lets it through unasked.
        if (IsAskMode && approval.ToolName is "WebSearch" or "WebFetch")
        {
            CrashLog.Trace($"Ask mode: allowed {approval.ToolName} unasked");

            if (_session is { } searching)
            {
                await searching.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Allow());
            }

            return;
        }

        // Shell commands are often just reads (listing, searching), and a CLI that cannot
        // sandbox them - codex on Windows - asks about every one: the reader decides those.
        if (IsAskMode && approval.ToolName is not ("AskUserQuestion" or "Bash"))
        {
            if (_toolCalls.TryGetValue(approval.ToolUseId ?? "", out var refused))
            {
                refused.Status = ToolCallStatus.Denied;
            }

            CrashLog.Trace($"Ask mode: denied {approval.ToolName}");

            if (_session is { } asking)
            {
                await asking.RespondToApprovalAsync(approval.RequestId, ApprovalDecision.Deny(AskModeDenial));
            }

            return;
        }

        if (!_toolCalls.TryGetValue(approval.ToolUseId ?? "", out var pending))
        {
            // An approval with no transcript row yet: give it a row of its own, so
            // the question is visible and answerable regardless.
            pending = CreateToolCall(approval.ToolUseId ?? approval.RequestId, approval.ToolName, approval.Input);

            if (approval.ToolUseId is { Length: > 0 } id)
            {
                _toolCalls[id] = pending;
            }

            AddToolStep(pending);
        }
        else if (_hiddenCalls.Remove(pending.ToolUseId))
        {
            // A call normally kept out of the timeline now needs an answer: it has to
            // be on screen for that.
            AddToolStep(pending);
        }

        pending.Status = ToolCallStatus.AwaitingApproval;
        pending.Diff ??= ToolDiffs.FromInput(approval.ToolName, approval.Input);

        // The question has to be on screen to be answered, so the step reopens - and
        // its explore group and the turn's timeline with it - even if the reader had
        // folded any of them.
        pending.ReopenForApproval();
        if (_activeTurn is { } turn)
        {
            turn.IsExpanded = true;
        }

        // The question lives on the transcript row; the click answers here.
        var answered = new TaskCompletionSource<ApprovalDecision>();
        _approvals[approval.RequestId] = answered;
        OnApprovalsChanged();
        pending.Approval = new ApprovalRequestItem
        {
            RequestId = approval.RequestId,
            ToolName = approval.ToolName,
            Input = approval.Input,
            IsPlan = string.Equals(approval.ToolName, "ExitPlanMode", StringComparison.Ordinal),
            Questions = ApprovalRequestItem.ParseQuestions(approval.Input),
            Suggestions = approval.Suggestions
                .Select(s => new SuggestionOption { RequestId = approval.RequestId, Suggestion = s })
                .ToArray(),
        };

        // The turn is blocked on the reader from here: say so, in case they are elsewhere.
        AttentionNeeded?.Invoke(this, pending.Approval switch
        {
            { IsAskUserQuestion: true } ask => new AttentionRequest(
                AttentionKind.Question, ask.Questions[0].Text),
            { IsPlan: true } => new AttentionRequest(
                AttentionKind.Plan, pending.Summary is { Length: > 0 } headline ? headline : "A plan is ready for review."),
            var permission => new AttentionRequest(
                AttentionKind.Permission, $"{permission.QuestionHeader} {pending.Target}".Trim()),
        });

        var decision = await answered.Task;

        _approvals.Remove(approval.RequestId);
        OnApprovalsChanged();
        pending.Approval = null;
        NoteRevisionRequest(pending, decision);
        pending.Status = decision.Behavior == ApprovalBehavior.Allow
            ? ToolCallStatus.Running
            : ToolCallStatus.Denied;

        if (decision.AcceptedSuggestion is { Mode: { Length: > 0 } mode })
        {
            PermissionMode = mode;
        }

        if (_session is not null)
        {
            await _session.RespondToApprovalAsync(approval.RequestId, decision);
        }

        if (decision.SwitchToMode is { Length: > 0 } chosen)
        {
            // Accepting a plan into a work mode is the user overriding the plan
            // restore: the session stays in the chosen mode after the turn, so the
            // saved "back to manual" would clobber their pick. The switch goes out
            // after the approval answer - the CLI is blocked on that answer first.
            // "chosen" is a host mode id; the CLI half goes through CliMode, so Auto
            // ("automatic") lands on the CLI's "auto" and Full on "bypassPermissions".
            _planSession = null;
            _planRestoreMode = null;
            PermissionMode = chosen;
            RestorePlanEffort();

            var cliChosen = CliMode(chosen);
            if (_session is { } live)
            {
                try
                {
                    // The CLI is on "plan" while the plan turn runs, so the work mode
                    // always differs; the only no-op case is a session already gone.
                    await live.TrySetPermissionModeAsync(cliChosen);
                }
                catch (Exception ex)
                {
                    CrashLog.Trace($"Post-plan mode switch failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// The user answered an inline approval card. Completing here resumes the
    /// handler waiting above on the UI thread; an unknown id (session gone, answer
    /// twice) is simply dropped.
    /// </summary>
    public void RespondToApproval(string requestId, ApprovalDecision decision)
    {
        if (_approvals.TryGetValue(requestId, out var answered))
        {
            answered.TrySetResult(decision);
        }
    }

    /// <summary>
    /// Annotations sent back on a plan. While the plan still awaits approval the turn is
    /// blocked on it, so the annotations answer that approval as a revision (the old plan
    /// is replaced and the turn carries on). Otherwise they go out as a new plan turn.
    /// </summary>
    /// <param name="annotated">The plan the annotations were taken on, when the sender knows it.</param>
    public Task SendPlanRevisionAsync(string message, SessionArtifact? annotated = null)
    {
        var pending = _toolCalls.Values
            .Select(c => c.Approval)
            .FirstOrDefault(a => a is { IsPlan: true } && _approvals.ContainsKey(a.RequestId));

        if (pending is null)
        {
            // No approval to answer: the annotated plan's turn is over, so the revision
            // travels as a fresh plan turn. Remember what is being revised - the plan and
            // its text as it stands now, before the agent overwrites the file - or the
            // next proposal arrives with no Revision and shows no markers. The request
            // rides the turn this message joins: the running one when it steers, else
            // the turn it opens (BeginTurn binds that one).
            if (annotated is { } plan)
            {
                IReadOnlyList<PlanAnnotation> notes = plan.SentNotes is { Count: > 0 } sent
                    ? sent
                    : plan.Annotations.ToList();
                _pendingRevision = (plan, plan.Markdown, notes);
                _revisionTurn = IsBusy && _activeTurn is not null ? _activeTurn : null;
            }

            return SendRoutedAsync(message, [], RouteIntent.Plan);
        }

        RespondToApproval(
            pending.RequestId,
            ApprovalDecision.Deny($"The user reviewed the plan and asked for revisions. {message}\n\nRevise the plan and present it again."));
        return Task.CompletedTask;
    }

    /// <summary>
    /// AskUserQuestion is the one tool where a bare "allow" is meaningless: the CLI
    /// reads the user's selections out of <c>updatedInput.answers</c> - a map of
    /// question text to the chosen label (an array for multi-select) - and reports
    /// "the user did not answer" when they are missing. The updated input must be
    /// the original fields verbatim plus answers; anything else is rejected.
    /// </summary>
    public void RespondToAskUserQuestion(ApprovalRequestItem approval)
    {
        var answers = new Dictionary<string, object?>();

        foreach (var question in approval.Questions)
        {
            // Picked labels plus the typed "Other" answer, which the CLI takes as
            // one more label - a free-text answer is still an answer.
            var selected = question.Answers;

            if (selected.Count > 0)
            {
                var val = question.MultiSelect ? selected : (object)selected[0];
                answers[question.Text] = val;
                if (question.Id is { Length: > 0 } id && id != question.Text)
                {
                    answers[id] = val;
                }
            }
        }

        // The step folds to what was answered, so the timeline reads "Asked you ·
        // Auth method → OAuth" rather than repeating the question.
        if (_toolCalls.Values.FirstOrDefault(c => ReferenceEquals(c.Approval, approval)) is { } call)
        {
            call.AnswerSummary = string.Join("  ·  ", approval.Questions
                .Where(q => q.IsAnswered)
                .Select(q => $"{(q.HasHeader ? q.Header : Shorten(q.Text, 48))} → {string.Join(", ", q.Answers)}"));
        }

        var decision = new ApprovalDecision
        {
            Behavior = ApprovalBehavior.Allow,
            UpdatedInput = BuildAnsweredInput(approval.Input, answers),
        };

        RespondToApproval(approval.RequestId, decision);
    }

    private static string Shorten(string text, int max)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length > max ? single[..max].TrimEnd() + "…" : single;
    }

    private static JsonElement? BuildAnsweredInput(
        JsonElement input, Dictionary<string, object?> answers)
    {
        if (input.ValueKind != JsonValueKind.Object || answers.Count == 0)
        {
            return null;
        }

        var node = JsonNode.Parse(input.GetRawText()) as JsonObject;
        if (node is null)
        {
            return null;
        }

        node["answers"] = JsonSerializer.SerializeToNode(answers);
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>The session is gone; nothing is coming to answer the open questions.</summary>
    private void AbandonPendingApprovals()
    {
        foreach (var answered in _approvals.Values)
        {
            answered.TrySetResult(ApprovalDecision.Deny("The session ended before this was answered."));
        }

        _approvals.Clear();
        OnApprovalsChanged();
    }

    [RelayCommand]
    private Task SendAsync() => SendCoreAsync(planOnly: false);

    /// <summary>Ctrl+Enter: send as a plan-only turn with high effort.</summary>
    [RelayCommand]
    private Task SendPlanAsync() => SendCoreAsync(planOnly: true);

    private async Task SendCoreAsync(bool planOnly)
    {
        // A WinUI TextBox stores line breaks as a bare \r, pasted \r\n included;
        // the CLI and the transcript expect \n.
        var text = Draft.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var attachments = Attachments.Select(a => a.ToTurnAttachment()).ToList();

        if ((text.Length == 0 && attachments.Count == 0) || _session is null || !IsConnected || IsRouting)
        {
            return;
        }

        // Smart history reads the message first: which feature it is work on (or that it is no
        // project work), and in Automatic mode the mode for the turn. Slash commands are the CLI's
        // own syntax and go out untouched; so does anything sent mid-turn, which the running turn covers.
        if (SmartHistory && !planOnly && !IsBusy && text.Length > 0 && !text.StartsWith('/') && Router is { } featureRouter)
        {
            FeatureDecision feature;
            IsRouting = true;
            try
            {
                feature = await featureRouter.RouteFeatureAsync(text, this, Features!);
                if (!await ApplyFeatureDecisionAsync(feature, text))
                {
                    return;
                }
            }
            finally
            {
                IsRouting = false;
            }

            if (IsAutomaticMode)
            {
                await SendRoutedAsync(feature.Route.Text, attachments, feature.Route.Intent);
            }
            else
            {
                await SendTurnAsync(text, attachments, planOnly: false, askOnly: IsAskMode);
            }

            return;
        }

        // Automatic mode reads the message first. Slash commands are the CLI's own
        // syntax and go out untouched; so does anything sent mid-turn, which the
        // running turn's mode already covers.
        if (IsAutomaticMode && !planOnly && !IsBusy && text.Length > 0 && !text.StartsWith('/') && Router is { } router)
        {
            RouteDecision decision;
            IsRouting = true;
            try
            {
                decision = await router.RouteAsync(text, this);
            }
            finally
            {
                IsRouting = false;
            }

            // The draft stays until the routing is done: a message is never lost to a slow model.
            if (decision.NewTopic && NewTopicRequested is { } handler)
            {
                Draft = "";
                Attachments.Clear();
                handler(this, new RoutedMessage(decision.Text, attachments, decision.Intent));
                return;
            }

            await SendRoutedAsync(decision.Text, attachments, decision.Intent);
            return;
        }

        await SendTurnAsync(text, attachments, planOnly, askOnly: IsAskMode && !planOnly);
    }

    /// <summary>
    /// Sends a message the router already read: a plan turn, an explain-only turn, or
    /// a plain one - each in its own mode for the turn, back to Automatic after.
    /// </summary>
    public async Task SendRoutedAsync(string text, IReadOnlyList<TurnAttachment> attachments, RouteIntent intent)
    {
        if (_session is null || !IsConnected)
        {
            // Nothing to send it to (a new session that failed to start): it waits in the composer.
            Draft = text;
            return;
        }

        switch (intent)
        {
            case RouteIntent.Plan:
                await SendTurnAsync(text, attachments, planOnly: true, askOnly: false);
                break;

            case RouteIntent.Ask:
                if (!await EnterAskTurnAsync())
                {
                    return;
                }

                await SendTurnAsync(text, attachments, planOnly: false, askOnly: true);
                break;

            default:
                await SendTurnAsync(text, attachments, planOnly: false, askOnly: false);
                break;
        }
    }

    /// <summary>Sends the CLI's /compact, leaving whatever the user was typing in the composer.</summary>
    public async Task CompactAsync()
    {
        if (_session is null || !IsConnected || IsBusy)
        {
            return;
        }

        var draft = Draft;
        var attachments = Attachments.ToList();
        await SendTurnAsync("/compact", [], planOnly: false, askOnly: false);
        Draft = draft;
        foreach (var attachment in attachments)
        {
            Attachments.Add(attachment);
        }
    }

    private async Task SendTurnAsync(string text, IReadOnlyList<TurnAttachment> attachments, bool planOnly, bool askOnly)
    {
        // A message sent while a turn runs steers it: the CLI folds it into the running
        // turn (one result closes both), so it must not stop or restart anything here.
        if (IsBusy && _activeTurn is not null && _session is { } running)
        {
            await SteerTurnAsync(running, text, attachments);
            return;
        }

        // Plan setup can restart the session (for the effort change), so it runs before
        // the draft is cleared - a failed setup must leave the message in place.
        if (planOnly && !await EnterPlanModeAsync())
        {
            return;
        }

        var session = _session;
        if (session is null)
        {
            return;
        }

        Draft = "";
        NextPromptSuggestion = null;
        Attachments.Clear();
        ComposerNotice = null;
        IsBusy = true;
        _lastUserPrompt = text;
        Add(new UserMessageItem { Text = text, Attachments = attachments, SentAsPlan = planOnly });
        RecordUserTurn(text);

        // The first prompt names the tab - its gist, not a copy - until something better
        // (the helper model's name, a history title) does.
        if (Title is null && text.Length > 0 && !SessionTitles.IsResetCommand(text))
        {
            Title = SessionTitles.FromPrompt(text);
        }
        BeginTurn();

        try
        {
            // The feature's briefing rides on the first real message; a slash command is the
            // CLI's own syntax and would not survive a prefix.
            var seed = text.StartsWith('/') ? null : TakeFeatureSeed(text);
            var context = seed is { Length: > 0 } ? SessionBriefing.ContextBlock(seed) : null;

            await session.SendAsync(new UserTurn(text) { Attachments = attachments, PlanOnly = planOnly, AskOnly = askOnly, ContextPrefix = context });
        }
        catch (Exception ex)
        {
            IsBusy = false;
            RestoreAfterPlanTurn();
            EndTurn(TurnOutcome.NotSent);
            Add(new NoticeItem { Text = $"Send failed: {ex.Message}", Severity = NoticeSeverity.Error });
        }
    }

    /// <summary>
    /// Inserts a prompt into the turn in flight. The timeline so far folds as its own
    /// segment, the prompt lands in the transcript in order, and a fresh segment opens
    /// under it with the live clock carrying on. Busy stays on: the turn is still running.
    /// </summary>
    private async Task SteerTurnAsync(IAgentSession session, string text, IReadOnlyList<TurnAttachment> attachments)
    {
        Draft = "";
        NextPromptSuggestion = null;
        Attachments.Clear();
        ComposerNotice = null;
        _lastUserPrompt = text;

        var started = _turnStarted;
        EndTurn(TurnOutcome.Completed);
        Add(new UserMessageItem { Text = text, Attachments = attachments, IsSteer = true });
        RecordUserTurn(text);
        BeginTurn();
        _turnStarted = started;

        try
        {
            await session.SendAsync(new UserTurn(text) { Attachments = attachments });
        }
        catch (Exception ex)
        {
            // The turn it was meant for is still running; only the message was lost.
            Add(new NoticeItem { Text = $"Send failed: {ex.Message}", Severity = NoticeSeverity.Error });
        }
    }

    /// <summary>
    /// The CLI starts some turns by itself: a background agent's or command's
    /// task-notification is queued as a prompt and answered with no Send from here.
    /// The first main-session output while idle is the only sign of such a turn, so it
    /// reopens the busy state and a turn of its own - without one the stop button
    /// stays a send button and the closing result has no turn to settle (todos included).
    /// </summary>
    private void ResumeUnpromptedTurn(AgentEvent e)
    {
        if (IsBusy)
        {
            return;
        }

        var mainSessionOutput = e switch
        {
            AssistantTextDelta delta => delta.ParentToolUseId is null,
            AssistantThinkingDelta thought => thought.ParentToolUseId is null,
            ToolCallStarted started => started.ParentToolUseId is null,
            AssistantMessageCompleted done => done.ParentToolUseId is null,
            _ => false,
        };

        if (!mainSessionOutput)
        {
            return;
        }

        CrashLog.Trace($"Unprompted turn started by the CLI ({e.GetType().Name})");
        NextPromptSuggestion = null;
        IsBusy = true;
        BeginTurn();
    }

    /// <summary>
    /// Opens the turn's status line under the prompt just sent. The clock starts here
    /// and stays at the top of the turn's block - prompt, status line, tool calls,
    /// replies - until the turn lands, whatever happens in between.
    /// </summary>
    private void BeginTurn()
    {
        // A second prompt while a turn is still open: the CLI interleaves them, but
        // the older turn is over as a unit, so its timeline settles rather than
        // dangling. A stored transcript has no turn results at all - the next prompt
        // is the only sign a turn ended - so a replayed turn ends as completed.
        if (_activeTurn is not null)
        {
            EndTurn(TurnOutcome.Stopped);
        }

        _turnStarted = DateTimeOffset.Now;
        _turnTodo = null;
        _turnPlanFile = null;

        _activeTurn = new TurnActivityItem { HasClock = true };

        // A revision request that has not met its plan yet rides on this new turn; an
        // older one not bound to a turn (its plan's turn ended long ago) does not.
        if (_pendingRevision is not null && _revisionTurn is null)
        {
            _revisionTurn = _activeTurn;
        }

        Add(_activeTurn);

        _workingTicks = 0;
        WorkingElapsed = "";
        WorkingMessage = WorkingPhrases.Next(WorkingMessage);
        _turnTimer.Start();
    }

    /// <summary>
    /// Applies a model/effort pick to the live session without a restart when the CLI
    /// can switch mid-session: Claude has set_model / apply_flag_settings control requests.
    /// Returns false when the live switch is unavailable or rejected - the caller
    /// then restarts - and never throws.
    /// </summary>
    public async Task<bool> TryApplyModelEffortLiveAsync(string? model, string? effort)
    {
        var session = _session;
        if (session is null || !IsConnected)
        {
            return false;
        }

        try
        {
            model = LaunchValue(model);
            effort = LaunchValue(effort);
            var applied = await session.TrySetModelEffortAsync(model, effort);

            if (applied)
            {
                _liveEffort = effort;
                if (_endpointSettings?.ContextWindowFor(EndpointName, model) is { } window && window > 0)
                {
                    _status.ContextWindow = window;
                }
                CrashLog.Trace($"Model/effort applied live: model={model ?? "default"} effort={effort ?? "default"}");
            }

            return applied;
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Live model/effort switch failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Restarts the CLI so spawn-time settings (model, effort) take effect, resuming
    /// onto the same conversation. The fallback for picks the live session cannot
    /// switch (e.g. Claude effort "max", or an older CLI without the control requests).
    /// </summary>
    public async Task RestartWithCurrentConfigAsync()
    {
        var resumeId = Items.Any(i => i is UserMessageItem or TurnActivityItem) ? SessionId : null;
        CrashLog.Trace(
            $"Restart for config: model={RequestedModel ?? "default"} effort={RequestedEffort ?? "default"} " +
            $"resume={resumeId ?? "none"}");

        // The session only: a dev server Codale runs for the agent outlives a model switch.
        await DisposeSessionAsync();
        // The transcript stays: the CLI resumes the same conversation, so the view must too.
        EndTurn(TurnOutcome.Stopped);
        _streaming = null;
        ResumeSessionId = resumeId;

        await ConnectAsync();
    }

    /// <summary>
    /// Moves this chat onto the named provider ("" for Default). The provider is part of the
    /// CLI's spawn environment, so a connected CLI restarts and resumes the same conversation.
    /// The picker is disabled while a turn runs, so a turn is never cut off here.
    /// Nothing connected: the next connect reads the choice anyway.
    /// </summary>
    public async Task SetEndpointAsync(string name)
    {
        name = name.Trim();
        if (string.Equals(EndpointName, name, StringComparison.OrdinalIgnoreCase) || IsBusy)
        {
            return;
        }

        EndpointName = name;
        OnPropertyChanged(nameof(EndpointName));
        RefreshCustomProviderName();

        if (!IsConnected || _session is null || _connecting)
        {
            if (!IsConnected && !_connecting)
            {
                RequestedModel = null;
                AvailableModels = [];
                HasModelCatalog = false;
                OnPropertyChanged(nameof(AvailableModels));
                OnPropertyChanged(nameof(HasModelCatalog));
                OnPropertyChanged(nameof(CanPickModel));
                await ConnectAsync();
            }
            return;
        }

        if (_connectedProvider == SelectedProviderName())
        {
            return;
        }

        // The old endpoint's model names mean nothing to the new one (and vice versa).
        RequestedModel = null;
        AvailableModels = [];
        HasModelCatalog = false;
        OnPropertyChanged(nameof(AvailableModels));
        OnPropertyChanged(nameof(HasModelCatalog));
        OnPropertyChanged(nameof(CanPickModel));

        CrashLog.Trace($"Provider changed {(_connectedProvider is { Length: > 0 } ? _connectedProvider : "Default")} -> {(SelectedProviderName() is { Length: > 0 } n ? n : "Default")}; restarting session");
        await RestartWithCurrentConfigAsync();

        if (IsConnected)
        {
            var defaultLogin = $"the {ProviderName} login";
            var targetName = SelectedProviderName() is { Length: > 0 } now ? now : defaultLogin;
            var text = Items.Any(i => i is UserMessageItem or TurnActivityItem)
                ? $"Switched to {targetName}. The conversation continues."
                : $"Switched to {targetName}.";
            Add(new NoticeItem
            {
                Text = text,
                Severity = NoticeSeverity.Info,
            });
        }
    }

    /// <summary>Stages a picked file for the next turn. Returns false (with a notice) when it cannot be used.</summary>
    public bool TryAddAttachment(string path)
    {
        if (Attachments.Count >= MaxAttachments)
        {
            ComposerNotice = new NoticeItem { Text = $"Up to {MaxAttachments} attachments per message.", Severity = NoticeSeverity.Warning };
            return false;
        }

        var kind = TurnAttachment.KindOf(path);
        if (kind is null)
        {
            ComposerNotice = new NoticeItem { Text = "Only images (png, jpg, gif, webp), PDFs, and text or code files can be attached.", Severity = NoticeSeverity.Warning };
            return false;
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            ComposerNotice = new NoticeItem { Text = $"{Path.GetFileName(path)} no longer exists.", Severity = NoticeSeverity.Warning };
            return false;
        }

        if (info.Length > MaxAttachmentBytes)
        {
            ComposerNotice = new NoticeItem { Text = $"{Path.GetFileName(path)} is larger than 20 MB.", Severity = NoticeSeverity.Warning };
            return false;
        }

        if (Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        Attachments.Add(new ComposerAttachment { Kind = kind.Value, Name = Path.GetFileName(path), Path = path });
        return true;
    }

    /// <summary>At most six files per turn keeps the base64 payload inside a sane request size.</summary>
    public const int MaxAttachments = 6;

    private const long MaxAttachmentBytes = 20 * 1024 * 1024;

    public void RemoveAttachment(ComposerAttachment attachment) => Attachments.Remove(attachment);

    /// <summary>
    /// Recomputes the autocomplete rows from the draft: a leading "/token" matches the
    /// CLI's slash commands, an "@token" matches workspace files. The view owns the
    /// popup itself - open while rows exist.
    /// </summary>
    public void UpdateSuggestions()
    {
        var draft = Draft ?? "";

        // Same token, same rows: keystrokes inside an unchanged query token (or none)
        // used to clear and rebuild the popup per keystroke.
        var key = draft.Length > 0 && draft[0] == '/' && !draft.Contains(' ')
            ? $"cmd:{draft}"
            : draft.LastIndexOf('@') is var atIndex && atIndex >= 0 ? $"file:{draft[(atIndex + 1)..]}" : "";
        if (key == _lastSuggestionKey && Suggestions.Count > 0)
        {
            return;
        }
        _lastSuggestionKey = key;

        Suggestions.Clear();

        if (draft.Length > 0 && draft[0] == '/' && !draft.Contains(' '))
        {
            // "/" alone lists everything; from the second character on it filters.
            var typed = draft[1..];

            foreach (var (name, description) in SlashCommands.Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
            {
                Suggestions.Add(new SuggestionItem
                {
                    Title = "/" + name,
                    Detail = description,
                    Insert = "/" + name,
                    IsCommand = true,
                });
            }

            return;
        }

        var at = draft.LastIndexOf('@');
        if (at < 0)
        {
            return;
        }

        var query = draft[(at + 1)..];
        if (query.Contains(' ') || query.Length > 260)
        {
            return;
        }

        // The index is built in the background on first use; rows appear once it lands.
        _ = EnsureWorkspaceFilesAsync();

        foreach (var file in MatchWorkspaceFiles(query).Take(12))
        {
            Suggestions.Add(new SuggestionItem
            {
                Title = file,
                Detail = Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "",
                Insert = file,
            });
        }
    }

    private IEnumerable<string> MatchWorkspaceFiles(string query)
    {
        if (_workspaceFiles is not { Count: > 0 } files)
        {
            yield break;
        }

        // One pass instead of three: each path's file name is taken once, each candidate
        // is bucketed into the strongest tier, and the first 12 wins short-circuit the
        // rest of the index.
        var prefix = new List<string>();
        var nameContains = new List<string>();
        var pathContains = new List<string>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                // Twelve rows are all the popup shows; a full prefix tier makes the
                // rest of the index irrelevant.
                if (prefix.Count < 12)
                {
                    prefix.Add(file);
                }

                continue;
            }

            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                nameContains.Add(file);
            }
            else if (file.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                pathContains.Add(file);
            }
        }

        foreach (var file in prefix)
        {
            yield return file;
        }

        foreach (var file in nameContains.Take(12 - Math.Min(prefix.Count, 12)))
        {
            yield return file;
        }

        var shown = prefix.Count + Math.Min(nameContains.Count, Math.Max(0, 12 - prefix.Count));
        if (shown < 12)
        {
            foreach (var file in pathContains.Take(12 - shown))
            {
                yield return file;
            }
        }
    }

    private async Task EnsureWorkspaceFilesAsync()
    {
        if (_workspaceFiles is not null || _loadingWorkspaceFiles)
        {
            return;
        }

        _loadingWorkspaceFiles = true;
        try
        {
            var root = WorktreePath ?? _projectPath;
            var files = await Task.Run(() => WorkspaceFileIndex.Enumerate(root));

            if (_workspaceFiles is null)
            {
                _workspaceFiles = files;
                CrashLog.Trace($"Workspace file index: {files.Count} files");
            }
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Workspace file index failed: {ex.Message}");
        }
        finally
        {
            _loadingWorkspaceFiles = false;
        }

        if (_workspaceFiles is { Count: > 0 })
        {
            UpdateSuggestions();
        }
    }

    [RelayCommand]
    private async Task InterruptAsync()
    {
        if (_session is null || !ShowStop)
        {
            return;
        }

        // Stopping the main agent cascades: every subagent still running goes with it.
        var running = RunningTasks
            .Where(t => t.Kind == RunningTaskKind.Subagent && t.Hosted is null && t.IsRunning)
            .ToList();

        if (!IsBusy)
        {
            // Only subagents left (the turn is parked waiting on them): stop those.
            foreach (var task in running)
            {
                await StopTaskAsync(task);
            }

            return;
        }

        // Verified against the CLI (tests/fixtures/claude/interrupt.jsonl): the CLI acks
        // the interrupt and closes the turn with subtype error_during_execution - which
        // the handler below shows as an interruption, not a failure.
        _interruptPending = true;

        try
        {
            await _session.InterruptAsync();
            foreach (var task in running.Where(t => t.IsRunning && t.RuntimeId is not null))
            {
                await StopTaskAsync(task);
            }
        }
        catch (Exception ex)
        {
            _interruptPending = false;
            ComposerNotice = new NoticeItem { Text = $"Interrupt failed: {ex.Message}", Severity = NoticeSeverity.Warning };
        }
    }

    private void ApplyTodos(JsonElement input)
    {
        // A malformed call keeps the last good list rather than blanking the panel.
        if (TodoParser.FromToolInput(input) is { Count: > 0 } todos)
        {
            ShowTodos(todos);
            UpdateTodoStep();
        }
    }

    private void ShowTodos(IReadOnlyList<TodoItem> todos)
    {
        Todos.ReplaceAll(todos);

        TasksDone = todos.Count(t => t.IsDone);
        TasksTotal = todos.Count;
        OnPropertyChanged(nameof(ActiveTask));
        OnPropertyChanged(nameof(StatusDetail));

        TodosChanged?.Invoke(this, todos);
    }

    /// <summary>Raised so the workspace can persist the list.</summary>
    public event EventHandler<IReadOnlyList<TodoItem>>? TodosChanged;

    /// <summary>
    /// Drops the conversation entirely: transcript, tool state, todos and any resume
    /// id. The next <see cref="ConnectAsync"/> spawns a brand-new session; the old
    /// one stays reachable through the session list.
    /// </summary>
    public void ResetForNewSession()
    {
        EndTurn(TurnOutcome.Stopped);
        Items.Clear();
        _toolCalls.Clear();
        _hiddenCalls.Clear();
        StopTaskHost();
        ClearTasks();
        AbandonPendingApprovals();
        _streaming = null;
        Title = null;
        ShowTodos([]);
        Artifacts.Clear();
        _status.ClearSessionChanges();
        _messageTokens.Clear();
        _status.SessionTokens = 0;
        ResumeSessionId = null;
        ClearFeature();
        WorktreePath = null;
    }

    /// <summary>The prose being streamed: a narration step in the running turn, or a bare message outside one.</summary>
    private AssistantMessageItem Streaming()
    {
        if (_streaming is null)
        {
            _streaming = new AssistantMessageItem();

            if (_activeTurn is { } turn)
            {
                turn.Steps.Add(_streaming);
            }
            else
            {
                Add(_streaming);
            }
        }

        return _streaming;
    }

    /// <summary>Takes an item out of wherever it sits: the running turn's timeline or the transcript.</summary>
    private void RemoveItem(ChatItem item)
    {
        if (_activeTurn?.Steps.Remove(item) != true)
        {
            Items.Remove(item);
            Reflow();
        }
    }

    /// <summary>
    /// Adds a Claude request's tokens to the override's session total, once per message:
    /// the CLI repeats a message, and its stream's message_delta reports it again. The
    /// larger reading wins, so a later partial one never takes tokens back.
    /// Codex reports its own session total (SessionTokensUpdated).
    /// </summary>
    private void CountMessageTokens(string messageId, UsageSnapshot usage)
    {
        if (!_status.IsOverrideCli || IsCodexSession)
        {
            return;
        }

        _messageTokens.TryGetValue(messageId, out var counted);
        if (usage.TotalTokens > counted)
        {
            _messageTokens[messageId] = usage.TotalTokens;
            _status.SessionTokens += usage.TotalTokens - counted;
        }
    }

    private void CompleteStreaming(AssistantMessageCompleted done)
    {
        // Every assistant message carries the usage of its own API request: fresh input
        // plus cache reads/creation is exactly what the context window holds at that
        // moment. Recorded for the end of the turn, because the result event's usage is
        // the turn's CUMULATIVE total - every request's tokens summed, the number the
        // CLI builds total_cost_usd from - which is not what the window holds now.
        // Subagent messages (they carry a parent tool use) read a different context.
        if (done.ParentToolUseId is null && done.Usage is { } usage)
        {
            _lastMessageUsage = usage;
            _status.Apply(usage);
        }

        // A message that was only a tool call has no text worth a bubble.
        if (_streaming is null && string.IsNullOrWhiteSpace(done.Text))
        {
            return;
        }

        var item = _streaming ?? Streaming();
        item.Complete(done.Text);
        item.HasThinking = done.HasThinking;
        _streaming = null;

        if (string.IsNullOrWhiteSpace(item.Text) && !item.HasThinking)
        {
            RemoveItem(item);
        }
    }

    private void MarkDenied(string? toolUseId)
    {
        if (toolUseId is not null && _toolCalls.TryGetValue(toolUseId, out var call))
        {
            call.Status = ToolCallStatus.Denied;
            call.Duration ??= DateTimeOffset.Now - call.Timestamp;
            call.AutoCollapse();

            if (call.IsPlan)
            {
                SetPlanStatus(call.ToolUseId, SessionArtifactStatus.Revised);
            }
        }
    }

    private void Add(ChatItem item)
    {
        Items.Add(item);
        Reflow();
    }

    /// <summary>
    /// Neighbor-aware transcript spacing: rows of one block - the tool run, the prose
    /// answer - sit close together, and a boundary between blocks gets room to breathe.
    /// Recomputed over the whole list after every transcript mutation; the list is short,
    /// and a margin set is a no-op when the neighborhood did not change.
    /// </summary>
    private void Reflow()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].SetRowMargin(RowMargin(i > 0 ? Items[i - 1] : null, Items[i]));
        }
    }

    /// <summary>The gap table: everything answers to the user's prompt, otherwise block against block.</summary>
    private static Thickness RowMargin(ChatItem? above, ChatItem item)
    {
        var top = above is null ? 0 : (above.Block, item.Block) switch
        {
            (_, TranscriptBlock.User) => 16,                       // room before the next prompt
            (TranscriptBlock.Notice, TranscriptBlock.Notice) => 4,
            (_, TranscriptBlock.Notice) => 6,                      // notices hug what they annotate
            (TranscriptBlock.User, _) => 10,
            (TranscriptBlock.Notice, _) => 6,
            (TranscriptBlock.Tool, TranscriptBlock.Tool) => 4,     // the tool run reads as one block
            (TranscriptBlock.Tool, TranscriptBlock.Prose) => 16,   // the tool run, then the answer
            (TranscriptBlock.Prose, TranscriptBlock.Tool) => 12,
            (TranscriptBlock.Prose, TranscriptBlock.Prose) => 8,
            _ => 8,
        };

        return new Thickness(item.RowInset.Left, top, item.RowInset.Right, 0);
    }

    [GeneratedRegex("\u001B\\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiCodes();

    private const int MaxOutputLines = 80;

    /// <summary>
    /// Tool output keeps the shape it arrived in: newlines and the column runs of a
    /// table stay put, tabs widen to spaces (a terminal convention the mono block
    /// cannot reproduce otherwise), ANSI colouring is stripped, and only sheer size
    /// is capped - a tail note says how much was dropped.
    /// </summary>
    private static string? CapOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Trailing blank lines are dropped; the cap then scans for the 80th line break
        // instead of splitting the whole output into lines first.
        var clean = AnsiCodes().Replace(text, "").ReplaceLineEndings("\n").TrimEnd('\n');
        var totalLines = clean.AsSpan().Count('\n') + 1;

        string body;
        if (totalLines <= MaxOutputLines)
        {
            body = clean.Replace("\t", "    ");
        }
        else
        {
            var cut = -1;
            for (var i = 0; i < MaxOutputLines; i++)
            {
                cut = clean.IndexOf('\n', cut + 1);
            }

            body = clean[..cut].Replace("\t", "    ") + $"\n… {totalLines - MaxOutputLines} more lines";
        }

        return body.Length > 12000 ? TextClip.Truncate(body, 12000) + "\n…" : body;
    }

    /// <summary>
    /// Ends the chat: the session, and the tasks Codale runs for it. Used when the tab
    /// closes; <see cref="DisposeSessionAsync"/> is the part a restart wants.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (SmartHistory && _recording && FeatureId is { } featureId)
        {
            Features!.Summarize(featureId, force: true);
        }

        StopTaskHost();
        await DisposeSessionAsync();
    }

    /// <summary>
    /// Stops the CLI session only, leaving the task host (and its dev servers) running:
    /// a model switch restarts the session and resumes the same conversation.
    /// </summary>
    public async ValueTask DisposeSessionAsync()
    {
        // A tab closed mid-turn would otherwise leave the 500ms timer ticking forever,
        // keeping this view model and its whole transcript rooted.
        _turnTimer.Stop();

        // Nobody is left to answer an open question once the session goes.
        AbandonPendingApprovals();

        // The pump drops the dying session's SessionEnded, so a turn in flight has to be
        // closed here, or the busy pulses and the stop button animate forever.
        if (IsBusy)
        {
            IsBusy = false;
            EndTurn(TurnOutcome.Stopped);
        }

        if (_session is { } session)
        {
            CrashLog.Trace($"Dispose session: session={session.SessionId ?? "unknown"}");

            // Detach first: the dying process's SessionEnded (and its stderr, e.g. a failed
            // default-login connect) then reaches a pump that no longer owns it and is dropped.
            _session = null;
            await session.DisposeAsync();
        }
    }
}