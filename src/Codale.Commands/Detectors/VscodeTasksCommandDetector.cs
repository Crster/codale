using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class VscodeTasksCommandDetector : IProjectCommandDetector
{
    public string Id => "VscodeTasks";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, ".vscode", "tasks.json");
        using var doc = ParseFile(path);
        if (doc is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();

        foreach (var task in doc.RootElement.Items("tasks"))
        {
            // Per-OS overrides win when present: "windows": { "command": ... }.
            var windows = task.Prop("windows");
            var command = windows.Str("command") ?? task.Str("command");
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            var args = (windows.StrArray("args").Count > 0 ? windows : task).StrArray("args");
            var cwd = windows.Prop("options").Str("cwd") ?? task.Prop("options").Str("cwd");

            commands.Add(new ProjectCommand
            {
                Name = task.Str("label") ?? JoinArguments(command, args),
                Command = JoinArguments(command, args),
                WorkingDirectory = cwd,
                Source = CommandSource.VscodeTasks,
                SourcePath = path,
            });
        }

        return commands;
    }

    /// <remarks>
    /// Launch configurations describe debuggers, not shell commands, so the command is
    /// derived: the runtime executable (or the program itself) plus its arguments.
    /// Configurations that derive nothing - attach requests, browser types with only a
    /// URL - are skipped rather than shown as commands that cannot work.
    /// </remarks>
}
