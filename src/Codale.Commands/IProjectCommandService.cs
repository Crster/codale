using Codale.Workspace;

namespace Codale.Commands;

public interface IProjectCommandService
{
    IReadOnlyList<ProjectCommand> Discover(string projectRoot);
    ConfigStore ConfigOf(string projectRoot);
    ProjectCommand Add(string projectRoot, string name, string command, string? workingDirectory = null);
}
