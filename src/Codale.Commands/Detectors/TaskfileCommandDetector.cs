using System.Text.RegularExpressions;

using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed partial class TaskfileCommandDetector : IProjectCommandDetector
{
    public string Id => "Taskfile";
    [GeneratedRegex(@"^(\s*)([A-Za-z0-9_.:-]+)\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex YamlKeyRegex();

    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = FirstExisting(root, "Taskfile.yml", "Taskfile.yaml", "taskfile.yml", "taskfile.yaml");
        if (path is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inTasks = false;
        int? taskIndent = null;

        foreach (var rawLine in ReadTextSafe(path).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var match = YamlKeyRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var indent = match.Groups[1].Value.Length;
            var key = match.Groups[2].Value;

            if (indent == 0)
            {
                inTasks = key == "tasks";
                taskIndent = null;
                continue;
            }

            if (!inTasks)
            {
                continue;
            }

            taskIndent ??= indent;
            if (indent == taskIndent.Value && IsSafeName(key) && seen.Add(key))
            {
                commands.Add(new ProjectCommand
                {
                    Name = key,
                    Command = $"task {key}",
                    Source = CommandSource.Taskfile,
                    SourcePath = path,
                });
            }
        }

        return commands;
    }

}