using System.Collections.ObjectModel;

using Codale.App.Services;
using Codale.Core.Tasks;
using Codale.Terminal;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Codale.App.ViewModels;

/// <summary>What the terminal is up to, driving the tab's state icon.</summary>
public enum TerminalActivity
{
    /// <summary>The shell is alive and nothing else is running in it.</summary>
    Idle,

    /// <summary>A command or CLI is running inside the shell.</summary>
    Busy,

    /// <summary>The shell exited with code zero.</summary>
    Terminated,

    /// <summary>The shell exited with a failure code, or never started.</summary>
    Error,
}

/// <summary>
/// A terminal tab. Two kinds:
/// <list type="bullet">
/// <item>A <b>notebook</b> (every plain shell and project-command tab): commands are typed into an
/// input cell and each one becomes a <see cref="CommandBlockViewModel"/> in <see cref="Blocks"/>,
/// running in a process of its own. <see cref="State"/> carries the folder and environment from one
/// block to the next, so it reads like one shell while several commands can run at once.</item>
/// <item>An agent <b>CLI</b> tab (<see cref="IsCliTerminal"/>): one shell in a pseudoconsole running
/// the CLI's TUI, rendered by a classic TerminalView; the tab ends with the CLI.</item>
/// </list>
/// <see cref="Activity"/> tints the tab icon either way.
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject, IDisposable
{
    private readonly string _workingDirectory;
    private TerminalSession? _session;
    private bool _disposed;
    private bool _refreshing;
    private short _columns = 120;
    private short _rows = 30;

    /// <summary>
    /// A plain shell tab, or one running a project command - the commands menu opens
    /// the second kind with the command as its first block.
    /// </summary>
    public TerminalViewModel(string workingDirectory, string? command = null, string? commandName = null, string? cliId = null)
    {
        _workingDirectory = workingDirectory;
        CommandText = command;
        CommandName = commandName ?? command;
        CliId = cliId;
        State = IsCliTerminal ? new ShellState(workingDirectory, new Dictionary<string, string>()) : ShellState.Initial(workingDirectory);

        // A command tab reads busy from the start: the command launches with the tab.
        if (command is not null)
        {
            Activity = TerminalActivity.Busy;
        }
    }

    private static int _nextId;

    /// <summary>The handle the agent's terminal tools use.</summary>
    public string Id { get; } = "term-" + Interlocked.Increment(ref _nextId);

    /// <summary>Everything the terminal printed, as plain text, so the agent can read a run's result.</summary>
    public TerminalTranscript Transcript { get; } = new();

    /// <summary>The shell's exit code once it has ended; for a notebook, the last finished block's.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>The command line this tab runs; null when the tab is a plain shell.</summary>
    public string? CommandText { get; }

    /// <summary>The name the commands menu gave this run; the tab's title.</summary>
    public string? CommandName { get; }

    public bool IsCommandTerminal => CommandText is not null;

    /// <summary>The agent CLI this tab was opened for; such a tab is dedicated to it and ends with it.</summary>
    public string? CliId { get; }

    /// <summary>True for a dedicated CLI tab: the shell exits with the CLI, so the exit is known.</summary>
    public bool IsCliTerminal => CliId is not null;

    /// <summary>True for a notebook tab: commands run as blocks, not in one console.</summary>
    public bool IsNotebook => !IsCliTerminal;

    /// <summary>
    /// The headless session this CLI tab stands for: what the CLI reports (tasks, artifacts, changes) lands in
    /// it, and it is what the session panel shows while the tab is in front. Null for a shell or a command run.
    /// </summary>
    public ChatViewModel? AgentSession { get; init; }

    /// <summary>True when this terminal tab was started by an agent task rather than the user.</summary>
    public bool IsAgentTerminal { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial TerminalActivity Activity { get; set; } = TerminalActivity.Idle;

    /// <summary>
    /// Whether closing the tab would kill a running command. For a notebook, whether any block still
    /// runs. For a CLI tab <see cref="Activity"/> cannot tell - the exit lands a beat late - so this
    /// asks the process tree directly.
    /// </summary>
    public bool IsCommandRunning => IsNotebook ? Blocks.Any(b => b.IsRunning) : _session?.IsCommandRunning == true;

    /// <summary>
    /// Swaps a command typed at the shell prompt for an adjusted one (an agent CLI launched with
    /// Codale's MCP servers attached); null result leaves it as typed, an empty one drops it.
    /// </summary>
    public Func<string, string?>? LaunchRewriter { get; set; }

    /// <summary>
    /// Applies <see cref="LaunchRewriter"/> to the prompt line typed into a live shell, and keeps the
    /// typed command in the project's history for recall and autocomplete.
    /// </summary>
    public string? RewriteSubmitted(string promptLine)
    {
        if (!IsRunning)
        {
            return null;
        }

        CommandHistory.Record(_workingDirectory, Codale.Core.Shell.PromptLine.Command(promptLine), "terminal");
        return LaunchRewriter?.Invoke(promptLine);
    }

    /// <summary>Why the shell could not be started, shown in the terminal when set.</summary>
    public string StartupError { get; private set; } = "";

    /// <summary>Raw decoded output from the shell (or any block), raised on the UI thread.</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>Raised on the UI thread after the shell process has exited (CLI tabs only).</summary>
    public event EventHandler? Exited;

    // ------------------------------------------------------------------ notebook

    /// <summary>
    /// A tab holding one block maximized out of a notebook ("Maximize in new tab"): shown as a plain
    /// terminal - the block's output filling the tab, with no header and no input cell.
    /// </summary>
    public bool IsSingleBlock { get; set; }

    /// <summary>The folder the tab's command history is kept under (the project, or a command's folder).</summary>
    public string HistoryKey => _workingDirectory;

    /// <summary>The notebook's command blocks, oldest first.</summary>
    public ObservableCollection<CommandBlockViewModel> Blocks { get; } = [];

    /// <summary>The folder and environment the next block starts in.</summary>
    [ObservableProperty]
    public partial ShellState State { get; private set; }

    /// <summary>The shell blocks run under, from Settings.</summary>
    public string ShellPath => TerminalSession.ResolveShell(AppSettings.TerminalShell);

    /// <summary>A short name for the shell, shown beside the input cell.</summary>
    public string ShellName => Path.GetFileNameWithoutExtension(ShellPath) switch
    {
        "pwsh" => "pwsh",
        "powershell" => "PowerShell",
        "cmd" => "cmd",
        var other => other,
    };

    /// <summary>Raised when a block was added (run, or adopted from another tab).</summary>
    public event EventHandler<CommandBlockViewModel>? BlockAdded;

    /// <summary>
    /// Runs a command typed into the input cell as a new block. Recorded in the project's history; an
    /// agent CLI launch is handed to <see cref="LaunchRewriter"/>, which opens it in a tab of its own.
    /// Returns the block, or null when nothing ran here.
    /// </summary>
    public CommandBlockViewModel? RunCommand(string command, bool record = true)
    {
        if (!IsNotebook || _disposed)
        {
            return null;
        }

        var line = command.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        if (!IsOpen)
        {
            Open();
        }

        if (record)
        {
            CommandHistory.Record(_workingDirectory, line, "terminal");

            // The rewriter reads a prompt line, as the screen of a classic shell shows it, so it can
            // tell the folder the command was typed in.
            if (LaunchRewriter?.Invoke($"PS {State.Cwd}> {line}") is { } rewritten)
            {
                if (rewritten.Length == 0)
                {
                    return null;
                }

                line = rewritten;
            }
        }

        var block = new CommandBlockViewModel(line, State);
        AddBlock(block);
        block.Run(ShellPath, AppSettings.TerminalLoadProfile);
        return block;
    }

    /// <summary>Takes a block (running or not) from another notebook: "Maximize in new tab".</summary>
    public void AdoptBlock(CommandBlockViewModel block)
    {
        if (!IsOpen)
        {
            Open();
        }

        State = block.EndState ?? block.Start;
        AddBlock(block);
        if (!block.IsRunning)
        {
            UpdateNotebookActivity();
        }
    }

    /// <summary>Lets go of a block that moved to another notebook; its process keeps running there.</summary>
    public void ReleaseBlock(CommandBlockViewModel block)
    {
        if (!Blocks.Remove(block))
        {
            return;
        }

        Unhook(block);
        UpdateNotebookActivity();
    }

    /// <summary>Removes one finished (or stopped) block from the history.</summary>
    public void RemoveBlock(CommandBlockViewModel block)
    {
        if (!Blocks.Remove(block))
        {
            return;
        }

        Unhook(block);
        block.Dispose();
        UpdateNotebookActivity();
    }

    /// <summary>Runs a block's command again as a new block at the end, from the current state.</summary>
    public CommandBlockViewModel? Rerun(CommandBlockViewModel block) => RunCommand(block.Command, record: false);

    /// <summary>The newest block still running: what the notebook's Ctrl+C and the agent's stop reach.</summary>
    public CommandBlockViewModel? NewestRunningBlock => Blocks.LastOrDefault(b => b.IsRunning);

    private void AddBlock(CommandBlockViewModel block)
    {
        block.OutputReceived += OnBlockOutput;
        block.Exited += OnBlockExited;
        Blocks.Add(block);

        var header = $"$ {block.Command}\n";
        Transcript.Append(header);
        OutputReceived?.Invoke(this, header);

        TrimHistory();
        UpdateNotebookActivity();
        BlockAdded?.Invoke(this, block);
    }

    private void Unhook(CommandBlockViewModel block)
    {
        block.OutputReceived -= OnBlockOutput;
        block.Exited -= OnBlockExited;
    }

    /// <summary>Keeps the history to the Settings cap by dropping the oldest finished blocks.</summary>
    private void TrimHistory()
    {
        var cap = AppSettings.TerminalBlockHistory;
        while (Blocks.Count > cap && Blocks.FirstOrDefault(b => !b.IsRunning) is { } oldest)
        {
            Blocks.Remove(oldest);
            Unhook(oldest);
            oldest.Dispose();
        }
    }

    private void OnBlockOutput(object? sender, string text)
    {
        Transcript.Append(text);
        OutputReceived?.Invoke(this, text);
    }

    private void OnBlockExited(object? sender, EventArgs e)
    {
        if (sender is not CommandBlockViewModel block)
        {
            return;
        }

        // The folder and environment move on with the newest block that finished, so a cd typed
        // after a long-running server started still takes effect.
        if (block.EndState is { } end && ReferenceEquals(block, Blocks.LastOrDefault(b => !b.IsRunning)))
        {
            State = end;
        }

        ExitCode = block.ExitCode;
        Transcript.Append("\n");
        UpdateNotebookActivity();
    }

    private void UpdateNotebookActivity()
    {
        if (!IsNotebook)
        {
            return;
        }

        OnPropertyChanged(nameof(IsCommandRunning));
        if (Blocks.Any(b => b.IsRunning))
        {
            Activity = TerminalActivity.Busy;
        }
        else if (IsCommandTerminal && Blocks.Count > 0)
        {
            // A command tab is about its command: done is done, red when it failed.
            Activity = Blocks[^1].Status == BlockStatus.Succeeded ? TerminalActivity.Terminated : TerminalActivity.Error;
        }
        else
        {
            Activity = TerminalActivity.Idle;
        }
    }

    private void Open()
    {
        IsOpen = true;
        IsRunning = true;
    }

    // ------------------------------------------------------------------ lifecycle

    [RelayCommand]
    public void Toggle()
    {
        if (IsNotebook)
        {
            if (IsOpen)
            {
                return;
            }

            Open();

            // A command tab runs its command as its first block (already in the history; not re-recorded).
            if (CommandText is { } command && Blocks.Count == 0)
            {
                RunCommand(command, record: false);
            }

            return;
        }

        IsOpen = !IsOpen;

        // The shell is started lazily: opening a project should not spawn a shell the
        // user may never look at.
        if (IsOpen && _session is null)
        {
            Start();
        }
    }

    /// <summary>
    /// Ends the session: the terminal tab was closed, so the shell (or every block's process) is
    /// terminated rather than left running for a tab that no longer exists.
    /// </summary>
    public void Close()
    {
        IsOpen = false;
        IsRunning = false;
        var session = _session;
        _session = null;
        session?.Dispose();
        DisposeBlocks();
    }

    private void DisposeBlocks()
    {
        foreach (var block in Blocks.ToList())
        {
            Unhook(block);
            block.Dispose();
        }

        Blocks.Clear();
    }

    /// <summary>Raised on the UI thread when a restart wipes the screen, scrollback and transcript.</summary>
    public event EventHandler? Cleared;

    /// <summary>
    /// Starts over in the same tab. A notebook clears its history (stopping what still runs) and a
    /// command tab runs its command again; a CLI tab is never restarted. The restart starts clean:
    /// the previous run's output and transcript are dropped, so a tab restarted again and again
    /// never grows.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (IsCliTerminal || _disposed || !IsOpen || _refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            DisposeBlocks();
            await Task.Yield();
            if (_disposed || !IsOpen)
            {
                return;
            }

            ExitCode = null;
            Transcript.Clear();
            Cleared?.Invoke(this, EventArgs.Empty);
            if (CommandText is { } command)
            {
                State = ShellState.Initial(_workingDirectory);
                RunCommand(command, record: false);
            }

            UpdateNotebookActivity();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Clears a notebook's history: the menu's Clear. Running blocks stay.</summary>
    public void ClearFinishedBlocks()
    {
        foreach (var block in Blocks.Where(b => !b.IsRunning).ToList())
        {
            RemoveBlock(block);
        }
    }

    private void Start()
    {
        StartupError = "";

        try
        {
            // A CLI runs wrapped in the shell so .cmd shims resolve; the shell exits with it. The
            // shell comes from Settings, falling back to the automatic probe when missing.
            var shell = TerminalSession.ResolveShell(AppSettings.TerminalShell);
            var command = CommandText is { } line ? TerminalSession.WrapCommand(shell, line, keepShell: !IsCliTerminal) : shell;

            // Created on the UI thread, so the session delivers output there.
            var session = new TerminalSession();
            _session = session;
            session.OutputReceived += (_, text) =>
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }
                Transcript.Append(text);
                OutputReceived?.Invoke(this, text);
            };
            session.Exited += (_, exitCode) =>
            {
                if (!ReferenceEquals(_session, session))
                {
                    return;
                }
                ExitCode = exitCode;
                IsRunning = false;
                Activity = exitCode == 0 ? TerminalActivity.Terminated : TerminalActivity.Error;
                Exited?.Invoke(this, EventArgs.Empty);
            };
            session.Start(_workingDirectory, command, _columns, _rows);
            IsRunning = true;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StartupError = $"Could not start a shell: {ex.Message}";
            Activity = TerminalActivity.Error;
        }
    }

    /// <summary>Raised when the user sends a line (Enter) into the terminal; emulator replies never contain one.</summary>
    public event EventHandler? InputSubmitted;

    /// <summary>
    /// Forwards keystrokes, pastes and emulator replies into the shell. In a notebook they reach the
    /// newest running block (the agent's Ctrl+C stop goes through here).
    /// </summary>
    public Task WriteAsync(string text)
    {
        if (text.Contains('\r'))
        {
            InputSubmitted?.Invoke(this, EventArgs.Empty);
        }

        if (IsNotebook)
        {
            return NewestRunningBlock?.WriteAsync(text) ?? Task.CompletedTask;
        }

        return _session is { } session && IsRunning ? session.WriteAsync(text) : Task.CompletedTask;
    }

    /// <summary>Resizes the pseudoconsole to the view's cell grid (CLI tabs; a block sizes its own).</summary>
    public void Resize(short columns, short rows)
    {
        _columns = columns;
        _rows = rows;
        _session?.Resize(columns, rows);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var session = _session;
        _session = null;
        session?.Dispose();
        DisposeBlocks();
    }
}
