using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class GoCommandDetector : IProjectCommandDetector
{
    public string Id => "Go";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "go.mod");
        if (!File.Exists(path))
        {
            return [];
        }

        return PresetCommands(CommandSource.Go, path,
            ("run", "go run ."),
            ("build", "go build ./..."),
            ("test", "go test ./..."));
    }

}
