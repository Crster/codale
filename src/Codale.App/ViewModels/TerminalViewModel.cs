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
/// A shell in a pseudoconsole, rendered by the native TerminalView control. This object
/// owns the process and its raw byte stream; the view owns the display. Closing the
/// terminal tab terminates the shell. <see cref="Activity"/> tints the tab icon; a
/// command tab reads busy from its start until the shell exits.
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
    /// the second kind with the command wrapped so its shell stays after it ends.
    /// </summary>
    public TerminalViewModel(string workingDirectory, string? command = null, string? commandName = null, string? cliId = null)
    {
        _workingDirectory = workingDirectory;
        CommandText = command;
        CommandName = commandName ?? command;
        CliId = cliId;

        // A command tab reads busy from the start: the command launches with the shell.
        if (command is not null)
        {
            Activity = TerminalActivity.Busy;
        }
    }

    private static int _nextId;

    /// <summary>The handle the agent's terminal tools use.</summary>
    public string Id { get; } = "term-" + Interlocked.Increment(ref _nextId);

    /// <summary>Everything the shell printed, as plain text, so the agent can read a run's result.</summary>
    public TerminalTranscript Transcript { get; } = new();

    /// <summary>The shell's exit code once it has ended.</summary>
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
    /// Whether closing the tab would kill a running command: the shell is still alive and has a
    /// process running in it. <see cref="Activity"/> cannot tell - a command tab reads busy until its
    /// shell exits, which with a kept shell is long after the command ended, and the exit itself lands
    /// a beat late - so this asks the process tree directly.
    /// </summary>
    public bool IsCommandRunning => _session?.IsCommandRunning == true;

    /// <summary>
    /// Swaps a command typed at the shell prompt for an adjusted one (an agent CLI launched with
    /// Codale's MCP servers attached); null result leaves it as typed.
    /// </summary>
    public Func<string, string?>? LaunchRewriter { get; set; }

    /// <summary>Applies <see cref="LaunchRewriter"/> to the prompt line typed into a live shell.</summary>
    public string? RewriteSubmitted(string promptLine) =>
        IsRunning ? LaunchRewriter?.Invoke(promptLine) : null;

    /// <summary>Why the shell could not be started, shown in the terminal when set.</summary>
    public string StartupError { get; private set; } = "";

    /// <summary>Raw decoded output from the shell, raised on the UI thread.</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>Raised on the UI thread after the shell process has exited.</summary>
    public event EventHandler? Exited;

    [RelayCommand]
    public void Toggle()
    {
        IsOpen = !IsOpen;

        // The shell is started lazily: opening a project should not spawn a shell the
        // user may never look at.
        if (IsOpen && _session is null)
        {
            Start();
        }
    }

    /// <summary>
    /// Ends the session: the terminal tab was closed, so the shell is terminated
    /// rather than left running for a tab that no longer exists.
    /// </summary>
    public void Close()
    {
        IsOpen = false;
        IsRunning = false;
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    /// <summary>Ends a project command's shell and reruns its original command in the same tab.</summary>
    public async Task RefreshAsync()
    {
        if (!IsCommandTerminal || IsCliTerminal || _disposed || !IsOpen || _refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var previous = _session;
            _session = null;
            IsRunning = false;
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }

            // Closing the tab while teardown is pending must not launch another process.
            if (_disposed || !IsOpen)
            {
                return;
            }

            ExitCode = null;
            Activity = TerminalActivity.Busy;
            const string marker = "\r\n\x1b[0m\x1b[90m[restarting command]\x1b[0m\r\n";
            Transcript.Append(marker);
            OutputReceived?.Invoke(this, marker);
            Start();
            if (StartupError.Length > 0)
            {
                var error = $"\x1b[31m{StartupError}\x1b[0m\r\n";
                Transcript.Append(error);
                OutputReceived?.Invoke(this, error);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Start()
    {
        StartupError = "";
        CrashLog.Trace("terminal: session start begin");

        try
        {
            // A command runs wrapped in the shell so .cmd shims resolve and the shell
            // outlives it; a plain tab just gets the shell. The shell comes from
            // Settings, falling back to the automatic probe when missing.
            var shell = TerminalSession.ResolveShell(AppSettings.TerminalShell);
            var command = CommandText is { } line ? TerminalSession.WrapCommand(shell, line, keepShell: !IsCliTerminal) : shell;
            CrashLog.Trace($"terminal: shell = {command}");

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
            CrashLog.Trace($"terminal: session started, pid = {_session.ProcessId}");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StartupError = $"Could not start a shell: {ex.Message}";
            Activity = TerminalActivity.Error;
            CrashLog.Write("Terminal", "session start failed", ex);
        }
    }

    /// <summary>Raised when the user sends a line (Enter) into the terminal; emulator replies never contain one.</summary>
    public event EventHandler? InputSubmitted;

    /// <summary>Forwards keystrokes, pastes and emulator replies into the shell.</summary>
    public Task WriteAsync(string text)
    {
        if (text.Contains('\r'))
        {
            InputSubmitted?.Invoke(this, EventArgs.Empty);
        }

        return _session is { } session && IsRunning ? session.WriteAsync(text) : Task.CompletedTask;
    }

    /// <summary>Resizes the pseudoconsole to the view's cell grid.</summary>
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
    }
}
