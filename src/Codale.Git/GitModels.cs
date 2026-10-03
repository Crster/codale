namespace Codale.Git;

public enum GitChangeKind
{
    Unmodified,
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    Untracked,
    Ignored,
    Conflicted,
}

/// <summary>One entry from <c>git status</c>.</summary>
public sealed record GitFileStatus
{
    public required string Path { get; init; }

    /// <summary>What is staged for the next commit.</summary>
    public GitChangeKind Index { get; init; }

    /// <summary>What has changed in the working tree but is not staged.</summary>
    public GitChangeKind WorkTree { get; init; }

    public string? OriginalPath { get; init; }

    /// <summary>
    /// Directory (with trailing slash) shown before the file name; null for files at the
    /// repository root. Filled in by <c>git status</c> parsing.
    /// </summary>
    public string? PathHint { get; init; }

    /// <summary>
    /// The directory hint as rendered after the file name, separator included; empty at
    /// the repository root. The row puts the name first so a narrow panel trims the
    /// directory off the end, not the name.
    /// </summary>
    public string PathHintTail => PathHint is null ? "" : $" {PathHint}";

    public bool IsStaged => Index is not (GitChangeKind.Unmodified or GitChangeKind.Untracked);

    public bool IsConflicted => Index == GitChangeKind.Conflicted || WorkTree == GitChangeKind.Conflicted;

    /// <summary>The one state this entry should be shown as, whichever side staged it.</summary>
    public GitChangeKind DisplayKind =>
        IsConflicted ? GitChangeKind.Conflicted
        : WorkTree is not GitChangeKind.Unmodified ? WorkTree
        : Index;

    /// <summary>The two-letter code git itself would print, for the status column.</summary>
    public string ShortCode => $"{Letter(Index)}{Letter(WorkTree)}";

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>What the panel labels the row: the file name, or the whole path for an
    /// untracked directory entry ("src/"), whose name part is empty.</summary>
    public string DisplayName => FileName.Length > 0 ? FileName : Path;

    /// <summary>The directory part of the path, forward slashes as git reports them.</summary>
    public string? Directory =>
        Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : null;

    private static char Letter(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Modified => 'M',
        GitChangeKind.Added => 'A',
        GitChangeKind.Deleted => 'D',
        GitChangeKind.Renamed => 'R',
        GitChangeKind.Copied => 'C',
        GitChangeKind.Untracked => '?',
        GitChangeKind.Ignored => '!',
        GitChangeKind.Conflicted => 'U',
        _ => '.',
    };
}

public sealed record GitBranchInfo
{
    public string? Name { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }

    /// <summary>True on a detached HEAD, where <see cref="Name"/> is a commit id.</summary>
    public bool IsDetached { get; init; }

    public string Display => Name ?? "(unknown)";

    public string? TrackingSummary => Upstream is null
        ? null
        : (Ahead, Behind) switch
        {
            (0, 0) => "up to date",
            (> 0, 0) => $"{Ahead} ahead",
            (0, > 0) => $"{Behind} behind",
            var (a, b) => $"{a} ahead, {b} behind",
        };
}

/// <summary>One edge in the history graph: a line from lane <c>From</c> to lane <c>To</c>
/// between this row and its parent row below.</summary>
public readonly record struct GitGraphEdge(int From, int To);

public sealed record GitCommit
{
    public required string Sha { get; init; }
    public required string Subject { get; init; }
    public required string Author { get; init; }
    public DateTimeOffset Date { get; init; }

    /// <summary>Parent commit ids, first parent first; empty on a root commit.</summary>
    public IReadOnlyList<string> Parents { get; init; } = [];

    /// <summary>Branch and tag decorations, in git's own <c>%D</c> form: "HEAD -> main, origin/main".</summary>
    public string Refs { get; init; } = "";

    // Graph geometry, filled in by GitGraph.Assign: which lane the dot sits in, how many
    // lanes the row spans, and the edges drawn between this row and the next.
    public int Lane { get; init; }

    public int LaneCount { get; init; } = 1;

    public IReadOnlyList<GitGraphEdge> Edges { get; init; } = [];

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;

    /// <summary>The decoration labels worth showing, e.g. "main", "origin/main".</summary>
    public IEnumerable<string> RefLabels => Refs
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(r => r.StartsWith("HEAD -> ", StringComparison.Ordinal) ? r["HEAD -> ".Length..] : r);

    /// <summary>Ref labels joined for the one-line decoration chip under the subject.</summary>
    public string RefDisplay => string.Join(" ", RefLabels);

    public string RelativeDate
    {
        get
        {
            var age = DateTimeOffset.Now - Date;

            return age switch
            {
                { TotalMinutes: < 1 } => "just now",
                { TotalHours: < 1 } => $"{age.TotalMinutes:0}m ago",
                { TotalDays: < 1 } => $"{age.TotalHours:0}h ago",
                { TotalDays: < 30 } => $"{age.TotalDays:0}d ago",
                _ => Date.ToString("yyyy-MM-dd"),
            };
        }
    }
}

/// <summary>One local branch from <c>git branch</c>, for the switcher.</summary>
public sealed record GitBranch
{
    public required string Name { get; init; }

    public bool IsCurrent { get; init; }

    public string? Upstream { get; init; }

    /// <summary>Ahead/behind counts against the upstream, when there is one.</summary>
    public string? TrackingSummary { get; init; }
}

/// <summary>Everything the git panel shows, gathered in one pass.</summary>
public sealed record GitSnapshot
{
    public bool IsRepository { get; init; }

    /// <summary>
    /// Absolute path of the repository root. Status and ignored paths are relative to
    /// it, not to whichever project folder happens to be open, so they need it to be
    /// mapped onto the file tree.
    /// </summary>
    public string? RepositoryRoot { get; init; }

    public GitBranchInfo Branch { get; init; } = new();

    public IReadOnlyList<GitFileStatus> Changes { get; init; } = [];

    /// <summary>
    /// Paths git reports as ignored, kept apart from <see cref="Changes"/> so the change
    /// list and its counts stay about real work while the file tree uses them to mute.
    /// </summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];

    public IReadOnlyList<GitCommit> Log { get; init; } = [];

    /// <summary>Local branches, current first; the branch switcher's contents.</summary>
    public IReadOnlyList<GitBranch> Branches { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>
    /// Hash of the raw git output this snapshot was built from. A poll whose output
    /// hashes identically to the last one is skipped by the panel: nothing downstream
    /// (change list, file-tree recolour) needs re-running when git saw no change.
    /// </summary>
    public int Fingerprint { get; init; }
}
