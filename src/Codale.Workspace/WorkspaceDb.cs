using Codale.Core.Projects;
using Codale.Db;

namespace Codale.Workspace;

/// <summary>Shares the workspace's database lease across stores and releases the lock for tool server processes.</summary>
public static class WorkspaceDb
{
    internal static WorkspaceDatabasePool Shared { get; } = new();

    private sealed record Owner(string Repository);

    public static string DirectoryFor(string projectPath) => DirectoryFor(projectPath, s_root.Value);

    private static readonly Lazy<string> s_root = new(() =>
    {
        var root = CodalePaths.WorkspaceDirectory;
        MoveLegacyRoot(Path.Combine(Path.GetDirectoryName(CodalePaths.DataRoot)!, "workspace"), root);
        return root;
    });

    /// <summary>
    /// Workspace databases used to live in a <c>workspace</c> folder beside, not inside, the Codale
    /// data root. Moves that folder in once, if nothing is at the new place yet; on failure (a
    /// database still open elsewhere) the old one is left as it is and the indexes rebuild.
    /// </summary>
    internal static void MoveLegacyRoot(string legacy, string root)
    {
        try
        {
            if (!Directory.Exists(legacy) || Directory.Exists(root)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            Directory.Move(legacy, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string DirectoryFor(string projectPath, string root)
    {
        var repository = ProjectPaths.RepositoryIdentity(projectPath);
        var project = Path.GetFileName(repository).Equals(".git", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(repository)! : repository;
        var name = ProjectPaths.WorkspaceName(project);

        for (var suffix = 1; ; suffix++)
        {
            var directory = Path.Combine(root, suffix == 1 ? name : FormattableString.Invariant($"{name}_{suffix}"));
            var claimed = Use(directory, db =>
            {
                var owner = db.Record<Owner>().Key("repository");
                if (owner.Get() is { } saved)
                    return string.Equals(saved.Repository, repository, StringComparison.OrdinalIgnoreCase);

                owner.Value(new(repository));
                return true;
            });
            if (claimed) return directory;
        }
    }

    public static void Use(string directory, Action<CodaleDatabase> action) => Shared.UseRecords(directory, action);
    public static T Use<T>(string directory, Func<CodaleDatabase, T> work) => Shared.UseRecords(directory, work);
}
