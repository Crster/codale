using Codale.Git;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// The centre-panel diff tab: one file's uncommitted diff, or every file a commit
/// changed, with a file picker when there is more than one.
/// </summary>
/// <remarks>
/// One tab serves every diff request - each click replaces its contents - so reviewing
/// a working tree of thirty agent-touched files does not bury the tab strip.
/// </remarks>
public sealed partial class DiffViewerViewModel : ObservableObject
{
    private readonly GitRepository _repository;

    public DiffViewerViewModel(string projectPath) => _repository = new GitRepository(projectPath);

    public RangeObservableCollection<FileDiff> Files { get; } = [];

    [ObservableProperty]
    public partial FileDiff? Selected { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "Diff";

    /// <summary>Where the diff came from: the path, or the commit's sha and author.</summary>
    [ObservableProperty]
    public partial string? Subtitle { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>True when the left file has a copy on disk the editor can open.</summary>
    [ObservableProperty]
    public partial bool CanOpenFile { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Summarises the shown selection: "+12 −3", or "binary".</summary>
    public string Stat => Selected?.Stat ?? "";

    /// <summary>The commit behind the diff; null for a file or session diff.</summary>
    [ObservableProperty]
    public partial GitCommit? Commit { get; set; }

    public bool IsCommit => Commit is not null;

    public bool IsNotCommit => Commit is null;

    public string CommitDate => Commit is { } c ? c.Date.ToString("yyyy-MM-dd HH:mm") : "";

    /// <summary>Every file together: "23 files", with the line totals beside it.</summary>
    public string FilesSummary => Files.Count == 1 ? "1 file" : $"{Files.Count} files";

    public string TotalAddedText => $"+{Files.Sum(f => f.Added)}";

    public string TotalRemovedText => $"−{Files.Sum(f => f.Removed)}";

    partial void OnCommitChanged(GitCommit? value)
    {
        OnPropertyChanged(nameof(IsCommit));
        OnPropertyChanged(nameof(IsNotCommit));
        OnPropertyChanged(nameof(CommitDate));
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(Stat));
        OnPropertyChanged(nameof(FilesSummary));
        OnPropertyChanged(nameof(TotalAddedText));
        OnPropertyChanged(nameof(TotalRemovedText));
    }

    /// <summary>Loads one file's uncommitted diff - everything not yet committed for it.</summary>
    public async Task LoadFileAsync(string path, bool existsOnDisk)
    {
        Commit = null;
        Title = Path.GetFileName(path);
        Subtitle = path;
        CanOpenFile = existsOnDisk;

        await LoadAsync(async () => await _repository.GetFileDiffAsync(path)).ConfigureAwait(true);
    }

    /// <summary>Loads every file a commit changed, first one selected.</summary>
    public async Task LoadCommitAsync(GitCommit commit)
    {
        Commit = commit;
        Title = commit.Subject;
        Subtitle = $"{commit.ShortSha} · {commit.Author} · {commit.Date:yyyy-MM-dd HH:mm}";
        CanOpenFile = false;

        await LoadAsync(async () => await _repository.GetCommitDiffAsync(commit.Sha)).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows diffs that did not come from git - one agent edit, or a file's whole
    /// session of changes - in the same tab the git views use.
    /// </summary>
    public void LoadDiffs(string title, string? subtitle, IReadOnlyList<FileDiff> diffs, bool canOpenFile)
    {
        Commit = null;
        Title = title;
        Subtitle = subtitle;
        CanOpenFile = canOpenFile;
        Error = null;
        IsLoading = false;

        Files.ReplaceAll(diffs);
        Selected = Files.FirstOrDefault();
        RaiseTotals();
    }

    private async Task LoadAsync(Func<Task<IReadOnlyList<FileDiff>>> load)
    {
        IsLoading = true;
        Error = null;

        try
        {
            var diffs = await load().ConfigureAwait(true);

            Files.ReplaceAll(diffs);
            Selected = Files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Files.ReplaceAll([]);
            Selected = null;
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            RaiseTotals();
        }
    }

    partial void OnSelectedChanged(FileDiff? value) => OnPropertyChanged(nameof(Stat));
}
