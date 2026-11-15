using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class DotnetCommandDetector : IProjectCommandDetector
{
    public string Id => "Dotnet";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return [];
            }

            var slnFiles = Directory.GetFiles(root, "*.sln")
                .Concat(Directory.GetFiles(root, "*.slnx"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var projFiles = Directory.GetFiles(root, "*.csproj")
                .Concat(Directory.GetFiles(root, "*.fsproj"))
                .Concat(Directory.GetFiles(root, "*.vbproj"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (slnFiles.Count == 0 && projFiles.Count == 0)
            {
                ScanSubdirectoriesForDotnetProjects(root, depth: 0, maxDepth: 2, projFiles);
            }
            else if (projFiles.Count == 0)
            {
                // A root solution typically keeps its projects in src/* or one level deeper.
                ScanSubdirectoriesForDotnetProjects(root, depth: 0, maxDepth: 2, projFiles);
            }

            if (slnFiles.Count == 0 && projFiles.Count == 0)
            {
                return [];
            }

            var primarySource = slnFiles.FirstOrDefault() ?? projFiles[0];
            var commands = new List<ProjectCommand>();

            // Look for runnable projects (OutputType Exe or Web SDK) and launchSettings profiles.
            foreach (var proj in projFiles)
            {
                if (!IsRunnableDotnetProject(proj))
                {
                    continue;
                }

                var relProj = Path.GetRelativePath(root, proj);
                var projArg = relProj.Contains(Path.DirectorySeparatorChar) || projFiles.Count > 1
                    ? $" --project {Quote(relProj)}"
                    : "";
                var projLabel = projFiles.Count > 1 ? $" ({Path.GetFileNameWithoutExtension(proj)})" : "";

                commands.Add(new ProjectCommand
                {
                    Name = $"run{projLabel}",
                    Command = $"dotnet run{projArg}",
                    Source = CommandSource.Dotnet,
                    SourcePath = proj,
                });

                commands.Add(new ProjectCommand
                {
                    Name = $"watch{projLabel}",
                    Command = $"dotnet watch{projArg}",
                    Source = CommandSource.Dotnet,
                    SourcePath = proj,
                });

                var launchPath = Path.Combine(Path.GetDirectoryName(proj)!, "Properties", "launchSettings.json");
                using var launchDoc = ParseFile(launchPath);
                if (launchDoc?.RootElement.Prop("profiles") is { ValueKind: JsonValueKind.Object } profiles)
                {
                    foreach (var profile in profiles.EnumerateObject())
                    {
                        var cmdName = profile.Value.Str("commandName");
                        if (cmdName is not ("Project" or "Executable") || !IsSafeName(profile.Name))
                        {
                            continue;
                        }

                        commands.Add(new ProjectCommand
                        {
                            Name = $"run: {profile.Name}{projLabel}",
                            Command = $"dotnet run{projArg} --launch-profile {Quote(profile.Name)}",
                            Source = CommandSource.Dotnet,
                            SourcePath = launchPath,
                        });
                    }
                }
            }

            if (commands.Count == 0 && projFiles.Count == 1)
            {
                var relProj = Path.GetRelativePath(root, projFiles[0]);
                var projArg = relProj.Contains(Path.DirectorySeparatorChar) ? $" --project {Quote(relProj)}" : "";
                commands.Add(new ProjectCommand
                {
                    Name = "run",
                    Command = $"dotnet run{projArg}",
                    Source = CommandSource.Dotnet,
                    SourcePath = projFiles[0],
                });
            }

            commands.AddRange(PresetCommands(CommandSource.Dotnet, primarySource,
                ("build", "dotnet build"),
                ("test", "dotnet test")));

            return commands;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void ScanSubdirectoriesForDotnetProjects(string directory, int depth, int maxDepth, List<string> results)
    {
        if (depth >= maxDepth)
        {
            return;
        }

        foreach (var sub in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules" or "packages" or "dist" or "target" or "TestResults")
            {
                continue;
            }

            results.AddRange(Directory.GetFiles(sub, "*.csproj")
                .Concat(Directory.GetFiles(sub, "*.fsproj"))
                .Concat(Directory.GetFiles(sub, "*.vbproj"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

            ScanSubdirectoriesForDotnetProjects(sub, depth + 1, maxDepth, results);
        }
    }

    private static bool IsRunnableDotnetProject(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";
            if (sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                sdk.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase) ||
                sdk.Contains("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return doc.Descendants()
                .Any(e => string.Equals(e.Name.LocalName, "OutputType", StringComparison.OrdinalIgnoreCase) &&
                          (string.Equals(e.Value.Trim(), "Exe", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(e.Value.Trim(), "WinExe", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception)
        {
            return false;
        }
    }

}
