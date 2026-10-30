using System.ComponentModel;
using System.Text.RegularExpressions;

using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Shell;

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
    private IHelperModel? _assessmentModel;
    private string? _cwd;
    private IReadOnlyList<string> _extraRoots = [];
    private CancellationTokenSource? _assessmentCancellation;
    private string? _assessmentCommand;

    public TaskPeekPane()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
    }

    public event EventHandler? CloseRequested;

    public RunningTaskItem? Task => _task;

    public void Show(RunningTaskItem task, IHelperModel? model, string? cwd, IReadOnlyList<string> extraRoots)
    {
        Detach();
        _assessmentModel = model;
        _cwd = cwd;
        _extraRoots = extraRoots.ToArray();
        _task = task;
        task.IsPeeked = true;
        task.PropertyChanged += OnTaskChanged;

        KindIcon.Glyph = task.KindGlyph;
        TitleText.Text = task.DisplayTitle;

        ShowDetails(task);

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
        RequestAssessment();
    }

    /// <summary>Only an open peek requests a helper assessment; closing or switching cancels it.</summary>
    private void RequestAssessment()
    {
        if (_task is not { IsShell: true } task)
        {
            return;
        }

        var command = task.CommandOf();
        if (command == _assessmentCommand)
        {
            return;
        }

        CancelAssessment();
        if (command.Length == 0 || _assessmentModel is not { IsAvailable: true } model)
        {
            return;
        }

        _assessmentCommand = command;
        var cancellation = new CancellationTokenSource();
        _assessmentCancellation = cancellation;
        _ = RefineAssessmentAsync(model, task, command, _cwd, _extraRoots, cancellation);
    }

    private async System.Threading.Tasks.Task RefineAssessmentAsync(
        IHelperModel model, RunningTaskItem task, string command, string? cwd,
        IReadOnlyList<string> extraRoots, CancellationTokenSource cancellation)
    {
        try
        {
            var assessment = await ShellCommandAnalyzer.AssessAsync(
                model, command, cwd, task.Description, cancellation.Token, extraRoots).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_assessmentCancellation, cancellation) || !ReferenceEquals(_task, task))
                {
                    return;
                }

                if (assessment.Description.Length > 0)
                {
                    task.Description = assessment.Description;
                }
                task.Risk = assessment.Risk;
                task.RiskReason = assessment.Reason;
            });
        }
        catch (OperationCanceledException)
        {
            // The pane closed or moved to another command.
        }
        catch (Exception ex)
        {
            CrashLog.Debug("tasks", $"shell task assessment failed: {ex.Message}");
        }
    }

    private void CancelAssessment()
    {
        var cancellation = _assessmentCancellation;
        _assessmentCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        _assessmentCommand = null;
    }

    /// <summary>
    /// Fills the Run / Description / Assessment block above a shell's output: the command as it was
    /// issued, what the AI or agent said it does, and its safety rating.
    /// </summary>
    private void ShowDetails(RunningTaskItem task)
    {
        var command = task.CommandOf();
        DetailsPanel.Visibility = task.IsShell && command.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DetailsPanel.Visibility == Visibility.Collapsed)
        {
            return;
        }

        var baseline = ShellCommandAnalyzer.Assess(command, _cwd, hint: task.Description, extraRoots: _extraRoots);
        var description = task.Description.Length > 0 ? task.Description : baseline.Description;
        var risk = (ShellRisk)Math.Max((int)baseline.Risk, (int)task.Risk);
        var reason = task.RiskReason.Length > 0 ? task.RiskReason : baseline.Reason;

        TitleText.Text = description.Length > 0 ? description : task.DisplayTitle;
        RunText.Text = command;
        DescriptionText.Text = description;
        AssessmentText.Text = reason.Length > 0 ? $"{risk} - {reason}" : risk.ToString();
        AssessmentText.Foreground = (Brush)Application.Current.Resources[risk switch
        {
            ShellRisk.Danger => "SystemFillColorCriticalBrush",
            ShellRisk.Caution => "SystemFillColorCautionBrush",
            _ => "SystemFillColorSuccessBrush",
        }];
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
        CancelAssessment();
        _assessmentModel = null;
        _cwd = null;
        _extraRoots = [];
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
                case nameof(RunningTaskItem.Header):
                    if (_task is not null)
                    {
                        ShowDetails(_task);
                        RequestAssessment();
                    }

                    Refresh(stickToEnd: false);
                    break;
                case nameof(RunningTaskItem.Output):
                    Refresh(stickToEnd: false);
                    break;
                case nameof(RunningTaskItem.Description):
                case nameof(RunningTaskItem.Risk):
                case nameof(RunningTaskItem.RiskReason):
                case nameof(RunningTaskItem.DisplayTitle):
                    if (_task is not null)
                    {
                        TitleText.Text = _task.DisplayTitle;
                        ShowDetails(_task);
                    }

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

        // A shell's command is in the details block above; the log is just its output.
        foreach (var line in task.IsShell ? [] : task.Header.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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

        if (task.Header.Length > 0 && !task.IsShell)
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
