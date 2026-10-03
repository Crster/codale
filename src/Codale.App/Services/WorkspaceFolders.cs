using System.Text.Json;

using Codale.Core.Projects;
using Codale.Storage;

namespace Codale.App.Services;

/// <summary>
/// Folders added to a project beyond its root - a backend next to the frontend that is
/// open. They show in the Files panel and are granted to the agent with --add-dir, so
/// its permissions treat them as part of the workspace. Persisted per project and
/// re-read on every access; a change applies to the next session start.
/// </summary>
public sealed class WorkspaceFolders
{
    private const string Key = "workspace.extra-folders";

    private readonly CodaleStore _store;
    private readonly string _projectPath;

    public WorkspaceFolders(CodaleStore store, string projectPath)
    {
        _store = store;
        _projectPath = projectPath;
    }

    public event EventHandler? Changed;

    /// <summary>The added folders that still exist on disk, in the order they were added.</summary>
    public IReadOnlyList<string> Folders => Read().Where(Directory.Exists).ToList();

    /// <summary>False for a folder that is missing, already covered by the project root or another added folder.</summary>
    public bool Add(string path)
    {
        var folder = ProjectPaths.Normalize(path);
        var current = Read();

        if (!Directory.Exists(folder) || IsCovered(folder, current.Prepend(_projectPath)))
        {
            return false;
        }

        // Adding a parent absorbs the folders beneath it.
        current.RemoveAll(existing => IsWithin(existing, folder));
        current.Add(folder);
        Write(current);
        return true;
    }

    public void Remove(string path)
    {
        var folder = ProjectPaths.Normalize(path);
        var current = Read();

        if (current.RemoveAll(existing => string.Equals(existing, folder, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Write(current);
        }
    }

    private static bool IsCovered(string folder, IEnumerable<string> roots) =>
        roots.Any(root => IsWithin(folder, root));

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || !(relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative));
    }

    private List<string> Read()
    {
        try
        {
            return _store.GetUiState(_projectPath, Key) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<List<string>>(json) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write(List<string> folders)
    {
        _store.SetUiState(_projectPath, Key, JsonSerializer.Serialize(folders));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
