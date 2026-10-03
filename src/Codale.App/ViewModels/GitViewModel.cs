using System.Text;

using Codale.App.Services;
using Codale.Core.Helper;
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
    private readonly GitRepository _repository;
    private readonly IHelperModel _helper;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _timer;
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <summary>Fingerprint of the snapshot currently applied; null until one is.</summary>
    private int? _lastFingerprint;

    public GitViewModel(string projectPath, IHelperModel helper)
    {
        _repository = new GitRepository(projectPath);
        _helper = helper;

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(AppSettings.GitPollSeconds);
        _timer.Tick += (_, _) => _ = RefreshAsyncSafe();
    }

    public RangeObservableCollection<GitFileStatus> Changes { get; } = [];

    public RangeObservableCollection<GitCommit> Log { get; } = [];

    public RangeObservableCollection<GitBranch> Branches { get; } = [];

    /// <summary>
    /// Raised on the UI thread after every refresh, carrying the fresh snapshot. The
    /// file tree listens to colour itself; the panel's own collections are updated
    /// before this fires.
    /// </summary>
    public event EventHandler<GitSnapshot>? SnapshotRefreshed;

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

    /// <summary>True while the helper model is writing a commit message; the sparkle waits.</summary>
    [ObservableProperty]
    public partial bool IsGeneratingMessage { get; set; }

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
        catch (Exception ex)
        {
            CrashLog.Trace($"Git refresh failed: {ex.Message}");
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
                Replace(Changes, snapshot.Changes);

                // Selection is reference-based, and every refresh builds new records;
                // put it back on the same path so the toolbar keeps its target.
                SelectedChange = previouslySelected is null
                    ? null
                    : Changes.FirstOrDefault(c => c.Path == previouslySelected);

                Replace(Log, snapshot.Log, CommitKey);
                Replace(Branches, snapshot.Branches, b => b.Name);
            }

            SnapshotRefreshed?.Invoke(this, snapshot);
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
    /// Asks the helper model for a commit subject for the working tree's changes, in
    /// Conventional Commits form, and puts it in the message box. A CLI cold-starts per
    /// call, so the first click may take a while; the sparkle shows why.
    /// </summary>
    [RelayCommand]
    public async Task GenerateCommitMessageAsync()
    {
        if (IsGeneratingMessage || !IsRepository)
        {
            return;
        }

        if (ChangeCount == 0)
        {
            ActionError = "No changes to describe.";
            return;
        }

        IsGeneratingMessage = true;
        ActionError = null;

        try
        {
            var diffs = await _repository.GetWorkingTreeDiffAsync().ConfigureAwait(true);
            var prompt = BuildMessagePrompt(diffs);
            if (prompt.Length == 0)
            {
                ActionError = "The changes have no readable diff to describe.";
                return;
            }

            // Generous because a CLI cold-starts per call; a stuck one answers neither
            // way, and the box stays editable meanwhile.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            var message = await _helper.CompleteAsync(MessageSystemPrompt, prompt, timeout.Token)
                .ConfigureAwait(true);

            message = CleanGeneratedMessage(message);
            if (message.Length == 0)
            {
                ActionError = "The model returned nothing usable; write the message or try again.";
                return;
            }

            CommitMessage = message;
        }
        catch (OperationCanceledException)
        {
            // The 120 s budget ran out: the box stays as the user left it.
        }
        catch (Exception ex)
        {
            // No CLI installed, or the call failed: the box stays as the user left it.
            ActionError = $"Could not generate a message: {ex.Message}";
        }
        finally
        {
            IsGeneratingMessage = false;
        }
    }

    /// <summary>The contract the commit subject must satisfy, stated the strict way.</summary>
    private const string MessageSystemPrompt =
        "You write git commit subjects in Conventional Commits format. Reply with the subject line " +
        "only: one single line, no body, no markdown, no code fences, no quotes. " +
        "Format: type(scope): summary. The type is exactly one of feat, fix, perf, revert, refactor, " +
        "docs, test, build, ci, chore, style. The scope is a short lowercase area in parentheses and is " +
        "omitted when nothing fits. The summary starts lowercase, uses the imperative mood (\"add\", not " +
        "\"added\" or \"adds\"), and has no trailing period. The whole line is at most 72 characters.";

    /// <summary>
    /// Renders the working-tree diff as the model's prompt: one block per file with its
    /// change shape, then the hunks. Trims to a budget so a large working tree cannot
    /// overflow the model's context - the header of every file survives, detail gives way.
    /// </summary>
    private static string BuildMessagePrompt(IReadOnlyList<FileDiff> diffs)
    {
        const int TotalBudget = 8000;
        const int FileBudget = 1600;

        var builder = new StringBuilder("These working-tree changes are about to be committed:\n");

        foreach (var diff in diffs)
        {
            var shape = diff.IsBinary ? "binary"
                : $"{(diff.IsNew ? "new file" : diff.IsDeleted ? "deleted" : "modified")}, {diff.Stat}";

            var header = new StringBuilder("\n--- ").Append(diff.Path).Append(" (").Append(shape).Append(")\n");
            var room = Math.Min(FileBudget, TotalBudget - builder.Length - header.Length);
            if (room <= 0)
            {
                break;
            }

            builder.Append(header);

            foreach (var hunk in diff.Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    if (room <= 0)
                    {
                        break;
                    }

                    var text = line.Text;
                    if (text.Length > room)
                    {
                        text = text[..room];
                    }

                    builder.Append(line.Sign).Append(' ').Append(text).Append('\n');
                    room -= text.Length + 2;
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reduces the model's answer to one clean subject: the first real line of a fenced
    /// or chatty reply, unwrapped from quotes, without a trailing period, capped at 72
    /// characters on a word boundary.
    /// </summary>
    private static string CleanGeneratedMessage(string message)
    {
        var line = message
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal));

        if (line is null)
        {
            return "";
        }

        line = line.Trim('"', '\'', '`', '*').TrimEnd('.', '!', '*').Trim();

        // Compress the whitespace a small model likes to leave between sentences.
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        line = string.Join(" ", parts);

        if (line.Length > 72)
        {
            line = line[..72];
            var cut = line.LastIndexOf(' ');
            if (cut > 40)
            {
                line = line[..cut];
            }
        }

        return line;
    }

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
        _timer.Stop();
        _refreshing.Dispose();
    }
}
