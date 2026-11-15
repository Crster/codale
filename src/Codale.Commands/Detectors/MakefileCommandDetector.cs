using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class MakefileCommandDetector : IProjectCommandDetector
{
    public string Id => "Makefile";
    private static readonly Regex MakefileTargetRegex = new(@"^([A-Za-z0-9][A-Za-z0-9_.-]*)\s*:(?![=])", RegexOptions.CultureInvariant);

    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = FirstExisting(root, "Makefile", "makefile", "GNUmakefile");
        if (path is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ReadTextSafe(path).Split('\n'))
        {
            var match = MakefileTargetRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var target = match.Groups[1].Value;
            if (!IsSafeName(target) || !seen.Add(target))
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = target,
                Command = $"make {target}",
                Source = CommandSource.Makefile,
                SourcePath = path,
            });
        }

        return commands;
    }

}
