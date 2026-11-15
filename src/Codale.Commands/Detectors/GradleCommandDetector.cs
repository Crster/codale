using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class GradleCommandDetector : IProjectCommandDetector
{
    public string Id => "Gradle";
    private static readonly Regex GradleTaskRegex = new(
        @"^\s*(?:tasks\.register\s*(?:<[^>]+>)?\s*\(\s*[""']([A-Za-z0-9_.-]+)[""']|task\s+([A-Za-z0-9_.-]+)\b|task\s*\(\s*[""']([A-Za-z0-9_.-]+)[""']\s*\))",
        RegexOptions.CultureInvariant);

    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = FirstExisting(root, "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts");
        if (path is null)
        {
            return [];
        }

        var exe = File.Exists(Path.Combine(root, "gradlew.bat")) || File.Exists(Path.Combine(root, "gradlew"))
            ? @".\gradlew"
            : "gradle";

        var buildText = ReadTextSafe(FirstExisting(root, "build.gradle", "build.gradle.kts") ?? path);
        var hasSpringBoot = buildText.Contains("org.springframework.boot", StringComparison.OrdinalIgnoreCase)
            || buildText.Contains("bootRun", StringComparison.Ordinal);
        var hasApplication = buildText.Contains("application", StringComparison.Ordinal)
            || buildText.Contains("mainClass", StringComparison.Ordinal);

        var commands = new List<ProjectCommand>();
        if (hasSpringBoot)
        {
            commands.Add(new ProjectCommand { Name = "bootRun", Command = $"{exe} bootRun", Source = CommandSource.Gradle, SourcePath = path });
        }
        else if (hasApplication)
        {
            commands.Add(new ProjectCommand { Name = "run", Command = $"{exe} run", Source = CommandSource.Gradle, SourcePath = path });
        }

        commands.AddRange(PresetCommands(CommandSource.Gradle, path,
            ("build", $"{exe} build"),
            ("test", $"{exe} test"),
            ("clean", $"{exe} clean")));

        var seen = new HashSet<string>(commands.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var line in buildText.Split('\n'))
        {
            var match = GradleTaskRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var taskName = match.Groups[1].Value is { Length: > 0 } g1 ? g1
                : match.Groups[2].Value is { Length: > 0 } g2 ? g2
                : match.Groups[3].Value;
            if (IsSafeName(taskName) && seen.Add(taskName))
            {
                commands.Add(new ProjectCommand
                {
                    Name = taskName,
                    Command = $"{exe} {taskName}",
                    Source = CommandSource.Gradle,
                    SourcePath = path,
                });
            }
        }

        return commands;
    }

}
