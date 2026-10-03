using System.Diagnostics;
using System.Text;

using Codale.Core.Processes;

namespace Codale.Core.Tasks;

public enum HostedTaskState
{
    Running,
    Exited,
    Stopped,
}

/// <summary>
/// One long-running command that Codale itself started and owns. Because the app holds
/// the process, it can show every line as it arrives and kill the whole tree on demand -
/// neither of which is possible for a shell the agent CLI spawned.
/// </summary>
public sealed class HostedTask
{
    private const int MaxChars = 200_000;

    /// <summary>The buffer may overshoot the cap by this much before it is cut back, so a chatty task does not shift 200k chars per line.</summary>
    private const int TrimSlack = MaxChars / 10;

    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private long _dropped;
    private Process? _process;
    private ProcessJob? _job;

    internal HostedTask(string id, string name, string command)
    {
        Id = id;
        Name = name;
        Command = command;
    }

    public string Id { get; }

    public string Name { get; }

    public string Command { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public HostedTaskState State { get; private set; } = HostedTaskState.Running;

    public int? ExitCode { get; private set; }

    public int? Pid { get; private set; }

    /// <summary>When the task ended, or null while it runs; the manager drops the oldest finished tasks by this.</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Raised on a background thread with each new chunk of output.</summary>
    public event Action<HostedTask, string>? OutputReceived;

    /// <summary>Raised once, on a background thread, when the process ends by itself or is stopped.</summary>
    public event Action<HostedTask>? Finished;

    /// <summary>Everything still held, plus the absolute offset just past it.</summary>
    public (string Text, long NextOffset) Read(long? since = null, int? tailChars = null)
    {
        lock (_gate)
        {
            var end = _dropped + _buffer.Length;

            // A caller's offset can be stale (past a restarted buffer) or ahead of the end: clamp into what is held.
            var from = Math.Min(Math.Max(since ?? _dropped, _dropped), end);

            // A tail request wants the last N chars; clamp the window before materialising
            // so a full 200k-char buffer is not copied just to throw most of it away.
            if (tailChars is > 0 && end - from > tailChars)
            {
                from = end - tailChars.Value;
            }

            var text = _buffer.ToString((int)(from - _dropped), (int)(end - from));

            if (tailChars is > 0 && text.Length > tailChars)
            {
                text = text[^tailChars.Value..];
            }

            return (text, end);
        }
    }

    internal void Attach(Process process)
    {
        _process = process;
        Pid = process.Id;

        // Right after Start: the shell has not yet had time to spawn what it will run.
        var job = ProcessJob.TryCreate(process);

        bool ended;
        lock (_gate)
        {
            // A process that exited before this point has already been through Complete/Stop,
            // which found no job to dispose; nothing else would ever release this one.
            ended = State != HostedTaskState.Running;
            if (!ended)
            {
                _job = job;
            }
        }

        if (ended)
        {
            job?.Dispose();
        }
    }

    /// <summary>Releases the process handle once nothing will touch it again.</summary>
    internal void ReleaseProcess()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        process?.Dispose();
    }

    internal void Append(string line)
    {
        lock (_gate)
        {
            _buffer.Append(line).Append('\n');
            if (_buffer.Length > MaxChars + TrimSlack)
            {
                var cut = _buffer.Length - MaxChars;
                _buffer.Remove(0, cut);
                _dropped += cut;
            }
        }

        try
        {
            OutputReceived?.Invoke(this, line + "\n");
        }
        catch (Exception ex)
        {
            // This runs on the process's data thread: a throwing subscriber must not take the app down.
            Trace.WriteLine($"Hosted task output handler failed: {ex.Message}");
        }
    }

    private void RaiseFinished()
    {
        try
        {
            Finished?.Invoke(this);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Hosted task finished handler failed: {ex.Message}");
        }
    }

    internal void Complete(int? exitCode)
    {
        lock (_gate)
        {
            if (State != HostedTaskState.Running)
            {
                return;
            }

            ExitCode = exitCode;
            State = HostedTaskState.Exited;
            FinishedAt = DateTimeOffset.Now;
        }

        // The shell is done; anything it left running goes with it.
        _job?.Dispose();

        RaiseFinished();
    }

    /// <summary>Kills the process and everything it spawned. False if it had already finished.</summary>
    public bool Stop()
    {
        lock (_gate)
        {
            if (State != HostedTaskState.Running)
            {
                return false;
            }

            State = HostedTaskState.Stopped;
            FinishedAt = DateTimeOffset.Now;
        }

        // The job reaches processes orphaned by a dead wrapper (npm -> cmd -> node); the
        // tree walk is only the fallback where no job could be made.
        if (_job?.Terminate() != true)
        {
            ProcessUtil.TryKillTree(_process);
        }

        _job?.Dispose();
        RaiseFinished();
        return true;
    }
}

/// <summary>
/// Starts and tracks a session's hosted tasks. Disposing it ends every one still running,
/// so closing a chat never strands a dev server.
/// </summary>
public sealed class HostedTaskManager : IDisposable
{
    private readonly string _workingDirectory;
    private readonly Dictionary<string, HostedTask> _tasks = [];
    private readonly object _gate = new();
    private int _next;

    /// <summary>Finished tasks kept for reading back; each holds up to 200k chars of output.</summary>
    private const int MaxFinishedKept = 20;

    public HostedTaskManager(string workingDirectory) => _workingDirectory = workingDirectory;

    /// <summary>Raised on a background thread when a task starts.</summary>
    public event Action<HostedTask>? TaskStarted;

    public HostedTask Start(string command, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A command is required.", nameof(command));
        }

        var task = new HostedTask($"t{Interlocked.Increment(ref _next)}", string.IsNullOrWhiteSpace(name) ? command.Trim() : name.Trim(), command.Trim());

        var info = new ProcessStartInfo
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (OperatingSystem.IsWindows())
        {
            info.FileName = "powershell.exe";
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + task.Command);
        }
        else
        {
            info.FileName = "/bin/sh";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(task.Command);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        // A quick command can exit before Start returns and the readers are attached.
        var ready = new ManualResetEventSlim();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) task.Append(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) task.Append(e.Data); };
        process.Exited += (_, _) =>
        {
            ready.Wait(TimeSpan.FromSeconds(10));

            // WaitForExit() drains the async readers, so no trailing line is lost.
            process.WaitForExit();
            task.Complete(process.ExitCode);
            task.ReleaseProcess();
        };

        try
        {
            process.Start();
            process.StandardInput.Close(); // nothing will type into it; a prompt would hang, not wait
            task.Attach(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            process.Dispose();
            throw;
        }
        finally
        {
            ready.Set();
        }

        lock (_gate)
        {
            _tasks[task.Id] = task;
            PurgeFinished();
        }

        try
        {
            TaskStarted?.Invoke(task);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Hosted task started handler failed: {ex.Message}");
        }

        return task;
    }

    /// <summary>Drops all but the newest finished tasks; caller holds the gate.</summary>
    private void PurgeFinished()
    {
        var finished = _tasks.Values.Where(t => t.FinishedAt is not null).OrderByDescending(t => t.FinishedAt).ToList();
        foreach (var old in finished.Skip(MaxFinishedKept))
        {
            _tasks.Remove(old.Id);
        }
    }

    public HostedTask? Get(string id)
    {
        lock (_gate)
        {
            return _tasks.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<HostedTask> List()
    {
        lock (_gate)
        {
            return [.. _tasks.Values];
        }
    }

    public void Dispose()
    {
        foreach (var task in List())
        {
            task.Stop();
        }
    }
}
