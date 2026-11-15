using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class ClaudeCommandDetector : IProjectCommandDetector
{
    public string Id => "Claude";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, ".claude", "launch.json");
        using var doc = ParseFile(path);
        if (doc is null)
        {
            return [];
        }

        var rootElement = doc.RootElement;
        var entries = rootElement.ValueKind == JsonValueKind.Array
            ? rootElement.EnumerateArray()
            : rootElement.Items("commands", "configurations");

        var commands = new List<ProjectCommand>();
        foreach (var entry in entries)
        {
            if (CommandValue(entry, "command", "cmd", "run") is not { Length: > 0 } command)
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = entry.Str("name", "label", "title") ?? command.Trim(),
                Command = command,
                WorkingDirectory = entry.Str("cwd", "workingDirectory", "directory"),
                Source = CommandSource.Claude,
                SourcePath = path,
            });
        }

        return commands;
    }

}
