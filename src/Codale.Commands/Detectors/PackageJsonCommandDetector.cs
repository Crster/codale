using System.Text.Json;

using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class PackageJsonCommandDetector : IProjectCommandDetector
{
    public string Id => "PackageJson";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "package.json");
        using var doc = ParseFile(path);
        if (doc?.RootElement.Prop("scripts") is not { ValueKind: JsonValueKind.Object } scripts)
        {
            return [];
        }

        var runner = File.Exists(Path.Combine(root, "bun.lockb")) || File.Exists(Path.Combine(root, "bun.lock")) ? "bun run"
            : File.Exists(Path.Combine(root, "pnpm-lock.yaml")) ? "pnpm run"
            : File.Exists(Path.Combine(root, "yarn.lock")) ? "yarn run"
            : "npm run";

        var commands = new List<ProjectCommand>();
        foreach (var script in scripts.EnumerateObject())
        {
            if (script.Value.ValueKind != JsonValueKind.String || script.Value.GetString() is not { Length: > 0 })
            {
                continue;
            }

            // A leading dash would be read by npm as one of its own options.
            if (!IsSafeName(script.Name))
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = script.Name,
                Command = $"{runner} {script.Name}",
                Source = CommandSource.PackageJson,
                SourcePath = path,
            });
        }

        return commands;
    }

}