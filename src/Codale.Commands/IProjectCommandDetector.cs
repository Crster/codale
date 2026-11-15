namespace Codale.Commands;

/// <summary>Discovers commands from one configuration format or ecosystem.</summary>
public interface IProjectCommandDetector
{
    string Id { get; }
    IReadOnlyList<ProjectCommand> Discover(string projectRoot);
}

internal sealed class ProjectCommandDetector(string id, Func<string, IReadOnlyList<ProjectCommand>> discover) : IProjectCommandDetector
{
    public string Id { get; } = id;
    public IReadOnlyList<ProjectCommand> Discover(string projectRoot) => discover(projectRoot);
}
