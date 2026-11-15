using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class DockerComposeCommandDetector : IProjectCommandDetector
{
    public string Id => "DockerCompose";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = FirstExisting(root, "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml");
        return path is null
            ? []
            : (IReadOnlyList<ProjectCommand>)PresetCommands(CommandSource.DockerCompose, path,
            ("up", "docker compose up"),
            ("up --build", "docker compose up --build"),
            ("down", "docker compose down"),
            ("logs", "docker compose logs -f"));
    }

}