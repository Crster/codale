using Codale.Core.Projects;
using Codale.Db;

namespace Codale.Workspace;

/// <summary>
/// The workspace's own database, kept in Codale's data folder and never in the workspace itself, so the
/// project tree stays clean. It houses four things: <c>history</c> (every chat's log, tool calls included),
/// <c>memory</c> (notes the agent saves and recalls), <c>config</c> (what used to live in <c>.codale/</c>) and
/// <c>env</c> (secrets, encrypted per value with a key only this Windows account can unwrap).
/// </summary>
/// <remarks>
/// The app and the agents' tool servers are separate processes and one process owns a database folder at a
/// time, so nothing holds it for long: <see cref="Use{T}"/> opens it for one operation and keeps it a moment
/// afterwards (a burst of writes does not reopen it each time), but never longer than a second and a half, so
/// another process always gets its turn.
/// </remarks>
public static class WorkspaceDb
{
    internal static WorkspaceDatabasePool Shared { get; } = new();

    public static string DirectoryFor(string projectPath) =>
        Path.Combine(ProjectPaths.KnowledgeDirectory(projectPath), "workspace-db");

    public static void Use(string directory, Action<CodaleDatabase> action) => Shared.UseRecords(directory, action);
    public static T Use<T>(string directory, Func<CodaleDatabase, T> work) => Shared.UseRecords(directory, work);
}
