using Codale.Workspace;

namespace Codale.Commands;

public interface IProjectCommandService
{
    IReadOnlyList<ProjectCommand> Discover(string projectRoot);

    /// <summary>Only the project's own saved commands, without scanning the project's config files.</summary>
    IReadOnlyList<ProjectCommand> Saved(string projectRoot);

    ConfigStore ConfigOf(string projectRoot);
    ProjectCommand Add(string projectRoot, string name, string command, string? workingDirectory = null);

    /// <summary>Saves an edited command; <paramref name="originalName"/> is the name it had before a rename.</summary>
    ProjectCommand Save(string projectRoot, string? originalName, ProjectCommandEdit edit);

    /// <summary>Removes a saved command by name; false when there was none.</summary>
    bool Remove(string projectRoot, string name);
}
