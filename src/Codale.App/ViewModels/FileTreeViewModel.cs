using System.Collections.ObjectModel;

using Codale.App.Services;
using Codale.Git;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// A node in the project tree. Children are loaded on expand rather than up front:
/// a repo with a node_modules in it would otherwise stall the window on open.
/// </summary>
public sealed partial class FileNode : ObservableObject
{
    /// <summary>
    /// Only git's own store is hidden. Everything else - node_modules, bin, obj - is
    /// shown, faded when .gitignore covers it; loading on expand keeps big ones cheap.
    /// </summary>
    internal static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
    };

    /// <summary>
    /// WinUI's bound-mode TreeView draws the expander chevron only when a node already
    /// has children, so a folder we have not read yet would look like a leaf and could
    /// never be expanded. Seeding one placeholder keeps the chevron while staying lazy.
    /// </summary>
    private static FileNode Placeholder(string parentPath) => new()
    {
        Name = "Loading…",
        FullPath = parentPath,
        IsDirectory = false,
        IsPlaceholder = true,
    };

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }

    public bool IsPlaceholder { get; init; }

    /// <summary>True for a folder the user added to the workspace; it is listed as a root beside the project.</summary>
    public bool IsWorkspaceRoot { get; init; }

    /// <summary>
    /// A folder shown as a top-level root beside others (the project and each added
    /// folder) rather than as one of the project's entries. It starts expanded.
    /// </summary>
    public bool IsRootEntry { get; init; }

    /// <summary>
    /// Whether this node is expanded in the tree. Two-way bound to the TreeViewItem so
    /// expanding in code reflects in the UI and vice versa.
    /// </summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && !IsLoaded)
        {
            LoadChildren();
        }
    }

    /// <summary>
    /// This node's git state, folders included - a folder shows the most severe change
    /// beneath it. Null means clean, which leaves the name in the theme's own colour.
    /// </summary>
    [ObservableProperty]
    public partial GitChangeKind? GitStatus { get; set; }

    /// <summary>True when git ignores this node, or it lives inside an ignored folder.</summary>
    [ObservableProperty]
    public partial bool IsIgnored { get; set; }

    public ObservableCollection<FileNode> Children { get; } = [];

    public bool IsLoaded { get; private set; }

    public static FileNode ForDirectory(string path, bool isRootEntry = false)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        var node = new FileNode
        {
            Name = string.IsNullOrEmpty(name) ? path : name,
            FullPath = path,
            IsDirectory = true,
            IsRootEntry = isRootEntry,
        };

        if (isRootEntry)
        {
            node.IsExpanded = true;
            node.LoadChildren();
        }
        else
        {
            node.Children.Add(Placeholder(path));
        }

        return node;
    }

    /// <summary>A folder added to the workspace beside the project root; its name carries the parent so two "src" folders stay apart.</summary>
    public static FileNode ForWorkspaceFolder(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        var node = new FileNode
        {
            Name = string.IsNullOrEmpty(name) ? path : name,
            FullPath = path,
            IsDirectory = true,
            IsWorkspaceRoot = true,
            IsRootEntry = true,
            IsExpanded = true,
        };

        // Shown expanded, so read it now rather than on an expand that never happens.
        node.LoadChildren();
        return node;
    }

    public static FileNode ForFile(string path) => new()
    {
        Name = Path.GetFileName(path),
        FullPath = path,
        IsDirectory = false,
    };

    public void LoadChildren()
    {
        if (IsLoaded || !IsDirectory)
        {
            return;
        }

        IsLoaded = true;
        ReplaceWith(Children, EnumerateChildren(FullPath));
    }

    private static void ReplaceWith(ObservableCollection<FileNode> target, List<FileNode> source)
    {
        target.Clear();
        foreach (var child in source)
        {
            target.Add(child);
        }
    }

    /// <summary>One directory read, in display order: folders first, then files.</summary>
    internal static List<FileNode> EnumerateChildren(string fullPath) =>
        EnumerateEntries(fullPath).Select(Create).ToList();

    internal static FileNode Create((string Path, bool IsDirectory) entry) =>
        entry.IsDirectory ? ForDirectory(entry.Path) : ForFile(entry.Path);

    /// <summary>
    /// The names in one directory, in display order, without building nodes: a rescan
    /// only wants node objects for what is new.
    /// </summary>
    internal static List<(string Path, bool IsDirectory)> EnumerateEntries(string fullPath)
    {
        var loaded = new List<(string Path, bool IsDirectory)>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fullPath)
                         .Where(d => !Skip.Contains(Path.GetFileName(d)))
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                loaded.Add((dir, true));
            }

            foreach (var file in Directory.EnumerateFiles(fullPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                loaded.Add((file, false));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A folder we cannot read simply shows as empty.
        }

        return loaded;
    }
}

/// <summary>One row of the file filter's flat result list.</summary>
public sealed class FileSearchHit
{
    public required FileNode Node { get; init; }

    /// <summary>Where the file lives, relative to the folder that was searched; empty at its top level.</summary>
    public required string Directory { get; init; }

    public string Name => Node.Name;

    public string FullPath => Node.FullPath;

    public string DirectoryTail => Directory.Length == 0 ? "" : $" {Directory}";
}

public sealed partial class FileTreeViewModel : ObservableObject, IDisposable
{
    /// <summary>The filter lists at most this many files; a query that broad should be narrowed.</summary>
    private const int MaxSearchResults = 300;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    private readonly List<FileSystemWatcher> _watchers = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchTimer;
    private CancellationTokenSource? _searchCts;
    private int _searchStamp;
    private bool _disposed;

    /// <summary>The status map of the last snapshot, kept so search results can be coloured and pruned.</summary>
    private GitStatusMap? _lastMap;
    private string? _lastMapRoot;

    /// <summary>The most recent git snapshot, re-applied whenever more of the tree loads.</summary>
    private GitSnapshot? _snapshot;

    /// <summary>Repositories found in sub folders when the project root is not one.</summary>
    private IReadOnlyList<(string Root, GitStatusMap Map)> _nested = [];

    private readonly WorkspaceFolders? _extraFolders;

    public FileTreeViewModel(string projectPath, WorkspaceFolders? extraFolders = null)
    {
        ProjectPath = projectPath;
        Root = FileNode.ForDirectory(projectPath, isRootEntry: true);
        Root.LoadChildren();

        _extraFolders = extraFolders;
        SyncWorkspaceFolders();

        if (extraFolders is not null)
        {
            extraFolders.Changed += (_, _) =>
            {
                SyncWorkspaceFolders();
                SyncWatchers();
                RefreshSearch();
            };
        }

        SyncWatchers();
    }

    /// <summary>What the user typed in the file filter; blank shows the tree.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>True while a filter is typed: the flat result list replaces the tree.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    /// <summary>True once a filter has finished and found nothing.</summary>
    [ObservableProperty]
    public partial bool HasNoResults { get; set; }

    public ObservableCollection<FileSearchHit> SearchResults { get; } = [];

    partial void OnSearchTextChanged(string value)
    {
        var searching = !string.IsNullOrWhiteSpace(value);
        IsSearching = searching;

        if (!searching)
        {
            CancelSearch();
            SearchResults.Clear();
            HasNoResults = false;
            return;
        }

        // A search still running answers the old query, so it must not land; the previous
        // results stay up meanwhile instead of flashing empty, and typing is debounced.
        _searchCts?.Cancel();
        _searchStamp++;
        ScheduleSearch(150);
    }

    private void CancelSearch()
    {
        _searchTimer?.Stop();
        _searchCts?.Cancel();
        _searchStamp++;
    }

    /// <summary>Re-runs the filter against the disk now, if one is active.</summary>
    public void RefreshSearch()
    {
        if (IsSearching)
        {
            ScheduleSearch(0);
        }
    }

    private void ScheduleSearch(int delayMs)
    {
        if (_dispatcher is null)
        {
            _ = RunSearchAsync();
            return;
        }

        _searchTimer ??= CreateOneShot(() => _ = RunSearchAsync());
        _searchTimer.Stop();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        _searchTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateOneShot(Action tick)
    {
        var timer = _dispatcher!.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private IReadOnlyList<string> SearchRoots =>
    [
        ProjectPath,
        .. (_extraFolders?.Folders ?? []).Where(folder =>
            !string.Equals(folder, ProjectPath, StringComparison.OrdinalIgnoreCase)),
    ];

    private async Task RunSearchAsync()
    {
        var query = SearchText;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var stamp = ++_searchStamp;

        var roots = SearchRoots;
        var map = _lastMap;
        var mapRoot = _lastMapRoot;

        List<(string Path, string Directory)> found;
        try
        {
            found = await Task.Run(() => SearchDisk(roots, query, map, mapRoot, cts.Token), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // A newer keystroke, or a cleared box, owns the list now.
        if (stamp != _searchStamp || !IsSearching)
        {
            return;
        }

        ApplySearchResults(found);
        HasNoResults = SearchResults.Count == 0;
    }

    /// <summary>
    /// Walks every folder on disk - not only the ones the tree has opened - for files
    /// whose path holds every word of the query. Ignored folders (node_modules, bin) and
    /// git's own store are not entered; links to folders are not followed.
    /// </summary>
    private static List<(string Path, string Directory)> SearchDisk(
        IReadOnlyList<string> roots, string query, GitStatusMap? map, string? mapRoot, CancellationToken ct)
    {
        // Spaces carry no meaning in a fuzzy pattern; the characters just have to follow in order.
        var pattern = query.Replace('\\', '/').ToLowerInvariant().Replace(" ", "");
        var whole = pattern;
        var matches = new List<(string Path, string Directory, bool NameHit)>();
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = pending.Pop();

                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(current).EnumerateFileSystemInfos("*", options);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry is DirectoryInfo)
                    {
                        if (FileNode.Skip.Contains(entry.Name) ||
                            entry.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                            IsIgnoredDirectory(entry.FullName, map, mapRoot))
                        {
                            continue;
                        }

                        pending.Push(entry.FullName);
                        continue;
                    }

                    var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                    var lower = relative.ToLowerInvariant();
                    if (!IsFuzzyMatch(lower, pattern))
                    {
                        continue;
                    }

                    var slash = relative.LastIndexOf('/');
                    var directory = slash > 0 ? relative[..slash] : "";
                    matches.Add((entry.FullName, directory, IsFuzzyMatch(entry.Name.ToLowerInvariant(), whole)));
                }
            }
        }

        return matches
            .OrderBy(m => m.NameHit ? 0 : 1)
            .ThenBy(m => Path.GetFileName(m.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSearchResults)
            .Select(m => (m.Path, m.Directory))
            .ToList();
    }

    /// <summary>Sublime-style: every pattern character appears in the text, in order, not necessarily adjacent.</summary>
    private static bool IsFuzzyMatch(string text, string pattern)
    {
        var at = 0;
        foreach (var c in pattern)
        {
            at = text.IndexOf(c, at);
            if (at < 0)
            {
                return false;
            }

            at++;
        }

        return true;
    }

    private static bool IsIgnoredDirectory(string fullPath, GitStatusMap? map, string? mapRoot)
    {
        if (map is null || mapRoot is null ||
            !fullPath.StartsWith(mapRoot.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return map.Classify(Path.GetRelativePath(mapRoot, fullPath).Replace('\\', '/')).Ignored;
    }

    /// <summary>
    /// Brings the visible list to the new result set with the fewest moves, keeping the
    /// rows that survive, so a rescan while the filter is up does not flicker or lose scroll.
    /// </summary>
    private void ApplySearchResults(List<(string Path, string Directory)> found)
    {
        var wanted = found.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = SearchResults.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(SearchResults[i].FullPath))
            {
                SearchResults.RemoveAt(i);
            }
        }

        for (var i = 0; i < found.Count; i++)
        {
            if (i < SearchResults.Count &&
                string.Equals(SearchResults[i].FullPath, found[i].Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existing = -1;
            for (var j = i + 1; j < SearchResults.Count; j++)
            {
                if (string.Equals(SearchResults[j].FullPath, found[i].Path, StringComparison.OrdinalIgnoreCase))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                SearchResults.Move(existing, i);
            }
            else
            {
                SearchResults.Insert(i, new FileSearchHit
                {
                    Node = FileNode.ForFile(found[i].Path),
                    Directory = found[i].Directory,
                });
            }
        }

        ClassifySearchResults();
    }

    /// <summary>Colours the result rows from the current git map, the way tree rows are.</summary>
    private void ClassifySearchResults()
    {
        foreach (var hit in SearchResults)
        {
            if (_lastMap is { } map && _lastMapRoot is { } root &&
                hit.FullPath.StartsWith(root.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
            {
                var (status, ignored) = map.Classify(Path.GetRelativePath(root, hit.FullPath).Replace('\\', '/'));
                hit.Node.GitStatus = status;
                hit.Node.IsIgnored = ignored;
            }
            else
            {
                hit.Node.GitStatus = null;
                hit.Node.IsIgnored = false;
            }
        }
    }

    /// <summary>
    /// Watches the project and added folders for files appearing, vanishing or being
    /// renamed by anything outside this window - a terminal, another editor, a build.
    /// Events only restart a short timer; one rescan follows the burst.
    /// </summary>
    private void SyncWatchers()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();

        foreach (var root in SearchRoots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => OnDiskChanged(e.FullPath);
                watcher.Deleted += (_, e) => OnDiskChanged(e.FullPath);
                watcher.Renamed += (_, e) => OnDiskChanged(e.FullPath);

                // An overflowed buffer lost events; rescanning is the only way to catch up.
                watcher.Error += (_, _) => OnDiskChanged(null);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be watched is still covered by the manual refresh.
            }
        }
    }

    private void OnDiskChanged(string? path)
    {
        if (path is not null &&
            (path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("\\.git", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _dispatcher?.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            _watchTimer ??= CreateOneShot(Reload);
            _watchTimer.Stop();
            _watchTimer.Interval = TimeSpan.FromMilliseconds(500);
            _watchTimer.Start();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        CancelSearch();
        _watchTimer?.Stop();

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    /// <summary>True when the tree lists each folder as its own root instead of the project's entries.</summary>
    private bool HasRoots => Nodes.Contains(Root);

    /// <summary>
    /// Makes the tree match the saved list. With no added folders the project's entries
    /// sit at the top level; with some, the project and every added folder are roots.
    /// </summary>
    private void SyncWorkspaceFolders()
    {
        var wanted = _extraFolders?.Folders ?? [];

        if (wanted.Count == 0)
        {
            if (HasRoots || Nodes.Count == 0)
            {
                if (HasRoots)
                {
                    ReloadChildren(Root, Root.Children);
                }

                Nodes.Clear();
                foreach (var child in Root.Children)
                {
                    Nodes.Add(child);
                }
            }

            return;
        }

        if (!HasRoots)
        {
            Nodes.Clear();
            Nodes.Add(Root);
            ReloadChildren(Root, Root.Children);
        }

        for (var i = Nodes.Count - 1; i >= 0; i--)
        {
            if (Nodes[i].IsWorkspaceRoot && !wanted.Contains(Nodes[i].FullPath, StringComparer.OrdinalIgnoreCase))
            {
                Nodes.RemoveAt(i);
            }
        }

        foreach (var folder in wanted)
        {
            if (!Nodes.Any(node => string.Equals(node.FullPath, folder, StringComparison.OrdinalIgnoreCase)))
            {
                Nodes.Add(FileNode.ForWorkspaceFolder(folder));
            }
        }
    }

    public FileNode Root { get; }

    public string ProjectPath { get; }

    /// <summary>Top level entries: the project's own, or - with added folders - one root per folder.</summary>
    public ObservableCollection<FileNode> Nodes { get; } = [];

    /// <summary>
    /// Expands the folder hierarchy leading down to <paramref name="path"/>, ensuring every
    /// intermediate directory is loaded and marked expanded. Returns the matching node for the
    /// target (file or directory), or null if the path is outside the workspace or does not exist.
    /// </summary>
    public FileNode? RevealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = !Path.IsPathRooted(path)
                ? Path.GetFullPath(Path.Combine(ProjectPath, path))
                : Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }

        ObservableCollection<FileNode> currentCollection;
        FileNode? currentNode;
        string rootPath;

        if (HasRoots)
        {
            var matchingRoot = Nodes
                .Where(n => string.Equals(n.FullPath.TrimEnd('\\', '/'), fullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                            fullPath.StartsWith(n.FullPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            fullPath.StartsWith(n.FullPath.TrimEnd('\\', '/') + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.FullPath.Length)
                .FirstOrDefault();

            if (matchingRoot is null)
            {
                return null;
            }

            if (string.Equals(matchingRoot.FullPath.TrimEnd('\\', '/'), fullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return matchingRoot;
            }

            matchingRoot.IsExpanded = true;
            matchingRoot.LoadChildren();
            currentNode = matchingRoot;
            currentCollection = matchingRoot.Children;
            rootPath = matchingRoot.FullPath;
        }
        else
        {
            var projectRootTrimmed = ProjectPath.TrimEnd('\\', '/');
            var fullPathTrimmed = fullPath.TrimEnd('\\', '/');

            if (string.Equals(projectRootTrimmed, fullPathTrimmed, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!fullPath.StartsWith(projectRootTrimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith(projectRootTrimmed + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!Root.IsLoaded)
            {
                Root.LoadChildren();
            }

            currentNode = null;
            currentCollection = Nodes;
            rootPath = ProjectPath;
        }

        var relative = Path.GetRelativePath(rootPath, fullPath);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[0] == "..")
        {
            return null;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Length - 1;

            var match = currentCollection.FirstOrDefault(n => string.Equals(n.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                if (currentNode is not null)
                {
                    ReloadChildren(currentNode, currentCollection);
                }
                else
                {
                    ReloadChildren(Root, Nodes);
                }

                match = currentCollection.FirstOrDefault(n => string.Equals(n.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    return null;
                }
            }

            if (isLast)
            {
                RefreshGitStatus();
                return match;
            }

            if (!match.IsDirectory)
            {
                return null;
            }

            match.IsExpanded = true;
            match.LoadChildren();
            currentNode = match;
            currentCollection = match.Children;
        }

        RefreshGitStatus();
        return null;
    }

    /// <summary>
    /// Colours the tree from a fresh snapshot. Only the nodes already loaded are
    /// touched; folders that expand later are covered by <see cref="RefreshGitStatus"/>.
    /// </summary>
    public void ApplyGitStatus(GitSnapshot snapshot)
    {
        _snapshot = snapshot.IsRepository ? snapshot : null;
        RefreshGitStatus();
    }

    /// <summary>
    /// Colours the tree from the repositories that live in sub folders, for a project
    /// whose own root is not a repository (a parent holding an app and a server repo).
    /// </summary>
    public void ApplyNestedGitStatus(IReadOnlyList<GitSnapshot> snapshots)
    {
        _nested = [.. snapshots.Where(s => s is { IsRepository: true, RepositoryRoot: not null })
            .Select(s => (Root: s.RepositoryRoot!, Map: GitStatusMap.Build(s.Changes, s.Ignored)))];
        RefreshGitStatus();
    }

    /// <summary>The git state of one file from the last snapshot, or null when clean or outside the repository.</summary>
    public GitChangeKind? GitStatusOf(string fullPath)
    {
        if (_lastMap is not { } map || _lastMapRoot is not { } root ||
            !fullPath.StartsWith(root.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return map.Classify(Path.GetRelativePath(root, fullPath).Replace('\\', '/')).Status;
    }

    /// <summary>Re-applies the last snapshot, after lazily loading more of the tree.</summary>
    public void RefreshGitStatus()
    {
        if (_snapshot is not { IsRepository: true } snapshot)
        {
            _lastMap = null;
            _lastMapRoot = null;
            foreach (var node in Nodes)
            {
                WalkNested(node);
            }

            ClassifySearchResults();
            return;
        }

        var map = GitStatusMap.Build(snapshot.Changes, snapshot.Ignored);
        var repositoryRoot = snapshot.RepositoryRoot ?? ProjectPath;
        _lastMap = map;
        _lastMapRoot = repositoryRoot;
        foreach (var node in Nodes)
        {
            Walk(node, map, repositoryRoot);
        }

        ClassifySearchResults();
    }

    /// <summary>
    /// Rescans the tree in place. The agent creates and deletes files outside this
    /// process, and nothing else tells the tree about it: every directory that has
    /// been opened is re-read from disk, additions appear and deletions disappear.
    /// Existing nodes keep their instances - Add and Remove, never a Clear - so the
    /// TreeView's realised containers, expansion state and scroll all survive the
    /// pass. Never-opened folders stay lazy.
    /// </summary>
    public void Reload()
    {
        // Whatever changed on disk may change the filter's answer too; it re-reads the
        // disk itself, so a filtered list stays correct without falling back to the tree.
        RefreshSearch();

        if (!Root.IsLoaded)
        {
            return;
        }

        ReloadChildren(Root, HasRoots ? Root.Children : Nodes);

        foreach (var folder in Nodes.Where(node => node.IsWorkspaceRoot && node.IsLoaded).ToList())
        {
            ReloadChildren(folder, folder.Children);
        }

        RefreshGitStatus();
    }

    private static void ReloadChildren(FileNode directory, ObservableCollection<FileNode> displayed)
    {
        var fresh = FileNode.EnumerateEntries(directory.FullPath);
        var freshPaths = fresh.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = displayed.Count - 1; i >= 0; i--)
        {
            if (!freshPaths.Contains(displayed[i].FullPath))
            {
                displayed.RemoveAt(i);
            }
        }

        // Nodes (and their placeholders) are only built for entries the tree does not have yet.
        var keptPaths = displayed.Select(node => node.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in fresh)
        {
            if (keptPaths.Add(entry.Path))
            {
                InsertOrdered(displayed, FileNode.Create(entry));
            }
        }

        foreach (var child in displayed)
        {
            if (child.IsDirectory && child.IsLoaded)
            {
                ReloadChildren(child, child.Children);
            }
        }
    }

    private static void InsertOrdered(ObservableCollection<FileNode> target, FileNode node)
    {
        var index = 0;
        while (index < target.Count && ComesBefore(target[index], node))
        {
            index++;
        }

        target.Insert(index, node);
    }

    /// <summary>The same order the directory listing starts in: folders first, then by name.</summary>
    private static bool ComesBefore(FileNode left, FileNode right)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) < 0;
    }

    /// <summary>
    /// Walks nodes above any nested repository uncoloured, and hands each nested
    /// repository's folder to <see cref="Walk"/> with that repository's own status map.
    /// </summary>
    private void WalkNested(FileNode node)
    {
        foreach (var (root, map) in _nested)
        {
            if (string.Equals(node.FullPath.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || node.FullPath.StartsWith(root.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
            {
                Walk(node, map, root);
                return;
            }
        }

        node.GitStatus = null;
        node.IsIgnored = false;

        if (!node.IsDirectory)
        {
            return;
        }

        foreach (var child in node.Children.ToList())
        {
            if (!child.IsPlaceholder)
            {
                WalkNested(child);
            }
        }
    }

    /// <summary>
    /// Updates <paramref name="node"/> and everything below it - the walk stays within loaded
    /// directories, so a collapsed folder's children are not read from disk here.
    /// </summary>
    private static void Walk(FileNode node, GitStatusMap? map, string? repositoryRoot)
    {
        // The snapshot is the project's repository; an added folder is outside it.
        if (node.IsWorkspaceRoot)
        {
            map = null;
        }

        if (map is not null && repositoryRoot is not null)
        {
            // Status paths are repository-relative with forward slashes, whichever
            // folder of the repository is open in the tree.
            var relative = Path.GetRelativePath(repositoryRoot, node.FullPath).Replace('\\', '/');
            var (status, ignored) = map.Classify(relative);

            node.GitStatus = status;
            node.IsIgnored = ignored;
        }
        else
        {
            node.GitStatus = null;
            node.IsIgnored = false;
        }

        if (!node.IsDirectory)
        {
            return;
        }

        foreach (var child in node.Children.ToList())
        {
            if (child.IsPlaceholder)
            {
                continue;
            }

            if (IsGoneFromDisk(child, map, repositoryRoot))
            {
                node.Children.Remove(child);
                continue;
            }

            Walk(child, map, repositoryRoot);
        }
    }

    /// <summary>
    /// True when git reports the node deleted and it is really gone: external edits -
    /// a terminal, another editor - reach this panel only through the git poll, so the
    /// entry is dropped here rather than left red until the next manual refresh.
    /// </summary>
    private static bool IsGoneFromDisk(FileNode node, GitStatusMap? map, string? repositoryRoot)
    {
        if (map is null || repositoryRoot is null)
        {
            return false;
        }

        var relative = Path.GetRelativePath(repositoryRoot, node.FullPath).Replace('\\', '/');
        if (map.Classify(relative).Status != GitChangeKind.Deleted)
        {
            return false;
        }

        return node.IsDirectory ? !Directory.Exists(node.FullPath) : !File.Exists(node.FullPath);
    }
}