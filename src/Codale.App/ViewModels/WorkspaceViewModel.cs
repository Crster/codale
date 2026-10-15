using System.Collections.ObjectModel;
using System.ComponentModel;

using Codale.Agents.History;
using Codale.Agents;
using Codale.Agents.Claude;
using Codale.Agents.OpenAi;
using Codale.App.Services;
using Codale.Commands;
using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Core.Tasks;
using Codale.Git;
using Codale.Storage;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>One project, one window. Everything the three panels bind to hangs off this.</summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CodaleStore _store;
    private readonly string _databasePath;
    private readonly CliEndpointSettings _endpoints;
    private readonly McpServerSettings _mcp;

    /// <summary>Sessions this window has already tried to title, so each is attempted once.</summary>

    /// <summary>
    /// Background jobs (titles, commit messages, routing, search) answered by one-shot calls
    /// to the Claude CLI, or to a BYOK provider's API when Settings says so.
    /// </summary>
    private readonly HelperModel _helper = new(
        () => AppSettings.HelperApiEndpoint is { } api ? new OpenAiEndpoint(api.BaseUrl, api.ApiKey, api.Model) : null,
        () => AppSettings.HelperProvider,
        () => AppSettings.TierModelFor("codex", "flash") ?? HelperModel.DefaultCodexModel);

    /// <summary>Raised when a background task fails due to missing CLI authentication.</summary>
    public event EventHandler<string?>? HelperLoginRequired;

    /// <summary>The project's features: the history every chat in the window shares.</summary>
    public FeatureService Features { get; }

    /// <summary>Automatic mode's message reader, shared by every chat in the window.</summary>
    private readonly MessageRouter _router;

    public WorkspaceViewModel(string projectPath)
    {
        ProjectPath = ProjectPaths.Normalize(projectPath);
        DisplayName = ProjectPaths.DisplayName(ProjectPath);

        (_store, _databasePath, StorageWarning) = OpenStore();

        ExtraFolders = new WorkspaceFolders(_store, ProjectPath);
        _helper.LoginRequired += cli => HelperLoginRequired?.Invoke(this, cli);
        Files = new FileTreeViewModel(ProjectPath, ExtraFolders);
        Git = new GitViewModel(ProjectPath, _helper);
        Search = new SearchViewModel(ProjectPath, _helper);
        Diff = new DiffViewModel(ProjectPath);
        DiffViewer = new DiffViewerViewModel(ProjectPath);
        Features = new FeatureService(ProjectPath, _helper);
        Sessions = new SessionsViewModel(ProjectPath, Features);
        _endpoints = new CliEndpointSettings();
        _mcp = new McpServerSettings(_store, ProjectPath);
        _mcp.TerminalServersProvider = () => Panel is { IsTerminalSession: true } session ? session.ServersForTerminalCli() : _mcp.Servers();
        McpTools = new McpToolsViewModel(_mcp);
        CliEndpoint = new CliEndpointViewModel();
        _router = new MessageRouter(_helper);
        CliEndpoint.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CliEndpointViewModel.ActiveName))
            {
                OnPropertyChanged(nameof(EndpointLabel));
            }
        };

        // The background-task provider follows Settings ("Background tasks"), the BYOK
        // provider picker - none of which are the chat in front.
        AppSettings.Changed += OnAppSettingsChanged;
        StartElapsedTimer();
        _helper.ApiRequestServed += OnBackgroundRequestServed;

        Sessions.SessionOpened += OnSessionOpened;
        Git.SnapshotRefreshed += OnGitSnapshotRefreshed;
        Git.NestedSnapshotsRefreshed += OnNestedSnapshotsRefreshed;
        Git.WorkingTreeChanged += OnGitWorkingTreeChanged;

        Chat = CreateChat();
        Panel = Chat;
    }

    /// <summary>
    /// Set when the database could not be used (written by a newer Codale): the window runs
    /// on a throwaway one, so history, titles and recovery do not persist. Null when all is well.
    /// </summary>
    public string? StorageWarning { get; }

    private static (CodaleStore Store, string Path, string? Warning) OpenStore()
    {
        try
        {
            return (new CodaleStore(CodaleStore.DefaultDatabasePath), CodaleStore.DefaultDatabasePath, null);
        }
        catch (StoreSchemaException ex)
        {
            CrashLog.Warn("workspace", $"database not usable, running on a temporary one: {ex.Message}");
            var temporary = Path.Combine(Path.GetTempPath(), "codale-temporary", $"{Environment.ProcessId}.db");
            return (new CodaleStore(temporary), temporary, ex.Message + " Session history will not be saved in this window.");
        }
    }

    public string ProjectPath { get; }

    public string DisplayName { get; }

    /// <summary>The status of the chat in front: each conversation keeps its own.</summary>
    public StatusViewModel Status => Chat.Status;

    public FileTreeViewModel Files { get; }

    /// <summary>Folders added beside the project root; the agent is granted them at session start.</summary>
    public WorkspaceFolders ExtraFolders { get; }

    /// <summary>
    /// The conversation in front - the one whose tab is selected, and the one the
    /// session panel and status bar show. Every open chat tab has its own; they all
    /// keep running while another is in front.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(WorkingDirectory))]
    [NotifyPropertyChangedFor(nameof(IsIsolated))]
    [NotifyPropertyChangedFor(nameof(EndpointLabel))]
    public partial ChatViewModel Chat { get; set; }

    /// <summary>
    /// The session the panel shows: the chat in front for a Claude chat tab (the full panel), or the
    /// headless session of the terminal CLI in front (the simple panel). Each agent session has its
    /// own; the panel is not shared. It keeps the last session while a non-agent tab is in front.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkingDirectory))]
    [NotifyPropertyChangedFor(nameof(IsIsolated))]
    [NotifyPropertyChangedFor(nameof(EndpointLabel))]
    [NotifyPropertyChangedFor(nameof(ShowSessionPickers))]
    public partial ChatViewModel Panel { get; set; }

    /// <summary>The provider picker follows the session on the panel; a terminal CLI's has none to switch.</summary>
    partial void OnPanelChanged(ChatViewModel value)
    {
        CliEndpoint.Attach(value);
    }

    /// <summary>The chat in front is also the one the panel shows, unless a terminal CLI's session is (the page moves the panel when a tab comes to front).</summary>
    partial void OnChatChanged(ChatViewModel value)
    {
        if (Panel is not { IsTerminalSession: true })
        {
            Panel = value;
        }
    }

    /// <summary>The panel header: the terminal CLI's name ("Antigravity"), else the selected custom provider's name, else "Default".</summary>
    public string EndpointLabel =>
        Panel.TerminalCli is { } cli
            ? cli.DisplayName
            : CliEndpoint.IsActive && CliEndpoint.ActiveName is { Length: > 0 } name ? name : "Default";

    /// <summary>The model, effort and mode tiles: only a Claude chat has them; a CLI in a terminal gets the simple panel.</summary>
    public bool ShowSessionPickers => !Panel.IsTerminalSession;

    /// <summary>The start-over buttons begin a Claude conversation, so they belong to a chat's panel.</summary>
    /// <summary>Each terminal CLI's headless session, one per CLI tab; kept apart from <see cref="Chats"/> (those are tabs).</summary>
    public ObservableCollection<ChatViewModel> TerminalSessions { get; } = [];

    /// <summary>
    /// A session for an agent CLI launched in a terminal: it owns the task host the CLI reports its
    /// todos, artifacts and tasks to, and the changes attributed to it, so its panel is its own.
    /// </summary>
    public ChatViewModel CreateTerminalSession(Codale.Agents.CliDescriptor cli)
    {
        var session = new ChatViewModel(ProjectPath, new StatusViewModel(_customUsage), _endpoints, _mcp) { Router = _router, Helper = _helper, ExtraFolders = ExtraFolders, Terminals = new TerminalSource(this), TerminalCli = cli };
        TerminalSessions.Add(session);
        return session;
    }

    /// <summary>The CLI ended or its tab closed: its session goes, and with it its pipe and task host.</summary>
    public async Task RemoveTerminalSessionAsync(ChatViewModel session)
    {
        _terminalBaselines.Remove(session);
        _terminalLastOutput.TryRemove(session, out _);
        foreach (var path in _changeOwners.Where(o => ReferenceEquals(o.Value, session)).Select(o => o.Key).ToList())
        {
            _changeOwners.Remove(path);
        }

        TerminalSessions.Remove(session);
        _mcp.ClearStaged(SessionKey(session));
        await session.DisposeAsync();
    }

    private readonly CustomUsageTally _customUsage = new();

    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        foreach (var chat in Chats)
        {
            chat.RefreshCustomProviderName();
        }
    }

    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    /// <summary>One background request the custom API answered; may arrive off the UI thread.</summary>
    private void OnBackgroundRequestServed(UsageSnapshot usage)
    {
        if (_uiContext is null)
        {
            _customUsage.Record(usage);
        }
        else
        {
            _uiContext.Post(_ => _customUsage.Record(usage), null);
        }
    }

    /// <summary>Every open conversation, one per chat tab.</summary>
    public ObservableCollection<ChatViewModel> Chats { get; } = [];

    /// <summary>The Claude CLI's endpoint override, editable from the status bar flyout.</summary>
    public CliEndpointViewModel CliEndpoint { get; }

    /// <summary>The built-in browser and desktop-control servers, editable from the status bar flyout.</summary>
    public McpToolsViewModel McpTools { get; }

    /// <summary>
    /// Writes the session's Codale MCP servers into the workspace config (Antigravity reads it at startup), and
    /// takes back what earlier builds wrote. A shared file: the CLI launched last wins, so it is written per launch.
    /// </summary>
    public void InjectMcpForTerminalCli(ChatViewModel session) =>
        _mcp.SyncWorkspaceMcpConfig(servers: session.ServersForTerminalCli());

    /// <summary>
    /// Adds this session's Codale MCP servers to an agent CLI launch typed at a terminal prompt (its own pipe,
    /// so what the CLI reports lands in its own panel); null when the line is not one.
    /// </summary>
    public string? RewriteCliLaunch(string promptLine, ChatViewModel session) =>
        _mcp.RewriteCliLaunch(promptLine, session.ServersForTerminalCli(), SessionKey(session));

    /// <summary>Names one session's staged launch files.</summary>
    private static string SessionKey(ChatViewModel session) =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(session).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Opens a dedicated terminal for an agent CLI session: the shell runs only the CLI and exits
    /// with it, so the tab knows when the session ends. The terminal carries the session its panel shows.
    /// </summary>
    public TerminalViewModel RunCliTerminal(string command, Codale.Agents.CliDescriptor cli, string directory, ChatViewModel session)
    {
        var terminal = new TerminalViewModel(directory, command, cli.DisplayName, cli.Id) { AgentSession = session };
        terminal.PropertyChanged += OnCommandTerminalPropertyChanged;
        lock (_terminals)
        {
            _terminals.Add(terminal);
        }

        UpdateRunningCommandCount();
        return terminal;
    }

    /// <summary>
    /// A chat needs to be on screen: opened from history, resumed, or started fresh.
    /// The page opens its tab (or selects the one it has) and brings it to the front.
    /// </summary>
    public event EventHandler<ChatViewModel>? ChatActivationRequested;

    /// <summary>A new conversation with its own status, wired to the workspace's refreshes. Null <paramref name="cliId"/> keeps the default CLI.</summary>
    public ChatViewModel CreateChat(string? cliId = null)
    {
        var chat = new ChatViewModel(ProjectPath, new StatusViewModel(_customUsage), _endpoints, _mcp)
        {
            Router = _router, Helper = _helper, Features = Features, ExtraFolders = ExtraFolders, Terminals = new TerminalSource(this),
            CliId = cliId is { Length: > 0 } ? cliId : Codale.Agents.CliLocator.ActiveCli.Id,
        };

        // Model and effort come from the CLI's saved default pick (or its own default);
        // the mode is the last one the user picked for this CLI.
        chat.PermissionMode = AppSettings.DefaultModeFor(chat.CliId);

        chat.TurnFinished += OnTurnFinished;
        chat.FilesChanged += OnChatFilesChanged;
        chat.SessionStateChanged += OnSessionStateChanged;
        chat.TodosChanged += OnTodosChanged;
        chat.NewTopicRequested += OnNewTopicRequested;
        chat.PropertyChanged += OnChatPropertyChanged;
        Chats.Add(chat);
        return chat;
    }

    /// <summary>
    /// A chat to start something new in: the one in front when it holds nothing yet,
    /// else a new one - opening a session never ends another that is still going.
    /// </summary>
    /// <summary>
    /// A chat with nothing in it yet: the one in front when it holds nothing and runs the
    /// wanted CLI, else the first blank unconnected one that does, else a new one.
    /// </summary>
    public ChatViewModel FreshChat(string? cliId = null)
    {
        bool Wants(ChatViewModel chat) => cliId is null || string.Equals(chat.CliId, cliId, StringComparison.Ordinal);

        if (!Chat.HasConversation && Wants(Chat))
        {
            return Chat;
        }

        return Chats.FirstOrDefault(c => !c.HasConversation && !c.IsConnected && Wants(c))
            ?? CreateChat(cliId);
    }

    /// <summary>The chat's tab closed: its session ends, and the front moves to another chat.</summary>
    public async Task CloseChatAsync(ChatViewModel chat)
    {
        CrashLog.Trace($"Close chat: session={chat.SessionId ?? "none"}");

        chat.TurnFinished -= OnTurnFinished;
        chat.FilesChanged -= OnChatFilesChanged;
        chat.SessionStateChanged -= OnSessionStateChanged;
        chat.TodosChanged -= OnTodosChanged;
        chat.NewTopicRequested -= OnNewTopicRequested;
        chat.PropertyChanged -= OnChatPropertyChanged;
        Chats.Remove(chat);

        // The panel always needs a chat to show; with the last one gone, a blank one
        // waits for the next chat tab.
        if (ReferenceEquals(Chat, chat))
        {
            Chat = Chats.LastOrDefault() ?? CreateChat();
        }

        PublishOpenSessions();
        await chat.DisposeAsync();
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.IsBusy) or nameof(ChatViewModel.IsConnected) or nameof(ChatViewModel.FeatureId))
        {
            PublishOpenSessions();
        }
        else if (e.PropertyName == nameof(ChatViewModel.WorktreePath) && ReferenceEquals(sender, Panel))
        {
            OnPropertyChanged(nameof(WorkingDirectory));
            OnPropertyChanged(nameof(IsIsolated));
        }
    }

    /// <summary>Tells the history list which features are open in a tab, and which are working.</summary>
    private void PublishOpenSessions() =>
        Sessions.UpdateOpenSessions(
            Chats.Where(c => c.IsConnected && c.FeatureId is not null)
                .GroupBy(c => c.FeatureId!)
                .ToDictionary(g => g.Key, g => g.Any(c => c.IsBusy)));

    /// <summary>Every open terminal, each with its own shell; closed ones are removed.</summary>
    private readonly List<TerminalViewModel> _terminals = [];

    /// <summary>
    /// A fresh terminal with its own pseudoconsole. The caller owns showing it;
    /// disposing the workspace terminates every remaining shell.
    /// </summary>
    public TerminalViewModel CreateTerminal()
    {
        var terminal = new TerminalViewModel(ProjectPath);
        lock (_terminals)
        {
            _terminals.Add(terminal);
        }

        return terminal;
    }

    /// <summary>Raised on the UI thread for a command tab the agent started; the page shows it as a tab.</summary>
    public event Action<TerminalViewModel>? AgentTerminalStarted;

    /// <summary>Command tabs the agent started, so list_tasks and stop_task can tell them from the user's.</summary>
    private readonly HashSet<string> _agentTerminalIds = [];

    private Task<TerminalViewModel?> OpenAgentTerminalAsync(string command, string? name)
    {
        if (_uiContext is null || AgentTerminalStarted is null)
        {
            return Task.FromResult<TerminalViewModel?>(null);
        }

        var opened = new TaskCompletionSource<TerminalViewModel?>();
        _uiContext.Post(_ =>
        {
            try
            {
                // A live terminal for the same command is reused rather than starting a second copy
                // (e.g. two dev servers fighting over the same port), so the caller surfaces that tab.
                TerminalViewModel? live;
                var trimmedCommand = command.Trim();
                var trimmedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                lock (_terminals)
                {
                    live = _terminals.FirstOrDefault(t =>
                        t.IsCommandTerminal &&
                        t.IsRunning &&
                        t.Activity == TerminalActivity.Busy &&
                        (string.Equals(t.CommandText?.Trim(), trimmedCommand, StringComparison.Ordinal) ||
                         (trimmedName is not null && string.Equals(t.CommandName?.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase))));
                }

                if (live is not null)
                {
                    lock (_terminals)
                    {
                        live.IsAgentTerminal = true;
                        _agentTerminalIds.Add(live.Id);
                    }

                    AgentTerminalStarted?.Invoke(live);
                    opened.SetResult(live);
                    return;
                }

                // Built on the UI thread: the terminal posts its state changes back to where it was made.
                var terminal = new TerminalViewModel(ProjectPath, command, name)
                {
                    IsAgentTerminal = true,
                };
                terminal.PropertyChanged += OnCommandTerminalPropertyChanged;
                lock (_terminals)
                {
                    _terminals.Add(terminal);
                    _agentTerminalIds.Add(terminal.Id);
                }

                UpdateRunningCommandCount();
                AgentTerminalStarted?.Invoke(terminal);
                opened.SetResult(terminal);
            }
            catch (Exception ex)
            {
                opened.SetException(ex);
            }
        }, null);
        return opened.Task;
    }

    public bool IsAgentTerminal(TerminalViewModel terminal)
    {
        lock (_terminals)
        {
            return terminal.IsAgentTerminal || _agentTerminalIds.Contains(terminal.Id);
        }
    }

    /// <summary>The open terminals as the agent's terminal tools read them; safe to call off the UI thread.</summary>
    private sealed class TerminalSource(WorkspaceViewModel workspace) : ITerminalSource
    {
        public async Task<TaskSnapshot?> StartAsync(string command, string? name, int waitSeconds, CancellationToken ct)
        {
            if (await workspace.OpenAgentTerminalAsync(command, name).ConfigureAwait(false) is not { } terminal)
            {
                return null;
            }

            // Early output (a port number, a compile error) is what the agent needs next.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 60));
            while (terminal.Activity == TerminalActivity.Busy && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            return Started(terminal, null, 8000);
        }

        public async Task<TaskSnapshot?> StopAsync(string id, CancellationToken ct)
        {
            if (workspace.TerminalsSnapshot().FirstOrDefault(t => t.Id == id && workspace.IsAgentTerminal(t)) is not { } terminal)
            {
                return null;
            }

            // Ctrl+C ends the command and leaves the tab, with what it printed, for the user.
            await terminal.WriteAsync("\u0003").ConfigureAwait(false);
            await Task.Delay(1500, ct).ConfigureAwait(false);
            return Started(terminal, null, 2000);
        }

        public IReadOnlyList<TaskSnapshot> ListStarted() =>
            [.. workspace.TerminalsSnapshot().Where(workspace.IsAgentTerminal).Select(t => Started(t, null, 0))];

        /// <summary>A tab's shell outlives its command: for a command the agent started, idle means it finished.</summary>
        private static TaskSnapshot Started(TerminalViewModel t, long? since, int? tailChars)
        {
            var snapshot = Snapshot(t, since, tailChars);
            return snapshot.State == "idle" ? snapshot with { State = "exited" } : snapshot;
        }

        public IReadOnlyList<TaskSnapshot> List() => [.. workspace.TerminalsSnapshot().Select(t => Snapshot(t, null, 0))];

        public TaskSnapshot? Read(string id, long? since, int? tailChars) =>
            workspace.TerminalsSnapshot().FirstOrDefault(t => t.Id == id) is { } terminal ? Snapshot(terminal, since, tailChars) : null;

        private static TaskSnapshot Snapshot(TerminalViewModel t, long? since, int? tailChars)
        {
            var (text, next) = tailChars == 0 ? ("", t.Transcript.Read().NextOffset) : t.Transcript.Read(since, tailChars);

            // A command tab's shell outlives its command, so "idle" there means the command has finished.
            var state = t.Activity switch
            {
                TerminalActivity.Busy => "running",
                TerminalActivity.Idle => "idle",
                _ => "exited",
            };
            return new TaskSnapshot(
                t.Id,
                t.CommandName ?? "shell",
                t.CommandText ?? "(interactive shell)",
                state,
                t.ExitCode,
                text,
                next);
        }
    }

    private TerminalViewModel[] TerminalsSnapshot()
    {
        lock (_terminals)
        {
            return [.. _terminals];
        }
    }

    /// <summary>The tab was closed: stop tracking (and killing) this shell.</summary>
    public void RemoveTerminal(TerminalViewModel terminal)
    {
        terminal.PropertyChanged -= OnCommandTerminalPropertyChanged;
        terminal.Close();
        lock (_terminals)
        {
            _terminals.Remove(terminal);
            _agentTerminalIds.Remove(terminal.Id);
        }

        UpdateRunningCommandCount();
    }

    /// <summary>The commands menu's fresh read of the project's config files.</summary>
    public IReadOnlyList<ProjectCommand> DiscoverCommands() => CommandCatalog.Discover(ProjectPath);

    /// <summary>Command terminals whose command is still executing; the badge and the menu read this.</summary>
    public IReadOnlyList<TerminalViewModel> RunningCommands =>
        _terminals.Where(t => t.IsCommandTerminal && t.Activity == TerminalActivity.Busy).ToList();

    /// <summary>How many commands are executing; the title-bar play button's badge.</summary>
    [ObservableProperty]
    public partial int RunningCommandCount { get; private set; }

    /// <summary>
    /// Opens a terminal running the command and returns it. A live terminal for the
    /// same command is reused rather than starting a second copy - a second dev
    /// server would only fight over the port - so the caller surfaces that tab.
    /// </summary>
    public TerminalViewModel RunProjectCommand(ProjectCommand command)
    {
        var trimmedCommand = command.Command.Trim();
        var trimmedName = command.Name.Trim();
        lock (_terminals)
        {
            if (_terminals.FirstOrDefault(t =>
                    t.IsCommandTerminal &&
                    t.IsRunning &&
                    t.Activity == TerminalActivity.Busy &&
                    (string.Equals(t.CommandName?.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t.CommandText?.Trim(), trimmedCommand, StringComparison.Ordinal))) is { } live)
            {
                return live;
            }
        }

        var terminal = new TerminalViewModel(ResolveCommandDirectory(command), command.Command, command.Name);
        terminal.PropertyChanged += OnCommandTerminalPropertyChanged;
        lock (_terminals)
        {
            _terminals.Add(terminal);
        }

        UpdateRunningCommandCount();

        return terminal;
    }

    private void OnCommandTerminalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.Activity))
        {
            UpdateRunningCommandCount();
        }
    }

    private void UpdateRunningCommandCount() =>
        RunningCommandCount = _terminals.Count(t => t.IsCommandTerminal && t.Activity == TerminalActivity.Busy);

    /// <summary>The command's directory, relative ones resolved against the project root.</summary>
    private string ResolveCommandDirectory(ProjectCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            return ProjectPath;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(ProjectPath, command.WorkingDirectory));
        }
        catch (ArgumentException)
        {
            return ProjectPath;
        }
    }

    /// <summary>
    /// The project's commands file, created from a starter template when missing:
    /// "edit commands" should land in a file that exists, not an empty editor error.
    /// </summary>
    public string EnsureCommandsFile()
    {
        var path = Path.Combine(ProjectPath, ".codale", "commands.json");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {
                  "commands": [
                    { "name": "build", "command": "dotnet build" },
                    { "name": "run", "command": "dotnet run", "cwd": "src/MyApp" }
                  ]
                }
                """);
        }

        return path;
    }

    public GitViewModel Git { get; }

    /// <summary>The one-shot model behind editor actions: the chosen custom provider, else the Claude CLI.</summary>
    public IHelperModel Helper => _helper;

    public SearchViewModel Search { get; }

    public DiffViewModel Diff { get; }

    /// <summary>The centre-panel diff tab's view model: one file, or a whole commit.</summary>
    public DiffViewerViewModel DiffViewer { get; }

    public SessionsViewModel Sessions { get; }

    /// <summary>Every open editor tab's view model, so closing the window can ask about unsaved edits. The page registers and unregisters them.</summary>
    private readonly List<EditorViewModel> _editors = [];

    public void RegisterEditor(EditorViewModel editor)
    {
        if (!_editors.Contains(editor))
        {
            _editors.Add(editor);
        }
    }

    public void UnregisterEditor(EditorViewModel editor) => _editors.Remove(editor);

    /// <summary>True when any open editor holds edits that are not on disk.</summary>
    public bool HasDirtyEditors => _editors.Any(e => e.IsDirty && e.CanEdit);

    /// <summary>The names of the tabs with unsaved edits, for the close prompt.</summary>
    public IReadOnlyList<string> DirtyEditorNames =>
        _editors.Where(e => e.IsDirty && e.CanEdit).Select(e => e.FileName ?? "Untitled").ToList();

    /// <summary>
    /// Saves every dirty editor that has a file. Returns the names of those still unsaved:
    /// untitled buffers (they need a name from their own tab), files that changed on disk
    /// meanwhile, and failed writes.
    /// </summary>
    public async Task<IReadOnlyList<string>> SaveAllEditorsAsync()
    {
        foreach (var editor in _editors.Where(e => e.IsDirty && e.CanEdit && e.FilePath is not null).ToList())
        {
            await editor.SaveAsync();
        }

        return DirtyEditorNames;
    }

    /// <summary>
    /// The view model of whichever editor tab is in front; each editor tab owns its
    /// own, and null means another kind of tab is active. The status bar reads this.
    /// </summary>
    [ObservableProperty]
    public partial EditorViewModel? ActiveEditor { get; set; }

    /// <summary>True while an old transcript is being read instead of the live session.</summary>
    [ObservableProperty]
    public partial bool IsViewingHistory { get; set; }

    [ObservableProperty]
    public partial string? HistoryTitle { get; set; }

    /// <summary>
    /// Window-load work that does not involve a CLI: the centre area starts empty, so
    /// no agent session is spawned until the user opens a chat tab - and the session
    /// panel's history stays unpopulated until the panel first appears.
    /// </summary>
    public async Task InitializeAsync()
    {
        Git.Start();
        await Sessions.RefreshAsync();

        await Diff.RefreshAsync();
    }

    /// <summary>
    /// Starts a fresh session in its own git worktree on a new branch.
    /// </summary>
    /// <remarks>
    /// Opt-in rather than the default: running every session in a worktree would mean
    /// the agent's edits never appear in the project the user is looking at, which is
    /// surprising. It earns its place when you want the agent working on something
    /// without touching your working tree, or two sessions running at once.
    /// </remarks>
    public async Task StartIsolatedSessionAsync()
    {
        IsStartingWorktree = true;

        try
        {
            var worktrees = new GitWorktrees(ProjectPath);
            var sessionId = Guid.NewGuid().ToString();
            var created = await worktrees.TryCreateAsync(sessionId);

            if (created.Path is not { } path)
            {
                WorktreeError = created.Error is { Length: > 0 } error
                    ? $"Could not create a worktree: {error}"
                    : "Could not create a worktree. Is this a git repository with at least one commit?";
                return;
            }

            WorktreeError = null;
            CrashLog.Trace($"Isolated session: worktree={path}");

            // Same rule as New session: its own tab unless the chat in front holds nothing.
            // The reused chat is reset below, so anything on screen - a notice, a task,
            // an artifact - counts as something, not just a conversation.
            var chat = IsBlank(Chat) ? Chat
                : Chats.FirstOrDefault(c => IsBlank(c) && !c.IsConnected) ?? CreateChat();
            await chat.DisposeSessionAsync();
            chat.ResetForNewSession();
            chat.PermissionMode = AppSettings.DefaultModeFor(chat.CliId);
            chat.WorktreePath = path;
            ChatActivationRequested?.Invoke(this, chat);

            await chat.ConnectAsync();
            await Diff.RefreshAsync();
        }
        finally
        {
            IsStartingWorktree = false;
        }
    }

    /// <summary>True when resetting the chat would lose nothing: no conversation, no running turn, an empty transcript.</summary>
    private static bool IsBlank(ChatViewModel chat) => !chat.HasConversation && !chat.IsBusy && chat.Items.Count == 0;

    /// <summary>True while the session on the panel works in its own worktree.</summary>
    public bool IsIsolated => Panel.IsIsolated;

    /// <summary>
    /// Leaves isolation for the chat in front. Merging first brings the worktree's
    /// branch into the project; either way the worktree is removed and the chat
    /// starts a fresh session in the project directory.
    /// </summary>
    public async Task LeaveIsolationAsync(bool merge)
    {
        var chat = Chat;
        if (chat.WorktreePath is not { } path)
        {
            return;
        }

        IsStartingWorktree = true;

        var sessionStopped = false;

        try
        {
            var worktrees = new GitWorktrees(ProjectPath);

            // The agent runs with the worktree as its working directory; Windows will not
            // delete a folder a live process is sitting in ("Permission denied").
            if (!merge)
            {
                await chat.DisposeSessionAsync();
                sessionStopped = true;
            }

            var result = merge
                ? await worktrees.MergeBackAsync(path)
                : await worktrees.RemoveAsync(path, force: true);

            // A plain git merge gives up on a dirty project or a conflict; hand it to an agent.
            if (!result.Success && merge)
            {
                IsMergingWithAgent = true;
                WorktreeError = null;
                result = await MergeWithAgentAsync(worktrees, path, result.Error);
            }

            if (!result.Success)
            {
                WorktreeError = merge
                    ? $"Could not merge back: {result.Error}"
                    : $"Could not remove the worktree: {result.Error}";

                if (sessionStopped)
                {
                    // Still isolated: bring the agent back in the worktree.
                    await chat.ConnectAsync();
                }

                return;
            }

            WorktreeError = null;
            CrashLog.Trace($"Isolated session left: worktree={path} merged={merge}");

            await chat.DisposeSessionAsync();
            chat.ResetForNewSession();
            await chat.ConnectAsync();
            await Diff.RefreshAsync();
            await Git.RefreshAsync();
            await Sessions.RefreshAsync();
        }
        finally
        {
            IsStartingWorktree = false;
            IsMergingWithAgent = false;
        }
    }

    /// <summary>
    /// Merges an isolated branch with an agent working in the project directory, for the
    /// cases plain git refuses: uncommitted local changes the merge would overwrite, or
    /// conflicts. The result is checked with git rather than trusted from the agent's reply.
    /// </summary>
    private async Task<GitRepository.GitWriteResult> MergeWithAgentAsync(GitWorktrees worktrees, string worktreePath, string? gitError)
    {
        var branch = await worktrees.BranchOfAsync(worktreePath);
        if (branch is null)
        {
            return new GitRepository.GitWriteResult(false, "That worktree has no Codale branch to merge.");
        }

        // The agent merges the branch, so the session's pending edits must be on it.
        if (await worktrees.CommitPendingAsync(worktreePath) is { } commitError)
        {
            return new GitRepository.GitWriteResult(false, commitError);
        }

        var worker = new ChatViewModel(ProjectPath, new StatusViewModel(), _endpoints)
        {
            PermissionMode = "auto",
        };

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.TurnFinished += (_, _) => finished.TrySetResult();
        worker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.IsConnected) && !worker.IsConnected)
            {
                finished.TrySetResult();
            }
        };

        try
        {
            await worker.ConnectAsync();
            if (!worker.IsConnected)
            {
                return new GitRepository.GitWriteResult(false, $"Could not start a {worker.ProviderName} session to merge. {gitError}");
            }

            worker.Draft = MergePrompt(branch, gitError);
            await worker.SendCommand.ExecuteAsync(null);
            await finished.Task.WaitAsync(TimeSpan.FromMinutes(10));

            if (!await worktrees.IsMergedAsync(branch))
            {
                var reply = worker.Items.OfType<AssistantMessageItem>().LastOrDefault()?.Text.Trim();
                return new GitRepository.GitWriteResult(false, reply is { Length: > 0 }
                    ? $"The agent could not finish the merge: {reply}"
                    : "The agent could not finish the merge.");
            }

            return await worktrees.RemoveAsync(worktreePath, force: true);
        }
        catch (TimeoutException)
        {
            return new GitRepository.GitWriteResult(false, "The agent took too long to merge; the worktree was kept.");
        }
        finally
        {
            await worker.DisposeAsync();
        }
    }

    private static string MergePrompt(string branch, string? gitError) =>
        $"""
        Merge the git branch `{branch}` into the branch currently checked out in this directory. It holds the work of an isolated session and everything on it is committed.

        A plain `git merge --no-ff {branch}` already failed with: {gitError ?? "an unknown error"}

        Do this carefully so nothing is lost:
        1. Run `git status` first. If there are uncommitted local changes, keep them: stash them (`git stash push -u`), merge, then `git stash pop` and resolve anything that clashes. Never discard them.
        2. Merge with `git merge --no-ff {branch}`.
        3. Resolve every conflict by reading both sides and keeping the intent of both changes. Do not just take one side. Stage the files and complete the merge commit.
        4. If the project has a quick build or test command, run it to check the result.
        5. Finish with no merge in progress and no leftover stash.

        Do not use `git reset --hard`, `git checkout -- .`, `git clean`, or force anything. Do not delete the branch or the worktree; that is handled afterwards. Reply with one short line saying what you did.
        """;

    [ObservableProperty]
    public partial bool IsStartingWorktree { get; set; }

    /// <summary>True while an agent is merging an isolated branch back, for the card to say so.</summary>
    [ObservableProperty]
    public partial bool IsMergingWithAgent { get; set; }

    [ObservableProperty]
    public partial string? WorktreeError { get; set; }


    /// <summary>Where the agent on the panel is working: the project, or an isolated worktree.</summary>
    public string WorkingDirectory => Panel.WorktreePath ?? ProjectPath;

    /// <summary>Leaves history and returns to the live conversation.</summary>
    public void ReturnToLiveSession()
    {
        IsViewingHistory = false;
        HistoryTitle = null;
        Chat.Items.Clear();
    }

    /// <summary>
    /// Starts a brand-new session in the project directory: the running CLI is
    /// stopped and the transcript drops. The old conversation stays reachable
    /// through the session list, which resumes it.
    /// </summary>
    public async Task StartNewChatSessionAsync()
    {
        // The conversation in front keeps running in its own tab; the new one gets a
        // tab of its own - unless the one in front never started anything.
        var chat = FreshChat();
        await ApplyDefaultsToEmptyChatAsync(chat);

        ChatActivationRequested?.Invoke(this, chat);

        if (!chat.IsConnected)
        {
            await chat.ConnectAsync();
        }
    }

    /// <summary>
    /// A reused empty chat may still carry the last session's temporary picks; mode goes
    /// back to Settings' default. Model and effort keep the CLI default for new sessions.
    /// A chat with a conversation is left alone.
    /// </summary>
    public async Task ApplyDefaultsToEmptyChatAsync(ChatViewModel chat)
    {
        if (chat.HasConversation)
        {
            return;
        }

        if (chat.IsConnected)
        {
            // Settings' CLI provider may have changed since this blank chat connected.
            await chat.SetEndpointAsync(AppSettings.CliProviderFor(chat.CliId));

            if (AppSettings.DefaultModeFor(chat.CliId) is { Length: > 0 } mode)
            {
                await chat.SetPermissionModeAsync(mode);
            }
        }
        else
        {
            chat.ReloadCliDefaults();
            chat.PermissionMode = AppSettings.DefaultModeFor(chat.CliId);
        }
    }

    /// <summary>
    /// Automatic mode read a message as a new subject: it goes to a fresh session of
    /// its own, with the same model, effort and mode, and the conversation
    /// it came from stays as it was.
    /// </summary>
    private async void OnNewTopicRequested(object? sender, RoutedMessage message)
    {
        if (sender is not ChatViewModel source)
        {
            return;
        }

        try
        {
            var chat = FreshChat();
            if (ReferenceEquals(chat, source))
            {
                await source.SendRoutedAsync(message.Text, message.Attachments, message.Intent);
                return;
            }

            chat.RequestedModel = source.RequestedModel;
            chat.RequestedEffort = source.RequestedEffort;
            chat.PermissionMode = ChatViewModel.AutomaticMode;
            ChatActivationRequested?.Invoke(this, chat);

            if (!chat.IsConnected)
            {
                await chat.ConnectAsync();
            }

            CrashLog.Trace("Automatic mode: new topic, sent to a fresh session");
            await chat.SendRoutedAsync(message.Text, message.Attachments, message.Intent);
        }
        catch (Exception ex)
        {
            // The message must not vanish: it goes back where it was typed.
            CrashLog.Trace($"New-topic session failed: {ex.Message}");
            source.Draft = message.Text;
        }
    }

    private async void OnTurnFinished(object? sender, EventArgs e)
    {
        try
        {
            // A turn may have created or deleted files through the shell, which no
            // FileChange ever reported; the tree rescans before git re-colours it.
            Files.Reload();

            // Totals a write burst coalesced away are settled now that the last edit landed.
            Status.RefreshSessionChanges();

            // git is the authority on what actually changed, so both panels refresh from it
            // rather than trusting the event stream to have reported every edit.
            await Git.RefreshAsync();
            await Diff.RefreshAsync();

            if (sender is ChatViewModel chat)
            {
            }
        }
        catch (OperationCanceledException)
        {
            // A git read that outlived its 30 s timeout; the next poll catches up.
            CrashLog.Debug("workspace", "post-turn refresh timed out");
        }
        catch (Exception ex)
        {
            CrashLog.Error("workspace", "post-turn refresh failed", ex);
        }
    }

    /// <summary>
    /// A tool wrote a file mid-turn: the tree shows it right away, and git and the
    /// working-tree diff follow shortly after - debounced, because an agent writing
    /// ten files in a burst needs one refresh, not ten. The tree rescan is debounced
    /// on the same stamp: it walks the expanded tree synchronously, so a burst pays
    /// for one walk, not one per file.
    /// </summary>
    private void OnChatFilesChanged(object? sender, EventArgs e)
    {
        var stamp = ++_liveRefreshStamp;
        _ = RefreshLiveAsync(stamp);
    }

    private int _liveRefreshStamp;

    private async Task RefreshLiveAsync(int stamp)
    {
        // The first write shows up immediately; only a rapid burst collapses.
        var leading = stamp == 1 || Environment.TickCount64 - _lastLiveRefresh > 400;
        if (!leading)
        {
            await Task.Delay(400);
            if (stamp != _liveRefreshStamp)
            {
                return;
            }
        }

        _lastLiveRefresh = Environment.TickCount64;

        try
        {
            if (leading || stamp == _liveRefreshStamp)
            {
                Files.Reload();
            }

            await Git.RefreshAsync();
            await Diff.RefreshAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Live refresh failed: {ex.Message}");
        }
    }

    private long _lastLiveRefresh = -10_000;

    /// <summary>The session's opening message is named, never answered: it often is a task the model would happily start on.</summary>
    private const string SessionNamePrompt =
        "You name a coding session after the developer's opening message in <message>.\n" +
        "Output: the name only - 2 to 5 words, like a short headline, no quotes, no punctuation at the end.\n" +
        "Examples: Fix login redirect | Git panel refactor | Explain retry policy\n" +
        "\n" +
        "Rules:\n" +
        "- Name the message; never answer it, carry it out or reply to it.\n" +
        PromptRules.DataOnly + "\n" +
        PromptRules.ResultOnly;

    /// <summary>The helper model's short headline for an opening prompt, cleaned and capped for the tab strip and history list.</summary>
    private async Task<string> NameFromPromptAsync(string prompt, CancellationToken ct)
    {
        var title = await _helper.CompleteAsync(SessionNamePrompt, PromptRules.Tag("message", prompt), ct);

        // A CLI model sometimes adds a follow-up paragraph; the name is the first line.
        title = SessionTitles.CleanModelTitle(
            Codale.Core.Text.ModelOutput.CleanText(title).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "");
        return title.Length > 40 ? title[..40].TrimEnd() + "…" : title;
    }

    /// <summary>
    /// A suggested name for the Retitle dialog's auto-generate button: the helper model
    /// when an agent CLI is installed, else the same short prompt-derived name a new session gets.
    /// Null when the session has no prompt to name.
    /// </summary>
    public async Task<string?> SuggestTitleAsync(SessionListItem item, CancellationToken ct = default)
    {
        var prompt = item.Summary.FirstPrompt ?? item.Summary.LastPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        if (_helper.IsAvailable)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var title = await NameFromPromptAsync(prompt, timeout.Token);
                if (title.Length > 0)
                {
                    return title;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own 60 s budget ran out, not the caller's cancel: use the prompt-derived name.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through to the prompt-derived name.
            }
        }

        var fallback = SessionTitles.FromPrompt(prompt);
        return fallback.Length > 0 ? fallback : null;
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        // A conversation changed: the history list re-badges what is open and working.
        PublishOpenSessions();

        ScheduleSessionRefresh();
    }

    private bool _sessionRefreshPending;

    /// <summary>
    /// A connect, a turn start and a turn end land within moments of each other; one
    /// scan after they settle is enough, and none of them should compete with the
    /// click that caused them.
    /// </summary>
    private async void ScheduleSessionRefresh()
    {
        if (_sessionRefreshPending)
        {
            return;
        }

        _sessionRefreshPending = true;
        try
        {
            await Task.Delay(1500);
        }
        finally
        {
            _sessionRefreshPending = false;
        }

        try
        {
            await Sessions.RefreshAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Error("workspace", "session list refresh failed", ex);
        }
    }

    /// <summary>Every git poll re-colours the file tree from the same snapshot.</summary>
    private void OnGitSnapshotRefreshed(object? sender, GitSnapshot snapshot) => Files.ApplyGitStatus(snapshot);

    private void OnNestedSnapshotsRefreshed(object? sender, IReadOnlyList<GitSnapshot> snapshots) => Files.ApplyNestedGitStatus(snapshots);

    /// <summary>
    /// Working-tree diffs (by path) as they stood when each agent CLI was launched in a terminal. Codale
    /// cannot see a CLI's edits, so what differs from its baseline is its work.
    /// </summary>
    private readonly Dictionary<ChatViewModel, Dictionary<string, (int Added, int Removed)>> _terminalBaselines = [];

    /// <summary>When each terminal session's CLI last wrote to its terminal: the one most recently active gets the next changes.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChatViewModel, DateTime> _terminalLastOutput = [];

    /// <summary>The session a changed file was first listed under, so a second CLI is not credited with the first one's edits.</summary>
    private readonly Dictionary<string, ChatViewModel> _changeOwners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An agent CLI was launched in a terminal: from now on, working-tree changes are listed under its session.</summary>
    public async Task BeginTerminalCliSessionAsync(ChatViewModel session, TerminalViewModel terminal)
    {
        _terminalLastOutput[session] = DateTime.UtcNow;
        terminal.OutputReceived += (_, _) => _terminalLastOutput[session] = DateTime.UtcNow;

        await Diff.RefreshAsync();
        if (TerminalSessions.Contains(session))
        {
            _terminalBaselines[session] = Diff.Files.ToDictionary(f => f.Path, f => (f.Added, f.Removed), StringComparer.OrdinalIgnoreCase);
        }
    }

    private void ListTerminalCliChanges()
    {
        var session = _terminalBaselines.Keys
            .OrderByDescending(s => _terminalLastOutput.GetValueOrDefault(s))
            .FirstOrDefault();
        if (session is null)
        {
            return;
        }

        var baseline = _terminalBaselines[session];
        foreach (var file in Diff.Files)
        {
            if (baseline.TryGetValue(file.Path, out var before) && before == (file.Added, file.Removed))
            {
                continue;
            }

            if (_changeOwners.TryGetValue(file.Path, out var owner) && !ReferenceEquals(owner, session))
            {
                continue;
            }

            _changeOwners[file.Path] = session;
            var kind = file.IsNew ? FileChangeKind.Create : file.IsDeleted ? FileChangeKind.Delete : FileChangeKind.Update;
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectPath, file.Path));
            session.Status.RecordFileChange(new FileChange { Kind = kind, FilePath = path }, file);
        }
    }

    private async void OnGitWorkingTreeChanged(object? sender, EventArgs e)
    {
        try
        {
            await Diff.RefreshAsync();
            ListTerminalCliChanges();
        }
        catch (Exception ex)
        {
            CrashLog.Error("workspace", "diff refresh after a git change failed", ex);
        }
    }

    private void OnTodosChanged(object? sender, IReadOnlyList<TodoItem> todos)
    {
        if (sender is ChatViewModel { SessionId: { Length: > 0 } id })
        {
            _store.SaveTodos(id, todos.Select(t => (t.Content, t.Status.ToString())).ToList());
        }
    }

    private async void OnSessionOpened(object? sender, TranscriptSummary summary)
    {
        try
        {
            await OpenSessionAsync(summary);
        }
        catch (Exception ex)
        {
            CrashLog.Error("workspace", $"opening {summary.SessionId} failed", ex);
        }
    }

    /// <summary>
    /// Opening a feature from the history list puts a chat on it - and never at another's
    /// expense: a feature already open just comes to the front; otherwise the chat in front
    /// takes it when it holds nothing yet, or a new chat tab does. Nothing of the feature's
    /// past is loaded: its first message carries the summary.
    /// </summary>
    private async Task<ChatViewModel?> OpenSessionAsync(TranscriptSummary summary)
    {
        CrashLog.Trace($"Open: {summary.SessionId}");

        if (FindOpenChat(summary.SessionId) is { } open)
        {
            ChatActivationRequested?.Invoke(this, open);
            return open;
        }

        if (Features.Get(summary.SessionId) is not { } feature)
        {
            return null;
        }

        var chat = FreshChat();
        IsViewingHistory = false;
        HistoryTitle = null;
        ChatActivationRequested?.Invoke(this, chat);
        await chat.AttachFeatureAsync(feature);
        await Diff.RefreshAsync();
        return chat;
    }

    private ChatViewModel? FindOpenChat(string featureId) =>
        Chats.FirstOrDefault(c => c.FeatureId == featureId);

    private static readonly DateTime WindowOpenedAt = DateTime.Now;

    private Timer? _elapsedTimer;

    /// <summary>When this run started, as a clock time: "9:41 AM".</summary>
    public string StartedAtText => WindowOpenedAt.ToString("t");

    /// <summary>How long the window has been open: "42s", "12m 05s", "1h 03m".</summary>
    public string ElapsedText
    {
        get
        {
            var elapsed = DateTime.Now - WindowOpenedAt;
            return elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m"
                : elapsed.TotalMinutes >= 1
                    ? $"{elapsed.Minutes}m {elapsed.Seconds:00}s"
                    : $"{elapsed.Seconds}s";
        }
    }

    private void StartElapsedTimer()
    {
        _elapsedTimer = new Timer(
            _ =>
            {
                if (_uiContext is { } ui)
                {
                    ui.Post(_ => OnPropertyChanged(nameof(ElapsedText)), null);
                }
            },
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    public async ValueTask DisposeAsync()
    {
        _elapsedTimer?.Dispose();
        AppSettings.Changed -= OnAppSettingsChanged;
        CliEndpoint.Dispose();
        foreach (var chat in Chats)
        {
            chat.TurnFinished -= OnTurnFinished;
            chat.FilesChanged -= OnChatFilesChanged;
            chat.SessionStateChanged -= OnSessionStateChanged;
            chat.TodosChanged -= OnTodosChanged;
            chat.NewTopicRequested -= OnNewTopicRequested;
                chat.PropertyChanged -= OnChatPropertyChanged;
        }

        // Every session is told to go at once:
        // awaiting them one by one means the process can exit after the first, leaving the
        // other CLIs running.
        var shutdown = Task.WhenAll(Chats.Concat(TerminalSessions).ToList().Select(chat => chat.DisposeAsync().AsTask()));

        Sessions.SessionOpened -= OnSessionOpened;
        Git.SnapshotRefreshed -= OnGitSnapshotRefreshed;
        Git.NestedSnapshotsRefreshed -= OnNestedSnapshotsRefreshed;
        Git.WorkingTreeChanged -= OnGitWorkingTreeChanged;

        // Runs do not outlive the project window, dev servers included; every terminal
        // tab is disposed here too, command ones and plain ones alike.
        foreach (var terminal in TerminalsSnapshot())
        {
            terminal.PropertyChanged -= OnCommandTerminalPropertyChanged;
            terminal.Dispose();
        }

        lock (_terminals)
        {
            _terminals.Clear();
        }

        UpdateRunningCommandCount();
        Git.Dispose();

        // A warm helper CLI waiting for Automatic mode's next message goes with the window.
        _helper.Dispose();

        try
        {
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            // A session that will not stop in time is abandoned: its process dies with ours.
            CrashLog.Warn("workspace", $"closing sessions: {ex.GetType().Name}: {ex.Message}");
        }

        Features.Dispose();
        _store.Dispose();

        if (StorageWarning is not null)
        {
            // The throwaway database (see OpenStore) has no use after the window.
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(_databasePath + suffix);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A leftover temp file is harmless.
                }
            }
        }
    }
}
