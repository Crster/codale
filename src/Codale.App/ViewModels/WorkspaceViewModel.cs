using System.Collections.ObjectModel;
using System.ComponentModel;

using Codale.Agents;
using Codale.Agents.History;
using Codale.App.Services;
using Codale.Commands;
using Codale.Core.Agents;
using Codale.Core.Projects;
using Codale.Core.Serving;
using Codale.Core.Tasks;
using Codale.Git;
using Codale.Workspace;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>One project, one window. Everything the three panels bind to hangs off this.</summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CliEndpointSettings _endpoints;
    private readonly McpServerSettings _mcp;
    private readonly IAgentSessionFactory _agentSessions;
    private readonly IGitRepositoryFactory _gitRepositories;
    private readonly ISmartDecider _decider;
    private readonly IProjectCommandService _commands;
    private readonly CommandHistory _commandHistory;


    /// <summary>The project's features: the history every chat in the window shares.</summary>
    public FeatureService Features { get; }
    public IWorkspaceData Data { get; }
    public KeyboardShortcuts Shortcuts { get; }
    public SyntaxSelection Syntax { get; }
    public SmartExperienceHost Smart { get; }
    public ICliHistoryReaderFactory CliHistory { get; }

    public WorkspaceViewModel(string projectPath, IAgentSessionFactory agentSessions, IGitRepositoryFactory gitRepositories, SmartExperienceHost smart, ISmartDecider decider, IProjectCommandService commands, IWorkspaceDataFactory workspaceData, ICliHistoryReaderFactory cliHistory, ApplicationDatabase database, CommandHistory commandHistory, KeyboardShortcuts shortcuts, SyntaxSelection syntax)
    {
        _agentSessions = agentSessions;
        CliHistory = cliHistory;
        Shortcuts = shortcuts;
        Syntax = syntax;
        _commandHistory = commandHistory;
        _gitRepositories = gitRepositories;
        Smart = smart;
        _decider = decider;
        _commands = commands;
        ProjectPath = ProjectPaths.Normalize(projectPath);
        DisplayName = ProjectPaths.DisplayName(ProjectPath);
        Data = workspaceData.Open(ProjectPath);

        ExtraFolders = new WorkspaceFolders(ProjectPath, Data.Config);
        Files = new FileTreeViewModel(ProjectPath, ExtraFolders);
        Git = new GitViewModel(ProjectPath, gitRepositories, smart);
        Features = new FeatureService(ProjectPath, smart, Data);
        Search = new SearchViewModel(ProjectPath, smart, syntax, Features.Knowledge);
        References = new SearchViewModel(ProjectPath, smart, syntax);
        Diff = new DiffViewModel(gitRepositories.Create(ProjectPath));
        DiffViewer = CreateDiffViewer();
        Sessions = new SessionsViewModel(Features);
        _endpoints = new CliEndpointSettings(smart.Settings);
        _mcp = new McpServerSettings(ProjectPath, database, Data);
        Health = new WorkspaceHealthViewModel(ProjectPath, Features.Knowledge);
        SmartActivity = new SmartActivityViewModel(ProjectPath, smart.Instance);
        SmartActivity.ObserveIndex(Features.Knowledge);
        McpTools = new McpToolsViewModel(_mcp, FolderServers, ProjectPath);
        CliEndpoint = new CliEndpointViewModel(smart.Settings);
        CliEndpoint.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CliEndpointViewModel.ActiveName))
            {
                OnPropertyChanged(nameof(EndpointLabel));
            }
        };


        Sessions.SessionOpened += OnSessionOpened;
        Git.SnapshotRefreshed += OnGitSnapshotRefreshed;
        Git.NestedSnapshotsRefreshed += OnNestedSnapshotsRefreshed;
        Git.WorkingTreeChanged += OnGitWorkingTreeChanged;

        Chat = CreateChat();
        Panel = Chat;
    }

    public string ProjectPath { get; }

    public DiffViewerViewModel CreateDiffViewer() => new(_gitRepositories.Create(ProjectPath));

    public string DisplayName { get; }

    /// <summary>The status of the chat in front: each conversation keeps its own.</summary>
    public StatusViewModel Status => Chat.Status;

    /// <summary>The status bar's indexer for this workspace.</summary>
    public WorkspaceHealthViewModel Health { get; }
    public SmartActivityViewModel SmartActivity { get; }

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
    [NotifyPropertyChangedFor(nameof(EndpointLabel))]
    public partial ChatViewModel Chat { get; set; }

    /// <summary>
    /// The session the panel shows: the chat in front for a Claude chat tab (the full panel), or the
    /// headless session of the terminal CLI in front (the simple panel). Each agent session has its
    /// own; the panel is not shared. It keeps the last session while a non-agent tab is in front.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkingDirectory))]
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
            : CliEndpoint.ActiveName is { Length: > 0 } name ? name : "Default";

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
        var session = new ChatViewModel(ProjectPath, Data.Directory, new StatusViewModel(), _endpoints, _mcp, _agentSessions, Smart, _decider) { ExtraFolders = ExtraFolders, FolderServers = FolderServers, TerminalCli = cli };
        session.Terminals = new TerminalSource(this, session);
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
        _mcp.ClearStaged(session.TerminalLaunchKey);
        await session.DisposeAsync();
    }

    /// <summary>Every open conversation, one per chat tab.</summary>
    public ObservableCollection<ChatViewModel> Chats { get; } = [];

    /// <summary>The Claude CLI's endpoint override, editable from the status bar flyout.</summary>
    public CliEndpointViewModel CliEndpoint { get; }

    /// <summary>The built-in browser and desktop-control servers, editable from the status bar flyout.</summary>
    public McpToolsViewModel McpTools { get; }

    /// <summary>Folders served on localhost from the Agent tools flyout; they live as long as this window.</summary>
    public FolderServerHost FolderServers { get; } = new();

    /// <summary>
    /// Adds this session's Codale MCP servers to an agent CLI launch typed at a terminal prompt (its own pipe,
    /// so what the CLI reports lands in its own panel); null when the line is not one.
    /// </summary>
    public string? RewriteCliLaunch(string promptLine, ChatViewModel session)
    {
        var shell = Codale.Terminal.TerminalSession.ResolveShell(Smart.Settings.TerminalShell);
        var powershell = Path.GetFileName(shell).ToLowerInvariant() is "pwsh.exe" or "powershell.exe";
        return _mcp.RewriteCliLaunch(promptLine, session.ServersForTerminalCli(), session.TerminalLaunchKey, powershell);
    }

    /// <summary>
    /// Opens a dedicated terminal for an agent CLI session: the shell runs only the CLI and exits
    /// with it, so the tab knows when the session ends. The terminal carries the session its panel shows.
    /// </summary>
    public TerminalViewModel RunCliTerminal(string command, Codale.Agents.CliDescriptor cli, string directory, ChatViewModel session)
    {
        var terminal = new TerminalViewModel(Smart.Settings, _commandHistory, directory, command, cli.DisplayName, cli.Id)
        {
            AgentSession = session,
            ClaudeContextPath = cli.Id == "claude" ? _mcp.ClaudeTerminalContextPath(session.TerminalLaunchKey) : null,
        };
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
        var chat = new ChatViewModel(ProjectPath, Data.Directory, new StatusViewModel(), _endpoints, _mcp, _agentSessions, Smart, _decider)
        {
            Features = Features,
            ExtraFolders = ExtraFolders,
            FolderServers = FolderServers,
            CliId = cliId is { Length: > 0 } ? cliId : Smart.Settings.DefaultChatCli,
        };

        chat.Terminals = new TerminalSource(this, chat);
        // Model and effort come from the CLI's saved default pick (or its own default);
        // the mode is the last one the user picked for this CLI.
        chat.PermissionMode = Smart.Settings.DefaultModeFor(chat.CliId);

        chat.TurnFinished += OnTurnFinished;
        chat.PromptStarted += OnChatPromptStarted;
        chat.FilesChanged += OnChatFilesChanged;
        chat.SessionStateChanged += OnSessionStateChanged;
        chat.TodosChanged += OnTodosChanged;
        chat.PropertyChanged += OnChatPropertyChanged;
        chat.TopicRedirectRequested += OnTopicRedirectRequested;
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

        return !Chat.HasConversation && Wants(Chat)
            ? Chat
            : Chats.FirstOrDefault(c => !c.HasConversation && !c.IsConnected && Wants(c))
            ?? CreateChat(cliId);
    }

    /// <summary>The chat's tab closed: its session ends, and the front moves to another chat.</summary>
    public async Task CloseChatAsync(ChatViewModel chat)
    {

        chat.TurnFinished -= OnTurnFinished;
        chat.PromptStarted -= OnChatPromptStarted;
        chat.FilesChanged -= OnChatFilesChanged;
        chat.SessionStateChanged -= OnSessionStateChanged;
        chat.TodosChanged -= OnTodosChanged;
        chat.PropertyChanged -= OnChatPropertyChanged;
        chat.TopicRedirectRequested -= OnTopicRedirectRequested;
        _promptBaselines.Remove(chat);
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
    public TerminalViewModel CreateTerminal(string? command = null, string? commandName = null, string? cliId = null)
    {
        var terminal = new TerminalViewModel(Smart.Settings, _commandHistory, ProjectPath, command, commandName, cliId);
        terminal.PropertyChanged += OnCommandTerminalPropertyChanged;
        lock (_terminals)
        {
            _terminals.Add(terminal);
        }

        UpdateRunningCommandCount();
        return terminal;
    }

    /// <summary>
    /// A terminal task the agent opened (run_in_terminal, run_command). Raised off the UI thread;
    /// the page starts it hidden in its owner's background tasks. A subscriber provides the UI dispatcher.
    /// </summary>
    public event EventHandler<TerminalViewModel>? AgentTerminalOpened;

    /// <summary>Command tabs the agent started, so stop_terminal can tell them from the user's.</summary>
    private readonly HashSet<string> _agentTerminalIds = [];

    public bool IsAgentTerminal(TerminalViewModel terminal)
    {
        lock (_terminals)
        {
            return terminal.IsAgentTerminal || _agentTerminalIds.Contains(terminal.Id);
        }
    }

    /// <summary>The open terminals as the agent's terminal tools read them; safe to call off the UI thread.</summary>
    private sealed class TerminalSource(WorkspaceViewModel workspace, ChatViewModel owner) : ITerminalSource
    {
        public async Task<TaskSnapshot?> StartAsync(string command, string? name, int waitSeconds, CancellationToken ct)
        {
            if (workspace.AgentTerminalOpened is not { } opened)
            {
                return null;
            }

            var terminal = workspace.CreateTerminal(command, string.IsNullOrWhiteSpace(name) ? command : name);
            terminal.IsAgentTerminal = true;
            terminal.BackgroundTaskOwner = owner;
            lock (workspace._terminals)
            {
                workspace._agentTerminalIds.Add(terminal.Id);
            }

            opened(workspace, terminal);

            // Early output (a port number, a compile error) is what the agent needs next.
            // The UI dispatcher starts the hidden terminal; it reads busy until the command finishes.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(waitSeconds);
            while (DateTime.UtcNow < deadline && (!terminal.IsOpen || terminal.Activity == TerminalActivity.Busy))
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

        /// <summary>A tab's shell outlives its command: for a command the agent started, idle means it finished.</summary>
        private static TaskSnapshot Started(TerminalViewModel t, long? since, int? tailChars)
        {
            var snapshot = Snapshot(t, since, tailChars);
            return snapshot.State == "idle" ? snapshot with { State = "exited" } : snapshot;
        }

        // Interactive authentication can print a one-time code. Keep its terminal out of agent tools.
        private IEnumerable<TerminalViewModel> VisibleTerminals() =>
            workspace.TerminalsSnapshot().Where(t => !t.IsCliTerminal || t.AgentSession is not null);

        public IReadOnlyList<TaskSnapshot> List() => [.. VisibleTerminals().Select(t => Snapshot(t, null, 0))];

        public TaskSnapshot? Read(string id, long? since, int? tailChars) =>
            VisibleTerminals().FirstOrDefault(t => t.Id == id) is { } terminal ? Snapshot(terminal, since, tailChars) : null;

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

    /// <summary>Command terminals whose command is still executing; the badge and the menu read this.</summary>
    public IReadOnlyList<TerminalViewModel> RunningCommands =>
        [.. _terminals.Where(t => t.IsCommandTerminal && t.Activity == TerminalActivity.Busy)];

    /// <summary>What is still running behind the window (working chats, commands), for the close prompt.</summary>
    public IReadOnlyList<string> RunningActivities
    {
        get
        {
            var list = new List<string>();
            foreach (var chat in Chats)
            {
                if (chat.IsBusy)
                {
                    list.Add($"Chat: {chat.TabTitle}");
                }

            }

            list.AddRange(RunningCommands.Select(t => $"Command: {t.CommandName ?? "terminal"}"));
            return list;
        }
    }

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
        lock (_terminals)
        {
            if (_terminals.FirstOrDefault(t =>
                    t.IsCommandTerminal &&
                    t.IsRunning &&
                    t.Activity == TerminalActivity.Busy &&
                    ProjectCommandRun.IsCoveredBy(command, t.CommandName, t.CommandText)) is { } live)
            {
                return live;
            }
        }

        var terminal = new TerminalViewModel(Smart.Settings, _commandHistory, ProjectCommandRun.WorkingDirectoryFor(command, ProjectPath), command.Command, command.Name);
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

    /// <summary>The window's UI thread; the workspace is created on it.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _ui =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    /// <summary>
    /// The badge count is bound in XAML, so it is set on the UI thread: the agent's terminal tools
    /// and a terminal's activity changes call this from background threads.
    /// </summary>
    private void UpdateRunningCommandCount()
    {
        if (_ui is not null && !_ui.HasThreadAccess)
        {
            _ui.TryEnqueue(UpdateRunningCommandCount);
            return;
        }

        int count;
        lock (_terminals)
        {
            count = _terminals.Count(t => t.IsCommandTerminal && t.Activity == TerminalActivity.Busy);
        }

        RunningCommandCount = count;
    }

    /// <summary>The project's saved commands (not the ones discovered in project files), for the commands editor.</summary>
    public IReadOnlyList<ProjectCommand> SavedCommands() => _commands.Saved(ProjectPath);

    /// <summary>Saves a command from the editor; <paramref name="originalName"/> lets a rename replace the old entry.</summary>
    public ProjectCommand SaveCommand(string? originalName, ProjectCommandEdit edit) =>
        _commands.Save(ProjectPath, originalName, edit);

    /// <summary>Removes a saved command; false when it was already gone.</summary>
    public bool RemoveCommand(string name) => _commands.Remove(ProjectPath, name);

    public GitViewModel Git { get; }

    public SearchViewModel Search { get; }

    /// <summary>Find References results, kept apart from the Search tab so neither overwrites the other.</summary>
    public SearchViewModel References { get; }

    public DiffViewModel Diff { get; }

    /// <summary>The centre-panel diff tab's view model: one file, or a whole commit.</summary>
    public DiffViewerViewModel DiffViewer { get; }

    public SessionsViewModel Sessions { get; }

    /// <summary>Every open editor tab's view model, so closing the window can ask about unsaved edits. The page registers and unregisters them.</summary>
    private readonly List<EditorViewModel> _editors = [];

    /// <summary>True when any open editor holds edits that are not on disk.</summary>
    public bool HasDirtyEditors => _editors.Any(e => e.IsDirty && e.CanEdit);

    /// <summary>The names of the tabs with unsaved edits, for the close prompt.</summary>
    public IReadOnlyList<string> DirtyEditorNames =>
        [.. _editors.Where(e => e.IsDirty && e.CanEdit).Select(e => e.FileName ?? "Untitled")];

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

    /// <summary>Where the agent on the panel is working: the project.</summary>
    public string WorkingDirectory => ProjectPath;

    /// <summary>
    /// Applies the latest defaults to a blank chat reused for a new conversation.
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
            await chat.SetEndpointAsync(Smart.Settings.CliProviderFor(chat.CliId));
            await chat.ApplyDefaultModelSelectionAsync();

            if (Smart.Settings.DefaultModeFor(chat.CliId) is { Length: > 0 } mode)
            {
                await chat.SetPermissionModeAsync(mode);
            }
        }
        else
        {
            chat.ReloadCliDefaults();
            chat.PermissionMode = Smart.Settings.DefaultModeFor(chat.CliId);
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
            (sender is ChatViewModel finishedChat ? finishedChat.Status : Status).RefreshSessionChanges();

            // git is the authority on what actually changed, so both panels refresh from it
            // rather than trusting the event stream to have reported every edit.
            await Git.RefreshAsync();
            await Diff.RefreshAsync();

            // Edits made through the shell report no FileChange; git shows them, so they are
            // listed under the prompt now, against the working tree as it stood when it was sent.
            if (sender is ChatViewModel chat && await ReconcilePromptChangesAsync(chat))
            {
                chat.Status.RefreshSessionChanges();
            }
        }
        catch (OperationCanceledException)
        {
            // A git read that outlived its 30 s timeout; the next poll catches up.
        }
        catch (Exception)
        {
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
        catch (Exception)
        {
        }
    }

    private long _lastLiveRefresh = -10_000;

    /// <summary>The working tree as it stood when a chat's latest prompt was sent: full path → git totals and content.</summary>
    private sealed record PromptBaseline(int PromptIndex, Dictionary<string, (int Added, int Removed, bool Deleted, string? Content)> Files);

    private readonly Dictionary<ChatViewModel, Task<PromptBaseline?>> _promptBaselines = [];

    /// <summary>Files read per baseline, and the largest one read: a huge dirty tree falls back to git totals.</summary>
    private const int MaxBaselineFiles = 300;
    private const long MaxBaselineBytes = 2 * 1024 * 1024;

    private void OnChatPromptStarted(object? sender, EventArgs e)
    {
        if (sender is ChatViewModel chat && !TerminalSessions.Contains(chat))
        {
            _promptBaselines[chat] = CapturePromptBaselineAsync(chat.Status.PromptIndex);
        }
    }

    private async Task<PromptBaseline?> CapturePromptBaselineAsync(int promptIndex)
    {
        try
        {
            await Diff.RefreshAsync();
            var files = new Dictionary<string, (int, int, bool, string?)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Diff.Files.ToList())
            {
                var path = WorkspaceFullPath(file.Path);
                files[path] = (file.Added, file.Removed, file.IsDeleted,
                    file.IsDeleted || files.Count >= MaxBaselineFiles ? null : ReadSmallFile(path));
            }

            return new PromptBaseline(promptIndex, files);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists, under the chat's latest prompt, every file git shows changed since that prompt
    /// was sent that the agent's own events did not already report - Codex and Claude both
    /// edit through the shell at times, which no FileChange describes. True when any was added.
    /// </summary>
    private async Task<bool> ReconcilePromptChangesAsync(ChatViewModel chat)
    {
        if (!_promptBaselines.TryGetValue(chat, out var pending) || await pending is not { } baseline ||
            baseline.PromptIndex != chat.Status.PromptIndex)
        {
            return false;
        }

        // Another chat still working may be the one that wrote a file; it lists its own.
        bool OwnedElsewhere(string path) => Chats.Any(other => !ReferenceEquals(other, chat) && other.IsBusy && other.Status.CurrentPromptTouched(path));

        var recorded = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Diff.Files.ToList())
        {
            var path = WorkspaceFullPath(file.Path);
            seen.Add(path);
            if (chat.Status.CurrentPromptTouched(path) || OwnedElsewhere(path))
            {
                continue;
            }

            var now = file.IsDeleted ? null : ReadSmallFile(path);
            if (baseline.Files.TryGetValue(path, out var before))
            {
                var unchanged = before.Deleted == file.IsDeleted && before.Added == file.Added && before.Removed == file.Removed &&
                    (before.Content is null || string.Equals(before.Content, now, StringComparison.Ordinal));
                if (unchanged)
                {
                    continue;
                }

                if (before.Content is not null)
                {
                    // Already dirty when the prompt was sent: this prompt's change is measured from that content.
                    var kind = file.IsDeleted ? FileChangeKind.Delete : FileChangeKind.Update;
                    chat.Status.RecordFileChange(
                        new FileChange { Kind = kind, FilePath = path, OriginalContent = before.Content, NewContent = now },
                        TextDiff.Compute(path, before.Content, now));
                    recorded = true;
                    continue;
                }
            }

            var newKind = file.IsNew ? FileChangeKind.Create : file.IsDeleted ? FileChangeKind.Delete : FileChangeKind.Update;
            chat.Status.RecordFileChange(new FileChange { Kind = newKind, FilePath = path, NewContent = now }, file);
            recorded = true;
        }

        // Dirty when the prompt was sent, clean now: the prompt put it back to what git has.
        foreach (var (path, before) in baseline.Files)
        {
            if (seen.Contains(path) || before.Content is null || chat.Status.CurrentPromptTouched(path) || OwnedElsewhere(path) ||
                ReadSmallFile(path) is not { } restored || restored == before.Content)
            {
                continue;
            }

            chat.Status.RecordFileChange(
                new FileChange { Kind = FileChangeKind.Update, FilePath = path, OriginalContent = before.Content, NewContent = restored },
                TextDiff.Compute(path, before.Content, restored));
            recorded = true;
        }

        return recorded;
    }

    private string WorkspaceFullPath(string relative)
    {
        try
        {
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectPath, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return relative;
        }
    }

    private static string? ReadSmallFile(string path)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            return info.Exists && info.Length <= MaxBaselineBytes ? System.IO.File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Suggest a local title based on the session's latest prompt.</summary>
    public Task<string?> SuggestTitleAsync(SessionListItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var prompt = FindOpenChat(item.SessionId)?.LatestUserPrompt ?? item.Summary.LastPrompt ?? item.Summary.FirstPrompt;
        if (string.IsNullOrWhiteSpace(prompt)) return Task.FromResult<string?>(null);
        var title = SessionTitles.FromPrompt(prompt);
        return Task.FromResult<string?>(title.Length > 0 ? title : null);
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
        catch (Exception)
        {
        }
    }

    /// <summary>Every git poll re-colours the file tree from the same snapshot.</summary>
    private void OnGitSnapshotRefreshed(object? sender, GitSnapshot snapshot)
    {
        Files.ApplyGitStatus(snapshot);

        // Open tabs carry the same colour and letter as the tree row.
        foreach (var editor in _editors)
        {
            editor.GitStatus = editor.FilePath is { } path ? Files.GitStatusOf(path) : null;
        }
    }

    private void OnNestedSnapshotsRefreshed(object? sender, IReadOnlyList<GitSnapshot> snapshots) => Files.ApplyNestedGitStatus(snapshots);

    /// <summary>
    /// Working-tree diffs (by path) as they stood when each agent CLI was launched in a terminal. Codale
    /// cannot see a CLI's edits, so what differs from its baseline is its work.
    /// </summary>
    private readonly Dictionary<ChatViewModel, Dictionary<string, (int Added, int Removed)>> _terminalBaselines = [];

    /// <summary>When each terminal session's CLI last wrote to its terminal: the one most recently active gets the next changes.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChatViewModel, DateTime> _terminalLastOutput = [];

    /// <summary>The session a changed file was first listed under, so a second CLI is not credited with the first one's edits.</summary>
    private readonly Dictionary<string, ChatViewModel> _changeOwners = [with(StringComparer.OrdinalIgnoreCase)];

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
        catch (Exception)
        {
        }
    }

    private sealed record SessionTodo(string Content, string Status);
    private sealed record SessionTodoList(SessionTodo[] Items);

    private void OnTodosChanged(object? sender, IReadOnlyList<TodoItem> todos)
    {
        if (sender is ChatViewModel { SessionId: { Length: > 0 } id })
        {
            var snapshot = new SessionTodoList([.. todos.Select(todo => new SessionTodo(todo.Content, todo.Status.ToString()))]);
            Data.UseRecords(db => db.Record<SessionTodoList>().Key(id).Value(snapshot));
        }
    }

    private async void OnSessionOpened(object? sender, TranscriptSummary summary)
    {
        if (!Sessions.TryBeginOpening(summary.SessionId)) return;
        try
        {
            await Task.Yield();
            if (await OpenSessionAsync(summary) is null)
                Sessions.OpenError = "This conversation is no longer available. Refresh History and try again.";
        }
        catch (Exception)
        {
            Sessions.OpenError = "Could not open this conversation. Please try again.";
        }
        finally
        {
            Sessions.EndOpening();
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

        if (FindOpenChat(summary.SessionId) is { } open)
        {
            if (!ReferenceEquals(open, Chat) && !open.IsBusy && !open.IsConnecting &&
                Features.Get(summary.SessionId) is { } existing)
            {
                await open.AttachFeatureAsync(existing, selection: Chat.ContinuationSelection);
            }
            ChatActivationRequested?.Invoke(this, open);
            return open;
        }

        if (Features.Get(summary.SessionId) is not { } feature)
        {
            return null;
        }

        // Histories share the active chat's CLI, account, model and mode. Attaching
        // uses a transcript handoff when that account cannot resume the native session.
        var selection = Chat.ContinuationSelection;
        // The empty chat in front takes it whether or not it already connected (a blank chat
        // connects on open); AttachFeatureAsync tears that session down and starts the right one.
        var reuseFront = !Chat.HasConversation && !Chat.IsTerminalSession;
        var chat = reuseFront ? Chat : FreshChat(selection.CliId);
        IsViewingHistory = false;
        HistoryTitle = null;
        await chat.AttachFeatureAsync(feature, selection: selection);
        ChatActivationRequested?.Invoke(this, chat);
        await Diff.RefreshAsync();
        return chat;
    }

    /// <summary>
    /// Auto mode found a prompt belongs to another session and the user agreed: that session comes to the front (opened
    /// in a chat of its own when it is not open yet), or a new one starts, and the prompt is sent there.
    /// </summary>
    private async void OnTopicRedirectRequested(object? sender, TopicRedirect redirect)
    {
        if (sender is not ChatViewModel from) return;
        try
        {
            var selection = from.ContinuationSelection;
            ChatViewModel target;
            if (redirect.FeatureId is { } id)
            {
                if (Features.Get(id) is not { } feature) { from.Draft = redirect.Text; return; }
                target = FindOpenChat(id) ?? CreateChat(selection.CliId);
                // A redirect must not interrupt work already running in the destination.
                if (target.IsBusy || target.IsConnecting) { from.Draft = redirect.Text; return; }
                await target.AttachFeatureAsync(feature, selection: selection);
            }
            else
            {
                target = CreateChat(selection.CliId);
                target.ApplyContinuationSelection(selection);
            }
            IsViewingHistory = false;
            HistoryTitle = null;
            ChatActivationRequested?.Invoke(this, target);
            await target.SendRedirectedAsync(redirect.Text, redirect.NewName, redirect.Attachments);
        }
        catch (Exception)
        {
            from.Draft = redirect.Text;
        }
    }

    private ChatViewModel? FindOpenChat(string featureId) =>
        Chats.FirstOrDefault(c => c.FeatureId == featureId);

    public async ValueTask DisposeAsync()
    {
        Smart.Instance.CancelOwner(ProjectPath);
        Health.Dispose();
        SmartActivity.Dispose();
        CliEndpoint.Dispose();
        McpTools.Dispose();
        FolderServers.Dispose();
        foreach (var chat in Chats)
        {
            chat.TurnFinished -= OnTurnFinished;
            chat.PromptStarted -= OnChatPromptStarted;
            chat.FilesChanged -= OnChatFilesChanged;
            chat.SessionStateChanged -= OnSessionStateChanged;
            chat.TodosChanged -= OnTodosChanged;
            chat.PropertyChanged -= OnChatPropertyChanged;
            chat.TopicRedirectRequested -= OnTopicRedirectRequested;
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
        Files.Dispose();


        try
        {
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // A session that will not stop in time is abandoned: its process dies with ours.
        }

        Features.Dispose();
    }
}
