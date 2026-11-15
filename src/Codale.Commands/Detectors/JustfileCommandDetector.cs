using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class JustfileCommandDetector : IProjectCommandDetector
{
    public string Id => "Justfile";
    private static readonly Regex JustRecipeRegex = new(@"^@?([A-Za-z][A-Za-z0-9_-]*)(?:\s+[^:]*)?\s*:(?![=])", RegexOptions.CultureInvariant);

    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = FirstExisting(root, "justfile", "Justfile", ".justfile");
        if (path is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ReadTextSafe(path).Split('\n'))
        {
            var match = JustRecipeRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var recipe = match.Groups[1].Value;
            if (recipe is "alias" or "set" or "export" or "import" or "mod")
            {
                continue;
            }

            if (!IsSafeName(recipe) || !seen.Add(recipe))
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = recipe,
                Command = $"just {recipe}",
                Source = CommandSource.Justfile,
                SourcePath = path,
            });
        }

        return commands;
    }

}
