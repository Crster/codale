using Codale.Core.Projects;

namespace Codale.Git;

/// <summary>
/// Reads repository state by shelling out to git, and performs the everyday writes
/// (stage, commit, checkout) the same way.
/// </summary>
/// <remarks>
/// The CLI is used rather than a managed git library because <c>git status</c> on a
/// large repository is where managed implementations fall down, and git's own
/// porcelain v2 format is explicitly designed to be parsed by tools.
/// </remarks>
public sealed class GitRepository
{
    /// <summary>The flags every diff shares; the prefixes are pinned because the parser expects a/ and b/ whatever the user's diff.noprefix says.</summary>
    private static readonly string[] DiffFlags =
        ["--no-ext-diff", "--no-color", "--find-renames", "--unified=3", "--src-prefix=a/", "--dst-prefix=b/"];

    private const int MaxUntrackedDiffFiles = 50;
    private const long MaxUntrackedDiffBytes = 4 * 1024 * 1024;

    private static readonly GitWriteResult InvalidRef = new(false, "That is not a valid branch or commit name.");

    private readonly string _workingDirectory;

    /// <summary>
    /// The cached rev-parse answer: the repository root, or null when the folder is not
    /// a repository. Re-verified at most once a minute - the answer almost never flips,
    /// and running it serially in front of every other call doubled each poll's latency.
    /// </summary>
    private string? _repositoryRootCache;
    private long _insideProbedAt = long.MinValue;

    public GitRepository(string workingDirectory) => _workingDirectory = workingDirectory;

    public async Task<GitSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            string repositoryRoot;
            if (_repositoryRootCache is { } cached && Environment.TickCount64 - _insideProbedAt < 60_000)
            {
                repositoryRoot = cached;
            }
            else
            {
                // One call answers both questions: the first line is the inside-work-tree
                // answer, the second the repository root the status paths are relative to.
                var inside = await RunReadAsync(["rev-parse", "--is-inside-work-tree", "--show-toplevel"], ct).ConfigureAwait(false);
                var lines = inside.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                _insideProbedAt = Environment.TickCount64;
                if (!inside.Success || lines.Length == 0 || !lines[0].TrimStart().StartsWith("true", StringComparison.Ordinal))
                {
                    _repositoryRootCache = null;
                    return new GitSnapshot { IsRepository = false };
                }

                // Git prints the root with forward slashes; the file tree compares it against
                // paths from the Windows directory APIs, which use backslashes.
                repositoryRoot = lines.Length > 1 ? lines[1].Trim().Replace('/', '\\') : _workingDirectory;
                _repositoryRootCache = repositoryRoot;
            }

            // -z makes every field NUL-delimited, so paths containing spaces, quotes or
            // newlines survive intact - git will not quote them in this mode.
            // --ignored=traditional appends "! " records for ignored paths, collapsing an
            // entirely ignored directory to one entry instead of every file inside it.
            // The log is topo-ordered so GitGraph can assign lanes: a parent is always
            // on a later row than its child.
            var statusTask = RunReadAsync(["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal", "--ignored=traditional"], ct);
            var logTask = RunReadAsync(["log", "-n", "100", "--topo-order", "--date=iso-strict", "--format=%H%x1f%P%x1f%s%x1f%an%x1f%ad%x1f%D%x1e"], ct);

            // Tab as the field delimiter: for-each-ref formatting (unlike log) has no
            // %x1f hex escape, and ref names cannot contain whitespace.
            var branchesTask = RunReadAsync(["branch", "--format=%(HEAD)\t%(refname:short)\t%(upstream:short)\t%(upstream:track)"], ct);

            await Task.WhenAll(statusTask, logTask, branchesTask).ConfigureAwait(false);

            var status = await statusTask.ConfigureAwait(false);
            var log = await logTask.ConfigureAwait(false);
            var branches = await branchesTask.ConfigureAwait(false);

            var (branch, changes) = ParseStatus(status.StandardOutput, untrackedFiles: null);

            // Individual untracked files are only needed when status collapsed a wholly
            // untracked directory to "dir/" - on every other tick the ls-files process
            // is a spawn for nothing.
            if (changes.Any(c => c.Path.EndsWith('/')))
            {
                var untracked = await RunReadAsync(["ls-files", "--others", "--exclude-standard", "--full-name", "-z", ":/"], ct).ConfigureAwait(false);
                if (untracked.Success)
                {
                    (_, changes) = ParseStatus(
                        status.StandardOutput,
                        untracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries));
                }
            }

            var commits = log.Success ? ParseLog(log.StandardOutput) : [];

            return new GitSnapshot
            {
                IsRepository = true,
                RepositoryRoot = repositoryRoot,
                Branch = branch,
                Changes = changes,
                Ignored = ParseIgnored(status.StandardOutput),
                Log = GitGraph.Assign(commits),
                Branches = branches.Success ? ParseBranches(branches.StandardOutput) : [],
                Fingerprint = Hash(status.StandardOutput, log.StandardOutput, branches.StandardOutput),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GitSnapshot { IsRepository = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Diff of everything not yet committed, staged and unstaged together, plus
    /// untracked files.
    /// </summary>
    /// <remarks>
    /// <c>--no-ext-diff</c> and <c>--no-color</c> stop a user's configured external
    /// diff tool or colour settings from producing output the parser cannot read.
    /// <c>--find-renames</c> makes a moved file read as a rename rather than as a
    /// wholesale delete and add.
    /// </remarks>
    public async Task<IReadOnlyList<FileDiff>> GetWorkingTreeDiffAsync(CancellationToken ct = default)
    {
        var tracked = await RunReadAsync(["diff", .. DiffFlags, "HEAD"], ct).ConfigureAwait(false);

        var diffs = tracked.Success
            ? GitDiffParser.Parse(tracked.StandardOutput).ToList()
            : [];

        // Untracked files are invisible to `git diff`; they are diffed in process
        // instead - one `git diff --no-index` process per file made a 10-new-file
        // burst cost 11 spawns per refresh, repeated on every agent write burst.
        var untracked = await RunReadAsync(["ls-files", "--others", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
        if (untracked.Success)
        {
            // Bounded per refresh: a build dropping thousands of new files must not be
            // read and diffed in full on every poll. Past the budget a file is still
            // listed, just without content.
            var filesLeft = MaxUntrackedDiffFiles;
            var bytesLeft = MaxUntrackedDiffBytes;

            foreach (var path in untracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (filesLeft <= 0 || bytesLeft <= 0)
                {
                    diffs.Add(new FileDiff { Path = path, IsNew = true, IsBinary = true });
                    continue;
                }

                filesLeft--;
                var (diff, size) = await UntrackedFileDiffAsync(path, ct).ConfigureAwait(false);
                bytesLeft -= size;
                diffs.Add(diff);
            }
        }

        return diffs;
    }

    /// <summary>
    /// A brand-new file diffed against nothing: its whole content reads as added lines.
    /// The file the agent just wrote is read directly - spawning git to concatenate it
    /// told us nothing the file did not already say.
    /// </summary>
    private async Task<(FileDiff Diff, long Size)> UntrackedFileDiffAsync(string path, CancellationToken ct)
    {
        // ls-files prints repo-relative paths; the read has to anchor at the repository,
        // whose working directory is not necessarily the process's.
        var fullPath = System.IO.Path.Combine(_workingDirectory, path);
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > 2 * 1024 * 1024)
            {
                return (new FileDiff { Path = path, IsNew = true, IsBinary = true }, 0);
            }

            var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            if (content.AsSpan(0, Math.Min(content.Length, 8192)).IndexOf('\0') >= 0)
            {
                return (new FileDiff { Path = path, IsNew = true, IsBinary = true }, info.Length);
            }

            return (TextDiff.Compute(path, null, content), info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new FileDiff { Path = path, IsNew = true, IsBinary = true }, 0);
        }
    }

    /// <summary>
    /// Diff of a single file's working-tree state against HEAD - everything not yet
    /// committed for that one path - or, for an untracked file, its full contents.
    /// </summary>
    public async Task<IReadOnlyList<FileDiff>> GetFileDiffAsync(string path, CancellationToken ct = default)
    {
        var tracked = await RunReadAsync(["diff", .. DiffFlags, "HEAD", "--", path], ct).ConfigureAwait(false);

        if (tracked.Success)
        {
            var diffs = GitDiffParser.Parse(tracked.StandardOutput);
            if (diffs.Count > 0)
            {
                return diffs;
            }

            // An empty diff for a path status calls untracked means the file is not in
            // HEAD or the index; fall through and read it as brand new.
        }

        var untracked = await RunReadAsync(["ls-files", "--others", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
        if (!untracked.Success || !untracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Contains(path))
        {
            return [];
        }

        var added = await RunReadAsync(
            ["diff", "--no-index", "--no-ext-diff", "--no-color", "--unified=3", "--src-prefix=a/", "--dst-prefix=b/", "--", "/dev/null", path],
            ct).ConfigureAwait(false);

        // --no-index exits 1 when the files differ, which is the normal case here.
        return GitDiffParser.Parse(added.StandardOutput);
    }

    /// <summary>
    /// Diff of one commit: what it changed against its first parent. A merge commit's
    /// combined diff is deliberately not shown - against the first parent is what
    /// "what did this commit do" means for review.
    /// </summary>
    public async Task<IReadOnlyList<FileDiff>> GetCommitDiffAsync(string sha, CancellationToken ct = default)
    {
        if (!GitProcess.IsSafeRef(sha))
        {
            return [];
        }

        var commit = await RunReadAsync(
            ["show", "-s", "--format=%P", "--no-color", "--end-of-options", sha], ct).ConfigureAwait(false);

        var parents = commit.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var result = parents.Length > 0
            ? await RunReadAsync(["diff", .. DiffFlags, "--end-of-options", parents[0], sha], ct).ConfigureAwait(false)
            : await RunReadAsync(["diff-tree", "--root", "-r", .. DiffFlags, "--end-of-options", sha], ct).ConfigureAwait(false);

        return result.Success ? GitDiffParser.Parse(result.StandardOutput) : [];
    }

    /// <summary>The local branches, for the switcher.</summary>
    internal static List<GitBranch> ParseBranches(string output)
    {
        var branches = new List<GitBranch>();

        foreach (var record in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\t');
            if (fields.Length < 2)
            {
                continue;
            }

            branches.Add(new GitBranch
            {
                Name = fields[1],
                IsCurrent = fields[0] == "*",
                Upstream = fields.Length > 2 && fields[2].Length > 0 ? fields[2] : null,

                // "(upstream:track)" reads "[ahead 2, behind 1]" or "[gone]".
                TrackingSummary = fields.Length > 3 && fields[3].Length > 0
                    ? fields[3].Trim('[', ']')
                    : null,
            });
        }

        // The current branch at the top, then alphabetical.
        branches.Sort((a, b) =>
        {
            var rank = b.IsCurrent.CompareTo(a.IsCurrent);
            return rank != 0 ? rank : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return branches;
    }

    /// <summary>Order-sensitive hash of the raw poll output; the panel skips ticks that hash the same.</summary>
    private static int Hash(params string[] parts)
    {
        var hash = new HashCode();
        foreach (var part in parts)
        {
            hash.Add(part, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>Result of a git write: success, or git's own message for the panel.</summary>
    public sealed record GitWriteResult(bool Success, string? Error = null)
    {
        public static readonly GitWriteResult Ok = new(true);
    }

    internal static (GitBranchInfo Branch, List<GitFileStatus> Changes) ParseStatus(
        string output, IReadOnlyList<string>? untrackedFiles = null)
    {
        var branch = new GitBranchInfo();
        var changes = new List<GitFileStatus>();

        // Sorted once so each collapsed "dir/" entry finds its files by prefix range
        // instead of a linear scan per directory (quadratic when a burst leaves many
        // new files spread over many new directories).
        string[]? sortedUntracked = null;
        if (untrackedFiles is { Count: > 0 } untracked)
        {
            sortedUntracked = [.. untracked];
            Array.Sort(sortedUntracked, StringComparer.Ordinal);
        }

        // branch.oid is emitted before branch.head, so the commit id has to be held
        // until we know whether HEAD is detached and therefore needs it as a label.
        string? headOid = null;

        // Entries are NUL separated, but a rename entry carries two paths and therefore
        // consumes the following record as its original path.
        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];

            if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var name = record["# branch.head ".Length..];
                branch = branch with
                {
                    Name = name == "(detached)" ? null : name,
                    IsDetached = name == "(detached)",
                };
            }
            else if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                headOid = record["# branch.oid ".Length..];
            }
            else if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                branch = branch with { Upstream = record["# branch.upstream ".Length..] };
            }
            else if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var (ahead, behind) = ParseAheadBehind(record["# branch.ab ".Length..]);
                branch = branch with { Ahead = ahead, Behind = behind };
            }
            else if (record.StartsWith("1 ", StringComparison.Ordinal))
            {
                // Ordinary change: 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                if (SplitFields(record, 9) is { } fields)
                {
                    changes.Add(TrackedChange(fields, pathField: 8));
                }
            }
            else if (record.StartsWith("2 ", StringComparison.Ordinal))
            {
                // Rename or copy: the original path is the next NUL-separated record.
                if (SplitFields(record, 10) is { } fields)
                {
                    var original = i + 1 < records.Length ? records[++i] : null;
                    changes.Add(TrackedChange(fields, pathField: 9) with { OriginalPath = original });
                }
            }
            else if (record.StartsWith("u ", StringComparison.Ordinal))
            {
                if (SplitFields(record, 11) is { } fields)
                {
                    changes.Add(new GitFileStatus
                    {
                        Path = fields[10],
                        Index = GitChangeKind.Conflicted,
                        WorkTree = GitChangeKind.Conflicted,
                    });
                }
            }
            else if (record.StartsWith("? ", StringComparison.Ordinal))
            {
                var path = record[2..];

                // status collapses a wholly untracked directory to "dir/"; list its files
                // instead so each one can be opened and staged.
                if (path.EndsWith('/') && untrackedFiles is not null)
                {
                    var inside = FilesUnder(path, sortedUntracked!);
                    if (inside.Count > 0)
                    {
                        changes.AddRange(inside.Select(UntrackedChange));
                        continue;
                    }
                }

                changes.Add(UntrackedChange(path));
            }
        }

        if (branch.IsDetached && headOid is { Length: > 0 })
        {
            branch = branch with { Name = headOid.Length >= 7 ? headOid[..7] : headOid };
        }

        // Conflicts first, then staged, then the rest; alphabetical within each group.
        changes.Sort((a, b) =>
        {
            var rank = Rank(a).CompareTo(Rank(b));
            return rank != 0 ? rank : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        return (branch, DisambiguateNames(changes));

        static int Rank(GitFileStatus s) => s.IsConflicted ? 0 : s.IsStaged ? 1 : 2;
    }

    /// <summary>Format: "+&lt;ahead&gt; -&lt;behind&gt;"; a part that does not parse counts as 0.</summary>
    private static (int Ahead, int Behind) ParseAheadBehind(string text)
    {
        var ahead = 0;
        var behind = 0;

        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith('+'))
            {
                int.TryParse(part[1..], out ahead);
            }
            else if (part.StartsWith('-'))
            {
                int.TryParse(part[1..], out behind);
            }
        }

        return (ahead, behind);
    }

    /// <summary>A porcelain-v2 "1"/"2" entry: field 1 is the two-letter XY state, <paramref name="pathField"/> the path.</summary>
    private static GitFileStatus TrackedChange(string[] fields, int pathField) => new()
    {
        Path = fields[pathField],
        Index = FromLetter(fields[1][0]),
        WorkTree = FromLetter(fields[1][1]),
    };

    private static GitFileStatus UntrackedChange(string path) => new()
    {
        Path = path,
        Index = GitChangeKind.Untracked,
        WorkTree = GitChangeKind.Untracked,
    };

    /// <summary>
    /// The files under <paramref name="dir"/>, from a sorted list: the range is found by
    /// binary search, because every path under the directory sorts directly after it.
    /// </summary>
    private static List<string> FilesUnder(string dir, string[] sorted)
    {
        var lo = 0;
        var hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (string.Compare(sorted[mid], dir, StringComparison.Ordinal) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        var inside = new List<string>();
        for (var i = lo; i < sorted.Length && sorted[i].StartsWith(dir, StringComparison.Ordinal); i++)
        {
            inside.Add(sorted[i]);
        }

        return inside;
    }

    /// <summary>
    /// Gives every entry inside a folder its directory as a hint, shown before the file
    /// name so the panel never leaves it unclear where a file comes from.
    /// </summary>
    private static List<GitFileStatus> DisambiguateNames(List<GitFileStatus> changes) =>
        changes.Select(c => c.FileName.Length > 0 && c.Directory is { } dir ?c with { PathHint = dir + "/" } : c).ToList();

    /// <summary>
    /// Pulls the "! &lt;path&gt;" records out of a porcelain v2 status run that was asked
    /// for ignored entries. Kept apart from <see cref="ParseStatus"/> so the change list
    /// never mixes ignored files in with real work.
    /// </summary>
    internal static List<string> ParseIgnored(string output)
    {
        var ignored = new List<string>();

        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.StartsWith("! ", StringComparison.Ordinal))
            {
                ignored.Add(record[2..]);
            }
        }

        return ignored;
    }

    /// <summary>
    /// Splits a porcelain record into exactly <paramref name="count"/> fields, keeping
    /// any spaces in the trailing path field intact.
    /// </summary>
    private static string[]? SplitFields(string record, int count)
    {
        var fields = record.Split(' ', count);
        return fields.Length == count && fields[1].Length >= 2 ? fields : null;
    }

    internal static List<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();

        // Records separated by RS (0x1e), fields by US (0x1f): neither can appear in a
        // commit subject, unlike newlines. Fields: sha, parents, subject, author, date,
        // decorations.
        foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Trim('\n', '\r').Split('\x1f');
            if (fields.Length < 6)
            {
                continue;
            }

            DateTimeOffset.TryParse(fields[4], out var date);

            commits.Add(new GitCommit
            {
                Sha = fields[0],
                Parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Subject = fields[2],
                Author = fields[3],
                Date = date,
                Refs = fields[5],
            });
        }

        return commits;
    }

    /// <summary>Stages one path - additions, edits and deletions alike - for the next commit.</summary>
    public Task<GitWriteResult> StageAsync(string path, CancellationToken ct = default) =>
        RunWriteAsync(["add", "--", path], ct);

    /// <summary>Stages every change in the working tree, including untracked files.</summary>
    public Task<GitWriteResult> StageAllAsync(CancellationToken ct = default) =>
        RunWriteAsync(["add", "-A"], ct);

    /// <summary>
    /// Appends a path to the repository's .gitignore, creating the file if needed, so
    /// git stops listing the change. A path that is already covered is left alone.
    /// </summary>
    public async Task<GitWriteResult> IgnoreAsync(string path, CancellationToken ct = default)
    {
        var isDirectory = path.EndsWith('/') || path.EndsWith('\\');
        var entry = path.Replace('\\', '/').TrimEnd('/') + (isDirectory ? "/" : "");

        try
        {
            var gitignorePath = System.IO.Path.Combine(_workingDirectory, ".gitignore");
            var existing = File.Exists(gitignorePath)
                ? await File.ReadAllTextAsync(gitignorePath, ct).ConfigureAwait(false)
                : "";

            // One pattern per line; skip the write if this path is already covered.
            if (existing.Split('\n').Any(line => line.Trim() == entry))
            {
                return GitWriteResult.Ok;
            }

            using var writer = new StreamWriter(gitignorePath, append: true);
            if (existing.Length > 0 && !existing.EndsWith('\n'))
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
            }

            await writer.WriteLineAsync(entry).ConfigureAwait(false);
            return GitWriteResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GitWriteResult(false, ex.Message);
        }
    }

    /// <summary>Takes a staged change back out of the index, leaving the working tree alone.</summary>
    public Task<GitWriteResult> UnstageAsync(string path, CancellationToken ct = default) =>
        RunWriteAsync(["restore", "--staged", "--", path], ct);

    /// <summary>
    /// Throws away every uncommitted change to a path: staged and unstaged edits revert
    /// to HEAD's version, and an untracked file (or directory) is deleted, since git has
    /// no version of it to go back to.
    /// </summary>
    public async Task<GitWriteResult> DiscardAsync(string path, CancellationToken ct = default)
    {
        if (path.EndsWith('/') || path.EndsWith('\\'))
        {
            // Recursive delete: only ever inside the repository (a rooted path would
            // replace the working directory in Combine), and only for a directory git
            // has nothing tracked under, the same "untracked" rule files get below.
            var full = Path.GetFullPath(Path.Combine(_workingDirectory, path));
            if (!ProjectPaths.IsInside(_workingDirectory, full) ||
                Path.TrimEndingDirectorySeparator(full).Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(_workingDirectory)), StringComparison.OrdinalIgnoreCase))
            {
                return new GitWriteResult(false, "That folder is outside the repository.");
            }

            var tracked = await RunReadAsync(["ls-files", "-z", "--cached", "--", path], ct).ConfigureAwait(false);
            if (!tracked.Success || tracked.StandardOutput.Length > 0)
            {
                return new GitWriteResult(false, "That folder has tracked files; discard them individually.");
            }

            try
            {
                Directory.Delete(full, recursive: true);
                return GitWriteResult.Ok;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new GitWriteResult(false, ex.Message);
            }
        }

        if (await IsUntrackedAsync(path, ct).ConfigureAwait(false))
        {
            // git marks nothing here, but a read-only file would still block the delete.
            try
            {
                File.SetAttributes(Path.Combine(_workingDirectory, path), FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException)
            {
                // Gone already, or never there: the discard has nothing left to do.
            }

            try
            {
                File.Delete(Path.Combine(_workingDirectory, path));
                return GitWriteResult.Ok;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new GitWriteResult(false, ex.Message);
            }
        }

        return await RunWriteAsync(
            ["restore", "--source=HEAD", "--staged", "--worktree", "--", path], ct).ConfigureAwait(false);
    }

    /// <summary>Creates a commit from whatever is staged.</summary>
    public async Task<GitWriteResult> CommitAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitWriteResult(false, "The commit message is empty.");
        }

        return await RunWriteAsync(["commit", "-m", message], ct).ConfigureAwait(false);
    }

    /// <summary>Moves the working tree onto another branch.</summary>
    public Task<GitWriteResult> CheckoutAsync(string branch, CancellationToken ct = default) =>
        GitProcess.IsSafeRef(branch)
            ? RunWriteAsync(["checkout", "--end-of-options", branch], ct)
            : Task.FromResult(InvalidRef);

    /// <summary>Creates a branch at HEAD and switches to it.</summary>
    public Task<GitWriteResult> CreateBranchAsync(string name, CancellationToken ct = default) =>
        GitProcess.IsSafeRef(name)
            ? RunWriteAsync(["checkout", "-b", name], ct)
            : Task.FromResult(InvalidRef);

    /// <summary>
    /// Pushes the current branch, setting its upstream on the first push so a plain
    /// <c>git push</c> works from then on.
    /// </summary>
    public Task<GitWriteResult> PushAsync(string branch, bool hasUpstream, CancellationToken ct = default) =>
        hasUpstream
            ? RunWriteAsync(["push"], ct)
            : GitProcess.IsSafeRef(branch)
                ? RunWriteAsync(["push", "-u", "--end-of-options", "origin", branch], ct)
                : Task.FromResult(InvalidRef);

    /// <summary>Fast-forwards the current branch from its upstream.</summary>
    public Task<GitWriteResult> PullAsync(CancellationToken ct = default) =>
        RunWriteAsync(["pull", "--ff-only"], ct);

    /// <summary>Turns the working directory into a new repository.</summary>
    public Task<GitWriteResult> InitAsync(CancellationToken ct = default) =>
        RunWriteAsync(["init"], ct);

    private async Task<bool> IsUntrackedAsync(string path, CancellationToken ct)
    {
        var result = await RunReadAsync(["ls-files", "--others", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
        return result.Success &&
            result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Contains(path);
    }

    private async Task<GitWriteResult> RunWriteAsync(string[] arguments, CancellationToken ct)
    {
        var result = await GitProcess.RunAsync(
            _workingDirectory, arguments, readOnly: false, GitProcess.WriteTimeout, ct).ConfigureAwait(false);

        // git puts the useful explanation on stderr; trim it to one line for the panel.
        return result.Success
            ? GitWriteResult.Ok
            : new GitWriteResult(false, FirstLine(result.StandardError) is { Length: > 0 } error ? error : "git failed.");
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static GitChangeKind FromLetter(char c) => c switch
    {
        'M' => GitChangeKind.Modified,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'U' => GitChangeKind.Conflicted,
        '?' => GitChangeKind.Untracked,
        '!' => GitChangeKind.Ignored,
        _ => GitChangeKind.Unmodified,
    };

    private Task<GitResult> RunReadAsync(string[] arguments, CancellationToken ct) =>
        GitProcess.RunAsync(_workingDirectory, arguments, readOnly: true, GitProcess.ReadTimeout, ct);
}
