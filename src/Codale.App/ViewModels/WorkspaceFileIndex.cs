namespace Codale.App.ViewModels;

/// <summary>
/// Builds the flat relative-path list behind the composer's @-file references.
/// A shallow, dependency-free walk: good enough to point the agent at a file (it
/// reads the real contents itself), and rebuilt per connect so worktree switches
/// stay honest.
/// </summary>
public static class WorkspaceFileIndex
{
    /// <summary>Directories that never belong in a mention list.</summary>
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        ".codale",
        ".nuget",
        "node_modules",
        "bin",
        "obj",
        "packages",
        "dist",
        "out",
        "build",
        ".pytest_cache",
        ".venv",
        "venv",
    };

    private const int MaxFiles = 2000;
    private const int MaxDepth = 12;

    /// <summary>Every file under <paramref name="root"/>, as '/'-separated relative paths, ordered for a stable popup.</summary>
    public static IReadOnlyList<string> Enumerate(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var files = new List<string>(MaxFiles);
        var pending = new Stack<(string path, int depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0 && files.Count < MaxFiles)
        {
            var (directory, depth) = pending.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);

                if (name.StartsWith('.'))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    if (depth < MaxDepth && !SkippedDirectories.Contains(name))
                    {
                        pending.Push((entry, depth + 1));
                    }

                    continue;
                }

                files.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));

                if (files.Count >= MaxFiles)
                {
                    break;
                }
            }
        }

        // Case-insensitive so the suggestion popup's grouping (name matches before
        // path matches) reads the way the user typed.
        return [.. files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
    }
}
