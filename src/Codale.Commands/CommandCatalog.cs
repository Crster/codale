using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Codale.Commands;

/// <summary>
/// Finds a project's run commands in whatever config files happen to be lying around:
/// Codale's own <c>.codale/commands.json</c> plus the ones the user already keeps for
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
public static class CommandCatalog
{
    /// <summary>VS Code configs are JSONC in the wild: comments and trailing commas are legal there.</summary>
    private static readonly JsonDocumentOptions Jsonc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<ProjectCommand> Discover(string projectRoot) =>
    [
        .. ReadCodale(projectRoot),
        .. ReadClaude(projectRoot),
        .. ReadVscodeTasks(projectRoot),
        .. ReadVscodeLaunch(projectRoot),
        .. ReadPackageJson(projectRoot),
        .. ReadDeno(projectRoot),
        .. ReadDotnet(projectRoot),
        .. ReadGradle(projectRoot),
        .. ReadMaven(projectRoot),
        .. ReadPython(projectRoot),
        .. ReadCargo(projectRoot),
        .. ReadGo(projectRoot),
        .. ReadComposer(projectRoot),
        .. ReadLaravel(projectRoot),
        .. ReadRails(projectRoot),
        .. ReadFlutter(projectRoot),
        .. ReadElixir(projectRoot),
        .. ReadMakefile(projectRoot),
        .. ReadJustfile(projectRoot),
        .. ReadTaskfile(projectRoot),
        .. ReadDockerCompose(projectRoot),
        .. ReadPrisma(projectRoot),
        .. ReadShellScripts(projectRoot),
    ];

    /// <summary>
    /// Adds a command to <c>.codale/commands.json</c>, or replaces the entry of the same
    /// name (case-insensitive), so an agent can register a command and re-register it
    /// corrected. Other entries and any extra fields in the file are kept; a file that
    /// cannot be parsed is an error rather than overwritten.
    /// </summary>
    public static ProjectCommand Add(string projectRoot, string name, string command, string? workingDirectory = null)
    {
        name = name.Trim();
        command = command.Trim();
        if (name.Length == 0 || command.Length == 0)
        {
            throw new ArgumentException("A name and a command are required.");
        }

        var path = Path.Combine(projectRoot, ".codale", "commands.json");
        var entry = new JsonObject { ["name"] = name, ["command"] = command };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            entry["cwd"] = workingDirectory.Trim();
        }

        JsonNode? existing = null;
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                existing = string.IsNullOrWhiteSpace(text)
                    ? null
                    : JsonNode.Parse(text, documentOptions: Jsonc);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{path} is not valid JSON ({ex.Message}); fix it before adding commands.");
            }
        }

        // The canonical wrapper is { "commands": [...] }; a bare array is kept as one.
        JsonArray list;
        JsonNode document;
        switch (existing)
        {
            case JsonArray bare:
                list = bare;
                document = bare;
                break;
            case JsonObject wrapper:
                list = wrapper["commands"] as JsonArray ?? [];
                wrapper["commands"] = list;
                document = wrapper;
                break;
            default:
                list = [];
                document = new JsonObject { ["commands"] = list };
                break;
        }

        var index = list.ToList().FindIndex(n =>
            n is JsonObject o && string.Equals(NameOf(o), name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            list[index] = entry;
        }
        else
        {
            list.Add(entry);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        return new ProjectCommand
        {
            Name = name,
            Command = command,
            WorkingDirectory = entry["cwd"]?.GetValue<string>(),
            Source = CommandSource.Codale,
            SourcePath = path,
        };
    }

    private static string? NameOf(JsonObject o) =>
        (o["name"] ?? o["label"]) is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    private static List<ProjectCommand> ReadCodale(string root)
    {
        var path = Path.Combine(root, ".codale", "commands.json");
        using var doc = ParseFile(path);
        if (doc is null)
        {
            return [];
        }

        // The canonical shape is { "commands": [...] }; a bare array is accepted so a
        // minimal file can skip the wrapper.
        var rootElement = doc.RootElement;
        var entries = rootElement.ValueKind == JsonValueKind.Array
            ? rootElement.EnumerateArray()
            : rootElement.Items("commands");

        var commands = new List<ProjectCommand>();
        foreach (var entry in entries)
        {
            if (entry.Str("command", "cmd") is not { Length: > 0 } command)
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = entry.Str("name", "label") ?? command.Trim(),
                Command = command,
                WorkingDirectory = entry.Str("cwd", "workingDirectory"),
                Source = CommandSource.Codale,
                SourcePath = path,
            });
        }

        return commands;
    }

    /// <remarks>
    /// No published schema exists, so this accepts the three shapes seen in the wild: a
    /// bare array, VS Code-style <c>configurations</c>, or a <c>commands</c> wrapper -
    /// with the command as a string or a string array (argv style).
    /// </remarks>
    private static List<ProjectCommand> ReadClaude(string root)
    {
        var path = Path.Combine(root, ".claude", "launch.json");
        using var doc = ParseFile(path);
        if (doc is null)
        {
            return [];
        }

        var rootElement = doc.RootElement;
        var entries = rootElement.ValueKind == JsonValueKind.Array
            ? rootElement.EnumerateArray()
            : rootElement.Items("commands", "configurations");

        var commands = new List<ProjectCommand>();
        foreach (var entry in entries)
        {
            if (CommandValue(entry, "command", "cmd", "run") is not { Length: > 0 } command)
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = entry.Str("name", "label", "title") ?? command.Trim(),
                Command = command,
                WorkingDirectory = entry.Str("cwd", "workingDirectory", "directory"),
                Source = CommandSource.Claude,
                SourcePath = path,
            });
        }

        return commands;
    }

    private static List<ProjectCommand> ReadVscodeTasks(string root)
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
    private static List<ProjectCommand> ReadVscodeLaunch(string root)
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
    private static readonly Regex SafeScriptName = new(@"^[A-Za-z0-9_:.@/-]+$", RegexOptions.CultureInvariant);

    private static bool IsSafeName(string name) =>
        SafeScriptName.IsMatch(name) && !name.StartsWith('-');

    private static List<ProjectCommand> ReadPackageJson(string root)
    {
        var path = Path.Combine(root, "package.json");
        using var doc = ParseFile(path);
        if (doc?.RootElement.Prop("scripts") is not { ValueKind: JsonValueKind.Object } scripts)
        {
            return [];
        }

        var runner = File.Exists(Path.Combine(root, "bun.lockb")) || File.Exists(Path.Combine(root, "bun.lock")) ? "bun run"
            : File.Exists(Path.Combine(root, "pnpm-lock.yaml")) ? "pnpm run"
            : File.Exists(Path.Combine(root, "yarn.lock")) ? "yarn run"
            : "npm run";

        var commands = new List<ProjectCommand>();
        foreach (var script in scripts.EnumerateObject())
        {
            if (script.Value.ValueKind != JsonValueKind.String || script.Value.GetString() is not { Length: > 0 })
            {
                continue;
            }

            // A leading dash would be read by npm as one of its own options.
            if (!IsSafeName(script.Name))
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = script.Name,
                Command = $"{runner} {script.Name}",
                Source = CommandSource.PackageJson,
                SourcePath = path,
            });
        }

        return commands;
    }

    private static List<ProjectCommand> ReadDeno(string root)
    {
        var path = FirstExisting(root, "deno.json", "deno.jsonc");
        if (path is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        using var doc = ParseFile(path);
        if (doc?.RootElement.Prop("tasks") is { ValueKind: JsonValueKind.Object } tasks)
        {
            foreach (var task in tasks.EnumerateObject())
            {
                var hasCommand = (task.Value.ValueKind == JsonValueKind.String && task.Value.GetString() is { Length: > 0 })
                    || (task.Value.ValueKind == JsonValueKind.Object && task.Value.Str("command") is { Length: > 0 });
                if (!hasCommand || !IsSafeName(task.Name))
                {
                    continue;
                }

                commands.Add(new ProjectCommand
                {
                    Name = task.Name,
                    Command = $"deno task {task.Name}",
                    Source = CommandSource.Deno,
                    SourcePath = path,
                });
            }
        }

        if (commands.Count == 0)
        {
            commands.AddRange(PresetCommands(CommandSource.Deno, path,
                ("test", "deno test"),
                ("lint", "deno lint")));
        }

        return commands;
    }

    private static List<ProjectCommand> ReadDotnet(string root)
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

    private static readonly Regex GradleTaskRegex = new(
        @"^\s*(?:tasks\.register\s*(?:<[^>]+>)?\s*\(\s*[""']([A-Za-z0-9_.-]+)[""']|task\s+([A-Za-z0-9_.-]+)\b|task\s*\(\s*[""']([A-Za-z0-9_.-]+)[""']\s*\))",
        RegexOptions.CultureInvariant);

    private static List<ProjectCommand> ReadGradle(string root)
    {
        var path = FirstExisting(root, "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts");
        if (path is null)
        {
            return [];
        }

        var exe = File.Exists(Path.Combine(root, "gradlew.bat")) || File.Exists(Path.Combine(root, "gradlew"))
            ? @".\gradlew"
            : "gradle";

        var buildText = ReadTextSafe(FirstExisting(root, "build.gradle", "build.gradle.kts") ?? path);
        var hasSpringBoot = buildText.Contains("org.springframework.boot", StringComparison.OrdinalIgnoreCase)
            || buildText.Contains("bootRun", StringComparison.Ordinal);
        var hasApplication = buildText.Contains("application", StringComparison.Ordinal)
            || buildText.Contains("mainClass", StringComparison.Ordinal);

        var commands = new List<ProjectCommand>();
        if (hasSpringBoot)
        {
            commands.Add(new ProjectCommand { Name = "bootRun", Command = $"{exe} bootRun", Source = CommandSource.Gradle, SourcePath = path });
        }
        else if (hasApplication)
        {
            commands.Add(new ProjectCommand { Name = "run", Command = $"{exe} run", Source = CommandSource.Gradle, SourcePath = path });
        }

        commands.AddRange(PresetCommands(CommandSource.Gradle, path,
            ("build", $"{exe} build"),
            ("test", $"{exe} test"),
            ("clean", $"{exe} clean")));

        var seen = new HashSet<string>(commands.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var line in buildText.Split('\n'))
        {
            var match = GradleTaskRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var taskName = match.Groups[1].Value is { Length: > 0 } g1 ? g1
                : match.Groups[2].Value is { Length: > 0 } g2 ? g2
                : match.Groups[3].Value;
            if (IsSafeName(taskName) && seen.Add(taskName))
            {
                commands.Add(new ProjectCommand
                {
                    Name = taskName,
                    Command = $"{exe} {taskName}",
                    Source = CommandSource.Gradle,
                    SourcePath = path,
                });
            }
        }

        return commands;
    }

    private static List<ProjectCommand> ReadMaven(string root)
    {
        var path = Path.Combine(root, "pom.xml");
        if (!File.Exists(path))
        {
            return [];
        }

        var exe = File.Exists(Path.Combine(root, "mvnw.cmd")) || File.Exists(Path.Combine(root, "mvnw"))
            ? @".\mvnw"
            : "mvn";
        var pomText = ReadTextSafe(path);
        var commands = new List<ProjectCommand>();

        if (pomText.Contains("spring-boot-maven-plugin", StringComparison.OrdinalIgnoreCase))
        {
            commands.Add(new ProjectCommand
            {
                Name = "spring-boot:run",
                Command = $"{exe} spring-boot:run",
                Source = CommandSource.Maven,
                SourcePath = path,
            });
        }

        commands.AddRange(PresetCommands(CommandSource.Maven, path,
            ("test", $"{exe} test"),
            ("package", $"{exe} package"),
            ("clean install", $"{exe} clean install")));

        return commands;
    }

    private static List<ProjectCommand> ReadPython(string root)
    {
        var commands = new List<ProjectCommand>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPython(string name, string command, string sourcePath)
        {
            if (seenNames.Add(name))
            {
                commands.Add(new ProjectCommand
                {
                    Name = name,
                    Command = command,
                    Source = CommandSource.Python,
                    SourcePath = sourcePath,
                });
            }
        }

        var managePy = Path.Combine(root, "manage.py");
        if (File.Exists(managePy))
        {
            AddPython("django: runserver", "python manage.py runserver", managePy);
            AddPython("django: migrate", "python manage.py migrate", managePy);
            AddPython("django: makemigrations", "python manage.py makemigrations", managePy);
            AddPython("django: test", "python manage.py test", managePy);
        }

        var pyproject = Path.Combine(root, "pyproject.toml");
        var pyprojectText = File.Exists(pyproject) ? ReadTextSafe(pyproject) : "";
        if (pyprojectText.Length > 0)
        {
            var runner = File.Exists(Path.Combine(root, "uv.lock")) ? "uv run"
                : File.Exists(Path.Combine(root, "poetry.lock")) || pyprojectText.Contains("[tool.poetry", StringComparison.Ordinal) ? "poetry run"
                : File.Exists(Path.Combine(root, "pdm.lock")) || pyprojectText.Contains("[tool.pdm", StringComparison.Ordinal) ? "pdm run"
                : null;

            foreach (var section in new[] { "project.scripts", "tool.poetry.scripts", "tool.pdm.scripts", "tool.hatch.envs.default.scripts" })
            {
                foreach (var (key, _) in ParseTomlSectionKeys(pyprojectText, section))
                {
                    if (!IsSafeName(key))
                    {
                        continue;
                    }

                    var cmd = section == "tool.hatch.envs.default.scripts"
                        ? $"hatch run {key}"
                        : runner is not null ? $"{runner} {key}" : key;
                    AddPython(key, cmd, pyproject);
                }
            }

            foreach (var (key, _) in ParseTomlSectionKeys(pyprojectText, "tool.poe.tasks"))
            {
                if (IsSafeName(key))
                {
                    AddPython(key, $"poe {key}", pyproject);
                }
            }
        }

        var pipfile = Path.Combine(root, "Pipfile");
        if (File.Exists(pipfile))
        {
            foreach (var (key, _) in ParseTomlSectionKeys(ReadTextSafe(pipfile), "scripts"))
            {
                if (IsSafeName(key))
                {
                    AddPython(key, $"pipenv run {key}", pipfile);
                }
            }
        }

        var primaryManifest = FirstExisting(root, "pyproject.toml", "Pipfile", "requirements.txt", "setup.py", "manage.py");
        if (primaryManifest is not null)
        {
            foreach (var entry in new[] { "main.py", "app.py" })
            {
                var entryPath = Path.Combine(root, entry);
                if (File.Exists(entryPath))
                {
                    AddPython($"run {entry}", $"python {entry}", entryPath);
                }
            }

            if (pyprojectText.Contains("pytest", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(root, "pytest.ini")) ||
                Directory.Exists(Path.Combine(root, "tests")))
            {
                AddPython("pytest", "pytest", primaryManifest);
            }

            if (pyprojectText.Contains("[tool.ruff", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(root, "ruff.toml")))
            {
                AddPython("ruff check", "ruff check .", primaryManifest);
            }
        }

        return commands;
    }

    private static List<ProjectCommand> ReadCargo(string root)
    {
        var path = Path.Combine(root, "Cargo.toml");
        if (!File.Exists(path))
        {
            return [];
        }

        var text = ReadTextSafe(path);
        var isPureWorkspace = text.Contains("[workspace]", StringComparison.Ordinal)
            && !text.Contains("[package]", StringComparison.Ordinal);

        var commands = new List<ProjectCommand>();
        if (!isPureWorkspace)
        {
            commands.Add(new ProjectCommand
            {
                Name = "run",
                Command = "cargo run",
                Source = CommandSource.Cargo,
                SourcePath = path,
            });
        }

        commands.AddRange(PresetCommands(CommandSource.Cargo, path,
            ("build", "cargo build"),
            ("test", "cargo test"),
            ("check", "cargo check"),
            ("clippy", "cargo clippy")));

        return commands;
    }

    private static List<ProjectCommand> ReadGo(string root)
    {
        var path = Path.Combine(root, "go.mod");
        if (!File.Exists(path))
        {
            return [];
        }

        return PresetCommands(CommandSource.Go, path,
            ("run", "go run ."),
            ("build", "go build ./..."),
            ("test", "go test ./..."));
    }

    private static readonly HashSet<string> ComposerLifecycleHooks = new(StringComparer.OrdinalIgnoreCase)
    {
        "pre-install-cmd", "post-install-cmd", "pre-update-cmd", "post-update-cmd",
        "pre-status-cmd", "post-status-cmd", "pre-archive-cmd", "post-archive-cmd",
        "pre-autoload-dump", "post-autoload-dump", "post-root-package-install",
        "post-create-project-cmd", "pre-operations-exec",
    };

    private static List<ProjectCommand> ReadComposer(string root)
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

    private static List<ProjectCommand> ReadLaravel(string root)
    {
        var path = Path.Combine(root, "artisan");
        if (!File.Exists(path))
        {
            return [];
        }

        return PresetCommands(CommandSource.Laravel, path,
            ("serve", "php artisan serve"),
            ("migrate", "php artisan migrate"),
            ("test", "php artisan test"),
            ("queue:work", "php artisan queue:work"),
            ("optimize:clear", "php artisan optimize:clear"));
    }

    private static List<ProjectCommand> ReadRails(string root)
    {
        var binRails = Path.Combine(root, "bin", "rails");
        var gemfile = Path.Combine(root, "Gemfile");
        var hasRails = File.Exists(binRails)
            || (File.Exists(gemfile) && Regex.IsMatch(ReadTextSafe(gemfile), @"^\s*gem\s+['""]rails['""]", RegexOptions.Multiline));
        if (!hasRails)
        {
            return [];
        }

        var sourcePath = File.Exists(binRails) ? binRails : gemfile;
        return PresetCommands(CommandSource.Rails, sourcePath,
            ("server", "bundle exec rails server"),
            ("db:migrate", "bundle exec rails db:migrate"),
            ("test", "bundle exec rails test"),
            ("routes", "bundle exec rails routes"));
    }

    private static List<ProjectCommand> ReadFlutter(string root)
    {
        var path = Path.Combine(root, "pubspec.yaml");
        if (!File.Exists(path))
        {
            return [];
        }

        var text = ReadTextSafe(path);
        var isFlutter = Regex.IsMatch(text, @"^\s*flutter\s*:", RegexOptions.Multiline);
        if (isFlutter)
        {
            return PresetCommands(CommandSource.Flutter, path,
                ("run", "flutter run"),
                ("test", "flutter test"),
                ("pub get", "flutter pub get"),
                ("build", "flutter build"));
        }

        return PresetCommands(CommandSource.Flutter, path,
            ("run", "dart run"),
            ("test", "dart test"),
            ("pub get", "dart pub get"));
    }

    private static List<ProjectCommand> ReadElixir(string root)
    {
        var path = Path.Combine(root, "mix.exs");
        if (!File.Exists(path))
        {
            return [];
        }

        var text = ReadTextSafe(path);
        var commands = new List<ProjectCommand>();
        if (text.Contains(":phoenix", StringComparison.Ordinal))
        {
            commands.Add(new ProjectCommand
            {
                Name = "phx.server",
                Command = "mix phx.server",
                Source = CommandSource.Elixir,
                SourcePath = path,
            });
        }
        else
        {
            commands.Add(new ProjectCommand
            {
                Name = "run",
                Command = "mix run",
                Source = CommandSource.Elixir,
                SourcePath = path,
            });
        }

        commands.AddRange(PresetCommands(CommandSource.Elixir, path,
            ("test", "mix test"),
            ("deps.get", "mix deps.get"),
            ("compile", "mix compile")));

        return commands;
    }

    private static readonly Regex MakefileTargetRegex = new(@"^([A-Za-z0-9][A-Za-z0-9_.-]*)\s*:(?![=])", RegexOptions.CultureInvariant);

    private static List<ProjectCommand> ReadMakefile(string root)
    {
        var path = FirstExisting(root, "Makefile", "makefile", "GNUmakefile");
        if (path is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ReadTextSafe(path).Split('\n'))
        {
            var match = MakefileTargetRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var target = match.Groups[1].Value;
            if (!IsSafeName(target) || !seen.Add(target))
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = target,
                Command = $"make {target}",
                Source = CommandSource.Makefile,
                SourcePath = path,
            });
        }

        return commands;
    }

    private static readonly Regex JustRecipeRegex = new(@"^@?([A-Za-z][A-Za-z0-9_-]*)(?:\s+[^:]*)?\s*:(?![=])", RegexOptions.CultureInvariant);

    private static List<ProjectCommand> ReadJustfile(string root)
    {
        var path = FirstExisting(root, "justfile", "Justfile", ".justfile");
        if (path is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ReadTextSafe(path).Split('\n'))
        {
            var match = JustRecipeRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var recipe = match.Groups[1].Value;
            if (recipe is "alias" or "set" or "export" or "import" or "mod")
            {
                continue;
            }

            if (!IsSafeName(recipe) || !seen.Add(recipe))
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = recipe,
                Command = $"just {recipe}",
                Source = CommandSource.Justfile,
                SourcePath = path,
            });
        }

        return commands;
    }

    private static readonly Regex YamlKeyRegex = new(@"^(\s*)([A-Za-z0-9_.:-]+)\s*:", RegexOptions.CultureInvariant);

    private static List<ProjectCommand> ReadTaskfile(string root)
    {
        var path = FirstExisting(root, "Taskfile.yml", "Taskfile.yaml", "taskfile.yml", "taskfile.yaml");
        if (path is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inTasks = false;
        int? taskIndent = null;

        foreach (var rawLine in ReadTextSafe(path).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var match = YamlKeyRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var indent = match.Groups[1].Value.Length;
            var key = match.Groups[2].Value;

            if (indent == 0)
            {
                inTasks = key == "tasks";
                taskIndent = null;
                continue;
            }

            if (!inTasks)
            {
                continue;
            }

            taskIndent ??= indent;
            if (indent == taskIndent.Value && IsSafeName(key) && seen.Add(key))
            {
                commands.Add(new ProjectCommand
                {
                    Name = key,
                    Command = $"task {key}",
                    Source = CommandSource.Taskfile,
                    SourcePath = path,
                });
            }
        }

        return commands;
    }

    private static List<ProjectCommand> ReadDockerCompose(string root)
    {
        var path = FirstExisting(root, "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml");
        if (path is null)
        {
            return [];
        }

        return PresetCommands(CommandSource.DockerCompose, path,
            ("up", "docker compose up"),
            ("up --build", "docker compose up --build"),
            ("down", "docker compose down"),
            ("logs", "docker compose logs -f"));
    }

    private static List<ProjectCommand> ReadPrisma(string root)
    {
        var path = FirstExisting(root, Path.Combine("prisma", "schema.prisma"), "schema.prisma");
        if (path is null)
        {
            return [];
        }

        return PresetCommands(CommandSource.Prisma, path,
            ("generate", "npx prisma generate"),
            ("migrate dev", "npx prisma migrate dev"),
            ("db push", "npx prisma db push"),
            ("studio", "npx prisma studio"));
    }

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

    private static List<ProjectCommand> ReadShellScripts(string root)
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

    private static List<ProjectCommand> PresetCommands(
        CommandSource source,
        string sourcePath,
        params (string Name, string Command)[] items) =>
        items.Select(item => new ProjectCommand
        {
            Name = item.Name,
            Command = item.Command,
            Source = source,
            SourcePath = sourcePath,
        }).ToList();

    private static string? FirstExisting(string root, params string[] relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            var full = Path.Combine(root, rel);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private static string ReadTextSafe(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }

    private static readonly Regex TomlSectionHeaderRegex = new(@"^\s*\[([^\]]+)\]\s*(?:#.*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex TomlKeyValueRegex = new(@"^\s*(?:""([^""]+)""|'([^']+)'|([A-Za-z0-9_.-]+))\s*=\s*(.+)$", RegexOptions.CultureInvariant);

    private static IEnumerable<(string Key, string Value)> ParseTomlSectionKeys(string text, string targetSection)
    {
        var inSection = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var headerMatch = TomlSectionHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                inSection = string.Equals(headerMatch.Groups[1].Value.Trim(), targetSection, StringComparison.Ordinal);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var kvMatch = TomlKeyValueRegex.Match(line);
            if (kvMatch.Success)
            {
                var key = kvMatch.Groups[1].Value is { Length: > 0 } k1 ? k1
                    : kvMatch.Groups[2].Value is { Length: > 0 } k2 ? k2
                    : kvMatch.Groups[3].Value;
                yield return (key, kvMatch.Groups[4].Value.Trim());
            }
        }
    }

    private static JsonDocument? ParseFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonDocument.Parse(File.ReadAllText(path), Jsonc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Unreadable or malformed: the file simply contributes no commands.
            return null;
        }
    }

    /// <summary>The command line as a string, or joined and quoted when the format allows argv arrays.</summary>
    private static string? CommandValue(JsonElement entry, params string[] names)
    {
        foreach (var name in names)
        {
            switch (entry.Prop(name))
            {
                case { ValueKind: JsonValueKind.String } s:
                    return s.GetString();
                case { ValueKind: JsonValueKind.Array } a:
                    var parts = a.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .OfType<string>()
                        .ToList();
                    return parts.Count > 0 ? JoinArguments(parts[0], parts[1..]) : null;
            }
        }

        return null;
    }

    private static string JoinArguments(string command, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return command;
        }

        return string.Join(' ', [command.Trim(), .. args.Select(Quote)]);
    }

    /// <summary>
    /// Quotes an argument the shell would otherwise split on a space. An argument that holds
    /// a quote of its own would end a double-quoted string early, so it is single-quoted
    /// instead (PowerShell doubles an embedded <c>'</c>, POSIX sh closes and reopens the string).
    /// </summary>
    private static string Quote(string arg)
    {
        var trimmed = arg.Trim();

        if (trimmed.Contains('"') && !(trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Count(c => c == '"') == 2))
        {
            return $"'{trimmed.Replace("'", "''")}'";
        }

        return trimmed.Contains(' ') && !trimmed.StartsWith('"') ? $"\"{trimmed}\"" : trimmed;
    }
}

/// <summary>Tolerant accessors over config files Codale does not control (see JsonEx's rationale).</summary>
internal static class JsonElementEx
{
    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static string? Str(this JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.Prop(name) is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s)
            {
                return s;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> StrArray(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { ValueKind: JsonValueKind.Array } arr)
        {
            return [];
        }

        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    public static IEnumerable<JsonElement> Items(this JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.Prop(name) is { ValueKind: JsonValueKind.Array } arr)
            {
                return arr.EnumerateArray();
            }
        }

        return [];
    }
}