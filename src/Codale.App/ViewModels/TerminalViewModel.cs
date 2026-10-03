using Codale.App.Services;
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
/// terminal tab terminates the shell. A background poll of the shell's process tree
/// keeps <see cref="RunningCommand"/> and <see cref="Activity"/> current, so the tab
/// can read "claude" with a state-tinted icon.
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject, IDisposable
{
    /// <summary>Console plumbing that no user command is expected to run.</summary>
    private static readonly HashSet<string> PlumbingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost", "openconsole",
    };

    /// <summary>Shells: present at the chain's root, not interesting as "the command".</summary>
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost", "openconsole", "cmd", "powershell", "pwsh",
    };

    private readonly string _workingDirectory;
    private readonly SynchronizationContext? _uiContext;
    private TerminalSession? _session;
    private bool _disposed;

    /// <summary>
    /// A plain shell tab, or one running a project command - the commands menu opens
    /// the second kind with the command wrapped so its shell stays after it ends.
    /// </summary>
    public TerminalViewModel(string workingDirectory, string? command = null, string? commandName = null)
    {
        _workingDirectory = workingDirectory;
        CommandText = command;
        CommandName = commandName ?? command;

        // A command tab reads busy from the start: the command launches with the shell,
        // and the first process-tree poll confirms or corrects it.
        if (command is not null)
        {
            Activity = TerminalActivity.Busy;
        }

        // The consumer feeds a UI-bound renderer, so state changes are posted to the UI thread.
        _uiContext = SynchronizationContext.Current;
    }

    /// <summary>The command line this tab runs; null when the tab is a plain shell.</summary>
    public string? CommandText { get; }

    /// <summary>The name the commands menu gave this run; the tab's title.</summary>
    public string? CommandName { get; }

    public bool IsCommandTerminal => CommandText is not null;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial TerminalActivity Activity { get; set; } = TerminalActivity.Idle;

    /// <summary>
    /// The foreground CLI running inside the shell, by process name - "claude", "npm",
    /// "python" - empty when only the shell itself is running.
    /// </summary>
    [ObservableProperty]
    public partial string RunningCommand { get; set; } = "";

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
            var command = CommandText is { } line ? TerminalSession.WrapCommand(shell, line) : shell;
            CrashLog.Trace($"terminal: shell = {command}");

            // Created on the UI thread, so the session delivers output there.
            _session = new TerminalSession();
            _session.OutputReceived += (_, text) => OutputReceived?.Invoke(this, text);
            _session.Exited += (_, exitCode) =>
            {
                IsRunning = false;
                Activity = exitCode == 0 ? TerminalActivity.Terminated : TerminalActivity.Error;
                Exited?.Invoke(this, EventArgs.Empty);
            };
            _session.Start(_workingDirectory, command);
            IsRunning = true;
            CrashLog.Trace($"terminal: session started, pid = {_session.ProcessId}");
            _ = Task.Run(MonitorProcessTreeAsync);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StartupError = $"Could not start a shell: {ex.Message}";
            Activity = TerminalActivity.Error;
            CrashLog.Write("Terminal", "session start failed", ex);
        }
    }

    /// <summary>
    /// Polls the shell's process tree until the shell exits. Each pass picks up the
    /// foreground CLI ("claude") and whether anything is running at all; the events
    /// are raised on the UI thread because the tab header binds to these properties.
    /// </summary>
    private async Task MonitorProcessTreeAsync()
    {
        var shellPid = _session?.ProcessId ?? 0;

        while (!_disposed && shellPid != 0 && IsRunning && _session is not null)
        {
            await Task.Delay(1500).ConfigureAwait(false);

            var chain = await Task.Run(() => ProcessTree.GetCommandChain(shellPid)).ConfigureAwait(false);

            Post(() =>
            {
                if (_disposed || _session is null)
                {
                    return;
                }

                // The chain is root shell first; the first non-shell process is the CLI.
                var command = "";
                foreach (var name in chain.Skip(1))
                {
                    if (!ShellProcesses.Contains(name))
                    {
                        command = name;
                        break;
                    }
                }

                RunningCommand = command;
                var busy = chain.Skip(1).Any(name => !PlumbingProcesses.Contains(name));
                Activity = busy ? TerminalActivity.Busy : TerminalActivity.Idle;
            });
        }
    }

    private void Post(Action action)
    {
        if (_uiContext is null)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    /// <summary>Forwards keystrokes, pastes and emulator replies into the shell.</summary>
    public Task WriteAsync(string text) =>
        _session is { } session && IsRunning ? session.WriteAsync(text) : Task.CompletedTask;

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
