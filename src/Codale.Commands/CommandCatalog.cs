using Codale.Workspace;

namespace Codale.Commands;

/// <summary>Compatibility entry point; application hosts inject IProjectCommandService.</summary>
public static class CommandCatalog
{
    public static Func<string, string> WorkspaceDirectory { get; set; } = WorkspaceDb.DirectoryFor;
    private static IProjectCommandService Service => new ProjectCommandService(WorkspaceDirectory);
    public static IReadOnlyList<ProjectCommand> Discover(string root) => Service.Discover(root);
    public static IReadOnlyList<ProjectCommand> Saved(string root) => Service.Saved(root);
    public static ConfigStore ConfigOf(string root) => Service.ConfigOf(root);
    public static ProjectCommand Add(string root, string name, string command, string? workingDirectory = null) => Service.Add(root, name, command, workingDirectory);
    public static ProjectCommand Save(string root, string? originalName, ProjectCommandEdit edit) => Service.Save(root, originalName, edit);
    public static bool Remove(string root, string name) => Service.Remove(root, name);
}
