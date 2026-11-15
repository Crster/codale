using System.Text.Json;

using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class ComposerCommandDetector : IProjectCommandDetector
{
    public string Id => "Composer";
    private static readonly HashSet<string> ComposerLifecycleHooks =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "pre-install-cmd", "post-install-cmd", "pre-update-cmd", "post-update-cmd",
        "pre-status-cmd", "post-status-cmd", "pre-archive-cmd", "post-archive-cmd",
        "pre-autoload-dump", "post-autoload-dump", "post-root-package-install",
        "post-create-project-cmd", "pre-operations-exec",
    ];

    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "composer.json");
        using var doc = ParseFile(path);
        if (doc?.RootElement.Prop("scripts") is not { ValueKind: JsonValueKind.Object } scripts)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        foreach (var script in scripts.EnumerateObject())
        {
            var hasBody = (script.Value.ValueKind == JsonValueKind.String && script.Value.GetString() is { Length: > 0 })
                || (script.Value.ValueKind == JsonValueKind.Array && script.Value.GetArrayLength() > 0);
            if (!hasBody || !IsSafeName(script.Name) || ComposerLifecycleHooks.Contains(script.Name))
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = script.Name,
                Command = $"composer run-script {script.Name}",
                Source = CommandSource.Composer,
                SourcePath = path,
            });
        }

        return commands;
    }

}