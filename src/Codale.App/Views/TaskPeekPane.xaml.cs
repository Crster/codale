using System.ComponentModel;
using System.Text.RegularExpressions;

using Codale.App.ViewModels;
using Codale.Core.Agents;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Views;

/// <summary>
/// Read-only live view of a <see cref="RunningTaskItem"/>. A background shell's log
/// is tailed from the file the CLI writes it to; a subagent's steps arrive through
/// the task itself. The log follows the end unless the user scrolls up.
/// </summary>
public sealed partial class TaskPeekPane : UserControl
{
    // A TextBlock this long is already slow to lay out; older lines are in the file.
    private const int MaxDisplayChars = 40_000;

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private RunningTaskItem? _task;
    private FileTailer? _tailer;

    public TaskPeekPane()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
    }

    public event EventHandler? CloseRequested;

    public RunningTaskItem? Task => _task;

    public void Show(RunningTaskItem task)
    {
        Detach();
        _task = task;
        task.IsPeeked = true;
        task.PropertyChanged += OnTaskChanged;

        KindIcon.Glyph = task.KindGlyph;
        TitleText.Text = task.DisplayTitle;

        LogScroll.Visibility = task.IsShell ? Visibility.Visible : Visibility.Collapsed;
        TranscriptScroll.Visibility = task.IsShell ? Visibility.Collapsed : Visibility.Visible;
        if (!task.IsShell)
        {
            Transcript.Attach(task);
            Transcript.Changed += OnTranscriptChanged;
        }

        if (task.Hosted is { } hosted)
        {
            // Rebuild from the process's own buffer so the pane never depends on the 250 ms copy having run.
            var (text, next) = hosted.Read(0);
            task.Output = "";
            task.AppendOutput(text);
            task.HostOffset = next;
        }

        Refresh(stickToEnd: true);
        StartTailing();
    }

    private void OnTranscriptChanged(object? sender, EventArgs e) => FollowTranscript();

    private void FollowTranscript()
    {
        if (TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 24)
        {
            _dispatcher.TryEnqueue(() =>
            {
                TranscriptScroll.UpdateLayout();
                TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null, disableAnimation: true);
            });
        }
    }

    public void Detach()
    {
        if (_task is not null)
        {
            _task.IsPeeked = false;
            _task.PropertyChanged -= OnTaskChanged;
        }

        Transcript.Changed -= OnTranscriptChanged;
        Transcript.Detach();
        _tailer?.Dispose();
        _tailer = null;
        _task = null;
    }

    private void StartTailing()
    {
        if (_task is not { IsShell: true, OutputPath: { Length: > 0 } path } task || _tailer is not null)
        {
            return;
        }

        // The file is the whole log, so start from a clean buffer instead of doubling it.
        task.Output = "";
        var tailer = new FileTailer(path);
        tailer.Chunk += text => _dispatcher.TryEnqueue(() =>
        {
            if (ReferenceEquals(_tailer, tailer))
            {
                task.AppendOutput(text);
            }
        });
        _tailer = tailer;
        tailer.Start();
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(RunningTaskItem.OutputPath):
                    StartTailing();
                    break;
                case nameof(RunningTaskItem.Output):
                case nameof(RunningTaskItem.Header):
                    Refresh(stickToEnd: false);
                    break;
                case nameof(RunningTaskItem.Status):
                case nameof(RunningTaskItem.CanStop):
                    UpdateStatus();
                    break;
            }
        });
    }

    private void Refresh(bool stickToEnd)
    {
        if (_task is null)
        {
            return;
        }

        if (!_task.IsShell)
        {
            UpdateStatus();
            return;
        }

        var follow = stickToEnd || LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 24;
        RenderLog(_task);
        UpdateStatus();

        if (follow)
        {
            LogScroll.UpdateLayout();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private static readonly Brush PromptBrush = Rgb(0x16, 0xC6, 0x0C);
    private static readonly Brush CommandBrush = Rgb(0xF2, 0xF2, 0xF2);
    private static readonly Brush ErrorBrush = Rgb(0xF1, 0x4C, 0x4C);
    private static readonly Brush WarnBrush = Rgb(0xF9, 0xF1, 0xA5);
    private static readonly Brush OkBrush = Rgb(0x13, 0xA1, 0x0E);
    private static readonly Brush InfoBrush = Rgb(0x3A, 0x96, 0xDD);
    private static readonly Brush DimBrush = Rgb(0x76, 0x76, 0x76);
    private static readonly Brush PlainBrush = Rgb(0xCC, 0xCC, 0xCC);

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));

    [GeneratedRegex(@"^(\s*\d+[:\-])(.*)$")]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"\b0 (error|warning)\(?s?\)?|\b(no|0) (errors?|warnings?|failed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroCount();

    [GeneratedRegex(@"\b(error|fatal|exception|failed|failure|panic|denied|cannot|unhandled)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorWord();

    [GeneratedRegex(@"\b(warn|warning|deprecated)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarnWord();

    [GeneratedRegex(@"\b(succeeded|success|passed|ready|listening|compiled|done|started|running on)\b|https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex OkWord();

    /// <summary>
    /// The log as a terminal would show it: the command that was run on top in bold,
    /// then the output with errors, warnings, success lines and line numbers coloured.
    /// </summary>
    private void RenderLog(RunningTaskItem task)
    {
        var inlines = LogText.Inlines;
        inlines.Clear();

        foreach (var line in task.Header.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("$ ", StringComparison.Ordinal))
            {
                Add(inlines, "$ ", PromptBrush, bold: true);
                Add(inlines, line[2..] + "\n", CommandBrush, bold: true);
            }
            else if (line == "[prompt]")
            {
                Add(inlines, line + "\n", InfoBrush, bold: true);
            }
            else
            {
                Add(inlines, line + "\n", PlainBrush);
            }
        }

        if (task.Header.Length > 0)
        {
            Add(inlines, new string('─', 24) + "\n", DimBrush);
        }

        var output = task.Output;
        if (output.Length > MaxDisplayChars)
        {
            Add(inlines, "… earlier output omitted\n", DimBrush);
            output = output[^MaxDisplayChars..];
            output = output[(output.IndexOf('\n') + 1)..];
        }

        if (output.Length == 0)
        {
            Add(inlines, task.IsRunning ? "(no output yet)" : "(no output)", DimBrush);
            return;
        }

        foreach (var line in output.Split('\n'))
        {
            var numbered = NumberedLine().Match(line);
            var body = numbered.Success ? numbered.Groups[2].Value : line;
            if (numbered.Success)
            {
                Add(inlines, numbered.Groups[1].Value, DimBrush);
            }

            Add(inlines, body + "\n", Classify(body));
        }
    }

    private static Brush Classify(string line)
    {
        if (ZeroCount().IsMatch(line))
        {
            return OkBrush;
        }

        if (ErrorWord().IsMatch(line))
        {
            return ErrorBrush;
        }

        if (WarnWord().IsMatch(line))
        {
            return WarnBrush;
        }

        if (OkWord().IsMatch(line))
        {
            return OkBrush;
        }

        return PlainBrush;
    }

    private static void Add(InlineCollection inlines, string text, Brush brush, bool bold = false)
    {
        var run = new Run { Text = text, Foreground = brush };
        if (bold)
        {
            run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }

        inlines.Add(run);
    }

    private void UpdateStatus()
    {
        if (_task is null)
        {
            return;
        }

        var waiting = _task.IsShell && _task.Output.Length == 0 && _task.IsRunning
            ? _task.OutputPath is null && _task.Hosted is null && _task.RuntimeId is null ? " · no output file reported" : " · waiting for output"
            : "";
        var termination = !_task.IsShell ? ""
            : _task.IsStopping ? " · stopping…"
            : _task.CanStop ? " · still running, Stop to terminate"
            : _task.IsRunning ? _task.Pid is { } pid ? $" · pid {pid}, can't be stopped from here" : " · can't be stopped from here"
            : " · nothing left to terminate";
        StatusText.Text = _task.StatusLabel + termination + waiting;
        RunningDot.Visibility = _task.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = _task.CanStop ? Visibility.Visible : Visibility.Collapsed;
        Pulse.SetIsActive(RunningDot, _task.IsRunning);
    }

    public event EventHandler<RunningTaskItem>? StopRequested;

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_task is not null)
        {
            StopRequested?.Invoke(this, _task);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}