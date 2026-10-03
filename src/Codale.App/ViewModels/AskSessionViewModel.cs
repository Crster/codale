using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Codale.App.ViewModels;

public enum AskLogKind
{
    Info,
    Search,
    Done,
    Error,
}

/// <summary>One line of the Ask panel's progress stream.</summary>
public sealed record AskLogEntry(AskLogKind Kind, string Text, string Time);

/// <summary>
/// The editor's model job in flight (ask Claude, format, check issues) as the Ask panel
/// shows it: a log of what the agent is doing, how long it has been going, a way to stop
/// it, and how it ended. One per editor tab; a new job clears the previous log.
/// </summary>
public sealed partial class AskSessionViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private Timer? _clock;
    private DateTime _startedAt;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public ObservableCollection<AskLogEntry> Entries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = string.Empty;

    /// <summary>How the last job ended: "Inserted 3 lines". Null while running or before any job.</summary>
    [ObservableProperty]
    public partial string? Outcome { get; private set; }

    [ObservableProperty]
    public partial bool OutcomeIsError { get; private set; }

    /// <summary>Starts a job and hands back the token <see cref="CancelCommand"/> trips. Call on the UI thread.</summary>
    public CancellationToken Begin()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        Entries.Clear();
        Outcome = null;
        OutcomeIsError = false;
        _startedAt = DateTime.Now;
        ElapsedText = "0s";
        IsRunning = true;

        _clock?.Dispose();
        _clock = new Timer(_ => _ui?.Post(_ => TickElapsed(), null), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return _cts.Token;
    }

    /// <summary>Adds a line to the stream; safe to call from any thread.</summary>
    public void Log(AskLogKind kind, string text)
    {
        var entry = new AskLogEntry(kind, text, DateTime.Now.ToString("HH:mm:ss"));
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            Entries.Add(entry);
        }
        else
        {
            _ui.Post(_ => Entries.Add(entry), null);
        }
    }

    /// <summary>Ends the job with a closing log line. Call on the UI thread.</summary>
    public void End(string outcome, bool isError)
    {
        _clock?.Dispose();
        _clock = null;
        _cts?.Dispose();
        _cts = null;

        Entries.Add(new AskLogEntry(isError ? AskLogKind.Error : AskLogKind.Done, outcome, DateTime.Now.ToString("HH:mm:ss")));
        Outcome = outcome;
        OutcomeIsError = isError;
        IsRunning = false;
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void TickElapsed()
    {
        if (!IsRunning)
        {
            return;
        }

        var elapsed = DateTime.Now - _startedAt;
        ElapsedText = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s"
            : $"{elapsed.Seconds}s";
    }
}
