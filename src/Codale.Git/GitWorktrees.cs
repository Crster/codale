namespace Codale.Git;

/// <summary>Outcome of creating a session worktree: its path, or why git refused.</summary>
public sealed record GitWorktreeResult(string? Path, string? Error);

public sealed record GitWorktree
{
    public required string Path { get; init; }
    public string? Branch { get; init; }
    public string? Head { get; init; }
    public bool IsMain { get; init; }
}

/// <summary>
/// Creates and removes the per-session git worktrees.
/// </summary>
/// <remarks>
/// Codale creates the worktree itself rather than delegating to the CLIs' own
/// <c>--worktree</c> flags, because it needs to know the path: the agent is launched
/// with the worktree as its working directory, and the session's diff is taken against
/// that branch. Worktrees live under <c>.codale/worktrees</c> and are hidden from git
/// via <c>.git/info/exclude</c>, never by editing the user's .gitignore.
/// </remarks>
public sealed class GitWorktrees
{
    public const string CodaleDirectory = ".codale";

    /// <summary>Branches Codale creates for sessions live under this prefix; only those are ever deleted.</summary>
    private const string BranchPrefix = "codale/";

    private readonly string _repositoryRoot;

    public GitWorktrees(string repositoryRoot) => _repositoryRoot = repositoryRoot;

    public string WorktreesRoot => Path.Combine(_repositoryRoot, CodaleDirectory, "worktrees");

    /// <summary>
    /// Creates a worktree on a new branch for a session.
    /// </summary>
    /// <returns>The worktree path, or null when this is not a git repository or git refused.</returns>
    public async Task<string?> CreateAsync(string sessionId, string? baseRef = null, CancellationToken ct = default) =>
        (await TryCreateAsync(sessionId, baseRef, ct).ConfigureAwait(false)).Path;

    /// <summary>
    /// <see cref="CreateAsync"/> with git's own explanation when it fails, for the UI to show.
    /// </summary>
    public async Task<GitWorktreeResult> TryCreateAsync(string sessionId, string? baseRef = null, CancellationToken ct = default)
    {
        var inside = await RunAsync(ct, readOnly: true, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
        if (!inside.Success)
        {
            return new GitWorktreeResult(null, "Not a git repository.");
        }

        if (baseRef is { Length: > 0 } && !GitProcess.IsSafeRef(baseRef))
        {
            return new GitWorktreeResult(null, "That is not a valid branch or commit name.");
        }

        await ExcludeCodaleDirectoryAsync(ct).ConfigureAwait(false);

        var shortId = Shorten(sessionId);
        var path = Path.Combine(WorktreesRoot, shortId);
        var branch = BranchPrefix + shortId;

        Directory.CreateDirectory(WorktreesRoot);

        var args = new List<string> { "worktree", "add", "-b", branch, "--end-of-options", path };
        if (baseRef is { Length: > 0 })
        {
            args.Add(baseRef);
        }

        var result = await RunAsync(ct, readOnly: false, [.. args]).ConfigureAwait(false);

        return result.Success
            ? new GitWorktreeResult(path, null)
            : new GitWorktreeResult(null, ErrorOf(result));
    }

    public async Task<IReadOnlyList<GitWorktree>> ListAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(ct, readOnly: true, "worktree", "list", "--porcelain").ConfigureAwait(false);

        return result.Success ? ParseList(result.StandardOutput) : [];
    }

    /// <summary>
    /// Removes a worktree, and the <c>codale/*</c> branch that was created with it when
    /// that branch has no unique commits (<c>git branch -d</c> refuses an unmerged one).
    /// </summary>
    public async Task<GitRepository.GitWriteResult> RemoveAsync(string worktreePath, bool force = false, CancellationToken ct = default)
    {
        // The branch has to be read before the worktree goes; the list forgets it after.
        var branch = (await ListAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(w => !w.IsMain && SamePath(w.Path, worktreePath))?.Branch;

        var args = force
            ? new[] { "worktree", "remove", "--force", "--end-of-options", worktreePath }
            : ["worktree", "remove", "--end-of-options", worktreePath];

        var result = await RunAsync(ct, readOnly: false, args).ConfigureAwait(false);
        if (!result.Success)
        {
            return new GitRepository.GitWriteResult(false, ErrorOf(result));
        }

        if (branch is not null && branch.StartsWith(BranchPrefix, StringComparison.Ordinal))
        {
            // Best effort: a branch holding unmerged work is deliberately kept.
            await RunAsync(ct, readOnly: false, "branch", "-d", "--end-of-options", branch).ConfigureAwait(false);
        }

        return GitRepository.GitWriteResult.Ok;
    }

    /// <summary>
    /// Merges an isolated session back: commits whatever is pending in the worktree,
    /// merges its branch into the project's current branch, then removes the worktree.
    /// A conflicting merge is aborted and the worktree left untouched.
    /// </summary>
    public async Task<GitRepository.GitWriteResult> MergeBackAsync(string worktreePath, CancellationToken ct = default)
    {
        var branch = (await ListAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(w => !w.IsMain && SamePath(w.Path, worktreePath))?.Branch;

        if (branch is null || !branch.StartsWith(BranchPrefix, StringComparison.Ordinal) || !GitProcess.IsSafeRef(branch))
        {
            return new GitRepository.GitWriteResult(false, "That worktree has no Codale branch to merge.");
        }

        var status = await GitProcess.RunAsync(worktreePath, ["status", "--porcelain"], true, GitProcess.ReadTimeout, ct).ConfigureAwait(false);
        if (status.Success && status.StandardOutput.Trim().Length > 0)
        {
            var add = await GitProcess.RunAsync(worktreePath, ["add", "-A"], false, GitProcess.WriteTimeout, ct).ConfigureAwait(false);
            var commit = add.Success
                ? await GitProcess.RunAsync(worktreePath, ["commit", "-m", "Isolated session changes"], false, GitProcess.WriteTimeout, ct).ConfigureAwait(false)
                : add;
            if (!commit.Success)
            {
                return new GitRepository.GitWriteResult(false, ErrorOf(commit));
            }
        }

        var merge = await RunAsync(ct, readOnly: false, "merge", "--no-ff", "-m", $"Merge isolated session {branch}", "--end-of-options", branch).ConfigureAwait(false);
        if (!merge.Success)
        {
            await RunAsync(ct, readOnly: false, "merge", "--abort").ConfigureAwait(false);
            var detail = ErrorOf(merge);
            return new GitRepository.GitWriteResult(false, detail == "git failed." && merge.StandardOutput.Length > 0
                ? "Merge conflict; the isolated worktree was kept."
                : detail);
        }

        return await RemoveAsync(worktreePath, force: true, ct).ConfigureAwait(false);
    }

    /// <summary>The <c>codale/*</c> branch a worktree was created on, or null for any other worktree.</summary>
    public async Task<string?> BranchOfAsync(string worktreePath, CancellationToken ct = default)
    {
        var branch = (await ListAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(w => !w.IsMain && SamePath(w.Path, worktreePath))?.Branch;

        return branch is not null && branch.StartsWith(BranchPrefix, StringComparison.Ordinal) && GitProcess.IsSafeRef(branch)
            ? branch
            : null;
    }

    /// <summary>True when every commit on <paramref name="branch"/> is already in the project's current branch.</summary>
    public async Task<bool> IsMergedAsync(string branch, CancellationToken ct = default) =>
        GitProcess.IsSafeRef(branch) &&
        (await RunAsync(ct, readOnly: true, "merge-base", "--is-ancestor", branch, "HEAD").ConfigureAwait(false)).Success;

    /// <summary>
    /// Commits whatever is pending in the worktree, so an agent merging its branch sees
    /// all of the session's work. Returns an error, or null when the worktree is clean or committed.
    /// </summary>
    public async Task<string?> CommitPendingAsync(string worktreePath, CancellationToken ct = default)
    {
        var status = await GitProcess.RunAsync(worktreePath, ["status", "--porcelain"], true, GitProcess.ReadTimeout, ct).ConfigureAwait(false);
        if (!status.Success || status.StandardOutput.Trim().Length == 0)
        {
            return null;
        }

        var add = await GitProcess.RunAsync(worktreePath, ["add", "-A"], false, GitProcess.WriteTimeout, ct).ConfigureAwait(false);
        var commit = add.Success
            ? await GitProcess.RunAsync(worktreePath, ["commit", "-m", "Isolated session changes"], false, GitProcess.WriteTimeout, ct).ConfigureAwait(false)
            : add;

        return commit.Success ? null : ErrorOf(commit);
    }

    /// <summary>git lists paths with forward slashes; the caller's may use backslashes.</summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string ErrorOf(GitResult result) =>
        result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            is { Length: > 0 } line ? line : "git failed.";

    internal static IReadOnlyList<GitWorktree> ParseList(string porcelain)
    {
        var worktrees = new List<GitWorktree>();

        string? path = null;
        string? head = null;
        string? branch = null;

        void Flush()
        {
            if (path is not null)
            {
                worktrees.Add(new GitWorktree
                {
                    Path = path,
                    Head = head,
                    // refs/heads/foo -> foo
                    Branch = branch is { Length: > 0 } b && b.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? b["refs/heads/".Length..]
                        : branch,
                    IsMain = worktrees.Count == 0,
                });
            }

            path = null;
            head = null;
            branch = null;
        }

        foreach (var line in TextDiff.SplitLf(porcelain))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line["HEAD ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = line["branch ".Length..];
            }
        }

        Flush();
        return worktrees;
    }

    /// <summary>
    /// Keeps <c>.codale/</c> out of git without touching a file the user owns.
    /// <c>.git/info/exclude</c> is local-only and never committed.
    /// </summary>
    private async Task ExcludeCodaleDirectoryAsync(CancellationToken ct)
    {
        var gitDir = await RunAsync(ct, readOnly: true, "rev-parse", "--git-common-dir").ConfigureAwait(false);
        if (!gitDir.Success)
        {
            return;
        }

        var dir = gitDir.StandardOutput.Trim();
        if (!Path.IsPathRooted(dir))
        {
            dir = Path.Combine(_repositoryRoot, dir);
        }

        var excludePath = Path.Combine(dir, "info", "exclude");
        const string entry = "/" + CodaleDirectory + "/";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);

            if (File.Exists(excludePath) &&
                (await File.ReadAllTextAsync(excludePath, ct).ConfigureAwait(false)).Contains(entry, StringComparison.Ordinal))
            {
                return;
            }

            await File.AppendAllTextAsync(
                excludePath,
                $"{Environment.NewLine}# Added by Codale{Environment.NewLine}{entry}{Environment.NewLine}",
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the worktree still works, it just shows up as untracked.
        }
    }

    /// <summary>Session ids are GUIDs; the first segment is unique enough for a path.</summary>
    private static string Shorten(string sessionId)
    {
        var cleaned = new string(sessionId.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return cleaned.Length <= 8 ? cleaned : cleaned[..8];
    }

    private Task<GitResult> RunAsync(CancellationToken ct, bool readOnly, params string[] arguments) =>
        GitProcess.RunAsync(
            _repositoryRoot, arguments, readOnly, readOnly ? GitProcess.ReadTimeout : GitProcess.WriteTimeout, ct);
}
