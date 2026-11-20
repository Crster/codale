using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// The files one user prompt made the agent change. The session panel lists these
/// groups; opening one shows its files in the side preview.
/// </summary>
public sealed partial class PromptChangeGroup : ObservableObject
{
    public required string Title { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Files this prompt changed, most recent first; each measured from before this prompt touched it.</summary>
    public ObservableCollection<SessionFileChange> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial int Added { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial int Removed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial int FileCount { get; set; }

    /// <summary>True for a few seconds after the agent last wrote for this prompt.</summary>
    [ObservableProperty]
    public partial bool IsFresh { get; set; }

    public string TimeText => StartedAt.ToString("HH:mm");

    public string SummaryText => $"{FileCount} {(FileCount == 1 ? "file" : "files")} · +{Added} −{Removed}";

    public SessionFileChange Row(string path, out bool created)
    {
        var row = Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        created = row is null;

        if (row is null)
        {
            row = new SessionFileChange { Path = path };
            Files.Insert(0, row);
        }
        else if (Files.IndexOf(row) is > 0 and var at)
        {
            Files.Move(at, 0);
        }

        return row;
    }

    public void Recompute()
    {
        FileCount = Files.Count;
        Added = Files.Sum(f => f.Added);
        Removed = Files.Sum(f => f.Removed);
    }

    public void Settle()
    {
        foreach (var file in Files)
        {
            file.RefreshCountsIfDirty();
        }

        Recompute();
    }

    public void Freeze()
    {
        foreach (var file in Files)
        {
            file.Freeze();
        }

        Recompute();
    }
}
