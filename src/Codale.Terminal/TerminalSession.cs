using System.Text;
using System.Threading.Channels;

namespace Codale.Terminal;

/// <summary>
/// A shell running in a pseudoconsole, emitting its raw output as decoded text.
/// Rendering is somebody else's job - the app pipes the chunks into the native
/// TerminalEmulator, which does the VT parsing.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly ConPty _pty = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SynchronizationContext? _uiContext;

    /// <summary>
    /// Keystrokes queue here and one writer drains them in order. Fire-and-forget
    /// writes straight to the pipe run on arbitrary pool threads, so a fast typist's
    /// characters could race each other and arrive reordered.
    /// </summary>
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly StringBuilder _pending = new();
    private bool _flushScheduled;
    private Task? _reader;
    private Task? _writer;
    private Task? _watcher;
    private bool _disposed;

    /// <summary>How long Dispose waits for the pseudoconsole teardown before leaving it to finish in the background.</summary>
    private static readonly TimeSpan TeardownWait = TimeSpan.FromSeconds(1);

    public TerminalSession()
    {
        // The consumer feeds a UI-bound renderer, so output is delivered on the UI thread.
        _uiContext = SynchronizationContext.Current;
    }

    /// <summary>Diagnostics sink the host app wires up; null (silent) in tests and tools.</summary>
    public static Action<string>? Log { get; set; }

    public bool IsRunning => _reader is { IsCompleted: false };

    /// <summary>The shell's process id, root of the tab's process tree.</summary>
    public int ProcessId => _pty.ProcessId;

    /// <summary>
    /// Raised on the UI thread when the shell exits, with its exit code (non-zero on
    /// failure; -1 when the code could not be read because the process was gone).
    /// </summary>
    public event EventHandler<int>? Exited;

    /// <summary>
    /// Raised on the UI thread with each decoded output chunk, ready to be written
    /// straight into the terminal view.
    /// </summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>Picks the best available shell: pwsh, then Windows PowerShell, then cmd.</summary>
    public static string ResolveDefaultShell()
    {
        foreach (var candidate in new[] { "pwsh.exe", "powershell.exe" })
        {
            var resolved = ResolveOnPath(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
    }

    /// <summary>
    /// Resolves the shell the user picked in Settings. "auto" and a named shell that
    /// is not installed both fall back to the automatic probe, so a terminal always
    /// starts with something that exists.
    /// </summary>
    public static string ResolveShell(string preference)
    {
        var resolved = preference switch
        {
            "pwsh" => ResolveOnPath("pwsh.exe"),
            "powershell" => ResolveOnPath("powershell.exe"),
            "cmd" => ResolveOnPath("cmd.exe"),
            _ => null,
        };

        return resolved ?? ResolveDefaultShell();
    }

    /// <summary>
    /// Wraps a command line so it runs inside the resolved default shell, which then
    /// stays open. CreateProcess cannot run the <c>.cmd</c> shims half the JavaScript
    /// toolchain installs as, and a command tab should not vanish the moment a
    /// one-shot command ends - the shell that stays behind keeps the output readable.
    /// </summary>
    public static string BuildCommandShell(string command) =>
        WrapCommand(ResolveDefaultShell(), command);

    /// <summary>The wrapping itself, testable against a pinned shell path.</summary>
    public static string WrapCommand(string shell, string command)
    {
        var name = Path.GetFileName(shell);

        if (name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            // EncodedCommand sidesteps every quoting hazard a raw -Command string has.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            return $"\"{shell}\" -NoExit -EncodedCommand {encoded}";
        }

        return $"\"{shell}\" /k \"{command}\"";
    }

    private static string? ResolveOnPath(string executable)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry should not stop the search.
            }
        }

        return null;
    }

    public void Start(string workingDirectory, string? command = null, short columns = 120, short rows = 30)
    {
        command ??= ResolveDefaultShell();

        Log?.Invoke($"start shell={command} cwd={workingDirectory} size={columns}x{rows}");
        _pty.Start(command, workingDirectory, columns, rows);
        Log?.Invoke($"started pid={_pty.ProcessId}");
        _reader = Task.Run(ReadLoopAsync);
        _writer = Task.Run(WriteLoopAsync);
        _watcher = Task.Run(WatchForExitAsync);
    }

    /// <summary>
    /// Watches the shell process itself.
    /// </summary>
    /// <remarks>
    /// The output pipe is owned by the pseudoconsole, not the child, so it stays open
    /// until ClosePseudoConsole. Waiting for EOF to learn that the shell exited would
    /// therefore wait forever - the process handle is the only reliable signal.
    /// </remarks>
    private async Task WatchForExitAsync()
    {
        var exitCode = -1;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(_pty.ProcessId);
            await process.WaitForExitAsync(_cancellation.Token).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (ArgumentException)
        {
            // Already gone by the time we looked; the code stays unreadable.
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            // Same race, hit between WaitForExitAsync and ExitCode.
        }

        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        // Let the last of the output drain before announcing the exit.
        await Task.Delay(100).ConfigureAwait(false);
        Log?.Invoke($"exited pid={_pty.ProcessId} code={exitCode}");
        Post(() => Exited?.Invoke(this, exitCode));
    }

    public void Resize(short columns, short rows)
    {
        Log?.Invoke($"resize pid={_pty.ProcessId} {columns}x{rows}");
        _pty.Resize(columns, rows);
    }

    /// <summary>Queues input for the shell; returns immediately, writes land in call order.</summary>
    public Task WriteAsync(string text, CancellationToken ct = default)
    {
        _input.Writer.TryWrite(text);
        return Task.CompletedTask;
    }

    private async Task WriteLoopAsync()
    {
        var builder = new StringBuilder();

        try
        {
            while (await _input.Reader.WaitToReadAsync(_cancellation.Token).ConfigureAwait(false))
            {
                // Batch whatever queued up while the last write was in flight.
                builder.Clear();
                while (_input.Reader.TryRead(out var text))
                {
                    builder.Append(text);
                }

                var bytes = Encoding.UTF8.GetBytes(builder.ToString());
                await _pty.Input.WriteAsync(bytes, _cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposing.
        }
        catch (IOException ex)
        {
            // The pipe closed because the pseudoconsole was torn down.
            Log?.Invoke($"input pipe closed: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Same teardown, hit between the wait and the write.
        }
    }

    private async Task ReadLoopAsync()
    {
        var bytes = new byte[8192];

        // Decoder, not Encoding.GetString: a UTF-8 sequence can straddle a read.
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];

        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var read = await _pty.Output.ReadAsync(bytes, _cancellation.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var charCount = decoder.GetChars(bytes, 0, read, chars, 0);
                if (charCount == 0)
                {
                    continue;
                }

                QueueOutput(new string(chars, 0, charCount));
            }
        }
        catch (OperationCanceledException)
        {
            // Disposing.
        }
        catch (IOException ex)
        {
            Log?.Invoke($"output pipe closed: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Teardown closed the stream under a pending read.
        }
    }

    /// <summary>
    /// Output produced while a UI post is still pending joins it instead of posting its
    /// own: a chatty build otherwise queues thousands of tiny UI callbacks, each one
    /// re-parsing, re-scrolling and repainting.
    /// </summary>
    private void QueueOutput(string text)
    {
        var schedule = false;
        lock (_pending)
        {
            _pending.Append(text);
            if (!_flushScheduled)
            {
                _flushScheduled = true;
                schedule = true;
            }
        }

        if (schedule)
        {
            Post(FlushOutput);
        }
    }

    private void FlushOutput()
    {
        string text;
        lock (_pending)
        {
            text = _pending.ToString();
            _pending.Clear();
            _flushScheduled = false;
        }

        if (text.Length > 0 && !_disposed)
        {
            OutputReceived?.Invoke(this, text);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Log?.Invoke($"dispose pid={_pty.ProcessId}");
        _input.Writer.TryComplete();
        _cancellation.Cancel();

        // Closing the pseudoconsole is what unblocks the pipe read (a synchronous pipe read
        // ignores the token), and it can stall while output drains - so it runs off the
        // caller's (UI) thread. The wait is bounded; shutdown still reaps the shell.
        var teardown = Task.Run(_pty.Dispose);
        try
        {
            teardown.Wait(TeardownWait);
        }
        catch (AggregateException ex)
        {
            Log?.Invoke($"teardown failed: {ex.InnerException?.Message}");
        }

        // The loops read the token until they unwind, so it can only be disposed after them.
        var loops = new[] { teardown, _reader, _writer, _watcher }.OfType<Task>().ToArray();
        _ = Task.WhenAll(loops).ContinueWith(
            _ => _cancellation.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
