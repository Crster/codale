using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class CargoCommandDetector : IProjectCommandDetector
{
    public string Id => "Cargo";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "Cargo.toml");
        if (!File.Exists(path))
        {
            return [];
        }

        var text = ReadTextSafe(path);
        var isPureWorkspace = text.Contains("[workspace]", StringComparison.Ordinal)
            && !text.Contains("[package]", StringComparison.Ordinal);

        var commands = new List<ProjectCommand>();
        if (!isPureWorkspace)
        {
            commands.Add(new ProjectCommand
            {
                Name = "run",
                Command = "cargo run",
                Source = CommandSource.Cargo,
                SourcePath = path,
            });
        }

        commands.AddRange(PresetCommands(CommandSource.Cargo, path,
            ("build", "cargo build"),
            ("test", "cargo test"),
            ("check", "cargo check"),
            ("clippy", "cargo clippy")));

        return commands;
    }

}
