using Codale.Core.Projects;
using Codale.Db;

namespace Codale.Workspace;

/// <summary>Shares the workspace's database lease across stores and releases the lock for tool server processes.</summary>
public static class WorkspaceDb
{
    internal static WorkspaceDatabasePool Shared { get; } = new();

    private sealed record Owner(string Repository);

    public static string DirectoryFor(string projectPath) => DirectoryFor(projectPath,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "workspace"));

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
