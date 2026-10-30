using System.Text.Json;

using Codale.Core.Projects;
using Codale.Storage;

namespace Codale.App.Services;

/// <summary>
/// Folders added to a project beyond its root - a backend next to the frontend that is
/// open. They show in the Files panel and are granted to the agent with --add-dir, so
/// its permissions treat them as part of the workspace. Deliberately not persisted: they
/// live for this window only and have to be attached again after Codale reopens. A change
/// reaches a Claude chat on its next turn (the CLI restarts) and a Codex chat on its next turn.
/// </summary>
public sealed class WorkspaceFolders
{
    private readonly object _gate = new();
    private readonly List<string> _folders = [];

    private readonly CodaleStore _store;
    private readonly string _projectPath;

    public WorkspaceFolders(CodaleStore store, string projectPath)
    {
        _store = store;
        _projectPath = projectPath;
    }

    public event EventHandler? Changed;

    /// <summary>The added folders that still exist on disk, in the order they were added.</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate)
            {
                return _folders.Where(Directory.Exists).ToList();
            }
        }
    }

    /// <summary>False for a folder that is missing, already covered by the project root or another added folder.</summary>
    public bool Add(string path)
    {
        var folder = ProjectPaths.Normalize(path);

        lock (_gate)
        {
            if (!Directory.Exists(folder) || IsCovered(folder, _folders.Prepend(_projectPath)))
            {
                return false;
            }

            // Adding a parent absorbs the folders beneath it.
            _folders.RemoveAll(existing => IsWithin(existing, folder));
            _folders.Add(folder);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Remove(string path)
    {
        var folder = ProjectPaths.Normalize(path);

        lock (_gate)
        {
            if (_folders.RemoveAll(existing => string.Equals(existing, folder, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private const string RecentKey = "workspace.recent-cwds";
    private const int MaxRecent = 6;

    /// <summary>Folders picked through "Choose folder…" for the status bar shell, newest first.</summary>
    public IReadOnlyList<string> RecentCwds
    {
        get
        {
            try
            {
                return _store.GetUiState(_projectPath, RecentKey) is { Length: > 0 } json
                    ? (JsonSerializer.Deserialize<List<string>>(json) ?? []).Where(Directory.Exists).ToList()
                    : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public void AddRecentCwd(string path)
    {
        var folder = ProjectPaths.Normalize(path);
        var recent = RecentCwds.Where(r => !string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)).Prepend(folder).Take(MaxRecent);
        _store.SetUiState(_projectPath, RecentKey, JsonSerializer.Serialize(recent));
    }

    private static bool IsCovered(string folder, IEnumerable<string> roots) =>
        roots.Any(root => IsWithin(folder, root));

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || !(relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative));
    }
}
