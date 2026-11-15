using System.Text.Json;

using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class DenoCommandDetector : IProjectCommandDetector
{
    public string Id => "Deno";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = FirstExisting(root, "deno.json", "deno.jsonc");
        if (path is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        using var doc = ParseFile(path);
        if (doc?.RootElement.Prop("tasks") is { ValueKind: JsonValueKind.Object } tasks)
        {
            foreach (var task in tasks.EnumerateObject())
            {
                var hasCommand = (task.Value.ValueKind == JsonValueKind.String && task.Value.GetString() is { Length: > 0 })
                    || (task.Value.ValueKind == JsonValueKind.Object && task.Value.Str("command") is { Length: > 0 });
                if (!hasCommand || !IsSafeName(task.Name))
                {
                    continue;
                }

                commands.Add(new ProjectCommand
                {
                    Name = task.Name,
                    Command = $"deno task {task.Name}",
                    Source = CommandSource.Deno,
                    SourcePath = path,
                });
            }
        }

        if (commands.Count == 0)
        {
            commands.AddRange(PresetCommands(CommandSource.Deno, path,
                ("test", "deno test"),
                ("lint", "deno lint")));
        }

        return commands;
    }

}