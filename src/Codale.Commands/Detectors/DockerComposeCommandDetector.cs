using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class DockerComposeCommandDetector : IProjectCommandDetector
{
    public string Id => "DockerCompose";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = FirstExisting(root, "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml");
        if (path is null)
        {
            return [];
        }

        return PresetCommands(CommandSource.DockerCompose, path,
            ("up", "docker compose up"),
            ("up --build", "docker compose up --build"),
            ("down", "docker compose down"),
            ("logs", "docker compose logs -f"));
    }

}
