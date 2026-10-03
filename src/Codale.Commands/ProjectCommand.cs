namespace Codale.Commands;

public enum CommandSource
{
    Codale,
    Claude,
    VscodeTasks,
    VscodeLaunch,
    PackageJson,
}

/// <summary>One runnable project command, discovered from a config file.</summary>
/// <param name="Name">
/// What the menu shows. Editors rename theirs constantly, so the label is kept verbatim
/// rather than normalized - "dev: server" is how its author refers to it.
/// </param>
/// <param name="Command">The shell command line, executed through cmd.exe.</param>
/// <param name="WorkingDirectory">Directory to run in; relative paths resolve against the project root.</param>
/// <param name="Source">Which config file family the command came from.</param>
/// <param name="SourcePath">Full path of the file it was discovered in, for the menu's edit items.</param>
public sealed record ProjectCommand
{
    public required string Name { get; init; }

    public required string Command { get; init; }

    public string? WorkingDirectory { get; init; }

    public required CommandSource Source { get; init; }

    public string? SourcePath { get; init; }

    public string SourceLabel => Source switch
    {
        CommandSource.Codale => "Codale",
        CommandSource.Claude => ".claude",
        CommandSource.VscodeTasks => "tasks.json",
        CommandSource.VscodeLaunch => "launch.json",
        CommandSource.PackageJson => "npm",
        _ => "",
    };
}
