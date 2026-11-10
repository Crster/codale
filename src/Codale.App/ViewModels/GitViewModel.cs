using System.Text;

using Codale.App.Services;
using Codale.Core.Helper;
using Codale.Core.Text;
using Codale.Git;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.UI.Dispatching;

namespace Codale.App.ViewModels;

/// <summary>
/// Branches, working-tree changes and the history graph for the left panel, plus the
/// everyday writes on top of them: staging, committing and switching branch.
/// </summary>
/// <remarks>
/// Refreshed on a timer and after every agent turn: the whole point of the panel is to
/// show what the agent just did to the working tree, and git is the authority on that
/// even when the event stream misses something. Write operations refresh immediately
/// rather than waiting for the next tick, and raise <see cref="WorkingTreeChanged"/> so
/// the rest of the workspace (the diff panel, the file tree colours) follows git's new
/// state.
/// </remarks>
public sealed partial class GitViewModel : ObservableObject, IDisposable
{
    private readonly string _projectPath;
    private readonly GitRepository _repository;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _timer;
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <summary>Fingerprint of the snapshot currently applied; null until one is.</summary>
    private int? _lastFingerprint;
    private CancellationTokenSource? _smartCommit;
    private long _messageRevision;
    partial void OnCommitMessageChanged(string value) => _messageRevision++;

    [ObservableProperty]
    public partial bool IsGeneratingCommitMessage { get; set; }

    [RelayCommand]
    private async Task GenerateCommitMessageAsync()
    {
        if (IsGeneratingCommitMessage || IsBusy) return;
        if (SmartExperienceHost.Instance.Status.State != Codale.Smart.SmartState.Ready || !AppSettings.SmartExperience)
        { ActionError = $"Enable Smart experience in Settings and wait for {SmartExperienceHost.Instance.Model.Name} to be ready."; return; }
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        _smartCommit = cancel;
        IsGeneratingCommitMessage = true;
        ActionError = null;
        var revision = _messageRevision;
        try
        {
            var snapshot = await _repository.GetStagedDiffAsync(cancel.Token);
            if (string.IsNullOrWhiteSpace(snapshot.Diff)) { ActionError = "Stage changes before generating a commit message."; return; }
            var result = await SmartExperienceHost.Instance.CommitMessageAsync(new(_projectPath, _projectPath, "commit"), cancel.Token);
            var message = result?.Value;
            if (string.IsNullOrWhiteSpace(message)) { ActionError = "Could not generate a commit message. Try again."; return; }
            var current = await _repository.GetStagedDiffAsync(cancel.Token);
            if (revision != _messageRevision || current.TreeId != snapshot.TreeId)
            { ActionError = "The message or staged changes changed during generation. Generate again."; return; }
            if (AppSettings.SmartExperience) CommitMessage = message;
        }
        catch (OperationCanceledException) { ActionError = "Commit-message generation timed out or was cancelled."; }
        catch (Exception ex) { ActionError = $"Commit-message generation failed: {ex.Message}"; }
        finally { _smartCommit = null; IsGeneratingCommitMessage = false; }
    }

    public GitViewModel(string projectPath)
    {
        _projectPath = projectPath;
        _repository = new GitRepository(projectPath);

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(AppSettings.GitPollSeconds);
        _timer.Tick += (_, _) => _ = RefreshAsyncSafe();
    }

    /// <summary>Working-tree changes not yet staged (untracked files included).</summary>
    public RangeObservableCollection<GitFileStatus> Changes { get; } = [];

    /// <summary>Entries with something in the index, ready for the next commit.</summary>
    public RangeObservableCollection<GitFileStatus> StagedChanges { get; } = [];

    public RangeObservableCollection<GitCommit> Log { get; } = [];

    public RangeObservableCollection<GitBranch> Branches { get; } = [];

    /// <summary>
    /// Raised on the UI thread after every refresh, carrying the fresh snapshot. The
    /// file tree listens to colour itself; the panel's own collections are updated
    /// before this fires.
    /// </summary>
    public event EventHandler<GitSnapshot>? SnapshotRefreshed;

    /// <summary>Snapshots of the repositories in the project's sub folders; empty when the project is itself one.</summary>
    public event EventHandler<IReadOnlyList<GitSnapshot>>? NestedSnapshotsRefreshed;

    /// <summary>
    /// Raised after a write (stage, commit, checkout...) actually changed the repository.
    /// The workspace refreshes the diff panel from it; the panel's own refresh happens
    /// before this fires.
    /// </summary>
    public event EventHandler? WorkingTreeChanged;

    [ObservableProperty]
    public partial bool IsRepository { get; set; }

    /// <summary>Absolute repository root; change paths are relative to it, so a diff tab needs it to open files.</summary>
    [ObservableProperty]
    public partial string? RepositoryRoot { get; set; }

    [ObservableProperty]
    public partial string BranchName { get; set; } = "";

    [ObservableProperty]
    public partial string? TrackingSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeSummary))]
    public partial int ChangeCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    [NotifyPropertyChangedFor(nameof(CreatedCount))]
    public partial int AddedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    public partial int ModifiedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    public partial int DeletedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummary))]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    [NotifyPropertyChangedFor(nameof(CreatedCount))]
    public partial int UntrackedCount { get; set; }

    /// <summary>New files, staged or not: the status bar's "created" counter.</summary>
    public int CreatedCount => AddedCount + UntrackedCount;

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The change under the list's selection; the toolbar and menus act on it.</summary>
    [ObservableProperty]
    public partial GitFileStatus? SelectedChange { get; set; }

    /// <summary>The commit the user is about to create.</summary>
    [ObservableProperty]
    public partial string CommitMessage { get; set; } = "";

    /// <summary>True while a write (stage, commit, checkout) is running; buttons wait.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }


    /// <summary>git's own explanation when a write failed, shown in the panel.</summary>
    [ObservableProperty]
    public partial string? ActionError { get; set; }

    /// <summary>How many paths are staged for the next commit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StagedSummary))]
    public partial int StagedCount { get; set; }

    public string ChangeSummary => ChangeCount switch
    {
        0 => "No changes",
        1 => "1 change",
        _ => $"{ChangeCount} changes",
    };

    public string StagedSummary => StagedCount switch
    {
        0 => "nothing staged",
        1 => "1 staged",
        _ => $"{StagedCount} staged",
    };

    /// <summary>Compact per-kind counts for the status bar, e.g. "1A 2M 1D".</summary>
    public string StatusSummary
    {
        get
        {
            if (ChangeCount == 0)
            {
                return "clean";
            }

            var parts = new List<string>(4);

            if (AddedCount > 0)
            {
                parts.Add($"{AddedCount}A");
            }

            if (ModifiedCount > 0)
            {
                parts.Add($"{ModifiedCount}M");
            }

            if (DeletedCount > 0)
            {
                parts.Add($"{DeletedCount}D");
            }

            if (UntrackedCount > 0)
            {
                parts.Add($"{UntrackedCount}?");
            }

            return string.Join(" ", parts);
        }
    }

    /// <summary>Spells out what the letters mean, for the status bar tooltip.</summary>
    public string StatusDetail
    {
        get
        {
            if (ChangeCount == 0)
            {
                return "No changes";
            }

            var parts = new List<string>(4);

            if (AddedCount > 0)
            {
                parts.Add($"{AddedCount} added");
            }

            if (ModifiedCount > 0)
            {
                parts.Add($"{ModifiedCount} modified");
            }

            if (DeletedCount > 0)
            {
                parts.Add($"{DeletedCount} deleted");
            }

            if (UntrackedCount > 0)
            {
                parts.Add($"{UntrackedCount} untracked");
            }

            return string.Join(" · ", parts);
        }
    }

    public void Start()
    {
        _ = RefreshAsyncSafe();
        _timer.Start();
    }

    /// <summary>Re-reads the refresh interval from Settings; the Settings window changed it.</summary>
    public void ApplyPollInterval() => _timer.Interval = TimeSpan.FromSeconds(AppSettings.GitPollSeconds);

    private async Task RefreshAsyncSafe()
    {
        try
        {
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // A git read that outlived its 30s timeout; the next tick tries again.
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    public Task RefreshAsync() => RefreshCoreAsync(waitForRunning: false);

    /// <summary>
    /// Overlapping refreshes would fight over the collections. A poll that finds one
    /// running skips - the next tick is a few seconds away - but a refresh that follows
    /// a write (<paramref name="waitForRunning"/>) must see the write's result, which a
    /// refresh already in flight may have read git before, so it waits its turn.
    /// </summary>
    private async Task<IReadOnlyList<GitSnapshot>> GetNestedSnapshotsAsync()
    {
        try
        {
            var folders = Directory.EnumerateDirectories(_projectPath)
                .Where(folder => Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git")))
                .ToList();

            var snapshots = await Task.WhenAll(folders.Select(folder => new GitRepository(folder).GetSnapshotAsync())).ConfigureAwait(true);
            return snapshots;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task RefreshCoreAsync(bool waitForRunning)
    {
        if (waitForRunning)
        {
            await _refreshing.WaitAsync().ConfigureAwait(true);
        }
        else if (!await _refreshing.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var snapshot = await _repository.GetSnapshotAsync().ConfigureAwait(true);

            // The 5-second poll lands here too; when git's raw output is byte-identical
            // to last time, re-running the whole apply (and the file-tree recolour hung
            // off SnapshotRefreshed) is work for nothing.
            if (snapshot.IsRepository && snapshot.Fingerprint == _lastFingerprint)
            {
                return;
            }

            _lastFingerprint = snapshot.IsRepository ? snapshot.Fingerprint : null;

            IsRepository = snapshot.IsRepository;
            Error = snapshot.Error;
            RepositoryRoot = snapshot.RepositoryRoot;

            if (!snapshot.IsRepository)
            {
                Changes.Clear();
                StagedChanges.Clear();
                Log.Clear();
                Branches.Clear();
                ChangeCount = 0;
                AddedCount = ModifiedCount = DeletedCount = UntrackedCount = 0;
                StagedCount = 0;
                BranchName = "";
                SelectedChange = null;
            }
            else
            {
                BranchName = snapshot.Branch.Display;
                TrackingSummary = snapshot.Branch.TrackingSummary;
                ChangeCount = snapshot.Changes.Count;
                (AddedCount, ModifiedCount, DeletedCount, UntrackedCount) = CountKinds(snapshot.Changes);
                StagedCount = snapshot.Changes.Count(c => c.IsStaged);

                var previouslySelected = SelectedChange?.Path;

                // A file edited after staging belongs to both lists, as in `git status`.
                Replace(StagedChanges, [.. snapshot.Changes.Where(c => c.IsStaged && !c.IsConflicted)]);
                Replace(Changes, [.. snapshot.Changes.Where(c => c.IsConflicted || c.WorkTree is not GitChangeKind.Unmodified)]);

                // Selection is reference-based, and every refresh builds new records;
                // put it back on the same path so the toolbar keeps its target.
                SelectedChange = previouslySelected is null
                    ? null
                    : Changes.FirstOrDefault(c => c.Path == previouslySelected)
                        ?? StagedChanges.FirstOrDefault(c => c.Path == previouslySelected);

                Replace(Log, snapshot.Log, CommitKey);
                Replace(Branches, snapshot.Branches, b => b.Name);
            }

            SnapshotRefreshed?.Invoke(this, snapshot);

            // A project folder that is not itself a repository may hold some one level down;
            // the panel stays on the root, but the file tree can still show their status.
            NestedSnapshotsRefreshed?.Invoke(this, snapshot.IsRepository ? [] : await GetNestedSnapshotsAsync().ConfigureAwait(true));
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Stages one change for the next commit.</summary>
    [RelayCommand]
    public async Task StageChangeAsync(GitFileStatus? change)
    {
        if (change is null)
        {
            return;
        }

        await RunWriteAsync(_repository.StageAsync(change.Path)).ConfigureAwait(true);
    }

    /// <summary>Stages everything, untracked files included.</summary>
    [RelayCommand]
    public async Task StageAllAsync()
    {
        await RunWriteAsync(_repository.StageAllAsync()).ConfigureAwait(true);
    }

    /// <summary>The stash stack, newest first; reloaded by <see cref="LoadStashesAsync"/>.</summary>
    public RangeObservableCollection<GitStash> Stashes { get; } = [];

    public async Task LoadStashesAsync()
    {
        Replace(Stashes, await _repository.GetStashesAsync().ConfigureAwait(true), s => s.Ref + s.Message);
    }

    /// <summary>Shelves all uncommitted changes, including untracked files.</summary>
    public async Task StashChangesAsync()
    {
        if (await RunWriteAsync(_repository.StashPushAsync(CommitMessage)).ConfigureAwait(true))
        {
            await LoadStashesAsync().ConfigureAwait(true);
        }
    }

    public async Task PopStashAsync(GitStash stash)
    {
        await RunWriteAsync(_repository.StashPopAsync(stash.Ref)).ConfigureAwait(true);
        await LoadStashesAsync().ConfigureAwait(true);
    }

    public async Task DropStashAsync(GitStash stash)
    {
        await RunWriteAsync(_repository.StashDropAsync(stash.Ref)).ConfigureAwait(true);
        await LoadStashesAsync().ConfigureAwait(true);
    }

    /// <summary>Takes one staged change back out of the index.</summary>
    [RelayCommand]
    public async Task UnstageChangeAsync(GitFileStatus? change)
    {
        if (change is null)
        {
            return;
        }

        await RunWriteAsync(_repository.UnstageAsync(change.Path)).ConfigureAwait(true);
    }

    /// <summary>
    /// Throws away one change. The caller confirms first: this deletes untracked files.
    /// </summary>
    public Task DiscardChangeAsync(GitFileStatus change) =>
        RunWriteAsync(_repository.DiscardAsync(change.Path));

    /// <summary>
    /// Adds one change's path to .gitignore so git stops listing it. A staged entry
    /// would keep showing up despite the rule, so it is taken back out of the index
    /// too, which leaves the working tree untouched.
    /// </summary>
    public Task IgnoreChangeAsync(GitFileStatus change) =>
        IgnoreChangePathAsync(change, change.Path);

    /// <summary>
    /// Ignores a whole folder (a relative path with a trailing slash) on behalf of one
    /// change, so every file under it drops out of the list at once.
    /// </summary>
    public Task IgnoreFolderAsync(GitFileStatus change, string folder) =>
        IgnoreChangePathAsync(change, folder);

    private async Task IgnoreChangePathAsync(GitFileStatus change, string path)
    {
        if (await RunWriteAsync(_repository.IgnoreAsync(path)).ConfigureAwait(true) &&
            change.IsStaged)
        {
            await RunWriteAsync(_repository.UnstageAsync(change.Path)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Commits the staged changes. With nothing staged - the common case after an agent
    /// turn, since staging is optional - everything is staged first, so one button does
    /// the whole "commit my work" flow.
    /// </summary>
    [RelayCommand]
    public async Task CommitAsync()
    {
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            ActionError = "Write a commit message first.";
            return;
        }

        IsBusy = true;
        ActionError = null;

        try
        {
            if (StagedCount == 0 && ChangeCount > 0)
            {
                var staged = await _repository.StageAllAsync().ConfigureAwait(true);
                if (!staged.Success)
                {
                    ActionError = staged.Error;
                    return;
                }
            }

            var result = await _repository.CommitAsync(CommitMessage).ConfigureAwait(true);
            if (!result.Success)
            {
                ActionError = result.Error;
                return;
            }

            CommitMessage = "";
            await RefreshCoreAsync(waitForRunning: true).ConfigureAwait(true);
            WorkingTreeChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>


    /// <summary>Switches the working tree to another branch.</summary>
    public async Task<bool> SwitchBranchAsync(GitBranch branch)
    {
        if (branch.IsCurrent)
        {
            return true;
        }

        return await RunWriteAsync(_repository.CheckoutAsync(branch.Name)).ConfigureAwait(true);
    }

    /// <summary>Creates a branch at HEAD and switches to it. Returns false on failure.</summary>
    public async Task<bool> CreateBranchAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            ActionError = "Give the branch a name.";
            return false;
        }

        return await RunWriteAsync(_repository.CreateBranchAsync(name)).ConfigureAwait(true);
    }

    public Task<bool> CreateBranchAtAsync(string name, GitCommit commit) =>
        string.IsNullOrWhiteSpace(name)
            ? Task.FromResult(false)
            : RunWriteAsync(_repository.CreateBranchAtAsync(name.Trim(), commit.Sha));

    public Task<bool> CheckoutCommitAsync(GitCommit commit) =>
        RunWriteAsync(_repository.CheckoutCommitAsync(commit.Sha));

    public Task<bool> CherryPickAsync(GitCommit commit) =>
        RunWriteAsync(_repository.CherryPickAsync(commit.Sha));

    public Task<bool> RevertCommitAsync(GitCommit commit) =>
        RunWriteAsync(_repository.RevertAsync(commit.Sha));

    /// <summary>Resets the current branch to a commit; <paramref name="mode"/> is soft, mixed or hard.</summary>
    public Task<bool> ResetToAsync(GitCommit commit, string mode) =>
        RunWriteAsync(_repository.ResetAsync(commit.Sha, mode));

    /// <summary>Runs <c>git init</c> in the project folder when it is not a repository yet.</summary>
    [RelayCommand]
    public async Task InitRepositoryAsync()
    {
        try
        {
            await RunWriteAsync(_repository.InitAsync()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Process.Start throws when git itself is missing from PATH.
            ActionError = ex.Message;
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task PushAsync()
    {
        var current = Branches.FirstOrDefault(b => b.IsCurrent);
        if (current is null)
        {
            return;
        }

        await RunWriteAsync(_repository.PushAsync(current.Name, current.Upstream is not null)).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task PullAsync()
    {
        await RunWriteAsync(_repository.PullAsync()).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs one write, then brings the panel and everything listening to it up to date.
    /// Returns whether git accepted the operation.
    /// </summary>
    private async Task<bool> RunWriteAsync(Task<GitRepository.GitWriteResult> operation)
    {
        IsBusy = true;
        ActionError = null;

        try
        {
            var result = await operation.ConfigureAwait(true);

            if (!result.Success)
            {
                ActionError = result.Error;
                return false;
            }

            await RefreshCoreAsync(waitForRunning: true).ConfigureAwait(true);
            WorkingTreeChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Sorts a status listing into the four buckets the status bar shows. A file lands
    /// in the first bucket its staged or unstaged state matches, so a rename with
    /// further edits reads as modified, and untracked files stay their own "?" group
    /// because "added" would imply git is already tracking them.
    /// </summary>
    private static (int Added, int Modified, int Deleted, int Untracked) CountKinds(
        IReadOnlyList<GitFileStatus> changes)
    {
        var added = 0;
        var modified = 0;
        var deleted = 0;
        var untracked = 0;

        foreach (var change in changes)
        {
            if (change.WorkTree == GitChangeKind.Untracked)
            {
                untracked++;
            }
            else if (change.Index == GitChangeKind.Deleted || change.WorkTree == GitChangeKind.Deleted)
            {
                deleted++;
            }
            else if (change.Index == GitChangeKind.Added || change.WorkTree == GitChangeKind.Added)
            {
                added++;
            }
            else
            {
                modified++;
            }
        }

        return (added, modified, deleted, untracked);
    }

    /// <summary>
    /// Identity of a graph row across refreshes: the commit plus where it sits now. A
    /// value-equality pass would rewrite the list every tick regardless, because each
    /// refresh re-parses the parent lists into fresh references.
    /// </summary>
    private static string CommitKey(GitCommit commit)
    {
        var builder = new StringBuilder(commit.Sha)
            .Append('|').Append(commit.Lane)
            .Append('|').Append(commit.LaneCount)
            .Append('|').Append(commit.Refs);

        foreach (var edge in commit.Edges)
        {
            builder.Append('|').Append(edge.From).Append('-').Append(edge.To);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rewrites a collection in place only where it differs, so the ListView does not
    /// lose scroll position and selection on every five-second poll.
    /// </summary>
    private static void Replace<T>(RangeObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.Count == source.Count && target.SequenceEqual(source))
        {
            return;
        }

        target.ReplaceAll(source);
    }

    /// <summary>
    /// The same, for records whose equality is reference-based (list-typed members), or
    /// whose identity is only part of the record: rows are compared by their keys.
    /// </summary>
    private static void Replace<T>(RangeObservableCollection<T> target, IReadOnlyList<T> source, Func<T, object> key)
    {
        if (target.Count == source.Count &&
            target.Select(key).SequenceEqual(source.Select(key)))
        {
            return;
        }

        target.ReplaceAll(source);
    }

    public void Dispose()
    {
        _smartCommit?.Cancel();
        _timer.Stop();
        _refreshing.Dispose();
    }
}
