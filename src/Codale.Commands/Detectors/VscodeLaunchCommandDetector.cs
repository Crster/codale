using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class VscodeLaunchCommandDetector : IProjectCommandDetector
{
    public string Id => "VscodeLaunch";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, ".vscode", "launch.json");
        using var doc = ParseFile(path);
        if (doc is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();

        foreach (var config in doc.RootElement.Items("configurations"))
        {
            var windows = config.Prop("windows");
            var executable = windows.Str("runtimeExecutable") ?? config.Str("runtimeExecutable")
                ?? windows.Str("program") ?? config.Str("program");
            if (string.IsNullOrWhiteSpace(executable))
            {
                continue;
            }

            var runtimeArgs = (windows.StrArray("runtimeArgs").Count > 0 ? windows : config).StrArray("runtimeArgs");
            var args = (windows.StrArray("args").Count > 0 ? windows : config).StrArray("args");

            commands.Add(new ProjectCommand
            {
                Name = config.Str("name") ?? executable,
                Command = JoinArguments(executable, [.. runtimeArgs, .. args]),
                WorkingDirectory = windows.Str("cwd") ?? config.Str("cwd"),
                Source = CommandSource.VscodeLaunch,
                SourcePath = path,
            });
        }

        return commands;
    }

    /// <summary>
    /// A script name that is safe to place after <c>npm run</c> unquoted. The key comes from a
    /// file in the (possibly untrusted) project, and the command is later run through a shell:
    /// anything beyond a plain name - spaces, <c>;</c>, <c>&amp;</c>, quotes - is not run.
    /// </summary>
}