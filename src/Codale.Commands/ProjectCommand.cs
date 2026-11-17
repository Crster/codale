namespace Codale.Commands;

public enum CommandSource
{
    Codale,
    Claude,
    VscodeTasks,
    VscodeLaunch,
    PackageJson,
    Deno,
    Dotnet,
    Gradle,
    Maven,
    Python,
    Cargo,
    Go,
    Composer,
    Laravel,
    Rails,
    Flutter,
    Elixir,
    Makefile,
    Justfile,
    Taskfile,
    DockerCompose,
    Prisma,
    ShellScript,
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

    /// <summary>The line that runs; for a saved script this is the interpreter invocation of its file.</summary>
    public required string Command { get; init; }

    public string? WorkingDirectory { get; init; }

    public required CommandSource Source { get; init; }

    public string? SourcePath { get; init; }

    /// <summary>
    /// The body of a script saved from the commands menu (<c>powershell</c>, <c>batch</c> or <c>shell</c>);
    /// null for a plain command line and for commands discovered in project files.
    /// </summary>
    public string? Script { get; init; }

    /// <summary>Which kind of script <see cref="Script"/> is; null when there is none.</summary>
    public string? ScriptKind { get; init; }

    public string SourceLabel => Source switch
    {
        CommandSource.Codale => "Codale",
        CommandSource.Claude => ".claude",
        CommandSource.VscodeTasks => "tasks.json",
        CommandSource.VscodeLaunch => "launch.json",
        CommandSource.PackageJson => "npm",
        CommandSource.Deno => "Deno",
        CommandSource.Dotnet => ".NET",
        CommandSource.Gradle => "Gradle",
        CommandSource.Maven => "Maven",
        CommandSource.Python => "Python",
        CommandSource.Cargo => "Cargo",
        CommandSource.Go => "Go",
        CommandSource.Composer => "Composer",
        CommandSource.Laravel => "Laravel",
        CommandSource.Rails => "Rails",
        CommandSource.Flutter => "Flutter",
        CommandSource.Elixir => "Mix",
        CommandSource.Makefile => "Make",
        CommandSource.Justfile => "Just",
        CommandSource.Taskfile => "Taskfile",
        CommandSource.DockerCompose => "Docker Compose",
        CommandSource.Prisma => "Prisma",
        CommandSource.ShellScript => "Scripts",
        _ => "",
    };
}
