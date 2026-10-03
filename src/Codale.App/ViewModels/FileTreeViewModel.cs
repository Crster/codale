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
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
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
        var node = new FileNode
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            FullPath = path,
            IsDirectory = true,
            IsRootEntry = isRootEntry,
        };

        node.Children.Add(Placeholder(path));
        return node;
    }

    /// <summary>A folder added to the workspace beside the project root; its name carries the parent so two "src" folders stay apart.</summary>
    public static FileNode ForWorkspaceFolder(string path)
    {
        var node = new FileNode
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            FullPath = path,
            IsDirectory = true,
            IsWorkspaceRoot = true,
            IsRootEntry = true,
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

public sealed partial class FileTreeViewModel : ObservableObject
{
    /// <summary>The most recent git snapshot, re-applied whenever more of the tree loads.</summary>
    private GitSnapshot? _snapshot;

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
            extraFolders.Changed += (_, _) => SyncWorkspaceFolders();
        }
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
    /// Colours the tree from a fresh snapshot. Only the nodes already loaded are
    /// touched; folders that expand later are covered by <see cref="RefreshGitStatus"/>.
    /// </summary>
    public void ApplyGitStatus(GitSnapshot snapshot)
    {
        _snapshot = snapshot.IsRepository ? snapshot : null;
        RefreshGitStatus();
    }

    /// <summary>Re-applies the last snapshot, after lazily loading more of the tree.</summary>
    public void RefreshGitStatus()
    {
        if (_snapshot is not { IsRepository: true } snapshot)
        {
            foreach (var node in Nodes)
            {
                Walk(node, null, null);
            }

            return;
        }

        var map = GitStatusMap.Build(snapshot.Changes, snapshot.Ignored);
        var repositoryRoot = snapshot.RepositoryRoot ?? ProjectPath;
        foreach (var node in Nodes)
        {
            Walk(node, map, repositoryRoot);
        }
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
