using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class MavenCommandDetector : IProjectCommandDetector
{
    public string Id => "Maven";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "pom.xml");
        if (!File.Exists(path))
        {
            return [];
        }

        var exe = File.Exists(Path.Combine(root, "mvnw.cmd")) || File.Exists(Path.Combine(root, "mvnw"))
            ? @".\mvnw"
            : "mvn";
        var pomText = ReadTextSafe(path);
        var commands = new List<ProjectCommand>();

        if (pomText.Contains("spring-boot-maven-plugin", StringComparison.OrdinalIgnoreCase))
        {
            commands.Add(new ProjectCommand
            {
                Name = "spring-boot:run",
                Command = $"{exe} spring-boot:run",
                Source = CommandSource.Maven,
                SourcePath = path,
            });
        }

        commands.AddRange(PresetCommands(CommandSource.Maven, path,
            ("test", $"{exe} test"),
            ("package", $"{exe} package"),
            ("clean install", $"{exe} clean install")));

        return commands;
    }

}
