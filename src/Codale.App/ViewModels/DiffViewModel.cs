using Codale.Git;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Codale.App.ViewModels;

/// <summary>
/// The review surface for what a session changed.
/// </summary>
/// <remarks>
/// Backed by <c>git diff</c> rather than by the agent's event stream. The events say
/// what the agent believes it did; git says what is actually on disk, which is what the
/// user is reviewing - and it stays correct when a tool call is missed, when the user
/// edits a file by hand, or when a change is reverted.
/// </remarks>
public sealed partial class DiffViewModel : ObservableObject
{
    private readonly GitRepository _repository;

    public DiffViewModel(string projectPath) => _repository = new GitRepository(projectPath);

    public RangeObservableCollection<FileDiff> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    public partial int FileCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int AddedLines { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int RemovedLines { get; set; }

    [ObservableProperty]
    public partial FileDiff? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public bool HasChanges => FileCount > 0;

    public string Summary => FileCount == 0
        ? "No uncommitted changes"
        : $"{FileCount} file{(FileCount == 1 ? "" : "s")} · +{AddedLines} −{RemovedLines}";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;

        try
        {
            var diffs = await _repository.GetWorkingTreeDiffAsync();

            var previouslySelected = Selected?.Path;

            // A burst refresh usually re-reports the very same files; replacing the
            // collection then discards every realized row container for nothing.
            var unchanged = Files.Count == diffs.Count && Files.Zip(diffs, (oldRow, newRow) =>
                string.Equals(oldRow.Path, newRow.Path, StringComparison.OrdinalIgnoreCase) &&
                oldRow.Added == newRow.Added &&
                oldRow.Removed == newRow.Removed).All(same => same);

            if (!unchanged)
            {
                Files.ReplaceAll(diffs);
            }

            FileCount = diffs.Count;
            AddedLines = diffs.Sum(f => f.Added);
            RemovedLines = diffs.Sum(f => f.Removed);

            // Keep the user on the file they were reading across a refresh.
            Selected = Files.FirstOrDefault(f => string.Equals(f.Path, previouslySelected, StringComparison.OrdinalIgnoreCase)) ?? Files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            // git missing, or the read timed out: the panel shows nothing rather than stale rows.
            CrashLog.Debug("diff", $"working-tree diff failed: {ex.Message}");
            Files.ReplaceAll([]);
            FileCount = 0;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
