using Codale.Agents.Claude;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// One past conversation in the session panel's history, plus what the transcript on
/// disk cannot know: whether it is open in a chat tab right now, and whether that tab
/// is mid-turn.
/// </summary>
public sealed partial class SessionListItem : ObservableObject
{
    public required TranscriptSummary Summary { get; init; }

    public string SessionId => Summary.SessionId;

    /// <summary>Ran in its own git worktree: the title carries a lock.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsIsolated { get; set; }

    public string Title => IsIsolated ? "\U0001F512 " + Summary.Title : Summary.Title;

    public int UserTurns => Summary.UserTurns;

    public string TurnsText => UserTurns == 1 ? "1 turn" : $"{UserTurns} turns";

    public DateTimeOffset UpdatedAt => Summary.UpdatedAt;

    /// <summary>Open in a chat tab: clicking it switches to that tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenIdle))]
    public partial bool IsOpen { get; set; }

    /// <summary>Its tab is running a turn right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenIdle))]
    public partial bool IsWorking { get; set; }

    public bool IsOpenIdle => IsOpen && !IsWorking;

    public void ApplyOpenState(
        IReadOnlyDictionary<string, bool> open, IReadOnlySet<string> isolated, IReadOnlyDictionary<string, string> worktrees)
    {
        IsOpen = open.TryGetValue(SessionId, out var working);
        IsWorking = IsOpen && working;
        IsIsolated = isolated.Contains(SessionId) || worktrees.ContainsKey(SessionId);
    }
}
