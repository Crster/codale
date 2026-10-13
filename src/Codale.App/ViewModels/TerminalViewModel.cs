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
        _session?.Dispose();
        _session = null;
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
            _session = new TerminalSession();
            _session.OutputReceived += (_, text) =>
            {
                Transcript.Append(text);
                OutputReceived?.Invoke(this, text);
            };
            _session.Exited += (_, exitCode) =>
            {
                ExitCode = exitCode;
                IsRunning = false;
                Activity = exitCode == 0 ? TerminalActivity.Terminated : TerminalActivity.Error;
                Exited?.Invoke(this, EventArgs.Empty);
            };
            _session.Start(_workingDirectory, command);
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
    public void Resize(short columns, short rows) => _session?.Resize(columns, rows);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session?.Dispose();
        _session = null;
    }
}
