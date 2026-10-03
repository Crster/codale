namespace Codale.Git;

/// <summary>
/// Maps working-tree paths onto the status a file tree should show for them: the
/// entry's own change, the colour a folder inherits from changed descendants, and
/// whether git ignores the entry outright.
/// </summary>
/// <remarks>
/// Paths are repository-relative with forward slashes, exactly as git reports them.
/// Everything is compared case-insensitively because the tree paths it will be matched
/// against come from the Windows filesystem.
/// </remarks>
public sealed class GitStatusMap
{
    private readonly Dictionary<string, GitChangeKind> _files;
    private readonly Dictionary<string, GitChangeKind> _folders;
    private readonly Dictionary<string, GitChangeKind> _collapsed;
    private readonly HashSet<string> _ignoredFiles;
    private readonly HashSet<string> _ignoredDirs;

    private GitStatusMap(
        Dictionary<string, GitChangeKind> files,
        Dictionary<string, GitChangeKind> folders,
        Dictionary<string, GitChangeKind> collapsed,
        HashSet<string> ignoredFiles,
        HashSet<string> ignoredDirs)
    {
        _files = files;
        _folders = folders;
        _collapsed = collapsed;
        _ignoredFiles = ignoredFiles;
        _ignoredDirs = ignoredDirs;
    }

    /// <summary>
    /// Indexes a status listing for lookup. Ignored paths carry a trailing slash when
    /// git is collapsing a directory to one entry; the slash is what says "directory",
    /// so it is read before being stripped.
    /// </summary>
    public static GitStatusMap Build(IReadOnlyList<GitFileStatus> changes, IReadOnlyList<string> ignoredPaths)
    {
        var files = new Dictionary<string, GitChangeKind>(Ordinal);
        var folders = new Dictionary<string, GitChangeKind>(Ordinal);
        var collapsed = new Dictionary<string, GitChangeKind>(Ordinal);
        var ignoredFiles = new HashSet<string>(Ordinal);
        var ignoredDirs = new HashSet<string>(Ordinal);

        foreach (var change in changes)
        {
            var path = Normalize(change.Path, out var isDirectory);
            var kind = change.DisplayKind;

            if (isDirectory)
            {
                // An untracked directory arrives collapsed to "dir/"; everything the
                // tree finds inside it is untracked too, so descendants inherit.
                Merge(folders, path, kind);
                collapsed[path] = kind;
            }
            else
            {
                Merge(files, path, kind);

                // Each ancestor folder shows the most severe change beneath it.
                for (var parent = ParentOf(path); parent is not null; parent = ParentOf(parent))
                {
                    Merge(folders, parent, kind);
                }
            }
        }

        foreach (var entry in ignoredPaths)
        {
            var path = Normalize(entry, out var isDirectory);

            if (isDirectory)
            {
                ignoredDirs.Add(path);
            }
            else
            {
                ignoredFiles.Add(path);
            }
        }

        return new GitStatusMap(files, folders, collapsed, ignoredFiles, ignoredDirs);
    }

    /// <summary>
    /// The state to show for a node: its own change, an inherited one, and whether it
    /// is ignored. A change always wins over muting, and an ignored directory mutes
    /// everything beneath it even though git only names the directory itself.
    /// </summary>
    public (GitChangeKind? Status, bool Ignored) Classify(string relativePath)
    {
        if (_files.TryGetValue(relativePath, out var status) || _folders.TryGetValue(relativePath, out status))
        {
            return (status, false);
        }

        return (InheritedStatus(relativePath), IsIgnored(relativePath));
    }

    /// <summary>Status a node inherits from a collapsed directory above it, if any.</summary>
    private GitChangeKind? InheritedStatus(string relativePath)
    {
        for (var parent = ParentOf(relativePath); parent is not null; parent = ParentOf(parent))
        {
            if (_collapsed.TryGetValue(parent, out var kind))
            {
                return kind;
            }
        }

        return null;
    }

    private bool IsIgnored(string relativePath)
    {
        if (_ignoredFiles.Contains(relativePath) || _ignoredDirs.Contains(relativePath))
        {
            return true;
        }

        for (var parent = ParentOf(relativePath); parent is not null; parent = ParentOf(parent))
        {
            if (_ignoredDirs.Contains(parent))
            {
                return true;
            }
        }

        return false;
    }

    private static void Merge(Dictionary<string, GitChangeKind> target, string path, GitChangeKind kind)
    {
        if (!target.TryGetValue(path, out var existing) || Rank(kind) < Rank(existing))
        {
            target[path] = kind;
        }
    }

    /// <summary>
    /// Which of two kinds a folder should be coloured by when both apply: conflicts
    /// first, then deletions, then the kinds that mean new work, then ordinary edits.
    /// </summary>
    private static int Rank(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Conflicted => 0,
        GitChangeKind.Deleted => 1,
        GitChangeKind.Added => 2,
        GitChangeKind.Untracked => 3,
        GitChangeKind.Modified => 4,
        GitChangeKind.Renamed => 5,
        GitChangeKind.Copied => 6,
        _ => 7,
    };

    private static string Normalize(string path, out bool isDirectory)
    {
        isDirectory = path.EndsWith('/');
        return isDirectory ? path[..^1] : path;
    }

    private static string? ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : null;
    }

    private static StringComparer Ordinal => StringComparer.OrdinalIgnoreCase;
}
