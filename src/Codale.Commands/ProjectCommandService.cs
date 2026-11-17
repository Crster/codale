using Codale.Commands.Detectors;
using Codale.Workspace;

namespace Codale.Commands;

/// <summary>
/// Finds a project's run commands in whatever config files happen to be lying around:
/// Codale's own saved commands (in the workspace database) plus the ones the user already keeps for
/// other tools - Claude's <c>launch.json</c>, VS Code's <c>tasks.json</c> and
/// <c>launch.json</c>, <c>package.json</c> scripts, and common language ecosystems,
/// task runners, and framework utilities (.NET, Gradle, Maven, Python, Cargo, Go,
/// Composer, Laravel, Rails, Flutter, Elixir, Make, Just, Taskfile, Docker Compose,
/// Prisma, and root shell scripts).
/// </summary>
/// <remarks>
/// Every file is optional and every field in it is optional. These are formats Codale
/// does not control (and <c>.claude/launch.json</c> has no published schema at all), so
/// the parsers read tolerantly: a malformed file contributes nothing and never throws.
/// Discovery runs fresh on each call - the commands menu re-runs it every time it opens,
/// which keeps up with edits from any editor without a file watcher.
/// </remarks>
public sealed class ProjectCommandService : IProjectCommandService
{
    /// <summary>VS Code configs are JSONC in the wild: comments and trailing commas are legal there.</summary>
    private readonly IReadOnlyList<IProjectCommandDetector> _detectors;
    private readonly Func<string, ConfigStore> _configFor;

    public IReadOnlyList<ProjectCommand> Discover(string projectRoot) =>
        [.. _detectors.SelectMany(detector => detector.Discover(projectRoot))];

    /// <summary>
    /// Where the workspace database of a project lives; tests point it at a temporary folder.
    /// </summary>
    public Func<string, string> WorkspaceDirectory { get; }

    public ProjectCommandService(Func<string, string>? workspaceDirectory = null, IEnumerable<IProjectCommandDetector>? detectors = null, Func<string, ConfigStore>? configFor = null)
    {
        WorkspaceDirectory = workspaceDirectory ?? WorkspaceDb.DirectoryFor;
        _configFor = configFor ?? (root => new ConfigStore(WorkspaceDirectory(root)));
        _detectors = detectors?.ToArray() ??
        [
            new ProjectCommandDetector("Codale", ReadCodale),
            new ClaudeCommandDetector(),
            new VscodeTasksCommandDetector(),
            new VscodeLaunchCommandDetector(),
            new PackageJsonCommandDetector(),
            new DenoCommandDetector(),
            new DotnetCommandDetector(),
            new GradleCommandDetector(),
            new MavenCommandDetector(),
            new PythonCommandDetector(),
            new CargoCommandDetector(),
            new GoCommandDetector(),
            new ComposerCommandDetector(),
            new LaravelCommandDetector(),
            new RailsCommandDetector(),
            new FlutterCommandDetector(),
            new ElixirCommandDetector(),
            new MakefileCommandDetector(),
            new JustfileCommandDetector(),
            new TaskfileCommandDetector(),
            new DockerComposeCommandDetector(),
            new PrismaCommandDetector(),
            new ShellScriptsCommandDetector(),
        ];
    }

    /// <summary>
    /// The project's saved commands, in the workspace database (never in a file inside the workspace). An old
    /// <c>.codale/commands.json</c> is not read; the commands menu edits the saved entries directly.
    /// </summary>
    public ConfigStore ConfigOf(string projectRoot) => _configFor(projectRoot);

    /// <summary>
    /// Adds a command to the project's saved commands, or replaces the entry of the same
    /// name (case-insensitive), so an agent can register a command and re-register it corrected.
    /// </summary>
    public ProjectCommand Add(string projectRoot, string name, string command, string? workingDirectory = null)
    {
        var config = ConfigOf(projectRoot);
        var saved = config.AddCommand(name, command, workingDirectory);
        return ToProjectCommand(config, saved);
    }

    /// <summary>
    /// Saves a command from the menu, or replaces the entry being edited. A script body is written to a file
    /// beside the workspace database and the saved entry runs that file, so the script is one copy the user can
    /// edit again. <paramref name="originalName"/> is the entry being edited when it is being renamed.
    /// </summary>
    public ProjectCommand Save(string projectRoot, string? originalName, ProjectCommandEdit edit)
    {
        edit = edit.Normalize();
        var config = ConfigOf(projectRoot);
        var body = edit.Command.Trim();
        if (body.Length == 0)
        {
            throw new ArgumentException("Enter the command or script to run.");
        }

        var kind = NormalizeKind(edit.ScriptKind);
        var previous = originalName is null
            ? null
            : config.Commands().FirstOrDefault(c => string.Equals(c.Name, originalName.Trim(), StringComparison.OrdinalIgnoreCase));
        var saved = config.SaveCommand(originalName,
            new SavedCommand(edit.Name, body, edit.WorkingDirectory, kind));

        // A script that was renamed or turned back into a command line leaves no file behind.
        if (previous is { } old && NormalizeKind(old.Shell) is { } oldKind &&
            (!string.Equals(old.Name, saved.Name, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(oldKind, kind, StringComparison.Ordinal)))
        {
            DeleteScript(config, old.Name, oldKind);
        }

        return ToProjectCommand(config, saved);
    }

    /// <summary>Removes a saved command of that name, and the script file it ran; false when there was none.</summary>
    public bool Remove(string projectRoot, string name)
    {
        var config = ConfigOf(projectRoot);
        var saved = config.Commands().FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!config.RemoveCommand(name))
        {
            return false;
        }

        if (NormalizeKind(saved?.Shell) is { } kind)
        {
            DeleteScript(config, saved!.Name, kind);
        }

        return true;
    }

    /// <summary>Only the project's own saved commands, without scanning the project's config files.</summary>
    public IReadOnlyList<ProjectCommand> Saved(string projectRoot)
    {
        try
        {
            var config = ConfigOf(projectRoot);
            return [.. config.Commands().Select(saved => ToProjectCommand(config, saved))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void DeleteScript(ConfigStore config, string name, string kind)
    {
        try
        {
            var path = ScriptFileName(name, kind, config);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The command is gone; a stray script file in the data folder is harmless.
        }
    }

    private List<ProjectCommand> ReadCodale(string root)
        => [.. Saved(root)];

    /// <summary>
    /// A saved entry as the menu shows it. A script body is materialized to a file first; the entry runs the
    /// file through its interpreter, so it works from any shell the terminal tab uses.
    /// </summary>
    private static ProjectCommand ToProjectCommand(ConfigStore config, SavedCommand saved)
    {
        var kind = NormalizeKind(saved.Shell);
        var command = saved.Command;
        if (kind is not null)
        {
            try
            {
                command = ScriptInvocation(kind, WriteScript(config, saved.Name, kind, saved.Command));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The data folder is unavailable: the entry stays visible and reports it when run.
                command = ScriptInvocation(kind, ScriptFileName(saved.Name, kind, config));
            }
        }

        return new ProjectCommand
        {
            Name = saved.Name,
            Command = command,
            WorkingDirectory = saved.Cwd,
            Source = CommandSource.Codale,
            SourcePath = null,
            Script = kind is null ? null : saved.Command,
            ScriptKind = kind,
        };
    }

    /// <summary>The kinds the editor offers; anything else is treated as a plain command line.</summary>
    public static string? NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "powershell" => "powershell",
        "batch" or "cmd" => "batch",
        "shell" or "bash" or "sh" => "shell",
        _ => null,
    };

    /// <summary>
    /// Infers the script kind from its content when the user didn't pick one: shebangs mean shell,
    /// batch markers mean batch, PowerShell syntax or multi-line scripts mean PowerShell, and a
    /// plain single-line command stays a command line.
    /// </summary>
    public static string? DetectScriptKind(string script)
    {
        var trimmed = script.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith("#!", StringComparison.Ordinal))
        {
            return "shell";
        }

        var lines = trimmed.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Any(line =>
                line.StartsWith("@echo", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("echo off", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("setlocal", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)))
        {
            return "batch";
        }

        if (lines.Length > 1 ||
            trimmed.Contains("$env:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Write-Host", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Write-Output", StringComparison.OrdinalIgnoreCase))
        {
            return "powershell";
        }

        return null;
    }

    private static string Extension(string kind) => kind switch
    {
        "powershell" => ".ps1",
        "batch" => ".cmd",
        _ => ".sh",
    };

    /// <summary>
    /// A stable, filesystem-safe script file name for a command name, with a short hash so two names that
    /// sanitize alike (or a name that changes) never share a file.
    /// </summary>
    private static string ScriptFileName(string name, string kind, ConfigStore config)
    {
        if (name.Trim().Length == 0)
        {
            throw new ArgumentException("A name is required.");
        }

        // Windows forbids some characters in file names, and cmd treats a few more specially even inside
        // quotes; both become a dash so the invocation never needs to escape the path.
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. name.Trim().Select(c =>
                invalid.Contains(c) || c is ' ' or '%' or '!' or '^' or '&' or '(' or ')' or ',' or ';' or '\'' or '`'
                    ? '-'
                    : c)])
            .Trim('-', '.');
        if (safe.Length == 0)
        {
            safe = "command";
        }

        if (safe.Length > 48)
        {
            safe = safe[..48].TrimEnd('-', '.');
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant())))[..8].ToLowerInvariant();
        return Path.Combine(config.ScriptsDirectory, $"{safe}-{hash}{Extension(kind)}");
    }

    /// <summary>Writes the body when it changed and returns the script's path; the file keeps a predictable name.</summary>
    private static string WriteScript(ConfigStore config, string name, string kind, string body)
    {
        var path = ScriptFileName(name, kind, config);
        // Windows PowerShell 5.1 reads a BOM-less script in the ANSI code page, so PowerShell gets a BOM;
        // batch's first line and a shell script's shebang must start at byte zero, so those do not.
        var (text, encoding) = kind switch
        {
            "powershell" => (body.ReplaceLineEndings("\r\n") + "\r\n", new System.Text.UTF8Encoding(true)),
            "batch" => (body.ReplaceLineEndings("\r\n") + "\r\n", new System.Text.UTF8Encoding(false)),
            _ => (body.ReplaceLineEndings("\n") + "\n", new System.Text.UTF8Encoding(false)),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (!File.Exists(path) || File.ReadAllText(path) != text)
            {
                File.WriteAllText(path, text, encoding);
            }
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process (the agent's tool server) writes the same script; one copy is enough.
        }

        return path;
    }

    /// <summary>The line that runs a script file from any shell a terminal tab may use.</summary>
    private static string ScriptInvocation(string kind, string path) => kind switch
    {
        "powershell" => $"\"{PowerShellPath()}\" -NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
        "batch" => $"cmd /d /c call \"{path}\"",
        _ => $"\"{BashPath()}\" \"{path}\"",
    };

    /// <summary>PowerShell 7 when installed (its syntax is the newer one), otherwise Windows PowerShell.</summary>
    private static string PowerShellPath() =>
        ResolveOnPath("pwsh.exe") ?? ResolveOnPath("powershell.exe") ?? "powershell";

    /// <summary>An executable on PATH, or null when it is not installed there.</summary>
    private static string? ResolveOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry contributes nothing.
            }
        }

        return null;
    }

    /// <summary>
    /// The bash a shell script runs under. Codale's terminal shells are PowerShell and cmd, so bash is
    /// invoked explicitly; Git Bash is preferred because the <c>bash.exe</c> alias Windows ships is a WSL
    /// launcher that cannot read a Windows path.
    /// </summary>
    private static string BashPath()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\bin\bash.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Git\bin\bash.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\bin\bash.exe"),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "bash";
    }

    /// <remarks>
    /// No published schema exists, so this accepts the three shapes seen in the wild: a
    /// bare array, VS Code-style <c>configurations</c>, or a <c>commands</c> wrapper -
    /// with the command as a string or a string array (argv style).
    /// </remarks>
}
