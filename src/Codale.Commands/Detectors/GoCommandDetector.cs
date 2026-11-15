using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class GoCommandDetector : IProjectCommandDetector
{
    public string Id => "Go";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "go.mod");
        return !File.Exists(path)
            ? []
            : (IReadOnlyList<ProjectCommand>)PresetCommands(CommandSource.Go, path,
            ("run", "go run ."),
            ("build", "go build ./..."),
            ("test", "go test ./..."));
    }

}