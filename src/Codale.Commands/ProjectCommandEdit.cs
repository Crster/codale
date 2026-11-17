namespace Codale.Commands;

/// <summary>What the commands editor collects: a label, what runs, where, and whether it is a script.</summary>
/// <param name="Name">The menu label; it identifies the entry, so renaming replaces by the old name.</param>
/// <param name="Command">The command line, or the body of the script when <paramref name="ScriptKind"/> is set.</param>
/// <param name="WorkingDirectory">Relative to the project root; null runs at the root.</param>
/// <param name="ScriptKind"><c>powershell</c>, <c>batch</c> or <c>shell</c>; null for a plain command line.</param>
public sealed record ProjectCommandEdit(string Name, string Command, string? WorkingDirectory = null, string? ScriptKind = null)
{
    /// <summary>Trimmed values as they are saved; the label falls back to the command's first line when blank.</summary>
    public ProjectCommandEdit Normalize()
    {
        var command = Command.Trim();
        var name = Name.Trim();
        return new ProjectCommandEdit(
            name.Length > 0 ? name : command.Split('\n')[0].Trim(),
            command,
            string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim(),
            ProjectCommandService.NormalizeKind(ScriptKind));
    }
}
