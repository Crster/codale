using Codale.Agents.History;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// One row of the history list - a feature, or a terminal CLI's conversation - plus what
/// the stored data cannot know: whether it is open in a chat tab right now, and whether
/// that tab is mid-turn.
/// </summary>
public sealed partial class SessionListItem : ObservableObject
{
    public required TranscriptSummary Summary { get; init; }

    public string SessionId => Summary.SessionId;

    public string Title => Summary.Title;

    public int UserTurns => Summary.UserTurns;

    public string TurnsText => Summary.Detail ?? (UserTurns == 1 ? "1 turn" : $"{UserTurns} turns");

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

    public void ApplyOpenState(IReadOnlyDictionary<string, bool> open)
    {
        IsOpen = open.TryGetValue(SessionId, out var working);
        IsWorking = IsOpen && working;
    }
}