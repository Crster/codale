namespace Codale.Commands.Detectors;

internal sealed class ShellScriptsCommandDetector : IProjectCommandDetector
{
    public string Id => "ShellScripts";
    private static readonly (string FileName, string CommandPrefix)[] KnownShellScripts =
    [
        ("run.ps1", @"powershell -ExecutionPolicy Bypass -File .\"),
        ("dev.ps1", @"powershell -ExecutionPolicy Bypass -File .\"),
        ("build.ps1", @"powershell -ExecutionPolicy Bypass -File .\"),
        ("start.ps1", @"powershell -ExecutionPolicy Bypass -File .\"),
        ("test.ps1", @"powershell -ExecutionPolicy Bypass -File .\"),
        ("run.cmd", @".\"),
        ("dev.cmd", @".\"),
        ("build.cmd", @".\"),
        ("start.cmd", @".\"),
        ("run.bat", @".\"),
        ("dev.bat", @".\"),
        ("build.bat", @".\"),
        ("start.bat", @".\"),
        ("run.sh", "bash ./"),
        ("dev.sh", "bash ./"),
        ("build.sh", "bash ./"),
        ("start.sh", "bash ./"),
    ];

    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var commands = new List<ProjectCommand>();
        foreach (var (fileName, prefix) in KnownShellScripts)
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = fileName,
                Command = $"{prefix}{fileName}",
                Source = CommandSource.ShellScript,
                SourcePath = path,
            });
        }

        return commands;
    }

}