using System.Collections.ObjectModel;
using System.Text;

using Codale.Core.Agents;
using Codale.Core.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

public enum RunningTaskKind
{
    Shell,
    Subagent,
}

public enum RunningTaskStatus
{
    Running,
    Completed,
    Failed,
    Ended,
}

/// <summary>
/// One long-running thing the agent started: a background shell (dev server, watcher)
/// or a subagent. Listed in the session panel; selecting it opens the peek pane.
/// </summary>
public sealed class TaskCall
{
    public string ToolUseId { get; init; } = "";

    public ToolKind Kind { get; init; }

    public string Line { get; init; } = "";

    public string Result { get; set; } = "";

    public bool Failed { get; set; }
}

/// <summary>One block of a subagent's transcript: a piece of its prose, or a run of tool calls.</summary>
public sealed class TaskEntry : ObservableObject
{
    private string _summary = "";

    private TaskEntry()
    {
    }

    public static TaskEntry ForText(string text) => new() { Text = text, IsText = true };

    public static TaskEntry ForTools() => new();

    public bool IsText { get; private init; }

    public string Text { get; private init; } = "";

    public ObservableCollection<TaskCall> Calls { get; } = [];

    /// <summary>"Ran 5 commands, searched code, read 7 files" - the run in one line.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public void Refresh()
    {
        var parts = new List<string>();

        void Add(Func<ToolKind, bool> match, Func<int, string> phrase)
        {
            var count = Calls.Count(c => match(c.Kind));
            if (count > 0)
            {
                parts.Add(phrase(count));
            }
        }

        static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

        Add(k => k == ToolKind.Shell, n => "ran " + Plural(n, "command", "commands"));
        Add(k => k == ToolKind.Search, _ => "searched code");
        Add(k => k == ToolKind.Read, n => "read " + Plural(n, "file", "files"));
        Add(k => k == ToolKind.Web, n => "fetched " + Plural(n, "page", "pages"));
        Add(k => k == ToolKind.Edit, n => "edited " + Plural(n, "file", "files"));
        Add(k => k == ToolKind.Write, n => "wrote " + Plural(n, "file", "files"));
        Add(k => k is not (ToolKind.Shell or ToolKind.Search or ToolKind.Read or ToolKind.Web or ToolKind.Edit or ToolKind.Write),
            n => "used " + Plural(n, "tool", "tools"));

        var text = parts.Count == 0 ? "" : char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..];
        var failed = Calls.Count(c => c.Failed);
        Summary = failed > 0 ? $"{text} ({failed} failed)" : text;

        // A result landing changes a row without changing the summary line.
        OnPropertyChanged(nameof(Calls));
    }
}

public sealed partial class RunningTaskItem : ObservableObject
{
    private const int MaxOutputChars = 200_000;

    public string ToolUseId { get; init; } = "";

    public RunningTaskKind Kind { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Command line or subagent description.</summary>
    public string Title { get; init; } = "";

    /// <summary>The CLI's id for a background shell; later BashOutput/KillShell calls use it.</summary>
    [ObservableProperty]
    public partial string? ShellId { get; set; }

    /// <summary>File the CLI streams a shell's output into; tailed while peeking.</summary>
    [ObservableProperty]
    public partial string? OutputPath { get; set; }

    /// <summary>The provider runtime's own id for this task; set only where it tracks tasks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    public partial string? RuntimeId { get; set; }

    public int? Pid { get; set; }

    /// <summary>The tool call only launched it; the task runs on until the CLI says it ended.</summary>
    public bool LaunchedInBackground { get; set; }

    /// <summary>The process Codale itself runs for this task (codale-tasks); null for the CLI's own tasks.</summary>
    public HostedTask? Hosted { get; init; }

    /// <summary>How much of <see cref="Hosted"/>'s output has been copied into <see cref="Output"/>.</summary>
    public long HostOffset { get; set; }

    /// <summary>True while the peek pane shows this task: the poller then refreshes its output faster.</summary>
    public bool IsPeeked { get; set; }

    /// <summary>Replaces the log with a runtime snapshot (the runtime keeps only a tail).</summary>
    public void SetSnapshot(string text)
    {
        var clean = BackgroundTaskDetector.StripAnsi(text).Replace("\r\n", "\n").Replace("\r", "\n");
        if (clean != Output)
        {
            Output = clean;
        }
    }

    public string? LastRuntimeText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    public partial bool IsStopping { get; set; }

    /// <summary>Only runtime-tracked, still-running tasks can be stopped from here.</summary>
    public bool CanStop => IsRunning && (RuntimeId is not null || Hosted is not null || Cancel is not null) && !IsStopping;

    /// <summary>Ends work Codale runs in-process (an explore); null for tasks stopped another way.</summary>
    public Action? Cancel
    {
        get => _cancel;
        set
        {
            _cancel = value;
            OnPropertyChanged(nameof(CanStop));
        }
    }

    private Action? _cancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    public partial RunningTaskStatus Status { get; set; } = RunningTaskStatus.Running;

    /// <summary>What was asked - "$ command" or a subagent's prompt - shown above the output. Kept apart so a tailed log or snapshot replacing the output never loses it.</summary>
    [ObservableProperty]
    public partial string Header { get; set; } = "";

    // Appends land in a builder and the string is made only when something reads Output:
    // a chatty server's 4 appends a second no longer copy up to 200 KB each while no pane shows it.
    private readonly StringBuilder _output = new();
    private string? _outputText = "";

    /// <summary>The task's log text, newest <see cref="MaxOutputChars"/> at most.</summary>
    public string Output
    {
        get
        {
            if (_outputText is null)
            {
                if (_output.Length > MaxOutputChars)
                {
                    _output.Remove(0, _output.Length - MaxOutputChars);
                }

                _outputText = _output.ToString();
            }

            return _outputText;
        }
        set
        {
            value ??= "";
            if (_outputText == value)
            {
                return;
            }

            _output.Clear().Append(value);
            _outputText = value;
            TrackLastLine(value);
            OnPropertyChanged();
        }
    }

    /// <summary>The newest non-blank output line: the live message the status bar streams.</summary>
    [ObservableProperty]
    public partial string LastLine { get; set; } = "";

    private void TrackLastLine(string text)
    {
        for (var end = text.Length; end > 0;)
        {
            var start = text.LastIndexOf('\n', end - 1) + 1;
            var line = text.AsSpan(start, end - start).Trim();
            if (line.Length > 0)
            {
                LastLine = line.TrimStart("> ").ToString();
                return;
            }

            end = start - 1;
        }
    }

    /// <summary>The header and output as one log.</summary>
    public string FullLog => Header.Length == 0 ? Output : Output.Length == 0 ? Header : Header + Output;

    /// <summary>What a subagent was asked, shown as the first card of its transcript.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>A subagent's transcript: its own words and its tool calls folded into summary rows.</summary>
    public ObservableCollection<TaskEntry> Entries { get; } = [];

    public void AddText(string? text)
    {
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean) || (Entries.LastOrDefault() is { IsText: true } last && last.Text == clean))
        {
            return;
        }

        Entries.Add(TaskEntry.ForText(clean));
    }

    public void AddCall(string toolUseId, ToolKind kind, string line)
    {
        if (Entries.LastOrDefault() is not { IsText: false } run)
        {
            run = TaskEntry.ForTools();
            Entries.Add(run);
        }

        run.Calls.Add(new TaskCall { ToolUseId = toolUseId, Kind = kind, Line = line.Trim() });
        run.Refresh();
    }

    public void CompleteCall(string toolUseId, string? result, bool failed)
    {
        foreach (var entry in Entries.Reverse())
        {
            if (entry.Calls.FirstOrDefault(c => c.ToolUseId == toolUseId) is { } call)
            {
                call.Result = result?.Trim() ?? "";
                call.Failed = failed;
                entry.Refresh();
                return;
            }
        }
    }

    public static string CommandHeader(string command) => $"$ {command.Trim()}\n";

    public static string PromptHeader(string prompt) => $"[prompt]\n{prompt.Trim()}\n";

    public bool IsShell => Kind == RunningTaskKind.Shell;

    public bool IsRunning => Status == RunningTaskStatus.Running;

    public string KindGlyph => IsShell ? "" : "";

    public string StatusLabel => Status switch
    {
        RunningTaskStatus.Running => "running",
        RunningTaskStatus.Completed => "done",
        RunningTaskStatus.Failed => "failed",
        _ => "ended",
    };

    /// <summary>Appends text, keeping only the newest <see cref="MaxOutputChars"/>.</summary>
    public void AppendOutput(string text)
    {
        var clean = BackgroundTaskDetector.StripAnsi(text).Replace("\r\n", "\n").Replace("\r", "\n");
        _output.Append(clean);
        TrackLastLine(clean);

        // Trimmed in bulk at twice the cap (and to the cap when read), not on every append.
        if (_output.Length > MaxOutputChars * 2)
        {
            _output.Remove(0, _output.Length - MaxOutputChars);
        }

        _outputText = null;
        OnPropertyChanged(nameof(Output));
    }
}
