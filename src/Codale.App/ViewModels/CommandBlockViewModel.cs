using Codale.App.Services;
using Codale.Core.Tasks;
using Codale.Terminal;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>How much room a command block takes in the notebook terminal.</summary>
public enum BlockSize
{
    /// <summary>Full width, <see cref="AppSettings.TerminalBlockLines"/> lines.</summary>
    Regular,

    /// <summary>The only block of its tab: fills the tab (set when a block was opened in a tab of its own).</summary>
    Fill,
}

/// <summary>Where a command block is docked beside the centre tabs; <see cref="None"/> keeps it in its notebook.</summary>
public enum BlockDock
{
    None,

    /// <summary>The whole height of the dock column.</summary>
    Right,

    /// <summary>The top half of the dock column.</summary>
    TopRight,

    /// <summary>The bottom half of the dock column.</summary>
    BottomRight,
}

public enum BlockStatus
{
    Running,
    Succeeded,
    Failed,
    Stopped,
}

/// <summary>
/// One command in the notebook terminal: the command line, the process running it, and an emulator
/// holding what it printed. The emulator lives here rather than in the view, so a block's output
/// survives its view being recycled or the block moving to another tab, and the process keeps
/// running through both. Created on the UI thread; output, exit and state are delivered there.
/// </summary>
public sealed partial class CommandBlockViewModel : ObservableObject, IDisposable
{
    private readonly TerminalSession _session = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private BlockLaunch? _launch;
    private bool _disposed;
    private short _columns = 120;
    private short _rows;

    public CommandBlockViewModel(string command, ShellState start)
    {
        Command = command;
        Start = start;
        _rows = (short)AppSettings.TerminalBlockLines;
        Emulator = new TerminalEmulator(_columns, _rows) { ScrollbackLimit = AppSettings.TerminalScrollback };

        // DSR/DA replies go back to the process whether or not a view is showing the block.
        Emulator.Response += reply => _ = _session.WriteAsync(reply);
        Emulator.Invalidated += OnEmulatorInvalidated;
    }

    /// <summary>The command line as the user typed it.</summary>
    public string Command { get; }

    /// <summary>The folder and environment the block started in.</summary>
    public ShellState Start { get; }

    public string Cwd => Start.Cwd;

    /// <summary>The folder and environment the command ended with; null until it exits, or when it left none (it called exit, or was stopped).</summary>
    public ShellState? EndState { get; private set; }

    /// <summary>What the command printed, rendered by the block's view.</summary>
    public TerminalEmulator Emulator { get; }

    /// <summary>What the command printed, as plain text for the agent and for copying.</summary>
    public TerminalTranscript Transcript { get; } = new();

    public DateTimeOffset StartedAt { get; private set; }

    [ObservableProperty]
    public partial BlockStatus Status { get; private set; } = BlockStatus.Running;

    [ObservableProperty]
    public partial int? ExitCode { get; private set; }

    [ObservableProperty]
    public partial BlockSize Size { get; set; } = BlockSize.Regular;

    /// <summary>Where the block is docked; while docked, its notebook hides it and the dock shows it.</summary>
    [ObservableProperty]
    public partial BlockDock Dock { get; set; } = BlockDock.None;

    /// <summary>True while a full-screen app (vim, htop) has the alternate screen.</summary>
    [ObservableProperty]
    public partial bool InAlternate { get; private set; }

    /// <summary>Why the process could not be started; the block shows it as its output.</summary>
    public string StartupError { get; private set; } = "";

    public bool IsRunning => Status == BlockStatus.Running;

    /// <summary>How long the command ran, or has been running.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Raw output, raised on the UI thread after it was fed to <see cref="Emulator"/>.</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>Raised on the UI thread once the command has exited and its end state was read.</summary>
    public event EventHandler? Exited;

    /// <summary>Starts the command. Called once, on the UI thread.</summary>
    public void Run(string shell, bool loadProfile)
    {
        StartedAt = DateTimeOffset.Now;
        _clock.Start();

        try
        {
            _launch = BlockCommand.Build(shell, Command, BlockCommand.DefaultStateDirectory, loadProfile);
            _session.OutputReceived += OnSessionOutput;
            _session.Exited += OnSessionExited;
            _session.Start(Start.Cwd, _launch.CommandLine, _columns, _rows, Start.Environment);
        }
        catch (Exception ex)
        {
            StartupError = $"Could not run the command: {ex.Message}";
            Emulator.Feed($"\x1b[31m{StartupError}\x1b[0m\r\n");
            Transcript.Append(StartupError + "\n");
            Finish(-1, BlockStatus.Failed);
        }
    }

    private void OnSessionOutput(object? sender, string text)
    {
        if (_disposed)
        {
            return;
        }

        Emulator.Feed(text);
        Transcript.Append(text);
        OutputReceived?.Invoke(this, text);
    }

    private void OnSessionExited(object? sender, int exitCode)
    {
        if (_disposed || !IsRunning)
        {
            return;
        }

        Finish(exitCode, Status == BlockStatus.Stopped || _stopping ? BlockStatus.Stopped
            : exitCode == 0 ? BlockStatus.Succeeded : BlockStatus.Failed);
    }

    private void Finish(int exitCode, BlockStatus status)
    {
        _clock.Stop();
        if (_launch is { } launch)
        {
            EndState = ShellState.Read(launch.StateFile);
            launch.Cleanup();
        }

        ExitCode = exitCode;
        Status = status;
        OnPropertyChanged(nameof(IsRunning));
        Exited?.Invoke(this, EventArgs.Empty);
    }

    private void OnEmulatorInvalidated()
    {
        if (Emulator.InAlternate != InAlternate)
        {
            InAlternate = Emulator.InAlternate;
        }
    }

    /// <summary>Keystrokes, pasted text or a line from the notebook's input, sent to the running command.</summary>
    public Task WriteAsync(string text) =>
        IsRunning && !_disposed ? _session.WriteAsync(text) : Task.CompletedTask;

    /// <summary>The block's view fitted a new grid; the pseudoconsole follows.</summary>
    public void Resize(int columns, int rows)
    {
        _columns = (short)Math.Clamp(columns, 1, short.MaxValue);
        _rows = (short)Math.Clamp(rows, 1, short.MaxValue);
        if (Emulator.Cols != _columns || Emulator.Rows != _rows)
        {
            Emulator.Resize(_columns, _rows);
        }

        if (IsRunning && !_disposed)
        {
            _session.Resize(_columns, _rows);
        }
    }

    private bool _stopping;

    /// <summary>Interrupts the command (Ctrl+C) and ends its process if it is still alive shortly after.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning || _stopping)
        {
            return;
        }

        _stopping = true;
        await _session.WriteAsync("\x03");
        await Task.Delay(1500);
        if (IsRunning && !_disposed)
        {
            // The pseudoconsole teardown kills the process; its exit event never comes after that.
            _session.Dispose();
            Finish(-1, BlockStatus.Stopped);
        }
    }

    /// <summary>Plain text of the block: the command, then what it printed.</summary>
    public string OutputText => Transcript.Read().Text.TrimEnd();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.OutputReceived -= OnSessionOutput;
        _session.Exited -= OnSessionExited;
        _session.Dispose();
        _launch?.Cleanup();
        Emulator.Invalidated -= OnEmulatorInvalidated;
    }
}
