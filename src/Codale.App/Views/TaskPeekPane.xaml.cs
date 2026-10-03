using System.ComponentModel;

using Codale.App.ViewModels;
using Codale.Core.Agents;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        TitleText.Text = task.Title;

        LogScroll.Visibility = task.IsShell ? Visibility.Visible : Visibility.Collapsed;
        TranscriptScroll.Visibility = task.IsShell ? Visibility.Collapsed : Visibility.Visible;
        if (!task.IsShell)
        {
            Transcript.Attach(task);
            Transcript.Changed += OnTranscriptChanged;
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
        var output = _task.FullLog;
        LogText.Text = output.Length > MaxDisplayChars ? _task.Header + "…" + output[^MaxDisplayChars..] : output;
        UpdateStatus();

        if (follow)
        {
            LogScroll.UpdateLayout();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        }
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
        StatusText.Text = _task.StatusLabel + waiting;
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
