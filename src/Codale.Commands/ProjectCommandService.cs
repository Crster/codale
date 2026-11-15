using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
        _detectors.SelectMany(detector => detector.Discover(projectRoot)).ToList();

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
    /// <c>.codale/commands.json</c> is not read; the editable copy in Codale's data folder is read back
    /// after the user changed it.
    /// </summary>
    public ConfigStore ConfigOf(string projectRoot)
    {
        var config = _configFor(projectRoot);
        config.SyncEdited();
        return config;
    }

    /// <summary>
    /// Adds a command to the project's saved commands, or replaces the entry of the same
    /// name (case-insensitive), so an agent can register a command and re-register it corrected.
    /// </summary>
    public ProjectCommand Add(string projectRoot, string name, string command, string? workingDirectory = null)
    {
        var config = ConfigOf(projectRoot);
        var saved = config.AddCommand(name, command, workingDirectory);
        return new ProjectCommand
        {
            Name = saved.Name,
            Command = saved.Command,
            WorkingDirectory = saved.Cwd,
            Source = CommandSource.Codale,
            SourcePath = config.EditablePath,
        };
    }

    private List<ProjectCommand> ReadCodale(string root)
    {
        try
        {
            var config = ConfigOf(root);
            return config.Commands().Select(saved => new ProjectCommand
            {
                Name = saved.Name,
                Command = saved.Command,
                WorkingDirectory = saved.Cwd,
                Source = CommandSource.Codale,
                SourcePath = config.EditablePath,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The database is busy in another process: the saved commands are missing this once.
            return [];
        }
    }

    /// <remarks>
    /// No published schema exists, so this accepts the three shapes seen in the wild: a
    /// bare array, VS Code-style <c>configurations</c>, or a <c>commands</c> wrapper -
    /// with the command as a string or a string array (argv style).
    /// </remarks>
}
