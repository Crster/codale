using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;

namespace Codale.App;

/// <summary>
/// The app's diagnostics, all under %LOCALAPPDATA%\Codale:
/// <list type="bullet">
/// <item><c>crash.log</c> - unhandled exceptions and caught-and-reported failures, with stacks.
/// Rolls to <c>crash.log.1</c> past 1 MB. A stowed XAML exception kills the process without even a .NET Runtime event-log entry,
/// so every handler lands what it knows here.</item>
/// <item><c>session.log</c> - a timestamped, thread-tagged trace of what the app did
/// (tab lifecycle, terminal and editor activity, file I/O, sessions). Rolls to
/// <c>session.log.1</c> past 2 MB so a reproduction is never lost to truncation.</item>
/// <item><c>raw\*.jsonl</c> - one CLI session's raw stdout, pruned after three days.</item>
/// </list>
/// Everything here is best-effort: logging must never cost the app its life.
/// </summary>
public static class CrashLog
{
    private const long MaxTraceBytes = 2_000_000;
    private const long MaxCrashBytes = 1_000_000;

    private static readonly object Gate = new();

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");

    private static readonly string LogPath = Path.Combine(Folder, "crash.log");
    private static readonly string TracePath = Path.Combine(Folder, "session.log");

    private static StreamWriter? _trace;

    /// <summary>
    /// A fresh file for one CLI session's raw stdout - the only record of what the CLI
    /// last said when a turn stalls. Kept beside the other logs, local only, and pruned
    /// after a few days so it never grows without bound.
    /// </summary>
    public static string? NewRawLogPath(string label)
    {
        try
        {
            var folder = Path.Combine(Folder, "raw");
            Directory.CreateDirectory(folder);

            foreach (var old in Directory.EnumerateFiles(folder, "*.jsonl"))
            {
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-3))
                {
                    File.Delete(old);
                }
            }

            return Path.Combine(folder, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Wires the process-wide handlers. Call once, on the UI thread.</summary>
    public static void Install()
    {
        Info("app", $"start pid={Environment.ProcessId} os={Environment.OSVersion.Version} " +
                    $"clr={Environment.Version} args={Environment.CommandLine}");

        // Marking an exception handled keeps the process alive, which is only right for
        // failures that leave nothing half-done (a cancelled operation, a window that closed
        // under an async callback). Anything else is logged and left to take the process down
        // rather than limp on with state nobody can vouch for.
        Application.Current.UnhandledException += (_, e) =>
        {
            Write("XAML", e.Message, e.Exception);
            e.Handled = IsSafeToContinue(e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain", e.ExceptionObject.ToString(), e.ExceptionObject as Exception);

        // Fires late (finalization) and only for fully discarded tasks, but a discarded
        // task is exactly how fire-and-forget work is launched here.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("UnobservedTask", e.Exception.ToString(), e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Info("app", "exit");
            FlushPending();
            lock (Gate)
            {
                _trace?.Dispose();
                _trace = null;
            }
        };
    }

    // RO_E_CLOSED and RPC_E_DISCONNECTED: a WinRT object or window went away mid-call.
    private const int RoEClosed = unchecked((int)0x80000013);
    private const int RpcEDisconnected = unchecked((int)0x80010108);

    private static bool IsSafeToContinue(Exception? exception) => exception switch
    {
        OperationCanceledException or ObjectDisposedException => true,
        COMException com => com.HResult is RoEClosed or RpcEDisconnected,
        _ => false,
    };

    /// <summary>An exception (or failure report) with its stack, in crash.log; also noted in the trace.</summary>
    public static void Write(string source, string? message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaxCrashBytes)
                {
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                }

                File.AppendAllText(
                    LogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [t{Environment.CurrentManagedThreadId}] {source}\n{message}\n{exception}\n\n");
            }

            Line("ERROR", source, $"{message} {exception?.GetType().Name}: {exception?.Message}", flushImmediately: true);
        }
        catch
        {
            // Nowhere left to report to.
        }
    }

    /// <summary>Lifecycle trace without a category, kept for the many existing call sites.</summary>
    public static void Trace(string message) => Line("INFO", "app", message);

    public static void Debug(string category, string message) => Line("DEBUG", category, message);

    public static void Info(string category, string message) => Line("INFO", category, message);

    public static void Warn(string category, string message) => Line("WARN", category, message);

    /// <summary>A handled exception: worth a trace line and a stack, but not a crash.</summary>
    public static void Error(string category, string message, Exception exception) =>
        Write(category, message, exception);

    // Trace lines used to take the log lock and disk-flush on the calling (UI) thread
    // per line - twice per tool call during a turn. They queue here and a timer drains
    // them off-thread; anything that may precede a hard crash flushes immediately.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> PendingLines = new();
    private static System.Threading.Timer? _flushTimer;

    private static void Line(string level, string category, string message, bool flushImmediately = false)
    {
        try
        {
            PendingLines.Enqueue(
                $"[{DateTimeOffset.Now:HH:mm:ss.fff}] [t{Environment.CurrentManagedThreadId,-2}] {level,-5} {category}: {message}");

            if (flushImmediately)
            {
                FlushPending();
            }
            else
            {
                EnsureFlusher();
            }
        }
        catch
        {
            // Tracing must never cost the app its life.
        }
    }

    private static void EnsureFlusher()
    {
        if (_flushTimer is not null)
        {
            return;
        }

        lock (Gate)
        {
            _flushTimer ??= new System.Threading.Timer(_ => FlushPending(), null, 250, 250);
        }
    }

    private static void FlushPending()
    {
        try
        {
            lock (Gate)
            {
                while (PendingLines.TryDequeue(out var line))
                {
                    OpenTrace().WriteLine(line);
                }

                _trace?.Flush();
            }
        }
        catch
        {
            // A log flush must never cost the app its life.
        }
    }

    /// <summary>The open trace writer, rolling the file over once it is big enough. Call under <see cref="Gate"/>.</summary>
    private static StreamWriter OpenTrace()
    {
        if (_trace is { } open && open.BaseStream.Length < MaxTraceBytes)
        {
            return open;
        }

        _trace?.Dispose();
        Directory.CreateDirectory(Folder);

        if (File.Exists(TracePath) && new FileInfo(TracePath).Length >= MaxTraceBytes)
        {
            File.Move(TracePath, TracePath + ".1", overwrite: true);
        }

        // FileShare.ReadWrite so the log can be tailed while the app runs; AutoFlush so
        // the last line before a hard crash is on disk.
        var stream = new FileStream(TracePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _trace = new StreamWriter(stream) { AutoFlush = true };
        return _trace;
    }
}
